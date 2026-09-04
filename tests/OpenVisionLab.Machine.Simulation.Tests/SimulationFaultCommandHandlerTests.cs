using OpenVisionLab.Machine.Core.Channels;
using OpenVisionLab.Machine.IO.Channels;
using OpenVisionLab.Machine.Simulation.Axis;
using OpenVisionLab.Machine.Simulation.Commands;
using OpenVisionLab.Machine.Simulation.Engine;
using OpenVisionLab.Machine.Simulation.Faults;
using OpenVisionLab.Machine.Simulation.Layout;
using Xunit;

namespace OpenVisionLab.Machine.Simulation.Tests;

public sealed class SimulationFaultCommandHandlerTests
{
    [Fact]
    public void Apply_StuckInputInjectionAddsFaultAndOperationEvent()
    {
        var handler = new SimulationFaultCommandHandler();
        var command = new InjectSimulationFaultCommand(
            SimulationFaultKind.StuckDigitalInput,
            "di.sensor",
            false);
        var context = CreateContext(signalHub: CreateSignalHub("di.sensor"));

        var outcome = handler.Apply(command, context);

        Assert.True(outcome.Result.IsAccepted, outcome.Result.Detail);
        Assert.Single(context.ActiveFaults);
        var activeFault = Assert.Single(context.ActiveFaults.Values);
        Assert.Equal(SimulationFaultKind.StuckDigitalInput, activeFault.Kind);
        Assert.Equal(false, activeFault.ForcedValue);
        var operationEvent = Assert.Single(outcome.Events!);
        Assert.Equal("FaultInjected", operationEvent.Code);
        Assert.Equal("StuckDigitalInput on 'di.sensor' forced to OFF.", operationEvent.Message);
    }

    [Fact]
    public void Apply_ClearStuckInputRemovesFaultAndReturnsClearEvent()
    {
        var handler = new SimulationFaultCommandHandler();
        var context = CreateContext(signalHub: CreateSignalHub("di.sensor"));
        var inject = new InjectSimulationFaultCommand(
            SimulationFaultKind.StuckDigitalInput,
            "di.sensor",
            false);
        handler.Apply(inject, context);

        var clear = new ClearSimulationFaultCommand(
            SimulationFaultKind.StuckDigitalInput,
            "di.sensor");
        var outcome = handler.Apply(clear, context);

        Assert.True(outcome.Result.IsAccepted, outcome.Result.Detail);
        Assert.Empty(context.ActiveFaults);
        var operationEvent = Assert.Single(outcome.Events!);
        Assert.Equal("FaultCleared", operationEvent.Code);
        Assert.Equal("Cleared StuckDigitalInput on 'di.sensor' forced to OFF.", operationEvent.Message);
    }

    [Fact]
    public void Apply_MissingAxisFaultTargetRejectsWithoutMutatingFaultState()
    {
        var handler = new SimulationFaultCommandHandler();
        var command = new InjectSimulationFaultCommand(
            SimulationFaultKind.AxisMotionBlocked,
            "missing-axis");
        var context = CreateContext();

        var outcome = handler.Apply(command, context);

        Assert.False(outcome.Result.IsAccepted);
        Assert.Equal(SimulationCommandErrorCode.FaultTargetNotFound, outcome.Result.ErrorCode);
        Assert.Empty(context.ActiveFaults);
        Assert.Empty(outcome.Events ?? Array.Empty<SimulationFaultCommandEvent>());
    }

    private static SimulationFaultCommandContext CreateContext(
        IList<ServoAxisComponent>? axes = null,
        DeterministicSignalHub? signalHub = null,
        DeterministicMachineLayout? machineLayout = null,
        IDictionary<SimulationFaultKey, SimulationFaultSnapshot>? activeFaults = null) =>
        new(
            axes ?? new List<ServoAxisComponent>(),
            signalHub ?? CreateSignalHub(),
            machineLayout,
            activeFaults ?? new Dictionary<SimulationFaultKey, SimulationFaultSnapshot>(),
            7,
            TimeSpan.FromMilliseconds(35));

    private static DeterministicSignalHub CreateSignalHub(params string[] channelIds) =>
        DeterministicSignalHub.Create(channelIds.Select(Channel).ToArray()).Hub!;

    private static ChannelDefinition Channel(string id) => new()
    {
        Id = id,
        Name = id,
        Kind = ChannelKind.DigitalInput
    };
}
