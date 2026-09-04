using OpenVisionLab.Machine.Core.Channels;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Sequence.Compilation;
using OpenVisionLab.Machine.Simulation.Engine;

namespace OpenVisionLab.Machine.Simulation.Compilation;

internal sealed class MachineProjectRuntimeAutomaticRunCompiler
{
    private readonly FixedStepDelayConverter _delayConverter;

    internal MachineProjectRuntimeAutomaticRunCompiler(FixedStepDelayConverter delayConverter)
    {
        ArgumentNullException.ThrowIfNull(delayConverter);
        _delayConverter = delayConverter;
    }

    internal AutomaticRunConfiguration? Compile(
        AutomaticRunDefinition? definition,
        IReadOnlyList<CompiledSequence> sequences,
        IReadOnlyDictionary<string, ChannelKind>? channelKinds,
        ICollection<MachineProjectRuntimeCompilationError> errors)
    {
        if (definition is null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(definition.SequenceId) ||
            !string.Equals(definition.SequenceId, definition.SequenceId.Trim(), StringComparison.Ordinal))
        {
            errors.Add(Error(
                MachineProjectRuntimeCompilationErrorCode.AutomaticSequenceIdRequired,
                "simulation.automaticRun.sequenceId",
                "Automatic run requires a non-blank sequence id without surrounding whitespace."));
        }
        else if (!sequences.Any(sequence =>
                     string.Equals(sequence.Id, definition.SequenceId, StringComparison.Ordinal)))
        {
            errors.Add(Error(
                MachineProjectRuntimeCompilationErrorCode.AutomaticSequenceNotFound,
                definition.SequenceId,
                $"Automatic sequence '{definition.SequenceId}' is not configured."));
        }

        if (definition.StartInputId is not null)
        {
            if (string.IsNullOrWhiteSpace(definition.StartInputId) ||
                !string.Equals(
                    definition.StartInputId,
                    definition.StartInputId.Trim(),
                    StringComparison.Ordinal))
            {
                errors.Add(Error(
                    MachineProjectRuntimeCompilationErrorCode.AutomaticStartInputInvalid,
                    "simulation.automaticRun.startInputId",
                    "Automatic start input id cannot be blank or contain surrounding whitespace."));
            }
            else if (channelKinds is null ||
                     !channelKinds.TryGetValue(definition.StartInputId, out ChannelKind kind))
            {
                errors.Add(Error(
                    MachineProjectRuntimeCompilationErrorCode.AutomaticStartInputNotFound,
                    definition.StartInputId,
                    $"Automatic start input '{definition.StartInputId}' is not configured."));
            }
            else if (kind != ChannelKind.DigitalInput)
            {
                errors.Add(Error(
                    MachineProjectRuntimeCompilationErrorCode.AutomaticStartInputKindInvalid,
                    definition.StartInputId,
                    $"Automatic start input '{definition.StartInputId}' must be a DigitalInput."));
            }
        }

        bool repeatDelayValid = definition.RepeatDelayMilliseconds >= 0 &&
            (definition.Repeat || definition.RepeatDelayMilliseconds == 0) &&
            _delayConverter.TryConvertDelayToTicks(
                definition.RepeatDelayMilliseconds,
                allowZero: true,
                out _);
        if (!repeatDelayValid)
        {
            errors.Add(Error(
                MachineProjectRuntimeCompilationErrorCode.AutomaticRepeatDelayInvalid,
                "simulation.automaticRun.repeatDelayMilliseconds",
                "Automatic repeat delay must be non-negative, zero when repeat is disabled, and an exact fixed-step multiple."));
        }

        if (errors.Any(error => error.Code is
                MachineProjectRuntimeCompilationErrorCode.AutomaticSequenceIdRequired or
                MachineProjectRuntimeCompilationErrorCode.AutomaticSequenceNotFound or
                MachineProjectRuntimeCompilationErrorCode.AutomaticStartInputInvalid or
                MachineProjectRuntimeCompilationErrorCode.AutomaticStartInputNotFound or
                MachineProjectRuntimeCompilationErrorCode.AutomaticStartInputKindInvalid or
                MachineProjectRuntimeCompilationErrorCode.AutomaticRepeatDelayInvalid))
        {
            return null;
        }

        return new AutomaticRunConfiguration(
            definition.SequenceId,
            definition.StartInputId,
            definition.StartInputValue,
            definition.Repeat,
            definition.RepeatDelayMilliseconds);
    }

    private static MachineProjectRuntimeCompilationError Error(
        MachineProjectRuntimeCompilationErrorCode code,
        string? targetId,
        string message) =>
        new(code, targetId, message);
}
