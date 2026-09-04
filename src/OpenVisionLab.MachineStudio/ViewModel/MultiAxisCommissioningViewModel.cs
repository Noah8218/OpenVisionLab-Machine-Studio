using System.Globalization;
using System.IO;
using System.Windows.Input;
using OpenVisionLab;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Simulation.Commissioning;
using OpenVisionLab.Machine.Simulation.Compilation;
using OpenVisionLab.Machine.Simulation.Engine;

namespace OpenVisionLab.MachineStudio.ViewModel;

public sealed class MultiAxisCommissioningViewModel : ViewModelBase, IDisposable
{
    private readonly MultiAxisCommissioningRecipeEditorViewModel _recipe;
    private readonly MultiAxisCommissioningArtifactStore _artifactStore = new();
    private readonly Func<bool> _canValidateFromParent;
    private readonly Func<bool> _isOtherValidationRunning;
    private readonly Func<MachineProjectDocument> _getProject;
    private readonly Func<string?> _getProjectPath;
    private readonly Func<string> _serializeProject;
    private readonly Func<SimulationRuntimeConfiguration> _buildRuntime;
    private readonly TimeSpan _fixedStep;
    private readonly Func<Action, Task> _dispatchToUi;
    private readonly Action<string> _setStatus;
    private readonly Action<string> _log;
    private readonly Action<DeterministicCommissioningMismatch> _navigateToMismatch;
    private readonly Action<bool> _notifyParentPresentationChanged;
    private readonly Action<Exception> _onCommandException;
    private CancellationTokenSource? _validationCancellation;
    private Task? _validationTask;
    private DeterministicCommissioningResultHistoryEntry? _selectedHistoryEntry;
    private DeterministicCommissioningBaselineComparison? _baselineComparison;
    private bool _isValidationRunning;
    private int _completedRuns;
    private bool _disposed;

    public MultiAxisCommissioningViewModel(
        MultiAxisCommissioningRecipeEditorViewModel recipe,
        Func<bool> canValidateFromParent,
        Func<bool> isOtherValidationRunning,
        Func<MachineProjectDocument> getProject,
        Func<string?> getProjectPath,
        Func<string> serializeProject,
        Func<SimulationRuntimeConfiguration> buildRuntime,
        TimeSpan fixedStep,
        Func<Action, Task> dispatchToUi,
        Action<string> setStatus,
        Action<string> log,
        Action<DeterministicCommissioningMismatch> navigateToMismatch,
        Action<bool> notifyParentPresentationChanged,
        Action<Exception> onCommandException)
    {
        _recipe = recipe ?? throw new ArgumentNullException(nameof(recipe));
        _canValidateFromParent = canValidateFromParent ?? throw new ArgumentNullException(nameof(canValidateFromParent));
        _isOtherValidationRunning = isOtherValidationRunning ?? throw new ArgumentNullException(nameof(isOtherValidationRunning));
        _getProject = getProject ?? throw new ArgumentNullException(nameof(getProject));
        _getProjectPath = getProjectPath ?? throw new ArgumentNullException(nameof(getProjectPath));
        _serializeProject = serializeProject ?? throw new ArgumentNullException(nameof(serializeProject));
        _buildRuntime = buildRuntime ?? throw new ArgumentNullException(nameof(buildRuntime));
        _fixedStep = fixedStep;
        _dispatchToUi = dispatchToUi ?? throw new ArgumentNullException(nameof(dispatchToUi));
        _setStatus = setStatus ?? throw new ArgumentNullException(nameof(setStatus));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _navigateToMismatch = navigateToMismatch ?? throw new ArgumentNullException(nameof(navigateToMismatch));
        _notifyParentPresentationChanged = notifyParentPresentationChanged
            ?? throw new ArgumentNullException(nameof(notifyParentPresentationChanged));
        _onCommandException = onCommandException ?? throw new ArgumentNullException(nameof(onCommandException));
        _artifactStore.Reset(_getProject().Id);

        ValidateCommand = new AsyncRelayCommand(
            _ => RunValidationTask(),
            _ => CanValidate,
            _onCommandException,
            useCommandManagerRequery: false);
        AcceptBaselineCommand = new RelayCommand(
            _ => AcceptBaseline(),
            _ => CanAcceptBaseline,
            useCommandManagerRequery: false);
        ClearBaselineCommand = new RelayCommand(
            _ => ClearBaseline(),
            _ => CanClearBaseline,
            useCommandManagerRequery: false);
        NavigateToMismatchCommand = new RelayCommand(
            _ => NavigateToMismatch(),
            _ => CanNavigateToMismatch,
            useCommandManagerRequery: false);
    }

