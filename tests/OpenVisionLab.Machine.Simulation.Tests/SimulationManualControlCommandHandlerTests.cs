using OpenVisionLab.Machine.Core.Channels;
using OpenVisionLab.Machine.IO.Channels;
using OpenVisionLab.Machine.Sequence.Runtime;
using OpenVisionLab.Machine.Simulation.Axis;
using OpenVisionLab.Machine.Simulation.Camera;
using OpenVisionLab.Machine.Simulation.Commands;
using OpenVisionLab.Machine.Simulation.Engine;
using OpenVisionLab.Machine.Simulation.Faults;
using OpenVisionLab.Machine.Simulation.Layout;
using Xunit;

namespace OpenVisionLab.Machine.Simulation.Tests;

public sealed class SimulationManualControlCommandHandlerTests
{
    [Fact]
    public void Apply_StartManualControlReturnsExplicitStateDeltaAndOperationEvent()
    {
        var command = new StartManualControlCommand();
        var handler = new SimulationManualControlCommandHandler();

        var outcome = handler.Apply(command, CreateContext());

        Assert.True(outcome.Result.IsAccepted, outcome.Result.Detail);
        Assert.Equal(SimulationRunMode.RealTime, outcome.RunMode);
        Assert.Equal(SimulationControlOwner.Manual, outcome.ControlOwner);
        Assert.Equal(0, outcome.PendingSteps);
        var operationEvent = Assert.Single(outcome.Events!);
        Assert.Equal("ManualControlStarted", operationEvent.Code);
        Assert.Equal(7, outcome.Result.AppliedTick);
        Assert.Equal(TimeSpan.FromMilliseconds(35), outcome.Result.SimulationTime);
    }

    [Fact]
    public void Apply_GroupMoveValidatesEveryAxisBeforeMutatingAnyAxis()
    {
        var axes = new[] { CreateAxis("x"), CreateAxis("y") };
        var command = new MoveAxesAbsoluteCommand(new[]
        {
            new AxisMoveTarget("x", 10),
            new AxisMoveTarget("y", 500)
        });
        var handler = new SimulationManualControlCommandHandler();

        var outcome = handler.Apply(command, CreateContext(axes: axes));

        Assert.False(outcome.Result.IsAccepted);
        Assert.Equal(SimulationCommandErrorCode.AxisTargetOutOfRange, outcome.Result.ErrorCode);
        Assert.All(axes, axis => Assert.Equal(AxisState.Idle, axis.State));
        Assert.Empty(outcome.Events ?? Array.Empty<SimulationManualControlEvent>());
    }

    [Fact]
    public void Apply_ManualMoveReturnsOperationEventAndPreservesBoundaryResult()
    {
        var handler = new SimulationManualControlCommandHandler();
        var command = new MoveAbsoluteCommand("x", 20);
        var axes = new[] { CreateAxis("x") };

        var outcome = handler.Apply(command, CreateContext(axes: axes));

        Assert.True(outcome.Result.IsAccepted, outcome.Result.Detail);
        Assert.Equal(SimulationCommandErrorCode.None, outcome.Result.ErrorCode);
        Assert.Equal(7, outcome.Result.AppliedTick);
        Assert.Equal(TimeSpan.FromMilliseconds(35), outcome.Result.SimulationTime);
        Assert.Equal(AxisState.Moving, Assert.Single(axes).State);
        var operationEvent = Assert.Single(outcome.Events!);
        Assert.Equal("AxisMoveAccepted", operationEvent.Code);
        Assert.Equal("x target = 20.000.", operationEvent.Message);
    }

    [Fact]
    public void Apply_VirtualInputReturnsChangedOperationEventWithoutManualOwner()
    {
        var handler = new SimulationManualControlCommandHandler();
        var command = new SetVirtualInputCommand("di.start", true);

        var outcome = handler.Apply(
            command,
            CreateContext(
                controlOwner: SimulationControlOwner.Definition,
                signalHub: CreateSignalHub("di.start")));

        Assert.True(outcome.Result.IsAccepted, outcome.Result.Detail);
        var operationEvent = Assert.Single(outcome.Events!);
        Assert.Equal("DigitalInputChanged", operationEvent.Code);
        Assert.Equal("di.start = ON.", operationEvent.Message);
    }

    [Fact]
    public void Apply_VirtualInputForceRejectsAnActiveStuckInputFault()
    {
        var handler = new SimulationManualControlCommandHandler();
        var command = new SetVirtualInputForceCommand("di.start", true);
        var activeFaults = new Dictionary<SimulationFaultKey, SimulationFaultSnapshot>
        {
            [new SimulationFaultKey(SimulationFaultKind.StuckDigitalInput, "di.start")] =
                new(
                    SimulationFaultKind.StuckDigitalInput,
                    "di.start",
                    false,
                    7,
                    TimeSpan.FromMilliseconds(35))
        };

        var outcome = handler.Apply(
            command,
            CreateContext(
                signalHub: CreateSignalHub("di.start"),
                activeFaults: activeFaults));

        Assert.False(outcome.Result.IsAccepted);
        Assert.Equal(SimulationCommandErrorCode.SignalWriteRejected, outcome.Result.ErrorCode);
        Assert.Empty(outcome.Events ?? Array.Empty<SimulationManualControlEvent>());
    }

    [Fact]
    public void Apply_DigitalSensorForceRejectsWhenRuntimeLayoutIsUnavailable()
    {
        var handler = new SimulationManualControlCommandHandler();
        var outcome = handler.Apply(
            new SetDigitalSensorForceCommand("sensor.start", true),
            CreateContext());

        Assert.False(outcome.Result.IsAccepted);
        Assert.Equal(SimulationCommandErrorCode.DigitalSensorNotFound, outcome.Result.ErrorCode);
    }

    private static SimulationManualControlContext CreateContext(
        SimulationRunMode runMode = SimulationRunMode.Paused,
        SimulationControlOwner controlOwner = SimulationControlOwner.Manual,
        bool automaticRunActive = false,
        IReadOnlyList<ServoAxisComponent>? axes = null,
        DeterministicSignalHub? signalHub = null,
        DeterministicMachineLayout? machineLayout = null,
        IReadOnlyDictionary<SimulationFaultKey, SimulationFaultSnapshot>? activeFaults = null) =>
        new(
            runMode,
            controlOwner,
            automaticRunActive,
            axes ?? new[] { CreateAxis("x") },
            Array.Empty<DeterministicVirtualCamera>(),
            new Dictionary<string, DeterministicSequenceExecutor>(),
            signalHub ?? DeterministicSignalHub.Create(Array.Empty<ChannelDefinition>()).Hub!,
            machineLayout,
            activeFaults ?? new Dictionary<SimulationFaultKey, SimulationFaultSnapshot>(),
            7,
            TimeSpan.FromMilliseconds(35),
            value => value ? "ON" : "OFF");

    private static DeterministicSignalHub CreateSignalHub(params string[] channelIds) =>
        DeterministicSignalHub.Create(channelIds.Select(Channel).ToArray()).Hub!;

    private static ChannelDefinition Channel(string id) => new()
    {
        Id = id,
        Name = id,
        Kind = ChannelKind.DigitalInput
    };

    private static ServoAxisComponent CreateAxis(string id) => new(new AxisConfiguration
    {
        Id = id,
        Name = id,
        MinimumPosition = 0,
        MaximumPosition = 300,
        HomePosition = 0,
        MaximumVelocity = 200,
        Acceleration = 500,
        Deceleration = 500
    });
}
