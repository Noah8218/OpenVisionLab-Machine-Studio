using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows.Input;
using OpenVisionLab;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Sequence.Runtime;
using OpenVisionLab.Machine.Simulation.Axis;
using OpenVisionLab.Machine.Simulation.Commands;
using OpenVisionLab.Machine.Simulation.Engine;
using OpenVisionLab.Machine.Simulation.Events;
using OpenVisionLab.Machine.Simulation.Layout;
using OpenVisionLab.Machine.Simulation.Snapshots;

namespace OpenVisionLab.MachineStudio.ViewModel;

public enum RuntimeWatchKind
{
    Sequence,
    Axis,
    Signal,
    Equipment
}

public sealed record RuntimeWatchTarget(RuntimeWatchKind Kind, string Id, string Name)
{
    public string DisplayText => $"{Kind} · {Name} · {Id}";
}

public sealed class RuntimeWatchItem : ViewModelBase
{
    private RuntimeWatchTarget _target;
    private string _valueText = string.Empty;

    public RuntimeWatchItem(RuntimeWatchTarget target)
    {
        _target = target ?? throw new ArgumentNullException(nameof(target));
    }

    public RuntimeWatchTarget Target => _target;
    public string Name => _target.Name;
    public string KindText => _target.Kind.ToString();

    public string ValueText
    {
        get => _valueText;
        internal set => SetProperty(ref _valueText, value);
    }

    internal void UpdateTarget(RuntimeWatchTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (_target == target)
        {
            return;
        }

        _target = target;
        OnPropertyChanged(nameof(Target));
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(KindText));
    }
}

public sealed class SequenceBreakpointItem : ViewModelBase
{
    private bool _isEnabled;

    public SequenceBreakpointItem(string sequenceId, string stepId, string displayText)
    {
        SequenceId = sequenceId;
        StepId = stepId;
        DisplayText = displayText;
    }

    public string SequenceId { get; }
    public string StepId { get; }
    public string DisplayText { get; }

    public bool IsEnabled
    {
        get => _isEnabled;
        internal set => SetProperty(ref _isEnabled, value);
    }
}

public sealed class RuntimeDebuggerViewModel : ViewModelBase, IDisposable
{
    private readonly Func<SimulationCommand, Task<SimulationCommandResult>> _dispatch;
    private readonly RuntimeTimelineViewModel _timeline = new();
    private readonly RuntimeAlarmCollectionViewModel _alarmCollection;
    private readonly RuntimeDebuggerWatchTargetCatalog _watchTargetCatalog = new();
    private SimulationSnapshot? _latestSnapshot;
    private string? _projectId;
    private bool _defaultWatchApplied;
    private bool _isEnabled;
    private bool _isOperationPending;
    private RuntimeWatchTarget? _selectedWatchTarget;
    private RuntimeWatchItem? _selectedWatch;
    private SequenceBreakpointItem? _selectedBreakpoint;
    private string _operationStatusText = T(
        "Debugger.ReadyHint",
        "일시정지 후 다음 시퀀스 경계로 이동하거나 중단점을 설정하세요.",
        "Pause to move to the next sequence boundary or configure a breakpoint.");
    private ICommand? _semanticStepCommand;
    private ICommand? _toggleBreakpointCommand;
    private ICommand? _addWatchCommand;
    private ICommand? _removeWatchCommand;
    private ICommand? _clearTimelineCommand;
    private int _operationGeneration;
    private int _disposed;

    public RuntimeDebuggerViewModel(Func<SimulationCommand, Task<SimulationCommandResult>> dispatch)
    {
        _dispatch = dispatch ?? throw new ArgumentNullException(nameof(dispatch));
        _alarmCollection = new(
            () => IsEnabled && !IsDisposed,
            SequenceName,
            status => OperationStatusText = status);
        _alarmCollection.PropertyChanged += OnAlarmCollectionPropertyChanged;
    }

