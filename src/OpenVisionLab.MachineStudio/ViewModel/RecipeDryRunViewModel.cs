using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using OpenVisionLab;
using OpenVisionLab.Machine.Core.Layouts;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Core.Sequences;
using OpenVisionLab.Machine.Simulation.Layout;
using OpenVisionLab.Machine.Simulation.Sequences;
using OpenVisionLab.Machine.Simulation.Snapshots;

namespace OpenVisionLab.MachineStudio.ViewModel;

public sealed record RecipeDryRunStepPresentation(
    string SequenceId,
    string StepId,
    string? ComponentId,
    string OrderText,
    string Name,
    string TickText,
    bool HasIssue,
    bool HasCheckpoint,
    bool HasCheckpointMismatch,
    string CheckpointText,
    SimulationSnapshot BoundarySnapshot);

public sealed record RecipeDryRunEquipmentStatePresentation(
    string Text,
    bool IsLoadLock = false,
    bool IsWaferHandler = false,
    bool IsInspectionSorter = false,
    bool IsInspectionHandoff = false,
    bool IsOhtHandoff = false,
    bool IsPrealigner = false,
    bool IsFault = false);

public sealed class RecipeDryRunViewModel : ViewModelBase
{
    private readonly Func<string?> _validateSimulationReadiness;
    private readonly Func<string, Task<RecipeDryRunResult>> _runRecipeDryRun;
    private readonly Action<string, string> _openSequenceStep;
    private readonly Action<RecipeDryRunStepPresentation> _playRecipeDryRunStep;
    private readonly Action<string?> _selectComponent;
    private readonly Func<string?, string?> _resolveComponentId;
    private readonly RelayCommand _validateSimulationReadinessCommand;
    private readonly AsyncRelayCommand _runRecipeDryRunCommand;
    private readonly RelayCommand _openRecipeDryRunStepCommand;
    private readonly RelayCommand _playRecipeDryRunStepCommand;
    private MachineProjectDocument? _project;
    private bool _isEditable = true;
    private bool? _readinessPassed;
    private string? _readinessError;
    private int _definitionRevision;
    private bool _isRecipeDryRunRunning;
    private RecipeDryRunResult? _recipeDryRunResult;
    private RecipeDryRunStepPresentation? _selectedRecipeDryRunStep;
    private string _recipeDryRunStatusText = string.Empty;
    private string _recipeDryRunDetailText = string.Empty;
    private string _recipeDryRunIssueText = string.Empty;

    public RecipeDryRunViewModel(
        Func<string?> validateSimulationReadiness,
        Func<string, Task<RecipeDryRunResult>> runRecipeDryRun,
        Action<string, string> openSequenceStep,
        Action<RecipeDryRunStepPresentation> playRecipeDryRunStep,
        Action<string?> selectComponent,
        Func<string?, string?> resolveComponentId)
    {
        _validateSimulationReadiness = validateSimulationReadiness;
        _runRecipeDryRun = runRecipeDryRun;
        _openSequenceStep = openSequenceStep;
        _playRecipeDryRunStep = playRecipeDryRunStep;
        _selectComponent = selectComponent;
        _resolveComponentId = resolveComponentId;
        _validateSimulationReadinessCommand = new RelayCommand(
            _ => ValidateSimulationReadiness(),
            _ => IsEditable);
        _runRecipeDryRunCommand = new AsyncRelayCommand(
            RunRecipeDryRunAsync,
            _ => IsEditable
                 && ReadinessPassed == true
                 && !IsRecipeDryRunRunning
                 && ResolveRecipeSequenceId() is not null);
        _openRecipeDryRunStepCommand = new RelayCommand(
            OpenRecipeDryRunStep,
            parameter => IsEditable && parameter is RecipeDryRunStepPresentation);
        _playRecipeDryRunStepCommand = new RelayCommand(
            PlayRecipeDryRunStep,
            parameter => IsEditable && parameter is RecipeDryRunStepPresentation);
    }

    public ObservableCollection<RecipeDryRunStepPresentation> Timeline { get; } = new();
    public ObservableCollection<RecipeDryRunEquipmentStatePresentation> FinalStates { get; } = new();
    public ICommand ValidateSimulationReadinessCommand => _validateSimulationReadinessCommand;
    public ICommand RunRecipeDryRunCommand => _runRecipeDryRunCommand;
    public ICommand OpenRecipeDryRunStepCommand => _openRecipeDryRunStepCommand;
    public ICommand PlayRecipeDryRunStepCommand => _playRecipeDryRunStepCommand;

