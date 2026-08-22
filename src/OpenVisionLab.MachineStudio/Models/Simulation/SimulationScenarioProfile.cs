using System;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace OpenVisionLab.MachineStudio.Models.Simulation;

public enum SimulationScenarioMode
{
    Normal,
    Fault,
    Recovery,
    Congested
}

public sealed record SimulationScenarioProfile
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public static readonly int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public string ProfileId { get; init; } = "normal";

    public string Name { get; init; } = "Normal";

    public string Description { get; init; } =
        "Default stable line behavior with balanced warning/alarm balance and nominal throughput.";

    public SimulationScenarioMode Mode { get; init; } = SimulationScenarioMode.Normal;

    public int Seed { get; init; }

    public SimulationScenarioRuntime BaseRuntime { get; init; } = SimulationScenarioRuntime.Normal;

    public SimulationScenarioStepProfile[] Steps { get; init; } = [];

    public SimulationEquipmentScenarioProfile[] EquipmentOverrides { get; init; } = [];

    public static SimulationScenarioProfile[] BuiltIns { get; } =
    [
        new SimulationScenarioProfile
        {
            ProfileId = "normal",
            Name = "Normal",
            Description =
                "Normal operation with balanced behavior for long-run throughput and stable alarms.",
            Mode = SimulationScenarioMode.Normal,
            Seed = 1001,
            BaseRuntime = SimulationScenarioRuntime.Normal,
            Steps =
            [
                new SimulationScenarioStepProfile
                {
                    Name = "Baseline",
                    Description = "Balanced behavior for the entire run.",
                    StartCycle = 0,
                    EndCycle = null
                }
            ]
        },
        new SimulationScenarioProfile
        {
            ProfileId = "fault-injection",
            Name = "Fault Injection",
            Description =
                "Inject early warning and alarm behavior; useful for training operator responses.",
            Mode = SimulationScenarioMode.Fault,
            Seed = 2002,
            BaseRuntime = SimulationScenarioRuntime.Fault,
            Steps =
            [
                new SimulationScenarioStepProfile
                {
                    Name = "Fault burst",
                    Description = "Initial burst to validate alarm pathways.",
                    StartCycle = 30,
                    EndCycle = 160,
                    WarningMultiplier = 2.4,
                    AlarmMultiplier = 2.2,
                    WarningIntervalMultiplier = 0.6,
                    AlarmIntervalMultiplier = 0.5,
                    TemperatureDriftMultiplier = 1.4,
                    VibrationDriftMultiplier = 1.35,
                    ThroughputJitterMultiplier = 1.5,
                    ThroughputWarningLossMultiplier = 1.6,
                    ThroughputAlarmLossMultiplier = 1.2,
                    WarningToRecoveryMultiplier = 0.55,
                    WarningToAlarmMultiplier = 1.6
                },
                new SimulationScenarioStepProfile
                {
                    Name = "Recovery attempt",
                    Description = "System stabilizes and returns to moderate operation.",
                    StartCycle = 161,
                    EndCycle = null,
                    WarningMultiplier = 1.1,
                    AlarmMultiplier = 1.0,
                    ThroughputCapacityMultiplier = 0.9,
                    ThroughputJitterMultiplier = 1.2,
                    ThroughputWarningLossMultiplier = 1.1,
                    ThroughputAlarmLossMultiplier = 1.05
                }
            ],
            EquipmentOverrides =
            [
                new SimulationEquipmentScenarioProfile
                {
                    EquipmentId = "EQ-1003",
                    Description = "Conveyor shows higher warning sensitivity.",
                    WarningMultiplier = 1.8,
                    VibrationDriftMultiplier = 1.5
                },
                new SimulationEquipmentScenarioProfile
                {
                    EquipmentId = "EQ-1001",
                    Description = "Curing oven has stricter temperature drift.",
                    TemperatureDriftMultiplier = 1.5
                }
            ]
        },
        new SimulationScenarioProfile
        {
            ProfileId = "recovery",
            Name = "Recovery",
            Description =
                "Tough recovery path: initial instability then stable return with high recovery chance.",
            Mode = SimulationScenarioMode.Recovery,
            Seed = 3003,
            BaseRuntime = SimulationScenarioRuntime.Recovery,
            Steps =
            [
                new SimulationScenarioStepProfile
                {
                    Name = "Cold start",
                    Description = "Recovery window starts with lower warning risk but warmup drift.",
                    StartCycle = 0,
                    EndCycle = 30,
                    WarningMultiplier = 0.75,
                    AlarmMultiplier = 0.6,
                    ThroughputCapacityMultiplier = 0.85,
                    ThroughputJitterMultiplier = 0.7,
                    WarningToRecoveryMultiplier = 1.8,
                    AlarmToRecoveryMultiplier = 1.8,
                    RecoveryAfterWarningMultiplier = 1.8
                },
                new SimulationScenarioStepProfile
                {
                    Name = "Stable recovery",
                    Description = "Stable operation after recovery window.",
                    StartCycle = 31,
                    EndCycle = null,
                    WarningMultiplier = 0.7,
                    AlarmMultiplier = 0.55,
                    ThroughputCapacityMultiplier = 1.05,
                    TemperatureDriftMultiplier = 0.85,
                    VibrationDriftMultiplier = 0.85
                }
            ]
        },
        new SimulationScenarioProfile
        {
            ProfileId = "congested",
            Name = "Congested Line",
            Description = "Throughput bottleneck and congestion pressure across equipment.",
            Mode = SimulationScenarioMode.Congested,
            Seed = 4004,
            BaseRuntime = SimulationScenarioRuntime.Congested,
            Steps =
            [
                new SimulationScenarioStepProfile
                {
                    Name = "Ramp-up",
                    Description = "Start healthy, then build to peak congestion.",
                    StartCycle = 0,
                    EndCycle = 45,
                    ThroughputCapacityMultiplier = 0.92,
                    ThroughputJitterMultiplier = 1.15,
                },
                new SimulationScenarioStepProfile
                {
                    Name = "Peak congestion",
                    Description = "Stable queue pressure and elevated warning chance.",
                    StartCycle = 46,
                    EndCycle = 140,
                    WarningMultiplier = 1.4,
                    AlarmMultiplier = 1.45,
                    WarningIntervalMultiplier = 0.75,
                    AlarmIntervalMultiplier = 0.72,
                    TemperatureDriftMultiplier = 1.1,
                    VibrationDriftMultiplier = 1.05,
                    ThroughputCapacityMultiplier = 0.64,
                    ThroughputWarningLossMultiplier = 1.1
                },
                new SimulationScenarioStepProfile
                {
                    Name = "Cool-down",
                    Description = "Short recovery as line pressure drops.",
                    StartCycle = 141,
                    EndCycle = null,
                    WarningMultiplier = 1.0,
                    AlarmMultiplier = 0.9,
                    ThroughputCapacityMultiplier = 0.85,
                    ThroughputJitterMultiplier = 1.05
                }
            ]
        }
    ];

    public static SimulationScenarioProfile GetBuiltInById(string profileId) =>
        BuiltIns.FirstOrDefault(profile =>
            string.Equals(profile.ProfileId, profileId, StringComparison.OrdinalIgnoreCase))
        ?? BuiltIns[0];

    public static SimulationScenarioProfile? LoadFromJson(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var fileText = File.ReadAllText(path);
            var profile = JsonSerializer.Deserialize<SimulationScenarioProfile>(fileText, JsonOptions);
            if (profile is null)
            {
                return null;
            }

            return Normalize(profile);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static SimulationScenarioProfile Normalize(SimulationScenarioProfile? profile)
    {
        if (profile is null)
        {
            return BuiltIns[0];
        }

        profile = profile with
        {
            SchemaVersion = profile.SchemaVersion == 0 ? CurrentSchemaVersion : profile.SchemaVersion,
            ProfileId = string.IsNullOrWhiteSpace(profile.ProfileId) ? "custom" : profile.ProfileId.Trim(),
            Name = string.IsNullOrWhiteSpace(profile.Name) ? "Custom Scenario" : profile.Name.Trim(),
            Description = string.IsNullOrWhiteSpace(profile.Description)
                ? "Custom scenario profile."
                : profile.Description.Trim(),
            Steps = profile.Steps?.Select(step => step with
            {
                Name = string.IsNullOrWhiteSpace(step.Name) ? "Unnamed Step" : step.Name.Trim(),
                Description = string.IsNullOrWhiteSpace(step.Description) ? "-" : step.Description.Trim(),
            }).ToArray() ?? [],
            EquipmentOverrides = profile.EquipmentOverrides?
                .Where(profile => !string.IsNullOrWhiteSpace(profile.EquipmentId))
                .Select(profile => profile with
                {
                    EquipmentId = profile.EquipmentId!.Trim()
                }).ToArray() ?? []
        };
        return profile;
    }

    public SimulationScenarioRuntime ResolveRuntime(int cycle, string equipmentId)
    {
        var runtime = BaseRuntime;

        var activeStep = Steps.FirstOrDefault(step => step.AppliesTo(cycle));
        if (activeStep is not null)
        {
            runtime = activeStep.Apply(runtime);
        }

        var equipmentOverride = EquipmentOverrides.FirstOrDefault(overrideProfile =>
            string.Equals(
                overrideProfile.EquipmentId,
                equipmentId,
                StringComparison.OrdinalIgnoreCase));
        if (equipmentOverride is not null)
        {
            runtime = equipmentOverride.Apply(runtime);
        }

        return runtime with
        {
            WarningChanceMultiplier = Math.Clamp(runtime.WarningChanceMultiplier, 0.0, 8.0),
            AlarmChanceMultiplier = Math.Clamp(runtime.AlarmChanceMultiplier, 0.0, 8.0),
            WarningToRecoveryMultiplier = Math.Clamp(runtime.WarningToRecoveryMultiplier, 0.0, 5.0),
            WarningToAlarmMultiplier = Math.Clamp(runtime.WarningToAlarmMultiplier, 0.0, 5.0),
            AlarmToRecoveryMultiplier = Math.Clamp(runtime.AlarmToRecoveryMultiplier, 0.0, 5.0),
            WarningIntervalMultiplier = Math.Max(0.1, runtime.WarningIntervalMultiplier),
            AlarmIntervalMultiplier = Math.Max(0.1, runtime.AlarmIntervalMultiplier),
            TemperatureDriftMultiplier = Math.Max(0.1, runtime.TemperatureDriftMultiplier),
            VibrationDriftMultiplier = Math.Max(0.1, runtime.VibrationDriftMultiplier),
            ThroughputJitterMultiplier = Math.Max(0.1, runtime.ThroughputJitterMultiplier),
            ThroughputCapacityMultiplier = Math.Max(0.1, runtime.ThroughputCapacityMultiplier),
            ThroughputWarningLossMultiplier = Math.Max(0.1, runtime.ThroughputWarningLossMultiplier),
            ThroughputAlarmLossMultiplier = Math.Max(0.1, runtime.ThroughputAlarmLossMultiplier),
            RecoveryAfterWarningMultiplier = Math.Max(0.1, runtime.RecoveryAfterWarningMultiplier),
            RecoveryIntervalMultiplier = Math.Max(0.1, runtime.RecoveryIntervalMultiplier)
        };
    }
}

