using System.Globalization;
using OpenVisionLab;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Core.Sequences;
using OpenVisionLab.Machine.Sequence.Runtime;
using OpenVisionLab.Machine.Simulation.Engine;
using OpenVisionLab.Machine.Simulation.Snapshots;

namespace OpenVisionLab.MachineStudio.ViewModel;

public sealed record SimulationSequenceObservation(
    string ProjectId, long RuntimeGeneration, string RootSequenceId, string SequenceId, string? StepId,
    string? UnitId, string ProcessText, string StepText, string StateText, string DetailText,
    string ValuesText, string TimingText, bool IsFaulted, bool CanOpenStep)
{
    public bool Matches(SimulationSnapshot snapshot)
    {
        if (snapshot.ProjectId != ProjectId || snapshot.RuntimeGeneration != RuntimeGeneration) return false;
        var execution = snapshot.Sequences.FirstOrDefault(item => item.SequenceId == RootSequenceId);
        return execution is not null && SequenceId == DefinitionSequenceId(execution) && StepId == ObservedStepId(execution);
    }

    internal static IReadOnlyList<SimulationSequenceObservation> Create(MachineProjectDocument? project, SimulationSnapshot? snapshot)
    {
        if (project is null || snapshot?.ProjectId != project.Id) return Array.Empty<SimulationSequenceObservation>();
        return Array.AsReadOnly(snapshot.Sequences.Select(execution => Create(project, snapshot, execution)).ToArray());
    }

    private static SimulationSequenceObservation Create(MachineProjectDocument project, SimulationSnapshot snapshot, SequenceExecutionSnapshot execution)
    {
        string sequenceId = DefinitionSequenceId(execution);
        string? stepId = ObservedStepId(execution);
        var root = project.Sequences.FirstOrDefault(item => item.Id == execution.SequenceId);
        var definition = project.Sequences.FirstOrDefault(item => item.Id == sequenceId);
        var step = definition?.Steps.FirstOrDefault(item => item.Id == stepId);
        string? unitId = execution.UnitId ?? root?.UnitId;
        var unit = project.Stations.SelectMany(item => item.Units).FirstOrDefault(item => item.Id == unitId);
        string process = Name(root?.Name, execution.SequenceId);
        if (unit is not null) process = $"{Name(unit.Name, unit.Id)} · {process}";
        string stepText = step is null ? stepId ?? T("SimObservation.NoStep")
            : string.IsNullOrWhiteSpace(step.Name) ? step.Id : $"{step.Name} · {step.Id}";
        if (sequenceId != execution.SequenceId) stepText = $"{Name(definition?.Name, sequenceId)} / {stepText}";
        string state = T($"Equipment.State.{execution.Status}");
        if (execution.Status == SequenceExecutionStatus.Running && snapshot.RunMode == SimulationRunMode.Paused)
            state = T("Shell.Paused");
        string detail = execution.Status == SequenceExecutionStatus.Faulted && execution.LastError is { } error
            ? $"{error.Code} · {error.Message}"
            : execution.Status == SequenceExecutionStatus.Running && step is not null
                ? T($"Sequence.Action.{step.Action}") : string.Empty;
        string limit = step?.TimeoutMs > 0 ? $"{Number(step.TimeoutMs / 1000d)} s" : T("SimObservation.NoLimit");
        string timing = string.Format(CultureInfo.CurrentCulture, T("SimObservation.Timing"), Number(execution.ElapsedInStep.TotalSeconds), limit);
        if (execution.WatchdogTimeout > TimeSpan.Zero)
            timing += " · " + string.Format(CultureInfo.CurrentCulture, T("SimObservation.Watchdog"), Number(execution.TotalElapsed.TotalSeconds), Number(execution.WatchdogTimeout.TotalSeconds));
        return new(project.Id, snapshot.RuntimeGeneration, execution.SequenceId, sequenceId, stepId, unit?.Id,
            process, stepText, state, detail, Values(project, snapshot, step, execution), timing,
            execution.Status == SequenceExecutionStatus.Faulted, root is not null && step is not null);
    }

    private static string Values(MachineProjectDocument project, SimulationSnapshot snapshot, SequenceStepDefinition? step, SequenceExecutionSnapshot execution)
    {
        if (step is null) return T("SimObservation.NoValues");
        if (step.Action == SequenceStepAction.Delay)
            return string.Format(CultureInfo.CurrentCulture, T("SimObservation.Delay"), Number(execution.ElapsedInStep.TotalSeconds), step.Parameter);

        string target = step.TargetId;
        string actual = "—";
        string expected = step.Parameter;
        bool axisWait = step.Action == SequenceStepAction.WaitAxisDone || step.Action == SequenceStepAction.Wait
            && (string.IsNullOrWhiteSpace(step.Parameter) || string.Equals(step.Parameter.Trim(), "axisDone", StringComparison.OrdinalIgnoreCase));
        if (step.Action == SequenceStepAction.MoveAxis || axisWait)
        {
            var axis = snapshot.Axes.FirstOrDefault(item => item.Id == step.TargetId);
            var definition = project.Axes.FirstOrDefault(item => item.Id == step.TargetId);
            target = Name(axis?.Name ?? definition?.Name, target);
            string unit = definition?.Unit ?? string.Empty;
            if (axis is not null)
            {
                actual = $"{Number(axis.Position)} {unit} · {T($"Equipment.State.{axis.State}")}";
                if (axisWait) expected = Number(axis.CommandPosition);
            }
            if (!string.IsNullOrWhiteSpace(expected)) expected += $" {unit}";
        }
        else if (step.Action is SequenceStepAction.WaitSignal or SequenceStepAction.SetSignal or SequenceStepAction.SetChannel or SequenceStepAction.Wait)
        {
            var signal = snapshot.Signals.FirstOrDefault(item => item.Id == step.TargetId);
            target = Name(signal?.Name, target);
            if (signal is not null) actual = signal.Value ? "ON" : "OFF";
            if (bool.TryParse(step.Parameter.Trim(), out bool value)) expected = value ? "ON" : "OFF";
            else if (step.Parameter.Trim() is "1" or "0") expected = step.Parameter.Trim() == "1" ? "ON" : "OFF";
        }
        else if (step.Action is SequenceStepAction.TriggerCamera or SequenceStepAction.WaitVisionResult)
        {
            var camera = snapshot.Cameras.FirstOrDefault(item => item.Id == step.TargetId);
            target = Name(camera?.Name, target);
            if (camera is not null) actual = T($"Equipment.State.{camera.State}");
            if (step.Action == SequenceStepAction.WaitVisionResult) expected = T("Equipment.State.FrameReady");
        }
        else return string.IsNullOrWhiteSpace(target) ? T("SimObservation.NoValues") : target;

        return string.Format(CultureInfo.CurrentCulture, T("SimObservation.Values"), target, actual, string.IsNullOrWhiteSpace(expected) ? "—" : expected);
    }

    private static string DefinitionSequenceId(SequenceExecutionSnapshot execution) =>
        execution.Status == SequenceExecutionStatus.Faulted && execution.LastError is { } error ? error.SequenceId : execution.ActiveSequenceId ?? execution.SequenceId;

    private static string? ObservedStepId(SequenceExecutionSnapshot execution) =>
        execution.Status == SequenceExecutionStatus.Ready ? null
            : execution.Status == SequenceExecutionStatus.Faulted && execution.LastError is { } error ? error.StepId : execution.CurrentStepId;

    private static string Number(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
    private static string Name(string? name, string id) => string.IsNullOrWhiteSpace(name) ? id : name;
    private static string T(string key) => OpenVisionLanguageService.T(key);
}
