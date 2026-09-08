using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Simulation.Faults;
using OpenVisionLab.Machine.Simulation.Scenarios;
using OpenVisionLab.MachineStudio.Models.Simulation;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

public sealed class SimulationScenarioProjectMapperTests
{
    [Fact]
    public void Load_MapsModernFaultAndAssertions()
    {
        var simulation = new SimulationDefinition
        {
            Seed = 101,
            TestScenarioProfileId = "recovery",
            TestScenarioSeed = 1234,
            TestScenarioDurationCycles = 500,
            TestScenarioTargetId = "cell-1",
            TestScenarioBatchRepetitions = 8,
            TestScenarioFault = new TestScenarioFaultDefinition
            {
                Enabled = true,
                Kind = TestScenarioFaultKind.StuckDigitalInput,
                TargetId = "sensor-1",
                ForcedValue = true,
                InjectTick = 50,
                HoldTicks = 4,
                RestartSequenceId = "recover"
            },
            TestScenarioAssertions =
            [
                new TestScenarioAssertionDefinition
                {
                    AssertionId = "cycle-check",
                    Kind = TestScenarioAssertionKind.AutomaticCycleCompleted,
                    MinimumCount = 3
                },
                new TestScenarioAssertionDefinition
                {
                    AssertionId = "fault-check",
                    Kind = TestScenarioAssertionKind.NoActiveFaults
                },
                new TestScenarioAssertionDefinition
                {
                    AssertionId = "state-check",
                    Kind = TestScenarioAssertionKind.FinalEquipmentState,
                    TargetId = "cell-1",
                    ExpectedState = " ready "
                }
            ]
        };
        var mapper = new SimulationScenarioProjectMapper();

        SimulationScenarioProjectSnapshot snapshot = mapper.Load(
            simulation,
            SimulationScenarioProfile.BuiltIns);

        Assert.Equal("recovery", snapshot.SelectedScenarioProfile.ProfileId);
        Assert.Equal(1234, snapshot.ScenarioSeed);
        Assert.Equal(500, snapshot.ScenarioDurationCycles);
        Assert.Equal(8, snapshot.BatchRepetitionCount);
        Assert.True(snapshot.IsScheduledFaultEnabled);
        Assert.Equal(SimulationFaultKind.StuckDigitalInput, snapshot.ScheduledFaultKind);
        Assert.Equal("sensor-1", snapshot.ScheduledFaultTargetId);
        Assert.True(snapshot.ScheduledFaultForcedValue);
        Assert.Equal("recover", snapshot.RecoverySequenceId);
        Assert.True(snapshot.RequireAutomaticCycleCompleted);
        Assert.Equal(3, snapshot.MinimumCompletedCycles);
        Assert.True(snapshot.RequireNoActiveFaults);
        Assert.True(snapshot.RequireFinalEquipmentState);
        Assert.Equal("cell-1", snapshot.FinalEquipmentTargetId);
        Assert.Equal("ready", snapshot.FinalEquipmentExpectedState);
        Assert.Equal("cycle-check", snapshot.AutomaticCycleAssertionId);
        Assert.Equal("fault-check", snapshot.NoActiveFaultsAssertionId);
        Assert.Equal("state-check", snapshot.FinalEquipmentStateAssertionId);
    }

    [Fact]
    public void Load_MapsLegacyAxisFaultWhenModernFaultIsAbsent()
    {
        var simulation = new SimulationDefinition
        {
            TestScenarioAxisFault = new TestScenarioAxisFaultDefinition
            {
                Enabled = true,
                AxisId = "axis-1",
                InjectTick = 7,
                HoldTicks = 2,
                RestartSequenceId = "recover-axis"
            }
        };
        var mapper = new SimulationScenarioProjectMapper();

        SimulationScenarioProjectSnapshot snapshot = mapper.Load(
            simulation,
            SimulationScenarioProfile.BuiltIns);

        Assert.True(snapshot.IsScheduledFaultEnabled);
        Assert.Equal(SimulationFaultKind.AxisMotionBlocked, snapshot.ScheduledFaultKind);
        Assert.Equal("axis-1", snapshot.ScheduledFaultTargetId);
        Assert.Equal(7, snapshot.ScheduledFaultInjectTick);
        Assert.Equal(2, snapshot.ScheduledFaultHoldTicks);
        Assert.True(snapshot.RestartSequenceAfterFault);
        Assert.Equal("recover-axis", snapshot.RecoverySequenceId);
    }

