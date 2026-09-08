using System.Globalization;
using OpenVisionLab;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Sequence.Authoring;

namespace OpenVisionLab.MachineStudio.ViewModel;

/// <summary>
/// Owns recipe authoring composition: connection setup, Sequence navigation,
/// isolated preview/playback and Process Plan review. Layout and Sequence editor
/// are borrowed; project-wide presentation changes use explicit shell callbacks.
/// </summary>
public sealed class RecipeAuthoringWorkspace : IDisposable
{
    private readonly RecipeConnectionProjectApplier _projectApplier = new();
    private readonly VirtualCameraInspectionTemplate _cameraTemplate = new();
    private readonly MachineLayoutViewModel _layout;
    private readonly SequenceEditorViewModel _sequenceEditor;
    private readonly Func<MachineProjectDocument> _getProject;
    private readonly Action<int> _selectDocumentTab;
    private readonly Action _markProjectChanged;
    private readonly Action _updateRunToolAvailability;
    private readonly Action _refreshDefinitionPresentation;
    private readonly Action _resetLayoutHistory;
    private readonly Action<string> _refreshCameraWorkflowPresentation;
    private readonly Action _invalidateCommands;
    private readonly Action<string> _setStatus;
    private readonly Action<string, string> _log;
    private bool _disposed;

    internal RecipeAuthoringWorkspace(
        MachineLayoutViewModel layout,
        SequenceEditorViewModel sequenceEditor,
        Func<MachineProjectDocument> getProject,
        Func<bool> isDesignMode,
        Action<MachineProjectDocument> validateRuntimeConfiguration,
        Action<int> selectDocumentTab,
        Action markProjectChanged,
        Action updateRunToolAvailability,
        Action refreshDefinitionPresentation,
        Action resetLayoutHistory,
        Action<string> refreshCameraWorkflowPresentation,
        Action invalidateCommands,
        Action<string> setStatus,
        Action<string, string> log)
    {
        _layout = layout ?? throw new ArgumentNullException(nameof(layout));
        _sequenceEditor = sequenceEditor ?? throw new ArgumentNullException(nameof(sequenceEditor));
        _getProject = getProject ?? throw new ArgumentNullException(nameof(getProject));
        _selectDocumentTab = selectDocumentTab ?? throw new ArgumentNullException(nameof(selectDocumentTab));
        _markProjectChanged = markProjectChanged ?? throw new ArgumentNullException(nameof(markProjectChanged));
        _updateRunToolAvailability = updateRunToolAvailability ?? throw new ArgumentNullException(nameof(updateRunToolAvailability));
        _refreshDefinitionPresentation = refreshDefinitionPresentation ?? throw new ArgumentNullException(nameof(refreshDefinitionPresentation));
        _resetLayoutHistory = resetLayoutHistory ?? throw new ArgumentNullException(nameof(resetLayoutHistory));
        _refreshCameraWorkflowPresentation = refreshCameraWorkflowPresentation ?? throw new ArgumentNullException(nameof(refreshCameraWorkflowPresentation));
        _invalidateCommands = invalidateCommands ?? throw new ArgumentNullException(nameof(invalidateCommands));
        _setStatus = setStatus ?? throw new ArgumentNullException(nameof(setStatus));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        var setup = new RecipeConnectionSetupWorkflow(
            _projectApplier,
            _getProject,
            ExitPlayback,
            CompleteConnectionSetupMutation,
            CompleteConnectionProcessBlockMutation,
            _setStatus,
            _log);
        var simulation = new RecipeConnectionSimulationWorkflow(
            _getProject,
            validateRuntimeConfiguration,
            _setStatus,
            _log);
        Connections = new RecipeConnectionWorkbenchViewModel(
            componentId =>
            {
                if (componentId is null)
                {
                    _layout.SelectedItem = null;
                }
                else
                {
                    _layout.Select(componentId);
                }
            },
            OpenConnectionSequenceStep,
            AddConnectionSequenceStep,
            simulation.ValidateSimulationReadiness,
            simulation.RunSequenceStepPreviewAsync,
            simulation.RunRecipeDryRunAsync,
            ShowConnectionDryRunStep,
            ApplyConnectionVirtualCameraWorkflow,
            setup.ApplyStationSkeleton,
            setup.ApplyLoadLockSetup,
            setup.ApplyWaferHandlerSetup,
            setup.ApplyPrealignerSetup,
            setup.ApplyInspectionHandoffSetup,
            setup.ApplyInspectionSortRouterSetup,
            setup.ApplyOhtHandoffSetup,
            setup.ApplyProcessBlocks,
            ApplyConnectionProcessBlockTimeouts,
            OnConnectionCheckpointTemplateApplied,
            OpenProcessBlockSequenceStep);
        ProcessPlanReview = new ProcessPlanReviewViewModel(
            () => Connections.IsEditable,
            () => Connections.ProcessBlocks.IsProcessBlockPreviewVisible,
            () => Connections.ProcessBlocks.VisibleProcessBlockItems,
            () => Connections.ProcessBlocks.ProcessBlockItems,
            TryOpenConnectionSequenceStepForReview,
            stepId => Connections.ProcessBlocks.SelectProcessBlockStep(stepId)?.StepText,
            _selectDocumentTab,
            _setStatus);
        Playback = new RecipeDryRunPlaybackViewModel(
            isDesignMode,
            isEditable => _layout.IsEditable = isEditable,
            _selectDocumentTab,
            step => Connections.DryRun.SelectedRecipeDryRunStep = step,
            _setStatus);
        Connections.ProcessBlocks.ProcessBlockPreviewClosed += OnProcessBlockPreviewClosed;
    }

