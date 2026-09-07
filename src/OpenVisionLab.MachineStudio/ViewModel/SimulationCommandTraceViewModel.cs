using System.Globalization;
using System.IO;
using System.Windows.Input;
using OpenVisionLab;
using OpenVisionLab.Machine.Simulation.Engine;
using OpenVisionLab.Machine.Simulation.Scenarios;
using OpenVisionLab.Machine.Simulation.Snapshots;

namespace OpenVisionLab.MachineStudio.ViewModel;

/// <summary>
/// Owns command-trace capture/replay presentation state and commands. The
/// simulation engine remains the owner of trace entries and is reached only
/// through an explicit callback.
/// </summary>
public sealed class SimulationCommandTraceViewModel : ViewModelBase, IDisposable
{
    private readonly Func<bool> _canStartCapture;
    private readonly Func<FixedStepSimulationEngine?> _getEngine;
    private readonly Action<SimulationSnapshot> _applySnapshot;
    private readonly Action _clearUnifiedCommissioningEvidence;
    private readonly Action _notifyUnifiedCommissioningEvidenceChanged;
    private readonly Action<string> _setStatus;
    private readonly Action<string> _log;
    private readonly Action _openExportDialog;
    private readonly Func<Task> _openReplayDialog;
    private readonly Action<Exception> _handleCommandException;
    private readonly Func<
        FixedStepSimulationEngine,
        DeterministicSimulationCommandTracePackage,
        Task<DeterministicSimulationCommandTraceReplayResult>> _replay;
    private readonly RelayCommand _startCaptureCommand;
    private readonly RelayCommand _exportCommand;
    private readonly AsyncRelayCommand _replayCommand;
    private bool _captureStarted;
    private int? _lastReplayEntryCount;
    private string? _lastReplayHash;
    private bool _lastReplaySucceeded;
    private int _disposed;

    public SimulationCommandTraceViewModel(
        Func<bool> canStartCapture,
        Func<FixedStepSimulationEngine?> getEngine,
        Action<SimulationSnapshot> applySnapshot,
        Action clearUnifiedCommissioningEvidence,
        Action notifyUnifiedCommissioningEvidenceChanged,
        Action<string> setStatus,
        Action<string> log,
        Action openExportDialog,
        Func<Task> openReplayDialog,
        Action<Exception> handleCommandException)
        : this(
            canStartCapture,
            getEngine,
            applySnapshot,
            clearUnifiedCommissioningEvidence,
            notifyUnifiedCommissioningEvidenceChanged,
            setStatus,
            log,
            openExportDialog,
            openReplayDialog,
            handleCommandException,
            ReplayWithDefaultRunner)
    {
    }

    internal SimulationCommandTraceViewModel(
        Func<bool> canStartCapture,
        Func<FixedStepSimulationEngine?> getEngine,
        Action<SimulationSnapshot> applySnapshot,
        Action clearUnifiedCommissioningEvidence,
        Action notifyUnifiedCommissioningEvidenceChanged,
        Action<string> setStatus,
        Action<string> log,
        Action openExportDialog,
        Func<Task> openReplayDialog,
        Action<Exception> handleCommandException,
        Func<
            FixedStepSimulationEngine,
            DeterministicSimulationCommandTracePackage,
            Task<DeterministicSimulationCommandTraceReplayResult>> replay)
    {
        _canStartCapture = canStartCapture ?? throw new ArgumentNullException(nameof(canStartCapture));
        _getEngine = getEngine ?? throw new ArgumentNullException(nameof(getEngine));
        _applySnapshot = applySnapshot ?? throw new ArgumentNullException(nameof(applySnapshot));
        _clearUnifiedCommissioningEvidence = clearUnifiedCommissioningEvidence
            ?? throw new ArgumentNullException(nameof(clearUnifiedCommissioningEvidence));
        _notifyUnifiedCommissioningEvidenceChanged = notifyUnifiedCommissioningEvidenceChanged
            ?? throw new ArgumentNullException(nameof(notifyUnifiedCommissioningEvidenceChanged));
        _setStatus = setStatus ?? throw new ArgumentNullException(nameof(setStatus));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _openExportDialog = openExportDialog ?? throw new ArgumentNullException(nameof(openExportDialog));
        _openReplayDialog = openReplayDialog ?? throw new ArgumentNullException(nameof(openReplayDialog));
        _handleCommandException = handleCommandException
            ?? throw new ArgumentNullException(nameof(handleCommandException));
        _replay = replay ?? throw new ArgumentNullException(nameof(replay));
        _startCaptureCommand = new RelayCommand(
            _ => StartCapture(),
            _ => CanStartCapture,
            useCommandManagerRequery: false);
        _exportCommand = new RelayCommand(
            Export,
            _ => CanExportTrace,
            useCommandManagerRequery: false);
        _replayCommand = new AsyncRelayCommand(
            ReplayAsync,
            _ => CanReplayTrace,
            _handleCommandException,
            useCommandManagerRequery: false);
    }

