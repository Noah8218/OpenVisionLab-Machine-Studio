namespace OpenVisionLab.Machine.IO.Channels;

public sealed class DiscreteChannel
{
    public string Id { get; }
    public bool Value { get; set; }

    public DiscreteChannel(string id, bool initial = false)
    {
        Id = id;
        Value = initial;
    }
}
