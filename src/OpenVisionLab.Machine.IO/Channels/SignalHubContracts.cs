using OpenVisionLab.Machine.Core.Channels;

namespace OpenVisionLab.Machine.IO.Channels;

/// <summary>
/// Identifies the component requesting a discrete signal write.
/// </summary>
public enum SignalWriteOwner
{
    Manual,
    EmbeddedSequence,
    SimulationComponent
}

/// <summary>
/// Provides stable failure reasons for signal-hub configuration and access.
/// </summary>
public enum SignalHubErrorCode
{
    None,
    DefinitionsRequired,
    DefinitionRequired,
    ChannelIdRequired,
    DuplicateChannelId,
    UnsupportedChannelKind,
    InvalidInitialValue,
    ChannelNotFound,
    ChannelKindMismatch,
    WriteOwnerNotAllowed
}

/// <summary>
/// Describes the outcome of constructing a deterministic signal hub.
/// </summary>
public sealed class SignalHubCreationResult
{
    private SignalHubCreationResult(
        bool isAccepted,
        DeterministicSignalHub? hub,
        SignalHubErrorCode errorCode,
        string? channelId)
    {
        IsAccepted = isAccepted;
        Hub = hub;
        ErrorCode = errorCode;
        ChannelId = channelId;
    }

    public bool IsAccepted { get; }
    public DeterministicSignalHub? Hub { get; }
    public SignalHubErrorCode ErrorCode { get; }
    public string? ChannelId { get; }

    internal static SignalHubCreationResult Accepted(DeterministicSignalHub hub) =>
        new(true, hub, SignalHubErrorCode.None, null);

    internal static SignalHubCreationResult Rejected(SignalHubErrorCode errorCode, string? channelId = null) =>
        new(false, null, errorCode, channelId);
}

/// <summary>
/// Describes one deterministic digital-signal read.
/// </summary>
public sealed class SignalReadResult
{
    private SignalReadResult(
        bool isAccepted,
        SignalHubErrorCode errorCode,
        string? channelId,
        ChannelKind? kind,
        bool? value,
        long revision)
    {
        IsAccepted = isAccepted;
        ErrorCode = errorCode;
        ChannelId = channelId;
        Kind = kind;
        Value = value;
        Revision = revision;
    }

    public bool IsAccepted { get; }
    public SignalHubErrorCode ErrorCode { get; }
    public string? ChannelId { get; }
    public ChannelKind? Kind { get; }
    public bool? Value { get; }
    public long Revision { get; }

    internal static SignalReadResult Accepted(
        string channelId,
        ChannelKind kind,
        bool value,
        long revision) =>
        new(true, SignalHubErrorCode.None, channelId, kind, value, revision);

    internal static SignalReadResult Rejected(
        SignalHubErrorCode errorCode,
        string? channelId,
        long revision,
        ChannelKind? kind = null) =>
        new(false, errorCode, channelId, kind, null, revision);
}

/// <summary>
/// Describes one accepted or rejected discrete-signal write.
/// </summary>
public sealed class SignalWriteResult
{
    private SignalWriteResult(
        bool isAccepted,
        SignalHubErrorCode errorCode,
        string? channelId,
        ChannelKind? kind,
        SignalWriteOwner owner,
        bool requestedValue,
        bool? previousValue,
        bool? currentValue,
        bool stateChanged,
        long revision)
    {
        IsAccepted = isAccepted;
        ErrorCode = errorCode;
        ChannelId = channelId;
        Kind = kind;
        Owner = owner;
        RequestedValue = requestedValue;
        PreviousValue = previousValue;
        CurrentValue = currentValue;
        StateChanged = stateChanged;
        Revision = revision;
    }

    public bool IsAccepted { get; }
    public SignalHubErrorCode ErrorCode { get; }
    public string? ChannelId { get; }
    public ChannelKind? Kind { get; }
    public SignalWriteOwner Owner { get; }
    public bool RequestedValue { get; }
    public bool? PreviousValue { get; }
    public bool? CurrentValue { get; }
    public bool StateChanged { get; }
    public long Revision { get; }

    internal static SignalWriteResult Accepted(
        string channelId,
        ChannelKind kind,
        SignalWriteOwner owner,
        bool requestedValue,
        bool previousValue,
        bool currentValue,
        bool stateChanged,
        long revision) =>
        new(
            true,
            SignalHubErrorCode.None,
            channelId,
            kind,
            owner,
            requestedValue,
            previousValue,
            currentValue,
            stateChanged,
            revision);

    internal static SignalWriteResult Rejected(
        SignalHubErrorCode errorCode,
        string? channelId,
        ChannelKind? kind,
        SignalWriteOwner owner,
        bool requestedValue,
        bool? currentValue,
        long revision) =>
        new(
            false,
            errorCode,
            channelId,
            kind,
            owner,
            requestedValue,
            currentValue,
            currentValue,
            false,
            revision);
}

/// <summary>
/// Describes an atomic restoration of all signals to their authored initial values.
/// </summary>
public sealed class SignalHubResetResult
{
    internal SignalHubResetResult(
        int changedSignalCount,
        long previousRevision,
        long revision)
    {
        ChangedSignalCount = changedSignalCount;
        PreviousRevision = previousRevision;
        Revision = revision;
    }

    public bool IsAccepted => true;
    public SignalHubErrorCode ErrorCode => SignalHubErrorCode.None;
    public int ChangedSignalCount { get; }
    public bool StateChanged => ChangedSignalCount > 0;
    public long PreviousRevision { get; }
    public long Revision { get; }
}

/// <summary>
/// Describes activation or removal of a deterministic digital-input override.
/// The override changes the effective value while retaining the latest nominal
/// component/manual write for recovery.
/// </summary>
public sealed class DigitalInputOverrideResult
{
    internal DigitalInputOverrideResult(
        bool isAccepted,
        SignalHubErrorCode errorCode,
        string? channelId,
        bool? previousOverride,
        bool? currentOverride,
        bool? previousValue,
        bool? currentValue,
        bool overrideChanged,
        bool valueChanged,
        long revision)
    {
        IsAccepted = isAccepted;
        ErrorCode = errorCode;
        ChannelId = channelId;
        PreviousOverride = previousOverride;
        CurrentOverride = currentOverride;
        PreviousValue = previousValue;
        CurrentValue = currentValue;
        OverrideChanged = overrideChanged;
        ValueChanged = valueChanged;
        Revision = revision;
    }

    public bool IsAccepted { get; }
    public SignalHubErrorCode ErrorCode { get; }
    public string? ChannelId { get; }
    public bool? PreviousOverride { get; }
    public bool? CurrentOverride { get; }
    public bool? PreviousValue { get; }
    public bool? CurrentValue { get; }
    public bool OverrideChanged { get; }
    public bool ValueChanged { get; }
    public long Revision { get; }
}
