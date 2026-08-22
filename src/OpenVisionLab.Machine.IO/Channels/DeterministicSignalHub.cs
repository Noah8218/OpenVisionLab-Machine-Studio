using OpenVisionLab.Machine.Core.Channels;

namespace OpenVisionLab.Machine.IO.Channels;

/// <summary>
/// Owns deterministic digital-input and digital-output state for local simulation.
/// </summary>
/// <remarks>
/// Digital inputs accept writes from <see cref="SignalWriteOwner.Manual"/> or
/// <see cref="SignalWriteOwner.SimulationComponent"/>.
/// Digital outputs accept writes only from <see cref="SignalWriteOwner.EmbeddedSequence"/>.
/// Accepted no-op writes do not advance the state revision.
/// </remarks>
public sealed class DeterministicSignalHub
{
    private readonly object _sync = new();
    private readonly Dictionary<string, SignalState> _signalsById;
    private readonly string[] _orderedChannelIds;
    private long _revision;

    private DeterministicSignalHub(Dictionary<string, SignalState> signalsById)
    {
        _signalsById = signalsById;
        _orderedChannelIds = signalsById.Keys.OrderBy(id => id, StringComparer.Ordinal).ToArray();
    }

    /// <summary>
    /// Validates and copies authored channel definitions into a new signal hub.
    /// </summary>
    public static SignalHubCreationResult Create(IEnumerable<ChannelDefinition>? definitions)
    {
        if (definitions is null)
        {
            return SignalHubCreationResult.Rejected(SignalHubErrorCode.DefinitionsRequired);
        }

        var signalsById = new Dictionary<string, SignalState>(StringComparer.Ordinal);

        foreach (ChannelDefinition? definition in definitions)
        {
            if (definition is null)
            {
                return SignalHubCreationResult.Rejected(SignalHubErrorCode.DefinitionRequired);
            }

            if (string.IsNullOrWhiteSpace(definition.Id))
            {
                return SignalHubCreationResult.Rejected(SignalHubErrorCode.ChannelIdRequired, definition.Id);
            }

            if (definition.Kind is not (ChannelKind.DigitalInput or ChannelKind.DigitalOutput))
            {
                return SignalHubCreationResult.Rejected(
                    SignalHubErrorCode.UnsupportedChannelKind,
                    definition.Id);
            }

            if (definition.InitialValue is not (0d or 1d))
            {
                return SignalHubCreationResult.Rejected(
                    SignalHubErrorCode.InvalidInitialValue,
                    definition.Id);
            }

            if (!signalsById.TryAdd(
                    definition.Id,
                    new SignalState(
                        definition.Id,
                        definition.Name ?? string.Empty,
                        definition.Kind,
                        definition.InitialValue == 1d)))
            {
                return SignalHubCreationResult.Rejected(
                    SignalHubErrorCode.DuplicateChannelId,
                    definition.Id);
            }
        }

        return SignalHubCreationResult.Accepted(new DeterministicSignalHub(signalsById));
    }

    /// <summary>
    /// Captures a coherent, immutable snapshot ordered by ordinal channel ID.
    /// </summary>
    public SignalHubSnapshot CaptureSnapshot()
    {
        lock (_sync)
        {
            return new SignalHubSnapshot(
                _revision,
                _orderedChannelIds.Select(id => _signalsById[id].Capture()));
        }
    }

    /// <summary>
    /// Atomically restores every signal to its authored initial value.
    /// </summary>
    /// <remarks>
    /// The state revision advances exactly once when one or more values change.
    /// A no-op reset leaves the revision unchanged.
    /// </remarks>
    public SignalHubResetResult Reset()
    {
        lock (_sync)
        {
            long previousRevision = _revision;
            int changedSignalCount = 0;

            foreach (string channelId in _orderedChannelIds)
            {
                if (_signalsById[channelId].Reset())
                {
                    changedSignalCount++;
                }
            }

            if (changedSignalCount > 0)
            {
                _revision++;
            }

            return new SignalHubResetResult(
                changedSignalCount,
                previousRevision,
                _revision);
        }
    }

    /// <summary>
    /// Reads a configured digital input or output without exposing mutable state.
    /// </summary>
    public SignalReadResult ReadDigitalSignal(string? channelId)
    {
        lock (_sync)
        {
            if (string.IsNullOrWhiteSpace(channelId))
            {
                return SignalReadResult.Rejected(
                    SignalHubErrorCode.ChannelIdRequired,
                    channelId,
                    _revision);
            }

            if (!_signalsById.TryGetValue(channelId, out SignalState? signal))
            {
                return SignalReadResult.Rejected(
                    SignalHubErrorCode.ChannelNotFound,
                    channelId,
                    _revision);
            }

            return SignalReadResult.Accepted(
                signal.Id,
                signal.Kind,
                signal.Value,
                _revision);
        }
    }

    /// <summary>
    /// Requests a manual or simulation-component digital-input write.
    /// </summary>
    public SignalWriteResult SetDigitalInput(
        string? channelId,
        bool value,
        SignalWriteOwner owner) =>
        SetDigitalSignal(channelId, ChannelKind.DigitalInput, value, owner);

