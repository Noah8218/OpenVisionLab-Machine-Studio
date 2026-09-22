using System.IO;
using System.Text.Json;
using OpenVisionLab.Integration.Contracts;

namespace OpenVisionLab.Machine.Infrastructure.Integration;

/// <summary>
/// Owns file-backed integration transaction observation. It does not publish
/// a handoff, acknowledge a transaction, or start an inspection; the concrete
/// result-file watcher owns its event and cancellation lifetime.
/// </summary>
public sealed class MachineIntegrationResultObservationWorkflow : IDisposable
{
    private readonly Func<string> _exchangeRootProvider;
    private readonly Func<string?> _projectIdProvider;
    private readonly MachineIntegrationPathReadinessPolicy _pathReadiness;
    private readonly MachineIntegrationResultFileWatcher _resultFileWatcher;
    private readonly object _lifetimeGate = new();
    private string? _lastProjectId;
    private long _observationGeneration;
    private int _refreshInProgress;
    private bool _disposed;

    public MachineIntegrationResultObservationWorkflow(
        Func<string> exchangeRootProvider,
        Func<string?> projectIdProvider,
        Func<bool> canRefreshResults,
        Func<bool> isBusy,
        Func<Task> refreshAsync,
        Func<Func<Task>, Task> invokeOnUiThreadAsync,
        Action<Exception> handleAutomaticRefreshException,
        MachineIntegrationPathReadinessPolicy? pathReadiness = null)
    {
        _exchangeRootProvider = exchangeRootProvider ?? throw new ArgumentNullException(nameof(exchangeRootProvider));
        _projectIdProvider = projectIdProvider ?? throw new ArgumentNullException(nameof(projectIdProvider));
        _pathReadiness = pathReadiness ?? new MachineIntegrationPathReadinessPolicy();
        ArgumentNullException.ThrowIfNull(canRefreshResults);
        ArgumentNullException.ThrowIfNull(isBusy);
        ArgumentNullException.ThrowIfNull(refreshAsync);
        _resultFileWatcher = new(
            _exchangeRootProvider,
            canRefreshResults,
            isBusy,
            refreshAsync,
            invokeOnUiThreadAsync,
            handleAutomaticRefreshException,
            _pathReadiness);
        _lastProjectId = _projectIdProvider();
    }

    public MachineIntegrationTransactionSummary? LatestTransaction { get; private set; }

    public MachineIntegrationTransactionSummary? LatestAcknowledgementTransaction { get; private set; }

    public MachineIntegrationTransactionSummary? LatestResultTransaction { get; private set; }

    /// <summary>
    /// Current-project transactions ordered by newest handoff first.
    /// The collection is a read-only snapshot; refresh and project changes replace it.
    /// </summary>
    public IReadOnlyList<MachineIntegrationTransactionSummary> CurrentTransactions { get; private set; } = [];

    /// <summary>
    /// Read-only diagnostics for transaction directories under the configured exchange root.
    /// Staging and quarantine entries are correlated by TransactionId when available;
    /// this owner does not infer a project identity that the storage contract does not contain.
    /// </summary>
    public IReadOnlyList<MachineIntegrationTransactionDiagnostic> TransactionDiagnostics { get; private set; } = [];

    public IntegrationAcknowledgementV2? LatestAcknowledgement { get; private set; }

    public IntegrationResultV2? LatestResult { get; private set; }

    public MachineIntegrationValidatedResult? LatestValidatedResult { get; private set; }

    /// <summary>
    /// Validated results keyed by transaction so modality-specific projections
    /// do not lose a 2D result when a newer 3D transaction is observed (or the
    /// other way around).
    /// </summary>
    public IReadOnlyDictionary<Guid, MachineIntegrationValidatedResult> ValidatedResultsByTransaction { get; private set; } =
        new Dictionary<Guid, MachineIntegrationValidatedResult>();

    public string? AcknowledgementReadError { get; private set; }

    public string? ResultReadError { get; private set; }

    public MachineCoordinateProjectionResult? LatestProjectionResult { get; private set; }

