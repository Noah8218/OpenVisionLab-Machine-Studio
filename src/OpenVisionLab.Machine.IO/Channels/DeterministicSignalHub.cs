using OpenVisionLab.Machine.Core.Channels;

namespace OpenVisionLab.Machine.IO.Channels;

/// <summary>
/// Owns deterministic digital and analog channel state for local simulation.
/// </summary>
/// <remarks>
/// Digital inputs accept writes from <see cref="SignalWriteOwner.Manual"/> or
/// <see cref="SignalWriteOwner.SimulationComponent"/>.
/// Digital and analog outputs accept writes from the manual or embedded-sequence
/// owners. Analog inputs use the same manual/component ownership as digital
/// inputs.
/// Accepted no-op writes do not advance the state revision.
/// </remarks>
public sealed class DeterministicSignalHub
{
    private readonly object _sync = new();
    private readonly Dictionary<string, SignalState> _signalsById;
    private readonly string[] _orderedChannelIds;
    private long _revision;
    private SignalHubSnapshot? _snapshot;

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

        ChannelDefinition[] authoredDefinitions = definitions.ToArray();
        var signalsById = new Dictionary<string, SignalState>(StringComparer.Ordinal);

        foreach (ChannelDefinition? definition in authoredDefinitions)
        {
            if (definition is null)
            {
                return SignalHubCreationResult.Rejected(SignalHubErrorCode.DefinitionRequired);
            }

            if (string.IsNullOrWhiteSpace(definition.Id))
            {
                return SignalHubCreationResult.Rejected(SignalHubErrorCode.ChannelIdRequired, definition.Id);
            }

            if (definition.Kind is not (
                ChannelKind.DigitalInput
                or ChannelKind.DigitalOutput
                or ChannelKind.AnalogInput
                or ChannelKind.AnalogOutput))
            {
                return SignalHubCreationResult.Rejected(
                    SignalHubErrorCode.UnsupportedChannelKind,
                    definition.Id);
            }

            if (IsDigitalKind(definition.Kind)
                && definition.InitialValue is not (0d or 1d))
            {
                return SignalHubCreationResult.Rejected(
                    SignalHubErrorCode.InvalidInitialValue,
                    definition.Id);
            }

            if (IsAnalogKind(definition.Kind)
                && !double.IsFinite(definition.InitialValue))
            {
                return SignalHubCreationResult.Rejected(
                    SignalHubErrorCode.InvalidAnalogValue,
                    definition.Id);
            }

            if (!signalsById.TryAdd(
                    definition.Id,
                    new SignalState(
                        definition.Id,
                        definition.Name ?? string.Empty,
                        definition.Kind,
                        definition.InitialValue)))
            {
                return SignalHubCreationResult.Rejected(
                    SignalHubErrorCode.DuplicateChannelId,
                    definition.Id);
            }
        }

        foreach (ChannelDefinition definition in authoredDefinitions)
        {
            string[] interlockIds = (definition.InterlockIds ?? [])
                .ToArray();
            if (interlockIds.Length == 0)
            {
                continue;
            }

            if (definition.Kind != ChannelKind.DigitalOutput)
            {
                return SignalHubCreationResult.Rejected(
                    SignalHubErrorCode.InvalidInterlockConfiguration,
                    definition.Id);
            }

            var uniqueIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (string? interlockId in interlockIds)
            {
                if (string.IsNullOrWhiteSpace(interlockId)
                    || string.Equals(interlockId, definition.Id, StringComparison.Ordinal)
                    || !uniqueIds.Add(interlockId))
                {
                    return SignalHubCreationResult.Rejected(
                        SignalHubErrorCode.InvalidInterlockConfiguration,
                        interlockId ?? definition.Id);
                }

                if (!signalsById.TryGetValue(interlockId, out SignalState? interlock))
                {
                    return SignalHubCreationResult.Rejected(
                        SignalHubErrorCode.InterlockChannelNotFound,
                        interlockId);
                }

                if (interlock.Kind != ChannelKind.DigitalInput)
                {
                    return SignalHubCreationResult.Rejected(
                        SignalHubErrorCode.InterlockChannelKindMismatch,
                        interlockId);
                }
            }

            SignalState output = signalsById[definition.Id];
            output.SetInterlockIds(interlockIds);
            if (output.IsOn && interlockIds.Any(id => !signalsById[id].IsOn))
            {
                return SignalHubCreationResult.Rejected(
                    SignalHubErrorCode.InterlockNotSatisfied,
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
            if (_snapshot?.Revision == _revision)
            {
                return _snapshot;
            }

            _snapshot = new SignalHubSnapshot(
                _revision,
                _orderedChannelIds
                    .Where(id => IsDigitalKind(_signalsById[id].Kind))
                    .Select(id => _signalsById[id].CaptureDigital()),
                _orderedChannelIds
                    .Where(id => IsAnalogKind(_signalsById[id].Kind))
                    .Select(id => _signalsById[id].CaptureAnalog()));
            return _snapshot;
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
                signal.IsOn,
                _revision);
        }
    }

