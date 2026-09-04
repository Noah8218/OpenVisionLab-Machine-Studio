using OpenVisionLab.Machine.Core.Sequences;

namespace OpenVisionLab.Machine.Sequence.Authoring;

public enum SequenceEditErrorCode
{
    None,
    DefinitionRequired,
    StepRequired,
    StepIdRequired,
    DuplicateStepId,
    StepNotFound,
    LinearSequenceRequired,
    TerminalStepRequired,
    TerminalStepCannotMove,
    TerminalStepCannotDelete,
    MoveOutsideSequence
}

public sealed record SequenceEditResult(
    bool IsAccepted,
    SequenceEditErrorCode ErrorCode,
    string Message,
    SequenceStepDefinition? Step = null)
{
    public static SequenceEditResult Accepted(string message, SequenceStepDefinition step) =>
        new(true, SequenceEditErrorCode.None, message, step);

    public static SequenceEditResult Rejected(
        SequenceEditErrorCode errorCode,
        string message,
        SequenceStepDefinition? step = null) =>
        new(false, errorCode, message, step);
}

/// <summary>
/// Deterministic mutations for the first list-style Sequence editor. Structural
/// edits are intentionally limited to a single linear success path; explicit
/// error/failure branches remain editable as fields but are not silently
/// rewritten by list operations.
/// </summary>
public sealed class SequenceDefinitionEditor
{
    public SequenceEditResult InsertBeforeTerminal(
        SequenceDefinition? definition,
        SequenceStepDefinition? step)
    {
        SequenceEditResult? precondition = ValidateLinearEdit(definition, step);
        if (precondition is not null)
        {
            return precondition;
        }

        var steps = definition!.Steps;
        int insertIndex = steps.Count - 1;
        steps.Insert(insertIndex, step!);
        RebuildLinearTransitions(steps);
        return SequenceEditResult.Accepted($"Inserted step '{step!.Id}'.", step);
    }

    public SequenceEditResult Delete(
        SequenceDefinition? definition,
        string? stepId)
    {
        SequenceEditResult? precondition = ValidateLinearDefinition(definition);
        if (precondition is not null)
        {
            return precondition;
        }

        SequenceStepDefinition? step = definition!.Steps.FirstOrDefault(item =>
            string.Equals(item.Id, stepId, StringComparison.Ordinal));
        if (step is null)
        {
            return SequenceEditResult.Rejected(
                SequenceEditErrorCode.StepNotFound,
                $"Step '{stepId}' was not found.");
        }

        if (IsTerminal(step))
        {
            return SequenceEditResult.Rejected(
                SequenceEditErrorCode.TerminalStepCannotDelete,
                "The terminal Complete step cannot be deleted by the list editor.",
                step);
        }

        definition.Steps.Remove(step);
        RebuildLinearTransitions(definition.Steps);
        return SequenceEditResult.Accepted($"Deleted step '{step.Id}'.", step);
    }

    public SequenceEditResult Move(
        SequenceDefinition? definition,
        string? stepId,
        int offset)
    {
        SequenceEditResult? precondition = ValidateLinearDefinition(definition);
        if (precondition is not null)
        {
            return precondition;
        }

        int currentIndex = definition!.Steps.FindIndex(item =>
            string.Equals(item.Id, stepId, StringComparison.Ordinal));
        if (currentIndex < 0)
        {
            return SequenceEditResult.Rejected(
                SequenceEditErrorCode.StepNotFound,
                $"Step '{stepId}' was not found.");
        }

        SequenceStepDefinition step = definition.Steps[currentIndex];
        if (IsTerminal(step))
        {
            return SequenceEditResult.Rejected(
                SequenceEditErrorCode.TerminalStepCannotMove,
                "The terminal Complete step remains last in the list editor.",
                step);
        }

        int targetIndex = currentIndex + offset;
        int lastEditableIndex = definition.Steps.Count - 2;
        if (targetIndex < 0 || targetIndex > lastEditableIndex)
        {
            return SequenceEditResult.Rejected(
                SequenceEditErrorCode.MoveOutsideSequence,
                "The step is already at the requested list boundary.",
                step);
        }

        definition.Steps.RemoveAt(currentIndex);
        definition.Steps.Insert(targetIndex, step);
        RebuildLinearTransitions(definition.Steps);
        return SequenceEditResult.Accepted($"Moved step '{step.Id}'.", step);
    }