    public ObservableCollection<SequenceBreakpointItem> Breakpoints { get; } = new();
    public ObservableCollection<RuntimeWatchTarget> WatchTargets { get; } = new();
    public ObservableCollection<RuntimeWatchItem> Watches { get; } = new();
    public ObservableCollection<RuntimeTimelineItem> Timeline => _timeline.Items;
    public IReadOnlyList<RuntimeTimelineFilterItem> TimelineFilters => _timeline.FilterOptions;
    public IReadOnlyList<RuntimeTimelineFilterItem> TimelineSeverityFilters => _timeline.SeverityFilterOptions;
    public ObservableCollection<RuntimeAlarmItem> Alarms => _alarmCollection.Alarms;
    public ObservableCollection<RuntimeAlarmItem> AlarmHistory => _alarmCollection.AlarmHistory;
    public ObservableCollection<RuntimeAlarmItem> VisibleAlarmHistory => _alarmCollection.VisibleAlarmHistory;
    public IReadOnlyList<RuntimeAlarmHistoryFilterItem> AlarmHistoryFilters => _alarmCollection.AlarmHistoryFilters;

    public RuntimeWatchTarget? SelectedWatchTarget
    {
        get => _selectedWatchTarget;
        set
        {
            if (IsDisposed)
            {
                return;
            }

            if (SetProperty(ref _selectedWatchTarget, value))
            {
                InvalidateCommands();
            }
        }
    }

    public RuntimeWatchItem? SelectedWatch
    {
        get => _selectedWatch;
        set
        {
            if (IsDisposed)
            {
                return;
            }

            if (SetProperty(ref _selectedWatch, value))
            {
                InvalidateCommands();
            }
        }
    }

    public SequenceBreakpointItem? SelectedBreakpoint
    {
        get => _selectedBreakpoint;
        set
        {
            if (IsDisposed)
            {
                return;
            }

            if (SetProperty(ref _selectedBreakpoint, value))
            {
                OnPropertyChanged(nameof(BreakpointActionText));
                InvalidateCommands();
            }
        }
    }

    public bool IsEnabled => _isEnabled;
    public bool IsOperationPending => _isOperationPending;
    public bool HasTimeline => _timeline.HasItems;
    public bool HasAlarms => _alarmCollection.HasAlarms;
    public bool HasAlarmHistory => _alarmCollection.HasAlarmHistory;
    public bool HasVisibleAlarmHistory => _alarmCollection.HasVisibleAlarmHistory;
    public bool HasAlarmHistoryEmptyState => _alarmCollection.HasAlarmHistoryEmptyState;
    public bool HasWatches => Watches.Count > 0;
    public int UnacknowledgedAlarmCount => _alarmCollection.UnacknowledgedAlarmCount;

    public string SequenceStateText
    {
        get
        {
            var sequence = ActiveSequence;
            return sequence is null
                ? T("Debugger.NoActiveSequence", "활성 시퀀스 없음", "No active sequence")
                : $"{SequenceName(sequence.SequenceId)} · "
                    + T(
                        $"Equipment.State.{sequence.Status}",
                        sequence.Status.ToString(),
                        sequence.Status.ToString());
        }
    }

    public string CurrentStepText
    {
        get
        {
            var sequence = ActiveSequence;
            if (sequence?.CurrentStepId is not { } stepId)
            {
                return T("Debugger.NoCurrentStep", "현재 단계 없음", "No current step");
            }

            var activeSequenceId = sequence.ActiveSequenceId ?? sequence.SequenceId;
            return $"{StepName(activeSequenceId, stepId)} · {stepId}";
        }
    }

    public string PauseReasonText => _latestSnapshot?.SequenceDebug.PauseReason switch
    {
        SequenceDebugPauseReason.SemanticStep => T("Debugger.PauseSemanticStep", "다음 단계 경계", "Next-step boundary"),
        SequenceDebugPauseReason.Breakpoint => T("Debugger.PauseBreakpoint", "중단점", "Breakpoint"),
        SequenceDebugPauseReason.SequenceCompleted => T("Debugger.PauseCompleted", "시퀀스 완료", "Sequence completed"),
        SequenceDebugPauseReason.SequenceFaulted => T("Debugger.PauseFaulted", "시퀀스 오류", "Sequence faulted"),
        SequenceDebugPauseReason.SequenceAborted => T("Debugger.PauseAborted", "시퀀스 중단", "Sequence aborted"),
        SequenceDebugPauseReason.FixedTick => T("Debugger.PauseFixedTick", "고정 틱", "Fixed tick"),
        SequenceDebugPauseReason.User => T("Debugger.PauseUser", "사용자 일시정지", "User pause"),
        _ => _latestSnapshot?.RunMode == SimulationRunMode.Paused
            ? T("Debugger.PauseReady", "일시정지 · 디버깅 준비", "Paused · debugger ready")
            : T("Debugger.RuntimeActive", "런타임 실행 중", "Runtime active")
    };

