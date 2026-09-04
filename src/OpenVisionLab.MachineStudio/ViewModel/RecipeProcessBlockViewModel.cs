using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using OpenVisionLab;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Core.Sequences;
using OpenVisionLab.Machine.Sequence.Authoring;

namespace OpenVisionLab.MachineStudio.ViewModel;

/// <summary>
/// Owns process-block selection, plan preview, filtering, and managed timeout edits.
/// Project mutation remains delegated to the workbench through the supplied callbacks.
/// </summary>
public sealed class RecipeProcessBlockViewModel : ViewModelBase
{
    private enum ProcessBlockItemFilter
    {
        All,
        Customized,
        Removal,
        Conflict
    }

    private readonly Func<IReadOnlyList<SemiconductorProcessBlockKind>, int> _applyProcessBlock;
    private readonly Func<SemiconductorManagedTimeoutAdjustmentPreview, int> _applyProcessBlockTimeouts;
    private readonly Action _clearCompetingPreviews;
    private readonly Func<SemiconductorStationSkeletonEntry, SemiconductorStationSkeletonItemPresentation> _createStationSkeletonItem;
    private readonly SemiconductorProcessBlockComposer _composer = new();
    private readonly RelayCommand _previewCommand;
    private readonly RelayCommand _applyCommand;
    private readonly RelayCommand _cancelCommand;
    private readonly RelayCommand _previewTimeoutsCommand;
    private readonly RelayCommand _applyTimeoutsCommand;
    private readonly RelayCommand _cancelTimeoutsCommand;
    private MachineProjectDocument? _project;
    private SemiconductorProcessBlockItemPresentation? _selectedItem;
    private SemiconductorProcessBlockPlanPreview? _preview;
    private SemiconductorManagedTimeoutAdjustmentPreview? _timeoutPreview;
    private bool _isEditable = true;
    private bool _isPreservingPlan;
    private bool _isLoadBlockSelected = true;
    private bool _isAlignBlockSelected = true;
    private bool _isProcessBlockSelected = true;
    private bool _isInspectBlockSelected = true;
    private bool _isUnloadBlockSelected = true;
    private ProcessBlockItemFilter _filter;
    private string _timeoutText = "5000";

    public RecipeProcessBlockViewModel(
        Func<IReadOnlyList<SemiconductorProcessBlockKind>, int> applyProcessBlock,
        Func<SemiconductorManagedTimeoutAdjustmentPreview, int> applyProcessBlockTimeouts,
        Action clearCompetingPreviews,
        Func<SemiconductorStationSkeletonEntry, SemiconductorStationSkeletonItemPresentation> createStationSkeletonItem)
    {
        _applyProcessBlock = applyProcessBlock;
        _applyProcessBlockTimeouts = applyProcessBlockTimeouts;
        _clearCompetingPreviews = clearCompetingPreviews;
        _createStationSkeletonItem = createStationSkeletonItem;
        _previewCommand = new RelayCommand(_ => OpenProcessBlockPlan(), _ => IsEditable);
        _applyCommand = new RelayCommand(_ => ApplyProcessBlock(), _ => IsEditable && _preview?.CanApply == true);
        _cancelCommand = new RelayCommand(_ => ClearPreviewForCompetingSetup(), _ => IsProcessBlockPreviewVisible);
        _previewTimeoutsCommand = new RelayCommand(
            _ => PreviewProcessBlockTimeouts(),
            _ => IsEditable && IsProcessBlockPreviewVisible && IsProcessBlockTimeoutValid && CompatibleProcessBlockTimeoutCount > 0);
        _applyTimeoutsCommand = new RelayCommand(_ => ApplyProcessBlockTimeouts(), _ => IsEditable && _timeoutPreview?.CanApply == true);
        _cancelTimeoutsCommand = new RelayCommand(_ => ClearProcessBlockTimeoutPreview(), _ => IsProcessBlockTimeoutPreviewVisible);
    }

