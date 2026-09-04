using System.IO;
using System.Windows.Input;
using OpenVisionLab;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Simulation.Engine;
using OpenVisionLab.Machine.Simulation.Scenarios;
using OpenVisionLab.MachineStudio.Models.Simulation;
using OpenVisionLab.MachineStudio.ViewModel.Simulation;

namespace OpenVisionLab.MachineStudio.ViewModel;

public sealed class SimulationScenarioBatchViewModel : ViewModelBase, IDisposable
{
    private readonly SimulationWorkspaceViewModel _workspace;
    private readonly SimulationScenarioBatchArtifactStore _artifactStore = new();
    private readonly SimulationScenarioBatchPresentation _presentation = new();
    private readonly SimulationScenarioBatchRepetitionRunner _batchRepetitionRunner;
    private readonly Func<bool> _canRunFromParent;
    private readonly Func<bool> _canExportFromParent;
    private readonly Func<bool> _canImportFromParent;
    private readonly Func<bool> _isOtherValidationRunning;
    private readonly Func<MachineProjectDocument> _getProject;
    private readonly Func<string?> _getProjectPath;
    private readonly Func<Task<bool>> _ensureRuntimeDefinitionApplied;
    private readonly Func<bool> _isMainRuntimeRunning;
    private readonly Func<Task<bool>> _pauseMainRuntime;
    private readonly Action<MachineProjectDocument> _saveProjectScenario;
    private readonly Func<SimulationRuntimeConfiguration> _buildRuntime;
    private readonly Func<string> _serializeProject;
    private readonly TimeSpan _fixedStep;
    private readonly Func<Action, Task> _dispatchToUi;
    private readonly Func<IReadOnlyList<SimulationScenarioTargetOption>> _getScenarioTargets;
    private readonly Action _resetUnifiedEvidence;
    private readonly Action<string> _setStatus;
    private readonly Action<string> _log;
    private readonly Action<DeterministicSimulationBatchMismatch> _navigateToMismatch;
    private readonly Action<bool> _notifyParentPresentationChanged;
    private readonly Action<Exception> _onCommandException;
    private CancellationTokenSource? _batchCancellation;
    private Task? _batchTask;
    private bool _isBatchRunning;
    private bool _batchWasCanceled;
    private int _batchCompletedRuns;
    private bool _disposed;

