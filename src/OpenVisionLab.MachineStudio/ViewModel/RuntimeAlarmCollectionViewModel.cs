using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using OpenVisionLab;
using OpenVisionLab.Machine.Sequence.Runtime;
using OpenVisionLab.Machine.Simulation.Axis;
using OpenVisionLab.Machine.Simulation.Engine;
using OpenVisionLab.Machine.Simulation.Layout;
using OpenVisionLab.Machine.Simulation.Snapshots;

namespace OpenVisionLab.MachineStudio.ViewModel;

public sealed class RuntimeAlarmItem : ViewModelBase
{
    private string _source;
    private string _state;
    private string _recoveryText;
    private bool _isActive = true;
    private bool _isAcknowledged;
    private long _lastSeenTick;
    private TimeSpan _lastSeenTime;
    private long? _clearedTick;
    private TimeSpan? _clearedTime;
    private string _lifecycleText = string.Empty;
    private string _occurrenceText = string.Empty;
    private string _clearedAtText = string.Empty;
    private string _acknowledgeActionText = string.Empty;

    internal RuntimeAlarmItem(
        string alarmKey,
        string source,
        string state,
        string recoveryKey,
        string recoveryText,
        long firstSeenTick,
        TimeSpan firstSeenTime)
    {
        AlarmKey = alarmKey;
        _source = source;
        _state = state;
        _recoveryText = recoveryText;
        RecoveryKey = recoveryKey;
        FirstSeenTick = firstSeenTick;
        FirstSeenTime = firstSeenTime;
        _lastSeenTick = firstSeenTick;
        _lastSeenTime = firstSeenTime;
    }

    public string AlarmKey { get; }
    public string Source
    {
        get => _source;
        private set => SetProperty(ref _source, value);
    }

    public string State
    {
        get => _state;
        private set => SetProperty(ref _state, value);
    }

    public string RecoveryText
    {
        get => _recoveryText;
        private set => SetProperty(ref _recoveryText, value);
    }

    public long FirstSeenTick { get; }
    public TimeSpan FirstSeenTime { get; }
    public long LastSeenTick => _lastSeenTick;
    public TimeSpan LastSeenTime => _lastSeenTime;
    public long? ClearedTick => _clearedTick;
    public TimeSpan? ClearedTime => _clearedTime;

    public bool IsActive
    {
        get => _isActive;
        private set
        {
            if (SetProperty(ref _isActive, value))
            {
                OnPropertyChanged(nameof(CanAcknowledge));
            }
        }
    }

    public bool IsAcknowledged
    {
        get => _isAcknowledged;
        private set
        {
            if (SetProperty(ref _isAcknowledged, value))
            {
                OnPropertyChanged(nameof(CanAcknowledge));
            }
        }
    }

    public bool CanAcknowledge => IsActive && !IsAcknowledged;
    public string LifecycleText
    {
        get => _lifecycleText;
        private set => SetProperty(ref _lifecycleText, value);
    }

    public string OccurrenceText
    {
        get => _occurrenceText;
        private set => SetProperty(ref _occurrenceText, value);
    }

    public string ClearedAtText
    {
        get => _clearedAtText;
        private set => SetProperty(ref _clearedAtText, value);
    }

    public string AcknowledgeActionText
    {
        get => _acknowledgeActionText;
        private set => SetProperty(ref _acknowledgeActionText, value);
    }

    internal string RecoveryKey { get; }

    internal void MarkSeen(long tick, TimeSpan time)
    {
        LastSeenTickChanged(tick, time);
        IsActive = true;
    }

    internal void MarkCleared(long tick, TimeSpan time)
    {
        if (!IsActive)
        {
            return;
        }

        IsActive = false;
        _clearedTick = tick;
        _clearedTime = time;
        OnPropertyChanged(nameof(ClearedTick));
        OnPropertyChanged(nameof(ClearedTime));
    }

    internal void Acknowledge() => IsAcknowledged = true;

    internal void SetPresentation(
        string recoveryText,
        string lifecycleText,
        string occurrenceText,
        string clearedAtText,
        string acknowledgeActionText)
    {
        RecoveryText = recoveryText;
        LifecycleText = lifecycleText;
        OccurrenceText = occurrenceText;
        ClearedAtText = clearedAtText;
        AcknowledgeActionText = acknowledgeActionText;
    }

    private void LastSeenTickChanged(long tick, TimeSpan time)
    {
        _lastSeenTick = tick;
        _lastSeenTime = time;
        OnPropertyChanged(nameof(LastSeenTick));
        OnPropertyChanged(nameof(LastSeenTime));
    }
}

