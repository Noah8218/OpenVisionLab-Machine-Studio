using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using OpenVisionLab;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Sequence.Authoring;

namespace OpenVisionLab.MachineStudio.ViewModel;

public sealed record RecipeCheckpointTemplateItemPresentation(
    string RoleText,
    string DetailText,
    bool IsProposed,
    bool IsAlreadyConfigured,
    bool IsUnavailable);

/// <summary>
/// Owns representative checkpoint preview presentation and commands. Project
/// mutation and workbench reload remain delegated through the supplied callback.
/// </summary>
public sealed class RecipeCheckpointTemplateViewModel : ViewModelBase
{
    private readonly Func<RepresentativeRecipeCheckpointTemplatePreview, int> _applyCheckpointTemplate;
    private readonly Action _clearCompetingPreviews;
    private readonly RepresentativeRecipeCheckpointTemplate _checkpointTemplate = new();
    private readonly RelayCommand _previewCommand;
    private readonly RelayCommand _applyCommand;
    private readonly RelayCommand _cancelCommand;
    private MachineProjectDocument? _project;
    private bool _isEditable = true;
    private RepresentativeRecipeCheckpointTemplatePreview? _preview;

    public RecipeCheckpointTemplateViewModel(
        Func<RepresentativeRecipeCheckpointTemplatePreview, int> applyCheckpointTemplate,
        Action clearCompetingPreviews)
    {
        _applyCheckpointTemplate = applyCheckpointTemplate;
        _clearCompetingPreviews = clearCompetingPreviews;
        _previewCommand = new RelayCommand(
            _ => Preview(),
            _ => IsEditable && ResolveRecipeSequenceId() is not null);
        _applyCommand = new RelayCommand(
            _ => Apply(),
            _ => IsEditable && _preview?.ProposedCount > 0);
        _cancelCommand = new RelayCommand(
            _ => ClearPreview(),
            _ => IsPreviewVisible);
    }

    public ObservableCollection<RecipeCheckpointTemplateItemPresentation> Items { get; } = new();

    public ICommand PreviewCommand => _previewCommand;
    public ICommand ApplyCommand => _applyCommand;
    public ICommand CancelCommand => _cancelCommand;

    public bool IsEditable
    {
        get => _isEditable;
        set
        {
            if (!SetProperty(ref _isEditable, value))
            {
                return;
            }

            _previewCommand.RaiseCanExecuteChanged();
            _applyCommand.RaiseCanExecuteChanged();
        }
    }

    public bool IsPreviewVisible => _preview is not null;
    public int ProposedCount => _preview?.ProposedCount ?? 0;
    public string SummaryText => Format(
        "Connections.CheckpointTemplateSummaryFormat",
        _preview?.ProposedCount ?? 0,
        _preview?.ExistingCount ?? 0,
        _preview?.UnavailableCount ?? 0);
    public string ApplyText => Format(
        "Connections.CheckpointTemplateApplyFormat",
        ProposedCount);

    public void Load(MachineProjectDocument project)
    {
        _project = project ?? throw new ArgumentNullException(nameof(project));
        ClearPreview();
        RaiseCommandStates();
    }

    public void ClearPreviewForCompetingSetup() => ClearPreview();

    internal void RefreshLocalization(Action reloadWorkbench)
    {
        var hadPreview = IsPreviewVisible;
        reloadWorkbench();
        if (hadPreview)
        {
            Preview();
        }
    }

    private void Preview()
    {
        var sequenceId = ResolveRecipeSequenceId();
        if (_project is null || sequenceId is null)
        {
            return;
        }

        _clearCompetingPreviews();
        _preview = _checkpointTemplate.Preview(_project, sequenceId);
        Items.Clear();
        foreach (var entry in _preview.Entries)
        {
            Items.Add(CreateItem(entry));
        }

        RaisePreviewChanged();
    }

    private void Apply()
    {
        if (_preview is not null)
        {
            _applyCheckpointTemplate(_preview);
        }
    }

    private void ClearPreview()
    {
        _preview = null;
        Items.Clear();
        RaisePreviewChanged();
    }

    private void RaisePreviewChanged()
    {
        OnPropertyChanged(nameof(IsPreviewVisible));
        OnPropertyChanged(nameof(ProposedCount));
        OnPropertyChanged(nameof(SummaryText));
        OnPropertyChanged(nameof(ApplyText));
        _applyCommand.RaiseCanExecuteChanged();
        _cancelCommand.RaiseCanExecuteChanged();
    }

    private void RaiseCommandStates()
    {
        _previewCommand.RaiseCanExecuteChanged();
        _applyCommand.RaiseCanExecuteChanged();
        _cancelCommand.RaiseCanExecuteChanged();
    }

    private string? ResolveRecipeSequenceId() =>
        _project?.Simulation.AutomaticRun?.SequenceId
        ?? _project?.Sequences.FirstOrDefault()?.Id;

    private static RecipeCheckpointTemplateItemPresentation CreateItem(
        RepresentativeCheckpointTemplateEntry entry)
    {
        var roleText = OpenVisionLanguageService.T(
            $"Connections.CheckpointTemplateRole.{entry.Role}");
        var detailText = entry.Status switch
        {
            RepresentativeCheckpointTemplateStatus.Proposed => Format(
                "Connections.CheckpointTemplateProposedFormat",
                entry.StepName ?? entry.StepId ?? "—",
                entry.ExpectedTargetId ?? "—",
                entry.ExpectedState ?? "—"),
            RepresentativeCheckpointTemplateStatus.AlreadyConfigured => Format(
                "Connections.CheckpointTemplateExistingFormat",
                entry.StepName ?? entry.StepId ?? "—",
                entry.ExpectedTargetId ?? "—",
                entry.ExpectedState ?? "—"),
            _ when entry.UnavailableReason
                == RepresentativeCheckpointUnavailableReason.StepAlreadyHasCheckpoint => Format(
                    "Connections.CheckpointTemplateConflictFormat",
                    entry.StepName ?? entry.StepId ?? "—"),
            _ => OpenVisionLanguageService.T("Connections.CheckpointTemplateUnavailable")
        };
        return new RecipeCheckpointTemplateItemPresentation(
            roleText,
            detailText,
            entry.Status == RepresentativeCheckpointTemplateStatus.Proposed,
            entry.Status == RepresentativeCheckpointTemplateStatus.AlreadyConfigured,
            entry.Status == RepresentativeCheckpointTemplateStatus.Unavailable);
    }

    private static string Format(string key, params object[] args) =>
        string.Format(CultureInfo.CurrentCulture, OpenVisionLanguageService.T(key), args);
}