    [Fact]
    public void Save_AndLoad_PreserveScenarioStateAndCompatibilityFields()
    {
        var mapper = new SimulationScenarioProjectMapper();
        SimulationScenarioProjectSnapshot snapshot = mapper.Load(
            new SimulationDefinition(),
            SimulationScenarioProfile.BuiltIns) with
        {
            SelectedScenarioProfile = SimulationScenarioProfile.GetBuiltInById("fault-injection"),
            ScenarioSeed = 2222,
            ScenarioDurationCycles = 400,
            BatchRepetitionCount = 6,
            ScenarioTargetId = "cell-1",
            IsScheduledFaultEnabled = true,
            ScheduledFaultKind = SimulationFaultKind.CylinderTravelBlocked,
            ScheduledFaultTargetId = "cylinder-1",
            ScheduledFaultInjectTick = 40,
            ScheduledFaultHoldTicks = 5,
            RestartSequenceAfterFault = true,
            RecoverySequenceId = "recover",
            RequireAutomaticCycleCompleted = true,
            MinimumCompletedCycles = 2,
            RequireNoActiveFaults = true,
            RequireFinalEquipmentState = true,
            FinalEquipmentTargetId = "cell-1",
            FinalEquipmentExpectedState = "ready",
            AutomaticCycleAssertionId = "cycle-id",
            NoActiveFaultsAssertionId = "fault-id",
            FinalEquipmentStateAssertionId = "state-id"
        };
        var simulation = new SimulationDefinition
        {
            TestScenarioAxisFault = new TestScenarioAxisFaultDefinition()
        };

        mapper.Save(simulation, snapshot);
        SimulationScenarioProjectSnapshot reloaded = mapper.Load(
            simulation,
            SimulationScenarioProfile.BuiltIns);

        Assert.Null(simulation.TestScenarioAxisFault);
        Assert.NotNull(simulation.TestScenarioFault);
        Assert.Equal(TestScenarioFaultKind.CylinderTravelBlocked, simulation.TestScenarioFault!.Kind);
        Assert.Null(simulation.TestScenarioFault.ForcedValue);
        Assert.Equal(3, simulation.TestScenarioAssertions.Count);
        Assert.Equal(snapshot, reloaded);
    }

    [Fact]
    public void BuildEngineProfile_UsesSnapshotFaultAndAssertions()
    {
        var mapper = new SimulationScenarioProjectMapper();
        SimulationScenarioProjectSnapshot snapshot = mapper.Load(
            new SimulationDefinition(),
            SimulationScenarioProfile.BuiltIns) with
        {
            SelectedScenarioProfile = SimulationScenarioProfile.GetBuiltInById("recovery"),
            ScenarioSeed = 3003,
            ScenarioDurationCycles = 200,
            IsScheduledFaultEnabled = true,
            ScheduledFaultKind = SimulationFaultKind.AxisMotionBlocked,
            ScheduledFaultTargetId = "axis-1",
            ScheduledFaultInjectTick = 50,
            ScheduledFaultHoldTicks = 3,
            RestartSequenceAfterFault = true,
            RecoverySequenceId = "recover",
            RequireNoActiveFaults = true,
            NoActiveFaultsAssertionId = "no-faults"
        };

        DeterministicConditionScenarioProfile profile = mapper.BuildEngineProfile(
            snapshot,
            "cell-1");

        Assert.Equal("cell-1", profile.TargetId);
        Assert.Equal(3003, profile.Seed);
        Assert.Equal(200, profile.DurationTicks);
        Assert.Equal(SimulationFaultKind.AxisMotionBlocked, profile.FaultRecovery?.FaultKind);
        Assert.Equal("axis-1", profile.FaultRecovery?.TargetId);
        Assert.Equal("recover", profile.FaultRecovery?.RestartSequenceId);
        Assert.Contains(profile.Assertions, assertion => assertion.AssertionId == "no-faults");
    }
}