public sealed record SimulationScenarioRuntime(
    int RecoveryInterval,
    double WarningChanceMultiplier,
    double AlarmChanceMultiplier,
    double WarningToRecoveryMultiplier,
    double WarningToAlarmMultiplier,
    double AlarmToRecoveryMultiplier,
    double WarningIntervalMultiplier,
    double AlarmIntervalMultiplier,
    double TemperatureDriftMultiplier,
    double VibrationDriftMultiplier,
    double ThroughputJitterMultiplier,
    double ThroughputCapacityMultiplier,
    double ThroughputWarningLossMultiplier,
    double ThroughputAlarmLossMultiplier,
    double RecoveryAfterWarningMultiplier,
    double RecoveryIntervalMultiplier)
{
    public static readonly SimulationScenarioRuntime Normal = new(
        RecoveryInterval: 5,
        WarningChanceMultiplier: 1.0,
        AlarmChanceMultiplier: 1.0,
        WarningToRecoveryMultiplier: 1.0,
        WarningToAlarmMultiplier: 1.0,
        AlarmToRecoveryMultiplier: 1.0,
        WarningIntervalMultiplier: 1.0,
        AlarmIntervalMultiplier: 1.0,
        TemperatureDriftMultiplier: 1.0,
        VibrationDriftMultiplier: 1.0,
        ThroughputJitterMultiplier: 1.0,
        ThroughputCapacityMultiplier: 1.0,
        ThroughputWarningLossMultiplier: 1.0,
        ThroughputAlarmLossMultiplier: 1.0,
        RecoveryAfterWarningMultiplier: 1.0,
        RecoveryIntervalMultiplier: 1.0);

    public static readonly SimulationScenarioRuntime Fault = new(
        RecoveryInterval: 6,
        WarningChanceMultiplier: 1.5,
        AlarmChanceMultiplier: 1.9,
        WarningToRecoveryMultiplier: 0.6,
        WarningToAlarmMultiplier: 1.8,
        AlarmToRecoveryMultiplier: 0.85,
        WarningIntervalMultiplier: 0.65,
        AlarmIntervalMultiplier: 0.55,
        TemperatureDriftMultiplier: 1.4,
        VibrationDriftMultiplier: 1.35,
        ThroughputJitterMultiplier: 1.45,
        ThroughputCapacityMultiplier: 0.82,
        ThroughputWarningLossMultiplier: 1.4,
        ThroughputAlarmLossMultiplier: 1.2,
        RecoveryAfterWarningMultiplier: 0.75,
        RecoveryIntervalMultiplier: 0.85);

    public static readonly SimulationScenarioRuntime Recovery = new(
        RecoveryInterval: 4,
        WarningChanceMultiplier: 0.7,
        AlarmChanceMultiplier: 0.6,
        WarningToRecoveryMultiplier: 1.9,
        WarningToAlarmMultiplier: 0.5,
        AlarmToRecoveryMultiplier: 1.9,
        WarningIntervalMultiplier: 1.25,
        AlarmIntervalMultiplier: 1.3,
        TemperatureDriftMultiplier: 0.75,
        VibrationDriftMultiplier: 0.8,
        ThroughputJitterMultiplier: 0.85,
        ThroughputCapacityMultiplier: 0.88,
        ThroughputWarningLossMultiplier: 0.8,
        ThroughputAlarmLossMultiplier: 0.8,
        RecoveryAfterWarningMultiplier: 1.8,
        RecoveryIntervalMultiplier: 1.2);

    public static readonly SimulationScenarioRuntime Congested = new(
        RecoveryInterval: 5,
        WarningChanceMultiplier: 1.35,
        AlarmChanceMultiplier: 1.45,
        WarningToRecoveryMultiplier: 0.75,
        WarningToAlarmMultiplier: 1.25,
        AlarmToRecoveryMultiplier: 0.95,
        WarningIntervalMultiplier: 0.75,
        AlarmIntervalMultiplier: 0.72,
        TemperatureDriftMultiplier: 1.1,
        VibrationDriftMultiplier: 1.1,
        ThroughputJitterMultiplier: 1.2,
        ThroughputCapacityMultiplier: 0.62,
        ThroughputWarningLossMultiplier: 1.15,
        ThroughputAlarmLossMultiplier: 1.05,
        RecoveryAfterWarningMultiplier: 0.8,
        RecoveryIntervalMultiplier: 0.95);
}

