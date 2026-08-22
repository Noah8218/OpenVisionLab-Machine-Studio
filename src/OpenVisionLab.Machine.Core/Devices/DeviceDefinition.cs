using System.Text.Json.Serialization;
using OpenVisionLab.Machine.Core.Models;

namespace OpenVisionLab.Machine.Core.Devices;

public sealed class DeviceDefinition
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("kind")]
    public DeviceKind Kind { get; set; }

    [JsonPropertyName("mountPosition")]
    public Coordinate3D MountPosition { get; set; }

    [JsonPropertyName("channelIds")]
    public List<string> ChannelIds { get; set; } = new();

    [JsonPropertyName("camera")]
    public VirtualCameraDefinition? Camera { get; set; }

    [JsonPropertyName("sensor")]
    public DigitalSensorDefinition? Sensor { get; set; }

    [JsonPropertyName("cylinder")]
    public PneumaticCylinderDefinition? Cylinder { get; set; }

    [JsonPropertyName("conveyor")]
    public ConveyorDefinition? Conveyor { get; set; }

    [JsonPropertyName("workpiece")]
    public WorkpieceDefinition? Workpiece { get; set; }

    [JsonPropertyName("loadLock")]
    public LoadLockDefinition? LoadLock { get; set; }

    [JsonPropertyName("waferHandler")]
    public WaferHandlerDefinition? WaferHandler { get; set; }

    [JsonPropertyName("inspectionSortRouter")]
    public InspectionSortRouterDefinition? InspectionSortRouter { get; set; }

    [JsonPropertyName("inspectionHandoff")]
    public InspectionHandoffDefinition? InspectionHandoff { get; set; }

    [JsonPropertyName("ohtHandoff")]
    public OhtHandoffDefinition? OhtHandoff { get; set; }

    [JsonPropertyName("prealigner")]
    public PrealignerDefinition? Prealigner { get; set; }

    [JsonPropertyName("properties")]
    public Dictionary<string, string> Properties { get; set; } = new();
}
