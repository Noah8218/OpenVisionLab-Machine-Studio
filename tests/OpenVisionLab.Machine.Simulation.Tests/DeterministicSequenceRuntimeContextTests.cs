using OpenVisionLab.Machine.Core.Channels;
using OpenVisionLab.Machine.Core.Devices;
using OpenVisionLab.Machine.IO.Channels;
using OpenVisionLab.Machine.Sequence.Runtime;
using OpenVisionLab.Machine.Simulation.Axis;
using OpenVisionLab.Machine.Simulation.Camera;
using OpenVisionLab.Machine.Simulation.Engine;
using Xunit;

namespace OpenVisionLab.Machine.Simulation.Tests;

public sealed class DeterministicSequenceRuntimeContextTests
{
    [Fact]
    public void Context_AdaptsSignalAxisAndCameraOperationsWithDeterministicEvents()
    {
        var creation = DeterministicSignalHub.Create(
            new[]
            {
                new ChannelDefinition
                {
                    Id = "input.start",
                    Name = "Start input",
                    Kind = ChannelKind.DigitalInput,
                    InitialValue = 0
                },
                new ChannelDefinition
                {
                    Id = "output.ready",
                    Name = "Ready output",
                    Kind = ChannelKind.DigitalOutput,
                    InitialValue = 0
                }
            });
        Assert.True(creation.IsAccepted, creation.ErrorCode.ToString());

        var events = new List<RuntimeEvent>();
        var camera = new DeterministicVirtualCamera(
            new VirtualCameraConfiguration(
                "camera-1",
                "Camera 1",
                exposureTicks: 1,
                transferTicks: 1,
                PlaceholderInspectionDecision.Pass));
        var context = new DeterministicSequenceRuntimeContext(
            creation.Hub!,
            new[] { CreateAxis() },
            new[] { camera },
            eventTick: 12,
            eventTime: TimeSpan.FromMilliseconds(60),
            (category, code, message, tick, time) => events.Add(new RuntimeEvent(
                category,
                code,
                message,
                tick,
                time)));

        var input = context.ReadSignal("input.start");
        Assert.True(input.IsSuccess);
        Assert.False(input.Value);

        var output = context.SetSignal("output.ready", true);
        Assert.True(output.IsSuccess);
        Assert.Equal("DigitalOutputChanged", Assert.Single(events).Code);

        var move = context.RequestAxisMove("x", 100);
        Assert.True(move.IsSuccess);
        Assert.Equal(SequenceAxisMotionState.Moving, context.ReadAxisMotionState("x").State);

        var trigger = context.TriggerCamera("camera-1", "recipe-1");
        Assert.True(trigger.IsSuccess);
        Assert.NotNull(trigger.AcquisitionId);
        Assert.Equal(3, events.Count);

        var pending = context.ReadVisionResult("camera-1", trigger.AcquisitionId!);
        Assert.True(pending.IsSuccess);
        Assert.Equal(SequenceVisionResultState.Pending, pending.State);
    }

    [Fact]
    public void Context_ReadVisionResultReturnsCompletedDecisionAndTypedMissingTarget()
    {
        var camera = new DeterministicVirtualCamera(
            new VirtualCameraConfiguration(
                "camera-1",
                "Camera 1",
                exposureTicks: 1,
                transferTicks: 1,
                PlaceholderInspectionDecision.Fail));
        var creation = DeterministicSignalHub.Create(Array.Empty<ChannelDefinition>());
        Assert.True(creation.IsAccepted, creation.ErrorCode.ToString());
        var context = new DeterministicSequenceRuntimeContext(
            creation.Hub!,
            Array.Empty<ServoAxisComponent>(),
            new[] { camera },
            eventTick: 3,
            eventTime: TimeSpan.FromMilliseconds(15),
            (_, _, _, _, _) => { });

        var trigger = context.TriggerCamera("camera-1", "recipe-1");
        Assert.True(trigger.IsSuccess);
        camera.Tick();
        camera.Tick();

        var completed = context.ReadVisionResult("camera-1", trigger.AcquisitionId!);
        Assert.True(completed.IsSuccess);
        Assert.Equal(SequenceVisionResultState.Failed, completed.State);

        var missing = context.ReadSignal("missing");
        Assert.False(missing.IsSuccess);
        Assert.Equal(SequenceContextErrorCode.TargetNotFound, missing.Error!.Code);
    }

    private static ServoAxisComponent CreateAxis() => new(new AxisConfiguration
    {
        Id = "x",
        Name = "X Axis",
        MinimumPosition = 0,
        MaximumPosition = 300,
        HomePosition = 0,
        MaximumVelocity = 200,
        Acceleration = 500,
        Deceleration = 500
    });

    private sealed record RuntimeEvent(
        string Category,
        string Code,
        string Message,
        long Tick,
        TimeSpan Time);
}