public sealed record SimulationScenarioStepProfile
{
    public string Name { get; init; } = "Unnamed Step";

    public string Description { get; init; } = "-";

    public int? StartCycle { get; init; }

    public int? EndCycle { get; init; }

    public double WarningMultiplier { get; init; } = 1.0;

    public double AlarmMultiplier { get; init; } = 1.0;

    public double WarningToRecoveryMultiplier { get; init; } = 1.0;

    public double WarningToAlarmMultiplier { get; init; } = 1.0;

    public double AlarmToRecoveryMultiplier { get; init; } = 1.0;

    public double WarningIntervalMultiplier { get; init; } = 1.0;

    public double AlarmIntervalMultiplier { get; init; } = 1.0;

    public double TemperatureDriftMultiplier { get; init; } = 1.0;

    public double VibrationDriftMultiplier { get; init; } = 1.0;

    public double ThroughputJitterMultiplier { get; init; } = 1.0;

    public double ThroughputCapacityMultiplier { get; init; } = 1.0;

    public double ThroughputWarningLossMultiplier { get; init; } = 1.0;

    public double ThroughputAlarmLossMultiplier { get; init; } = 1.0;

    public double RecoveryAfterWarningMultiplier { get; init; } = 1.0;

    public double RecoveryIntervalMultiplier { get; init; } = 1.0;