    public string? ProjectionReadError { get; private set; }

    public string? DiagnosticReadError { get; private set; }

    public int TransactionCount { get; private set; }

    public void ConfigureWatcher()
    {
        if (!IsDisposed)
        {
            _resultFileWatcher.Configure();
        }
    }

    public bool RefreshContext()
    {
        if (IsDisposed)
        {
            return false;
        }

        var projectId = _projectIdProvider();
        lock (_lifetimeGate)
        {
            if (_disposed || string.Equals(_lastProjectId, projectId, StringComparison.Ordinal))
            {
                return false;
            }

            _lastProjectId = projectId;
            _observationGeneration++;
            ClearStateUnsafe();
        }

        return true;
    }

    public void Reset()
    {
        lock (_lifetimeGate)
        {
            if (_disposed)
            {
                return;
            }

            _observationGeneration++;
            ClearStateUnsafe();
        }
    }

    public void RecordPublishedHandoff(IntegrationHandoffV2 handoff)
    {
        ArgumentNullException.ThrowIfNull(handoff);
        lock (_lifetimeGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _observationGeneration++;
            LatestTransaction = new MachineIntegrationTransactionSummary(handoff, false, false);
            var updatedTransactions = new List<MachineIntegrationTransactionSummary>(CurrentTransactions.Count + 1)
            {
                LatestTransaction
            };
            updatedTransactions.AddRange(CurrentTransactions);
            CurrentTransactions = updatedTransactions;
            TransactionDiagnostics = [];
            LatestAcknowledgementTransaction = null;
            LatestResultTransaction = null;
            LatestAcknowledgement = null;
            LatestResult = null;
            LatestValidatedResult = null;
            ValidatedResultsByTransaction = new Dictionary<Guid, MachineIntegrationValidatedResult>();
            AcknowledgementReadError = null;
            ResultReadError = null;
            LatestProjectionResult = null;
            ProjectionReadError = null;
            DiagnosticReadError = null;
            TransactionCount = CurrentTransactions.Count;
        }
    }

