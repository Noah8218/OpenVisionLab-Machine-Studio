using OpenVisionLab;
using OpenVisionLab.Machine.Core.Sequences;

namespace OpenVisionLab.MachineStudio.ViewModel;

public sealed record SequenceTaskRoute(SequenceStepEditorItem Source, SequenceTaskGroupViewModel? Target, string Label, string TargetId, bool IsValid, bool IsTerminal)
{
    public string PropertyName { get; init; } = string.Empty;
    public string Caption => IsTerminal ? Label : $"{Label} → {(Target is null ? TargetId : Target.RangeText + ". " + Target.Title)}"
        + (IsValid ? string.Empty : " · " + OpenVisionLanguageService.T("Process.InvalidRoute"));
}

public sealed class SequenceTaskLaneViewModel
{
    internal SequenceTaskLaneViewModel(SequenceDefinition? sequence, string name, IReadOnlyList<SequenceStepEditorItem> steps,
        IReadOnlyList<SequenceTaskGroupViewModel> groups, IReadOnlyList<SequenceValidationIssue> issues)
    {
        Definition = sequence;
        Name = name;
        Steps = steps;
        Groups = groups;
        Issues = issues;
        bool uniqueIds = steps.All(step => !string.IsNullOrWhiteSpace(step.Id)) && steps.Select(step => step.Id).Distinct(StringComparer.Ordinal).Count() == steps.Count;
        foreach (var group in groups)
        {
            var routes = new List<SequenceTaskRoute>();
            foreach (var step in group.Steps)
            {
                bool terminal = step.Action is SequenceStepAction.Complete or SequenceStepAction.None;
                if (terminal)
                {
                    routes.Add(new(step, null, OpenVisionLanguageService.T("R24.CycleEnd"), string.Empty, true, true));
                    if (!string.IsNullOrWhiteSpace(step.NextStepId)) Add(step, step.NextStepId, OpenVisionLanguageService.T("Sequence.Next"), nameof(SequenceStepEditorItem.NextStepId));
                    if (!string.IsNullOrWhiteSpace(step.ErrorStepId)) Add(step, step.ErrorStepId, OpenVisionLanguageService.T("R24.Inspector.AlarmRoute"), nameof(SequenceStepEditorItem.ErrorStepId));
                    if (!string.IsNullOrWhiteSpace(step.FailureStepId)) Add(step, step.FailureStepId, "NG", nameof(SequenceStepEditorItem.FailureStepId));
                    continue;
                }
                bool inspection = step.Action is SequenceStepAction.WaitVisionResult or SequenceStepAction.BranchInspection;
                Add(step, !string.IsNullOrWhiteSpace(step.NextStepId) ? step.NextStepId : steps.ElementAtOrDefault(step.Order)?.Id ?? string.Empty,
                    inspection ? "OK" : OpenVisionLanguageService.T("Sequence.Next"), nameof(SequenceStepEditorItem.NextStepId));
                if (inspection || step.FailureStepId.Length > 0) Add(step, step.FailureStepId, "NG", nameof(SequenceStepEditorItem.FailureStepId));
                if (step.ErrorStepId.Length > 0) Add(step, step.ErrorStepId, OpenVisionLanguageService.T("R24.Inspector.AlarmRoute"), nameof(SequenceStepEditorItem.ErrorStepId));
            }
            group.Routes = routes;

            void Add(SequenceStepEditorItem source, string targetId, string label, string propertyName)
            {
                targetId = targetId.Trim();
                var target = uniqueIds ? groups.FirstOrDefault(candidate => candidate.Steps.Any(step => step.Id == targetId)) : null;
                if (ReferenceEquals(target, group) && group.Steps.Count > 1) return;
                bool valid = target is not null && source.Action is not (SequenceStepAction.Complete or SequenceStepAction.None) && (label != "NG" || source.HasFailureRoute);
                routes.Add(new(source, target, label, targetId, valid, false) { PropertyName = propertyName });
            }
        }
    }

    internal SequenceDefinition? Definition { get; }
    internal IReadOnlyList<SequenceValidationIssue> Issues { get; }
    public string Name { get; }
    public string SequenceText => Definition is null ? string.Empty : $"{Definition.Name} ({Definition.Id})";
    public IReadOnlyList<SequenceStepEditorItem> Steps { get; }
    public IReadOnlyList<SequenceTaskGroupViewModel> Groups { get; }
    public IEnumerable<SequenceTaskRoute> Routes => Groups.SelectMany(group => group.Routes);
    public bool IsEmpty => Groups.Count == 0;
    public string EmptyText => OpenVisionLanguageService.T(Definition is null ? "Sequence.NoSequenceSelected" : "R24.EmptyUnitSteps");
    public string StepCountText => string.Format(OpenVisionLanguageService.T("R24.StepCount"), Steps.Count);
}
