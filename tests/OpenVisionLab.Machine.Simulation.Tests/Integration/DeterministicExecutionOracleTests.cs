using System.Text.Json;
using OpenVisionLab.Machine.Core.Channels;
using OpenVisionLab.Machine.Core.Sequences;
using OpenVisionLab.Machine.Sequence.Compilation;
using OpenVisionLab.Machine.Sequence.Runtime;
using OpenVisionLab.Machine.Simulation.Axis;
using OpenVisionLab.Machine.Simulation.Commands;
using OpenVisionLab.Machine.Simulation.Engine;
using OpenVisionLab.Machine.Simulation.Scenarios;
using OpenVisionLab.Machine.Simulation.Snapshots;
using OpenVisionLab.TestSupport;
using Xunit;

namespace OpenVisionLab.Machine.Simulation.Tests;

/// <summary>
/// Keeps semantic expectations independent from evidence-hash comparison.
/// A repeated wrong run must fail this oracle even when its hashes match itself.
/// </summary>
public sealed class DeterministicExecutionOracleTests
{
    private static readonly OracleExpectation Expected = new(
        FinalTick: 44,
        FinalSimulationTime: TimeSpan.FromMilliseconds(220),
        AxisId: "x",
        AxisPosition: 5,
        AxisState: AxisState.Idle,
        StartInputId: "di.start",
        StartInputValue: true,
        ReadyOutputId: "do.ready",
        ReadyOutputValue: true,
        SequenceId: "oracle-cycle",
        SequenceStatus: SequenceExecutionStatus.Completed,
        SequenceCurrentStep: "complete",
        SequenceTickCount: 44,
        CompletedCycleCount: 1,
        IsAutomaticRunActive: false,
        RunMode: SimulationRunMode.Paused,
        ControlOwner: SimulationControlOwner.EmbeddedSequence,
        CommandBoundaryCount: 44);

    [Fact]
    public async Task SmallFixture_MatchesIndependentSemanticOracle_AndPreservesRepeatEvidence()
    {
        var first = await RunFixtureAsync();
        var second = await RunFixtureAsync();

        var firstOracle = Compare(Expected, first);
        var secondOracle = Compare(Expected, second);

        Assert.True(firstOracle.IsMatch, firstOracle.Detail);
        Assert.True(secondOracle.IsMatch, secondOracle.Detail);
        Assert.Equal(first.Trace.TraceHash, second.Trace.TraceHash);
        Assert.Equal(first.Trace.Entries.Length, second.Trace.Entries.Length);
        Assert.Equal(first.Snapshot.TickIndex, second.Snapshot.TickIndex);
        Assert.Equal(first.Snapshot.Axes.Single(axis => axis.Id == "x").Position,
            second.Snapshot.Axes.Single(axis => axis.Id == "x").Position);

        SaveEvidence(
            "semantic-pass.json",
            new
            {
                expectation = Expected,
                first = EvidenceSummary(first, firstOracle),
                second = EvidenceSummary(second, secondOracle),
                repeatComparison = new
                {
                    IsMatch = first.Trace.TraceHash == second.Trace.TraceHash,
                    FirstTraceHash = first.Trace.TraceHash,
                    SecondTraceHash = second.Trace.TraceHash
                }
            });
    }

    [Fact]
    public async Task ChangedExpectedMeaning_FailsOracleEvenWhenRepeatedHashesMatch()
    {
        var first = await RunFixtureAsync();
        var second = await RunFixtureAsync();
        var changedMeaning = Expected with { ReadyOutputValue = false };

        var firstOracle = Compare(changedMeaning, first);
        var secondOracle = Compare(changedMeaning, second);

        Assert.False(firstOracle.IsMatch);
        Assert.Contains("do.ready", firstOracle.Detail, StringComparison.Ordinal);
        Assert.False(secondOracle.IsMatch);
        Assert.Equal(first.Trace.TraceHash, second.Trace.TraceHash);

        SaveEvidence(
            "semantic-negative-gate.json",
            new
            {
                expectation = changedMeaning,
                first = EvidenceSummary(first, firstOracle),
                second = EvidenceSummary(second, secondOracle),
                repeatComparison = new
                {
                    IsMatch = first.Trace.TraceHash == second.Trace.TraceHash,
                    FirstTraceHash = first.Trace.TraceHash,
                    SecondTraceHash = second.Trace.TraceHash
                }
            });
    }

