using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using OpenVisionLab;
using OpenVisionLab.Machine.Simulation.Commands;
using OpenVisionLab.Machine.Simulation.Faults;
using OpenVisionLab.Machine.Simulation.Snapshots;

namespace OpenVisionLab.MachineStudio.ViewModel;

public sealed record SimulationFaultKindOption(SimulationFaultKind Kind, string Name);

public sealed record FaultForcedValueOption(bool Value, string Name);

public sealed record ActiveSimulationFaultItem(
    SimulationFaultKind Kind,
    string TargetId,
    string TargetName,
    string DisplayText,
    bool? ForcedValue,
    long ActivatedTick);

public sealed class FaultManagerViewModel : ViewModelBase
{
    private static readonly IReadOnlyList<SimulationFaultKindOption> KindOptions =
    [
        new(SimulationFaultKind.StuckDigitalInput, "Stuck digital input"),
        new(SimulationFaultKind.CylinderTravelBlocked, "Blocked cylinder travel"),
        new(SimulationFaultKind.AxisMotionBlocked, "Blocked axis motion"),
        new(SimulationFaultKind.AxisFollowingError, "Axis following error")
    ];

    private static readonly IReadOnlyList<FaultForcedValueOption> ValueOptions =
    [
        new(false, "Force OFF"),
        new(true, "Force ON")
    ];

    private readonly Func<SimulationCommand, Task<SimulationCommandResult>> _dispatch;
    private readonly SimulationFaultTargetCatalog _targetCatalog = new();
    private SimulationSnapshot? _latestSnapshot;
    private SimulationFaultKindOption _selectedKind = KindOptions[0];
    private SimulationFaultTarget? _selectedTarget;
    private FaultForcedValueOption _selectedForcedValue = ValueOptions[0];
    private ActiveSimulationFaultItem? _selectedActiveFault;
    private string _operationStatusText = OpenVisionLanguageService.T(
        "Fault.SelectTargetHint",
        "런타임 대상을 선택해 고장을 주입하세요.",
        "Select a runtime target to inject a fault.");
    private bool _isEnabled;
    private bool _isOperationPending;
    private ICommand? _injectCommand;
    private ICommand? _clearSelectedCommand;
    private ICommand? _clearAllCommand;

    public FaultManagerViewModel(
        Func<SimulationCommand, Task<SimulationCommandResult>> dispatch)
    {
        _dispatch = dispatch ?? throw new ArgumentNullException(nameof(dispatch));
    }

    public IReadOnlyList<SimulationFaultKindOption> AvailableKinds => KindOptions;
    public IReadOnlyList<FaultForcedValueOption> ForcedValueOptions => ValueOptions;
    public ObservableCollection<SimulationFaultTarget> Targets { get; } = new();
    public ObservableCollection<ActiveSimulationFaultItem> ActiveFaults { get; } = new();

    public SimulationFaultKindOption SelectedKind
    {
        get => _selectedKind;
        set
        {
            if (value is null)
            {
                return;
            }

            if (!SetProperty(ref _selectedKind, value))
            {
                return;
            }

            OnPropertyChanged(nameof(RequiresForcedValue));
            RefreshTargets();
        }
    }

