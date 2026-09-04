using OpenVisionLab.Machine.Core.Channels;
using OpenVisionLab.Machine.IO.Channels;
using OpenVisionLab.Machine.Simulation.Commands;
using OpenVisionLab.Machine.Simulation.Engine;
using OpenVisionLab.Machine.Simulation.Faults;
using OpenVisionLab.Machine.Simulation.Scenarios;
using Xunit;

namespace OpenVisionLab.Machine.Simulation.Tests;

public sealed class SimulationConditionScheduledFaultInjectionHandlerTests
{
    [Fact]
    public void Apply_AtInjectionTickAddsFaultAndUsesGeneratedCommandIdentity()
    {
        var signalHub = CreateSignalHub("di.sensor");
        var activeFaults = new Dictionary<SimulationFaultKey, SimulationFaultSnapshot>();
        var outcome = new SimulationConditionScheduledFaultInjectionHandler().Apply(
            CreateContext(
                new DeterministicFaultRecoverySchedule(
                    SimulationFaultKind.StuckDigitalInput,
                    "di.sensor",
                    InjectTick: 4,
                    HoldTicks: 2,
                    ForcedValue: false),
                scenarioTick: 4,
                signalHub,
                activeFaults));

        Assert.True(outcome.ScheduledFaultActive);
        Assert.Null(outcome.ConditionScenarioActive);
        Assert.Single(activeFaults);
        var operationEvent = Assert.Single(outcome.Events!);
        Assert.Equal("FaultInjected", operationEvent.Code);
        Assert.NotNull(operationEvent.CommandId);
    }

    [Fact]
    public void Apply_RejectedInjectionStopsConditionWithoutScheduledFault()
    {
        var outcome = new SimulationConditionScheduledFaultInjectionHandler().Apply(
            CreateContext(
                new DeterministicFaultRecoverySchedule(
                    SimulationFaultKind.StuckDigitalInput,
                    "di.missing",
                    InjectTick: 4,
                    HoldTicks: 2,
                    ForcedValue: true),
                scenarioTick: 4,
                CreateSignalHub("di.sensor"),
                new Dictionary<SimulationFaultKey, SimulationFaultSnapshot>()));

        Assert.False(outcome.ConditionScenarioActive);
        Assert.Null(outcome.ScheduledFaultActive);
        var operationEvent = Assert.Single(outcome.Events!);
        Assert.Equal("ConditionFaultScheduleRejected", operationEvent.Code);
        Assert.Contains("FaultTargetNotFound", operationEvent.Message, StringComparison.Ordinal);
        Assert.NotNull(operationEvent.CommandId);
    }

    [Fact]
    public void Apply_OutsideInjectionTickDoesNotMutateFaultOrScenarioState()
    {
        var activeFaults = new Dictionary<SimulationFaultKey, SimulationFaultSnapshot>();
        var outcome = new SimulationConditionScheduledFaultInjectionHandler().Apply(
            CreateContext(
                new DeterministicFaultRecoverySchedule(
                    SimulationFaultKind.StuckDigitalInput,
                    "di.sensor",
                    InjectTick: 4,
                    HoldTicks: 2,
                    ForcedValue: true),
                scenarioTick: 3,
                CreateSignalHub("di.sensor"),
                activeFaults));

        Assert.Null(outcome.ScheduledFaultActive);
        Assert.Null(outcome.ConditionScenarioActive);
        Assert.Empty(activeFaults);
        Assert.Empty(outcome.Events ?? Array.Empty<SimulationConditionScheduledFaultInjectionEvent>());
    }

    private static SimulationConditionScheduledFaultInjectionContext CreateContext(
        DeterministicFaultRecoverySchedule schedule,
        long scenarioTick,
        DeterministicSignalHub signalHub,
        IDictionary<SimulationFaultKey, SimulationFaultSnapshot> activeFaults) =>
        new(
            schedule,
            scenarioTick,
            new List<OpenVisionLab.Machine.Simulation.Axis.ServoAxisComponent>(),
            signalHub,
            null,
            activeFaults,
            new SimulationFaultCommandHandler(),
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