    public RecipeConnectionWorkbenchViewModel Connections { get; }
    public RecipeDryRunPlaybackViewModel Playback { get; }
    public ProcessPlanReviewViewModel ProcessPlanReview { get; }

    private void OpenConnectionSequenceStep(string sequenceId, string stepId)
    {
        ProcessPlanReview.Clear();
        TryOpenConnectionSequenceStep(sequenceId, stepId);
    }

    private bool TryOpenConnectionSequenceStep(string sequenceId, string stepId)
    {
        _sequenceEditor.SelectStep(sequenceId, stepId);
        if (_sequenceEditor.SelectedStep is null)
        {
            _setStatus(OpenVisionLanguageService.T("Connections.OpenStepRejectedStatus"));
            return false;
        }

        _selectDocumentTab(2);
        _setStatus(string.Format(
            CultureInfo.CurrentCulture,
            OpenVisionLanguageService.T("Connections.OpenStepStatus"),
            _sequenceEditor.SelectedStep.DisplayName));
        return true;
    }

    private void OpenProcessBlockSequenceStep(string sequenceId, string stepId) =>
        ProcessPlanReview.OpenProcessBlockSequenceStep(sequenceId, stepId);

    private string? TryOpenConnectionSequenceStepForReview(string sequenceId, string stepId) =>
        TryOpenConnectionSequenceStep(sequenceId, stepId)
            ? _sequenceEditor.SelectedStep?.DisplayName
            : null;

    private void OnProcessBlockPreviewClosed(object? sender, EventArgs args) =>
        ProcessPlanReview.Clear();

    private void ShowConnectionDryRunStep(RecipeDryRunStepPresentation step)
    {
        Playback.Show(step, Connections.DryRun.Timeline);
    }

    public void ExitPlayback()
    {
        Playback.Exit();
    }

