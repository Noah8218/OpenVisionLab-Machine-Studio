using System.Text.Json.Serialization;

namespace OpenVisionLab.Machine.Core.Devices;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DeviceKind
{
    Camera,
    Light,
    Cylinder,
    Sensor,
    Vacuum,
    Conveyor,
    Workpiece,
    LoadLock,
    Robot,
    Handler,
    Sorter,
    Inspection,
    Oht,
    Prealigner
}