internal sealed class RuntimeAlarmCollectionViewModel : ViewModelBase
{
    private const int AlarmHistoryRetentionLimit = 200;
    private readonly Func<bool> _isEnabled;
    private readonly Func<string, string> _sequenceName;
    private readonly Action<string> _setOperationStatus;
    private readonly Dictionary<string, RuntimeAlarmItem> _activeAlarms = new(StringComparer.Ordinal);
    private SimulationSnapshot? _latestSnapshot;
    private ICommand? _acknowledgeAlarmCommand;
    private ICommand? _acknowledgeAllAlarmsCommand;

    internal RuntimeAlarmCollectionViewModel(
        Func<bool> isEnabled,
        Func<string, string> sequenceName,
        Action<string> setOperationStatus)
    {
        _isEnabled = isEnabled ?? throw new ArgumentNullException(nameof(isEnabled));
        _sequenceName = sequenceName ?? throw new ArgumentNullException(nameof(sequenceName));
        _setOperationStatus = setOperationStatus ?? throw new ArgumentNullException(nameof(setOperationStatus));
    }

    internal ObservableCollection<RuntimeAlarmItem> Alarms { get; } = new();
    internal ObservableCollection<RuntimeAlarmItem> AlarmHistory { get; } = new();
    internal bool HasAlarms => Alarms.Count > 0;
    internal bool HasAlarmHistory => AlarmHistory.Count > 0;
    internal int UnacknowledgedAlarmCount => Alarms.Count(item => item.CanAcknowledge);

    internal string AlarmSummaryText => Alarms.Count switch
    {
        0 => T("Debugger.NoAlarms", "현재 알람 없음", "No current alarms"),
        1 => T("Debugger.OneAlarm", "알람 1건", "1 alarm"),
        _ => string.Format(CultureInfo.CurrentCulture, T("Debugger.AlarmCount", "알람 {0}건", "{0} alarms"), Alarms.Count)
    };

    internal string AlarmAcknowledgementSummaryText => UnacknowledgedAlarmCount switch
    {
        0 => T("Debugger.AllAlarmsAcknowledged", "현재 활성 알람을 모두 확인했습니다.", "All current alarms are acknowledged."),
        1 => T("Debugger.OneUnacknowledgedAlarm", "미확인 활성 알람 1건", "1 unacknowledged alarm"),
        _ => string.Format(
            CultureInfo.CurrentCulture,
            T("Debugger.UnacknowledgedAlarmCount", "미확인 활성 알람 {0}건", "{0} unacknowledged alarms"),
            UnacknowledgedAlarmCount)
    };

    internal string AlarmHistorySummaryText => AlarmHistory.Count == 0
        ? T("Debugger.NoAlarmHistory", "알람 기록 없음", "No alarm history")
        : string.Format(
            CultureInfo.CurrentCulture,
            T("Debugger.AlarmHistoryCount", "알람 기록 {0}/200건", "Alarm history {0}/200"),
            AlarmHistory.Count);

    internal ICommand AcknowledgeAlarmCommand => _acknowledgeAlarmCommand ??= new RelayCommand(
        parameter => AcknowledgeAlarm(parameter as RuntimeAlarmItem),
        parameter => _isEnabled()
            && parameter is RuntimeAlarmItem item
            && item.CanAcknowledge,
        useCommandManagerRequery: false);

    internal ICommand AcknowledgeAllAlarmsCommand => _acknowledgeAllAlarmsCommand ??= new RelayCommand(
        _ => AcknowledgeAllAlarms(),
        _ => _isEnabled() && UnacknowledgedAlarmCount > 0,
        useCommandManagerRequery: false);

    internal void Reset()
    {
        Alarms.Clear();
        _activeAlarms.Clear();
        AlarmHistory.Clear();
        OnPropertyChanged(nameof(HasAlarms));
        OnPropertyChanged(nameof(HasAlarmHistory));
        OnPropertyChanged(nameof(AlarmAcknowledgementSummaryText));
        OnPropertyChanged(nameof(AlarmHistorySummaryText));
        InvalidateCommands();
    }

    internal void ApplySnapshot(SimulationSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        _latestSnapshot = snapshot;
        RebuildAlarms(snapshot);
    }

    internal void RefreshLocalization()
    {
        if (_latestSnapshot is not null)
        {
            RebuildAlarms(_latestSnapshot);
        }
        else
        {
            RefreshAlarmPresentation();
        }
    }