    public SimulationScenarioBatchViewModel(
        SimulationWorkspaceViewModel workspace,
        Func<bool> canRunFromParent,
        Func<bool> canExportFromParent,
        Func<bool> canImportFromParent,
        Func<bool> isOtherValidationRunning,
        Func<MachineProjectDocument> getProject,
        Func<string?> getProjectPath,
        Func<Task<bool>> ensureRuntimeDefinitionApplied,
        Func<bool> isMainRuntimeRunning,
        Func<Task<bool>> pauseMainRuntime,
        Action<MachineProjectDocument> saveProjectScenario,
        Func<SimulationRuntimeConfiguration> buildRuntime,
        Func<string> serializeProject,
        TimeSpan fixedStep,
        Func<Action, Task> dispatchToUi,
        Func<IReadOnlyList<SimulationScenarioTargetOption>> getScenarioTargets,
        Action resetUnifiedEvidence,
        Action<string> setStatus,
        Action<string> log,
        Action<DeterministicSimulationBatchMismatch> navigateToMismatch,
        Action<bool> notifyParentPresentationChanged,
        Action<Exception> onCommandException)
    {
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _canRunFromParent = canRunFromParent ?? throw new ArgumentNullException(nameof(canRunFromParent));
        _canExportFromParent = canExportFromParent ?? throw new ArgumentNullException(nameof(canExportFromParent));
        _canImportFromParent = canImportFromParent ?? throw new ArgumentNullException(nameof(canImportFromParent));
        _isOtherValidationRunning = isOtherValidationRunning ?? throw new ArgumentNullException(nameof(isOtherValidationRunning));
        _getProject = getProject ?? throw new ArgumentNullException(nameof(getProject));
        _getProjectPath = getProjectPath ?? throw new ArgumentNullException(nameof(getProjectPath));
        _ensureRuntimeDefinitionApplied = ensureRuntimeDefinitionApplied
            ?? throw new ArgumentNullException(nameof(ensureRuntimeDefinitionApplied));
        _isMainRuntimeRunning = isMainRuntimeRunning
            ?? throw new ArgumentNullException(nameof(isMainRuntimeRunning));
        _pauseMainRuntime = pauseMainRuntime ?? throw new ArgumentNullException(nameof(pauseMainRuntime));
        _saveProjectScenario = saveProjectScenario
            ?? throw new ArgumentNullException(nameof(saveProjectScenario));
        _buildRuntime = buildRuntime ?? throw new ArgumentNullException(nameof(buildRuntime));
        _serializeProject = serializeProject ?? throw new ArgumentNullException(nameof(serializeProject));
        _fixedStep = fixedStep;
        _batchRepetitionRunner = new SimulationScenarioBatchRepetitionRunner();
        _dispatchToUi = dispatchToUi ?? throw new ArgumentNullException(nameof(dispatchToUi));
        _getScenarioTargets = getScenarioTargets
            ?? throw new ArgumentNullException(nameof(getScenarioTargets));
        _resetUnifiedEvidence = resetUnifiedEvidence
            ?? throw new ArgumentNullException(nameof(resetUnifiedEvidence));
        _setStatus = setStatus ?? throw new ArgumentNullException(nameof(setStatus));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _navigateToMismatch = navigateToMismatch
            ?? throw new ArgumentNullException(nameof(navigateToMismatch));
        _notifyParentPresentationChanged = notifyParentPresentationChanged
            ?? throw new ArgumentNullException(nameof(notifyParentPresentationChanged));
        _onCommandException = onCommandException
            ?? throw new ArgumentNullException(nameof(onCommandException));

        RunCommand = new AsyncRelayCommand(
            _ => RunBatchTask(),
            _ => CanRunScenarioBatch,
            _onCommandException,
            useCommandManagerRequery: false);
        CancelCommand = new RelayCommand(
            _ => CancelBatch(),
            _ => _isBatchRunning,
            useCommandManagerRequery: false);
        AcceptBaselineCommand = new RelayCommand(
            _ => AcceptBatchBaseline(),
            _ => CanAcceptBatchBaseline,
            useCommandManagerRequery: false);
        ClearBaselineCommand = new RelayCommand(
            _ => ClearBatchBaseline(),
            _ => CanClearBatchBaseline,
            useCommandManagerRequery: false);
        NavigateToMismatchCommand = new RelayCommand(
            _ => NavigateToBatchMismatch(),
            _ => CanNavigateToBatchMismatch,
            useCommandManagerRequery: false);
    }

    public ICommand RunCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand AcceptBaselineCommand { get; }
    public ICommand ClearBaselineCommand { get; }
    public ICommand NavigateToMismatchCommand { get; }

    public bool IsBatchRunning => _isBatchRunning;
    public bool IsScenarioConfigurationEnabled => !_isBatchRunning && !_isOtherValidationRunning();
    public int BatchCompletedRuns => _batchCompletedRuns;
    public bool CanRunScenarioBatch => !_isBatchRunning && _canRunFromParent();
    public bool CanAcceptBatchBaseline => !_isBatchRunning
        && LatestBatchResult is { IsComplete: true, IsSuccess: true, Runs.Length: > 0 };
    public bool CanClearBatchBaseline => !_isBatchRunning && AcceptedBatchBaseline is not null;
    public bool CanNavigateToBatchMismatch => !_isBatchRunning
        && LatestBatchResult?.FirstMismatch is not null;
    public bool CanExportEvidence => !_isBatchRunning
        && _canExportFromParent()
        && LatestBatchResult is { IsComplete: true }
        && LatestBatchResult.HasValidEvidenceHash();
    public bool CanImportEvidence => !_isBatchRunning
        && !_isOtherValidationRunning()
        && _canImportFromParent();

    public string BatchStatusText => _presentation.GetBatchStatusText(CreatePresentationState());

    public string BatchResultText => _presentation.GetBatchResultText(CreatePresentationState());

    public string BatchBaselineText => _presentation.GetBatchBaselineText(CreatePresentationState());

    public string BatchArtifactStatusText =>
        _presentation.GetBatchArtifactStatusText(CreatePresentationState());

    public IReadOnlyList<ScenarioAssertionOutcomePresentation> BatchAssertionOutcomes =>
        _presentation.GetAssertionOutcomes(GetLastBatchRunResult(), _getScenarioTargets());

    public bool HasBatchAssertionOutcomes =>
        SimulationScenarioBatchPresentation.HasAssertionOutcomes(GetLastBatchRunResult());