    public bool AppliesTo(int cycle) =>
        (StartCycle is null || cycle >= StartCycle) && (EndCycle is null || cycle <= EndCycle);

    public SimulationScenarioRuntime Apply(SimulationScenarioRuntime runtime) =>
        runtime with
        {
            WarningChanceMultiplier = runtime.WarningChanceMultiplier * WarningMultiplier,
            AlarmChanceMultiplier = runtime.AlarmChanceMultiplier * AlarmMultiplier,
            WarningToRecoveryMultiplier = runtime.WarningToRecoveryMultiplier * WarningToRecoveryMultiplier,
            WarningToAlarmMultiplier = runtime.WarningToAlarmMultiplier * WarningToAlarmMultiplier,
            AlarmToRecoveryMultiplier = runtime.AlarmToRecoveryMultiplier * AlarmToRecoveryMultiplier,
            WarningIntervalMultiplier = runtime.WarningIntervalMultiplier * WarningIntervalMultiplier,
            AlarmIntervalMultiplier = runtime.AlarmIntervalMultiplier * AlarmIntervalMultiplier,
            TemperatureDriftMultiplier = runtime.TemperatureDriftMultiplier * TemperatureDriftMultiplier,
            VibrationDriftMultiplier = runtime.VibrationDriftMultiplier * VibrationDriftMultiplier,
            ThroughputJitterMultiplier = runtime.ThroughputJitterMultiplier * ThroughputJitterMultiplier,
            ThroughputCapacityMultiplier = runtime.ThroughputCapacityMultiplier * ThroughputCapacityMultiplier,
            ThroughputWarningLossMultiplier = runtime.ThroughputWarningLossMultiplier * ThroughputWarningLossMultiplier,
            ThroughputAlarmLossMultiplier = runtime.ThroughputAlarmLossMultiplier * ThroughputAlarmLossMultiplier,
            RecoveryAfterWarningMultiplier = runtime.RecoveryAfterWarningMultiplier * RecoveryAfterWarningMultiplier,
            RecoveryIntervalMultiplier = runtime.RecoveryIntervalMultiplier * RecoveryIntervalMultiplier
        };
}

