using OpenVisionLab.Machine.Core.Sequences;

namespace OpenVisionLab.Machine.Sequence.Compilation;

public enum CompiledSequenceStepKind
{
    WaitSignal,
    SetSignal,
    MoveAxis,
    WaitAxisDone,
    TriggerCamera,
    FeedWorkpiece,
    EjectWorkpiece,
    WaitVisionResult,
    CallSubsequence,
    Complete,
    Delay,
    UnitOperation,
    BranchInspection
}

public abstract record CompiledSequenceStep(
    string Id,
    string Name,
    string? NextStepId,
    string? ErrorStepId,
    TimeSpan Timeout)
{
    public abstract CompiledSequenceStepKind Kind { get; }
}

public sealed record WaitSignalStep(
    string Id,
    string Name,
    string SignalId,
    bool ExpectedValue,
    string? NextStepId,
    string? ErrorStepId,
    TimeSpan Timeout)
    : CompiledSequenceStep(Id, Name, NextStepId, ErrorStepId, Timeout)
{
    public override CompiledSequenceStepKind Kind => CompiledSequenceStepKind.WaitSignal;
}

public sealed record SetSignalStep(
    string Id,
    string Name,
    string SignalId,
    bool Value,
    string? NextStepId,
    string? ErrorStepId)
    : CompiledSequenceStep(Id, Name, NextStepId, ErrorStepId, TimeSpan.Zero)
{
    public override CompiledSequenceStepKind Kind => CompiledSequenceStepKind.SetSignal;
}

public sealed record MoveAxisStep(
    string Id,
    string Name,
    string AxisId,
    double TargetPosition,
    string? NextStepId,
    string? ErrorStepId,
    bool WaitForCompletion = false,
    TimeSpan MotionTimeout = default)
    : CompiledSequenceStep(Id, Name, NextStepId, ErrorStepId, MotionTimeout)
{
    public override CompiledSequenceStepKind Kind => CompiledSequenceStepKind.MoveAxis;
}

public sealed record WaitAxisDoneStep(
    string Id,
    string Name,
    string AxisId,
    string? NextStepId,
    string? ErrorStepId,
    TimeSpan Timeout)
    : CompiledSequenceStep(Id, Name, NextStepId, ErrorStepId, Timeout)
{
    public override CompiledSequenceStepKind Kind => CompiledSequenceStepKind.WaitAxisDone;
}

public sealed record TriggerCameraStep(
    string Id,
    string Name,
    string CameraId,
    string RecipeId,
    string? NextStepId,
    string? ErrorStepId,
    string? WorkpieceComponentId = null)
    : CompiledSequenceStep(Id, Name, NextStepId, ErrorStepId, TimeSpan.Zero)
{
    public override CompiledSequenceStepKind Kind => CompiledSequenceStepKind.TriggerCamera;
}

public sealed record FeedWorkpieceStep(
    string Id,
    string Name,
    string WorkpieceComponentId,
    string? NextStepId,
    string? ErrorStepId)
    : CompiledSequenceStep(Id, Name, NextStepId, ErrorStepId, TimeSpan.Zero)
{
    public override CompiledSequenceStepKind Kind => CompiledSequenceStepKind.FeedWorkpiece;
}

public sealed record EjectWorkpieceStep(
    string Id,
    string Name,
    string WorkpieceComponentId,
    string? NextStepId,
    string? ErrorStepId)
    : CompiledSequenceStep(Id, Name, NextStepId, ErrorStepId, TimeSpan.Zero)
{
    public override CompiledSequenceStepKind Kind => CompiledSequenceStepKind.EjectWorkpiece;
}

public sealed record WaitVisionResultStep(
    string Id,
    string Name,
    string CameraId,
    string FailureStepId,
    string? NextStepId,
    string? ErrorStepId,
    TimeSpan Timeout)
    : CompiledSequenceStep(Id, Name, NextStepId, ErrorStepId, Timeout)
{
    public override CompiledSequenceStepKind Kind => CompiledSequenceStepKind.WaitVisionResult;
}

public sealed record CallSubsequenceStep(
    string Id,
    string Name,
    string SequenceId,
    string? NextStepId,
    string? ErrorStepId)
    : CompiledSequenceStep(Id, Name, NextStepId, ErrorStepId, TimeSpan.Zero)
{
    public override CompiledSequenceStepKind Kind => CompiledSequenceStepKind.CallSubsequence;
}

public sealed record UnitOperationStep(string Id, string Name, SequenceStepAction Action, string UnitId, string? DestinationUnitId,
    string? ConveyorId, string Route, TimeSpan Duration, string? NextStepId, string? ErrorStepId, TimeSpan Timeout)
    : CompiledSequenceStep(Id, Name, NextStepId, ErrorStepId, Timeout)
{
    public override CompiledSequenceStepKind Kind => CompiledSequenceStepKind.UnitOperation;
}

public sealed record BranchInspectionStep(string Id, string Name, string UnitId, string FailureStepId, string? NextStepId, string? ErrorStepId)
    : CompiledSequenceStep(Id, Name, NextStepId, ErrorStepId, TimeSpan.Zero)
{
    public override CompiledSequenceStepKind Kind => CompiledSequenceStepKind.BranchInspection;
}

public sealed record DelayStep(string Id, string Name, TimeSpan Duration, string? NextStepId, string? ErrorStepId)
    : CompiledSequenceStep(Id, Name, NextStepId, ErrorStepId, TimeSpan.Zero)
{
    public override CompiledSequenceStepKind Kind => CompiledSequenceStepKind.Delay;
}

public sealed record CompleteStep(string Id, string Name)
    : CompiledSequenceStep(Id, Name, null, null, TimeSpan.Zero)
{
    public override CompiledSequenceStepKind Kind => CompiledSequenceStepKind.Complete;
}

public sealed class CompiledSequence
{
    private readonly IReadOnlyDictionary<string, CompiledSequenceStep> _stepsById;

    internal CompiledSequence(
        string id,
        string name,
        TimeSpan watchdogTimeout,
        IReadOnlyList<CompiledSequenceStep> steps,
        string? unitId = null,
        int repeatCount = 1)
    {
        Id = id;
        Name = name;
        WatchdogTimeout = watchdogTimeout;
        Steps = steps;
        UnitId = unitId;
        RepeatCount = repeatCount;
        EntryStepId = steps[0].Id;
        _stepsById = steps.ToDictionary(step => step.Id, StringComparer.Ordinal);
    }

    public string Id { get; }

    public string Name { get; }
    public string? UnitId { get; }
    public int RepeatCount { get; }

    public TimeSpan WatchdogTimeout { get; }

    public string EntryStepId { get; }

    public IReadOnlyList<CompiledSequenceStep> Steps { get; }

    public bool TryGetStep(string stepId, out CompiledSequenceStep step)
    {
        return _stepsById.TryGetValue(stepId, out step!);
    }
}
