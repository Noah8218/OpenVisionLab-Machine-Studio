using System.IO;
using System.Text.Json;
using OpenVisionLab.Integration.Contracts;

namespace OpenVisionLab.MachineStudio.ViewModel;

/// <summary>
/// Owns the file-system watcher and debounced refresh lifetime for Machine
/// integration results. Reading and projecting transaction data remain owned
/// by the result-observation workflow.
/// </summary>
internal sealed class MachineIntegrationResultFileWatcher : IDisposable
{
    private const int AutomaticRefreshDelayMilliseconds = 150;

    private readonly Func<string> _exchangeRootProvider;
    private readonly Func<bool> _canRefreshResults;
    private readonly Func<bool> _isBusy;
    private readonly Func<Task> _refreshAsync;
    private readonly Func<Func<Task>, Task> _invokeOnUiThreadAsync;
    private readonly Action<Exception> _handleAutomaticRefreshException;
    private readonly MachineIntegrationPathReadinessPolicy _pathReadiness;
    private readonly CancellationTokenSource _disposeCancellation = new();
    private readonly CancellationToken _disposeToken;
    private readonly object _lifetimeGate = new();
    private FileSystemWatcher? _watcher;
    private int _automaticRefreshScheduled;
    private bool _disposed;

    public MachineIntegrationResultFileWatcher(
        Func<string> exchangeRootProvider,
        Func<bool> canRefreshResults,
        Func<bool> isBusy,
        Func<Task> refreshAsync,
        Func<Func<Task>, Task> invokeOnUiThreadAsync,
        Action<Exception> handleAutomaticRefreshException,
        MachineIntegrationPathReadinessPolicy? pathReadiness = null)
    {
        _exchangeRootProvider = exchangeRootProvider ?? throw new ArgumentNullException(nameof(exchangeRootProvider));
        _canRefreshResults = canRefreshResults ?? throw new ArgumentNullException(nameof(canRefreshResults));
        _isBusy = isBusy ?? throw new ArgumentNullException(nameof(isBusy));
        _refreshAsync = refreshAsync ?? throw new ArgumentNullException(nameof(refreshAsync));
        _invokeOnUiThreadAsync = invokeOnUiThreadAsync ?? throw new ArgumentNullException(nameof(invokeOnUiThreadAsync));
        _handleAutomaticRefreshException = handleAutomaticRefreshException
            ?? throw new ArgumentNullException(nameof(handleAutomaticRefreshException));
        _pathReadiness = pathReadiness ?? new MachineIntegrationPathReadinessPolicy();
        _disposeToken = _disposeCancellation.Token;
    }

    public void Configure()
    {
        lock (_lifetimeGate)
        {
            ReleaseWatcher();
            var exchangeRoot = _pathReadiness.Normalize(_exchangeRootProvider());
            if (_disposed || !_pathReadiness.IsExchangeRootAvailable(exchangeRoot))
            {
                return;
            }

            try
            {
                _watcher = new FileSystemWatcher(
                    _pathReadiness.NormalizeFullPath(exchangeRoot),
                    IntegrationTransactionLayout.ResultFileName)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName
                        | NotifyFilters.LastWrite
                        | NotifyFilters.Size,
                    EnableRaisingEvents = true
                };
                _watcher.Created += OnResultFileChanged;
                _watcher.Changed += OnResultFileChanged;
                _watcher.Renamed += OnResultFileRenamed;
            }
            catch (Exception exception) when (exception is IOException
                or UnauthorizedAccessException
                or ArgumentException)
            {
                ReleaseWatcher();
            }
        }
    }

    private void ReleaseWatcher()
    {
        var watcher = _watcher;
        if (watcher is null)
        {
            return;
        }

        _watcher = null;
        watcher.Created -= OnResultFileChanged;
        watcher.Changed -= OnResultFileChanged;
        watcher.Renamed -= OnResultFileRenamed;
        watcher.Dispose();
    }

    private void OnResultFileChanged(object sender, FileSystemEventArgs args) =>
        ScheduleAutomaticRefresh();

    private void OnResultFileRenamed(object sender, RenamedEventArgs args) =>
        ScheduleAutomaticRefresh();

    private void ScheduleAutomaticRefresh()
    {
        if (IsDisposed
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
            if (IsDisposed || _isBusy())
            {
                return;
            }

            await _invokeOnUiThreadAsync(InvokeRefreshIfActiveAsync).ConfigureAwait(false);
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
            if (!IsDisposed)
            {
                _handleAutomaticRefreshException(exception);
            }
        }
        finally
        {
            Interlocked.Exchange(ref _automaticRefreshScheduled, 0);
        }
    }

    private Task InvokeRefreshIfActiveAsync()
    {
        lock (_lifetimeGate)
        {
            return _disposed ? Task.CompletedTask : _refreshAsync();
        }
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
            ReleaseWatcher();
        }

        _disposeCancellation.Cancel();
        _disposeCancellation.Dispose();
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
}
