using OpenVisionLab.Machine.Core.Channels;
using OpenVisionLab.Machine.Core.Sequences;
using OpenVisionLab.Machine.IO.Channels;
using OpenVisionLab.Machine.Sequence.Compilation;
using OpenVisionLab.Machine.Sequence.Runtime;
using OpenVisionLab.Machine.Simulation.Commands;
using OpenVisionLab.Machine.Simulation.Engine;
using Xunit;

namespace OpenVisionLab.Machine.Simulation.Tests;

public sealed class SimulationAutomaticRunCommandHandlerTests
{
    [Fact]
    public void Apply_StartWritesInputStartsSequenceAndReturnsOrderedStateEvents()
    {
        var executor = CreateReadyExecutor("sequence");
        var creation = DeterministicSignalHub.Create(new[]
        {
            new ChannelDefinition
            {
                Id = "di.start",
                Name = "Start Input",
                Kind = ChannelKind.DigitalInput
            }
        });
        Assert.True(creation.IsAccepted, creation.ErrorCode.ToString());

        var handler = new SimulationAutomaticRunCommandHandler();
        var outcome = handler.Apply(
            new StartAutomaticRunCommand(beginRealTime: false),
            CreateContext(
                new AutomaticRunConfiguration("sequence", "di.start", true, false, 0),
                creation.Hub!,
                new Dictionary<string, DeterministicSequenceExecutor>
                {
                    ["sequence"] = executor
                }));

        Assert.True(outcome.Result.IsAccepted, outcome.Result.Detail);
        Assert.Equal(SequenceExecutionStatus.Running, executor.CaptureSnapshot().Status);
        Assert.True(creation.Hub!.ReadDigitalSignal("di.start").Value);
        Assert.Equal(SimulationRunMode.Paused, outcome.State!.RunMode);
        Assert.Equal(SimulationControlOwner.EmbeddedSequence, outcome.State.ControlOwner);
        Assert.Equal("sequence", outcome.State.ActiveSequenceId);
        Assert.True(outcome.State.AutomaticRunActive);
        Assert.Equal(
            new[] { "DigitalInputChanged", "SequenceStarted", "AutomaticRunStarted" },
            outcome.Events!.Select(item => item.Code).ToArray());
    }

    [Fact]
    public void Apply_RejectsWhenAutomaticRunIsNotConfiguredWithoutState()
    {
        var handler = new SimulationAutomaticRunCommandHandler();
        var outcome = handler.Apply(
            new StartAutomaticRunCommand(),
            CreateContext(
                null,
                CreateHub(),
                new Dictionary<string, DeterministicSequenceExecutor>()));

        Assert.False(outcome.Result.IsAccepted);
        Assert.Equal(SimulationCommandErrorCode.AutomaticRunNotConfigured, outcome.Result.ErrorCode);
        Assert.Null(outcome.State);
        Assert.Null(outcome.Events);
    }

    [Fact]
    public void Apply_RejectsWhenActiveSequenceIsAlreadyRunning()
    {
        var executor = CreateReadyExecutor("sequence");
        Assert.True(executor.Start().IsSuccess);
        var handler = new SimulationAutomaticRunCommandHandler();
        var outcome = handler.Apply(
            new StartAutomaticRunCommand(),
            CreateContext(
                new AutomaticRunConfiguration("sequence", null, true, false, 0),
                CreateHub(),
                new Dictionary<string, DeterministicSequenceExecutor>
                {
                    ["sequence"] = executor
                },
                new SimulationAutomaticRunCommandState(
                    SimulationRunMode.Paused,
                    SimulationControlOwner.EmbeddedSequence,
                    0,
                    "sequence",
                    AutomaticRunActive: false,
                    AutomaticRunWaitingForRepeat: false,
                    AutomaticRunCompletedCycleCount: 0,
                    AutomaticRunRemainingDelayTicks: 0)));

        Assert.False(outcome.Result.IsAccepted);
        Assert.Equal(SimulationCommandErrorCode.AutomaticRunStartRejected, outcome.Result.ErrorCode);
        Assert.Contains("already running", outcome.Result.Detail);
    }

    private static SimulationAutomaticRunCommandContext CreateContext(
        AutomaticRunConfiguration? configuration,
        DeterministicSignalHub signalHub,
        IReadOnlyDictionary<string, DeterministicSequenceExecutor> executors,
        SimulationAutomaticRunCommandState? state = null) =>
        new(
            configuration,
            state ?? new SimulationAutomaticRunCommandState(
                SimulationRunMode.Paused,
                SimulationControlOwner.Definition,
                0,
                null,
                AutomaticRunActive: false,
                AutomaticRunWaitingForRepeat: false,
                AutomaticRunCompletedCycleCount: 0,
                AutomaticRunRemainingDelayTicks: 0),
            signalHub,
            executors,
            17,
            TimeSpan.FromMilliseconds(85));

    private static DeterministicSignalHub CreateHub()
    {
        var creation = DeterministicSignalHub.Create(Array.Empty<ChannelDefinition>());
        Assert.True(creation.IsAccepted, creation.ErrorCode.ToString());
        return creation.Hub!;
    }

    private static DeterministicSequenceExecutor CreateReadyExecutor(string id)
    {
        var compilation = new SequenceCompiler().Compile(
            new SequenceDefinition
            {
                Id = id,
                Name = id,
                Steps =
                {
                    new SequenceStepDefinition
                    {
                        Id = "complete",
                        Name = "Complete",
                        Action = SequenceStepAction.Complete
                    }
                }
            });
        return new DeterministicSequenceExecutor(compilation.Sequence!);
    }
}