    public ObservableCollection<SemiconductorStationSkeletonItemPresentation> ProcessBlockConnectionItems { get; } = new();
    public ObservableCollection<SemiconductorProcessBlockItemPresentation> ProcessBlockItems { get; } = new();
    public ObservableCollection<SemiconductorProcessBlockItemPresentation> VisibleProcessBlockItems { get; } = new();
    public ObservableCollection<SemiconductorManagedTimeoutAdjustmentItemPresentation> ProcessBlockTimeoutItems { get; } = new();
    public ICommand PreviewProcessBlockCommand => _previewCommand;
    public ICommand ApplyProcessBlockCommand => _applyCommand;
    public ICommand CancelProcessBlockCommand => _cancelCommand;
    public ICommand PreviewProcessBlockTimeoutsCommand => _previewTimeoutsCommand;
    public ICommand ApplyProcessBlockTimeoutsCommand => _applyTimeoutsCommand;
    public ICommand CancelProcessBlockTimeoutsCommand => _cancelTimeoutsCommand;
    public event EventHandler? ProcessBlockPreviewClosed;

    public bool IsEditable
    {
        get => _isEditable;
        set
        {
            if (!SetProperty(ref _isEditable, value)) return;
            _previewCommand.RaiseCanExecuteChanged();
            _applyCommand.RaiseCanExecuteChanged();
            _previewTimeoutsCommand.RaiseCanExecuteChanged();
            _applyTimeoutsCommand.RaiseCanExecuteChanged();
        }
    }

    public bool IsProcessBlockPreviewVisible => _preview is not null;
    public bool IsLoadBlockSelected { get => _isLoadBlockSelected; set => SetProcessBlockSelection(ref _isLoadBlockSelected, value, nameof(IsLoadBlockSelected)); }
    public bool IsAlignBlockSelected { get => _isAlignBlockSelected; set => SetProcessBlockSelection(ref _isAlignBlockSelected, value, nameof(IsAlignBlockSelected)); }
    public bool IsProcessBlockSelected { get => _isProcessBlockSelected; set => SetProcessBlockSelection(ref _isProcessBlockSelected, value, nameof(IsProcessBlockSelected)); }
    public bool IsInspectBlockSelected { get => _isInspectBlockSelected; set => SetProcessBlockSelection(ref _isInspectBlockSelected, value, nameof(IsInspectBlockSelected)); }
    public bool IsUnloadBlockSelected { get => _isUnloadBlockSelected; set => SetProcessBlockSelection(ref _isUnloadBlockSelected, value, nameof(IsUnloadBlockSelected)); }
    public int SelectedProcessBlockCount => SelectedProcessBlockKinds().Count;
    public int ExistingProcessBlockCount => _preview?.ExistingKinds.Count ?? 0;
    public bool HasProcessBlockSelection => SelectedProcessBlockCount > 0;
    public bool IsProcessBlockFilterAll { get => _filter == ProcessBlockItemFilter.All; set { if (value) SetProcessBlockItemFilter(ProcessBlockItemFilter.All); } }
    public bool IsProcessBlockFilterCustomized { get => _filter == ProcessBlockItemFilter.Customized; set { if (value) SetProcessBlockItemFilter(ProcessBlockItemFilter.Customized); } }
    public bool IsProcessBlockFilterRemoval { get => _filter == ProcessBlockItemFilter.Removal; set { if (value) SetProcessBlockItemFilter(ProcessBlockItemFilter.Removal); } }
    public bool IsProcessBlockFilterConflict { get => _filter == ProcessBlockItemFilter.Conflict; set { if (value) SetProcessBlockItemFilter(ProcessBlockItemFilter.Conflict); } }
    public string ProcessBlockFilterAllText => FormatProcessBlockFilter("Connections.ProcessBlockFilterAll", ProcessBlockItems.Count);
    public string ProcessBlockFilterCustomizedText => FormatProcessBlockFilter("Connections.ProcessBlockStepCustomized", ProcessBlockItems.Count(item => item.IsCustomized));
    public string ProcessBlockFilterRemovalText => FormatProcessBlockFilter("Connections.ProcessBlockStepProposedRemoval", ProcessBlockItems.Count(item => item.IsProposedRemoval));
    public string ProcessBlockFilterConflictText => FormatProcessBlockFilter("Connections.ProcessBlockFilterConflict", ProcessBlockItems.Count(item => item.IsUnavailable));
    public bool HasVisibleProcessBlockItems => VisibleProcessBlockItems.Count > 0;
    public int CompatibleProcessBlockTimeoutCount => VisibleProcessBlockItems.Count(item => item.CanAdjustTimeout);
    public string ProcessBlockTimeoutText
    {
        get => _timeoutText;
        set
        {
            if (!SetProperty(ref _timeoutText, value)) return;
            ClearProcessBlockTimeoutPreview();
            OnPropertyChanged(nameof(IsProcessBlockTimeoutValid));
            OnPropertyChanged(nameof(ProcessBlockTimeoutValidationText));
            _previewTimeoutsCommand.RaiseCanExecuteChanged();
        }
    }
    public bool IsProcessBlockTimeoutValid => int.TryParse(ProcessBlockTimeoutText, NumberStyles.Integer, CultureInfo.CurrentCulture, out var timeout) && timeout >= 0;
    public bool IsProcessBlockTimeoutPreviewVisible => _timeoutPreview is not null;
    public string ProcessBlockTimeoutScopeText => Format("Connections.ProcessBlockTimeoutScopeFormat", CompatibleProcessBlockTimeoutCount, VisibleProcessBlockItems.Count);
    public string ProcessBlockTimeoutValidationText => OpenVisionLanguageService.T(!IsProcessBlockTimeoutValid
        ? "Connections.ProcessBlockTimeoutInvalid"
        : CompatibleProcessBlockTimeoutCount == 0
            ? "Connections.ProcessBlockTimeoutNoCompatible"
            : _timeoutPreview is { CanApply: true }
                ? "Connections.ProcessBlockTimeoutReady"
                : _timeoutPreview is not null
                    ? "Connections.ProcessBlockTimeoutNoChanges"
                    : "Connections.ProcessBlockTimeoutHint");
    public string ProcessBlockTimeoutApplyText => Format("Connections.ProcessBlockTimeoutApplyFormat", _timeoutPreview?.ChangedCount ?? 0);
    public string ProcessBlockKindText => Format("Connections.ProcessBlockEditSelectionFormat", SelectedProcessBlockCount, ExistingProcessBlockCount);
    public string ProcessBlockSummaryText => (_preview?.CustomizedStepCount ?? 0) > 0
        ? Format("Connections.ProcessBlockEditSummaryWithCustomizedFormat", _preview?.ProposedConnectionCount ?? 0, _preview?.ProposedStepCount ?? 0, _preview?.RemovedStepCount ?? 0, _preview?.ExistingStepCount ?? 0, _preview?.CustomizedStepCount ?? 0, _preview?.UnavailableCount ?? 0)
        : Format("Connections.ProcessBlockEditSummaryFormat", _preview?.ProposedConnectionCount ?? 0, _preview?.ProposedStepCount ?? 0, _preview?.RemovedStepCount ?? 0, _preview?.ExistingStepCount ?? 0, _preview?.UnavailableCount ?? 0);
    public string ProcessBlockApplyText => Format("Connections.ProcessBlockEditApplyFormat", _preview?.ProposedConnectionCount ?? 0, _preview?.ProposedStepCount ?? 0, _preview?.RemovedStepCount ?? 0);
    public bool HasProcessBlockPlanError => _preview is { CanApply: false, UnavailableCount: > 0 } || (_preview is { ExistingKinds.Count: 0 } && !HasProcessBlockSelection);
    public string ProcessBlockValidationText => OpenVisionLanguageService.T(_preview switch
    {
        { UnavailableCount: > 0 } => "Connections.ProcessBlockEditPlanUnavailable",
        { CanApply: true } => "Connections.ProcessBlockEditPlanReady",
        { ExistingKinds.Count: 0 } when !HasProcessBlockSelection => "Connections.ProcessBlockEditPlanEmpty",
        _ => "Connections.ProcessBlockEditPlanNoChanges"
    });
    public SemiconductorProcessBlockItemPresentation? SelectedProcessBlockItem
    {
        get => _selectedItem;
        set => SetProperty(ref _selectedItem, value);
    }

