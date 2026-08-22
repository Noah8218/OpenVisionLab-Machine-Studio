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
/// Immutable, ID-ordered view of all signal values at one hub revision.
/// </summary>
public sealed class SignalHubSnapshot
{
    private readonly IReadOnlyDictionary<string, DigitalSignalSnapshot> _signalsById;

    internal SignalHubSnapshot(long revision, IEnumerable<DigitalSignalSnapshot> signals)
    {
        Revision = revision;

        DigitalSignalSnapshot[] copiedSignals = signals.ToArray();
        Signals = Array.AsReadOnly(copiedSignals);
        _signalsById = new ReadOnlyDictionary<string, DigitalSignalSnapshot>(
            copiedSignals.ToDictionary(signal => signal.Id, StringComparer.Ordinal));
    }

    public long Revision { get; }
    public ReadOnlyCollection<DigitalSignalSnapshot> Signals { get; }

    public bool TryGetSignal(string channelId, out DigitalSignalSnapshot? signal)
    {
        if (channelId is null)
        {
            signal = null;
            return false;
        }

        return _signalsById.TryGetValue(channelId, out signal);
    }
}