    private static OracleComparison Compare(
        OracleExpectation expected,
        OracleObservation actual)
    {
        var mismatches = new List<string>();
        if (actual.Snapshot.TickIndex != expected.FinalTick)
        {
            mismatches.Add($"tick expected {expected.FinalTick}, actual {actual.Snapshot.TickIndex}");
        }
        if (actual.Snapshot.SimulationTime != expected.FinalSimulationTime)
        {
            mismatches.Add($"simulation time expected {expected.FinalSimulationTime}, actual {actual.Snapshot.SimulationTime}");
        }

        var axis = actual.Snapshot.Axes.SingleOrDefault(item => item.Id == expected.AxisId);
        if (axis is null)
        {
            mismatches.Add($"axis '{expected.AxisId}' is missing");
        }
        else
        {
            if (axis.State != expected.AxisState)
            {
                mismatches.Add($"axis '{expected.AxisId}' state expected {expected.AxisState}, actual {axis.State}");
            }
            if (axis.Position != expected.AxisPosition)
            {
                mismatches.Add($"axis '{expected.AxisId}' position expected {expected.AxisPosition:R}, actual {axis.Position:R}");
            }
        }

        CompareSignal(actual.Snapshot, expected.StartInputId, expected.StartInputValue, mismatches);
        CompareSignal(actual.Snapshot, expected.ReadyOutputId, expected.ReadyOutputValue, mismatches);

        var sequence = actual.Snapshot.Sequences.SingleOrDefault(item => item.SequenceId == expected.SequenceId);
        if (sequence is null)
        {
            mismatches.Add($"sequence '{expected.SequenceId}' is missing");
        }
        else
        {
            if (sequence.Status != expected.SequenceStatus)
            {
                mismatches.Add($"sequence '{expected.SequenceId}' status expected {expected.SequenceStatus}, actual {sequence.Status}");
            }
            if (sequence.CurrentStepId != expected.SequenceCurrentStep)
            {
                mismatches.Add($"sequence current step expected {expected.SequenceCurrentStep}, actual {sequence.CurrentStepId}");
            }
            if (sequence.TickCount != expected.SequenceTickCount)
            {
                mismatches.Add($"sequence tick count expected {expected.SequenceTickCount}, actual {sequence.TickCount}");
            }
        }

        if (actual.Snapshot.AutomaticRun.CompletedCycleCount != expected.CompletedCycleCount)
        {
            mismatches.Add($"completed cycles expected {expected.CompletedCycleCount}, actual {actual.Snapshot.AutomaticRun.CompletedCycleCount}");
        }
        if (actual.Snapshot.AutomaticRun.IsActive != expected.IsAutomaticRunActive)
        {
            mismatches.Add($"automatic active expected {expected.IsAutomaticRunActive}, actual {actual.Snapshot.AutomaticRun.IsActive}");
        }
        if (actual.Snapshot.RunMode != expected.RunMode)
        {
            mismatches.Add($"run mode expected {expected.RunMode}, actual {actual.Snapshot.RunMode}");
        }
        if (actual.Snapshot.ControlOwner != expected.ControlOwner)
        {
            mismatches.Add($"control owner expected {expected.ControlOwner}, actual {actual.Snapshot.ControlOwner}");
        }

        var stepResults = actual.CommandResults
            .Where(result => result.Detail == "One fixed tick was scheduled.")
            .ToArray();
        if (stepResults.Length != expected.CommandBoundaryCount)
        {
            mismatches.Add($"step command count expected {expected.CommandBoundaryCount}, actual {stepResults.Length}");
        }
        else if (stepResults[0].AppliedTick != 0
            || stepResults[^1].AppliedTick != expected.FinalTick - 1)
        {
            mismatches.Add($"step command boundaries expected 0..{expected.FinalTick - 1}, actual {stepResults[0].AppliedTick}..{stepResults[^1].AppliedTick}");
        }

        return new(mismatches.Count == 0, string.Join("; ", mismatches));
    }

    private static void CompareSignal(
        SimulationSnapshot snapshot,
        string signalId,
        bool expected,
        List<string> mismatches)
    {
        var signal = snapshot.Signals.SingleOrDefault(item => item.Id == signalId);
        if (signal is null)
        {
            mismatches.Add($"signal '{signalId}' is missing");
            return;
        }

        if (signal.Value != expected)
        {
            mismatches.Add($"signal '{signalId}' expected {expected}, actual {signal.Value}");
        }
    }

    private static object EvidenceSummary(
        OracleObservation observation,
        OracleComparison comparison) =>
        new
        {
            comparison.IsMatch,
            comparison.Detail,
            observation.Snapshot,
            observation.Trace.TraceHash,
            TraceEntryCount = observation.Trace.Entries.Length,
            CommandResults = observation.CommandResults.Select(result => new
            {
                result.AppliedTick,
                result.IsAccepted,
                result.ErrorCode,
                result.Detail
            }).ToArray()
        };

    private static void SaveEvidence(string fileName, object evidence)
    {
        var path = Path.Combine(TestStorage.RootPath, "mch-007-execution-oracle", fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(evidence, new JsonSerializerOptions
        {
            WriteIndented = true
        }));
    }