    public void Load(MachineProjectDocument project)
    {
        _project = project ?? throw new ArgumentNullException(nameof(project));
        ClearPreviewForCompetingSetup();
    }

    internal void RefreshDefinitionPreservingPlan(Action reload, string? selectedComponentId = null)
    {
        var kinds = _preview?.Kinds.ToArray();
        var selectedStepId = SelectedProcessBlockItem?.StepId;
        _isPreservingPlan = kinds is not null;
        try { reload(); }
        finally { _isPreservingPlan = false; }
        if (kinds is null) return;
        SetProcessBlockSelections(kinds);
        PreviewProcessBlockPlan();
        SelectProcessBlockStep(selectedStepId);
    }

    internal void PreservePlanAcross(Action reload)
    {
        var kinds = _preview?.Kinds.ToArray();
        _isPreservingPlan = kinds is not null;
        try { reload(); }
        finally { _isPreservingPlan = false; }
        if (kinds is not null)
        {
            SetProcessBlockSelections(kinds);
            PreviewProcessBlockPlan();
        }
    }

    public SemiconductorProcessBlockItemPresentation? SelectProcessBlockStep(string? stepId)
    {
        var item = ProcessBlockItems.FirstOrDefault(candidate => string.Equals(candidate.StepId, stepId, StringComparison.Ordinal));
        SelectedProcessBlockItem = null;
        SelectedProcessBlockItem = item;
        return item;
    }

