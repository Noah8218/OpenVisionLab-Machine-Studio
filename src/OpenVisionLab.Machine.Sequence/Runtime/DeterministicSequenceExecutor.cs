using System.Globalization;
using OpenVisionLab.Machine.Sequence.Compilation;

namespace OpenVisionLab.Machine.Sequence.Runtime;

public sealed class DeterministicSequenceExecutor
{
    private const int MaximumCallDepth = 64;
    private readonly CompiledSequence _rootSequence;
    private readonly IReadOnlyDictionary<string, CompiledSequence> _sequenceCatalog;
    private readonly List<ExecutionFrame> _frames = new();
    private SequenceExecutionStatus _status = SequenceExecutionStatus.Ready;
    private TimeSpan _totalElapsed;
    private long _tickCount;
    private SequenceExecutionError? _lastError;

    public DeterministicSequenceExecutor(CompiledSequence sequence)
        : this(sequence, SingleSequenceCatalog(sequence))
    {
    }

    public DeterministicSequenceExecutor(
        CompiledSequence sequence,
        IReadOnlyDictionary<string, CompiledSequence> sequenceCatalog)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        ArgumentNullException.ThrowIfNull(sequenceCatalog);

        _rootSequence = sequence;
        var catalog = new Dictionary<string, CompiledSequence>(StringComparer.Ordinal);
        foreach (var pair in sequenceCatalog)
        {
            if (string.IsNullOrWhiteSpace(pair.Key) || pair.Value is null)
            {
                throw new ArgumentException(
                    "The compiled Sequence catalog cannot contain an empty id or null value.",
                    nameof(sequenceCatalog));
            }

            catalog.Add(pair.Key, pair.Value);
        }

