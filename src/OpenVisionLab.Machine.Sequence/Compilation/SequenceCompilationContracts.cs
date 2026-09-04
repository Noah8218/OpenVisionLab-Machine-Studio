using OpenVisionLab.Machine.Core.Channels;

namespace OpenVisionLab.Machine.Sequence.Compilation;

public enum SequenceCompilationErrorCode
{
    DefinitionRequired,
    SequenceIdRequired,
    NoSteps,
    StepIdRequired,
    DuplicateStepId,
    UnsupportedAction,
    TargetIdRequired,
    UnexpectedTargetId,
    InvalidBooleanParameter,
    InvalidNumericParameter,
    UnexpectedParameter,
    InvalidTimeout,
    InvalidWatchdogTimeout,
    UnknownSubsequence,
    SubsequenceCycle,
    NextStepNotFound,
    ErrorStepNotFound,
    MissingSuccessor,
    CompleteStepHasTransition,
    UnknownSignal,
    InvalidSignalKind,
    UnknownAxis,
    UnknownCamera,
    RecipeIdRequired,
    FailureStepRequired,
    FailureStepNotFound,
    FailureStepNotAllowed,
    ExpectedTargetIdRequired,
    ExpectedStateRequired
}

public sealed record SequenceCompilationError(
    SequenceCompilationErrorCode Code,
    string? StepId,
    string Message);

public sealed record SequenceCompositionError(
    SequenceCompilationErrorCode Code,
    string SequenceId,
    string? StepId,
    string Message);

public sealed class SequenceCompilationResult
{
    internal SequenceCompilationResult(
        CompiledSequence? sequence,
        IReadOnlyList<SequenceCompilationError> errors)
    {
        Sequence = sequence;
        Errors = errors;
    }

    public bool IsSuccess => Sequence is not null && Errors.Count == 0;

    public CompiledSequence? Sequence { get; }

    public IReadOnlyList<SequenceCompilationError> Errors { get; }
}

public sealed class SequenceCompilationTargets
{
    private readonly IReadOnlyDictionary<string, ChannelKind> _channels;
    private readonly HashSet<string> _axisIds;
    private readonly HashSet<string> _cameraIds;
    private readonly HashSet<string> _sequenceIds;

    public SequenceCompilationTargets(
        IReadOnlyDictionary<string, ChannelKind> channels,
        IEnumerable<string> axisIds)
        : this(channels, axisIds, Array.Empty<string>(), Array.Empty<string>())
    {
    }

    public SequenceCompilationTargets(
        IReadOnlyDictionary<string, ChannelKind> channels,
        IEnumerable<string> axisIds,
        IEnumerable<string> cameraIds)
        : this(channels, axisIds, cameraIds, Array.Empty<string>())
    {
    }

    public SequenceCompilationTargets(
        IReadOnlyDictionary<string, ChannelKind> channels,
        IEnumerable<string> axisIds,
        IEnumerable<string> cameraIds,
        IEnumerable<string> sequenceIds)
    {
        ArgumentNullException.ThrowIfNull(channels);
        ArgumentNullException.ThrowIfNull(axisIds);
        ArgumentNullException.ThrowIfNull(cameraIds);
        ArgumentNullException.ThrowIfNull(sequenceIds);

        _channels = new Dictionary<string, ChannelKind>(channels, StringComparer.Ordinal);
        _axisIds = new HashSet<string>(axisIds, StringComparer.Ordinal);
        _cameraIds = new HashSet<string>(cameraIds, StringComparer.Ordinal);
        _sequenceIds = new HashSet<string>(sequenceIds, StringComparer.Ordinal);
    }

    internal bool TryGetChannelKind(string id, out ChannelKind kind)
    {
        return _channels.TryGetValue(id, out kind);
    }

    internal bool ContainsAxis(string id)
    {
        return _axisIds.Contains(id);
    }

    internal bool ContainsCamera(string id)
    {
        return _cameraIds.Contains(id);
    }

    internal bool ContainsSequence(string id)
    {
        return _sequenceIds.Contains(id);
    }
}