    public ICommand ValidateCommand { get; }
    public ICommand AcceptBaselineCommand { get; }
    public ICommand ClearBaselineCommand { get; }
    public ICommand NavigateToMismatchCommand { get; }

    public bool IsValidationRunning => _isValidationRunning;
    public bool IsValidationConfigurationEnabled => !_isValidationRunning;
    public bool CanValidate => !_isValidationRunning && _canValidateFromParent();
    public string ValidationStatusText => _isValidationRunning
        ? string.Format(
            CultureInfo.CurrentCulture,
            OpenVisionLanguageService.T("Axis.RecipeValidationRunning"),
            _completedRuns,
            _recipe.ValidationRepetitions)
        : LatestResult is null
            ? OpenVisionLanguageService.T("Axis.RecipeValidationReady")
            : LatestResult.IsSuccess
                ? string.Format(
                    CultureInfo.CurrentCulture,
                    OpenVisionLanguageService.T("Axis.RecipeValidationPassed"),
                    LatestResult.CompletedRuns)
                : OpenVisionLanguageService.T("Axis.RecipeValidationMismatch");
    public string ValidationResultText
    {
        get
        {
            if (LatestResult is null)
            {
                return OpenVisionLanguageService.T("Axis.RecipeValidationNoResult");
            }
            if (LatestResult.IsSuccess)
            {
                return string.Format(
                    CultureInfo.CurrentCulture,
                    OpenVisionLanguageService.T("Axis.RecipeValidationEvidence"),
                    ShortHash(LatestResult.EvidenceHash));
            }
            var mismatch = LatestResult.FirstMismatch;
            return mismatch is null
                ? OpenVisionLanguageService.T("Axis.RecipeValidationMismatch")
                : string.Format(
                    CultureInfo.CurrentCulture,
                    OpenVisionLanguageService.T("Axis.RecipeValidationMismatchDetail"),
                    mismatch.RunIndex,
                    mismatch.EvidenceKind,
                    mismatch.TickIndex);
        }
    }
    public string EvidenceStatusText => _artifactStore.State switch
    {
        MultiAxisCommissioningArtifactState.MemoryOnly => OpenVisionLanguageService.T("Axis.RecipeEvidenceMemoryOnly"),
        MultiAxisCommissioningArtifactState.Saved => OpenVisionLanguageService.T("Axis.RecipeEvidenceSaved"),
        MultiAxisCommissioningArtifactState.Restored => OpenVisionLanguageService.T("Axis.RecipeEvidenceRestored"),
        MultiAxisCommissioningArtifactState.StaleRejected => OpenVisionLanguageService.T("Axis.RecipeEvidenceStale"),
        MultiAxisCommissioningArtifactState.SaveFailed => OpenVisionLanguageService.T("Axis.RecipeEvidenceSaveFailed"),
        _ => OpenVisionLanguageService.T("Axis.RecipeEvidenceNone")
    };
    public IReadOnlyList<DeterministicCommissioningResultHistoryEntry> ResultHistoryEntries =>
        ResultHistory.Entries.IsDefault
            ? Array.Empty<DeterministicCommissioningResultHistoryEntry>()
            : ResultHistory.Entries;
    public DeterministicCommissioningResultHistoryEntry? SelectedHistoryEntry
    {
        get => _selectedHistoryEntry;
        set
        {
            if (SetProperty(ref _selectedHistoryEntry, value))
            {
                RaiseChanged();
            }
        }
    }
    public bool CanAcceptBaseline => !_isValidationRunning
        && !_isOtherValidationRunning()
        && SelectedHistoryEntry?.Reference is not null;
    public bool CanClearBaseline => !_isValidationRunning
        && !_isOtherValidationRunning()
        && AcceptedBaseline is not null;
    public bool CanNavigateToMismatch => !_isValidationRunning
        && !_isOtherValidationRunning()
        && !string.IsNullOrWhiteSpace(_baselineComparison?.FirstMismatch?.TargetId);
    public string HistoryStatusText => ResultHistory.Entries.IsDefaultOrEmpty
        ? OpenVisionLanguageService.T("Axis.RecipeHistoryEmpty")
        : string.Format(
            CultureInfo.CurrentCulture,
            OpenVisionLanguageService.T("Axis.RecipeHistorySummary"),
            ResultHistory.Entries.Length,
            DeterministicMultiAxisCommissioningResultHistory.MaximumEntries);
    public string BaselineStatusText
    {
        get
        {
            if (AcceptedBaseline is null)
            {
                return OpenVisionLanguageService.T("Axis.RecipeBaselineNone");
            }
            if (_baselineComparison is null)
            {
                return string.Format(
                    CultureInfo.CurrentCulture,
                    OpenVisionLanguageService.T("Axis.RecipeBaselineAccepted"),
                    ShortHash(AcceptedBaseline.EvidenceHash));
            }
            if (_baselineComparison.IsMatch)
            {
                return string.Format(
                    CultureInfo.CurrentCulture,
                    OpenVisionLanguageService.T("Axis.RecipeBaselineMatch"),
                    ShortHash(AcceptedBaseline.EvidenceHash));
            }
            var mismatch = _baselineComparison.FirstMismatch;
            return mismatch is null
                ? OpenVisionLanguageService.T("Axis.RecipeBaselineMismatch")
                : string.Format(
                    CultureInfo.CurrentCulture,
                    OpenVisionLanguageService.T("Axis.RecipeBaselineMismatchDetail"),
                    string.IsNullOrWhiteSpace(mismatch.TargetId)
                        ? _recipe.Name
                        : mismatch.TargetId,
                    mismatch.EvidenceKind,
                    mismatch.TickIndex);
        }
    }

