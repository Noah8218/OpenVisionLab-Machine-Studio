using System.Globalization;
using OpenVisionLab.Machine.Core.Channels;
using OpenVisionLab.Machine.Core.Sequences;

namespace OpenVisionLab.Machine.Sequence.Compilation;

public sealed class SequenceCompiler
{
    public SequenceCompilationResult Compile(
        SequenceDefinition? definition,
        SequenceCompilationTargets? targets = null)
    {
        if (definition is null)
        {
            return Failure(SequenceCompilationErrorCode.DefinitionRequired, null, "Sequence definition is required.");
        }

        var errors = new List<SequenceCompilationError>();
        if (string.IsNullOrWhiteSpace(definition.Id))
        {
            AddError(errors, SequenceCompilationErrorCode.SequenceIdRequired, null, "Sequence id is required.");
        }

        if (definition.Steps is null || definition.Steps.Count == 0)
        {
            AddError(errors, SequenceCompilationErrorCode.NoSteps, null, "Sequence must contain at least one step.");
            return new SequenceCompilationResult(null, errors.AsReadOnly());
        }

        var sourceSteps = definition.Steps;
        var knownStepIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in sourceSteps)
        {
            if (source is null || string.IsNullOrWhiteSpace(source.Id))
            {
                AddError(errors, SequenceCompilationErrorCode.StepIdRequired, source?.Id, "Every sequence step requires an id.");
                continue;
            }

            if (!knownStepIds.Add(source.Id))
            {
                AddError(errors, SequenceCompilationErrorCode.DuplicateStepId, source.Id, $"Step id '{source.Id}' is duplicated.");
            }
        }

        var compiledSteps = new List<CompiledSequenceStep>(sourceSteps.Count);
        for (var index = 0; index < sourceSteps.Count; index++)
        {
            var source = sourceSteps[index];
            if (source is null || string.IsNullOrWhiteSpace(source.Id))
            {
                continue;
            }

            var nextStepId = NormalizeId(source.NextStepId);
            var errorStepId = NormalizeId(source.ErrorStepId);
            var failureStepId = NormalizeId(source.FailureStepId);
            var actionName = source.Action.ToString();
            var isComplete = string.Equals(actionName, "Complete", StringComparison.Ordinal)
                || source.Action == SequenceStepAction.None;
            var isWaitVisionResult = string.Equals(actionName, "WaitVisionResult", StringComparison.Ordinal);

            if (isComplete)
            {
                if (nextStepId is not null || errorStepId is not null || failureStepId is not null)
                {
                    AddError(errors, SequenceCompilationErrorCode.CompleteStepHasTransition, source.Id, "Complete step cannot declare a transition.");
                }
            }
            else if (nextStepId is null)
            {
                nextStepId = index + 1 < sourceSteps.Count ? NormalizeId(sourceSteps[index + 1]?.Id) : null;
                if (nextStepId is null)
                {
                    AddError(errors, SequenceCompilationErrorCode.MissingSuccessor, source.Id, "Non-terminal step requires a successor.");
                }
            }

            ValidateTransitionTarget(source.Id, nextStepId, knownStepIds, SequenceCompilationErrorCode.NextStepNotFound, "next", errors);
            ValidateTransitionTarget(source.Id, errorStepId, knownStepIds, SequenceCompilationErrorCode.ErrorStepNotFound, "error", errors);
            ValidateTransitionTarget(source.Id, failureStepId, knownStepIds, SequenceCompilationErrorCode.FailureStepNotFound, "failure", errors);

            if (isWaitVisionResult && failureStepId is null)
            {
                AddError(errors, SequenceCompilationErrorCode.FailureStepRequired, source.Id, "WaitVisionResult requires failureStepId.");
            }
            else if (!isWaitVisionResult && failureStepId is not null)
            {
                AddError(errors, SequenceCompilationErrorCode.FailureStepNotAllowed, source.Id, "failureStepId is supported only by WaitVisionResult.");
            }

            ValidateExpectedStateCheckpoint(source, errors);

            var compiled = CompileStep(source, actionName, nextStepId, errorStepId, failureStepId, targets, errors);
            if (compiled is not null)
            {
                compiledSteps.Add(compiled);
            }
        }