    private static async Task<OracleObservation> RunFixtureAsync()
    {
        using var engine = new FixedStepSimulationEngine(new SimulationSettings
        {
            FixedStep = TimeSpan.FromMilliseconds(5),
            TimeScale = 0.000001
        });
        await engine.StartAsync();
        try
        {
            var commandResults = new List<SimulationCommandResult>
            {
                await engine.EnqueueCommandAsync(new ConfigureRuntimeCommand(CreateRuntime())),
                await engine.EnqueueCommandAsync(new StartAutomaticRunCommand(beginRealTime: false))
            };
            for (var index = 0; index < Expected.CommandBoundaryCount; index++)
            {
                var step = await engine.EnqueueCommandAsync(new StepCommand());
                commandResults.Add(step);
                if (engine.CurrentSnapshot.Sequences.Single().Status == SequenceExecutionStatus.Completed)
                {
                    break;
                }
            }

            var snapshot = engine.CurrentSnapshot;
            var trace = engine.CreateCommandTracePackage();
            await engine.StopAsync();
            return new OracleObservation(snapshot, commandResults, trace);
        }
        finally
        {
            if (!engine.Termination.IsCompleted)
            {
                await engine.StopAsync();
            }
        }
    }

    private static SimulationRuntimeConfiguration CreateRuntime() =>
        new(
            new[]
            {
                new AxisConfiguration
                {
                    Id = "x",
                    Name = "X axis",
                    MinimumPosition = 0,
                    MaximumPosition = 100,
                    HomePosition = 0,
                    MaximumVelocity = 200,
                    Acceleration = 500,
                    Deceleration = 500
                }
            },
            new[]
            {
                new ChannelDefinition
                {
                    Id = "di.start",
                    Name = "Start input",
                    Kind = ChannelKind.DigitalInput
                },
                new ChannelDefinition
                {
                    Id = "do.ready",
                    Name = "Ready output",
                    Kind = ChannelKind.DigitalOutput
                }
            },
            new[] { CompileSequence() },
            Array.Empty<OpenVisionLab.Machine.Simulation.Camera.VirtualCameraConfiguration>(),
            new AutomaticRunConfiguration("oracle-cycle", "di.start", true, Repeat: false, RepeatDelayMilliseconds: 0));

    private static CompiledSequence CompileSequence()
    {
        var definition = new SequenceDefinition
        {
            Id = "oracle-cycle",
            Name = "Oracle cycle",
            Steps =
            {
                new SequenceStepDefinition
                {
                    Id = "wait-start",
                    Name = "Wait start",
                    Action = SequenceStepAction.WaitSignal,
                    TargetId = "di.start",
                    Parameter = "true",
                    NextStepId = "move"
                },
                new SequenceStepDefinition
                {
                    Id = "move",
                    Name = "Move x",
                    Action = SequenceStepAction.MoveAxis,
                    TargetId = "x",
                    Parameter = "5",
                    NextStepId = "wait-axis"
                },
                new SequenceStepDefinition
                {
                    Id = "wait-axis",
                    Name = "Wait x",
                    Action = SequenceStepAction.WaitAxisDone,
                    TargetId = "x",
                    TimeoutMs = 1000,
                    NextStepId = "ready"
                },
                new SequenceStepDefinition
                {
                    Id = "ready",
                    Name = "Set ready",
                    Action = SequenceStepAction.SetSignal,
                    TargetId = "do.ready",
                    Parameter = "true",
                    NextStepId = "complete"
                },
                new SequenceStepDefinition
                {
                    Id = "complete",
                    Name = "Complete",
                    Action = SequenceStepAction.Complete
                }
            }
        };
        var result = new SequenceCompiler().Compile(
            definition,
            new SequenceCompilationTargets(
                new Dictionary<string, ChannelKind>(StringComparer.Ordinal)
                {
                    ["di.start"] = ChannelKind.DigitalInput,
                    ["do.ready"] = ChannelKind.DigitalOutput
                },
                new[] { "x" }));
        Assert.True(result.IsSuccess, string.Join(Environment.NewLine, result.Errors.Select(error => error.Message)));
        return result.Sequence!;
    }

    private sealed record OracleExpectation(
        long FinalTick,
        TimeSpan FinalSimulationTime,
        string AxisId,
        double AxisPosition,
        AxisState AxisState,
        string StartInputId,
        bool StartInputValue,
        string ReadyOutputId,
        bool ReadyOutputValue,
        string SequenceId,
        SequenceExecutionStatus SequenceStatus,
        string SequenceCurrentStep,
        long SequenceTickCount,
        long CompletedCycleCount,
        bool IsAutomaticRunActive,
        SimulationRunMode RunMode,
        SimulationControlOwner ControlOwner,
        int CommandBoundaryCount);

    private sealed record OracleObservation(
        SimulationSnapshot Snapshot,
        IReadOnlyList<SimulationCommandResult> CommandResults,
        DeterministicSimulationCommandTracePackage Trace);

    private sealed record OracleComparison(bool IsMatch, string Detail);
}