    internal void InvalidateCommands()
    {
        (_acknowledgeAlarmCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (_acknowledgeAllAlarmsCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    private void AcknowledgeAlarm(RuntimeAlarmItem? alarm)
    {
        if (!_isEnabled() || alarm is null || !alarm.CanAcknowledge)
        {
            return;
        }

        alarm.Acknowledge();
        RefreshAlarmPresentation(alarm);
        _setOperationStatus(T(
            "Debugger.AlarmAcknowledgedStatus",
            "알람을 확인 처리했습니다.",
            "Alarm acknowledged."));
        OnPropertyChanged(nameof(AlarmAcknowledgementSummaryText));
        InvalidateCommands();
    }

    private void AcknowledgeAllAlarms()
    {
        if (!_isEnabled())
        {
            return;
        }

        var alarms = Alarms.Where(item => item.CanAcknowledge).ToArray();
        foreach (var alarm in alarms)
        {
            alarm.Acknowledge();
            RefreshAlarmPresentation(alarm);
        }

        if (alarms.Length > 0)
        {
            _setOperationStatus(T(
                "Debugger.AlarmAcknowledgedStatus",
                "알람을 확인 처리했습니다.",
                "Alarm acknowledged."));
        }

        OnPropertyChanged(nameof(AlarmAcknowledgementSummaryText));
        InvalidateCommands();
    }

    private void RebuildAlarms(SimulationSnapshot snapshot)
    {
        var projections = BuildAlarmProjections(snapshot)
            .GroupBy(item => item.AlarmKey, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();
        var activeKeys = projections
            .Select(item => item.AlarmKey)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var clearedKey in _activeAlarms.Keys
                     .Where(key => !activeKeys.Contains(key))
                     .ToArray())
        {
            _activeAlarms[clearedKey].MarkCleared(snapshot.TickIndex, snapshot.SimulationTime);
            _activeAlarms.Remove(clearedKey);
        }

        Alarms.Clear();
        foreach (var projection in projections)
        {
            if (!_activeAlarms.TryGetValue(projection.AlarmKey, out var alarm))
            {
                alarm = new RuntimeAlarmItem(
                    projection.AlarmKey,
                    projection.Source,
                    projection.State,
                    projection.RecoveryKey,
                    T(projection.RecoveryKey),
                    snapshot.TickIndex,
                    snapshot.SimulationTime);
                _activeAlarms.Add(projection.AlarmKey, alarm);
                AlarmHistory.Insert(0, alarm);
            }
            else
            {
                alarm.MarkSeen(snapshot.TickIndex, snapshot.SimulationTime);
            }

            RefreshAlarmPresentation(alarm);
            Alarms.Add(alarm);
        }

        TrimAlarmHistory();
        RefreshAlarmPresentation();
        OnPropertyChanged(nameof(HasAlarms));
        OnPropertyChanged(nameof(HasAlarmHistory));
        OnPropertyChanged(nameof(AlarmSummaryText));
        OnPropertyChanged(nameof(AlarmAcknowledgementSummaryText));
        OnPropertyChanged(nameof(AlarmHistorySummaryText));
        InvalidateCommands();
    }

    private IReadOnlyList<RuntimeAlarmProjection> BuildAlarmProjections(SimulationSnapshot snapshot)
    {
        var projections = new List<RuntimeAlarmProjection>();
        foreach (var fault in snapshot.Faults)
        {
            projections.Add(new RuntimeAlarmProjection(
                $"fault:{fault.Kind}:{fault.TargetId}",
                fault.TargetId,
                fault.Kind.ToString(),
                "Debugger.RecoveryClearFault"));
        }

        foreach (var sequence in snapshot.Sequences.Where(item => item.LastError is not null))
        {
            var error = sequence.LastError!;
            projections.Add(new RuntimeAlarmProjection(
                $"sequence:{sequence.SequenceId}:{error.Code}:{error.StepId}",
                _sequenceName(sequence.SequenceId),
                $"{error.Code} · {error.Message}",
                sequence.Status == SequenceExecutionStatus.Faulted
                    ? "Debugger.RecoveryRetry"
                    : "Debugger.RecoveryReset"));
        }

        foreach (var axis in snapshot.Axes.Where(item => item.DriveAlarmActive || item.State == AxisState.Error))
        {
            projections.Add(new RuntimeAlarmProjection(
                $"axis:{axis.Id}:{axis.DriveAlarmActive}:{axis.State}",
                axis.Name,
                axis.DriveAlarmActive ? "Drive alarm" : axis.State.ToString(),
                "Debugger.RecoveryAxis"));
        }

        foreach (var component in snapshot.LayoutComponents.Where(item => item.CylinderState == PneumaticCylinderState.Fault))
        {
            projections.Add(new RuntimeAlarmProjection(
                $"equipment:{component.Id}:cylinder-fault",
                component.Name,
                "Cylinder fault",
                "Debugger.RecoveryReset"));
        }

        AddInterlockAlarms(
            projections,
            "load-lock",
            snapshot.LoadLocks
                .Where(item => item.State == LoadLockState.InterlockFault)
                .Select(item => (item.Name, item.State.ToString())));
        AddInterlockAlarms(
            projections,
            "wafer-handler",
            snapshot.WaferHandlers
                .Where(item => item.State == WaferHandlerOwnershipState.InterlockFault)
                .Select(item => (item.Name, item.State.ToString())));
        AddInterlockAlarms(
            projections,
            "inspection-sort",
            snapshot.InspectionSortRouters
                .Where(item => item.State == InspectionSortRouteState.InterlockFault)
                .Select(item => (item.Name, item.State.ToString())));
        AddInterlockAlarms(
            projections,
            "inspection-handoff",
            snapshot.InspectionHandoffs
                .Where(item => item.State == InspectionHandoffState.InterlockFault)
                .Select(item => (item.Name, item.State.ToString())));
        AddInterlockAlarms(
            projections,
            "oht-handoff",
            snapshot.OhtHandoffs
                .Where(item => item.State == OhtHandoffOwnershipState.InterlockFault)
                .Select(item => (item.Name, item.State.ToString())));
        AddInterlockAlarms(
            projections,
            "prealigner",
            snapshot.Prealigners
                .Where(item => item.State == PrealignerState.InterlockFault)
                .Select(item => (item.Name, item.State.ToString())));
        return projections;
    }

    private static void AddInterlockAlarms(
        ICollection<RuntimeAlarmProjection> projections,
        string category,
        IEnumerable<(string Name, string State)> alarms)
    {
        foreach (var alarm in alarms)
        {
            projections.Add(new RuntimeAlarmProjection(
                $"interlock:{category}:{alarm.Name}:{alarm.State}",
                alarm.Name,
                alarm.State,
                "Debugger.RecoveryInterlock"));
        }
    }

    private void RefreshAlarmPresentation()
    {
        foreach (var alarm in AlarmHistory)
        {
            RefreshAlarmPresentation(alarm);
        }
    }

    private void RefreshAlarmPresentation(RuntimeAlarmItem alarm)
    {
        var lifecycle = string.Join(
            " · ",
            T(alarm.IsActive ? "Debugger.AlarmActive" : "Debugger.AlarmCleared"),
            T(alarm.IsAcknowledged
                ? "Debugger.AlarmAcknowledged"
                : "Debugger.AlarmUnacknowledged"));
        var occurrence = string.Format(
            CultureInfo.CurrentCulture,
            T("Debugger.AlarmOccurredAt", "발생 {0} · tick {1}", "Occurred {0} · tick {1}"),
            FormatAlarmTime(alarm.FirstSeenTime),
            alarm.FirstSeenTick);
        var clearedAt = alarm.ClearedTime is { } clearedTime && alarm.ClearedTick is { } clearedTick
            ? string.Format(
                CultureInfo.CurrentCulture,
                T("Debugger.AlarmClearedAt", "해제 {0} · tick {1}", "Cleared {0} · tick {1}"),
                FormatAlarmTime(clearedTime),
                clearedTick)
            : T("Debugger.AlarmStillActive", "현재 활성", "Still active");
        alarm.SetPresentation(
            T(alarm.RecoveryKey),
            lifecycle,
            occurrence,
            clearedAt,
            alarm.IsAcknowledged
                ? T("Debugger.AlarmAcknowledgedAction", "확인됨", "Acknowledged")
                : T("Debugger.AcknowledgeAlarm", "확인", "Acknowledge"));
    }

    private void TrimAlarmHistory()
    {
        while (AlarmHistory.Count > AlarmHistoryRetentionLimit)
        {
            var candidate = AlarmHistory.LastOrDefault(item => !item.IsActive)
                ?? AlarmHistory[^1];
            AlarmHistory.Remove(candidate);
        }
    }

    private static string FormatAlarmTime(TimeSpan time) =>
        time.ToString(@"hh\:mm\:ss\.fff", CultureInfo.InvariantCulture);

    private sealed record RuntimeAlarmProjection(
        string AlarmKey,
        string Source,
        string State,
        string RecoveryKey);

    private static string T(string key, string korean, string english) =>
        OpenVisionLanguageService.T(key, korean, english);

    private static string T(string key) => OpenVisionLanguageService.T(key);
}