    public bool IsEditable
    {
        get => _isEditable;
        set
        {
            if (!SetProperty(ref _isEditable, value))
            {
                return;
            }

            RaiseCommandsCanExecuteChanged();
        }
    }

    public bool? ReadinessPassed => _readinessPassed;
    public string ReadinessStatusText => OpenVisionLanguageService.T(
        _readinessPassed switch
        {
            true => "Connections.ReadinessPassed",
            false => "Connections.ReadinessFailed",
            null => "Connections.ReadinessNotChecked"
        });
    public string ReadinessDetailText => _readinessPassed switch
    {
        true => OpenVisionLanguageService.T("Connections.ReadinessPassedDetail"),
        false => Format("Connections.ReadinessFailedDetail", _readinessError ?? string.Empty),
        null => OpenVisionLanguageService.T("Connections.ReadinessNotCheckedDetail")
    };
    public bool IsRecipeDryRunRunning
    {
        get => _isRecipeDryRunRunning;
        private set
        {
            if (SetProperty(ref _isRecipeDryRunRunning, value))
            {
                _runRecipeDryRunCommand.RaiseCanExecuteChanged();
            }
        }
    }
    public RecipeDryRunResult? RecipeDryRunResult
    {
        get => _recipeDryRunResult;
        private set => SetProperty(ref _recipeDryRunResult, value);
    }
    public bool HasRecipeDryRunResult => RecipeDryRunResult is not null;
    public bool RecipeDryRunPassed => RecipeDryRunResult?.Outcome == RecipeDryRunOutcome.Completed;
    public bool RecipeDryRunWarning => RecipeDryRunResult is { Outcome: not RecipeDryRunOutcome.Completed };
    public bool HasRecipeDryRunIssue => RecipeDryRunResult?.FirstIssue is not null
        || RecipeDryRunResult?.FirstCheckpointMismatch is not null;
    public RecipeDryRunStepPresentation? SelectedRecipeDryRunStep
    {
        get => _selectedRecipeDryRunStep;
        set
        {
            if (!SetProperty(ref _selectedRecipeDryRunStep, value))
            {
                return;
            }

            _selectComponent(value?.ComponentId);
        }
    }
    public string RecipeDryRunStatusText
    {
        get => _recipeDryRunStatusText;
        private set => SetProperty(ref _recipeDryRunStatusText, value);
    }
    public string RecipeDryRunDetailText
    {
        get => _recipeDryRunDetailText;
        private set => SetProperty(ref _recipeDryRunDetailText, value);
    }
    public string RecipeDryRunIssueText
    {
        get => _recipeDryRunIssueText;
        private set => SetProperty(ref _recipeDryRunIssueText, value);
    }

    public void Load(MachineProjectDocument project, bool preserveReadiness = false)
    {
        ArgumentNullException.ThrowIfNull(project);
        var readinessPassed = preserveReadiness ? _readinessPassed : null;
        var readinessError = preserveReadiness ? _readinessError : null;
        _project = project;
        _definitionRevision++;
        _readinessPassed = readinessPassed;
        _readinessError = readinessError;
        ClearRecipeDryRun();
        RaiseReadinessChanged();
        _runRecipeDryRunCommand.RaiseCanExecuteChanged();
    }

    private void ValidateSimulationReadiness()
    {
        _readinessError = _validateSimulationReadiness();
        _readinessPassed = _readinessError is null;
        RaiseReadinessChanged();
    }

    private async Task RunRecipeDryRunAsync(object? parameter)
    {
        var sequenceId = ResolveRecipeSequenceId();
        if (sequenceId is null)
        {
            return;
        }

        var revision = _definitionRevision;
        IsRecipeDryRunRunning = true;
        try
        {
            var result = await _runRecipeDryRun(sequenceId);
            if (revision != _definitionRevision || ReadinessPassed != true)
            {
                return;
            }

            ApplyRecipeDryRun(result);
        }
        finally
        {
            IsRecipeDryRunRunning = false;
        }
    }

    private void OpenRecipeDryRunStep(object? parameter)
    {
        if (parameter is RecipeDryRunStepPresentation step)
        {
            SelectedRecipeDryRunStep = step;
            _openSequenceStep(step.SequenceId, step.StepId);
        }
    }

    private void PlayRecipeDryRunStep(object? parameter)
    {
        if (parameter is RecipeDryRunStepPresentation step)
        {
            SelectedRecipeDryRunStep = step;
            _playRecipeDryRunStep(step);
        }
    }