    public string BreakpointActionText => SelectedBreakpoint?.IsEnabled == true
        ? T("Debugger.RemoveBreakpoint", "중단점 해제", "Remove breakpoint")
        : T("Debugger.SetBreakpoint", "중단점 설정", "Set breakpoint");

    public string AlarmSummaryText => _alarmCollection.AlarmSummaryText;
    public string AlarmAcknowledgementSummaryText => _alarmCollection.AlarmAcknowledgementSummaryText;
    public string AlarmHistorySummaryText => _alarmCollection.AlarmHistorySummaryText;
    public string AlarmHistoryEmptyText => _alarmCollection.AlarmHistoryEmptyText;

    public RuntimeAlarmHistoryFilterItem? SelectedAlarmHistoryFilter
    {
        get => _alarmCollection.SelectedAlarmHistoryFilter;
        set
        {
            if (IsDisposed || !_alarmCollection.SetHistoryFilter(value?.State))
            {
                return;
            }

            OnPropertyChanged(nameof(SelectedAlarmHistoryFilter));
        }
    }

    public string TimelineSummaryText => _timeline.SummaryText;

    public string TimelineEmptyText => T(
        "Debugger.TimelineEmptyFiltered",
        "선택한 조건의 이벤트가 없습니다.",
        "No events match the selected filters.");

    public RuntimeTimelineFilterItem? SelectedTimelineFilter
    {
        get => _timeline.SelectedFilter;
        set
        {
            if (IsDisposed || !_timeline.SetCategory(value?.Key))
            {
                return;
            }

            OnPropertyChanged(nameof(SelectedTimelineFilter));
            RefreshTimelinePresentation();
        }
    }

    public RuntimeTimelineFilterItem? SelectedTimelineSeverityFilter
    {
        get => _timeline.SelectedSeverityFilter;
        set
        {
            if (IsDisposed || !_timeline.SetSeverity(value?.Key))
            {
                return;
            }

            OnPropertyChanged(nameof(SelectedTimelineSeverityFilter));
            RefreshTimelinePresentation();
        }
    }

    public string OperationStatusText
    {
        get => _operationStatusText;
        private set
        {
            if (!IsDisposed)
            {
                SetProperty(ref _operationStatusText, value);
            }
        }
    }

    public ICommand SemanticStepCommand => _semanticStepCommand ??= new AsyncRelayCommand(
        _ => RunOperationAsync(StepSequenceAsync),
        _ => CanStepSequence(),
        useCommandManagerRequery: false);

    public ICommand ToggleBreakpointCommand => _toggleBreakpointCommand ??= new AsyncRelayCommand(
        _ => RunOperationAsync(ToggleBreakpointAsync),
        _ => !IsDisposed && IsEnabled && !_isOperationPending && SelectedBreakpoint is not null,
        useCommandManagerRequery: false);

    public ICommand AddWatchCommand => _addWatchCommand ??= new RelayCommand(
        _ => AddSelectedWatch(),
        _ => !IsDisposed
            && IsEnabled
            && SelectedWatchTarget is not null
            && !Watches.Any(item => item.Target == SelectedWatchTarget),
        useCommandManagerRequery: false);

    public ICommand RemoveWatchCommand => _removeWatchCommand ??= new RelayCommand(
        _ => RemoveSelectedWatch(),
        _ => !IsDisposed && SelectedWatch is not null,
        useCommandManagerRequery: false);

    public ICommand ClearTimelineCommand => _clearTimelineCommand ??= new RelayCommand(
        _ => ClearTimeline(),
        _ => !IsDisposed && _timeline.HasEvents,
        useCommandManagerRequery: false);

    public ICommand AcknowledgeAlarmCommand => _alarmCollection.AcknowledgeAlarmCommand;
    public ICommand AcknowledgeAllAlarmsCommand => _alarmCollection.AcknowledgeAllAlarmsCommand;

    public void LoadProject(MachineProjectDocument project, bool resetSession)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (IsDisposed)
        {
            return;
        }

        Interlocked.Increment(ref _operationGeneration);
        var projectChanged = !string.Equals(_projectId, project.Id, StringComparison.Ordinal);
        _projectId = project.Id;