        catalog.TryAdd(sequence.Id, sequence);
        _sequenceCatalog = catalog;
    }

    public SequenceExecutionResult Start()
    {
        if (_status != SequenceExecutionStatus.Ready)
        {
            return InvalidState("Sequence can start only from Ready state.");
        }

        _frames.Clear();
        _frames.Add(new ExecutionFrame(_rootSequence, _rootSequence.EntryStepId, null));
        _status = SequenceExecutionStatus.Running;
        return Result(false, null, _rootSequence.Id, _rootSequence.EntryStepId, null);
    }

    public SequenceExecutionResult Abort()
    {
        if (_status != SequenceExecutionStatus.Running)
        {
            return InvalidState("Sequence can abort only while Running.");
        }

        _status = SequenceExecutionStatus.Aborted;
        ClearCameraCorrelation();
        return Result(
            false,
            CurrentFrame?.CurrentStepId,
            CurrentFrame?.Sequence.Id,
            CurrentFrame?.CurrentStepId,
            null);
    }

    public SequenceExecutionResult Retry()
    {
        if (_status != SequenceExecutionStatus.Faulted)
        {
            return InvalidState("Sequence can retry only from Faulted state.");
        }

        Reset();
        return Start();
    }

    public SequenceExecutionResult Tick(TimeSpan elapsed, ISequenceRuntimeContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (_status != SequenceExecutionStatus.Running)
        {
            return InvalidState("Sequence can tick only while Running.");
        }

        if (elapsed < TimeSpan.Zero)
        {
            var error = Error(SequenceExecutionErrorCode.InvalidElapsedTime, "Tick elapsed time cannot be negative.");
            return Result(
                false,
                CurrentFrame?.CurrentStepId,
                CurrentFrame?.Sequence.Id,
                CurrentFrame?.CurrentStepId,
                error);
        }

        _tickCount++;
        _totalElapsed += elapsed;
        foreach (var frame in _frames)
        {
            frame.TotalElapsed += elapsed;
        }

        var activeFrame = CurrentFrame;
        if (activeFrame is null || activeFrame.CurrentStepId is null
            || !activeFrame.Sequence.TryGetStep(activeFrame.CurrentStepId, out var step))
        {
            return Fault(Error(SequenceExecutionErrorCode.InvalidProgram, "Current step is missing from the compiled sequence."));
        }

        activeFrame.ElapsedInStep += elapsed;
        var execution = step switch
        {
            WaitSignalStep waitSignal => TickWaitSignal(waitSignal, context),
            SetSignalStep setSignal => TickSetSignal(setSignal, context),
            MoveAxisStep moveAxis => TickMoveAxis(moveAxis, context),
            WaitAxisDoneStep waitAxis => TickWaitAxisDone(waitAxis, context),
            TriggerCameraStep triggerCamera => TickTriggerCamera(triggerCamera, context),
            WaitVisionResultStep waitVision => TickWaitVisionResult(waitVision, context),
            CallSubsequenceStep callSubsequence => TickCallSubsequence(callSubsequence),
            CompleteStep => Complete(),
            _ => Fault(Error(SequenceExecutionErrorCode.InvalidProgram, $"Step type '{step.GetType().Name}' is not supported."))
        };

        if (_status == SequenceExecutionStatus.Running)
        {
            var watchdogFrame = _frames
                .AsEnumerable()
                .Reverse()
                .FirstOrDefault(frame =>
                    frame.Sequence.WatchdogTimeout > TimeSpan.Zero
                    && frame.TotalElapsed >= frame.Sequence.WatchdogTimeout);
            if (watchdogFrame is not null)
            {
                var milliseconds = watchdogFrame.Sequence.WatchdogTimeout.TotalMilliseconds.ToString(
                    "0.###",
                    CultureInfo.InvariantCulture);
                execution = Fault(ErrorFor(
                    SequenceExecutionErrorCode.SequenceWatchdogTimedOut,
                    watchdogFrame.Sequence.Id,
                    watchdogFrame.CurrentStepId,
                    $"Sequence watchdog timed out after {milliseconds} ms."));
            }
        }

        return execution;
    }

    public void Reset()
    {
        _status = SequenceExecutionStatus.Ready;
        _frames.Clear();
        _totalElapsed = TimeSpan.Zero;
        _tickCount = 0;
        _lastError = null;
    }

    public SequenceExecutionSnapshot CaptureSnapshot()
    {
        var frame = CurrentFrame;
        var index = frame?.CurrentStepId is { } stepId
            ? IndexOf(frame.Sequence.Steps, stepId)
            : -1;
        var nested = _frames.Count > 1;
        return new SequenceExecutionSnapshot(
            _rootSequence.Id,
            _status,
            frame?.CurrentStepId,
            index,
            frame?.ElapsedInStep ?? TimeSpan.Zero,
            _totalElapsed,
            _tickCount,
            _lastError,
            _rootSequence.WatchdogTimeout,
            nested ? frame!.Sequence.Id : null,
            nested ? _frames.Select(item => item.Sequence.Id).ToArray() : null);
    }

    private SequenceExecutionResult TickWaitSignal(WaitSignalStep step, ISequenceRuntimeContext context)
    {
        var read = context.ReadSignal(step.SignalId);
        if (!read.IsSuccess)
        {
            return RouteOrFault(step, Error(SequenceExecutionErrorCode.SignalReadFailed, "Signal read failed.", read.Error));
        }

        if (read.Value == step.ExpectedValue)
        {
            return Advance(step);
        }

        return CheckTimeout(step);
    }

    private SequenceExecutionResult TickSetSignal(SetSignalStep step, ISequenceRuntimeContext context)
    {
        var write = context.SetSignal(step.SignalId, step.Value);
        return write.IsSuccess
            ? Advance(step)
            : RouteOrFault(step, Error(SequenceExecutionErrorCode.SignalWriteFailed, "Signal write failed.", write.Error));
    }

    private SequenceExecutionResult TickMoveAxis(MoveAxisStep step, ISequenceRuntimeContext context)
    {
        var move = context.RequestAxisMove(step.AxisId, step.TargetPosition);
        return move.IsSuccess
            ? Advance(step)
            : RouteOrFault(step, Error(SequenceExecutionErrorCode.AxisMoveFailed, "Axis move request failed.", move.Error));
    }

    private SequenceExecutionResult TickWaitAxisDone(WaitAxisDoneStep step, ISequenceRuntimeContext context)
    {
        var read = context.ReadAxisMotionState(step.AxisId);
        if (!read.IsSuccess)
        {
            return RouteOrFault(step, Error(SequenceExecutionErrorCode.AxisStateReadFailed, "Axis state read failed.", read.Error));
        }

        return read.State switch
        {
            SequenceAxisMotionState.Completed => Advance(step),
            SequenceAxisMotionState.Faulted => RouteOrFault(step, Error(SequenceExecutionErrorCode.AxisFaulted, "Axis entered a faulted state.")),
            _ => CheckTimeout(step)
        };
    }

    private SequenceExecutionResult TickTriggerCamera(TriggerCameraStep step, ISequenceRuntimeContext context)
    {
        var frame = CurrentFrame!;
        frame.CameraAcquisitionIds.Remove(step.CameraId);
        var trigger = context.TriggerCamera(step.CameraId, step.RecipeId);
        if (!trigger.IsSuccess || string.IsNullOrWhiteSpace(trigger.AcquisitionId))
        {
            return RouteOrFault(
                step,
                Error(SequenceExecutionErrorCode.CameraTriggerFailed, "Camera trigger failed.", trigger.Error));
        }

        frame.CameraAcquisitionIds[step.CameraId] = trigger.AcquisitionId;
        return Advance(step);
    }

    private SequenceExecutionResult TickWaitVisionResult(
        WaitVisionResultStep step,
        ISequenceRuntimeContext context)
    {
        var frame = CurrentFrame!;
        if (!frame.CameraAcquisitionIds.TryGetValue(step.CameraId, out var acquisitionId))
        {
            return RouteOrFault(
                step,
                Error(SequenceExecutionErrorCode.VisionResultNotTriggered, "Camera has not been triggered by this sequence execution."));
        }

        var read = context.ReadVisionResult(step.CameraId, acquisitionId);
        if (!read.IsSuccess)
        {
            return RouteVisionError(
                step,
                Error(SequenceExecutionErrorCode.VisionResultReadFailed, "Vision result read failed.", read.Error));
        }

        return read.State switch
        {
            SequenceVisionResultState.Pending => CheckVisionTimeout(step),
            SequenceVisionResultState.Passed => AdvanceVisionSuccess(step),
            SequenceVisionResultState.Failed => AdvanceVisionFailure(step),
            SequenceVisionResultState.NotTriggered => RouteVisionError(
                step,
                Error(SequenceExecutionErrorCode.VisionResultNotTriggered, "Camera has not been triggered.")),
            SequenceVisionResultState.Faulted => RouteVisionError(
                step,
                Error(SequenceExecutionErrorCode.VisionResultFaulted, "Vision result entered a faulted state.")),
            _ => RouteVisionError(
                step,
                Error(SequenceExecutionErrorCode.VisionResultReadFailed, "Vision result state is not supported."))
        };
    }

    private SequenceExecutionResult TickCallSubsequence(CallSubsequenceStep step)
    {
        if (!_sequenceCatalog.TryGetValue(step.SequenceId, out var child))
        {
            return RouteOrFault(
                step,
                Error(SequenceExecutionErrorCode.InvalidProgram, $"Subsequence '{step.SequenceId}' is not present in the compiled catalog."));
        }

        if (_frames.Count >= MaximumCallDepth)
        {
            return RouteOrFault(
                step,
                Error(
                    SequenceExecutionErrorCode.SubsequenceDepthExceeded,
                    $"Subsequence call depth exceeded the deterministic limit of {MaximumCallDepth}."));
        }

        var parent = CurrentFrame!;
        if (step.NextStepId is null || !parent.Sequence.TryGetStep(step.NextStepId, out _))
        {
            return Fault(Error(SequenceExecutionErrorCode.InvalidProgram, "Subsequence caller successor is missing from the compiled sequence."));
        }

        var previousSequenceId = parent.Sequence.Id;
        var previousStepId = parent.CurrentStepId;
        parent.ElapsedInStep = TimeSpan.Zero;
        _frames.Add(new ExecutionFrame(child, child.EntryStepId, step.NextStepId));
        return Result(
            true,
            previousStepId,
            previousSequenceId,
            child.EntryStepId,
            null,
            child.Id);
    }

    private SequenceExecutionResult CheckTimeout(CompiledSequenceStep step)
    {
        var frame = CurrentFrame!;
        if (step.Timeout > TimeSpan.Zero && frame.ElapsedInStep >= step.Timeout)
        {
            var milliseconds = step.Timeout.TotalMilliseconds.ToString("0.###", CultureInfo.InvariantCulture);
            return RouteOrFault(step, Error(SequenceExecutionErrorCode.StepTimedOut, $"Step timed out after {milliseconds} ms."));
        }

        return Result(false, step.Id, frame.Sequence.Id, step.Id, null);
    }

    private SequenceExecutionResult CheckVisionTimeout(WaitVisionResultStep step)
    {
        var frame = CurrentFrame!;
        if (frame.ElapsedInStep >= step.Timeout)
        {
            var milliseconds = step.Timeout.TotalMilliseconds.ToString("0.###", CultureInfo.InvariantCulture);
            return RouteVisionError(
                step,
                Error(
                    SequenceExecutionErrorCode.VisionResultTimedOut,
                    $"Vision result timed out after {milliseconds} ms."));
        }

        return Result(false, step.Id, frame.Sequence.Id, step.Id, null);
    }

    private SequenceExecutionResult AdvanceVisionSuccess(WaitVisionResultStep step)
    {
        CurrentFrame!.CameraAcquisitionIds.Remove(step.CameraId);
        return Advance(step);
    }

    private SequenceExecutionResult Advance(CompiledSequenceStep step)
    {
        var frame = CurrentFrame!;
        if (step.NextStepId is null || !frame.Sequence.TryGetStep(step.NextStepId, out _))
        {
            return Fault(Error(SequenceExecutionErrorCode.InvalidProgram, "Step successor is missing from the compiled sequence."));
        }

        var previous = step.Id;
        frame.CurrentStepId = step.NextStepId;
        frame.ElapsedInStep = TimeSpan.Zero;
        return Result(true, previous, frame.Sequence.Id, frame.CurrentStepId, null);
    }

    private SequenceExecutionResult AdvanceVisionFailure(WaitVisionResultStep step)
    {
        var frame = CurrentFrame!;
        frame.CameraAcquisitionIds.Remove(step.CameraId);
        if (!frame.Sequence.TryGetStep(step.FailureStepId, out _))
        {
            return Fault(Error(SequenceExecutionErrorCode.InvalidProgram, "Vision failure successor is missing from the compiled sequence."));
        }

        var previous = step.Id;
        frame.CurrentStepId = step.FailureStepId;
        frame.ElapsedInStep = TimeSpan.Zero;
        return Result(true, previous, frame.Sequence.Id, frame.CurrentStepId, null);
    }

    private SequenceExecutionResult RouteVisionError(
        WaitVisionResultStep step,
        SequenceExecutionError error)
    {
        CurrentFrame!.CameraAcquisitionIds.Remove(step.CameraId);
        return RouteOrFault(step, error);
    }

    private SequenceExecutionResult Complete()
    {
        var completedFrame = CurrentFrame!;
        var previous = completedFrame.CurrentStepId;
        completedFrame.ElapsedInStep = TimeSpan.Zero;
        completedFrame.CameraAcquisitionIds.Clear();
        if (_frames.Count == 1)
        {
            _status = SequenceExecutionStatus.Completed;
            return Result(true, previous, completedFrame.Sequence.Id, completedFrame.CurrentStepId, null);
        }

        var completedSequenceId = completedFrame.Sequence.Id;
        var returnStepId = completedFrame.ReturnStepId;
        _frames.RemoveAt(_frames.Count - 1);
        var parent = CurrentFrame!;
        if (returnStepId is null || !parent.Sequence.TryGetStep(returnStepId, out _))
        {
            return Fault(Error(SequenceExecutionErrorCode.InvalidProgram, "Subsequence return successor is missing from the compiled sequence."));
        }

        parent.CurrentStepId = returnStepId;
        parent.ElapsedInStep = TimeSpan.Zero;
        return Result(
            true,
            previous,
            completedSequenceId,
            parent.CurrentStepId,
            null,
            parent.Sequence.Id);
    }

    private SequenceExecutionResult RouteOrFault(CompiledSequenceStep step, SequenceExecutionError error)
    {
        var frame = CurrentFrame!;
        _lastError = error;
        if (step.ErrorStepId is null)
        {
            return Fault(error);
        }

        if (!frame.Sequence.TryGetStep(step.ErrorStepId, out _))
        {
            return Fault(Error(SequenceExecutionErrorCode.InvalidProgram, "Step error successor is missing from the compiled sequence."));
        }

        var previous = step.Id;
        frame.CurrentStepId = step.ErrorStepId;
        frame.ElapsedInStep = TimeSpan.Zero;
        return Result(true, previous, frame.Sequence.Id, frame.CurrentStepId, error);
    }

    private SequenceExecutionResult Fault(SequenceExecutionError error)
    {
        _lastError = error;
        if (_frames.Count > 1)
        {
            var child = CurrentFrame!;
            var parent = _frames[^2];
            if (parent.CurrentStepId is { } callStepId
                && parent.Sequence.TryGetStep(callStepId, out var callStep)
                && callStep.ErrorStepId is { } parentErrorStepId
                && parent.Sequence.TryGetStep(parentErrorStepId, out _))
            {
                var previousStepId = child.CurrentStepId;
                var previousSequenceId = child.Sequence.Id;
                child.CameraAcquisitionIds.Clear();
                _frames.RemoveAt(_frames.Count - 1);
                parent.CurrentStepId = parentErrorStepId;
                parent.ElapsedInStep = TimeSpan.Zero;
                return Result(
                    true,
                    previousStepId,
                    previousSequenceId,
                    parent.CurrentStepId,
                    error,
                    parent.Sequence.Id);
            }
        }

        _status = SequenceExecutionStatus.Faulted;
        ClearCameraCorrelation();
        return Result(
            false,
            CurrentFrame?.CurrentStepId,
            CurrentFrame?.Sequence.Id,
            CurrentFrame?.CurrentStepId,
            error);
    }

    private SequenceExecutionResult InvalidState(string message)
    {
        var error = Error(SequenceExecutionErrorCode.InvalidState, message);
        return Result(
            false,
            CurrentFrame?.CurrentStepId,
            CurrentFrame?.Sequence.Id,
            CurrentFrame?.CurrentStepId,
            error);
    }

    private SequenceExecutionError Error(
        SequenceExecutionErrorCode code,
        string message,
        SequenceContextError? contextError = null) =>
        ErrorFor(
            code,
            CurrentFrame?.Sequence.Id ?? _rootSequence.Id,
            CurrentFrame?.CurrentStepId,
            message,
            contextError);

    private static SequenceExecutionError ErrorFor(
        SequenceExecutionErrorCode code,
        string sequenceId,
        string? stepId,
        string message,
        SequenceContextError? contextError = null) =>
        new(code, sequenceId, stepId, message, contextError);

    private SequenceExecutionResult Result(
        bool transitioned,
        string? previousStepId,
        string? previousSequenceId,
        string? currentStepId,
        SequenceExecutionError? error,
        string? currentSequenceId = null) =>
        new(
            CaptureSnapshot(),
            transitioned,
            previousStepId,
            currentStepId,
            error,
            previousSequenceId,
            currentSequenceId ?? CurrentFrame?.Sequence.Id);

    private void ClearCameraCorrelation()
    {
        foreach (var frame in _frames)
        {
            frame.CameraAcquisitionIds.Clear();
        }
    }

    private static IReadOnlyDictionary<string, CompiledSequence> SingleSequenceCatalog(
        CompiledSequence sequence)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        return new Dictionary<string, CompiledSequence>(StringComparer.Ordinal)
        {
            [sequence.Id] = sequence
        };
    }

    private ExecutionFrame? CurrentFrame => _frames.Count == 0 ? null : _frames[^1];

    private static int IndexOf(IReadOnlyList<CompiledSequenceStep> steps, string stepId)
    {
        for (var index = 0; index < steps.Count; index++)
        {
            if (string.Equals(steps[index].Id, stepId, StringComparison.Ordinal))
            {
                return index;
            }
        }

        return -1;
    }

    private sealed class ExecutionFrame
    {
        public ExecutionFrame(CompiledSequence sequence, string currentStepId, string? returnStepId)
        {
            Sequence = sequence;
            CurrentStepId = currentStepId;
            ReturnStepId = returnStepId;
        }

        public CompiledSequence Sequence { get; }

        public string? CurrentStepId { get; set; }

        public string? ReturnStepId { get; }

        public TimeSpan ElapsedInStep { get; set; }

        public TimeSpan TotalElapsed { get; set; }

        public Dictionary<string, string> CameraAcquisitionIds { get; } = new(StringComparer.Ordinal);
    }
}