    internal DeterministicMultiAxisCommissioningResultPackage? LatestResult => _artifactStore.LatestResult;
    internal DeterministicMultiAxisCommissioningBaseline? AcceptedBaseline => _artifactStore.AcceptedBaseline;
    internal DeterministicMultiAxisCommissioningResultHistory ResultHistory => _artifactStore.History;
    internal DeterministicCommissioningBaselineComparison? BaselineComparison => _baselineComparison;
    internal bool HasRestoredResult => _artifactStore.HasRestoredResult;
    internal bool RejectedStaleResult =>
        _artifactStore.State == MultiAxisCommissioningArtifactState.StaleRejected;
    internal bool HasLatestResult => LatestResult is not null;
    internal Task? ValidationTask => _validationTask;

    public void Reset()
    {
        _artifactStore.Reset(_getProject().Id);
        _selectedHistoryEntry = null;
        _baselineComparison = null;
        _isValidationRunning = false;
        _completedRuns = 0;
        RaiseChanged(invalidateCommands: false);
    }

    public void Restore()
    {
        var project = _getProject();
        _selectedHistoryEntry = null;
        _baselineComparison = null;
        _isValidationRunning = false;
        _completedRuns = 0;
        var projectPath = _getProjectPath();
        _artifactStore.Restore(project.Id, projectPath, CreateArtifactContext);
        _selectedHistoryEntry = ResultHistory.Entries.LastOrDefault();
        _completedRuns = LatestResult?.CompletedRuns ?? 0;
        if (_artifactStore.HasRestoredResult)
        {
            _baselineComparison = AcceptedBaseline?.CompareTo(LatestResult);
            _log("Saved commissioning validation result restored");
        }
        else if (_artifactStore.State == MultiAxisCommissioningArtifactState.StaleRejected)
        {
            _log("Saved commissioning validation result rejected because project or recipe changed");
        }
        RaiseChanged();
    }

