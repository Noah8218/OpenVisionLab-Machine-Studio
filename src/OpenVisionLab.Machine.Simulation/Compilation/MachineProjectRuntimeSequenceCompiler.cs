using OpenVisionLab.Machine.Core.Axes;
using OpenVisionLab.Machine.Core.Channels;
using OpenVisionLab.Machine.Core.Sequences;
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
        ICollection<MachineProjectRuntimeCompilationError> errors)
    {
        var definitionList = definitions.ToArray();
        var targets = new SequenceCompilationTargets(
            channelKinds,
            axes.Select(axis => axis.Id),
            cameras.Select(camera => camera.Id),
            definitionList
                .Where(definition => definition is not null)
                .Select(definition => definition.Id));
        var compiler = new SequenceCompiler();
        var compiled = new List<CompiledSequence>();
        var ids = new HashSet<string>(StringComparer.Ordinal);

        foreach (SequenceDefinition definition in definitionList)
        {
            if (!string.IsNullOrWhiteSpace(definition.Id) && !ids.Add(definition.Id))
            {
                errors.Add(Error(
                    MachineProjectRuntimeCompilationErrorCode.DuplicateSequenceId,
                    definition.Id,
                    $"Sequence id '{definition.Id}' is duplicated."));
                continue;
            }

            SequenceCompilationResult result = compiler.Compile(definition, targets);
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

        foreach (SequenceCompositionError error in SequenceCompiler.ValidateComposition(compiled))
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