        if (resetSession || projectChanged)
        {
            _latestSnapshot = null;
            OperationStatusText = T(
                "Debugger.ReadyHint",
                "일시정지 후 다음 시퀀스 경계로 이동하거나 중단점을 설정하세요.",
                "Pause to move to the next sequence boundary or configure a breakpoint.");
            Watches.Clear();
            _timeline.Clear();
            _alarmCollection.Reset();
            _defaultWatchApplied = false;
            SelectedWatch = null;
            OnPropertyChanged(nameof(HasTimeline));
            OnPropertyChanged(nameof(TimelineFilters));
            OnPropertyChanged(nameof(SelectedTimelineFilter));
            OnPropertyChanged(nameof(TimelineSeverityFilters));
            OnPropertyChanged(nameof(SelectedTimelineSeverityFilter));
            OnPropertyChanged(nameof(HasWatches));
        }

        var enabledBreakpoints = resetSession || projectChanged
            ? []
            : Breakpoints
                .Where(item => item.IsEnabled)
                .Select(item => (item.SequenceId, item.StepId))
                .ToHashSet();
        var selectedKey = SelectedBreakpoint is null
            ? default((string SequenceId, string StepId)?)
            : (SelectedBreakpoint.SequenceId, SelectedBreakpoint.StepId);
        Breakpoints.Clear();
        foreach (var sequence in project.Sequences)
        {
            foreach (var step in sequence.Steps)
            {
                var item = new SequenceBreakpointItem(
                    sequence.Id,
                    step.Id,
                    $"{sequence.Name} · {step.Name} · {step.Id}")
                {
                    IsEnabled = enabledBreakpoints.Contains((sequence.Id, step.Id))
                };
                Breakpoints.Add(item);
            }
        }