    internal DeterministicSimulationBatchResultPackage? LatestBatchResult => _artifactStore.LatestBatchResult;
    internal DeterministicSimulationRunResultPackage? AcceptedBatchBaseline => _artifactStore.AcceptedBatchBaseline;
    internal bool HasAcceptedBatchBaseline => AcceptedBatchBaseline is not null;
    internal bool BatchWasCanceled => _batchWasCanceled;
    internal bool HasRestoredBatchArtifacts =>
        _artifactStore.HasRestoredArtifacts;
    internal bool RejectedStaleBatchArtifacts =>
        _artifactStore.State == SimulationScenarioBatchArtifactState.StaleRejected;
    internal Task? BatchTask => _batchTask;

    public void Reset()
    {
        _artifactStore.Reset();
        _batchWasCanceled = false;
        _batchCompletedRuns = 0;
        RaiseChanged(invalidateCommands: false);
    }

    public void Restore()
    {
        _batchWasCanceled = false;
        _batchCompletedRuns = 0;
        var projectPath = _getProjectPath();
        _artifactStore.Restore(projectPath, CreateArtifactContext);
        _batchCompletedRuns = LatestBatchResult?.CompletedRuns ?? 0;
        if (_artifactStore.State == SimulationScenarioBatchArtifactState.StaleRejected)
        {
            _log("Saved batch result or baseline rejected because project or scenario context changed");
        }
        else if (_artifactStore.State == SimulationScenarioBatchArtifactState.Restored)
        {
            _log("Saved batch result and baseline restored");
        }

        RaiseChanged();
    }

    public void RelinkProjectPath(string projectPath)
    {
        _artifactStore.RelinkProjectPath(projectPath);
    }

    public void PersistBatchArtifacts()
    {
        var errorDetail = _artifactStore.Persist(_getProjectPath(), CreateArtifactContext);
        if (errorDetail is not null)
        {
            _log($"Evidence save failed · {errorDetail}");
        }

        RaiseChanged();
    }

    private SimulationScenarioBatchArtifactContext? CreateArtifactContext()
    {
        var targetId = _workspace.ScenarioTargetId;
        if (string.IsNullOrWhiteSpace(targetId))
        {
            return null;
        }

        var project = _getProject();
        return new SimulationScenarioBatchArtifactContext(
            project.Id,
            _serializeProject(),
            _fixedStep,
            _workspace.BuildEngineProfile(targetId),
            _workspace.BatchRepetitionCount,
            BuildIdentity.Current);
    }

    public void PersistForProjectPath(string projectPath)
    {
        RelinkProjectPath(projectPath);
        PersistBatchArtifacts();
    }

    internal void SetImportedPackages(
        DeterministicSimulationBatchResultPackage batchResult,
        DeterministicSimulationRunResultPackage? acceptedBaseline,
        bool notifyPresentation = true)
    {
        _artifactStore.SetImportedPackages(batchResult, acceptedBaseline);
        _batchCompletedRuns = batchResult.CompletedRuns;
        _batchWasCanceled = false;
        if (notifyPresentation)
        {
            RaiseChanged();
        }
    }

