using OpenVisionLab.Machine.Core.Channels;
using OpenVisionLab.Machine.Sequence.Compilation;
using OpenVisionLab.Machine.Simulation.Axis;
using OpenVisionLab.Machine.Simulation.Engine;
using Xunit;

namespace OpenVisionLab.Machine.Simulation.Tests;

public sealed class SimulationRuntimeStateTests
{
    [Fact]
    public void TryApplyRuntimeConfiguration_UpdatesAggregateAndIdentityTogether()
    {
        var state = new SimulationRuntimeState(TimeSpan.FromMilliseconds(5), 1.0);
        var configuration = new SimulationRuntimeConfiguration(
            new[] { new AxisConfiguration { Id = "axis-x" } },
            Array.Empty<ChannelDefinition>(),
            Array.Empty<CompiledSequence>());

        var accepted = state.TryApplyRuntimeConfiguration(configuration, "project-a", out var error);

        Assert.True(accepted, error);
        Assert.Single(state.Axes);
        Assert.Equal("project-a", state.ProjectId);
        Assert.Equal(1, state.RuntimeGeneration);
        Assert.Equal(SimulationRunMode.Paused, state.RunMode);
        Assert.Equal("project-a", state.CreateSnapshot().ProjectId);
        Assert.Equal(1, state.CreateSnapshot().RuntimeGeneration);
    }

    [Fact]
    public void Reset_RestoresRuntimeOwnedStateAndKeepsProjectIdentity()
    {
        var state = new SimulationRuntimeState(TimeSpan.FromMilliseconds(5), 1.0);
        var configuration = new SimulationRuntimeConfiguration(
            new[] { new AxisConfiguration { Id = "axis-x" } },
            Array.Empty<ChannelDefinition>(),
            Array.Empty<CompiledSequence>());
        Assert.True(state.TryApplyRuntimeConfiguration(configuration, "project-a", out var error), error);
        state.RunMode = SimulationRunMode.RealTime;
        state.PendingSteps = 3;
        state.ActiveSequenceId = "sequence-a";
        state.AdvanceTick();
        var generation = state.RuntimeGeneration;

        state.Reset();

        Assert.Equal(SimulationRunMode.Paused, state.RunMode);
        Assert.Equal(0, state.PendingSteps);
        Assert.Null(state.ActiveSequenceId);
        Assert.Equal(0, state.TickIndex);
        Assert.Equal("project-a", state.ProjectId);
        Assert.Equal(generation + 1, state.RuntimeGeneration);
        Assert.Equal(0, state.CreateSnapshot().TickIndex);
    }

    [Fact]
    public void TryApplyAxisConfiguration_ReplacesRuntimeAndClearsProjectIdentity()
    {
        var state = new SimulationRuntimeState(TimeSpan.FromMilliseconds(5), 1.0);
        var runtimeConfiguration = new SimulationRuntimeConfiguration(
            new[] { new AxisConfiguration { Id = "axis-old" } },
            Array.Empty<ChannelDefinition>(),
            Array.Empty<CompiledSequence>());
        Assert.True(state.TryApplyRuntimeConfiguration(runtimeConfiguration, "project-a", out var configureError), configureError);

        var accepted = state.TryApplyAxisConfiguration(
            new[] { new AxisConfiguration { Id = "axis-new" } },
            out var error);

        Assert.True(accepted, error);
        Assert.Equal("axis-new", Assert.Single(state.Axes).Id);
        Assert.Null(state.ProjectId);
        Assert.Equal(2, state.RuntimeGeneration);
        Assert.Empty(state.Cameras);
        Assert.Null(state.MachineLayout);
        Assert.Null(state.PickPlaceWorkpiece);
    }

    [Fact]
    public void TryApplyRuntimeConfiguration_LeavesExistingAggregateWhenCandidateIsInvalid()
    {
        var state = new SimulationRuntimeState(TimeSpan.FromMilliseconds(5), 1.0);
        var existing = new SimulationRuntimeConfiguration(
            new[] { new AxisConfiguration { Id = "axis-existing" } },
            Array.Empty<ChannelDefinition>(),
            Array.Empty<CompiledSequence>());
        Assert.True(state.TryApplyRuntimeConfiguration(existing, "project-a", out var existingError), existingError);
        var generation = state.RuntimeGeneration;

        var invalid = new SimulationRuntimeConfiguration(
            new[]
            {
                new AxisConfiguration { Id = "axis-invalid" },
                new AxisConfiguration { Id = "axis-invalid" }
            },
            Array.Empty<OpenVisionLab.Machine.Core.Channels.ChannelDefinition>(),
            Array.Empty<OpenVisionLab.Machine.Sequence.Compilation.CompiledSequence>());

        var accepted = state.TryApplyRuntimeConfiguration(invalid, "project-b", out var error);

        Assert.False(accepted);
        Assert.Contains("duplicated", error, StringComparison.Ordinal);
        Assert.Equal("axis-existing", Assert.Single(state.Axes).Id);
        Assert.Equal("project-a", state.ProjectId);
        Assert.Equal(generation, state.RuntimeGeneration);
    }
}