        SelectedBreakpoint = selectedKey is { } key
            ? Breakpoints.FirstOrDefault(item => item.SequenceId == key.SequenceId && item.StepId == key.StepId)
            : Breakpoints.FirstOrDefault();
        RefreshWatchTargets();
        RefreshSnapshotPresentation();
    }

    public void ApplySnapshot(SimulationSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (IsDisposed)
        {
            return;
        }

        _latestSnapshot = snapshot;
        var enabledBreakpoints = snapshot.SequenceDebug.Breakpoints
            .Select(item => (item.SequenceId, item.StepId))
            .ToHashSet();
        foreach (var item in Breakpoints)
        {
            item.IsEnabled = enabledBreakpoints.Contains((item.SequenceId, item.StepId));
        }

        RefreshWatchTargets();
        if (!_defaultWatchApplied && snapshot.Sequences.FirstOrDefault() is { } sequence)
        {
            var target = WatchTargets.FirstOrDefault(item =>
                item.Kind == RuntimeWatchKind.Sequence && item.Id == sequence.SequenceId);
            if (target is not null)
            {
                Watches.Add(new RuntimeWatchItem(target));
            }
            _defaultWatchApplied = true;
        }

        foreach (var watch in Watches)
        {
            watch.ValueText = FormatWatchValue(watch.Target, snapshot);
        }
        _alarmCollection.ApplySnapshot(snapshot);
        RefreshSnapshotPresentation();
        InvalidateCommands();
    }

    public void ApplyEvent(SimulationEvent runtimeEvent)
    {
        ArgumentNullException.ThrowIfNull(runtimeEvent);
        if (IsDisposed)
        {
            return;
        }

        _timeline.Add(runtimeEvent);
        RefreshTimelinePresentation();
    }

    public void SetEnabled(bool value, bool invalidateCommands)
    {
        if (IsDisposed)
        {
            return;
        }

        if (SetProperty(ref _isEnabled, value, nameof(IsEnabled)) && invalidateCommands)
        {
            InvalidateCommands();
        }
    }

    public void InvalidateCommands()
    {
        (_semanticStepCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (_toggleBreakpointCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (_addWatchCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (_removeWatchCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (_clearTimelineCommand as RelayCommand)?.RaiseCanExecuteChanged();
        _alarmCollection.InvalidateCommands();
    }

    public void RefreshLocalization()
    {
        if (IsDisposed)
        {
            return;
        }

        _timeline.RefreshLocalization();
        RefreshTimelinePresentation();
        if (_latestSnapshot is not null)
        {
            foreach (var watch in Watches)
            {
                watch.ValueText = FormatWatchValue(watch.Target, _latestSnapshot);
            }
        }
        _alarmCollection.RefreshLocalization();
        RefreshSnapshotPresentation();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Interlocked.Increment(ref _operationGeneration);
        _alarmCollection.PropertyChanged -= OnAlarmCollectionPropertyChanged;
        InvalidateCommands();
    }

    private SequenceExecutionSnapshot? ActiveSequence => _latestSnapshot?.Sequences
        .FirstOrDefault(sequence => sequence.Status == SequenceExecutionStatus.Running)
        ?? _latestSnapshot?.Sequences.FirstOrDefault();

    private bool CanStepSequence() => !IsDisposed
        && IsEnabled
        && !_isOperationPending
        && _latestSnapshot?.RunMode == SimulationRunMode.Paused
        && ActiveSequence?.Status == SequenceExecutionStatus.Running;

    private async Task StepSequenceAsync()
    {
        if (IsDisposed)
        {
            return;
        }

        var operationGeneration = Volatile.Read(ref _operationGeneration);
        if (ActiveSequence is not { } sequence)
        {
            return;
        }
        var result = await _dispatch(new StepSequenceCommand(sequence.SequenceId));
        if (IsDisposed || operationGeneration != Volatile.Read(ref _operationGeneration))
        {
            return;
        }

        OperationStatusText = result.IsAccepted
            ? T("Debugger.StepAccepted", "다음 시퀀스 경계까지 실행했습니다.", "Advanced to the next sequence boundary.")
            : FormatRejected(result);
    }

    private async Task ToggleBreakpointAsync()
    {
        if (IsDisposed)
        {
            return;
        }

        var operationGeneration = Volatile.Read(ref _operationGeneration);
        if (SelectedBreakpoint is not { } breakpoint)
        {
            return;
        }
        var enable = !breakpoint.IsEnabled;
        var result = await _dispatch(new SetSequenceBreakpointCommand(
            breakpoint.SequenceId,
            breakpoint.StepId,
            enable));
        if (IsDisposed || operationGeneration != Volatile.Read(ref _operationGeneration))
        {
            return;
        }

        OperationStatusText = result.IsAccepted
            ? T(
                enable ? "Debugger.BreakpointSet" : "Debugger.BreakpointRemoved",
                enable ? "중단점을 설정했습니다." : "중단점을 해제했습니다.",
                enable ? "Breakpoint set." : "Breakpoint removed.")
            : FormatRejected(result);
    }

    private async Task RunOperationAsync(Func<Task> operation)
    {
        if (IsDisposed || _isOperationPending)
        {
            return;
        }
        SetProperty(ref _isOperationPending, true, nameof(IsOperationPending));
        InvalidateCommands();
        try
        {
            await operation();
        }
        finally
        {
            if (IsDisposed)
            {
                _isOperationPending = false;
            }
            else
            {
                SetProperty(ref _isOperationPending, false, nameof(IsOperationPending));
                InvalidateCommands();
            }
        }
    }

    private void AddSelectedWatch()
    {
        if (IsDisposed)
        {
            return;
        }

        if (SelectedWatchTarget is not { } target || Watches.Any(item => item.Target == target))
        {
            return;
        }
        var item = new RuntimeWatchItem(target);
        if (_latestSnapshot is not null)
        {
            item.ValueText = FormatWatchValue(target, _latestSnapshot);
        }
        Watches.Add(item);
        SelectedWatch = item;
        OnPropertyChanged(nameof(HasWatches));
        InvalidateCommands();
    }

    private void RemoveSelectedWatch()
    {
        if (IsDisposed)
        {
            return;
        }

        if (SelectedWatch is not { } item)
        {
            return;
        }
        Watches.Remove(item);
        SelectedWatch = null;
        OnPropertyChanged(nameof(HasWatches));
        InvalidateCommands();
    }

    private void ClearTimeline()
    {
        if (IsDisposed)
        {
            return;
        }

        _timeline.Clear();
        RefreshTimelinePresentation();
    }

    private void RefreshWatchTargets()
    {
        if (_latestSnapshot is null)
        {
            return;
        }
        IReadOnlyList<RuntimeWatchTarget> targets = _watchTargetCatalog.Build(
            _latestSnapshot,
            SequenceName);
        (RuntimeWatchKind Kind, string Id)? selectedKey = SelectedWatchTarget is null
            ? null
            : (SelectedWatchTarget.Kind, SelectedWatchTarget.Id);
        if (WatchTargets.SequenceEqual(targets))
        {
            return;
        }

        var targetsByKey = targets.ToDictionary(item => (item.Kind, item.Id));
        foreach (var watch in Watches)
        {
            if (targetsByKey.TryGetValue((watch.Target.Kind, watch.Target.Id), out var target))
            {
                watch.UpdateTarget(target);
            }
        }

        WatchTargets.Clear();
        foreach (var target in targets)
        {
            WatchTargets.Add(target);
        }
        SelectedWatchTarget = WatchTargets.FirstOrDefault(item => selectedKey is { }
            && item.Kind == selectedKey.Value.Kind
            && item.Id == selectedKey.Value.Id)
            ?? WatchTargets.FirstOrDefault();
    }

    private void RefreshTimelinePresentation()
    {
        OnPropertyChanged(nameof(HasTimeline));
        OnPropertyChanged(nameof(TimelineSummaryText));
        OnPropertyChanged(nameof(TimelineEmptyText));
        OnPropertyChanged(nameof(TimelineFilters));
        OnPropertyChanged(nameof(SelectedTimelineFilter));
        OnPropertyChanged(nameof(TimelineSeverityFilters));
        OnPropertyChanged(nameof(SelectedTimelineSeverityFilter));
        InvalidateCommands();
    }

    private string FormatWatchValue(RuntimeWatchTarget target, SimulationSnapshot snapshot) => target.Kind switch
    {
        RuntimeWatchKind.Sequence => snapshot.Sequences.FirstOrDefault(item => item.SequenceId == target.Id) is { } sequence
            ? $"{sequence.Status} · {StepName(sequence.ActiveSequenceId ?? sequence.SequenceId, sequence.CurrentStepId)} · {sequence.ElapsedInStep.TotalMilliseconds:0} ms"
            : T("Shell.Unavailable"),
        RuntimeWatchKind.Axis => snapshot.Axes.FirstOrDefault(item => item.Id == target.Id) is { } axis
            ? $"{axis.State} · {axis.Position:0.###} · {axis.Velocity:0.###}/s"
            : T("Shell.Unavailable"),
        RuntimeWatchKind.Signal => snapshot.Signals.FirstOrDefault(item => item.Id == target.Id) is { } signal
            ? $"{(signal.Value ? T("Shell.SignalOn") : T("Shell.SignalOff"))} · r{snapshot.SignalRevision}"
            : T("Shell.Unavailable"),
        RuntimeWatchKind.Equipment => snapshot.LayoutComponents.FirstOrDefault(item => item.Id == target.Id) is { } component
            ? component.CylinderState?.ToString()
                ?? (component.ConveyorRunning is { } running
                    ? $"{(running ? T("Shell.Running") : T("Shell.Paused"))} · {component.ConveyorDirection}"
                    : component.IsDetected is { } detected
                        ? (detected ? T("Shell.SignalOn") : T("Shell.SignalOff"))
                        : component.Kind.ToString())
            : T("Shell.Unavailable"),
        _ => T("Shell.Unavailable")
    };

    private string SequenceName(string sequenceId) => Breakpoints
        .FirstOrDefault(item => item.SequenceId == sequenceId)?.DisplayText.Split('·')[0].Trim()
        ?? sequenceId;

    private string StepName(string sequenceId, string? stepId)
    {
        if (stepId is null)
        {
            return T("Debugger.NoCurrentStep", "현재 단계 없음", "No current step");
        }
        var item = Breakpoints.FirstOrDefault(candidate =>
            candidate.SequenceId == sequenceId && candidate.StepId == stepId);
        return item?.DisplayText.Split('·').ElementAtOrDefault(1)?.Trim() ?? stepId;
    }

    private void OnAlarmCollectionPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (!IsDisposed && args.PropertyName is { } propertyName)
        {
            OnPropertyChanged(propertyName);
        }
    }

    private bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    private void RefreshSnapshotPresentation()
    {
        OnPropertyChanged(nameof(SequenceStateText));
        OnPropertyChanged(nameof(CurrentStepText));
        OnPropertyChanged(nameof(PauseReasonText));
        OnPropertyChanged(nameof(BreakpointActionText));
        OnPropertyChanged(nameof(TimelineSummaryText));
    }

    private static string FormatRejected(SimulationCommandResult result) => string.Format(
        CultureInfo.CurrentCulture,
        T("Debugger.CommandRejected", "명령 거부 · {0} · {1}", "Command rejected · {0} · {1}"),
        result.ErrorCode,
        result.Detail);

    private static string T(string key, string korean, string english) =>
        OpenVisionLanguageService.T(key, korean, english);

    private static string T(string key) => OpenVisionLanguageService.T(key);
}