    /// <summary>
    /// Reads a configured analog input or output without exposing mutable state.
    /// </summary>
    public AnalogSignalReadResult ReadAnalogSignal(string? channelId)
    {
        lock (_sync)
        {
            if (string.IsNullOrWhiteSpace(channelId))
            {
                return AnalogSignalReadResult.Rejected(
                    SignalHubErrorCode.ChannelIdRequired,
                    channelId,
                    _revision);
            }

            if (!_signalsById.TryGetValue(channelId, out SignalState? signal))
            {
                return AnalogSignalReadResult.Rejected(
                    SignalHubErrorCode.ChannelNotFound,
                    channelId,
                    _revision);
            }

            if (!IsAnalogKind(signal.Kind))
            {
                return AnalogSignalReadResult.Rejected(
                    SignalHubErrorCode.ChannelKindMismatch,
                    channelId,
                    _revision,
                    signal.Kind);
            }

            return AnalogSignalReadResult.Accepted(
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
    /// Requests a manual or simulation-component analog-input write.
    /// </summary>
    public AnalogSignalWriteResult SetAnalogInput(
        string? channelId,
        double value,
        SignalWriteOwner owner) =>
        SetAnalogSignal(channelId, ChannelKind.AnalogInput, value, owner);

    /// <summary>
    /// Requests a manual or embedded-sequence analog-output write.
    /// </summary>
    public AnalogSignalWriteResult SetAnalogOutput(
        string? channelId,
        double value,
        SignalWriteOwner owner) =>
        SetAnalogSignal(channelId, ChannelKind.AnalogOutput, value, owner);

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
                    signal.IsOn,
                    signal.IsOn,
                    false,
                    false,
                    _revision);
            }

            bool? previousOverride = signal.OverrideValue;
            bool previousValue = signal.IsOn;
            signal.SetOverride(forcedValue);
            bool overrideChanged = previousOverride != signal.OverrideValue;
            bool valueChanged = previousValue != signal.IsOn;
            bool dependentOutputChanged = !signal.IsOn
                && DeactivateOutputsInterlockedBy(signal.Id);
            if (overrideChanged || valueChanged || dependentOutputChanged)
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
                signal.IsOn,
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

    /// <summary>
    /// Requests two digital-output writes as one validation and revision boundary.
    /// No output is changed when either request is rejected.
    /// </summary>
    public DigitalOutputPairWriteResult SetDigitalOutputPairAtomically(
        string? firstChannelId,
        bool firstValue,
        string? secondChannelId,
        bool secondValue,
        SignalWriteOwner owner)
    {
        lock (_sync)
        {
            if (!TryValidateDigitalOutputWrite(
                    firstChannelId,
                    firstValue,
                    owner,
                    out SignalState? firstSignal,
                    out SignalHubErrorCode firstError))
            {
                return DigitalOutputPairWriteResult.Rejected(
                    firstError,
                    firstChannelId,
                    owner,
                    _revision);
            }

            if (string.Equals(firstChannelId, secondChannelId, StringComparison.Ordinal))
            {
                return DigitalOutputPairWriteResult.Rejected(
                    SignalHubErrorCode.DuplicateChannelId,
                    secondChannelId,
                    owner,
                    _revision);
            }

            if (!TryValidateDigitalOutputWrite(
                    secondChannelId,
                    secondValue,
                    owner,
                    out SignalState? secondSignal,
                    out SignalHubErrorCode secondError))
            {
                return DigitalOutputPairWriteResult.Rejected(
                    secondError,
                    secondChannelId,
                    owner,
                    _revision);
            }

            int changedSignalCount = 0;
            if (firstSignal!.IsOn != firstValue)
            {
                changedSignalCount++;
            }

            if (secondSignal!.IsOn != secondValue)
            {
                changedSignalCount++;
            }

            firstSignal.SetNominalValue(firstValue);
            secondSignal.SetNominalValue(secondValue);
            if (changedSignalCount > 0)
            {
                _revision++;
            }

            return DigitalOutputPairWriteResult.Accepted(
                owner,
                changedSignalCount,
                _revision);
        }
    }

    private AnalogSignalWriteResult SetAnalogSignal(
        string? channelId,
        ChannelKind requestedKind,
        double value,
        SignalWriteOwner owner)
    {
        lock (_sync)
        {
            if (string.IsNullOrWhiteSpace(channelId))
            {
                return AnalogSignalWriteResult.Rejected(
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
                return AnalogSignalWriteResult.Rejected(
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
                return AnalogSignalWriteResult.Rejected(
                    SignalHubErrorCode.ChannelKindMismatch,
                    channelId,
                    signal.Kind,
                    owner,
                    value,
                    signal.Value,
                    _revision);
            }

            if (!double.IsFinite(value))
            {
                return AnalogSignalWriteResult.Rejected(
                    SignalHubErrorCode.InvalidAnalogValue,
                    channelId,
                    signal.Kind,
                    owner,
                    value,
                    signal.Value,
                    _revision);
            }

            bool ownerAllowed = requestedKind == ChannelKind.AnalogInput
                ? owner is SignalWriteOwner.Manual or SignalWriteOwner.SimulationComponent
                : owner is SignalWriteOwner.Manual or SignalWriteOwner.EmbeddedSequence;
            if (!ownerAllowed)
            {
                return AnalogSignalWriteResult.Rejected(
                    SignalHubErrorCode.WriteOwnerNotAllowed,
                    channelId,
                    signal.Kind,
                    owner,
                    value,
                    signal.Value,
                    _revision);
            }

            double previousValue = signal.Value;
            signal.SetNominalValue(value);
            bool stateChanged = previousValue != signal.Value;
            if (stateChanged)
            {
                _revision++;
            }

            return AnalogSignalWriteResult.Accepted(
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

    private SignalWriteResult SetDigitalSignal(
        string? channelId,
        ChannelKind requestedKind,
        bool value,
        SignalWriteOwner owner)
    {
        lock (_sync)
        {
            SignalState? signal;
            if (requestedKind == ChannelKind.DigitalOutput)
            {
                if (!TryValidateDigitalOutputWrite(
                        channelId,
                        value,
                        owner,
                        out signal,
                        out SignalHubErrorCode errorCode))
                {
                    return SignalWriteResult.Rejected(
                        errorCode,
                        channelId,
                        signal?.Kind,
                        owner,
                        value,
                        signal?.IsOn,
                        _revision);
                }
            }
            else
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

                if (!_signalsById.TryGetValue(channelId, out signal))
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
                        signal.IsOn,
                        _revision);
                }

                if (owner is not (SignalWriteOwner.Manual or SignalWriteOwner.SimulationComponent))
                {
                    return SignalWriteResult.Rejected(
                        SignalHubErrorCode.WriteOwnerNotAllowed,
                        channelId,
                        signal.Kind,
                        owner,
                        value,
                        signal.IsOn,
                        _revision);
                }
            }

            bool previousNominalValue = signal!.NominalValue == 1d;
            bool previousValue = signal.IsOn;
            signal.SetNominalValue(value);
            bool stateChanged = previousValue != signal.IsOn;
            bool dependentOutputChanged = requestedKind == ChannelKind.DigitalInput
                && !signal.IsOn
                && DeactivateOutputsInterlockedBy(signal.Id);
            bool nominalValueChanged = previousNominalValue != (signal.NominalValue == 1d);
            if (nominalValueChanged || stateChanged || dependentOutputChanged)
            {
                _revision++;
            }

            return SignalWriteResult.Accepted(
                channelId!,
                signal.Kind,
                owner,
                value,
                previousValue,
                signal.IsOn,
                stateChanged,
                _revision);
        }
    }

    private bool TryValidateDigitalOutputWrite(
        string? channelId,
        bool value,
        SignalWriteOwner owner,
        out SignalState? signal,
        out SignalHubErrorCode errorCode)
    {
        signal = null;
        if (string.IsNullOrWhiteSpace(channelId))
        {
            errorCode = SignalHubErrorCode.ChannelIdRequired;
            return false;
        }

        if (!_signalsById.TryGetValue(channelId, out signal))
        {
            errorCode = SignalHubErrorCode.ChannelNotFound;
            return false;
        }

        if (signal.Kind != ChannelKind.DigitalOutput)
        {
            errorCode = SignalHubErrorCode.ChannelKindMismatch;
            return false;
        }

        if (owner is not (SignalWriteOwner.Manual or SignalWriteOwner.EmbeddedSequence))
        {
            errorCode = SignalHubErrorCode.WriteOwnerNotAllowed;
            return false;
        }

        if (value && signal.InterlockIds.Any(id => !_signalsById[id].IsOn))
        {
            errorCode = SignalHubErrorCode.InterlockNotSatisfied;
            return false;
        }

        errorCode = SignalHubErrorCode.None;
        return true;
    }

    private bool DeactivateOutputsInterlockedBy(string inputId)
    {
        bool changed = false;
        foreach (SignalState signal in _signalsById.Values)
        {
            if (signal.Kind != ChannelKind.DigitalOutput
                || !signal.InterlockIds.Contains(inputId, StringComparer.Ordinal)
                || !signal.IsOn)
            {
                continue;
            }

            signal.SetNominalValue(false);
            changed = true;
        }

        return changed;
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

    private static bool IsDigitalKind(ChannelKind kind) =>
        kind is ChannelKind.DigitalInput or ChannelKind.DigitalOutput;

    private static bool IsAnalogKind(ChannelKind kind) =>
        kind is ChannelKind.AnalogInput or ChannelKind.AnalogOutput;

    private sealed class SignalState
    {
        public SignalState(string id, string name, ChannelKind kind, double value)
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
        public double InitialValue { get; }
        public double NominalValue { get; private set; }
        public bool? OverrideValue { get; private set; }
        public double Value { get; private set; }
        public bool IsOn => Value == 1d;
        public IReadOnlyList<string> InterlockIds { get; private set; } = Array.Empty<string>();

        public void SetInterlockIds(IEnumerable<string> interlockIds) =>
            InterlockIds = Array.AsReadOnly(interlockIds.ToArray());

        public void SetNominalValue(bool value)
            => SetNominalValue(value ? 1d : 0d);

        public void SetNominalValue(double value)
        {
            NominalValue = value;
            Value = OverrideValue is bool forced
                ? forced ? 1d : 0d
                : NominalValue;
        }

        public void SetOverride(bool? forcedValue)
        {
            OverrideValue = forcedValue;
            Value = OverrideValue is bool forced
                ? forced ? 1d : 0d
                : NominalValue;
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

        public DigitalSignalSnapshot CaptureDigital() =>
            new(Id, Name, Kind, IsOn, NominalValue == 1d, OverrideValue);

        public AnalogSignalSnapshot CaptureAnalog() =>
            new(Id, Name, Kind, Value, NominalValue);
    }
}
