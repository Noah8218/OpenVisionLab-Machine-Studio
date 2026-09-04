using System.Linq;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Simulation.Faults;
using OpenVisionLab.Machine.Simulation.Scenarios;

namespace OpenVisionLab.MachineStudio.Models.Simulation;

internal sealed record SimulationScenarioProjectSnapshot(
    SimulationScenarioProfile SelectedScenarioProfile,
    int ScenarioSeed,
    int ScenarioDurationCycles,
    int BatchRepetitionCount,
    string? ScenarioTargetId,
    bool IsScheduledFaultEnabled,
    SimulationFaultKind ScheduledFaultKind,
    string? ScheduledFaultTargetId,
    bool ScheduledFaultForcedValue,
    int ScheduledFaultInjectTick,
    int ScheduledFaultHoldTicks,
    bool RestartSequenceAfterFault,
    string? RecoverySequenceId,
    bool RequireAutomaticCycleCompleted,
    int MinimumCompletedCycles,
    bool RequireNoActiveFaults,
    bool RequireFinalEquipmentState,
    string? FinalEquipmentTargetId,
    string FinalEquipmentExpectedState,
    string AutomaticCycleAssertionId,
    string NoActiveFaultsAssertionId,
    string FinalEquipmentStateAssertionId);

internal sealed class SimulationScenarioProjectMapper
{
    internal const string AutomaticCycleAssertionDefaultId = "automatic-cycle-completed";
    internal const string NoActiveFaultsAssertionDefaultId = "final-faults-cleared";
    internal const string FinalEquipmentStateAssertionDefaultId = "final-equipment-state";

    internal SimulationScenarioProjectSnapshot Load(
        SimulationDefinition simulation,
        IReadOnlyList<SimulationScenarioProfile> profiles)
    {
        ArgumentNullException.ThrowIfNull(simulation);
        ArgumentNullException.ThrowIfNull(profiles);

        var assertions = simulation.TestScenarioAssertions ?? [];
        var automaticCycle = assertions.FirstOrDefault(assertion =>
            assertion.Kind == TestScenarioAssertionKind.AutomaticCycleCompleted);
        var noActiveFaults = assertions.FirstOrDefault(assertion =>
            assertion.Kind == TestScenarioAssertionKind.NoActiveFaults);
        var finalEquipmentState = assertions.FirstOrDefault(assertion =>
            assertion.Kind == TestScenarioAssertionKind.FinalEquipmentState);
        var selectedProfile = profiles.FirstOrDefault(profile =>
            string.Equals(profile.ProfileId, simulation.TestScenarioProfileId, StringComparison.OrdinalIgnoreCase))
            ?? profiles[0];
        var legacyAxisFault = simulation.TestScenarioAxisFault;
        var fault = simulation.TestScenarioFault;
        var targetId = fault?.TargetId ?? legacyAxisFault?.AxisId;
        var restartSequenceId = fault?.RestartSequenceId ?? legacyAxisFault?.RestartSequenceId;

        return new SimulationScenarioProjectSnapshot(
            selectedProfile,
            simulation.TestScenarioSeed ?? simulation.Seed,
            Math.Clamp(simulation.TestScenarioDurationCycles, 1, 100_000),
            Math.Clamp(simulation.TestScenarioBatchRepetitions, 1, 100),
            NormalizeNullable(simulation.TestScenarioTargetId),
            fault?.Enabled ?? legacyAxisFault?.Enabled == true,
            fault is null ? SimulationFaultKind.AxisMotionBlocked : ToSimulationFaultKind(fault.Kind),
            NormalizeNullable(targetId),
            fault?.ForcedValue ?? false,
            Math.Clamp(fault?.InjectTick ?? legacyAxisFault?.InjectTick ?? 50, 0, 99_999),
            Math.Clamp(fault?.HoldTicks ?? legacyAxisFault?.HoldTicks ?? 3, 1, 100_000),
            !string.IsNullOrWhiteSpace(restartSequenceId),
            NormalizeNullable(restartSequenceId),
            automaticCycle is not null,
            automaticCycle is null
                ? 1
                : (int)Math.Clamp(automaticCycle.MinimumCount, 1, int.MaxValue),
            noActiveFaults is not null,
            finalEquipmentState is not null,
            NormalizeNullable(finalEquipmentState?.TargetId),
            finalEquipmentState?.ExpectedState?.Trim() ?? string.Empty,
            NormalizeAssertionId(automaticCycle?.AssertionId, AutomaticCycleAssertionDefaultId),
            NormalizeAssertionId(noActiveFaults?.AssertionId, NoActiveFaultsAssertionDefaultId),
            NormalizeAssertionId(finalEquipmentState?.AssertionId, FinalEquipmentStateAssertionDefaultId));
    }

    internal void Save(
        SimulationDefinition simulation,
        SimulationScenarioProjectSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(simulation);
        ArgumentNullException.ThrowIfNull(snapshot);

        simulation.TestScenarioProfileId = snapshot.SelectedScenarioProfile.ProfileId;
        simulation.TestScenarioSeed = snapshot.ScenarioSeed;
        simulation.TestScenarioDurationCycles = snapshot.ScenarioDurationCycles;
        simulation.TestScenarioTargetId = snapshot.ScenarioTargetId;
        simulation.TestScenarioBatchRepetitions = snapshot.BatchRepetitionCount;
        simulation.TestScenarioAxisFault = null;
        simulation.TestScenarioFault = new TestScenarioFaultDefinition
        {
            Enabled = snapshot.IsScheduledFaultEnabled,
            Kind = ToProjectFaultKind(snapshot.ScheduledFaultKind),
            TargetId = snapshot.ScheduledFaultTargetId,
            ForcedValue = snapshot.ScheduledFaultKind == SimulationFaultKind.StuckDigitalInput
                ? snapshot.ScheduledFaultForcedValue
                : null,
            InjectTick = snapshot.ScheduledFaultInjectTick,
            HoldTicks = snapshot.ScheduledFaultHoldTicks,
            RestartSequenceId = snapshot.RestartSequenceAfterFault
                ? snapshot.RecoverySequenceId
                : null
        };
        simulation.TestScenarioAssertions = BuildProjectAssertions(snapshot);
    }

