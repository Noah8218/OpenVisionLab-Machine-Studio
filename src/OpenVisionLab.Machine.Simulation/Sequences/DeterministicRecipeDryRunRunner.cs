using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Core.Sequences;
using OpenVisionLab.Machine.Sequence.Runtime;
using OpenVisionLab.Machine.Simulation.Commands;
using OpenVisionLab.Machine.Simulation.Compilation;
using OpenVisionLab.Machine.Simulation.Engine;
using OpenVisionLab.Machine.Simulation.Scenarios;
using OpenVisionLab.Machine.Simulation.Snapshots;

namespace OpenVisionLab.Machine.Simulation.Sequences;

public enum RecipeDryRunOutcome
{
    Completed,
    CompletedWithIssue,
    CompletedWithMismatch,
    LimitReached,
    Faulted,
    Rejected
}

public sealed record RecipeDryRunIssue(
    string StepId,
    long Tick,
    string Code,
    string Detail);

public sealed record RecipeDryRunStepCheckpoint(
    string TargetId,
    string ExpectedState,
    string ActualState,
    bool IsPassed,
    string Detail);

public sealed record RecipeDryRunCheckpointMismatch(
    string StepId,
    long Tick,
    string TargetId,
    string ExpectedState,
    string ActualState,
    string Detail);

public sealed record RecipeDryRunStepTrace(
    string StepId,
    string Name,
    SequenceStepAction Action,
    long StartedTick,
    long EndedTick,
    bool HasIssue,
    SimulationSnapshot BoundarySnapshot,
    RecipeDryRunStepCheckpoint? Checkpoint)
{
    public bool HasCheckpoint => Checkpoint is not null;
    public bool HasCheckpointMismatch => Checkpoint is { IsPassed: false };
}

public sealed record RecipeDryRunResult(
    RecipeDryRunOutcome Outcome,
    string SequenceId,
    string SequenceName,
    long ExecutedTicks,
    int MaximumTicks,
    IReadOnlyList<RecipeDryRunStepTrace> Timeline,
    RecipeDryRunIssue? FirstIssue,
    RecipeDryRunCheckpointMismatch? FirstCheckpointMismatch,
    SimulationSnapshot? FinalSnapshot,
    string Detail)
{
    public bool IsCompleted => Outcome is RecipeDryRunOutcome.Completed
        or RecipeDryRunOutcome.CompletedWithIssue
        or RecipeDryRunOutcome.CompletedWithMismatch;
}

/// <summary>
/// Executes one complete authored sequence in an isolated production engine and
/// reduces its snapshots to a bounded step timeline.
/// </summary>
public sealed class DeterministicRecipeDryRunRunner
{
    public const int DefaultMaximumTicks = 4000;

