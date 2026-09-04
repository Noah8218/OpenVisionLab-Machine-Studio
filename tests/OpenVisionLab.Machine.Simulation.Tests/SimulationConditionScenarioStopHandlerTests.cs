using OpenVisionLab.Machine.Core.Channels;
using OpenVisionLab.Machine.IO.Channels;
using OpenVisionLab.Machine.Sequence.Runtime;
using OpenVisionLab.Machine.Simulation.Commands;
using OpenVisionLab.Machine.Simulation.Engine;
using OpenVisionLab.Machine.Simulation.Faults;
using OpenVisionLab.Machine.Simulation.Scenarios;
using Xunit;

namespace OpenVisionLab.Machine.Simulation.Tests;

public sealed class SimulationConditionScenarioStopHandlerTests
{
    [Fact]
    public void Apply_StopsScenarioAndClearsScheduledFaultBeforeStopEvent()
    {
        var activeFaults = ActiveFaults();
        var signalHub = CreateSignalHub("di.sensor");
        Assert.True(signalHub.SetDigitalInputOverride("di.sensor", false).IsAccepted);
        var outcome = new SimulationConditionScenarioStopHandler().Apply(
            new StopConditionScenarioCommand(),
            CreateContext(
                scenarioActive: true,
                scheduledFaultActive: true,
                profile: CreateProfile(),
                signalHub,
                activeFaults));

        Assert.True(outcome.Result.IsAccepted, outcome.Result.Detail);
        Assert.False(outcome.State!.ScenarioActive);
        Assert.False(outcome.State.RecoveryState.ScheduledFaultActive);
        Assert.Empty(activeFaults);
        Assert.Equal(
            new[] { "FaultCleared", "ConditionScenarioStopped" },
            outcome.Events!.Select(item => item.Code).ToArray());
    }

    [Fact]
    public void Apply_StopsScenarioWhenScheduledFaultClearIsRejected()
    {
        var outcome = new SimulationConditionScenarioStopHandler().Apply(
            new StopConditionScenarioCommand(),
            CreateContext(
                scenarioActive: true,
                scheduledFaultActive: true,
                profile: CreateProfile(),
                signalHub: CreateSignalHub("di.sensor"),
                activeFaults: new Dictionary<SimulationFaultKey, SimulationFaultSnapshot>()));

        Assert.True(outcome.Result.IsAccepted, outcome.Result.Detail);
        Assert.False(outcome.State!.ScenarioActive);
        Assert.True(outcome.State.RecoveryState.ScheduledFaultActive);
        Assert.Equal(
            new[] { "ConditionFaultClearRejected", "ConditionScenarioStopped" },
            outcome.Events!.Select(item => item.Code).ToArray());
    }

    [Fact]
    public void Apply_InactiveScenarioRejectsWithoutRecoveryOrEvents()
    {
        var outcome = new SimulationConditionScenarioStopHandler().Apply(
            new StopConditionScenarioCommand(),
            CreateContext(
                scenarioActive: false,
                scheduledFaultActive: false,
                profile: CreateProfile(),
                signalHub: CreateSignalHub("di.sensor"),
                activeFaults: new Dictionary<SimulationFaultKey, SimulationFaultSnapshot>()));

        Assert.False(outcome.Result.IsAccepted);
        Assert.Equal(
            SimulationCommandErrorCode.ConditionScenarioNotActive,
            outcome.Result.ErrorCode);
        Assert.Null(outcome.State);
        Assert.Empty(outcome.Events ?? Array.Empty<SimulationConditionScenarioStopEvent>());
    }

    private static SimulationConditionScenarioStopContext CreateContext(
        bool scenarioActive,
        bool scheduledFaultActive,
        DeterministicConditionScenarioProfile profile,
        DeterministicSignalHub signalHub,
        IDictionary<SimulationFaultKey, SimulationFaultSnapshot> activeFaults) =>
        new(
            scenarioActive,
            profile,
            4,
            new SimulationConditionScheduledFaultRecoveryContext(
                profile.FaultRecovery,
                RestartSequence: false,
                CommandId: null,
                new SimulationConditionScheduledFaultRecoveryState(
                    scheduledFaultActive,
                    InterruptedAutomaticRun: false,
                    ActiveSequenceId: null,
                    SimulationControlOwner.Definition,
                    AutomaticRunActive: false,
                    AutomaticRunWaitingForRepeat: false,
                    AutomaticRunRemainingDelayTicks: 0),
                new List<OpenVisionLab.Machine.Simulation.Axis.ServoAxisComponent>(),
                signalHub,
                null,
                activeFaults,
                new Dictionary<string, DeterministicSequenceExecutor>(),
                new SimulationFaultCommandHandler(),
                7,
                TimeSpan.FromMilliseconds(35)),
            new SimulationConditionScheduledFaultRecoveryHandler());

    private static DeterministicConditionScenarioProfile CreateProfile() =>
        new(
            DeterministicConditionScenarioProfile.CurrentSchemaVersion,
            "stop",
            "Stop",
            "Stop test",
            "equipment-1",
            7,
            10,
            FaultRecovery: new DeterministicFaultRecoverySchedule(
                SimulationFaultKind.StuckDigitalInput,
                "di.sensor",
                InjectTick: 2,
                HoldTicks: 2));

    private static Dictionary<SimulationFaultKey, SimulationFaultSnapshot> ActiveFaults() =>
        new()
        {
            [new(SimulationFaultKind.StuckDigitalInput, "di.sensor")] = new(
                SimulationFaultKind.StuckDigitalInput,
                "di.sensor",
                false,
                3,
                TimeSpan.FromMilliseconds(15))
        };

    private static DeterministicSignalHub CreateSignalHub(params string[] channelIds) =>
        DeterministicSignalHub.Create(channelIds.Select(Channel).ToArray()).Hub!;

    private static ChannelDefinition Channel(string id) => new()
    {
        Id = id,
        Name = id,
        Kind = ChannelKind.DigitalInput
    };
}
