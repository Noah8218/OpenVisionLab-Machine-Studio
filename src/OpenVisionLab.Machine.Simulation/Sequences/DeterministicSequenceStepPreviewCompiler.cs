using OpenVisionLab.Machine.Core.Sequences;
using OpenVisionLab.Machine.Sequence.Compilation;
using OpenVisionLab.Machine.Sequence.Runtime;
using OpenVisionLab.Machine.Simulation.Compilation;
using OpenVisionLab.Machine.Simulation.Engine;
using OpenVisionLab.Machine.Simulation.Layout;

namespace OpenVisionLab.Machine.Simulation.Sequences;

internal static class DeterministicSequenceStepPreviewCompiler
{
    internal const string PreviewSequenceId = "__connection-step-preview";
    internal const string PreviewStepId = "__preview-step";
    internal const string CompleteStepId = "__preview-complete";

    internal static SequenceCompilationResult CompilePreview(
        SequenceStepDefinition source,
        SimulationRuntimeConfiguration runtime,
        string componentId)
    {
        var steps = new List<SequenceStepDefinition>();
        LoadLockRuntimeConfiguration? loadLock = runtime.Layout?.LoadLocks.FirstOrDefault(candidate =>
            string.Equals(candidate.InnerDoorComponentId, componentId, StringComparison.Ordinal));
        if (loadLock is not null && RequiresVacuumPrerequisite(source, runtime, componentId))
        {
            steps.Add(new SequenceStepDefinition
            {
                Id = "__preview-evacuate",
                Name = "Preview load-lock pump down",
                Action = SequenceStepAction.SetSignal,
                TargetId = loadLock.EvacuateCommandChannelId,
                Parameter = "true",
                NextStepId = "__preview-wait-vacuum"
            });
            steps.Add(new SequenceStepDefinition
            {
                Id = "__preview-wait-vacuum",
                Name = "Preview wait for vacuum",
                Action = SequenceStepAction.WaitSignal,
                TargetId = loadLock.VacuumReadySensorChannelId,
                Parameter = "true",
                TimeoutMs = 10000,
                NextStepId = PreviewStepId
            });
        }

        steps.Add(new SequenceStepDefinition
        {
            Id = PreviewStepId,
            Name = source.Name,
            Action = source.Action,
            TargetId = source.TargetId,
            Parameter = source.Parameter,
            TimeoutMs = source.TimeoutMs,
            NextStepId = CompleteStepId
        });
        steps.Add(new SequenceStepDefinition
        {
            Id = CompleteStepId,
            Name = "Preview complete",
            Action = SequenceStepAction.Complete
        });

        var definition = new SequenceDefinition
        {
            Id = PreviewSequenceId,
            Name = "Connection step preview",
            Steps = steps
        };
        var targets = new SequenceCompilationTargets(
            runtime.Channels.ToDictionary(channel => channel.Id, channel => channel.Kind, StringComparer.Ordinal),
            runtime.Axes.Select(axis => axis.Id),
            runtime.Cameras.Select(camera => camera.Id));
        return new SequenceCompiler().Compile(definition, targets);
    }

    private static bool RequiresVacuumPrerequisite(
        SequenceStepDefinition source,
        SimulationRuntimeConfiguration runtime,
        string componentId)
    {
        var cylinder = runtime.Layout?.Components
            .OfType<PneumaticCylinderRuntimeConfiguration>()
            .SingleOrDefault(candidate => string.Equals(candidate.Id, componentId, StringComparison.Ordinal));
        if (cylinder is null)
        {
            return false;
        }

        return source.Action is SequenceStepAction.SetSignal or SequenceStepAction.SetChannel
            && string.Equals(source.TargetId, cylinder.ExtendCommandChannelId, StringComparison.Ordinal)
            && bool.TryParse(source.Parameter, out bool requested)
            && requested;
    }
}