public sealed record SimulationEquipmentScenarioProfile
{
    public string EquipmentId { get; init; } = "";

    public string Description { get; init; } = "-";

    public double WarningMultiplier { get; init; } = 1.0;

    public double AlarmMultiplier { get; init; } = 1.0;

    public double WarningToRecoveryMultiplier { get; init; } = 1.0;

    public double WarningToAlarmMultiplier { get; init; } = 1.0;

    public double AlarmToRecoveryMultiplier { get; init; } = 1.0;

    public double WarningIntervalMultiplier { get; init; } = 1.0;

    public double AlarmIntervalMultiplier { get; init; } = 1.0;

    public double TemperatureDriftMultiplier { get; init; } = 1.0;

    public double VibrationDriftMultiplier { get; init; } = 1.0;

    public double ThroughputJitterMultiplier { get; init; } = 1.0;

    public double ThroughputCapacityMultiplier { get; init; } = 1.0;

    public double ThroughputWarningLossMultiplier { get; init; } = 1.0;

    public double ThroughputAlarmLossMultiplier { get; init; } = 1.0;

    public double RecoveryAfterWarningMultiplier { get; init; } = 1.0;

    public double RecoveryIntervalMultiplier { get; init; } = 1.0;

    public SimulationScenarioRuntime Apply(SimulationScenarioRuntime runtime) =>
        runtime with
        {
            WarningChanceMultiplier = runtime.WarningChanceMultiplier * WarningMultiplier,
            AlarmChanceMultiplier = runtime.AlarmChanceMultiplier * AlarmMultiplier,
            WarningToRecoveryMultiplier = runtime.WarningToRecoveryMultiplier * WarningToRecoveryMultiplier,
            WarningToAlarmMultiplier = runtime.WarningToAlarmMultiplier * WarningToAlarmMultiplier,
            AlarmToRecoveryMultiplier = runtime.AlarmToRecoveryMultiplier * AlarmToRecoveryMultiplier,
            WarningIntervalMultiplier = runtime.WarningIntervalMultiplier * WarningIntervalMultiplier,
            AlarmIntervalMultiplier = runtime.AlarmIntervalMultiplier * AlarmIntervalMultiplier,
            TemperatureDriftMultiplier = runtime.TemperatureDriftMultiplier * TemperatureDriftMultiplier,
            VibrationDriftMultiplier = runtime.VibrationDriftMultiplier * VibrationDriftMultiplier,
            ThroughputJitterMultiplier = runtime.ThroughputJitterMultiplier * ThroughputJitterMultiplier,
            ThroughputCapacityMultiplier = runtime.ThroughputCapacityMultiplier * ThroughputCapacityMultiplier,
            ThroughputWarningLossMultiplier = runtime.ThroughputWarningLossMultiplier * ThroughputWarningLossMultiplier,
            ThroughputAlarmLossMultiplier = runtime.ThroughputAlarmLossMultiplier * ThroughputAlarmLossMultiplier,
            RecoveryAfterWarningMultiplier = runtime.RecoveryAfterWarningMultiplier * RecoveryAfterWarningMultiplier,
            RecoveryIntervalMultiplier = runtime.RecoveryIntervalMultiplier * RecoveryIntervalMultiplier
        };
}
