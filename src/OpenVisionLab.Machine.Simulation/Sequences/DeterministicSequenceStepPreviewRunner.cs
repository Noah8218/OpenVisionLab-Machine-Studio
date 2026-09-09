using System.Globalization;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Core.Sequences;
using OpenVisionLab.Machine.Sequence.Runtime;
using OpenVisionLab.Machine.Simulation.Axis;
using OpenVisionLab.Machine.Simulation.Commands;
using OpenVisionLab.Machine.Simulation.Compilation;
using OpenVisionLab.Machine.Simulation.Engine;
using OpenVisionLab.Machine.Simulation.Layout;
using OpenVisionLab.Machine.Simulation.Snapshots;

namespace OpenVisionLab.Machine.Simulation.Sequences;

public enum SequenceStepPreviewOutcome
{
    Completed,
    LimitReached,
    Faulted,
    Rejected
}

public sealed record SequenceStepPreviewResult(
    SequenceStepPreviewOutcome Outcome,
    SequenceStepAction Action,
    string TargetId,
    long ExecutedTicks,
    int MaximumTicks,
    SimulationSnapshot? FinalSnapshot,
    string Detail)
{
    public bool IsCompleted => Outcome == SequenceStepPreviewOutcome.Completed;
}

/// <summary>
/// Runs one authored connection step in an isolated instance of the production
/// fixed-step engine. The source project and the caller's engine are never changed.
/// </summary>
public sealed class DeterministicSequenceStepPreviewRunner
{
    public const int DefaultMaximumTicks = 2000;
    private const string PreviewSequenceId = DeterministicSequenceStepPreviewCompiler.PreviewSequenceId;
    private const string PreviewStepId = DeterministicSequenceStepPreviewCompiler.PreviewStepId;
    private const string CompleteStepId = DeterministicSequenceStepPreviewCompiler.CompleteStepId;

    private static readonly HashSet<SequenceStepAction> SupportedActions =
    [
        SequenceStepAction.MoveAxis,
        SequenceStepAction.Wait,
        SequenceStepAction.WaitAxisDone,
        SequenceStepAction.SetChannel,
        SequenceStepAction.SetSignal,
        SequenceStepAction.WaitSignal
    ];

