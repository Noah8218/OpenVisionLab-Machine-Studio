using System.IO;
using System.Text.Json;
using OpenVisionLab.Integration.Contracts;
using OpenVisionLab.Machine.Infrastructure.Integration;

namespace OpenVisionLab.MachineStudio.ViewModel;

/// <summary>
/// Owns file-backed integration transaction observation. It does not publish
/// a handoff, acknowledge a transaction, or start an inspection; the concrete
/// result-file watcher owns its event and cancellation lifetime.
/// </summary>
internal sealed class MachineIntegrationResultObservationWorkflow : IDisposable
{
    private readonly Func<string> _exchangeRootProvider;
    private readonly Func<string?> _projectIdProvider;
    private readonly MachineIntegrationPathReadinessPolicy _pathReadiness;
    private readonly MachineIntegrationResultFileWatcher _resultFileWatcher;
    private readonly object _lifetimeGate = new();
    private string? _lastProjectId;
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

    public IntegrationAcknowledgementV2? LatestAcknowledgement { get; private set; }

    public IntegrationResultV2? LatestResult { get; private set; }

    public string? AcknowledgementReadError { get; private set; }

    public string? ResultReadError { get; private set; }

    public MachineCoordinateProjectionResult? LatestProjectionResult { get; private set; }

    public string? ProjectionReadError { get; private set; }

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
        if (string.Equals(_lastProjectId, projectId, StringComparison.Ordinal))
        {
            return false;
        }

        _lastProjectId = projectId;
        ClearState();
        return true;
    }

    public void Reset()
    {
        ClearState();
    }

    public void RecordPublishedHandoff(IntegrationHandoffV2 handoff)
    {
        ArgumentNullException.ThrowIfNull(handoff);
        lock (_lifetimeGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            LatestTransaction = new MachineIntegrationTransactionSummary(handoff, false, false);
            LatestAcknowledgementTransaction = null;
            LatestResultTransaction = null;
            LatestAcknowledgement = null;
            LatestResult = null;
            AcknowledgementReadError = null;
            ResultReadError = null;
            LatestProjectionResult = null;
            ProjectionReadError = null;
            TransactionCount = Math.Max(1, TransactionCount + 1);
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
            var root = _pathReadiness.NormalizeFullPath(_exchangeRootProvider());
            var projectId = _projectIdProvider();
            var transactions = await Task.Run(() =>
                    MachineIntegrationExchange.DiscoverTransactions(root)
                        .Where(transaction => string.Equals(
                            transaction.Handoff.Context.ProjectId,
                            projectId,
                            StringComparison.Ordinal))
                        .ToArray())
                .ConfigureAwait(true);
            MachineIntegrationTransactionSummary? acknowledgementTransaction;
            MachineIntegrationTransactionSummary? resultTransaction;
            lock (_lifetimeGate)
            {
                if (_disposed)
                {
                    return null;
                }

                TransactionCount = transactions.Length;
                LatestTransaction = transactions.FirstOrDefault();
                LatestAcknowledgementTransaction = transactions.FirstOrDefault(transaction => transaction.HasAcknowledgement);
                LatestResultTransaction = transactions.FirstOrDefault(transaction => transaction.HasResult);
                acknowledgementTransaction = LatestAcknowledgementTransaction;
                resultTransaction = LatestResultTransaction;
                LatestAcknowledgement = null;
                LatestResult = null;
                AcknowledgementReadError = null;
                ResultReadError = null;
                LatestProjectionResult = null;
                ProjectionReadError = null;
            }

            if (acknowledgementTransaction is { })
            {
                try
                {
                    var acknowledgement = await Task.Run(() =>
                            MachineIntegrationExchange.ReadAcknowledgement(
                                root,
                                acknowledgementTransaction.Handoff.TransactionId))
                        .ConfigureAwait(true);
                    if (!TryPublish(() => LatestAcknowledgement = acknowledgement))
                    {
                        return null;
                    }
                }
                catch (Exception exception) when (exception is IOException
                    or UnauthorizedAccessException
                    or ArgumentException
                    or InvalidOperationException
                    or IntegrationContractException)
                {
                    if (!TryPublish(() => AcknowledgementReadError = exception.Message))
                    {
                        return null;
                    }
                }
            }

            if (resultTransaction is { })
            {
                try
                {
                    var result = await Task.Run(() =>
                            MachineIntegrationExchange.ReadResult(root, resultTransaction.Handoff.TransactionId))
                        .ConfigureAwait(true);
                    if (!TryPublish(() => LatestResult = result))
                    {
                        return null;
                    }
                }
                catch (Exception exception) when (exception is IOException
                    or UnauthorizedAccessException
                    or ArgumentException
                    or InvalidOperationException
                    or IntegrationContractException)
                {
                    if (!TryPublish(() => ResultReadError = exception.Message))
                    {
                        return null;
                    }
                }
            }

            IntegrationResultV2? latestResult;
            lock (_lifetimeGate)
            {
                if (_disposed)
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
                    if (!TryPublish(() => LatestProjectionResult = projection))
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
                    if (!TryPublish(() => ProjectionReadError = exception.Message))
                    {
                        return null;
                    }
                }
            }

            lock (_lifetimeGate)
            {
                return _disposed ? null : TransactionCount;
            }
        }
        finally
        {
            Volatile.Write(ref _refreshInProgress, 0);
        }
    }

    private void ClearState()
    {
        lock (_lifetimeGate)
        {
            if (_disposed)
            {
                return;
            }

            LatestTransaction = null;
            LatestAcknowledgementTransaction = null;
            LatestResultTransaction = null;
            LatestAcknowledgement = null;
            LatestResult = null;
            AcknowledgementReadError = null;
            ResultReadError = null;
            LatestProjectionResult = null;
            ProjectionReadError = null;
            TransactionCount = 0;
        }
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

    private bool TryPublish(Action action)
    {
        lock (_lifetimeGate)
        {
            if (_disposed)
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