    internal void ClearPreviewForCompetingSetup() => ClearProcessBlockPreview();

    private void OpenProcessBlockPlan()
    {
        var existingKinds = _project is null
            ? []
            : _composer.RecognizeExistingKinds(_project);
        SetProcessBlockSelections(existingKinds.Count > 0
            ? existingKinds
            : Enum.GetValues<SemiconductorProcessBlockKind>());
        PreviewProcessBlockPlan();
    }

    private void PreviewProcessBlockPlan()
    {
        if (_project is null) return;
        _clearCompetingPreviews();
        var selectedStepId = SelectedProcessBlockItem?.StepId;
        _preview = _composer.Preview(_project, SelectedProcessBlockKinds());
        ProcessBlockConnectionItems.Clear();
        foreach (var entry in _preview.Station.Entries.Where(entry => entry.Status != SemiconductorStationSkeletonStatus.Existing))
            ProcessBlockConnectionItems.Add(_createStationSkeletonItem(entry));
        ProcessBlockItems.Clear();
        var sequence = ResolveRecipeSequence();
        var sequenceId = sequence?.Id;
        foreach (var entry in _preview.Steps)
        {
            var statusText = entry.Status switch
            {
                SemiconductorProcessBlockStepStatus.Proposed => OpenVisionLanguageService.T("Connections.ProcessBlockStepProposed"),
                SemiconductorProcessBlockStepStatus.Existing => OpenVisionLanguageService.T("Connections.ProcessBlockStepExisting"),
                SemiconductorProcessBlockStepStatus.Customized => OpenVisionLanguageService.T("Connections.ProcessBlockStepCustomized"),
                SemiconductorProcessBlockStepStatus.ProposedRemoval => OpenVisionLanguageService.T("Connections.ProcessBlockStepProposedRemoval"),
                _ => OpenVisionLanguageService.T("Connections.ProcessBlockStepUnavailable")
            };
            var currentStep = sequence?.Steps.FirstOrDefault(step => string.Equals(step.Id, entry.StepId, StringComparison.Ordinal));
            var currentValue = string.IsNullOrWhiteSpace(currentStep?.Parameter) ? "—" : currentStep.Parameter;
            var templateValue = string.IsNullOrWhiteSpace(entry.Parameter) ? "—" : entry.Parameter;
            var currentTimeout = currentStep?.TimeoutMs.ToString("N0", CultureInfo.CurrentCulture);
            var templateTimeout = entry.TimeoutMs.ToString("N0", CultureInfo.CurrentCulture);
            var detailText = currentStep is null
                ? $"{statusText} · {entry.Action} · {entry.TargetId}"
                : $"{statusText} · {OpenVisionLanguageService.T("Sequence.Action")}: {WithTemplateDifference(currentStep.Action.ToString(), entry.Action.ToString())} · "
                  + $"{OpenVisionLanguageService.T("Sequence.Target")}: {WithTemplateDifference(currentStep.TargetId, entry.TargetId)} · "
                  + $"{OpenVisionLanguageService.T("Sequence.Value")}: {WithTemplateDifference(currentValue, templateValue)} · "
                  + $"{OpenVisionLanguageService.T("Sequence.Timeout")}: {WithTemplateDifference($"{currentTimeout} ms", $"{templateTimeout} ms")}";
            ProcessBlockItems.Add(new SemiconductorProcessBlockItemPresentation(sequenceId, entry.StepId, OpenVisionLanguageService.T($"Connections.ProcessBlockStep.{entry.StepId}"), detailText, currentStep?.Action, currentStep?.TimeoutMs,
                entry.Status == SemiconductorProcessBlockStepStatus.Proposed, entry.Status == SemiconductorProcessBlockStepStatus.Existing, entry.Status == SemiconductorProcessBlockStepStatus.Customized, entry.Status == SemiconductorProcessBlockStepStatus.ProposedRemoval, entry.Status == SemiconductorProcessBlockStepStatus.Unavailable));
        }
        SelectProcessBlockStep(selectedStepId);
        RefreshVisibleProcessBlockItems();
        RaiseProcessBlockChanged();
    }