    public bool IsCaptureStarted => !IsDisposed && _captureStarted;
    public bool CanStartCapture => !IsDisposed && _canStartCapture() && _getEngine() is not null;
    public bool CanExportTrace => !IsDisposed && CanStartCapture
        && _captureStarted
        && _getEngine()?.CommandTrace.Length > 0;
    public bool CanReplayTrace => !IsDisposed && CanStartCapture;
    public int EntryCount => IsDisposed ? 0 : _getEngine()?.CommandTrace.Length ?? 0;
    public bool LastReplaySucceeded => !IsDisposed && _lastReplaySucceeded;
    public string StatusText
    {
        get
        {
            if (IsDisposed)
            {
                return OpenVisionLanguageService.T("Simulation.CommandTraceUnavailable");
            }

            var traceEngine = _getEngine();
            if (traceEngine is null)
            {
                return OpenVisionLanguageService.T("Simulation.CommandTraceUnavailable");
            }

            var package = traceEngine.CreateCommandTracePackage();
            if (!_captureStarted)
            {
                if (_lastReplayEntryCount is { } replayEntryCount
                    && _lastReplayHash is { } replayHash)
                {
                    return string.Format(
                        CultureInfo.CurrentCulture,
                        OpenVisionLanguageService.T("Simulation.CommandTraceReplayStatus"),
                        replayEntryCount,
                        ShortHash(replayHash));
                }

                return traceEngine.CommandTrace.Length == 0
                    ? OpenVisionLanguageService.T("Simulation.CommandTraceIdle")
                    : OpenVisionLanguageService.T("Simulation.CommandTraceSetupOnly");
            }

            if (traceEngine.CommandTrace.Length == 0)
            {
                return OpenVisionLanguageService.T("Simulation.CommandTraceCaptureStarted");
            }

            return string.Format(
                CultureInfo.CurrentCulture,
                OpenVisionLanguageService.T(package.CanReplay
                    ? "Simulation.CommandTraceReady"
                    : "Simulation.CommandTraceReadyNonReplayable"),
                package.Entries.Length,
                ShortHash(package.TraceHash));
        }
    }

    public ICommand StartCaptureCommand => _startCaptureCommand;
    public ICommand ExportCommand => _exportCommand;
    public ICommand ReplayCommand => _replayCommand;

    internal bool TryExport(string path)
    {
        if (IsDisposed
            || !CanExportTrace
            || string.IsNullOrWhiteSpace(path)
            || _getEngine() is not { } traceEngine)
        {
            return false;
        }

        try
        {
            var package = traceEngine.CreateCommandTracePackage();
            DeterministicSimulationCommandTracePackage.SaveToJson(package, path);
            _setStatus(string.Format(
                CultureInfo.CurrentCulture,
                OpenVisionLanguageService.T("Simulation.CommandTraceExported"),
                package.Entries.Length,
                ShortHash(package.TraceHash)));
            _log($"Command trace exported · {package.Entries.Length} entries · {ShortHash(package.TraceHash)}");
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _setStatus(OpenVisionLanguageService.T("Simulation.CommandTraceExportFailed"));
            _log($"Command trace export failed · {exception.Message}");
            return false;
        }
    }

