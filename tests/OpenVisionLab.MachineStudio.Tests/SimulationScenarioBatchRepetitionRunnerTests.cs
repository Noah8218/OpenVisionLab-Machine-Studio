using OpenVisionLab.Machine.Core.Channels;
using OpenVisionLab.Machine.Sequence.Compilation;
using OpenVisionLab.Machine.Simulation.Axis;
using OpenVisionLab.Machine.Simulation.Engine;
using OpenVisionLab.Machine.Simulation.Scenarios;
using OpenVisionLab.MachineStudio.ViewModel;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

public sealed class SimulationScenarioBatchRepetitionRunnerTests
{
    [Fact]
    public async Task RunAsync_ReplaysScenarioAndReturnsProjectLinkedEvidence()
    {
        var fixedStep = TimeSpan.FromMilliseconds(5);
        var request = new SimulationScenarioBatchRepetitionRequest(
            "project-1",
            "Batch project",
            new SimulationRuntimeConfiguration(
                [new AxisConfiguration { Id = "axis-1", Name = "Test axis" }],
                Array.Empty<ChannelDefinition>(),
                Array.Empty<CompiledSequence>()),
            new DeterministicConditionScenarioProfile(
                DeterministicConditionScenarioProfile.CurrentSchemaVersion,
                "scenario-1",
                "Scenario",
                "Runner test scenario.",
                "axis-1",
                42,
                DurationTicks: 3,
                MinimumStateTicks: 1,
                JitterTicks: 0),
            "D:\\OpenVisionLab-TestData\\OpenVisionLab-Machine-Studio\\scenario-batch-runner\\project.ovmachine",
            "{\"id\":\"project-1\"}",
            fixedStep);

        var result = await new SimulationScenarioBatchRepetitionRunner().RunAsync(request);

        Assert.True(result.IsSuccess, result.FailureReason);
        Assert.Equal(request.ProjectId, result.ProjectId);
        Assert.Equal(request.ProjectName, result.ProjectName);
        Assert.Equal(request.ProjectPath, result.ProjectPath);
        Assert.Equal(request.Profile.ScenarioId, result.ScenarioId);
        Assert.Equal(request.Profile.DurationTicks, result.ExecutedTicks);
        Assert.Equal(request.FixedStep.Ticks, result.FixedStepTicks);
    }
}
