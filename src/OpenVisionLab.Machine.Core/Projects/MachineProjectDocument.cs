using System.Text.Json.Serialization;

namespace OpenVisionLab.Machine.Core.Projects;

public sealed class MachineProjectDocument
{
    public const string CurrentSchema = "1.12";

    [JsonPropertyName("schema")]
    public string Schema { get; set; } = CurrentSchema;

    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString("n");

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("modifiedAt")]
    public DateTimeOffset ModifiedAt { get; set; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("simulation")]
    public SimulationDefinition Simulation { get; set; } = new();

    [JsonPropertyName("layouts")]
    public List<Layouts.MachineLayoutDefinition> Layouts { get; set; } = new();

    [JsonPropertyName("axes")]
    public List<Axes.VirtualAxisDefinition> Axes { get; set; } = new();

    [JsonPropertyName("multiAxisCommissioningRecipe")]
    public MultiAxisCommissioningRecipeDefinition? MultiAxisCommissioningRecipe { get; set; }

    [JsonPropertyName("semiconductorStationSetup")]
    public SemiconductorStationSetupDefinition? SemiconductorStationSetup { get; set; }

    [JsonPropertyName("devices")]
    public List<Devices.DeviceDefinition> Devices { get; set; } = new();

    [JsonPropertyName("channels")]
    public List<Channels.ChannelDefinition> Channels { get; set; } = new();

    [JsonPropertyName("sequences")]
    public List<Sequences.SequenceDefinition> Sequences { get; set; } = new();
}

public sealed record SemiconductorStationSetupDefinition
{
    public const string DefaultStationName = "Semiconductor Station";
    public const string DefaultWaferType = "300 mm Wafer";
    public const double DefaultAxisTravel = 320;
    public const double DefaultTransportSpeed = 240;
    public const double DefaultEntrySensorPosition = 200;
    public const double DefaultProcessSensorPosition = 430;
    public const int DefaultCylinderTravelTimeMilliseconds = 120;

    [JsonPropertyName("stationName")]
    public string StationName { get; set; } = DefaultStationName;

    [JsonPropertyName("waferType")]
    public string WaferType { get; set; } = DefaultWaferType;

    [JsonPropertyName("axisTravel")]
    public double AxisTravel { get; set; } = DefaultAxisTravel;

    [JsonPropertyName("transportSpeed")]
    public double TransportSpeed { get; set; } = DefaultTransportSpeed;

    [JsonPropertyName("entrySensorPosition")]
    public double EntrySensorPosition { get; set; } = DefaultEntrySensorPosition;

    [JsonPropertyName("processSensorPosition")]
    public double ProcessSensorPosition { get; set; } = DefaultProcessSensorPosition;

    [JsonPropertyName("cylinderTravelTimeMilliseconds")]
    public int CylinderTravelTimeMilliseconds { get; set; } = DefaultCylinderTravelTimeMilliseconds;
}