    internal DeterministicConditionScenarioProfile BuildEngineProfile(
        SimulationScenarioProjectSnapshot snapshot,
        string targetId)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetId);

        var faultRecovery = snapshot.IsScheduledFaultEnabled
            && IsScheduledFaultConfigurationValid(snapshot)
            ? new DeterministicFaultRecoverySchedule(
                snapshot.ScheduledFaultKind,
                snapshot.ScheduledFaultTargetId!,
                snapshot.ScheduledFaultInjectTick,
                snapshot.ScheduledFaultHoldTicks,
                snapshot.ScheduledFaultKind == SimulationFaultKind.StuckDigitalInput
                    ? snapshot.ScheduledFaultForcedValue
                    : null,
                snapshot.RestartSequenceAfterFault ? snapshot.RecoverySequenceId : null)
            : null;
        var scenarioId = faultRecovery is null
            ? snapshot.SelectedScenarioProfile.ProfileId
            : $"{snapshot.SelectedScenarioProfile.ProfileId}:{faultRecovery.FaultKind}:{faultRecovery.TargetId}:" +
              $"{faultRecovery.ForcedValue}:{faultRecovery.InjectTick}:{faultRecovery.HoldTicks}:" +
              $"{faultRecovery.RestartSequenceId ?? "clear"}";

        return new DeterministicConditionScenarioProfile(
            DeterministicConditionScenarioProfile.CurrentSchemaVersion,
            scenarioId,
            snapshot.SelectedScenarioProfile.Name,
            snapshot.SelectedScenarioProfile.Description,
            targetId,
            snapshot.ScenarioSeed,
            snapshot.ScenarioDurationCycles,
            snapshot.SelectedScenarioProfile.MinimumStateTicks,
            snapshot.SelectedScenarioProfile.JitterTicks,
            snapshot.SelectedScenarioProfile.InitialState,
            FaultRecovery: faultRecovery,
            Assertions: DeterministicScenarioAssertion.FromProjectDefinitions(
                BuildProjectAssertions(snapshot)));
    }

    internal static bool IsScheduledFaultConfigurationValid(
        SimulationScenarioProjectSnapshot snapshot) =>
        !snapshot.IsScheduledFaultEnabled
        || (snapshot.ScheduledFaultTargetId is not null
            && snapshot.ScheduledFaultKind is SimulationFaultKind.StuckDigitalInput
                or SimulationFaultKind.CylinderTravelBlocked
                or SimulationFaultKind.AxisMotionBlocked
            && snapshot.ScheduledFaultInjectTick >= 0
            && snapshot.ScheduledFaultHoldTicks >= 1
            && (long)snapshot.ScheduledFaultInjectTick + snapshot.ScheduledFaultHoldTicks
                < snapshot.ScenarioDurationCycles
            && (!snapshot.RestartSequenceAfterFault || snapshot.RecoverySequenceId is not null));

    private static List<TestScenarioAssertionDefinition> BuildProjectAssertions(
        SimulationScenarioProjectSnapshot snapshot)
    {
        var assertions = new List<TestScenarioAssertionDefinition>(3);
        if (snapshot.RequireAutomaticCycleCompleted)
        {
            assertions.Add(new TestScenarioAssertionDefinition
            {
                AssertionId = snapshot.AutomaticCycleAssertionId,
                Kind = TestScenarioAssertionKind.AutomaticCycleCompleted,
                MinimumCount = snapshot.MinimumCompletedCycles
            });
        }
        if (snapshot.RequireNoActiveFaults)
        {
            assertions.Add(new TestScenarioAssertionDefinition
            {
                AssertionId = snapshot.NoActiveFaultsAssertionId,
                Kind = TestScenarioAssertionKind.NoActiveFaults
            });
        }
        if (snapshot.RequireFinalEquipmentState)
        {
            assertions.Add(new TestScenarioAssertionDefinition
            {
                AssertionId = snapshot.FinalEquipmentStateAssertionId,
                Kind = TestScenarioAssertionKind.FinalEquipmentState,
                TargetId = snapshot.FinalEquipmentTargetId,
                ExpectedState = snapshot.FinalEquipmentExpectedState.Trim()
            });
        }

        return assertions;
    }

    private static string? NormalizeNullable(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string NormalizeAssertionId(string? assertionId, string defaultId) =>
        string.IsNullOrWhiteSpace(assertionId) ? defaultId : assertionId.Trim();

    private static SimulationFaultKind ToSimulationFaultKind(TestScenarioFaultKind kind) => kind switch
    {
        TestScenarioFaultKind.StuckDigitalInput => SimulationFaultKind.StuckDigitalInput,
        TestScenarioFaultKind.CylinderTravelBlocked => SimulationFaultKind.CylinderTravelBlocked,
        _ => SimulationFaultKind.AxisMotionBlocked
    };

    private static TestScenarioFaultKind ToProjectFaultKind(SimulationFaultKind kind) => kind switch
    {
        SimulationFaultKind.StuckDigitalInput => TestScenarioFaultKind.StuckDigitalInput,
        SimulationFaultKind.CylinderTravelBlocked => TestScenarioFaultKind.CylinderTravelBlocked,
        _ => TestScenarioFaultKind.AxisMotionBlocked
    };
}
