using System.Globalization;
using OpenVisionLab.Machine.Sequence.Compilation;

namespace OpenVisionLab.Machine.Sequence.Runtime;

public sealed class DeterministicSequenceExecutor
{
    private readonly CompiledSequence _sequence;
    private readonly Dictionary<string, string> _cameraAcquisitionIds = new(StringComparer.Ordinal);
    private SequenceExecutionStatus _status = SequenceExecutionStatus.Ready;
    private string? _currentStepId;
    private TimeSpan _elapsedInStep;
    private TimeSpan _totalElapsed;
    private long _tickCount;
    private SequenceExecutionError? _lastError;

    public DeterministicSequenceExecutor(CompiledSequence sequence)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        _sequence = sequence;
    }

    public SequenceExecutionResult Start()
    {
        if (_status != SequenceExecutionStatus.Ready)
        {
            return InvalidState("Sequence can start only from Ready state.");
        }

        _currentStepId = _sequence.EntryStepId;
        _status = SequenceExecutionStatus.Running;
        return Result(false, null, _currentStepId, null);
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
            return Result(false, _currentStepId, _currentStepId, error);
        }

        _tickCount++;
        _elapsedInStep += elapsed;
        _totalElapsed += elapsed;

        if (_currentStepId is null || !_sequence.TryGetStep(_currentStepId, out var step))
        {
            return Fault(Error(SequenceExecutionErrorCode.InvalidProgram, "Current step is missing from the compiled sequence."));
        }

        return step switch
        {
            WaitSignalStep waitSignal => TickWaitSignal(waitSignal, context),
            SetSignalStep setSignal => TickSetSignal(setSignal, context),
            MoveAxisStep moveAxis => TickMoveAxis(moveAxis, context),
            WaitAxisDoneStep waitAxis => TickWaitAxisDone(waitAxis, context),
            TriggerCameraStep triggerCamera => TickTriggerCamera(triggerCamera, context),
            WaitVisionResultStep waitVision => TickWaitVisionResult(waitVision, context),
            CompleteStep => Complete(),
            _ => Fault(Error(SequenceExecutionErrorCode.InvalidProgram, $"Step type '{step.GetType().Name}' is not supported."))
        };
    }

    public void Reset()
    {
        _status = SequenceExecutionStatus.Ready;
        _currentStepId = null;
        _elapsedInStep = TimeSpan.Zero;
        _totalElapsed = TimeSpan.Zero;
        _tickCount = 0;
        _lastError = null;
        _cameraAcquisitionIds.Clear();
    }

    public SequenceExecutionSnapshot CaptureSnapshot()
    {
        var index = _currentStepId is null
            ? -1
            : IndexOf(_sequence.Steps, _currentStepId);
        return new SequenceExecutionSnapshot(
            _sequence.Id,
            _status,
            _currentStepId,
            index,
            _elapsedInStep,
            _totalElapsed,
            _tickCount,
            _lastError);
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
        _cameraAcquisitionIds.Remove(step.CameraId);
        var trigger = context.TriggerCamera(step.CameraId, step.RecipeId);
        if (!trigger.IsSuccess || string.IsNullOrWhiteSpace(trigger.AcquisitionId))
        {
            return RouteOrFault(
                step,
                Error(SequenceExecutionErrorCode.CameraTriggerFailed, "Camera trigger failed.", trigger.Error));
        }

        _cameraAcquisitionIds[step.CameraId] = trigger.AcquisitionId;
        return Advance(step);
    }

    private SequenceExecutionResult TickWaitVisionResult(
        WaitVisionResultStep step,
        ISequenceRuntimeContext context)
    {
        if (!_cameraAcquisitionIds.TryGetValue(step.CameraId, out var acquisitionId))
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

    private SequenceExecutionResult CheckTimeout(CompiledSequenceStep step)
    {
        if (step.Timeout > TimeSpan.Zero && _elapsedInStep >= step.Timeout)
        {
            var milliseconds = step.Timeout.TotalMilliseconds.ToString("0.###", CultureInfo.InvariantCulture);
            return RouteOrFault(step, Error(SequenceExecutionErrorCode.StepTimedOut, $"Step timed out after {milliseconds} ms."));
        }

        return Result(false, step.Id, step.Id, null);
    }

    private SequenceExecutionResult CheckVisionTimeout(WaitVisionResultStep step)
    {
        if (_elapsedInStep >= step.Timeout)
        {
            var milliseconds = step.Timeout.TotalMilliseconds.ToString("0.###", CultureInfo.InvariantCulture);
            return RouteVisionError(
                step,
                Error(
                    SequenceExecutionErrorCode.VisionResultTimedOut,
                    $"Vision result timed out after {milliseconds} ms."));
        }

        return Result(false, step.Id, step.Id, null);
    }

    private SequenceExecutionResult AdvanceVisionSuccess(WaitVisionResultStep step)
    {
        _cameraAcquisitionIds.Remove(step.CameraId);
        return Advance(step);
    }

    private SequenceExecutionResult Advance(CompiledSequenceStep step)
    {
        if (step.NextStepId is null || !_sequence.TryGetStep(step.NextStepId, out _))
        {
            return Fault(Error(SequenceExecutionErrorCode.InvalidProgram, "Step successor is missing from the compiled sequence."));
        }

        var previous = step.Id;
        _currentStepId = step.NextStepId;
        _elapsedInStep = TimeSpan.Zero;
        return Result(true, previous, _currentStepId, null);
    }

    private SequenceExecutionResult AdvanceVisionFailure(WaitVisionResultStep step)
    {
        _cameraAcquisitionIds.Remove(step.CameraId);
        if (!_sequence.TryGetStep(step.FailureStepId, out _))
        {
            return Fault(Error(SequenceExecutionErrorCode.InvalidProgram, "Vision failure successor is missing from the compiled sequence."));
        }

        var previous = step.Id;
        _currentStepId = step.FailureStepId;
        _elapsedInStep = TimeSpan.Zero;
        return Result(true, previous, _currentStepId, null);
    }

    private SequenceExecutionResult RouteVisionError(
        WaitVisionResultStep step,
        SequenceExecutionError error)
    {
        _cameraAcquisitionIds.Remove(step.CameraId);
        return RouteOrFault(step, error);
    }

    private SequenceExecutionResult Complete()
    {
        var previous = _currentStepId;
        _status = SequenceExecutionStatus.Completed;
        _elapsedInStep = TimeSpan.Zero;
        _cameraAcquisitionIds.Clear();
        return Result(true, previous, _currentStepId, null);
    }

    private SequenceExecutionResult RouteOrFault(CompiledSequenceStep step, SequenceExecutionError error)
    {
        _lastError = error;
        if (step.ErrorStepId is null)
        {
            return Fault(error);
        }

        if (!_sequence.TryGetStep(step.ErrorStepId, out _))
        {
            return Fault(Error(SequenceExecutionErrorCode.InvalidProgram, "Step error successor is missing from the compiled sequence."));
        }

        var previous = step.Id;
        _currentStepId = step.ErrorStepId;
        _elapsedInStep = TimeSpan.Zero;
        return Result(true, previous, _currentStepId, error);
    }

    private SequenceExecutionResult Fault(SequenceExecutionError error)
    {
        _lastError = error;
        _status = SequenceExecutionStatus.Faulted;
        _cameraAcquisitionIds.Clear();
        return Result(false, _currentStepId, _currentStepId, error);
    }

    private SequenceExecutionResult InvalidState(string message)
    {
        var error = Error(SequenceExecutionErrorCode.InvalidState, message);
        return Result(false, _currentStepId, _currentStepId, error);
    }

    private SequenceExecutionError Error(
        SequenceExecutionErrorCode code,
        string message,
        SequenceContextError? contextError = null)
    {
        return new SequenceExecutionError(code, _sequence.Id, _currentStepId, message, contextError);
    }

    private SequenceExecutionResult Result(
        bool transitioned,
        string? previousStepId,
        string? currentStepId,
        SequenceExecutionError? error)
    {
        return new SequenceExecutionResult(CaptureSnapshot(), transitioned, previousStepId, currentStepId, error);
    }

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
}