    private void SetProcessBlockItemFilter(ProcessBlockItemFilter filter)
    {
        if (_filter == filter) return;
        _filter = filter;
        ClearProcessBlockTimeoutPreview();
        RefreshVisibleProcessBlockItems();
        OnPropertyChanged(nameof(IsProcessBlockFilterAll));
        OnPropertyChanged(nameof(IsProcessBlockFilterCustomized));
        OnPropertyChanged(nameof(IsProcessBlockFilterRemoval));
        OnPropertyChanged(nameof(IsProcessBlockFilterConflict));
    }

    private void RefreshVisibleProcessBlockItems()
    {
        var selectedStepId = SelectedProcessBlockItem?.StepId;
        VisibleProcessBlockItems.Clear();
        foreach (var item in ProcessBlockItems.Where(item => _filter switch
        {
            ProcessBlockItemFilter.Customized => item.IsCustomized,
            ProcessBlockItemFilter.Removal => item.IsProposedRemoval,
            ProcessBlockItemFilter.Conflict => item.IsUnavailable,
            _ => true
        })) VisibleProcessBlockItems.Add(item);
        SelectProcessBlockStep(VisibleProcessBlockItems.FirstOrDefault(item => string.Equals(item.StepId, selectedStepId, StringComparison.Ordinal))?.StepId);
        OnPropertyChanged(nameof(ProcessBlockFilterAllText));
        OnPropertyChanged(nameof(ProcessBlockFilterCustomizedText));
        OnPropertyChanged(nameof(ProcessBlockFilterRemovalText));
        OnPropertyChanged(nameof(ProcessBlockFilterConflictText));
        OnPropertyChanged(nameof(HasVisibleProcessBlockItems));
        OnPropertyChanged(nameof(CompatibleProcessBlockTimeoutCount));
        OnPropertyChanged(nameof(ProcessBlockTimeoutScopeText));
        OnPropertyChanged(nameof(ProcessBlockTimeoutValidationText));
        _previewTimeoutsCommand.RaiseCanExecuteChanged();
    }