    public static bool IsStrictLinear(SequenceDefinition? definition)
    {
        if (definition?.Steps is not { Count: > 0 } steps
            || !IsTerminal(steps[^1])
            || steps.Take(steps.Count - 1).Any(IsTerminal))
        {
            return false;
        }

        for (var index = 0; index < steps.Count; index++)
        {
            SequenceStepDefinition step = steps[index];
            if (!string.IsNullOrWhiteSpace(step.ErrorStepId)
                || !string.IsNullOrWhiteSpace(step.FailureStepId))
            {
                return false;
            }

            string? expectedNext = index + 1 < steps.Count ? steps[index + 1].Id : null;
            if (!string.IsNullOrWhiteSpace(step.NextStepId)
                && !string.Equals(step.NextStepId, expectedNext, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Keeps editable authored fields compatible with the selected step action
    /// and the currently available project targets.
    /// </summary>
    public static void NormalizeStep(
        SequenceStepDefinition definition,
        SequenceStepTemplateCatalog templateCatalog,
        IReadOnlyList<SequenceAuthoringTarget> authoringTargets)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(templateCatalog);
        ArgumentNullException.ThrowIfNull(authoringTargets);

        if (definition.Action == SequenceStepAction.Complete)
        {
            definition.TargetId = string.Empty;
            definition.Parameter = string.Empty;
            definition.TimeoutMs = 0;
            definition.NextStepId = null;
            definition.ErrorStepId = null;
            definition.FailureStepId = null;
            return;
        }

        IReadOnlyList<SequenceAuthoringTarget> targets = templateCatalog.GetTargets(
            definition.Action,
            authoringTargets);
        if (targets.Count == 0)
        {
            definition.TargetId = string.Empty;
        }
        else if (!targets.Any(target =>
                     string.Equals(target.Id, definition.TargetId, StringComparison.Ordinal)))
        {
            definition.TargetId = targets[0].Id;
        }

        IReadOnlyList<string> parameterOptions = templateCatalog.GetParameterOptions(definition.Action);
        if (parameterOptions.Count != 0
            && !parameterOptions.Contains(definition.Parameter, StringComparer.OrdinalIgnoreCase))
        {
            definition.Parameter = parameterOptions[0];
        }

        if (definition.Action == SequenceStepAction.MoveAxis
            && (!double.TryParse(
                    definition.Parameter,
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out double position)
                || !double.IsFinite(position)))
        {
            definition.Parameter = FindDefaultParameter(definition.TargetId, targets) ?? "0";
        }

        if (definition.Action == SequenceStepAction.TriggerCamera
            && string.IsNullOrWhiteSpace(definition.Parameter))
        {
            definition.Parameter = "default";
        }

        if (definition.Action is SequenceStepAction.SetSignal
            or SequenceStepAction.MoveAxis
            or SequenceStepAction.TriggerCamera
            or SequenceStepAction.CallSubsequence)
        {
            definition.TimeoutMs = 0;
        }

        if (definition.Action is SequenceStepAction.WaitAxisDone
            or SequenceStepAction.WaitVisionResult
            or SequenceStepAction.CallSubsequence)
        {
            definition.Parameter = string.Empty;
        }

        if (definition.Action == SequenceStepAction.WaitVisionResult && definition.TimeoutMs <= 0)
        {
            definition.TimeoutMs = 1000;
        }

        if (definition.Action != SequenceStepAction.WaitVisionResult)
        {
            definition.FailureStepId = null;
        }
    }

    public static string? FindDefaultParameter(
        string? targetId,
        IReadOnlyList<SequenceAuthoringTarget> authoringTargets)
    {
        ArgumentNullException.ThrowIfNull(authoringTargets);
        return authoringTargets.FirstOrDefault(target =>
            string.Equals(target.Id, targetId, StringComparison.Ordinal))?.DefaultParameter;
    }

    private static SequenceEditResult? ValidateLinearEdit(
        SequenceDefinition? definition,
        SequenceStepDefinition? step)
    {
        SequenceEditResult? definitionError = ValidateLinearDefinition(definition);
        if (definitionError is not null)
        {
            return definitionError;
        }

        if (step is null)
        {
            return SequenceEditResult.Rejected(
                SequenceEditErrorCode.StepRequired,
                "A step definition is required.");
        }

        if (string.IsNullOrWhiteSpace(step.Id))
        {
            return SequenceEditResult.Rejected(
                SequenceEditErrorCode.StepIdRequired,
                "The new step requires a stable id.",
                step);
        }

        if (definition!.Steps.Any(item => string.Equals(item.Id, step.Id, StringComparison.Ordinal)))
        {
            return SequenceEditResult.Rejected(
                SequenceEditErrorCode.DuplicateStepId,
                $"Step id '{step.Id}' already exists.",
                step);
        }

        if (IsTerminal(step))
        {
            return SequenceEditResult.Rejected(
                SequenceEditErrorCode.TerminalStepRequired,
                "The existing terminal Complete step is retained; add a non-terminal step.",
                step);
        }

        if (!string.IsNullOrWhiteSpace(step.ErrorStepId)
            || !string.IsNullOrWhiteSpace(step.FailureStepId))
        {
            return SequenceEditResult.Rejected(
                SequenceEditErrorCode.LinearSequenceRequired,
                "A new list step cannot introduce an error or failure branch during insertion.",
                step);
        }

        return null;
    }

    private static SequenceEditResult? ValidateLinearDefinition(SequenceDefinition? definition)
    {
        if (definition is null)
        {
            return SequenceEditResult.Rejected(
                SequenceEditErrorCode.DefinitionRequired,
                "A sequence definition is required.");
        }

        if (definition.Steps.Count == 0 || !IsTerminal(definition.Steps[^1]))
        {
            return SequenceEditResult.Rejected(
                SequenceEditErrorCode.TerminalStepRequired,
                "The list editor requires a final Complete step.");
        }

        if (!IsStrictLinear(definition))
        {
            return SequenceEditResult.Rejected(
                SequenceEditErrorCode.LinearSequenceRequired,
                "Structural list edits require one linear success path without error/failure branches.");
        }

        return null;
    }

    private static void RebuildLinearTransitions(IReadOnlyList<SequenceStepDefinition> steps)
    {
        for (var index = 0; index < steps.Count; index++)
        {
            SequenceStepDefinition step = steps[index];
            step.NextStepId = IsTerminal(step)
                ? null
                : steps[index + 1].Id;
        }
    }

    private static bool IsTerminal(SequenceStepDefinition step) =>
        step.Action is SequenceStepAction.Complete or SequenceStepAction.None;
}