    public async Task<RecipeDryRunResult> RunAsync(
        MachineProjectDocument project,
        string sequenceId,
        int maximumTicks = DefaultMaximumTicks,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (maximumTicks <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumTicks));
        }

        var definition = project.Sequences.FirstOrDefault(sequence =>
            string.Equals(sequence.Id, sequenceId, StringComparison.Ordinal));
        if (definition is null)
        {
            return Rejected(sequenceId, string.Empty, maximumTicks, $"Sequence '{sequenceId}' was not found.");
        }

        var fixedStepMilliseconds = project.Simulation?.FixedStepMilliseconds ?? 0;
        if (fixedStepMilliseconds <= 0)
        {
            return Rejected(sequenceId, definition.Name, maximumTicks, "Project fixed step must be positive.");
        }

        var fixedStep = TimeSpan.FromMilliseconds(fixedStepMilliseconds);
        var compilation = new MachineProjectRuntimeCompiler(fixedStep).Compile(project);
        if (!compilation.IsSuccess)
        {
            return Rejected(
                sequenceId,
                definition.Name,
                maximumTicks,
                string.Join("; ", compilation.Errors.Select(error => error.Message)));
        }

        var sourceRuntime = compilation.Configuration!;
        var runtime = new SimulationRuntimeConfiguration(
            sourceRuntime.Axes,
            sourceRuntime.Channels,
            sourceRuntime.Sequences,
            sourceRuntime.Cameras,
            automaticRun: null,
            sourceRuntime.Layout,
            sourceRuntime.PickPlaceWorkpiece);
        var steps = definition.Steps.ToDictionary(step => step.Id, StringComparer.Ordinal);
        var timeline = new List<RecipeDryRunStepTrace>();
        RecipeDryRunIssue? firstIssue = null;

        using var engine = new FixedStepSimulationEngine(new SimulationSettings { FixedStep = fixedStep });
        await engine.StartAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var configure = await engine.EnqueueCommandAsync(
                new ConfigureRuntimeCommand(runtime), cancellationToken).ConfigureAwait(false);
            if (!configure.IsAccepted)
            {
                return Rejected(
                    sequenceId,
                    definition.Name,
                    maximumTicks,
                    $"Runtime configuration was rejected: {configure.Detail}");
            }

            var start = await engine.EnqueueCommandAsync(
                new StartSequenceCommand(sequenceId), cancellationToken).ConfigureAwait(false);
            if (!start.IsAccepted)
            {
                return Rejected(
                    sequenceId,
                    definition.Name,
                    maximumTicks,
                    $"Sequence start was rejected: {start.Detail}");
            }

            var sequence = FindSequence(engine.CurrentSnapshot, sequenceId);
            var activeStepId = sequence?.CurrentStepId;
            var activeStepStart = 0L;
            for (var tick = 1; tick <= maximumTicks; tick++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var step = await engine.EnqueueCommandAsync(
                    new StepCommand(), cancellationToken).ConfigureAwait(false);
                if (!step.IsAccepted)
                {
                    firstIssue ??= new RecipeDryRunIssue(
                        activeStepId ?? string.Empty,
                        tick - 1,
                        step.ErrorCode.ToString(),
                        step.Detail ?? "Fixed-step command was rejected.");
                    AddTrace(
                        timeline,
                        steps,
                        activeStepId,
                        activeStepStart,
                        tick - 1,
                        firstIssue,
                        engine.CurrentSnapshot);
                    return Result(
                        RecipeDryRunOutcome.Rejected,
                        definition,
                        tick - 1,
                        maximumTicks,
                        timeline,
                        firstIssue,
                        engine.CurrentSnapshot,
                        firstIssue.Detail);
                }

                var snapshot = engine.CurrentSnapshot;
                sequence = FindSequence(snapshot, sequenceId);
                if (sequence?.LastError is { } error && firstIssue is null)
                {
                    firstIssue = new RecipeDryRunIssue(
                        error.StepId ?? activeStepId ?? string.Empty,
                        tick,
                        error.Code.ToString(),
                        error.Message);
                }

                if (!string.Equals(activeStepId, sequence?.CurrentStepId, StringComparison.Ordinal))
                {
                    AddTrace(timeline, steps, activeStepId, activeStepStart, tick, firstIssue, snapshot);
                    activeStepId = sequence?.CurrentStepId;
                    activeStepStart = tick;
                }

                if (sequence?.Status == SequenceExecutionStatus.Faulted)
                {
                    AddTrace(timeline, steps, activeStepId, activeStepStart, tick, firstIssue, snapshot);
                    return Result(
                        RecipeDryRunOutcome.Faulted,
                        definition,
                        tick,
                        maximumTicks,
                        timeline,
                        firstIssue,
                        snapshot,
                        firstIssue?.Detail ?? "The recipe dry run faulted.");
                }

                if (sequence?.Status == SequenceExecutionStatus.Completed)
                {
                    AddTrace(timeline, steps, activeStepId, activeStepStart, tick, firstIssue, snapshot);
                    RecipeDryRunCheckpointMismatch? mismatch = FindFirstCheckpointMismatch(timeline);
                    return Result(
                        firstIssue is not null
                            ? RecipeDryRunOutcome.CompletedWithIssue
                            : mismatch is not null
                                ? RecipeDryRunOutcome.CompletedWithMismatch
                                : RecipeDryRunOutcome.Completed,
                        definition,
                        tick,
                        maximumTicks,
                        timeline,
                        firstIssue,
                        snapshot,
                        firstIssue is not null
                            ? "The recipe reached Complete after routing an issue."
                            : mismatch is not null
                                ? "The recipe reached Complete with an expected-state mismatch."
                                : "The isolated recipe dry run completed.");
                }
            }

            firstIssue ??= new RecipeDryRunIssue(
                activeStepId ?? string.Empty,
                maximumTicks,
                RecipeDryRunOutcome.LimitReached.ToString(),
                $"Recipe dry run stopped at the hard limit of {maximumTicks} ticks.");
            AddTrace(
                timeline,
                steps,
                activeStepId,
                activeStepStart,
                maximumTicks,
                firstIssue,
                engine.CurrentSnapshot);
            return Result(
                RecipeDryRunOutcome.LimitReached,
                definition,
                maximumTicks,
                maximumTicks,
                timeline,
                firstIssue,
                engine.CurrentSnapshot,
                firstIssue.Detail);
        }
        finally
        {
            await engine.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static SequenceExecutionSnapshot? FindSequence(
        SimulationSnapshot snapshot,
        string sequenceId) =>
        snapshot.Sequences.FirstOrDefault(sequence =>
            string.Equals(sequence.SequenceId, sequenceId, StringComparison.Ordinal));

    private static void AddTrace(
        ICollection<RecipeDryRunStepTrace> timeline,
        IReadOnlyDictionary<string, SequenceStepDefinition> steps,
        string? stepId,
        long startedTick,
        long endedTick,
        RecipeDryRunIssue? issue,
        SimulationSnapshot boundarySnapshot)
    {
        if (stepId is null || timeline.Any(trace =>
                string.Equals(trace.StepId, stepId, StringComparison.Ordinal)
                && trace.StartedTick == startedTick))
        {
            return;
        }

        steps.TryGetValue(stepId, out var step);
        RecipeDryRunStepCheckpoint? checkpoint = EvaluateCheckpoint(step, boundarySnapshot);
        timeline.Add(new RecipeDryRunStepTrace(
            stepId,
            string.IsNullOrWhiteSpace(step?.Name) ? stepId : step.Name,
            step?.Action ?? SequenceStepAction.None,
            startedTick,
            endedTick,
            string.Equals(issue?.StepId, stepId, StringComparison.Ordinal),
            boundarySnapshot,
            checkpoint));
    }

    private static RecipeDryRunStepCheckpoint? EvaluateCheckpoint(
        SequenceStepDefinition? step,
        SimulationSnapshot snapshot)
    {
        if (string.IsNullOrWhiteSpace(step?.ExpectedTargetId)
            || string.IsNullOrWhiteSpace(step.ExpectedState))
        {
            return null;
        }

        string targetId = step.ExpectedTargetId.Trim();
        string expected = step.ExpectedState.Trim();
        string actual = DeterministicScenarioAssertionEvaluator.ResolveEquipmentState(snapshot, targetId)
            ?? "Unavailable";
        bool passed = string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
        return new RecipeDryRunStepCheckpoint(
            targetId,
            expected,
            actual,
            passed,
            passed
                ? $"Equipment '{targetId}' matched expected state '{expected}'."
                : $"Equipment '{targetId}' expected state '{expected}', observed '{actual}'.");
    }

    private static RecipeDryRunCheckpointMismatch? FindFirstCheckpointMismatch(
        IEnumerable<RecipeDryRunStepTrace> timeline)
    {
        RecipeDryRunStepTrace? trace = timeline.FirstOrDefault(item => item.HasCheckpointMismatch);
        return trace?.Checkpoint is not { } checkpoint
            ? null
            : new RecipeDryRunCheckpointMismatch(
                trace.StepId,
                trace.EndedTick,
                checkpoint.TargetId,
                checkpoint.ExpectedState,
                checkpoint.ActualState,
                checkpoint.Detail);
    }

    private static RecipeDryRunResult Result(
        RecipeDryRunOutcome outcome,
        SequenceDefinition definition,
        long executedTicks,
        int maximumTicks,
        IEnumerable<RecipeDryRunStepTrace> timeline,
        RecipeDryRunIssue? firstIssue,
        SimulationSnapshot? finalSnapshot,
        string detail)
    {
        RecipeDryRunStepTrace[] traces = timeline.ToArray();
        return new(
            outcome,
            definition.Id,
            definition.Name,
            executedTicks,
            maximumTicks,
            traces,
            firstIssue,
            FindFirstCheckpointMismatch(traces),
            finalSnapshot,
            detail);
    }

    private static RecipeDryRunResult Rejected(
        string sequenceId,
        string sequenceName,
        int maximumTicks,
        string detail) =>
        new(
            RecipeDryRunOutcome.Rejected,
            sequenceId,
            sequenceName,
            0,
            maximumTicks,
            Array.Empty<RecipeDryRunStepTrace>(),
            null,
            null,
            null,
            detail);
}