    public SimulationFaultTarget? SelectedTarget
    {
        get => _selectedTarget;
        set
        {
            if (SetProperty(ref _selectedTarget, value))
            {
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public FaultForcedValueOption SelectedForcedValue
    {
        get => _selectedForcedValue;
        set
        {
            if (value is not null)
            {
                SetProperty(ref _selectedForcedValue, value);
            }
        }
    }

    public ActiveSimulationFaultItem? SelectedActiveFault
    {
        get => _selectedActiveFault;
        set
        {
            if (SetProperty(ref _selectedActiveFault, value))
            {
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public bool IsEnabled
    {
        get => _isEnabled;
        set => SetEnabled(value, invalidateCommands: true);
    }

    internal void SetEnabled(bool value, bool invalidateCommands)
    {
        if (SetProperty(ref _isEnabled, value) && invalidateCommands)
        {
            CommandManager.InvalidateRequerySuggested();
        }
    }

    internal void InvalidateCommands()
    {
        (_injectCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (_clearSelectedCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (_clearAllCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
    }

    public bool RequiresForcedValue =>
        SelectedKind.Kind == SimulationFaultKind.StuckDigitalInput;

    public bool HasTargets => Targets.Count > 0;

    public bool HasActiveFaults => ActiveFaults.Count > 0;

    public string TargetSelectionHelpText => HasTargets
        ? string.Empty
        : OpenVisionLanguageService.T(
            "Fault.NoMatchingTarget",
            "현재 스냅샷에 이 고장 유형과 일치하는 런타임 대상이 없습니다.",
            "No matching runtime target for this fault type in the current snapshot.");

    public string ActiveFaultCountText => ActiveFaults.Count == 0
        ? OpenVisionLanguageService.T("Fault.NoActiveFaults")
        : ActiveFaults.Count == 1
            ? OpenVisionLanguageService.T("Fault.OneActiveFault")
            : string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                OpenVisionLanguageService.T("Fault.ActiveFaults"),
                ActiveFaults.Count);

    public string OperationStatusText
    {
        get => _operationStatusText;
        private set => SetProperty(ref _operationStatusText, value);
    }

    public ICommand InjectCommand => _injectCommand ??= new AsyncRelayCommand(
        _ => RunOperationAsync(InjectAsync),
        _ => CanInject());

    public ICommand ClearSelectedCommand => _clearSelectedCommand ??= new AsyncRelayCommand(
        _ => RunOperationAsync(ClearSelectedAsync),
        _ => IsEnabled && !_isOperationPending && SelectedActiveFault is not null);

    public ICommand ClearAllCommand => _clearAllCommand ??= new AsyncRelayCommand(
        _ => RunOperationAsync(ClearAllAsync),
        _ => IsEnabled && !_isOperationPending && ActiveFaults.Count > 0);

    public void RefreshLocalization()
    {
        OnPropertyChanged(nameof(AvailableKinds));
        OnPropertyChanged(nameof(ForcedValueOptions));
        OnPropertyChanged(nameof(TargetSelectionHelpText));
        OnPropertyChanged(nameof(ActiveFaultCountText));
        OperationStatusText = OpenVisionLanguageService.T(
            HasActiveFaults ? "Fault.ActiveHint" : "Fault.SelectTargetHint");
        if (_latestSnapshot is not null)
        {
            ApplySnapshot(_latestSnapshot);
        }
    }

    public bool IsOperationPending
    {
        get => _isOperationPending;
        private set
        {
            if (SetProperty(ref _isOperationPending, value))
            {
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public void ApplySnapshot(SimulationSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        _latestSnapshot = snapshot;
        var hadActiveFaults = ActiveFaults.Count > 0;

        (SimulationFaultKind Kind, string TargetId)? selectedKey = SelectedActiveFault is null
            ? null
            : (SelectedActiveFault.Kind, SelectedActiveFault.TargetId);
        var names = snapshot.Signals
            .Select(signal => (signal.Id, signal.Name))
            .Concat(snapshot.LayoutComponents.Select(component => (component.Id, component.Name)))
            .Concat(snapshot.Axes.Select(axis => (axis.Id, axis.Name)))
            .GroupBy(item => item.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Name, StringComparer.Ordinal);

        ActiveFaults.Clear();
        foreach (var fault in snapshot.Faults)
        {
            names.TryGetValue(fault.TargetId, out var name);
            var targetLabel = string.IsNullOrWhiteSpace(name) ? fault.TargetId : name;
            var valueLabel = fault.ForcedValue switch
            {
                true => $" · {OpenVisionLanguageService.T("Shell.SignalOn")}",
                false => $" · {OpenVisionLanguageService.T("Shell.SignalOff")}",
                null => string.Empty
            };
            ActiveFaults.Add(new ActiveSimulationFaultItem(
                fault.Kind,
                fault.TargetId,
                targetLabel,
                $"{FormatKind(fault.Kind)} · {targetLabel}{valueLabel}",
                fault.ForcedValue,
                fault.ActivatedTick));
        }

        SelectedActiveFault = selectedKey is null
            ? ActiveFaults.FirstOrDefault()
            : ActiveFaults.FirstOrDefault(item =>
                item.Kind == selectedKey.Value.Kind
                && string.Equals(item.TargetId, selectedKey.Value.TargetId, StringComparison.Ordinal))
                ?? ActiveFaults.FirstOrDefault();

        OnPropertyChanged(nameof(ActiveFaultCountText));
        OnPropertyChanged(nameof(HasActiveFaults));
        if (hadActiveFaults && ActiveFaults.Count == 0)
        {
            OperationStatusText = OpenVisionLanguageService.T("Fault.RuntimeCleared");
        }
        RefreshTargets();
        CommandManager.InvalidateRequerySuggested();
    }

    private void RefreshTargets()
    {
        var previousTargetId = SelectedTarget?.Id;
        Targets.Clear();
        if (_latestSnapshot is not null)
        {
            foreach (var target in _targetCatalog.GetTargets(_latestSnapshot, SelectedKind.Kind))
            {
                Targets.Add(target);
            }
        }

        SelectedTarget = Targets.FirstOrDefault(target =>
                string.Equals(target.Id, previousTargetId, StringComparison.Ordinal))
            ?? Targets.FirstOrDefault();

        OnPropertyChanged(nameof(HasTargets));
        OnPropertyChanged(nameof(TargetSelectionHelpText));
        CommandManager.InvalidateRequerySuggested();
    }

    private bool CanInject()
    {
        if (!IsEnabled || IsOperationPending || SelectedTarget is null)
        {
            return false;
        }

        return !ActiveFaults.Any(fault =>
            fault.Kind == SelectedKind.Kind
            && string.Equals(fault.TargetId, SelectedTarget.Id, StringComparison.Ordinal));
    }

    private async Task InjectAsync()
    {
        var target = SelectedTarget;
        if (target is null)
        {
            return;
        }

        bool? forcedValue = RequiresForcedValue ? SelectedForcedValue.Value : null;
        var result = await _dispatch(new InjectSimulationFaultCommand(
            SelectedKind.Kind,
            target.Id,
            forcedValue));
        OperationStatusText = result.IsAccepted
            ? Format("Fault.Injected", FormatKind(SelectedKind.Kind), target.Name)
            : FormatRejection(result);
    }

    private async Task ClearSelectedAsync()
    {
        var fault = SelectedActiveFault;
        if (fault is null)
        {
            return;
        }

        var result = await _dispatch(new ClearSimulationFaultCommand(fault.Kind, fault.TargetId));
        OperationStatusText = result.IsAccepted
            ? Format("Fault.Cleared", FormatKind(fault.Kind), fault.TargetName)
            : FormatRejection(result);
    }

    private async Task ClearAllAsync()
    {
        var activeFaults = ActiveFaults.ToArray();
        var clearedCount = 0;
        foreach (var fault in activeFaults)
        {
            var result = await _dispatch(new ClearSimulationFaultCommand(fault.Kind, fault.TargetId));
            if (!result.IsAccepted)
            {
                OperationStatusText = Format(
                    "Fault.ClearPartial",
                    clearedCount,
                    activeFaults.Length,
                    FormatRejection(result));
                return;
            }

            clearedCount++;
        }

    }

    private async Task RunOperationAsync(Func<Task> operation)
    {
        if (IsOperationPending)
        {
            return;
        }

        IsOperationPending = true;
        try
        {
            await operation();
        }
        finally
        {
            IsOperationPending = false;
        }
    }

    private static string FormatKind(SimulationFaultKind kind) => kind switch
    {
        SimulationFaultKind.StuckDigitalInput => OpenVisionLanguageService.T("Fault.StuckDigitalInput"),
        SimulationFaultKind.CylinderTravelBlocked => OpenVisionLanguageService.T("Fault.CylinderTravelBlocked"),
        SimulationFaultKind.AxisMotionBlocked => OpenVisionLanguageService.T("Fault.AxisMotionBlocked"),
        SimulationFaultKind.AxisFollowingError => OpenVisionLanguageService.T("Fault.AxisFollowingError"),
        _ => kind.ToString()
    };

    private static string FormatRejection(SimulationCommandResult result) =>
        $"{OpenVisionLanguageService.T("Fault.Rejected")}: {result.ErrorCode}{(string.IsNullOrWhiteSpace(result.Detail) ? string.Empty : $" · {result.Detail}")}";

    private static string Format(string key, params object[] arguments) =>
        string.Format(CultureInfo.CurrentCulture, OpenVisionLanguageService.T(key), arguments);
}