    public async Task<int?> RefreshAsync()
    {
        if (IsDisposed || Interlocked.Exchange(ref _refreshInProgress, 1) != 0)
        {
            return null;
        }

        try
        {
            RefreshContext();
            string? projectId;
            long observationGeneration;
            lock (_lifetimeGate)
            {
                if (_disposed)
                {
                    return null;
                }

                projectId = _lastProjectId;
                observationGeneration = _observationGeneration;
            }

            var root = _pathReadiness.NormalizeFullPath(_exchangeRootProvider());
            var transactions = await Task.Run(() =>
                    MachineIntegrationExchange.DiscoverTransactions(root)
                        .Where(transaction => string.Equals(
                            transaction.Handoff.Context.ProjectId,
                            projectId,
                            StringComparison.Ordinal))
                        .ToArray())
                .ConfigureAwait(true);
            MachineIntegrationTransactionSummary? acknowledgementTransaction = null;
            MachineIntegrationTransactionSummary? resultTransaction = null;
            if (!TryPublishForGeneration(observationGeneration, () =>
            {
                TransactionCount = transactions.Length;
                CurrentTransactions = transactions;
                LatestTransaction = transactions.FirstOrDefault();
                LatestAcknowledgementTransaction = transactions.FirstOrDefault(transaction => transaction.HasAcknowledgement);
                LatestResultTransaction = transactions.FirstOrDefault(transaction => transaction.HasResult);
                acknowledgementTransaction = LatestAcknowledgementTransaction;
                resultTransaction = LatestResultTransaction;
                LatestAcknowledgement = null;
                LatestResult = null;
                LatestValidatedResult = null;
                ValidatedResultsByTransaction = new Dictionary<Guid, MachineIntegrationValidatedResult>();
                AcknowledgementReadError = null;
                ResultReadError = null;
                LatestProjectionResult = null;
                ProjectionReadError = null;
            }))
            {
                return null;
            }

            try
            {
                var diagnostics = await Task.Run(() => MachineIntegrationExchange.DiagnoseTransactions(root))
                    .ConfigureAwait(true);
                if (!TryPublishForGeneration(observationGeneration, () =>
                    {
                        TransactionDiagnostics = diagnostics;
                        DiagnosticReadError = null;
                    }))
                {
                    return null;
                }
            }
            catch (Exception exception) when (exception is IOException
                or UnauthorizedAccessException
                or ArgumentException
                or InvalidOperationException
                or NotSupportedException
                or IntegrationContractException)
            {
                if (!TryPublishForGeneration(observationGeneration, () =>
                    {
                        TransactionDiagnostics = [];
                        DiagnosticReadError = exception.Message;
                    }))
                {
                    return null;
                }
            }

            MachineIntegrationValidatedResult? validatedResult = null;
            var validatedResults = new Dictionary<Guid, MachineIntegrationValidatedResult>();
            if (resultTransaction is { })
            {
                try
                {
                    validatedResult = await Task.Run(() =>
                            MachineIntegrationExchange.ReadValidatedResult(
                                root,
                                resultTransaction.Handoff.TransactionId))
                        .ConfigureAwait(true);
                    if (!TryPublishForGeneration(observationGeneration, () =>
                        {
                            LatestValidatedResult = validatedResult;
                            LatestResult = validatedResult.Result;
                            validatedResults[validatedResult.Handoff.TransactionId] = validatedResult;
                            if (acknowledgementTransaction?.Handoff.TransactionId
                                == validatedResult.Handoff.TransactionId)
                            {
                                LatestAcknowledgement = validatedResult.Acknowledgement;
                            }
                        }))
                    {
                        return null;
                    }
                }
                catch (Exception exception) when (exception is IOException
                    or UnauthorizedAccessException
                    or ArgumentException
                    or InvalidOperationException
                    or InvalidDataException
                    or JsonException
                    or IntegrationContractException)
                {
                    if (!TryPublishForGeneration(observationGeneration, () => ResultReadError = exception.Message))
                    {
                        return null;
                    }
                }
            }

            foreach (var transaction in transactions.Where(transaction =>
                transaction.HasResult
                && resultTransaction?.Handoff.TransactionId != transaction.Handoff.TransactionId))
            {
                try
                {
                    var result = await Task.Run(() =>
                            MachineIntegrationExchange.ReadValidatedResult(
                                root,
                                transaction.Handoff.TransactionId))
                        .ConfigureAwait(true);
                    validatedResults[result.Handoff.TransactionId] = result;
                }
                catch (Exception exception) when (exception is IOException
                    or UnauthorizedAccessException
                    or ArgumentException
                    or InvalidOperationException
                    or InvalidDataException
                    or JsonException
                    or IntegrationContractException)
                {
                    // The latest result's failure remains visible through
                    // ResultReadError above. Older malformed transactions do
                    // not erase valid modality-specific results.
                }
            }

            if (!TryPublishForGeneration(observationGeneration, () =>
                ValidatedResultsByTransaction = new Dictionary<Guid, MachineIntegrationValidatedResult>(validatedResults)))
            {
                return null;
            }

            if (acknowledgementTransaction is { }
                && validatedResult?.Handoff.TransactionId
                    != acknowledgementTransaction.Handoff.TransactionId)
            {
                try
                {
                    var acknowledgement = await Task.Run(() =>
                            MachineIntegrationExchange.ReadAcknowledgement(
                                root,
                                acknowledgementTransaction.Handoff.TransactionId))
                        .ConfigureAwait(true);
                    if (!TryPublishForGeneration(observationGeneration, () => LatestAcknowledgement = acknowledgement))
                    {
                        return null;
                    }
                }
                catch (Exception exception) when (exception is IOException
                    or UnauthorizedAccessException
                    or ArgumentException
                    or InvalidOperationException
                    or InvalidDataException
                    or JsonException
                    or IntegrationContractException)
                {
                    if (!TryPublishForGeneration(observationGeneration, () => AcknowledgementReadError = exception.Message))
                    {
                        return null;
                    }
                }
            }

            IntegrationResultV2? latestResult;
            lock (_lifetimeGate)
            {
                if (_disposed || _observationGeneration != observationGeneration)
                {
                    return null;
                }

                latestResult = LatestResult;
            }

            if (latestResult is not null && resultTransaction is { } projectionTransaction)
            {
                try
                {
                    var projection = await Task.Run(() =>
                            ReadProjectionResult(
                                _pathReadiness,
                                root,
                                projectionTransaction.Handoff.TransactionId,
                                latestResult))
                        .ConfigureAwait(true);
                    if (!TryPublishForGeneration(observationGeneration, () => LatestProjectionResult = projection))
                    {
                        return null;
                    }
                }
                catch (Exception exception) when (exception is IOException
                    or UnauthorizedAccessException
                    or ArgumentException
                    or InvalidOperationException
                    or InvalidDataException
                    or JsonException
                    or IntegrationContractException)
                {
                    if (!TryPublishForGeneration(observationGeneration, () => ProjectionReadError = exception.Message))
                    {
                        return null;
                    }
                }
            }

            lock (_lifetimeGate)
            {
                return _disposed || _observationGeneration != observationGeneration
                    ? null
                    : TransactionCount;
            }
        }
        finally
        {
            Volatile.Write(ref _refreshInProgress, 0);
        }
    }

