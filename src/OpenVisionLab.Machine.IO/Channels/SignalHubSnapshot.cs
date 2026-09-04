using System.Collections.ObjectModel;
using OpenVisionLab.Machine.Core.Channels;

namespace OpenVisionLab.Machine.IO.Channels;

/// <summary>
/// Immutable value captured for one configured digital signal.
/// </summary>
public sealed record DigitalSignalSnapshot(
    string Id,
    string Name,
    ChannelKind Kind,
    bool Value,
    bool NominalValue,
    bool? OverrideValue)
{
    public DigitalSignalSnapshot(string id, string name, ChannelKind kind, bool value)
        : this(id, name, kind, value, value, null)
    {
    }
}

/// <summary>
/// Immutable value captured for one configured analog signal.
/// </summary>
public sealed record AnalogSignalSnapshot(
    string Id,
    string Name,
    ChannelKind Kind,
    double Value,
    double NominalValue);

/// <summary>
/// Immutable, ID-ordered view of all signal values at one hub revision.
/// </summary>
public sealed class SignalHubSnapshot
{
    private readonly IReadOnlyDictionary<string, DigitalSignalSnapshot> _signalsById;
    private readonly IReadOnlyDictionary<string, AnalogSignalSnapshot> _analogSignalsById;

    internal SignalHubSnapshot(
        long revision,
        IEnumerable<DigitalSignalSnapshot> signals,
        IEnumerable<AnalogSignalSnapshot>? analogSignals = null)
    {
        Revision = revision;

        DigitalSignalSnapshot[] copiedSignals = signals.ToArray();
        Signals = Array.AsReadOnly(copiedSignals);
        _signalsById = new ReadOnlyDictionary<string, DigitalSignalSnapshot>(
            copiedSignals.ToDictionary(signal => signal.Id, StringComparer.Ordinal));

        AnalogSignalSnapshot[] copiedAnalogSignals = (analogSignals ?? [])
            .ToArray();
        AnalogSignals = Array.AsReadOnly(copiedAnalogSignals);
        _analogSignalsById = new ReadOnlyDictionary<string, AnalogSignalSnapshot>(
            copiedAnalogSignals.ToDictionary(signal => signal.Id, StringComparer.Ordinal));
    }

    public long Revision { get; }
    public ReadOnlyCollection<DigitalSignalSnapshot> Signals { get; }
    public ReadOnlyCollection<AnalogSignalSnapshot> AnalogSignals { get; }

    public bool TryGetSignal(string channelId, out DigitalSignalSnapshot? signal)
    {
        if (channelId is null)
        {
            signal = null;
            return false;
        }

        return _signalsById.TryGetValue(channelId, out signal);
    }

    public bool TryGetAnalogSignal(
        string channelId,
        out AnalogSignalSnapshot? signal)
    {
        if (channelId is null)
        {
            signal = null;
            return false;
        }

        return _analogSignalsById.TryGetValue(channelId, out signal);
    }
}