    public void RelinkProjectPath(string projectPath)
    {
        _artifactStore.RelinkProjectPath(projectPath);
    }

    public void PersistResult()
    {
        var errorDetail = _artifactStore.Persist(_getProjectPath(), CreateArtifactContext);
        if (errorDetail is not null)
        {
            _log($"Commissioning evidence save failed · {errorDetail}");
        }
        RaiseChanged();
    }

    public void PersistForProjectPath(string projectPath)
    {
        RelinkProjectPath(projectPath);
        PersistResult();
    }

    internal void NotifyRuntimeChanged(bool invalidateCommands = true) => RaiseChanged(invalidateCommands);

    internal void NotifyRecipeChanged(bool invalidateCommands = true)
    {
        _artifactStore.InvalidateContextIfResult();
        _baselineComparison = null;
        RaiseChanged(invalidateCommands);
    }

    internal void InvalidateContextIfResult()
    {
        if (LatestResult is null)
        {
            return;
        }

        _artifactStore.InvalidateContextIfResult();
        _baselineComparison = null;
        RaiseChanged();
    }

    internal void RefreshLocalization() => RaiseChanged(invalidateCommands: false);

    internal void CancelValidation()
    {
        try
        {
            _validationCancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    internal void InvalidateCommands()
    {
        RaiseCanExecuteChanged(ValidateCommand);
        RaiseCanExecuteChanged(AcceptBaselineCommand);
        RaiseCanExecuteChanged(ClearBaselineCommand);
        RaiseCanExecuteChanged(NavigateToMismatchCommand);
    }

    private Task RunValidationTask()
    {
        var task = RunValidationTrackedAsync();
        _validationTask = task;
        return task;
    }

    private async Task RunValidationTrackedAsync()
    {
        var cancellation = new CancellationTokenSource();
        _validationCancellation = cancellation;
        try
        {
            await ValidateAsync(cancellation.Token);
        }
        finally
        {
            if (ReferenceEquals(_validationCancellation, cancellation))
            {
                _validationCancellation = null;
            }
            cancellation.Dispose();
        }
    }

    private async Task ValidateAsync(CancellationToken cancellationToken)
    {
        var project = _getProject();
        if (!CanValidate || project.MultiAxisCommissioningRecipe is not { } recipe)
        {
            return;
        }

        SimulationRuntimeConfiguration runtime;
        try
        {
            runtime = _buildRuntime();
        }
        catch (Exception exception) when (exception is InvalidDataException or ArgumentException)
        {
            _setStatus(OpenVisionLanguageService.T("Axis.RecipeValidationRejected"));
            _log($"Commissioning validation rejected · {exception.Message}");
            return;
        }

        var projectJson = _serializeProject();
        var projectPath = _getProjectPath()
            ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, $"unsaved-{project.Id}.ovmachine"));
        _artifactStore.SetLatestResult(null);
        _completedRuns = 0;
        SetValidationRunning(true);
        _setStatus(OpenVisionLanguageService.T("Axis.RecipeValidationStarted"));
        _log($"Commissioning repeat validation started · {recipe.ValidationRepetitions} run(s)");
        try
        {
            var result = await new DeterministicMultiAxisCommissioningRunner().RunAsync(
                runtime,
                project.Id,
                project.Name,
                projectPath,
                projectJson,
                recipe,
                _fixedStep,
                UpdateProgressAsync,
                cancellationToken);
            _artifactStore.SetLatestResult(result);
            _artifactStore.AppendHistory(result, DateTimeOffset.UtcNow);
            SelectedHistoryEntry = ResultHistory.Entries[^1];
            _baselineComparison = AcceptedBaseline?.CompareTo(result);
            _setStatus(result.IsSuccess
                ? OpenVisionLanguageService.T("Axis.RecipeValidationPassedStatus")
                : OpenVisionLanguageService.T("Axis.RecipeValidationMismatchStatus"));
            _log(
                result.IsSuccess
                    ? $"Commissioning repeat validation passed · {result.CompletedRuns} run(s) · {ShortHash(result.EvidenceHash)}"
                    : $"Commissioning repeat validation mismatch · run {result.FirstMismatch?.RunIndex} · {result.FirstMismatch?.EvidenceKind} · Tick {result.FirstMismatch?.TickIndex}");
            PersistResult();
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
        {
            _setStatus(OpenVisionLanguageService.T("Axis.RecipeValidationRejected"));
            _log($"Commissioning repeat validation rejected · {exception.Message}");
        }
        finally
        {
            SetValidationRunning(false);
        }
    }

    private Task UpdateProgressAsync(int completedRuns) => _dispatchToUi(() =>
    {
        _completedRuns = completedRuns;
        RaiseChanged();
    });

    private void AcceptBaseline()
    {
        var baseline = SelectedHistoryEntry?.Reference;
        if (baseline is null || !baseline.HasValidEvidenceHash())
        {
            return;
        }

        _artifactStore.SetAcceptedBaseline(baseline);
        _baselineComparison = baseline.CompareTo(LatestResult);
        _setStatus(OpenVisionLanguageService.T("Axis.RecipeBaselineAcceptedStatus"));
        _log($"Commissioning baseline accepted · {ShortHash(baseline.EvidenceHash)}");
        PersistResult();
    }

    private void ClearBaseline()
    {
        _baselineComparison = null;
        var errorDetail = _artifactStore.ClearBaseline(_getProjectPath());
        if (errorDetail is not null)
        {
            _log($"Commissioning baseline clear failed · {errorDetail}");
        }
        _setStatus(OpenVisionLanguageService.T("Axis.RecipeBaselineClearedStatus"));
        RaiseChanged();
    }

    private void NavigateToMismatch()
    {
        if (_baselineComparison?.FirstMismatch is { } mismatch
            && !string.IsNullOrWhiteSpace(mismatch.TargetId))
        {
            _navigateToMismatch(mismatch);
        }
    }

    private void SetValidationRunning(bool value)
    {
        _isValidationRunning = value;
        RaiseChanged();
    }

    private void RaiseChanged(bool invalidateCommands = true)
    {
        OnPropertyChanged(nameof(IsValidationRunning));
        OnPropertyChanged(nameof(IsValidationConfigurationEnabled));
        OnPropertyChanged(nameof(CanValidate));
        OnPropertyChanged(nameof(ValidationStatusText));
        OnPropertyChanged(nameof(ValidationResultText));
        OnPropertyChanged(nameof(EvidenceStatusText));
        OnPropertyChanged(nameof(ResultHistoryEntries));
        OnPropertyChanged(nameof(SelectedHistoryEntry));
        OnPropertyChanged(nameof(HistoryStatusText));
        OnPropertyChanged(nameof(BaselineStatusText));
        OnPropertyChanged(nameof(CanAcceptBaseline));
        OnPropertyChanged(nameof(CanClearBaseline));
        OnPropertyChanged(nameof(CanNavigateToMismatch));
        if (invalidateCommands)
        {
            InvalidateCommands();
        }
        _notifyParentPresentationChanged(invalidateCommands);
    }

    private MultiAxisCommissioningArtifactContext? CreateArtifactContext()
    {
        var project = _getProject();
        if (project.MultiAxisCommissioningRecipe is not { } recipe)
        {
            return null;
        }

        return new MultiAxisCommissioningArtifactContext(
            project.Id,
            _serializeProject(),
            _fixedStep,
            recipe);
    }

    private static string ShortHash(string hash) => hash.Length <= 12 ? hash : hash[..12];

    private static void RaiseCanExecuteChanged(ICommand command)
    {
        switch (command)
        {
            case AsyncRelayCommand asyncCommand:
                asyncCommand.RaiseCanExecuteChanged();
                break;
            case RelayCommand relayCommand:
                relayCommand.RaiseCanExecuteChanged();
                break;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CancelValidation();
    }
}