    internal bool TryExportEvidence(string path)
    {
        if (!CanExportEvidence || string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        if (_artifactStore.TryExportEvidence(path, out var evidenceHash, out var errorDetail))
        {
            _setStatus(OpenVisionLanguageService.T(
                "Simulation.EvidenceExported",
                "결정적 시뮬레이션 증거를 내보냈습니다.",
                "Deterministic simulation evidence exported."));
            _log($"Portable simulation evidence exported · {ShortHash(evidenceHash)}");
            return true;
        }

        _setStatus(OpenVisionLanguageService.T(
            "Simulation.EvidenceExportFailed",
            "시뮬레이션 증거를 내보내지 못했습니다.",
            "Simulation evidence could not be exported."));
        _log($"Portable simulation evidence export failed · {errorDetail}");
        return false;
    }

    internal bool TryImportEvidence(string path)
    {
        if (!CanImportEvidence || string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        if (_artifactStore.TryImportEvidence(
                path,
                _getProjectPath(),
                CreateArtifactContext,
                out var evidenceHash,
                out var rejectionDetail))
        {
            _setStatus(OpenVisionLanguageService.T(
                "Simulation.EvidenceImported",
                "결정적 시뮬레이션 증거를 가져왔습니다. 실행하지 않았습니다.",
                "Deterministic simulation evidence imported without execution."));
            _batchCompletedRuns = LatestBatchResult?.CompletedRuns ?? 0;
            _batchWasCanceled = false;
            _log($"Portable simulation evidence imported · {ShortHash(evidenceHash)}");
            RaiseChanged();
            return true;
        }

        SetEvidenceImportRejected(rejectionDetail ?? "file could not be loaded");
        return false;
    }

    internal void NotifyRuntimeChanged(bool invalidateCommands = true) => RaiseChanged(invalidateCommands);

    internal void RefreshLocalization() => RaiseChanged(invalidateCommands: false);

    internal void CancelBatch()
    {
        try
        {
            _batchCancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    internal void InvalidateCommands()
    {
        RaiseCanExecuteChanged(RunCommand);
        RaiseCanExecuteChanged(CancelCommand);
        RaiseCanExecuteChanged(AcceptBaselineCommand);
        RaiseCanExecuteChanged(ClearBaselineCommand);
        RaiseCanExecuteChanged(NavigateToMismatchCommand);
    }

    private Task RunBatchTask()
    {
        var task = RunScenarioBatchAsync();
        _batchTask = task;
        return task;
    }

    private async Task RunScenarioBatchAsync()
    {
        if (!await _ensureRuntimeDefinitionApplied())
        {
            return;
        }

        var targetId = _workspace.ScenarioTargetId;
        if (string.IsNullOrWhiteSpace(targetId))
        {
            _setStatus(OpenVisionLanguageService.T("Simulation.ScenarioTargetRequired"));
            return;
        }

        if (_isMainRuntimeRunning() && !await _pauseMainRuntime())
        {
            return;
        }

        var project = _getProject();
        _saveProjectScenario(project);
        var runtime = _buildRuntime();
        var profile = _workspace.BuildEngineProfile(targetId);
        var projectJson = _serializeProject();
        var projectPath = _getProjectPath()
            ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, $"unsaved-{project.Id}.ovmachine"));
        var repetitionCount = _workspace.BatchRepetitionCount;
        var artifactContext = new SimulationScenarioBatchArtifactContext(
            project.Id,
            projectJson,
            _fixedStep,
            profile,
            repetitionCount,
            BuildIdentity.Current);
        var definition = new DeterministicSimulationBatchDefinition(
            artifactContext.BatchId,
            repetitionCount,
            BuildIdentity.Current);

        _batchCancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        _batchCancellation = cancellation;
        _batchWasCanceled = false;
        _batchCompletedRuns = 0;
        _artifactStore.SetLatestBatchResult(null);
        _resetUnifiedEvidence();
        SetBatchRunning(true);
        _setStatus(OpenVisionLanguageService.T("Simulation.BatchStarted"));
        _log($"Sequential batch started · {repetitionCount} run(s)");

        try
        {
            var batchRunner = new DeterministicSimulationBatchRunner();
            var batchResult = await batchRunner.RunAsync(
                definition,
                async (runIndex, cancellationToken) =>
                {
                    await UpdateBatchProgressAsync(runIndex - 1);
                    var result = await _batchRepetitionRunner.RunAsync(
                        new SimulationScenarioBatchRepetitionRequest(
                            project.Id,
                            project.Name,
                            runtime,
                            profile,
                            projectPath,
                            projectJson,
                            _fixedStep),
                        cancellationToken);
                    await UpdateBatchProgressAsync(runIndex);
                    return result;
                },
                AcceptedBatchBaseline,
                cancellation.Token);
            _artifactStore.SetLatestBatchResult(batchResult);

            _setStatus(batchResult.IsSuccess
                ? OpenVisionLanguageService.T("Simulation.BatchPassedStatus")
                : OpenVisionLanguageService.T("Simulation.BatchMismatchStatus"));
            _log(
                batchResult.IsSuccess
                    ? $"Sequential batch passed · {batchResult.CompletedRuns} run(s) · {ShortHash(batchResult.EvidenceHash)}"
                    : FormatBatchMismatchLog(batchResult.FirstMismatch));
            PersistBatchArtifacts();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            _batchWasCanceled = true;
            _setStatus(OpenVisionLanguageService.T("Simulation.BatchCanceled"));
            _log($"Sequential batch canceled after {_batchCompletedRuns} completed run(s)");
        }
        finally
        {
            SetBatchRunning(false);
            if (ReferenceEquals(_batchCancellation, cancellation))
            {
                _batchCancellation = null;
            }
            cancellation.Dispose();
            RaiseChanged();
        }
    }

    private Task UpdateBatchProgressAsync(int completedRuns) => _dispatchToUi(() =>
    {
        _batchCompletedRuns = completedRuns;
        RaiseChanged();
    });

    private void AcceptBatchBaseline()
    {
        var firstRun = LatestBatchResult?.Runs.FirstOrDefault();
        if (firstRun is null || !LatestBatchResult!.IsComplete || !LatestBatchResult.IsSuccess)
        {
            return;
        }

        _artifactStore.SetAcceptedBatchBaseline(firstRun.Result);
        _setStatus(OpenVisionLanguageService.T("Simulation.BatchBaselineAcceptedStatus"));
        _log($"Accepted baseline · {ShortHash(AcceptedBatchBaseline!.EvidenceHash)}");
        PersistBatchArtifacts();
        RaiseChanged();
    }

    private void ClearBatchBaseline()
    {
        var errorDetail = _artifactStore.ClearBaseline(_getProjectPath());
        if (errorDetail is not null)
        {
            _log($"Baseline reset failed · {errorDetail}");
            RaiseChanged();
            return;
        }

        _setStatus(OpenVisionLanguageService.T("Simulation.BatchBaselineClearedStatus"));
        _log("Accepted baseline cleared");
        RaiseChanged();
    }

    private void NavigateToBatchMismatch()
    {
        if (LatestBatchResult?.FirstMismatch is { } mismatch)
        {
            _navigateToMismatch(mismatch);
        }
    }

    private void SetBatchRunning(bool value)
    {
        if (_isBatchRunning == value)
        {
            return;
        }

        _isBatchRunning = value;
        RaiseChanged();
    }

    private void SetEvidenceImportRejected(string detail)
    {
        _setStatus(OpenVisionLanguageService.T(
            "Simulation.EvidenceImportRejected",
            "현재 프로젝트 또는 시나리오와 일치하지 않아 증거를 가져오지 않았습니다.",
            "Evidence was not imported because it is not valid for the current project or scenario."));
        _log($"Portable simulation evidence import rejected · {detail}");
        RaiseChanged(invalidateCommands: false);
    }

    private void RaiseChanged(bool invalidateCommands = true)
    {
        OnPropertyChanged(nameof(IsBatchRunning));
        OnPropertyChanged(nameof(IsScenarioConfigurationEnabled));
        OnPropertyChanged(nameof(BatchCompletedRuns));
        OnPropertyChanged(nameof(CanRunScenarioBatch));
        OnPropertyChanged(nameof(CanAcceptBatchBaseline));
        OnPropertyChanged(nameof(CanClearBatchBaseline));
        OnPropertyChanged(nameof(CanNavigateToBatchMismatch));
        OnPropertyChanged(nameof(CanExportEvidence));
        OnPropertyChanged(nameof(CanImportEvidence));
        OnPropertyChanged(nameof(BatchStatusText));
        OnPropertyChanged(nameof(BatchResultText));
        OnPropertyChanged(nameof(BatchBaselineText));
        OnPropertyChanged(nameof(BatchArtifactStatusText));
        OnPropertyChanged(nameof(BatchAssertionOutcomes));
        OnPropertyChanged(nameof(HasBatchAssertionOutcomes));
        if (invalidateCommands)
        {
            InvalidateCommands();
        }
        _notifyParentPresentationChanged(invalidateCommands);
    }

    private SimulationScenarioBatchPresentationState CreatePresentationState() =>
        new(
            _isBatchRunning,
            _batchWasCanceled,
            _batchCompletedRuns,
            _workspace.BatchRepetitionCount,
            LatestBatchResult,
            AcceptedBatchBaseline,
            _artifactStore.State);

    private DeterministicSimulationRunResultPackage? GetLastBatchRunResult() =>
        LatestBatchResult?.Runs.LastOrDefault()?.Result;

    private static string FormatBatchMismatchLog(DeterministicSimulationBatchMismatch? mismatch) =>
        mismatch is null
            ? "Sequential batch evidence mismatch"
            : $"First mismatch · run {mismatch.RunIndex} · {mismatch.EvidenceKind} · {mismatch.TargetId} · Tick {mismatch.ObservedTickIndex}";

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
        CancelBatch();
    }
}
