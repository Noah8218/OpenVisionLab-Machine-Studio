using OpenVisionLab.Machine.Core.Channels;

namespace OpenVisionLab.Machine.IO.Channels;

/// <summary>
/// Owns the mutable value state for one deterministic signal.
/// </summary>
/// <remarks>
/// The hub owns synchronization, revisions, and cross-channel interlocks.
/// This type only applies one signal's nominal/effective value transitions and
/// creates its immutable snapshots.
/// </remarks>
internal sealed class DeterministicSignalState
{
    internal DeterministicSignalState(string id, string name, ChannelKind kind, double value)
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

    public void SetNominalValue(bool value) => SetNominalValue(value ? 1d : 0d);

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
