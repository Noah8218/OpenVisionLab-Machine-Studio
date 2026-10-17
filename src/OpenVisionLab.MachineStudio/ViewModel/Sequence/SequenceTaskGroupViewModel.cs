using OpenVisionLab;
using OpenVisionLab.Machine.Sequence.Authoring;

namespace OpenVisionLab.MachineStudio.ViewModel;

public sealed class SequenceTaskGroupViewModel(SequenceTaskKind kind, IReadOnlyList<SequenceStepEditorItem> steps) : ViewModelBase
{
    private bool _isExpanded;
    private string? _selectedStepId;
    private string _issueText = string.Empty;

    public IReadOnlyList<SequenceStepEditorItem> Steps { get; } = steps;
    public IReadOnlyList<SequenceTaskRoute> Routes { get; internal set; } = Array.Empty<SequenceTaskRoute>();
    public string Key => string.Join("\n", Steps.Select(step => step.Id));
    public SequenceStepEditorItem FirstStep => Steps[0];
    public string Title => kind == SequenceTaskKind.PositionAndCapture ? Steps[^1].DisplayName : FirstStep.DisplayName;
    public string CategoryText => kind == SequenceTaskKind.Step ? FirstStep.StepListActionText : OpenVisionLanguageService.T("Process.Task." + kind);
    public string RangeText => Steps.Count == 1 ? $"{FirstStep.Order}" : $"{FirstStep.Order}–{Steps[^1].Order}";
    public string TargetText => string.Join(" / ", Steps.Select(step => step.StepListTargetText).Where(text => text != "—").Distinct(StringComparer.Ordinal));
    public string RouteText => Steps[^1].StepListNextText;
    public bool IsGroup => Steps.Count > 1;
    public bool IsSelected => Steps.Any(step => step.Id == _selectedStepId);
    public bool IsExecuting => Steps.Any(step => step.IsExecuting);
    public bool IsSearchMatch => Steps.Any(step => step.IsSearchMatch);
    public bool HasIssue => _issueText.Length > 0;
    public string IssueText => _issueText;
    public bool IsExpanded { get => _isExpanded; set => SetProperty(ref _isExpanded, value); }

    internal void Refresh(string? selectedStepId, IEnumerable<SequenceValidationIssue> issues)
    {
        _selectedStepId = selectedStepId;
        _issueText = string.Join("\n", issues.Where(issue => Steps.Any(step => step.Id == issue.StepId)).Select(issue => issue.DisplayText));
        OnPropertyChanged(string.Empty);
    }
}
