using OpenVisionLab.Machine.Simulation.Snapshots;
using OpenVisionLab.MachineStudio.ViewModel.Simulation;

namespace OpenVisionLab.MachineStudio.ViewModel;

/// <summary>
/// Owns the mutable presentation side of the simulation snapshot boundary.
/// Pure projection remains in <see cref="SimulationRuntimeSnapshotProjection"/>;
/// this coordinator applies that projection to the child ViewModels and keeps
/// the current projection available to the shell.
/// </summary>
internal sealed class SimulationRuntimeProjectionCoordinator
{
    private readonly MultiAxisCommissioningRecipeEditorViewModel _commissioningRecipe;
    private readonly SimulationWorkspaceViewModel _simulationWorkspace;
    private readonly DigitalIoCommissioningViewModel _digitalIo;
    private readonly FaultManagerViewModel _faultManager;
    private readonly RuntimeDebuggerViewModel _runtimeDebugger;
    private readonly VisionExecutionEvidenceViewModel _visionExecutionEvidence;
    private readonly Action<bool> _setRunning;
    private readonly Action<SimulationSnapshot> _refreshManualProjection;
    private readonly Action _refreshCameraProjection;
    private bool _isApplyingProjection;

    internal SimulationRuntimeProjectionCoordinator(
        MultiAxisCommissioningRecipeEditorViewModel commissioningRecipe,
        SimulationWorkspaceViewModel simulationWorkspace,
        DigitalIoCommissioningViewModel digitalIo,
        FaultManagerViewModel faultManager,
        RuntimeDebuggerViewModel runtimeDebugger,
        VisionExecutionEvidenceViewModel visionExecutionEvidence,
        Action<bool> setRunning,
        Action<SimulationSnapshot> refreshManualProjection,
        Action refreshCameraProjection)
    {
        _commissioningRecipe = commissioningRecipe
            ?? throw new ArgumentNullException(nameof(commissioningRecipe));
        _simulationWorkspace = simulationWorkspace
            ?? throw new ArgumentNullException(nameof(simulationWorkspace));
        _digitalIo = digitalIo ?? throw new ArgumentNullException(nameof(digitalIo));
        _faultManager = faultManager ?? throw new ArgumentNullException(nameof(faultManager));
        _runtimeDebugger = runtimeDebugger
            ?? throw new ArgumentNullException(nameof(runtimeDebugger));
        _visionExecutionEvidence = visionExecutionEvidence
            ?? throw new ArgumentNullException(nameof(visionExecutionEvidence));
        _setRunning = setRunning ?? throw new ArgumentNullException(nameof(setRunning));
        _refreshManualProjection = refreshManualProjection
            ?? throw new ArgumentNullException(nameof(refreshManualProjection));
        _refreshCameraProjection = refreshCameraProjection
            ?? throw new ArgumentNullException(nameof(refreshCameraProjection));
    }

    internal SimulationRuntimeSnapshotProjection CurrentProjection { get; private set; } =
        SimulationRuntimeSnapshotProjection.Empty;

    internal bool IsApplyingProjection => _isApplyingProjection;

    internal void Apply(
        SimulationSnapshot snapshot,
        SimulationRuntimeProjectionSelection selection)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(selection);

        CurrentProjection = SimulationRuntimeSnapshotProjection.Create(snapshot, selection);
        _setRunning(CurrentProjection.IsRunning);
        _commissioningRecipe.ApplyAxisSnapshots(snapshot.Axes);
        _isApplyingProjection = true;
        try
        {
            _simulationWorkspace.EnsureScenarioTarget(CurrentProjection.ScenarioTargetIds);
            _simulationWorkspace.UpdateFinalEquipmentTargets(
                CurrentProjection.FinalEquipmentTargetIds);
            _simulationWorkspace.EnsureScheduledFaultTarget(
                CurrentProjection.ScheduledFaultTargetIds);
            _simulationWorkspace.EnsureRecoverySequence(CurrentProjection.RecoverySequenceIds);
        }
        finally
        {
            _isApplyingProjection = false;
        }

        _digitalIo.ApplySnapshot(snapshot);
        _faultManager.ApplySnapshot(snapshot);
        _refreshManualProjection(snapshot);
        _refreshCameraProjection();
        _runtimeDebugger.ApplySnapshot(snapshot);
        if (_visionExecutionEvidence.IsCapturing)
        {
            _visionExecutionEvidence.TryComplete(snapshot);
        }
    }

    internal void UpdateSelectedAxis(
        SimulationSnapshot snapshot,
        SimulationRuntimeProjectionSelection selection)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(selection);

        CurrentProjection = CurrentProjection with
        {
            CurrentAxis = SimulationRuntimeSnapshotProjection.SelectAxis(snapshot, selection)
        };
    }
}