    private string? ResolveRecipeSequenceId() =>
        _project?.Simulation.AutomaticRun?.SequenceId
        ?? _project?.Sequences.FirstOrDefault()?.Id;

    private void ApplyRecipeDryRun(RecipeDryRunResult result)
    {
        RecipeDryRunResult = result;
        RecipeDryRunStatusText = OpenVisionLanguageService.T(result.Outcome switch
        {
            RecipeDryRunOutcome.Completed => "Connections.DryRunCompleted",
            RecipeDryRunOutcome.CompletedWithIssue => "Connections.DryRunCompletedWithIssue",
            RecipeDryRunOutcome.CompletedWithMismatch => "Connections.DryRunCompletedWithMismatch",
            RecipeDryRunOutcome.LimitReached => "Connections.DryRunLimitReached",
            RecipeDryRunOutcome.Faulted => "Connections.DryRunFaulted",
            _ => "Connections.DryRunRejected"
        });
        RecipeDryRunDetailText = Format(
            "Connections.DryRunDetailFormat",
            string.IsNullOrWhiteSpace(result.SequenceName) ? result.SequenceId : result.SequenceName,
            result.ExecutedTicks,
            result.MaximumTicks,
            result.Timeline.Count);
        RecipeDryRunIssueText = result.FirstIssue is not null
            ? Format(
                "Connections.DryRunIssueFormat",
                result.FirstIssue.StepId,
                result.FirstIssue.Tick,
                result.FirstIssue.Code,
                result.FirstIssue.Detail)
            : result.FirstCheckpointMismatch is { } mismatch
                ? Format(
                    "Connections.DryRunMismatchFormat",
                    mismatch.StepId,
                    mismatch.Tick,
                    mismatch.TargetId,
                    mismatch.ExpectedState,
                    mismatch.ActualState)
                : OpenVisionLanguageService.T("Connections.DryRunNoIssue");

        Timeline.Clear();
        ClearSelectedStepWithoutComponentSelection();
        var sequence = _project?.Sequences.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, result.SequenceId, StringComparison.Ordinal));
        for (var index = 0; index < result.Timeline.Count; index++)
        {
            var trace = result.Timeline[index];
            var authoredStep = sequence?.Steps.FirstOrDefault(step =>
                string.Equals(step.Id, trace.StepId, StringComparison.Ordinal));
            string? relatedTargetId = trace.Checkpoint?.TargetId ?? authoredStep?.TargetId;
            Timeline.Add(new RecipeDryRunStepPresentation(
                result.SequenceId,
                trace.StepId,
                _resolveComponentId(relatedTargetId),
                $"#{index + 1}",
                trace.Name,
                Format("Connections.DryRunTickRangeFormat", trace.StartedTick, trace.EndedTick),
                trace.HasIssue,
                trace.HasCheckpoint,
                trace.HasCheckpointMismatch,
                trace.Checkpoint is null
                    ? string.Empty
                    : Format(
                        "Connections.DryRunCheckpointFormat",
                        trace.Checkpoint.TargetId,
                        trace.Checkpoint.ExpectedState,
                        trace.Checkpoint.ActualState),
                trace.BoundarySnapshot));
        }

        SelectedRecipeDryRunStep = Timeline.FirstOrDefault(step => step.HasIssue)
            ?? Timeline.FirstOrDefault(step => step.HasCheckpointMismatch);

        FinalStates.Clear();
        if (result.FinalSnapshot is { } snapshot)
        {
            foreach (var prealigner in snapshot.Prealigners)
            {
                FinalStates.Add(new RecipeDryRunEquipmentStatePresentation(
                    FormatPrealignerStatus(prealigner),
                    IsPrealigner: true,
                    IsFault: prealigner.State == PrealignerState.InterlockFault));
            }

            foreach (var handoff in snapshot.InspectionHandoffs)
            {
                FinalStates.Add(new RecipeDryRunEquipmentStatePresentation(
                    FormatInspectionHandoffStatus(handoff),
                    IsInspectionHandoff: true,
                    IsFault: handoff.State == InspectionHandoffState.InterlockFault));
            }

            foreach (var handoff in snapshot.OhtHandoffs)
            {
                FinalStates.Add(new RecipeDryRunEquipmentStatePresentation(
                    FormatOhtHandoffStatus(handoff),
                    IsOhtHandoff: true,
                    IsFault: handoff.State == OhtHandoffOwnershipState.InterlockFault));
            }

            foreach (var sorter in snapshot.InspectionSortRouters)
            {
                FinalStates.Add(new RecipeDryRunEquipmentStatePresentation(
                    FormatInspectionSorterStatus(sorter),
                    IsInspectionSorter: true,
                    IsFault: sorter.State == InspectionSortRouteState.InterlockFault));
            }

            foreach (var handler in snapshot.WaferHandlers)
            {
                FinalStates.Add(new RecipeDryRunEquipmentStatePresentation(
                    FormatWaferHandlerStatus(handler),
                    IsWaferHandler: true,
                    IsFault: handler.State == WaferHandlerOwnershipState.InterlockFault));
            }

            foreach (var loadLock in snapshot.LoadLocks)
            {
                FinalStates.Add(new RecipeDryRunEquipmentStatePresentation(
                    FormatLoadLockStatus(loadLock),
                    IsLoadLock: true,
                    IsFault: loadLock.State == LoadLockState.InterlockFault));
            }

            foreach (var axis in snapshot.Axes)
            {
                FinalStates.Add(new RecipeDryRunEquipmentStatePresentation(Format(
                    "Connections.DryRunAxisStateFormat",
                    axis.Name,
                    axis.Position,
                    axis.State)));
            }

            foreach (var component in snapshot.LayoutComponents)
            {
                switch (component.Kind)
                {
                    case LayoutComponentKind.PneumaticCylinder when component.CylinderState is { } cylinder:
                        FinalStates.Add(new RecipeDryRunEquipmentStatePresentation(Format(
                            "Connections.DryRunCylinderStateFormat",
                            component.Name,
                            cylinder,
                            component.MotionProgress ?? 0)));
                        break;
                    case LayoutComponentKind.Conveyor when component.ConveyorRunning is { } running:
                        FinalStates.Add(new RecipeDryRunEquipmentStatePresentation(Format(
                            "Connections.DryRunConveyorStateFormat",
                            component.Name,
                            running ? "ON" : "OFF",
                            component.ConveyorDirection?.ToString() ?? "—")));
                        break;
                    case LayoutComponentKind.DigitalSensor when component.IsDetected is { } detected:
                        FinalStates.Add(new RecipeDryRunEquipmentStatePresentation(Format(
                            "Connections.DryRunSensorStateFormat",
                            component.Name,
                            detected ? "ON" : "OFF")));
                        break;
                    case LayoutComponentKind.Workpiece:
                        FinalStates.Add(new RecipeDryRunEquipmentStatePresentation(Format(
                            "Connections.DryRunWorkpieceStateFormat",
                            component.Name,
                            component.X,
                            component.Y)));
                        break;
                }
            }
        }

        OnPropertyChanged(nameof(HasRecipeDryRunResult));
        OnPropertyChanged(nameof(RecipeDryRunPassed));
        OnPropertyChanged(nameof(RecipeDryRunWarning));
        OnPropertyChanged(nameof(HasRecipeDryRunIssue));
    }

    private void ClearRecipeDryRun()
    {
        RecipeDryRunResult = null;
        RecipeDryRunStatusText = string.Empty;
        RecipeDryRunDetailText = string.Empty;
        RecipeDryRunIssueText = string.Empty;
        ClearSelectedStepWithoutComponentSelection();
        Timeline.Clear();
        FinalStates.Clear();
        OnPropertyChanged(nameof(HasRecipeDryRunResult));
        OnPropertyChanged(nameof(RecipeDryRunPassed));
        OnPropertyChanged(nameof(RecipeDryRunWarning));
        OnPropertyChanged(nameof(HasRecipeDryRunIssue));
    }

    private void ClearSelectedStepWithoutComponentSelection()
    {
        if (_selectedRecipeDryRunStep is null)
        {
            return;
        }

        _selectedRecipeDryRunStep = null;
        OnPropertyChanged(nameof(SelectedRecipeDryRunStep));
    }

    private void RaiseReadinessChanged()
    {
        OnPropertyChanged(nameof(ReadinessPassed));
        OnPropertyChanged(nameof(ReadinessStatusText));
        OnPropertyChanged(nameof(ReadinessDetailText));
        RaiseCommandsCanExecuteChanged();
    }

    private void RaiseCommandsCanExecuteChanged()
    {
        _validateSimulationReadinessCommand.RaiseCanExecuteChanged();
        _runRecipeDryRunCommand.RaiseCanExecuteChanged();
        _openRecipeDryRunStepCommand.RaiseCanExecuteChanged();
        _playRecipeDryRunStepCommand.RaiseCanExecuteChanged();
    }

    private static string Format(string key, params object[] args) =>
        string.Format(CultureInfo.CurrentCulture, OpenVisionLanguageService.T(key), args);

    internal static string FormatLoadLockStatus(LoadLockSnapshot snapshot) => Format(
        "Connections.DryRunLoadLockStateFormat",
        snapshot.Name,
        OpenVisionLanguageService.T($"Connections.LoadLockState.{snapshot.State}"),
        OpenVisionLanguageService.T(snapshot.IsVacuumReady
            ? "Connections.LoadLockReady"
            : "Connections.LoadLockNotReady"),
        OpenVisionLanguageService.T(snapshot.IsAtmosphereReady
            ? "Connections.LoadLockReady"
            : "Connections.LoadLockNotReady"),
        OpenVisionLanguageService.T(snapshot.IsOuterDoorPermitted
            ? "Connections.LoadLockDoorAllowed"
            : "Connections.LoadLockDoorBlocked"),
        OpenVisionLanguageService.T(snapshot.IsInnerDoorPermitted
            ? "Connections.LoadLockDoorAllowed"
            : "Connections.LoadLockDoorBlocked"));

    internal static string FormatWaferHandlerStatus(WaferHandlerSnapshot snapshot) => Format(
        "Connections.DryRunWaferHandlerStateFormat",
        snapshot.Name,
        OpenVisionLanguageService.T($"Connections.WaferHandlerState.{snapshot.State}"),
        snapshot.HorizontalPosition,
        snapshot.VerticalPosition,
        OpenVisionLanguageService.T(snapshot.IsGateOpen
            ? "Connections.WaferHandlerGateOpen"
            : "Connections.WaferHandlerGateClosed"));

    internal static string FormatInspectionSorterStatus(InspectionSortRouterSnapshot snapshot) => Format(
        "Connections.DryRunInspectionSorterStateFormat",
        snapshot.Name,
        OpenVisionLanguageService.T($"Connections.InspectionSortState.{snapshot.State}"),
        OpenVisionLanguageService.T(snapshot.Decision is { } decision
            ? $"Connections.InspectionSortDecision.{decision}"
            : "Connections.InspectionSortDecision.Awaiting"));

    internal static string FormatInspectionHandoffStatus(InspectionHandoffSnapshot snapshot) => Format(
        "Connections.DryRunInspectionHandoffStateFormat",
        snapshot.Name,
        OpenVisionLanguageService.T($"Connections.InspectionHandoffState.{snapshot.State}"),
        OpenVisionLanguageService.T(snapshot.Decision is { } decision
            ? $"Connections.InspectionSortDecision.{decision}"
            : "Connections.InspectionSortDecision.Awaiting"),
        OpenVisionLanguageService.T(snapshot.IsMaterialPresent
            ? "Connections.InspectionMaterialPresent"
            : "Connections.InspectionMaterialAbsent"),
        OpenVisionLanguageService.T(snapshot.IsResultAccepted
            ? "Connections.InspectionResultAccepted"
            : "Connections.InspectionResultPending"));

    internal static string FormatOhtHandoffStatus(OhtHandoffSnapshot snapshot) => Format(
        "Connections.DryRunOhtHandoffStateFormat",
        snapshot.Name,
        OpenVisionLanguageService.T($"Connections.OhtHandoffState.{snapshot.State}"),
        OpenVisionLanguageService.T(snapshot.IsRouteAvailable
            ? "Connections.OhtRouteAvailable"
            : "Connections.OhtRouteBlocked"),
        OpenVisionLanguageService.T(snapshot.IsVehicleDocked
            ? "Connections.OhtVehicleDocked"
            : "Connections.OhtVehicleAway"),
        OpenVisionLanguageService.T(snapshot.IsLoadPortReady
            ? "Connections.OhtLoadPortReady"
            : "Connections.OhtLoadPortNotReady"));

    internal static string FormatPrealignerStatus(PrealignerSnapshot snapshot) => Format(
        "Connections.DryRunPrealignerStateFormat",
        snapshot.Name,
        OpenVisionLanguageService.T($"Connections.PrealignerState.{snapshot.State}"),
        snapshot.RotaryPositionDegrees,
        snapshot.AlignmentTargetDegrees,
        OpenVisionLanguageService.T(snapshot.IsWaferPresent
            ? "Connections.PrealignerWaferPresent"
            : "Connections.PrealignerWaferAbsent"));
}
