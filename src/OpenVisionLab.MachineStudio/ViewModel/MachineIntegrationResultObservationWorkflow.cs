using System.IO;
using System.Text.Json;
using OpenVisionLab.Integration.Contracts;
using OpenVisionLab.Machine.Infrastructure.Integration;

namespace OpenVisionLab.MachineStudio.ViewModel;

/// <summary>
/// Owns file-backed integration transaction observation and result-file
/// watching. It does not publish a handoff, acknowledge a transaction, or
/// start an inspection.
/// </summary>
internal sealed class MachineIntegrationResultObservationWorkflow : IDisposable
{
    private const int AutomaticRefreshDelayMilliseconds = 150;

    private readonly Func<string> _exchangeRootProvider;
    private readonly Func<string?> _projectIdProvider;
    private readonly Func<bool> _canRefreshResults;
    private readonly Func<bool> _isBusy;
    private readonly Func<Task> _refreshAsync;
    private readonly Func<Func<Task>, Task> _invokeOnUiThreadAsync;
    private readonly Action<Exception> _handleAutomaticRefreshException;
    private readonly CancellationTokenSource _disposeCancellation = new();
    private readonly CancellationToken _disposeToken;
    private string? _lastProjectId;
    private FileSystemWatcher? _resultWatcher;
    private int _automaticRefreshScheduled;
    private int _refreshInProgress;
    private bool _disposed;