    internal async Task<bool> TryReplayAsync(string path)
    {
        if (IsDisposed)
        {
            return false;
        }

        _lastReplaySucceeded = false;
        _lastReplayEntryCount = null;
        _lastReplayHash = null;
        if (!CanReplayTrace
            || string.IsNullOrWhiteSpace(path)
            || _getEngine() is not { } traceEngine)
        {
            return false;
        }

        var package = DeterministicSimulationCommandTracePackage.LoadFromJson(path);
        if (package is null)
        {
            _setStatus(OpenVisionLanguageService.T("Simulation.CommandTraceInvalidFile"));
            _log("Command trace replay rejected · file could not be loaded");
            return false;
        }

        try
        {
            var result = await _replay(traceEngine, package)
                .ConfigureAwait(true);
            if (IsDisposed)
            {
                return false;
            }

            if (!result.IsSuccess)
            {
                var detail = result.FailureReason
                    ?? result.FirstMismatch?.Detail
                    ?? "The command trace could not be replayed.";
                _setStatus(string.Format(
                    CultureInfo.CurrentCulture,
                    OpenVisionLanguageService.T("Simulation.CommandTraceReplayFailed"),
                    detail));
                _log($"Command trace replay rejected · {detail}");
                return false;
            }

            _applySnapshot(traceEngine.CurrentSnapshot);
            _captureStarted = false;
            _lastReplayEntryCount = result.AppliedEntries;
            _lastReplayHash = package.TraceHash;
            _lastReplaySucceeded = true;
            _setStatus(string.Format(
                CultureInfo.CurrentCulture,
                OpenVisionLanguageService.T("Simulation.CommandTraceReplayed"),
                result.AppliedEntries,
                ShortHash(package.TraceHash)));
            _log($"Command trace replayed · {result.AppliedEntries} entries · {ShortHash(package.TraceHash)}");
            RaiseTraceChanged();
            _notifyUnifiedCommissioningEvidenceChanged();
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            if (IsDisposed)
            {
                return false;
            }

            _setStatus(string.Format(
                CultureInfo.CurrentCulture,
                OpenVisionLanguageService.T("Simulation.CommandTraceReplayFailed"),
                exception.Message));
            _log($"Command trace replay failed · {exception.Message}");
            return false;
        }
    }

    internal void Reset()
    {
        if (IsDisposed)
        {
            return;
        }

        _captureStarted = false;
        _lastReplayEntryCount = null;
        _lastReplayHash = null;
        _lastReplaySucceeded = false;
        RaiseTraceChanged();
    }

    internal void NotifyRuntimeChanged()
    {
        if (!IsDisposed)
        {
            RaiseTraceChanged();
        }
    }

    internal void InvalidateCommands()
    {
        if (IsDisposed)
        {
            return;
        }

        _startCaptureCommand.RaiseCanExecuteChanged();
        _exportCommand.RaiseCanExecuteChanged();
        _replayCommand.RaiseCanExecuteChanged();
    }

    private void StartCapture()
    {
        if (IsDisposed || !CanStartCapture || _getEngine() is not { } traceEngine)
        {
            return;
        }

        traceEngine.ClearCommandTrace();
        _captureStarted = true;
        _clearUnifiedCommissioningEvidence();
        _lastReplayEntryCount = null;
        _lastReplayHash = null;
        _lastReplaySucceeded = false;
        _setStatus(OpenVisionLanguageService.T("Simulation.CommandTraceCaptureStarted"));
        _log("Command trace capture boundary started");
        RaiseTraceChanged();
        _notifyUnifiedCommissioningEvidenceChanged();
    }

    private void Export(object? parameter)
    {
        if (IsDisposed)
        {
            return;
        }

        if (parameter is string path && !string.IsNullOrWhiteSpace(path))
        {
            TryExport(path);
        }
        else
        {
            _openExportDialog();
        }
    }

    private async Task ReplayAsync(object? parameter)
    {
        if (IsDisposed)
        {
            return;
        }

        if (parameter is string path && !string.IsNullOrWhiteSpace(path))
        {
            await TryReplayAsync(path);
        }
        else
        {
            await _openReplayDialog();
        }
    }

    private void RaiseTraceChanged()
    {
        if (IsDisposed)
        {
            return;
        }

        OnPropertyChanged(nameof(IsCaptureStarted));
        OnPropertyChanged(nameof(CanStartCapture));
        OnPropertyChanged(nameof(CanExportTrace));
        OnPropertyChanged(nameof(CanReplayTrace));
        OnPropertyChanged(nameof(EntryCount));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(LastReplaySucceeded));
        InvalidateCommands();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _captureStarted = false;
        _lastReplayEntryCount = null;
        _lastReplayHash = null;
        _lastReplaySucceeded = false;
    }

    private static Task<DeterministicSimulationCommandTraceReplayResult> ReplayWithDefaultRunner(
        FixedStepSimulationEngine engine,
        DeterministicSimulationCommandTracePackage package) =>
        new DeterministicSimulationCommandTraceReplayRunner().ReplayAsync(engine, package);

    private bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    private static string ShortHash(string hash) =>
        hash.Length <= 12 ? hash : hash[..12];
}