    private int ApplyConnectionProcessBlockTimeouts(
        SemiconductorManagedTimeoutAdjustmentPreview preview)
    {
        ExitPlayback();
        var result = _projectApplier.ApplyProcessBlockTimeouts(_getProject(), preview);
        if (!result.Changed)
        {
            _setStatus(OpenVisionLanguageService.T("Connections.ProcessBlockTimeoutRejectedStatus"));
            return 0;
        }

        _markProjectChanged();
        _updateRunToolAvailability();
        _sequenceEditor.Load(_getProject());
        _setStatus(string.Format(
            CultureInfo.CurrentCulture,
            OpenVisionLanguageService.T("Connections.ProcessBlockTimeoutAppliedStatus"),
            result.AppliedStepCount,
            preview.ProposedTimeoutMs));
        Connections.RefreshDefinitionPreservingProcessBlockPlan(_layout.SelectedItem?.Id);
        _log(
            "Sequence",
            $"Applied managed timeout adjustment · {result.AppliedStepCount} step(s) · {preview.ProposedTimeoutMs} ms");
        _invalidateCommands();
        return result.ChangeCount;
    }

    private void CompleteConnectionSetupMutation()
    {
        _markProjectChanged();
        _updateRunToolAvailability();
        _refreshDefinitionPresentation();
        _sequenceEditor.RefreshAuthoringTargets();
        _resetLayoutHistory();
        _invalidateCommands();
    }

    private void CompleteConnectionProcessBlockMutation()
    {
        _markProjectChanged();
        _updateRunToolAvailability();
        _refreshDefinitionPresentation();
        _sequenceEditor.Load(_getProject());
        _resetLayoutHistory();
        _invalidateCommands();
    }

    private string? AddConnectionSequenceStep(string targetId)
    {
        string? stepId = _sequenceEditor.TryAddStepForTarget(targetId);
        if (stepId is null)
        {
            _setStatus(string.Format(
                CultureInfo.CurrentCulture,
                OpenVisionLanguageService.T("Connections.AddStepRejectedStatus"),
                _sequenceEditor.StructuralEditStatus));
            return null;
        }

        _selectDocumentTab(2);
        _setStatus(string.Format(
            CultureInfo.CurrentCulture,
            OpenVisionLanguageService.T("Connections.AddStepStatus"),
            targetId));
        _log("Sequence", $"Added connection target step · {stepId} · {targetId}");
        return stepId;
    }

    private void OnConnectionCheckpointTemplateApplied(int appliedCount)
    {
        ExitPlayback();
        if (appliedCount <= 0)
        {
            _setStatus(OpenVisionLanguageService.T("Connections.CheckpointTemplateNoChangesStatus"));
            return;
        }

        _markProjectChanged();
        _updateRunToolAvailability();
        _sequenceEditor.RefreshAuthoringTargets();
        _setStatus(string.Format(
            CultureInfo.CurrentCulture,
            OpenVisionLanguageService.T("Connections.CheckpointTemplateAppliedStatus"),
            appliedCount));
        _log("Sequence", $"Applied representative recipe checkpoints · {appliedCount}");
        _invalidateCommands();
    }

    private bool ApplyConnectionVirtualCameraWorkflow()
    {
        ExitPlayback();
        var result = _cameraTemplate.Apply(_getProject());
        if (!result.Created)
        {
            return false;
        }

        _markProjectChanged();
        _updateRunToolAvailability();
        _refreshCameraWorkflowPresentation(result.CameraId);
        OpenConnectionSequenceStep(result.SequenceId, result.TriggerStepId);
        _setStatus(OpenVisionLanguageService.T("Connections.VirtualCameraWorkflowCreatedStatus"));
        _log(
            "Project",
            $"Created virtual-camera inspection workflow · {result.CameraId} · {result.SequenceId}");
        _invalidateCommands();
        return true;
    }

    public void ResetPresentation()
    {
        ExitPlayback();
        ProcessPlanReview.Clear();
    }

    internal void InvalidateCommands()
    {
        Playback.InvalidateCommands();
        ProcessPlanReview.InvalidateCommands();
    }

    // Shell close detaches subscriptions before asynchronous resource shutdown.
    internal void DetachEventHandlers() =>
        Connections.ProcessBlocks.ProcessBlockPreviewClosed -= OnProcessBlockPreviewClosed;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        DetachEventHandlers();
        Connections.Dispose();
    }
}