    private void ClearStateUnsafe()
    {
        LatestTransaction = null;
        CurrentTransactions = [];
        TransactionDiagnostics = [];
        LatestAcknowledgementTransaction = null;
        LatestResultTransaction = null;
        LatestAcknowledgement = null;
        LatestResult = null;
        LatestValidatedResult = null;
        ValidatedResultsByTransaction = new Dictionary<Guid, MachineIntegrationValidatedResult>();
        AcknowledgementReadError = null;
        ResultReadError = null;
        LatestProjectionResult = null;
        ProjectionReadError = null;
        DiagnosticReadError = null;
        TransactionCount = 0;
    }

    private bool IsDisposed
    {
        get
        {
            lock (_lifetimeGate)
            {
                return _disposed;
            }
        }
    }

    private bool TryPublishForGeneration(long observationGeneration, Action action)
    {
        lock (_lifetimeGate)
        {
            if (_disposed || _observationGeneration != observationGeneration)
            {
                return false;
            }

            action();
            return true;
        }
    }

    private static MachineCoordinateProjectionResult? ReadProjectionResult(
        MachineIntegrationPathReadinessPolicy pathReadiness,
        string exchangeRoot,
        Guid transactionId,
        IntegrationResultV2 result)
    {
        var evidence = result.Evidence.FirstOrDefault(item =>
            string.Equals(
                item.Role,
                MachineCoordinateProjectionContract.ResultEvidenceRole,
                StringComparison.Ordinal)
            && string.Equals(
                item.ArtifactId,
                MachineCoordinateProjectionContract.ResultEvidenceArtifactId,
                StringComparison.Ordinal));
        if (evidence is null)
        {
            return null;
        }

        var transactionDirectory = Path.Combine(
            pathReadiness.NormalizeFullPath(exchangeRoot),
            IntegrationTransactionLayout.TransactionsDirectoryName,
            transactionId.ToString("D"));
        var transactionPrefix = transactionDirectory.TrimEnd(Path.DirectorySeparatorChar)
                                + Path.DirectorySeparatorChar;
        var path = pathReadiness.NormalizeFullPath(Path.Combine(
            transactionDirectory,
            evidence.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(transactionPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "Coordinate projection evidence escapes its transaction directory.");
        }

        var projection = MachineCoordinateProjectionContract.ReadResult(path);
        if (!string.Equals(
                projection.ThreeDTransactionId,
                transactionId.ToString("D"),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "Coordinate projection evidence does not belong to the current ThreeD transaction.");
        }

        return projection;
    }

    public void Dispose()
    {
        lock (_lifetimeGate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _resultFileWatcher.Dispose();
    }
}
