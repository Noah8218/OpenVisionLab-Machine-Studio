using OpenVisionLab.Machine.Core.Channels;
using OpenVisionLab.Machine.Core.Devices;
using OpenVisionLab.Machine.Simulation.Axis;
using OpenVisionLab.Machine.Simulation.Camera;
using OpenVisionLab.Machine.Simulation.Engine;
using OpenVisionLab.Machine.Sequence.Compilation;
using Xunit;

namespace OpenVisionLab.Machine.Simulation.Tests;

public sealed class SimulationRuntimeConfigurationBuilderTests
{
    [Fact]
    public void Build_ReturnsIndependentRuntimeCandidatesInOneResult()
    {
        var builder = new SimulationRuntimeConfigurationBuilder(TimeSpan.FromMilliseconds(5));
        var configuration = new SimulationRuntimeConfiguration(
            new[]
            {
                new AxisConfiguration
                {
                    Id = "x",
                    Name = "X"
                }
            },
            new[]
            {
                new ChannelDefinition
                {
                    Id = "di.start",
                    Name = "Start",
                    Kind = ChannelKind.DigitalInput
                }
            },
            Array.Empty<CompiledSequence>(),
            new[]
            {
                new VirtualCameraConfiguration(
                    "camera",
                    "Camera",
                    1,
                    1,
                    PlaceholderInspectionDecision.Pass)
            });

        var accepted = builder.TryBuild(configuration, out var result, out var error);

        Assert.True(accepted, error);
        Assert.NotNull(result);
        Assert.Single(result.Axes);
        Assert.Equal("x", result.Axes[0].Id);
        Assert.Single(result.Cameras);
        Assert.Equal("camera", result.Cameras[0].Id);
        Assert.Empty(result.CompiledSequences);
        Assert.Empty(result.SequenceExecutors);
        Assert.Null(result.MachineLayout);
        Assert.Null(result.PickPlaceWorkpiece);
        Assert.Equal(0, result.AutomaticRunRepeatDelayTicks);
    }

    [Fact]
    public void TryCreateAxes_PreservesDuplicateAndInvalidConfigurationErrors()
    {
        var builder = new SimulationRuntimeConfigurationBuilder(TimeSpan.FromMilliseconds(5));

        var duplicate = builder.TryCreateAxes(
            new[]
            {
                new AxisConfiguration { Id = "x" },
                new AxisConfiguration { Id = "x" }
            },
            out _,
            out var duplicateError);
        var invalid = builder.TryCreateAxes(
            new[]
            {
                new AxisConfiguration
                {
                    Id = "invalid",
                    MaximumPosition = -1
                }
            },
            out _,
            out var invalidError);

        Assert.False(duplicate);
        Assert.Equal("Axis id 'x' is duplicated.", duplicateError);
        Assert.False(invalid);
        Assert.Equal("Axis 'invalid' has invalid limits or motion parameters.", invalidError);
    }
}