    public async Task<SequenceStepPreviewResult> RunAsync(
        MachineProjectDocument project,
        string sequenceId,
        string stepId,
        string componentId,
        int maximumTicks = DefaultMaximumTicks,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (maximumTicks <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumTicks));
        }

        var sourceSequence = project.Sequences.FirstOrDefault(sequence =>
            string.Equals(sequence.Id, sequenceId, StringComparison.Ordinal));
        var sourceStep = sourceSequence?.Steps.FirstOrDefault(step =>
            string.Equals(step.Id, stepId, StringComparison.Ordinal));
        if (sourceStep is null)
        {
            return Rejected(SequenceStepAction.None, string.Empty, maximumTicks,
                $"Sequence step '{sequenceId}/{stepId}' was not found.");
        }

        if (!SupportedActions.Contains(sourceStep.Action))
        {
            return Rejected(sourceStep.Action, sourceStep.TargetId, maximumTicks,
                $"Action '{sourceStep.Action}' is not supported by connection preview.");
        }

        var fixedStepMilliseconds = project.Simulation?.FixedStepMilliseconds ?? 0;
        if (fixedStepMilliseconds <= 0)
        {
            return Rejected(sourceStep.Action, sourceStep.TargetId, maximumTicks,
                "Project fixed step must be positive.");
        }

        var fixedStep = TimeSpan.FromMilliseconds(fixedStepMilliseconds);
        var projectCompilation = new MachineProjectRuntimeCompiler(fixedStep).Compile(project);
        if (!projectCompilation.IsSuccess)
        {
            return Rejected(sourceStep.Action, sourceStep.TargetId, maximumTicks,
                string.Join("; ", projectCompilation.Errors.Select(error => error.Message)));
        }

        var sourceRuntime = projectCompilation.Configuration!;
        var previewCompilation = DeterministicSequenceStepPreviewCompiler.CompilePreview(
            sourceStep, sourceRuntime, componentId);
        if (!previewCompilation.IsSuccess)
        {
            return Rejected(sourceStep.Action, sourceStep.TargetId, maximumTicks,
                string.Join("; ", previewCompilation.Errors.Select(error => error.Message)));
        }

        var previewRuntime = new SimulationRuntimeConfiguration(
            sourceRuntime.Axes,
            sourceRuntime.Channels,
            [previewCompilation.Sequence!],
            sourceRuntime.Cameras,
            automaticRun: null,
            sourceRuntime.Layout,
            sourceRuntime.PickPlaceWorkpiece,
            sourceRuntime.TimeScale);

        using var engine = new FixedStepSimulationEngine(new SimulationSettings { FixedStep = fixedStep });
        await engine.StartAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var configure = await engine.EnqueueCommandAsync(
                new ConfigureRuntimeCommand(previewRuntime), cancellationToken).ConfigureAwait(false);
            if (!configure.IsAccepted)
            {
                return Rejected(sourceStep.Action, sourceStep.TargetId, maximumTicks,
                    $"Runtime configuration was rejected: {configure.Detail}");
            }

            var start = await engine.EnqueueCommandAsync(
                new StartSequenceCommand(PreviewSequenceId), cancellationToken).ConfigureAwait(false);
            if (!start.IsAccepted)
            {
                return Rejected(sourceStep.Action, sourceStep.TargetId, maximumTicks,
                    $"Sequence start was rejected: {start.Detail}");
            }

            long? completedAtTick = null;
            for (var tick = 1; tick <= maximumTicks; tick++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var step = await engine.EnqueueCommandAsync(
                    new StepCommand(), cancellationToken).ConfigureAwait(false);
                if (!step.IsAccepted)
                {
                    return new SequenceStepPreviewResult(
                        SequenceStepPreviewOutcome.Rejected,
                        sourceStep.Action,
                        sourceStep.TargetId,
                        tick - 1,
                        maximumTicks,
                        engine.CurrentSnapshot,
                        $"Fixed-step command was rejected: {step.Detail}");
                }

                var snapshot = engine.CurrentSnapshot;
                var sequence = snapshot.Sequences.FirstOrDefault(candidate =>
                    string.Equals(candidate.SequenceId, PreviewSequenceId, StringComparison.Ordinal));
                if (sequence?.Status == SequenceExecutionStatus.Faulted)
                {
                    return new SequenceStepPreviewResult(
                        SequenceStepPreviewOutcome.Faulted,
                        sourceStep.Action,
                        sourceStep.TargetId,
                        tick,
                        maximumTicks,
                        snapshot,
                        sequence.LastError?.Message ?? "The preview step faulted.");
                }

                if (sequence?.Status != SequenceExecutionStatus.Completed)
                {
                    continue;
                }

                completedAtTick ??= tick;
                var settledFault = GetSettlingFault(sourceStep, componentId, snapshot);
                if (settledFault is not null)
                {
                    return new SequenceStepPreviewResult(
                        SequenceStepPreviewOutcome.Faulted,
                        sourceStep.Action,
                        sourceStep.TargetId,
                        tick,
                        maximumTicks,
                        snapshot,
                        settledFault);
                }

                if (!HasSettled(sourceStep, componentId, snapshot, tick, completedAtTick.Value))
                {
                    continue;
                }

                return new SequenceStepPreviewResult(
                    SequenceStepPreviewOutcome.Completed,
                    sourceStep.Action,
                    sourceStep.TargetId,
                    tick,
                    maximumTicks,
                    snapshot,
                    "The isolated fixed-step preview completed.");
            }

            return new SequenceStepPreviewResult(
                SequenceStepPreviewOutcome.LimitReached,
                sourceStep.Action,
                sourceStep.TargetId,
                maximumTicks,
                maximumTicks,
                engine.CurrentSnapshot,
                string.Format(
                    CultureInfo.InvariantCulture,
                    "Preview stopped at the hard limit of {0} ticks ({1:0.###} s).",
                    maximumTicks,
                    maximumTicks * fixedStep.TotalSeconds));
        }
        finally
        {
            await engine.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static bool HasSettled(
        SequenceStepDefinition source,
        string componentId,
        SimulationSnapshot snapshot,
        long tick,
        long completedAtTick)
    {
        if (source.Action == SequenceStepAction.MoveAxis)
        {
            var axis = snapshot.Axes.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, source.TargetId, StringComparison.Ordinal));
            return axis is not null && axis.State != AxisState.Moving;
        }

        if (source.Action is not (SequenceStepAction.SetSignal or SequenceStepAction.SetChannel))
        {
            return true;
        }

        // Outputs are consumed by equipment on the following fixed tick.
        if (tick <= completedAtTick)
        {
            return false;
        }

        var component = snapshot.LayoutComponents.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, componentId, StringComparison.Ordinal));
        return component?.CylinderState is not (
            PneumaticCylinderState.Extending or PneumaticCylinderState.Retracting);
    }

    private static string? GetSettlingFault(
        SequenceStepDefinition source,
        string componentId,
        SimulationSnapshot snapshot)
    {
        if (source.Action == SequenceStepAction.MoveAxis)
        {
            var axis = snapshot.Axes.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, source.TargetId, StringComparison.Ordinal));
            if (axis is null)
            {
                return $"Axis '{source.TargetId}' was absent from the preview snapshot.";
            }

            if (axis.State is AxisState.Error or AxisState.Limited)
            {
                return $"Axis '{source.TargetId}' settled in state '{axis.State}'.";
            }
        }

        var component = snapshot.LayoutComponents.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, componentId, StringComparison.Ordinal));
        return component?.CylinderState == PneumaticCylinderState.Fault
            ? $"Cylinder '{componentId}' entered the fault state."
            : null;
    }

    private static SequenceStepPreviewResult Rejected(
        SequenceStepAction action,
        string targetId,
        int maximumTicks,
        string detail) =>
        new(
            SequenceStepPreviewOutcome.Rejected,
            action,
            targetId,
            0,
            maximumTicks,
            null,
            detail);
}