        if (errors.Count != 0 || compiledSteps.Count != sourceSteps.Count)
        {
            return new SequenceCompilationResult(null, errors.AsReadOnly());
        }

        return new SequenceCompilationResult(
            new CompiledSequence(definition.Id, definition.Name, compiledSteps.AsReadOnly()),
            Array.Empty<SequenceCompilationError>());
    }

    private static CompiledSequenceStep? CompileStep(
        SequenceStepDefinition source,
        string actionName,
        string? nextStepId,
        string? errorStepId,
        string? failureStepId,
        SequenceCompilationTargets? targets,
        List<SequenceCompilationError> errors)
    {
        var name = string.IsNullOrWhiteSpace(source.Name) ? source.Id : source.Name;

        return actionName switch
        {
            "WaitSignal" => CompileWaitSignal(source, name, nextStepId, errorStepId, targets, errors),
            "SetSignal" => CompileSetSignal(source, name, nextStepId, errorStepId, targets, errors),
            nameof(SequenceStepAction.SetChannel) => CompileSetSignal(source, name, nextStepId, errorStepId, targets, errors),
            nameof(SequenceStepAction.MoveAxis) => CompileMoveAxis(source, name, nextStepId, errorStepId, targets, errors),
            "WaitAxisDone" => CompileWaitAxisDone(source, name, nextStepId, errorStepId, targets, errors),
            nameof(SequenceStepAction.TriggerCamera) => CompileTriggerCamera(source, name, nextStepId, errorStepId, targets, errors),
            "WaitVisionResult" => CompileWaitVisionResult(source, name, nextStepId, errorStepId, failureStepId, targets, errors),
            nameof(SequenceStepAction.Wait) => CompileLegacyWait(source, name, nextStepId, errorStepId, targets, errors),
            "Complete" => CompileComplete(source, name, errors),
            nameof(SequenceStepAction.None) => CompileComplete(source, name, errors),
            _ => Unsupported(source, errors)
        };
    }

    private static void ValidateExpectedStateCheckpoint(
        SequenceStepDefinition source,
        List<SequenceCompilationError> errors)
    {
        bool hasTarget = !string.IsNullOrWhiteSpace(source.ExpectedTargetId);
        bool hasState = !string.IsNullOrWhiteSpace(source.ExpectedState);
        if (hasState && !hasTarget)
        {
            AddError(
                errors,
                SequenceCompilationErrorCode.ExpectedTargetIdRequired,
                source.Id,
                "expectedTargetId is required when expectedState is set.");
        }
        if (hasTarget && !hasState)
        {
            AddError(
                errors,
                SequenceCompilationErrorCode.ExpectedStateRequired,
                source.Id,
                "expectedState is required when expectedTargetId is set.");
        }
    }

    private static CompiledSequenceStep? CompileWaitSignal(
        SequenceStepDefinition source,
        string name,
        string? nextStepId,
        string? errorStepId,
        SequenceCompilationTargets? targets,
        List<SequenceCompilationError> errors)
    {
        var targetId = RequireTarget(source, errors);
        var expectedValue = ParseBoolean(source, errors);
        var timeout = ParseWaitTimeout(source, errors);
        if (targetId is not null)
        {
            ValidateReadableDigitalSignal(source.Id, targetId, targets, errors);
        }

        return targetId is not null && expectedValue.HasValue && timeout.HasValue
            ? new WaitSignalStep(source.Id, name, targetId, expectedValue.Value, nextStepId, errorStepId, timeout.Value)
            : null;
    }

    private static CompiledSequenceStep? CompileSetSignal(
        SequenceStepDefinition source,
        string name,
        string? nextStepId,
        string? errorStepId,
        SequenceCompilationTargets? targets,
        List<SequenceCompilationError> errors)
    {
        var targetId = RequireTarget(source, errors);
        var value = ParseBoolean(source, errors);
        ValidateNoTimeout(source, errors);
        if (targetId is not null)
        {
            ValidateWritableDigitalSignal(source.Id, targetId, targets, errors);
        }

        return targetId is not null && value.HasValue && source.TimeoutMs == 0
            ? new SetSignalStep(source.Id, name, targetId, value.Value, nextStepId, errorStepId)
            : null;
    }

    private static CompiledSequenceStep? CompileMoveAxis(
        SequenceStepDefinition source,
        string name,
        string? nextStepId,
        string? errorStepId,
        SequenceCompilationTargets? targets,
        List<SequenceCompilationError> errors)
    {
        var targetId = RequireTarget(source, errors);
        var targetPosition = ParseFiniteDouble(source, errors);
        ValidateNoTimeout(source, errors);
        if (targetId is not null)
        {
            ValidateAxis(source.Id, targetId, targets, errors);
        }

        return targetId is not null && targetPosition.HasValue && source.TimeoutMs == 0
            ? new MoveAxisStep(source.Id, name, targetId, targetPosition.Value, nextStepId, errorStepId)
            : null;
    }

    private static CompiledSequenceStep? CompileWaitAxisDone(
        SequenceStepDefinition source,
        string name,
        string? nextStepId,
        string? errorStepId,
        SequenceCompilationTargets? targets,
        List<SequenceCompilationError> errors)
    {
        var targetId = RequireTarget(source, errors);
        if (!string.IsNullOrWhiteSpace(source.Parameter)
            && !string.Equals(source.Parameter.Trim(), "axisDone", StringComparison.OrdinalIgnoreCase))
        {
            AddError(errors, SequenceCompilationErrorCode.UnexpectedParameter, source.Id, "WaitAxisDone parameter must be empty or 'axisDone'.");
        }

        var timeout = ParseWaitTimeout(source, errors);
        if (targetId is not null)
        {
            ValidateAxis(source.Id, targetId, targets, errors);
        }

        return targetId is not null
            && timeout.HasValue
            && (string.IsNullOrWhiteSpace(source.Parameter) || string.Equals(source.Parameter.Trim(), "axisDone", StringComparison.OrdinalIgnoreCase))
                ? new WaitAxisDoneStep(source.Id, name, targetId, nextStepId, errorStepId, timeout.Value)
                : null;
    }

    private static CompiledSequenceStep? CompileLegacyWait(
        SequenceStepDefinition source,
        string name,
        string? nextStepId,
        string? errorStepId,
        SequenceCompilationTargets? targets,
        List<SequenceCompilationError> errors)
    {
        return string.IsNullOrWhiteSpace(source.Parameter)
            || string.Equals(source.Parameter.Trim(), "axisDone", StringComparison.OrdinalIgnoreCase)
                ? CompileWaitAxisDone(source, name, nextStepId, errorStepId, targets, errors)
                : CompileWaitSignal(source, name, nextStepId, errorStepId, targets, errors);
    }

    private static CompiledSequenceStep? CompileTriggerCamera(
        SequenceStepDefinition source,
        string name,
        string? nextStepId,
        string? errorStepId,
        SequenceCompilationTargets? targets,
        List<SequenceCompilationError> errors)
    {
        var cameraId = RequireTarget(source, errors);
        var recipeId = source.Parameter?.Trim();
        if (string.IsNullOrWhiteSpace(recipeId))
        {
            AddError(errors, SequenceCompilationErrorCode.RecipeIdRequired, source.Id, "TriggerCamera requires a recipe id parameter.");
            recipeId = null;
        }

        ValidateNoTimeout(source, errors);
        if (cameraId is not null)
        {
            ValidateCamera(source.Id, cameraId, targets, errors);
        }

        return cameraId is not null && recipeId is not null && source.TimeoutMs == 0
            ? new TriggerCameraStep(source.Id, name, cameraId, recipeId, nextStepId, errorStepId)
            : null;
    }

    private static CompiledSequenceStep? CompileWaitVisionResult(
        SequenceStepDefinition source,
        string name,
        string? nextStepId,
        string? errorStepId,
        string? failureStepId,
        SequenceCompilationTargets? targets,
        List<SequenceCompilationError> errors)
    {
        var cameraId = RequireTarget(source, errors);
        var parameterIsEmpty = string.IsNullOrWhiteSpace(source.Parameter);
        if (!parameterIsEmpty)
        {
            AddError(errors, SequenceCompilationErrorCode.UnexpectedParameter, source.Id, "WaitVisionResult parameter must be empty.");
        }

        var timeout = ParsePositiveTimeout(source, errors);
        if (cameraId is not null)
        {
            ValidateCamera(source.Id, cameraId, targets, errors);
        }

        return cameraId is not null
            && failureStepId is not null
            && parameterIsEmpty
            && timeout.HasValue
                ? new WaitVisionResultStep(
                    source.Id,
                    name,
                    cameraId,
                    failureStepId,
                    nextStepId,
                    errorStepId,
                    timeout.Value)
                : null;
    }

    private static CompiledSequenceStep? CompileComplete(
        SequenceStepDefinition source,
        string name,
        List<SequenceCompilationError> errors)
    {
        var valid = true;
        if (!string.IsNullOrWhiteSpace(source.TargetId))
        {
            AddError(errors, SequenceCompilationErrorCode.UnexpectedTargetId, source.Id, "Complete step cannot declare targetId.");
            valid = false;
        }

        if (!string.IsNullOrWhiteSpace(source.Parameter))
        {
            AddError(errors, SequenceCompilationErrorCode.UnexpectedParameter, source.Id, "Complete step cannot declare parameter.");
            valid = false;
        }

        if (source.TimeoutMs != 0)
        {
            AddError(errors, SequenceCompilationErrorCode.InvalidTimeout, source.Id, "Complete step timeoutMs must be zero.");
            valid = false;
        }

        return valid ? new CompleteStep(source.Id, name) : null;
    }

    private static CompiledSequenceStep? Unsupported(
        SequenceStepDefinition source,
        List<SequenceCompilationError> errors)
    {
        AddError(errors, SequenceCompilationErrorCode.UnsupportedAction, source.Id, $"Action '{source.Action}' is not supported by the embedded runtime.");
        return null;
    }

    private static string? RequireTarget(SequenceStepDefinition source, List<SequenceCompilationError> errors)
    {
        if (string.IsNullOrWhiteSpace(source.TargetId))
        {
            AddError(errors, SequenceCompilationErrorCode.TargetIdRequired, source.Id, "Step targetId is required.");
            return null;
        }

        return source.TargetId;
    }

    private static bool? ParseBoolean(SequenceStepDefinition source, List<SequenceCompilationError> errors)
    {
        var parameter = source.Parameter?.Trim();
        if (bool.TryParse(parameter, out var value))
        {
            return value;
        }

        if (string.Equals(parameter, "1", StringComparison.Ordinal))
        {
            return true;
        }

        if (string.Equals(parameter, "0", StringComparison.Ordinal))
        {
            return false;
        }

        AddError(errors, SequenceCompilationErrorCode.InvalidBooleanParameter, source.Id, "Parameter must be 'true', 'false', '1', or '0'.");
        return null;
    }

    private static double? ParseFiniteDouble(SequenceStepDefinition source, List<SequenceCompilationError> errors)
    {
        if (double.TryParse(
                source.Parameter?.Trim(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var value)
            && double.IsFinite(value))
        {
            return value;
        }

        AddError(errors, SequenceCompilationErrorCode.InvalidNumericParameter, source.Id, "Parameter must be a finite invariant-culture number.");
        return null;
    }

    private static TimeSpan? ParseWaitTimeout(SequenceStepDefinition source, List<SequenceCompilationError> errors)
    {
        if (source.TimeoutMs < 0)
        {
            AddError(errors, SequenceCompilationErrorCode.InvalidTimeout, source.Id, "timeoutMs cannot be negative; zero disables the timeout.");
            return null;
        }

        return TimeSpan.FromMilliseconds(source.TimeoutMs);
    }

    private static TimeSpan? ParsePositiveTimeout(SequenceStepDefinition source, List<SequenceCompilationError> errors)
    {
        if (source.TimeoutMs <= 0)
        {
            AddError(errors, SequenceCompilationErrorCode.InvalidTimeout, source.Id, "WaitVisionResult timeoutMs must be positive.");
            return null;
        }

        return TimeSpan.FromMilliseconds(source.TimeoutMs);
    }

    private static void ValidateNoTimeout(SequenceStepDefinition source, List<SequenceCompilationError> errors)
    {
        if (source.TimeoutMs != 0)
        {
            AddError(errors, SequenceCompilationErrorCode.InvalidTimeout, source.Id, "Immediate step timeoutMs must be zero.");
        }
    }

    private static void ValidateReadableDigitalSignal(
        string stepId,
        string signalId,
        SequenceCompilationTargets? targets,
        List<SequenceCompilationError> errors)
    {
        if (targets is null)
        {
            return;
        }

        if (!targets.TryGetChannelKind(signalId, out var kind))
        {
            AddError(errors, SequenceCompilationErrorCode.UnknownSignal, stepId, $"Signal '{signalId}' is not declared by the project.");
        }
        else if (kind is not ChannelKind.DigitalInput and not ChannelKind.DigitalOutput)
        {
            AddError(errors, SequenceCompilationErrorCode.InvalidSignalKind, stepId, $"Signal '{signalId}' is not digital.");
        }
    }

    private static void ValidateWritableDigitalSignal(
        string stepId,
        string signalId,
        SequenceCompilationTargets? targets,
        List<SequenceCompilationError> errors)
    {
        if (targets is null)
        {
            return;
        }

        if (!targets.TryGetChannelKind(signalId, out var kind))
        {
            AddError(errors, SequenceCompilationErrorCode.UnknownSignal, stepId, $"Signal '{signalId}' is not declared by the project.");
        }
        else if (kind != ChannelKind.DigitalOutput)
        {
            AddError(errors, SequenceCompilationErrorCode.InvalidSignalKind, stepId, $"Signal '{signalId}' is not a digital output.");
        }
    }

    private static void ValidateAxis(
        string stepId,
        string axisId,
        SequenceCompilationTargets? targets,
        List<SequenceCompilationError> errors)
    {
        if (targets is not null && !targets.ContainsAxis(axisId))
        {
            AddError(errors, SequenceCompilationErrorCode.UnknownAxis, stepId, $"Axis '{axisId}' is not declared by the project.");
        }
    }

    private static void ValidateCamera(
        string stepId,
        string cameraId,
        SequenceCompilationTargets? targets,
        List<SequenceCompilationError> errors)
    {
        if (targets is not null && !targets.ContainsCamera(cameraId))
        {
            AddError(errors, SequenceCompilationErrorCode.UnknownCamera, stepId, $"Camera '{cameraId}' is not declared by the project.");
        }
    }

    private static void ValidateTransitionTarget(
        string sourceStepId,
        string? targetStepId,
        HashSet<string> knownStepIds,
        SequenceCompilationErrorCode code,
        string label,
        List<SequenceCompilationError> errors)
    {
        if (targetStepId is not null && !knownStepIds.Contains(targetStepId))
        {
            AddError(errors, code, sourceStepId, $"The {label} step '{targetStepId}' does not exist.");
        }
    }

    private static string? NormalizeId(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static SequenceCompilationResult Failure(
        SequenceCompilationErrorCode code,
        string? stepId,
        string message)
    {
        return new SequenceCompilationResult(null, new[] { new SequenceCompilationError(code, stepId, message) });
    }

    private static void AddError(
        List<SequenceCompilationError> errors,
        SequenceCompilationErrorCode code,
        string? stepId,
        string message)
    {
        errors.Add(new SequenceCompilationError(code, stepId, message));
    }
}