    private string FormatProcessBlockFilter(string key, int count) => Format("Connections.ProcessBlockFilterCountFormat", OpenVisionLanguageService.T(key), count);
    private void SetProcessBlockSelection(ref bool field, bool value, string propertyName)
    {
        if (!SetProperty(ref field, value, propertyName)) return;
        if (IsProcessBlockPreviewVisible) PreviewProcessBlockPlan();
    }
    private void SetProcessBlockSelections(IEnumerable<SemiconductorProcessBlockKind> kinds)
    {
        var selected = kinds.ToHashSet();
        _isLoadBlockSelected = selected.Contains(SemiconductorProcessBlockKind.Load);
        _isAlignBlockSelected = selected.Contains(SemiconductorProcessBlockKind.Align);
        _isProcessBlockSelected = selected.Contains(SemiconductorProcessBlockKind.Process);
        _isInspectBlockSelected = selected.Contains(SemiconductorProcessBlockKind.Inspect);
        _isUnloadBlockSelected = selected.Contains(SemiconductorProcessBlockKind.Unload);
        OnPropertyChanged(nameof(IsLoadBlockSelected)); OnPropertyChanged(nameof(IsAlignBlockSelected)); OnPropertyChanged(nameof(IsProcessBlockSelected)); OnPropertyChanged(nameof(IsInspectBlockSelected)); OnPropertyChanged(nameof(IsUnloadBlockSelected));
    }
    private IReadOnlyList<SemiconductorProcessBlockKind> SelectedProcessBlockKinds()
    {
        var kinds = new List<SemiconductorProcessBlockKind>(5);
        if (IsLoadBlockSelected) kinds.Add(SemiconductorProcessBlockKind.Load);
        if (IsAlignBlockSelected) kinds.Add(SemiconductorProcessBlockKind.Align);
        if (IsProcessBlockSelected) kinds.Add(SemiconductorProcessBlockKind.Process);
        if (IsInspectBlockSelected) kinds.Add(SemiconductorProcessBlockKind.Inspect);
        if (IsUnloadBlockSelected) kinds.Add(SemiconductorProcessBlockKind.Unload);
        return kinds;
    }
    private void PreviewProcessBlockTimeouts()
    {
        if (_project is null || !int.TryParse(ProcessBlockTimeoutText, NumberStyles.Integer, CultureInfo.CurrentCulture, out var timeout) || timeout < 0) return;
        _timeoutPreview = _composer.PreviewTimeoutAdjustment(_project, VisibleProcessBlockItems.Where(item => item.CanAdjustTimeout).Select(item => item.StepId), timeout);
        ProcessBlockTimeoutItems.Clear();
        foreach (var entry in _timeoutPreview.Entries)
            ProcessBlockTimeoutItems.Add(new SemiconductorManagedTimeoutAdjustmentItemPresentation(OpenVisionLanguageService.T($"Connections.ProcessBlockStep.{entry.StepId}"), Format("Connections.ProcessBlockTimeoutItemFormat", entry.Action, entry.TargetId, entry.CurrentTimeoutMs, entry.ProposedTimeoutMs)));
        RaiseProcessBlockTimeoutChanged();
    }
    private void ApplyProcessBlockTimeouts()
    {
        if (_timeoutPreview is not { CanApply: true } preview) return;
        if (_applyProcessBlockTimeouts(preview) <= 0) PreviewProcessBlockTimeouts();
    }
    private void ClearProcessBlockTimeoutPreview()
    {
        if (_timeoutPreview is null && ProcessBlockTimeoutItems.Count == 0) return;
        _timeoutPreview = null; ProcessBlockTimeoutItems.Clear(); RaiseProcessBlockTimeoutChanged();
    }
    private void RaiseProcessBlockTimeoutChanged()
    {
        OnPropertyChanged(nameof(IsProcessBlockTimeoutPreviewVisible)); OnPropertyChanged(nameof(ProcessBlockTimeoutValidationText)); OnPropertyChanged(nameof(ProcessBlockTimeoutApplyText));
        _previewTimeoutsCommand.RaiseCanExecuteChanged(); _applyTimeoutsCommand.RaiseCanExecuteChanged(); _cancelTimeoutsCommand.RaiseCanExecuteChanged();
    }
    private void ApplyProcessBlock()
    {
        if (_preview is { CanApply: true } preview) _applyProcessBlock(preview.Kinds);
    }
    private void ClearProcessBlockPreview()
    {
        var wasVisible = IsProcessBlockPreviewVisible;
        _preview = null; ClearProcessBlockTimeoutPreview(); ProcessBlockConnectionItems.Clear(); ProcessBlockItems.Clear(); VisibleProcessBlockItems.Clear(); SelectedProcessBlockItem = null; RaiseProcessBlockChanged();
        if (wasVisible && !_isPreservingPlan) ProcessBlockPreviewClosed?.Invoke(this, EventArgs.Empty);
    }
    private void RaiseProcessBlockChanged()
    {
        OnPropertyChanged(nameof(IsProcessBlockPreviewVisible)); OnPropertyChanged(nameof(ProcessBlockKindText)); OnPropertyChanged(nameof(ProcessBlockSummaryText)); OnPropertyChanged(nameof(ProcessBlockApplyText)); OnPropertyChanged(nameof(SelectedProcessBlockCount)); OnPropertyChanged(nameof(ExistingProcessBlockCount)); OnPropertyChanged(nameof(HasProcessBlockSelection)); OnPropertyChanged(nameof(HasProcessBlockPlanError)); OnPropertyChanged(nameof(ProcessBlockValidationText)); OnPropertyChanged(nameof(CompatibleProcessBlockTimeoutCount)); OnPropertyChanged(nameof(ProcessBlockTimeoutScopeText)); OnPropertyChanged(nameof(ProcessBlockTimeoutValidationText));
        _applyCommand.RaiseCanExecuteChanged(); _cancelCommand.RaiseCanExecuteChanged(); _previewTimeoutsCommand.RaiseCanExecuteChanged();
    }
    private SequenceDefinition? ResolveRecipeSequence()
    {
        var sequenceId = _project?.Simulation.AutomaticRun?.SequenceId ?? _project?.Sequences.FirstOrDefault()?.Id;
        return sequenceId is null ? null : _project?.Sequences.FirstOrDefault(sequence => string.Equals(sequence.Id, sequenceId, StringComparison.Ordinal));
    }
    private static string Format(string key, params object[] args) => string.Format(CultureInfo.CurrentCulture, OpenVisionLanguageService.T(key), args);
    private static string WithTemplateDifference(string current, string template) => string.Equals(current, template, StringComparison.Ordinal) ? current : $"{current} ({Format("Connections.ProcessBlockTemplateValueFormat", template)})";
}
