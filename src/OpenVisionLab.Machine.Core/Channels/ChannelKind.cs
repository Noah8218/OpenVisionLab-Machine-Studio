using System.Text.Json.Serialization;

namespace OpenVisionLab.Machine.Core.Channels;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ChannelKind
{
    DigitalInput,
    DigitalOutput,
    AnalogInput,
    AnalogOutput
}
