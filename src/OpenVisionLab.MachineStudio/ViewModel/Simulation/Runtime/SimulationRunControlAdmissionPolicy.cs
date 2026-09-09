using OpenVisionLab.Machine.Sequence.Runtime;
using OpenVisionLab.Machine.Simulation.Engine;

namespace OpenVisionLab.MachineStudio.ViewModel;

internal readonly record struct SimulationRunControlState(
    bool IsApplyingProject,
    bool IsValidationBusy,
    bool IsRunMode,
    bool IsRunning,
    bool RuntimeDefinitionDirty,
    bool HasAutomaticRun,
    bool AutomaticRunConfigured,
    bool AutomaticRunActive,
    bool HasEmbeddedSequence,
    bool HasAxes,
    bool HasAuthoredLayout,
    bool HasVirtualCamera,
    bool HasCycleStartInput,
    bool CycleStartActive,
    bool HasActiveFaults,
    SimulationControlOwner ControlOwner,
    SequenceExecutionStatus? ActiveSequenceStatus,
    string? ActiveSequenceId);

/// <summary>
/// Owns shell-facing run-control admission decisions over an immutable state snapshot.
/// Engine commands, callbacks, serialization, and disposal remain with the workflow.
/// </summary>
internal static class SimulationRunControlAdmissionPolicy
{
    internal static bool CanRun(SimulationRunControlState state)
    {
        if (state.IsApplyingProject || state.IsValidationBusy || state.IsRunning)
        {
            return false;
        }

        if (state.RuntimeDefinitionDirty)
        {
            return state.HasAxes || state.HasEmbeddedSequence;
        }

        if (state.HasAutomaticRun)
        {
            return state.AutomaticRunConfigured
                && (state.AutomaticRunActive
                    || state.ActiveSequenceStatus == SequenceExecutionStatus.Ready);
        }

        if (!state.HasEmbeddedSequence)
        {
            return state.HasAxes;
        }

        return state.ActiveSequenceStatus is SequenceExecutionStatus.Ready
            or SequenceExecutionStatus.Running;
    }

    internal static bool CanPause(SimulationRunControlState state) =>
        !state.IsApplyingProject
            && !state.IsValidationBusy
            && state.IsRunMode
            && state.IsRunning;

    internal static bool CanAbortSequence(SimulationRunControlState state) =>
        state.IsRunMode
            && !state.IsApplyingProject
            && !state.IsValidationBusy
            && !state.RuntimeDefinitionDirty
            && state.ActiveSequenceStatus == SequenceExecutionStatus.Running;

    internal static bool CanRetrySequence(SimulationRunControlState state) =>
        state.IsRunMode
            && !state.IsApplyingProject
            && !state.IsValidationBusy
            && !state.RuntimeDefinitionDirty
            && !state.HasActiveFaults
            && state.ActiveSequenceStatus == SequenceExecutionStatus.Faulted;

    internal static bool CanStep(SimulationRunControlState state)
    {
        if (state.IsApplyingProject
            || state.IsValidationBusy
            || !state.IsRunMode
            || state.IsRunning
            || state.RuntimeDefinitionDirty)
        {
            return false;
        }

        if (state.ControlOwner == SimulationControlOwner.Manual)
        {
            return state.HasAuthoredLayout || state.HasAxes || state.HasVirtualCamera;
        }

        if (!state.HasEmbeddedSequence)
        {
            return state.HasAxes;
        }

        if (state.HasAutomaticRun)
        {
            return state.AutomaticRunActive;
        }

        return state.ActiveSequenceStatus is SequenceExecutionStatus.Ready
            or SequenceExecutionStatus.Running;
    }

    internal static bool CanCycleStart(SimulationRunControlState state) =>
        state.IsRunMode
            && !state.IsApplyingProject
            && !state.IsValidationBusy
            && !state.RuntimeDefinitionDirty
            && state.HasEmbeddedSequence
            && state.HasCycleStartInput
            && !state.CycleStartActive
            && state.ActiveSequenceStatus == SequenceExecutionStatus.Running;

    internal static bool CanReset(SimulationRunControlState state) =>
        !state.IsApplyingProject
            && !state.IsValidationBusy
            && state.IsRunMode
            && !state.RuntimeDefinitionDirty;
}
