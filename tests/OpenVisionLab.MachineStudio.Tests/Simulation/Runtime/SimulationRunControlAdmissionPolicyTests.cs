using OpenVisionLab.Machine.Sequence.Runtime;
using OpenVisionLab.Machine.Simulation.Engine;
using OpenVisionLab.MachineStudio.ViewModel;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

public sealed class SimulationRunControlAdmissionPolicyTests
{
    [Fact]
    public void RunAdmissionUsesRuntimeReadiness()
    {
        var unavailable = CreateState() with
        {
            HasAxes = false,
            HasEmbeddedSequence = false,
            RuntimeDefinitionDirty = true
        };
        Assert.False(SimulationRunControlAdmissionPolicy.CanRun(unavailable));
        Assert.True(SimulationRunControlAdmissionPolicy.CanRun(unavailable with { HasAxes = true }));

        var automatic = CreateState() with
        {
            ControlOwner = SimulationControlOwner.Definition,
            HasAutomaticRun = true,
            AutomaticRunConfigured = true,
            AutomaticRunActive = false,
            ActiveSequenceStatus = SequenceExecutionStatus.Ready
        };
        Assert.True(SimulationRunControlAdmissionPolicy.CanRun(automatic));
        Assert.False(SimulationRunControlAdmissionPolicy.CanRun(automatic with
        {
            ActiveSequenceStatus = SequenceExecutionStatus.Faulted
        }));
        Assert.False(SimulationRunControlAdmissionPolicy.CanRun(automatic with
        {
            AutomaticRunActive = true,
            AutomaticExternalInspectionWaiting = true
        }));
    }

    [Fact]
    public void ManualResumeUsesManualEquipmentEvenWhenAnAutomaticSequenceIsNotReady()
    {
        var state = CreateState() with
        {
            HasAxes = false,
            HasAuthoredLayout = false,
            HasVirtualCamera = true,
            HasAutomaticRun = true,
            ActiveSequenceStatus = SequenceExecutionStatus.Faulted
        };
        Assert.True(SimulationRunControlAdmissionPolicy.CanRun(state));
        Assert.False(SimulationRunControlAdmissionPolicy.CanRun(state with { IsRunning = true }));
        Assert.False(SimulationRunControlAdmissionPolicy.CanRun(state with { HasVirtualCamera = false }));
    }

    [Fact]
    public void PauseAndResetRequireAStableRunMode()
    {
        var running = CreateState() with { IsRunning = true };
        Assert.True(SimulationRunControlAdmissionPolicy.CanPause(running));
        Assert.False(SimulationRunControlAdmissionPolicy.CanPause(running with { IsRunMode = false }));
        Assert.False(SimulationRunControlAdmissionPolicy.CanPause(running with { IsValidationBusy = true }));

        var waiting = running with { IsRunning = false, AutomaticExternalInspectionWaiting = true };
        Assert.True(SimulationRunControlAdmissionPolicy.CanPause(waiting));
        Assert.False(SimulationRunControlAdmissionPolicy.CanPause(waiting with { IsRunMode = false }));
        Assert.False(SimulationRunControlAdmissionPolicy.CanPause(waiting with { IsApplyingProject = true }));
        Assert.False(SimulationRunControlAdmissionPolicy.CanPause(waiting with { IsValidationBusy = true }));
        Assert.False(SimulationRunControlAdmissionPolicy.CanPause(CreateState()));

        Assert.True(SimulationRunControlAdmissionPolicy.CanReset(CreateState()));
        Assert.False(SimulationRunControlAdmissionPolicy.CanReset(CreateState() with { RuntimeDefinitionDirty = true }));
    }

    [Fact]
    public void SequenceAdmissionsMatchTheActiveSequenceStatus()
    {
        var running = CreateState() with
        {
            ActiveSequenceStatus = SequenceExecutionStatus.Running,
            ActiveSequenceId = "automatic-transfer-cycle",
            RuntimeDefinitionDirty = false
        };
        Assert.True(SimulationRunControlAdmissionPolicy.CanAbortSequence(running));
        Assert.False(SimulationRunControlAdmissionPolicy.CanRetrySequence(running));

        var faulted = running with
        {
            ActiveSequenceStatus = SequenceExecutionStatus.Faulted,
            HasActiveFaults = false
        };
        Assert.False(SimulationRunControlAdmissionPolicy.CanAbortSequence(faulted));
        Assert.True(SimulationRunControlAdmissionPolicy.CanRetrySequence(faulted));
        Assert.False(SimulationRunControlAdmissionPolicy.CanRetrySequence(faulted with { HasActiveFaults = true }));
    }

    [Fact]
    public void StepAdmissionFollowsTheControlOwner()
    {
        var manual = CreateState() with
        {
            HasAuthoredLayout = false,
            HasAxes = false,
            HasVirtualCamera = false
        };
        Assert.False(SimulationRunControlAdmissionPolicy.CanStep(manual));
        Assert.True(SimulationRunControlAdmissionPolicy.CanStep(manual with { HasVirtualCamera = true }));

        var automatic = manual with
        {
            ControlOwner = SimulationControlOwner.EmbeddedSequence,
            HasAutomaticRun = true,
            AutomaticRunActive = false,
            HasEmbeddedSequence = true,
            ActiveSequenceStatus = SequenceExecutionStatus.Ready
        };
        Assert.False(SimulationRunControlAdmissionPolicy.CanStep(automatic));
        Assert.True(SimulationRunControlAdmissionPolicy.CanStep(automatic with { AutomaticRunActive = true }));
        Assert.False(SimulationRunControlAdmissionPolicy.CanStep(automatic with
        {
            AutomaticRunActive = true,
            AutomaticExternalInspectionWaiting = true
        }));
    }

    [Fact]
    public void CycleStartRequiresAnActiveSequenceAndInput()
    {
        var state = CreateState() with
        {
            HasEmbeddedSequence = true,
            HasCycleStartInput = true,
            ActiveSequenceStatus = SequenceExecutionStatus.Running
        };
        Assert.True(SimulationRunControlAdmissionPolicy.CanCycleStart(state));
        Assert.False(SimulationRunControlAdmissionPolicy.CanCycleStart(state with { CycleStartActive = true }));
        Assert.False(SimulationRunControlAdmissionPolicy.CanCycleStart(state with
        {
            ActiveSequenceStatus = SequenceExecutionStatus.Ready
        }));
    }

    private static SimulationRunControlState CreateState() => new(
        IsApplyingProject: false,
        IsValidationBusy: false,
        IsRunMode: true,
        IsRunning: false,
        RuntimeDefinitionDirty: false,
        HasAutomaticRun: false,
        AutomaticRunConfigured: false,
        AutomaticRunActive: false,
        HasEmbeddedSequence: false,
        HasAxes: true,
        HasAuthoredLayout: true,
        HasVirtualCamera: false,
        HasCycleStartInput: false,
        CycleStartActive: false,
        HasActiveFaults: false,
        ControlOwner: SimulationControlOwner.Manual,
        ActiveSequenceStatus: null,
        ActiveSequenceId: null);
}