    /// <summary>
    /// Activates or clears a deterministic effective-value override for one
    /// digital input. Nominal manual/component writes continue to be retained
    /// while the override is active and become effective again when cleared.
    /// </summary>
    public DigitalInputOverrideResult SetDigitalInputOverride(
        string? channelId,
        bool? forcedValue)
    {
        lock (_sync)
        {
            if (string.IsNullOrWhiteSpace(channelId))
            {
                return OverrideRejected(SignalHubErrorCode.ChannelIdRequired, channelId);
            }

            if (!_signalsById.TryGetValue(channelId, out SignalState? signal))
            {
                return OverrideRejected(SignalHubErrorCode.ChannelNotFound, channelId);
            }

            if (signal.Kind != ChannelKind.DigitalInput)
            {
                return new DigitalInputOverrideResult(
                    false,
                    SignalHubErrorCode.ChannelKindMismatch,
                    channelId,
                    signal.OverrideValue,
                    signal.OverrideValue,
                    signal.Value,
                    signal.Value,
                    false,
                    false,
                    _revision);
            }

            bool? previousOverride = signal.OverrideValue;
            bool previousValue = signal.Value;
            signal.SetOverride(forcedValue);
            bool overrideChanged = previousOverride != signal.OverrideValue;
            bool valueChanged = previousValue != signal.Value;
            if (overrideChanged || valueChanged)
            {
                _revision++;
            }

            return new DigitalInputOverrideResult(
                true,
                SignalHubErrorCode.None,
                channelId,
                previousOverride,
                signal.OverrideValue,
                previousValue,
                signal.Value,
                overrideChanged,
                valueChanged,
                _revision);
        }
    }

    /// <summary>
    /// Requests a manual-commissioning or embedded-sequence digital-output write.
    /// </summary>
    public SignalWriteResult SetDigitalOutput(
        string? channelId,
        bool value,
        SignalWriteOwner owner) =>
        SetDigitalSignal(channelId, ChannelKind.DigitalOutput, value, owner);

    private SignalWriteResult SetDigitalSignal(
        string? channelId,
        ChannelKind requestedKind,
        bool value,
        SignalWriteOwner owner)
    {
        lock (_sync)
        {
            if (string.IsNullOrWhiteSpace(channelId))
            {
                return SignalWriteResult.Rejected(
                    SignalHubErrorCode.ChannelIdRequired,
                    channelId,
                    null,
                    owner,
                    value,
                    null,
                    _revision);
            }

            if (!_signalsById.TryGetValue(channelId, out SignalState? signal))
            {
                return SignalWriteResult.Rejected(
                    SignalHubErrorCode.ChannelNotFound,
                    channelId,
                    null,
                    owner,
                    value,
                    null,
                    _revision);
            }

            if (signal.Kind != requestedKind)
            {
                return SignalWriteResult.Rejected(
                    SignalHubErrorCode.ChannelKindMismatch,
                    channelId,
                    signal.Kind,
                    owner,
                    value,
                    signal.Value,
                    _revision);
            }

            bool ownerAllowed = requestedKind == ChannelKind.DigitalInput
                ? owner is SignalWriteOwner.Manual or SignalWriteOwner.SimulationComponent
                : owner is SignalWriteOwner.Manual or SignalWriteOwner.EmbeddedSequence;

            if (!ownerAllowed)
            {
                return SignalWriteResult.Rejected(
                    SignalHubErrorCode.WriteOwnerNotAllowed,
                    channelId,
                    signal.Kind,
                    owner,
                    value,
                    signal.Value,
                    _revision);
            }

            bool previousNominalValue = signal.NominalValue;
            bool previousValue = signal.Value;
            signal.SetNominalValue(value);
            bool stateChanged = previousValue != signal.Value;
            if (previousNominalValue != signal.NominalValue || stateChanged)
            {
                _revision++;
            }

            return SignalWriteResult.Accepted(
                channelId,
                signal.Kind,
                owner,
                value,
                previousValue,
                signal.Value,
                stateChanged,
                _revision);
        }
    }

    private DigitalInputOverrideResult OverrideRejected(
        SignalHubErrorCode errorCode,
        string? channelId) =>
        new(
            false,
            errorCode,
            channelId,
            null,
            null,
            null,
            null,
            false,
            false,
            _revision);

    private sealed class SignalState
    {
        public SignalState(string id, string name, ChannelKind kind, bool value)
        {
            Id = id;
            Name = name;
            Kind = kind;
            InitialValue = value;
            NominalValue = value;
            Value = value;
        }

        public string Id { get; }
        public string Name { get; }
        public ChannelKind Kind { get; }
        public bool InitialValue { get; }
        public bool NominalValue { get; private set; }
        public bool? OverrideValue { get; private set; }
        public bool Value { get; private set; }

        public void SetNominalValue(bool value)
        {
            NominalValue = value;
            Value = OverrideValue ?? NominalValue;
        }

        public void SetOverride(bool? forcedValue)
        {
            OverrideValue = forcedValue;
            Value = OverrideValue ?? NominalValue;
        }

        public bool Reset()
        {
            bool stateChanged = NominalValue != InitialValue
                || OverrideValue.HasValue
                || Value != InitialValue;
            NominalValue = InitialValue;
            OverrideValue = null;
            Value = InitialValue;
            return stateChanged;
        }

        public DigitalSignalSnapshot Capture() =>
            new(Id, Name, Kind, Value, NominalValue, OverrideValue);
    }
}
