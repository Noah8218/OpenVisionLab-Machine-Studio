using OpenVisionLab.Machine.Core.Sequences;

namespace OpenVisionLab.Machine.Sequence.Authoring;

public enum SequenceAuthoringTargetKind
{
    DigitalInput,
    DigitalOutput,
    Axis,
    Camera,
    Subsequence
}

public sealed record SequenceAuthoringTarget(
    string Id,
    string Name,
    SequenceAuthoringTargetKind Kind,
    string DefaultParameter = "");

public sealed record SequenceStepTemplateDefinition(
    string Id,
    string Name,
    SequenceStepAction Action,
    string DefaultParameter,
    int DefaultTimeoutMs,
    IReadOnlyList<SequenceAuthoringTargetKind> TargetKinds);

public sealed record SequenceStepDraftResult(
    bool IsCreated,
    string Message,
    SequenceStepDefinition? Step)
{
    public static SequenceStepDraftResult Created(SequenceStepDefinition step) =>
        new(true, $"Created '{step.Name}'.", step);

    public static SequenceStepDraftResult Rejected(string message) =>
        new(false, message, null);
}

/// <summary>
/// Source-neutral target filtering and deterministic drafts for the first
/// Sequence authoring templates. It never compiles or executes a Sequence.
/// </summary>
public sealed class SequenceStepTemplateCatalog
{
    private static readonly IReadOnlyList<SequenceStepTemplateDefinition> Definitions =
        Array.AsReadOnly(
        [
            Template(
                "set-output-on",
                "Set output ON",
                SequenceStepAction.SetSignal,
                "true",
                0,
                SequenceAuthoringTargetKind.DigitalOutput),
            Template(
                "set-output-off",
                "Set output OFF",
                SequenceStepAction.SetSignal,
                "false",
                0,
                SequenceAuthoringTargetKind.DigitalOutput),
            Template(
                "wait-input-on",
                "Wait for input ON",
                SequenceStepAction.WaitSignal,
                "true",
                5000,
                SequenceAuthoringTargetKind.DigitalInput),
            Template(
                "wait-input-off",
                "Wait for input OFF",
                SequenceStepAction.WaitSignal,
                "false",
                5000,
                SequenceAuthoringTargetKind.DigitalInput),
            Template(
                "move-axis-home",
                "Move axis to home",
                SequenceStepAction.MoveAxis,
                "",
                0,
                SequenceAuthoringTargetKind.Axis),
            Template(
                "wait-axis-done",
                "Wait for axis done",
                SequenceStepAction.WaitAxisDone,
                "",
                5000,
                SequenceAuthoringTargetKind.Axis),
            Template(
                "trigger-camera",
                "Trigger virtual camera",
                SequenceStepAction.TriggerCamera,
                "default",
                0,
                SequenceAuthoringTargetKind.Camera),
            Template(
                "call-subsequence",
                "Call subsequence",
                SequenceStepAction.CallSubsequence,
                "",
                0,
                SequenceAuthoringTargetKind.Subsequence)
        ]);

    private static readonly IReadOnlyList<string> BooleanParameters =
        Array.AsReadOnly(["true", "false"]);
    private static readonly IReadOnlyList<string> NoParameterChoices =
        Array.Empty<string>();

    public IReadOnlyList<SequenceStepTemplateDefinition> Templates => Definitions;

    public IReadOnlyList<SequenceStepTemplateDefinition> GetAvailableTemplates(
        IEnumerable<SequenceAuthoringTarget> targets)
    {
        ArgumentNullException.ThrowIfNull(targets);
        HashSet<SequenceAuthoringTargetKind> availableKinds = targets
            .Where(IsUsableTarget)
            .Select(target => target.Kind)
            .ToHashSet();

        return Definitions
            .Where(template => template.TargetKinds.Any(availableKinds.Contains))
            .ToArray();
    }

    public IReadOnlyList<SequenceAuthoringTarget> GetTargets(
        SequenceStepAction action,
        IEnumerable<SequenceAuthoringTarget> targets)
    {
        ArgumentNullException.ThrowIfNull(targets);
        HashSet<SequenceAuthoringTargetKind> acceptedKinds = TargetKindsFor(action).ToHashSet();
        if (acceptedKinds.Count == 0)
        {
            return Array.Empty<SequenceAuthoringTarget>();
        }

        return targets
            .Where(target => IsUsableTarget(target) && acceptedKinds.Contains(target.Kind))
            .GroupBy(target => target.Id, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();
    }

    public IReadOnlyList<string> GetParameterOptions(SequenceStepAction action) =>
        action is SequenceStepAction.SetSignal or SequenceStepAction.WaitSignal
            ? BooleanParameters
            : NoParameterChoices;

    public SequenceStepDraftResult CreateDraft(
        string? templateId,
        string? stepId,
        IEnumerable<SequenceAuthoringTarget> targets)
    {
        ArgumentNullException.ThrowIfNull(targets);
        SequenceStepTemplateDefinition? template = Definitions.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, templateId, StringComparison.Ordinal));
        if (template is null)
        {
            return SequenceStepDraftResult.Rejected($"Unknown Sequence step template '{templateId}'.");
        }

        if (string.IsNullOrWhiteSpace(stepId))
        {
            return SequenceStepDraftResult.Rejected("A stable step id is required.");
        }

        SequenceAuthoringTarget? target = GetTargets(template.Action, targets)
            .FirstOrDefault(candidate => template.TargetKinds.Contains(candidate.Kind));
        if (target is null)
        {
            return SequenceStepDraftResult.Rejected(
                $"Template '{template.Name}' has no compatible authored target.");
        }

        string parameter = template.Action == SequenceStepAction.MoveAxis
            ? target.DefaultParameter
            : template.DefaultParameter;
        return SequenceStepDraftResult.Created(new SequenceStepDefinition
        {
            Id = stepId,
            Name = template.Name,
            Action = template.Action,
            TargetId = target.Id,
            Parameter = parameter,
            TimeoutMs = template.DefaultTimeoutMs
        });
    }

    private static SequenceStepTemplateDefinition Template(
        string id,
        string name,
        SequenceStepAction action,
        string defaultParameter,
        int defaultTimeoutMs,
        params SequenceAuthoringTargetKind[] targetKinds) =>
        new(id, name, action, defaultParameter, defaultTimeoutMs, Array.AsReadOnly(targetKinds));

    private static IReadOnlyList<SequenceAuthoringTargetKind> TargetKindsFor(
        SequenceStepAction action) => action switch
    {
        SequenceStepAction.SetSignal => [SequenceAuthoringTargetKind.DigitalOutput],
        SequenceStepAction.WaitSignal =>
        [
            SequenceAuthoringTargetKind.DigitalInput,
            SequenceAuthoringTargetKind.DigitalOutput
        ],
        SequenceStepAction.MoveAxis or SequenceStepAction.WaitAxisDone =>
            [SequenceAuthoringTargetKind.Axis],
        SequenceStepAction.TriggerCamera or SequenceStepAction.WaitVisionResult =>
            [SequenceAuthoringTargetKind.Camera],
        SequenceStepAction.CallSubsequence => [SequenceAuthoringTargetKind.Subsequence],
        _ => Array.Empty<SequenceAuthoringTargetKind>()
    };

    private static bool IsUsableTarget(SequenceAuthoringTarget target) =>
        !string.IsNullOrWhiteSpace(target.Id);
}
