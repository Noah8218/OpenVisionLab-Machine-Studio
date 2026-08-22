using System.Linq;
using OpenVisionLab.Machine.Simulation.FaultScenarios;
using Xunit;

namespace OpenVisionLab.Machine.Simulation.Tests;

public sealed class DeterministicFaultScenarioProfileTests
{
    [Fact]
    public void Normalize_Defaults_TrimTargets_AndSortActions()
    {
        var input = new DeterministicFaultScenarioProfile(
            SchemaVersion: 0,
            ScenarioId: " ",
            Name: "",
            Description: "   ",
            DurationTicks: 0,
            Actions:
            [
                new(
                    Tick: 5,
                    Action: DeterministicFaultScenarioActionKind.ClearFault,
                    FaultKind: DeterministicFaultScenarioFaultKind.StuckDigitalInput,
                    TargetId: "  di.station-present  "),
                new(
                    Tick: 2,
                    Action: DeterministicFaultScenarioActionKind.InjectFault,
                    FaultKind: DeterministicFaultScenarioFaultKind.StuckDigitalInput,
                    TargetId: "  di.station-present ")
            ]);

        var normalized = DeterministicFaultScenarioProfile.Normalize(input);

        Assert.Equal(1, normalized.SchemaVersion);
        Assert.Equal("custom", normalized.ScenarioId);
        Assert.Equal("Custom fault scenario", normalized.Name);
        Assert.Equal("Custom fault scenario profile.", normalized.Description);
        Assert.Equal(6, normalized.DurationTicks);
        Assert.Collection(
            normalized.Actions,
            first =>
            {
                Assert.Equal(2, first.Tick);
                Assert.Equal("di.station-present", first.TargetId);
                Assert.Equal(DeterministicFaultScenarioActionKind.InjectFault, first.Action);
            },
            second =>
            {
                Assert.Equal(5, second.Tick);
                Assert.Equal("di.station-present", second.TargetId);
                Assert.Equal(DeterministicFaultScenarioActionKind.ClearFault, second.Action);
            });
    }

    [Fact]
    public void Validate_DurationTicksZeroAndNoActions_IsAllowed()
    {
        var scenario = new DeterministicFaultScenarioProfile(
            SchemaVersion: 1,
            ScenarioId: "no-actions",
            Name: "No actions",
            Description: "No action timeline fixture.",
            DurationTicks: 0,
            Actions: []);

        var errors = DeterministicFaultScenarioProfile.Validate(scenario);

        Assert.Empty(errors);
    }

    [Fact]
    public void Validate_ActionOutsideDuration_Fails()
    {
        var scenario = new DeterministicFaultScenarioProfile(
            SchemaVersion: 1,
            ScenarioId: "outside-duration",
            Name: "Outside duration",
            Description: "Outside duration validation fixture.",
            DurationTicks: 2,
            Actions:
            [
                new(
                    Tick: 2,
                    Action: DeterministicFaultScenarioActionKind.ClearFault,
                    FaultKind: DeterministicFaultScenarioFaultKind.StuckDigitalInput,
                    TargetId: "di.station-present")
            ]);

        var errors = DeterministicFaultScenarioProfile.Validate(scenario).ToArray();

        Assert.Contains(
            errors,
            error => error.Contains("targets tick 2", System.StringComparison.OrdinalIgnoreCase));
    }
}
