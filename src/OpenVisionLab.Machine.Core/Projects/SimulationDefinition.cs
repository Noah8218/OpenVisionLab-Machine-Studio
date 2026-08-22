using System.Text.Json.Serialization;

namespace OpenVisionLab.Machine.Core.Projects;

public sealed class SimulationDefinition
{
    [JsonPropertyName("fixedStepMilliseconds")]
    public int FixedStepMilliseconds { get; set; } = 5;

    [JsonPropertyName("defaultTimeScale")]
    public double DefaultTimeScale { get; set; } = 1.0;

    [JsonPropertyName("seed")]
    public int Seed { get; set; } = 1001;

    [JsonPropertyName("testScenarioProfileId")]
    public string TestScenarioProfileId { get; set; } = "normal";

    [JsonPropertyName("testScenarioSeed")]
    public int? TestScenarioSeed { get; set; }

    [JsonPropertyName("testScenarioDurationCycles")]
    public int TestScenarioDurationCycles { get; set; } = 200;

    [JsonPropertyName("testScenarioTargetId")]
    public string? TestScenarioTargetId { get; set; }

    [JsonPropertyName("testScenarioBatchRepetitions")]
    public int TestScenarioBatchRepetitions { get; set; } = 3;

    [JsonPropertyName("testScenarioAxisFault")]
    public TestScenarioAxisFaultDefinition? TestScenarioAxisFault { get; set; }

    [JsonPropertyName("testScenarioFault")]
    public TestScenarioFaultDefinition? TestScenarioFault { get; set; }

    [JsonPropertyName("testScenarioAssertions")]
    public List<TestScenarioAssertionDefinition> TestScenarioAssertions { get; set; } = [];

    [JsonPropertyName("activeLayoutId")]
    public string? ActiveLayoutId { get; set; }

    [JsonPropertyName("automaticRun")]
    public AutomaticRunDefinition? AutomaticRun { get; set; }

    [JsonPropertyName("pickPlaceWorkpiece")]
    public PickPlaceWorkpieceDefinition? PickPlaceWorkpiece { get; set; }
}

public sealed class TestScenarioAxisFaultDefinition
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("axisId")]
    public string? AxisId { get; set; }

    [JsonPropertyName("injectTick")]
    public int InjectTick { get; set; } = 50;

    [JsonPropertyName("holdTicks")]
    public int HoldTicks { get; set; } = 3;

    [JsonPropertyName("restartSequenceId")]
    public string? RestartSequenceId { get; set; }
}

[JsonConverter(typeof(JsonStringEnumConverter<TestScenarioFaultKind>))]
public enum TestScenarioFaultKind
{
    StuckDigitalInput,
    CylinderTravelBlocked,
    AxisMotionBlocked
}

public sealed class TestScenarioFaultDefinition
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("kind")]
    public TestScenarioFaultKind Kind { get; set; } = TestScenarioFaultKind.AxisMotionBlocked;

    [JsonPropertyName("targetId")]
    public string? TargetId { get; set; }

    [JsonPropertyName("forcedValue")]
    public bool? ForcedValue { get; set; }

    [JsonPropertyName("injectTick")]
    public int InjectTick { get; set; } = 50;

    [JsonPropertyName("holdTicks")]
    public int HoldTicks { get; set; } = 3;

    [JsonPropertyName("restartSequenceId")]
    public string? RestartSequenceId { get; set; }
}

[JsonConverter(typeof(JsonStringEnumConverter<TestScenarioAssertionKind>))]
public enum TestScenarioAssertionKind
{
    AutomaticCycleCompleted,
    NoActiveFaults,
    FinalEquipmentState
}

public sealed class TestScenarioAssertionDefinition
{
    [JsonPropertyName("assertionId")]
    public string AssertionId { get; set; } = string.Empty;

    [JsonPropertyName("kind")]
    public TestScenarioAssertionKind Kind { get; set; }

    [JsonPropertyName("targetId")]
    public string? TargetId { get; set; }

    [JsonPropertyName("expectedState")]
    public string? ExpectedState { get; set; }

    [JsonPropertyName("minimumCount")]
    public long MinimumCount { get; set; } = 1;
}
