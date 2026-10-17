using OpenVisionLab.Machine.Core.Axes;
using OpenVisionLab.Machine.Core.Channels;
using OpenVisionLab.Machine.Core.Sequences;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Sequence.Compilation;
using OpenVisionLab.Machine.Simulation.Axis;
using OpenVisionLab.Machine.Simulation.Camera;

namespace OpenVisionLab.Machine.Simulation.Compilation;

internal sealed class MachineProjectRuntimeSequenceCompiler
{
    internal IReadOnlyList<CompiledSequence> Compile(
        IEnumerable<SequenceDefinition> definitions,
        IReadOnlyDictionary<string, ChannelKind> channelKinds,
        IEnumerable<AxisConfiguration> axes,
        IEnumerable<VirtualCameraConfiguration> cameras,
        IEnumerable<string> workpieceComponentIds,
        ICollection<MachineProjectRuntimeCompilationError> errors,
        IReadOnlyList<MachineUnitDefinition>? units = null,
        IEnumerable<string>? conveyorIds = null,
        int? totalFeedCount = null)
    {
        var definitionList = definitions.ToArray();
        var targets = new SequenceCompilationTargets(
            channelKinds,
            axes.Select(axis => axis.Id),
            cameras.Select(camera => camera.Id),
            definitionList
                .Where(definition => definition is not null)
                .Select(definition => definition.Id),
            workpieceComponentIds, units?.Select(unit => unit.Id), conveyorIds);
        var compiler = new SequenceCompiler();
        var compiled = new List<CompiledSequence>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var assignedUnits = new HashSet<string>(StringComparer.Ordinal);

        foreach (SequenceDefinition definition in definitionList)
        {
            if (definition is null)
            {
                errors.Add(Error(MachineProjectRuntimeCompilationErrorCode.SequenceCompilationFailed, null, "Sequence definition is required."));
                continue;
            }
            if (definition.UnitId is not null && !assignedUnits.Add(definition.UnitId))
            {
                errors.Add(Error(MachineProjectRuntimeCompilationErrorCode.SequenceCompilationFailed, definition.Id, "A unit can own only one sequence."));
                continue;
            }
            if (!string.IsNullOrWhiteSpace(definition.Id) && !ids.Add(definition.Id))
            {
                errors.Add(Error(
                    MachineProjectRuntimeCompilationErrorCode.DuplicateSequenceId,
                    definition.Id,
                    $"Sequence id '{definition.Id}' is duplicated."));
                continue;
            }

            var usesSupply = totalFeedCount is not null && definition.UnitId is not null
                && definition.Steps.Any(step => step.Action is SequenceStepAction.FeedUnitWorkpiece or SequenceStepAction.ReceiveUnitWorkpiece);
            SequenceCompilationResult result = compiler.Compile(definition, targets,
                usesSupply ? totalFeedCount!.Value : units?.FirstOrDefault(unit => unit.Id == definition.UnitId)?.RepeatCount ?? 1);
            if (!result.IsSuccess)
            {
                foreach (SequenceCompilationError error in result.Errors)
                {
                    errors.Add(Error(
                        MachineProjectRuntimeCompilationErrorCode.SequenceCompilationFailed,
                        error.StepId ?? definition.Id,
                        $"{error.Code}: {error.Message}"));
                }
                continue;
            }

            compiled.Add(result.Sequence!);
        }

        foreach (SequenceCompositionError error in SequenceCompositionValidator.Validate(compiled))
        {
            errors.Add(Error(
                MachineProjectRuntimeCompilationErrorCode.SubsequenceCompositionInvalid,
                error.SequenceId,
                $"{error.Code}: {error.Message}"));
        }

        return compiled;
    }

    private static MachineProjectRuntimeCompilationError Error(
        MachineProjectRuntimeCompilationErrorCode code,
        string? targetId,
        string message) =>
        new(code, targetId, message);
}
