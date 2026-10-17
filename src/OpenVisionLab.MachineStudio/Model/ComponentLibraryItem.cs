using OpenVisionLab.Machine.Core.Layouts;
using OpenVisionLab.Machine.Core.Devices;

namespace OpenVisionLab.MachineStudio.Model;

public sealed record ComponentLibraryItem(
    LayoutComponentKind Kind,
    string Name,
    string Category,
    string Description,
    NumericalCameraInputKind? CameraInputKind = null)
{
    public string Symbol => Kind switch
    {
        LayoutComponentKind.Conveyor => "▭",
        LayoutComponentKind.FeedPosition => "⇥",
        LayoutComponentKind.ExitPosition => "⇤",
        LayoutComponentKind.DigitalSensor => "◉",
        LayoutComponentKind.LinearStage => "↔",
        LayoutComponentKind.RotaryStage => "↻",
        LayoutComponentKind.PneumaticCylinder => "⇨",
        LayoutComponentKind.Camera => CameraInputKind == NumericalCameraInputKind.HeightMap3D ? "▤" : "▣",
        LayoutComponentKind.DigitalIo => "◧",
        _ => "□"
    };
}
