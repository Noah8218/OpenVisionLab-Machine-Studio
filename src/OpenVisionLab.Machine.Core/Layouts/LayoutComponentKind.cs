using System.Text.Json.Serialization;

namespace OpenVisionLab.Machine.Core.Layouts;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum LayoutComponentKind
{
    MachineFrame,
    LinearStage,
    RotaryStage,
    DigitalSensor,
    PneumaticCylinder,
    Conveyor,
    Workpiece
}