    public MachineIntegrationResultObservationWorkflow(
        Func<string> exchangeRootProvider,
        Func<string?> projectIdProvider,
        Func<bool> canRefreshResults,
        Func<bool> isBusy,
        Func<Task> refreshAsync,
        Func<Func<Task>, Task> invokeOnUiThreadAsync,
        Action<Exception> handleAutomaticRefreshException)
    {
        _exchangeRootProvider = exchangeRootProvider ?? throw new ArgumentNullException(nameof(exchangeRootProvider));
        _projectIdProvider = projectIdProvider ?? throw new ArgumentNullException(nameof(projectIdProvider));
        _canRefreshResults = canRefreshResults ?? throw new ArgumentNullException(nameof(canRefreshResults));
        _isBusy = isBusy ?? throw new ArgumentNullException(nameof(isBusy));
        _refreshAsync = refreshAsync ?? throw new ArgumentNullException(nameof(refreshAsync));
        _invokeOnUiThreadAsync = invokeOnUiThreadAsync ?? throw new ArgumentNullException(nameof(invokeOnUiThreadAsync));
        _handleAutomaticRefreshException = handleAutomaticRefreshException
            ?? throw new ArgumentNullException(nameof(handleAutomaticRefreshException));
        _disposeToken = _disposeCancellation.Token;
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
        _resultWatcher?.Dispose();
        _resultWatcher = null;
        if (_disposed || !Directory.Exists(_exchangeRootProvider().Trim()))
        {
            return;
        }

        try
        {
            _resultWatcher = new FileSystemWatcher(
                Path.GetFullPath(_exchangeRootProvider().Trim()),
                IntegrationTransactionLayout.ResultFileName)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName
                    | NotifyFilters.LastWrite
                    | NotifyFilters.Size,
                EnableRaisingEvents = true
            };
            _resultWatcher.Created += OnResultFileChanged;
            _resultWatcher.Changed += OnResultFileChanged;
            _resultWatcher.Renamed += OnResultFileRenamed;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or ArgumentException)
        {
            _resultWatcher?.Dispose();
            _resultWatcher = null;
        }
    }

    public bool RefreshContext()
    {
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
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(handoff);
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

    public async Task<int?> RefreshAsync()
    {
        if (_disposed || Interlocked.Exchange(ref _refreshInProgress, 1) != 0)
        {
            return null;
        }

        try
        {
            var root = Path.GetFullPath(_exchangeRootProvider().Trim());
            var projectId = _projectIdProvider();
            var transactions = await Task.Run(() =>
                    MachineIntegrationExchange.DiscoverTransactions(root)
                        .Where(transaction => string.Equals(
                            transaction.Handoff.Context.ProjectId,
                            projectId,
                            StringComparison.Ordinal))
                        .ToArray())
                .ConfigureAwait(true);
            if (_disposed)
            {
                return null;
            }

            TransactionCount = transactions.Length;
            LatestTransaction = transactions.FirstOrDefault();
            LatestAcknowledgementTransaction = transactions.FirstOrDefault(transaction => transaction.HasAcknowledgement);
            LatestResultTransaction = transactions.FirstOrDefault(transaction => transaction.HasResult);
            LatestAcknowledgement = null;
            LatestResult = null;
            AcknowledgementReadError = null;
            ResultReadError = null;
            LatestProjectionResult = null;
            ProjectionReadError = null;

            if (LatestAcknowledgementTransaction is { } acknowledgementTransaction)
            {
                try
                {
                    LatestAcknowledgement = await Task.Run(() =>
                            MachineIntegrationExchange.ReadAcknowledgement(
                                root,
                                acknowledgementTransaction.Handoff.TransactionId))
                        .ConfigureAwait(true);
                }
                catch (Exception exception) when (exception is IOException
                    or UnauthorizedAccessException
                    or ArgumentException
                    or InvalidOperationException
                    or IntegrationContractException)
                {
                    AcknowledgementReadError = exception.Message;
                }
            }

            if (LatestResultTransaction is { } resultTransaction)
            {
                try
                {
                    LatestResult = await Task.Run(() =>
                            MachineIntegrationExchange.ReadResult(root, resultTransaction.Handoff.TransactionId))
                        .ConfigureAwait(true);
                }
                catch (Exception exception) when (exception is IOException
                    or UnauthorizedAccessException
                    or ArgumentException
                    or InvalidOperationException
                    or IntegrationContractException)
                {
                    ResultReadError = exception.Message;
                }
            }

            if (LatestResult is not null && LatestResultTransaction is { } projectionTransaction)
            {
                try
                {
                    LatestProjectionResult = await Task.Run(() =>
                            ReadProjectionResult(
                                root,
                                projectionTransaction.Handoff.TransactionId,
                                LatestResult))
                        .ConfigureAwait(true);
                }
                catch (Exception exception) when (exception is IOException
                    or UnauthorizedAccessException
                    or ArgumentException
                    or InvalidOperationException
                    or InvalidDataException
                    or JsonException
                    or IntegrationContractException)
                {
                    ProjectionReadError = exception.Message;
                }
            }

            return TransactionCount;
        }
        finally
        {
            Volatile.Write(ref _refreshInProgress, 0);
        }
    }

    private void OnResultFileChanged(object sender, FileSystemEventArgs args) =>
        ScheduleAutomaticRefresh();

    private void OnResultFileRenamed(object sender, RenamedEventArgs args) =>
        ScheduleAutomaticRefresh();

    private void ScheduleAutomaticRefresh()
    {
        if (_disposed
            || !_canRefreshResults()
            || Interlocked.Exchange(ref _automaticRefreshScheduled, 1) != 0)
        {
            return;
        }

        _ = RefreshAutomaticallyAsync(_disposeToken);
    }

    private async Task RefreshAutomaticallyAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(AutomaticRefreshDelayMilliseconds, cancellationToken).ConfigureAwait(false);
            if (_disposed || _isBusy())
            {
                return;
            }

            await _invokeOnUiThreadAsync(_refreshAsync).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or InvalidOperationException
            or InvalidDataException
            or JsonException
            or IntegrationContractException)
        {
            if (!_disposed)
            {
                _handleAutomaticRefreshException(exception);
            }
        }
        finally
        {
            Interlocked.Exchange(ref _automaticRefreshScheduled, 0);
        }
    }

    private void ClearState()
    {
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

    private static MachineCoordinateProjectionResult? ReadProjectionResult(
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
            Path.GetFullPath(exchangeRoot),
            IntegrationTransactionLayout.TransactionsDirectoryName,
            transactionId.ToString("D"));
        var transactionPrefix = transactionDirectory.TrimEnd(Path.DirectorySeparatorChar)
                                + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(
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
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _disposeCancellation.Cancel();
        _resultWatcher?.Dispose();
        _resultWatcher = null;
        _disposeCancellation.Dispose();
    }
}
