using OpenVisionLab.Machine.Core.Devices;
using OpenVisionLab.Machine.Core.Layouts;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Core.Sequences;

namespace OpenVisionLab.Machine.Sequence.Authoring;

public enum SequenceTaskKind { Step, Position, PositionAndCapture, Actuate, Transport }

public sealed record SequenceTaskGroup(SequenceTaskKind Kind, IReadOnlyList<int> StepIndices);

public static class SequenceTaskOverview
{
    public static IReadOnlyList<SequenceTaskGroup> Build(MachineProjectDocument project, SequenceDefinition sequence)
    {
        var steps = sequence.Steps;
        var incoming = new Dictionary<string, int>(StringComparer.Ordinal);
        bool uniqueIds = steps.All(step => !string.IsNullOrWhiteSpace(step.Id)) && steps.Select(step => step.Id).Distinct(StringComparer.Ordinal).Count() == steps.Count;
        foreach (var step in steps)
        {
            foreach (var target in new[] { step.Action == SequenceStepAction.Complete ? null : Next(step), step.ErrorStepId, step.FailureStepId })
                if (!string.IsNullOrWhiteSpace(target)) incoming[target.Trim()] = incoming.GetValueOrDefault(target.Trim()) + 1;
        }
        var layout = project.Layouts.FirstOrDefault(item => item.Id == project.Simulation.ActiveLayoutId)
            ?? (project.Layouts.Count == 1 ? project.Layouts[0] : null);
        var groups = new List<SequenceTaskGroup>();
        for (int index = 0; index < steps.Count; index++)
        {
            var first = steps[index];
            int count = 1;
            var kind = SequenceTaskKind.Step;
            if (Joins(index, index + 1))
            {
                var second = steps[index + 1];
                if (first.Action == SequenceStepAction.MoveAxis && project.Axes.Any(axis => axis.Id == first.TargetId)
                    && second.Action == SequenceStepAction.WaitAxisDone && second.TargetId == first.TargetId)
                {
                    count = 2;
                    kind = SequenceTaskKind.Position;
                    if (Joins(index + 1, index + 2) && steps[index + 2] is { Action: SequenceStepAction.TriggerCamera } shot
                        && project.Devices.Any(device => device.Id == shot.TargetId && device.Kind == DeviceKind.Camera && device.Camera?.Geometry?.AxisId == first.TargetId))
                    {
                        count = 3;
                        kind = SequenceTaskKind.PositionAndCapture;
                    }
                }
                else if (first.Action == SequenceStepAction.SetSignal && Signal(first.Parameter) is { } command
                    && second.Action == SequenceStepAction.WaitSignal && Signal(second.Parameter) == true)
                {
                    bool cylinder = project.Devices.Any(device => device.Kind == DeviceKind.Cylinder && device.Cylinder is { } value
                        && value.ExtendCommandChannelId == first.TargetId
                        && (command ? value.ExtendedSensorChannelId : value.RetractedSensorChannelId) == second.TargetId);
                    if (cylinder) { count = 2; kind = SequenceTaskKind.Actuate; }
                    else if (command && Joins(index + 1, index + 2) && steps[index + 2] is { Action: SequenceStepAction.SetSignal } stop
                        && stop.TargetId == first.TargetId && Signal(stop.Parameter) == false && !string.IsNullOrWhiteSpace(sequence.UnitId))
                    {
                        var conveyor = project.Devices.FirstOrDefault(device => device.Kind == DeviceKind.Conveyor && device.Conveyor?.RunCommandChannelId == first.TargetId);
                        var sensor = project.Devices.FirstOrDefault(device => device.Kind == DeviceKind.Sensor && device.Sensor?.OutputChannelId == second.TargetId);
                        if (conveyor is not null && sensor is not null && InUnit(conveyor.Id) && InUnit(sensor.Id))
                        {
                            count = 3;
                            kind = SequenceTaskKind.Transport;
                        }
                    }
                }
            }
            groups.Add(new SequenceTaskGroup(kind, Enumerable.Range(index, count).ToArray()));
            index += count - 1;
        }
        return groups;

        bool InUnit(string deviceId) => layout?.Components.Any(component => component.BehaviorBindingId == deviceId && component.UnitId == sequence.UnitId) == true;
        bool Joins(int from, int to) => uniqueIds && to < steps.Count && steps[from].Action != SequenceStepAction.Complete
            && steps[from].Action != SequenceStepAction.BranchInspection && string.IsNullOrWhiteSpace(steps[from].ErrorStepId)
            && string.IsNullOrWhiteSpace(steps[from].FailureStepId) && string.IsNullOrWhiteSpace(steps[to].ErrorStepId)
            && string.IsNullOrWhiteSpace(steps[to].FailureStepId) && Next(steps[from]) == steps[to].Id && incoming.GetValueOrDefault(steps[to].Id) == 1;
        string? Next(SequenceStepDefinition step)
        {
            if (!string.IsNullOrWhiteSpace(step.NextStepId)) return step.NextStepId.Trim();
            int index = steps.IndexOf(step);
            return index + 1 < steps.Count ? steps[index + 1].Id : null;
        }
    }

    private static bool? Signal(string value) => bool.TryParse(value.Trim(), out bool parsed) ? parsed : value.Trim() switch { "1" => true, "0" => false, _ => null };
}
