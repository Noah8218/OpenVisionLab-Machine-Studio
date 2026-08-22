using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using OpenVisionLab;
using OpenVisionLab.Machine.Core.Channels;
using OpenVisionLab.Machine.IO.Channels;
using OpenVisionLab.Machine.Simulation.Commands;
using OpenVisionLab.Machine.Simulation.Engine;
using OpenVisionLab.Machine.Simulation.Faults;
using OpenVisionLab.Machine.Simulation.Snapshots;

namespace OpenVisionLab.MachineStudio.ViewModel;

public sealed class DigitalIoSignalItemViewModel : ViewModelBase
{
    private bool _value;
    private bool _nominalValue;
    private bool? _overrideValue;
    private bool _isFaulted;

    internal DigitalIoSignalItemViewModel(
        string id,
        string name,
        ChannelKind kind)
    {
        Id = id;
        Name = name;
        Kind = kind;
    }

    public string Id { get; }
    public string Name { get; }
    public ChannelKind Kind { get; }
    public bool IsInput => Kind == ChannelKind.DigitalInput;
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Id : Name;
    public bool Value => _value;
    public bool NominalValue => _nominalValue;
    public bool? OverrideValue => _overrideValue;
    public bool IsFaulted => _isFaulted;
    public string KindText => OpenVisionLanguageService.T(
        IsInput ? "Io.DigitalInput" : "Io.DigitalOutput");
    public string ValueText => FormatSignal(Value);
    public string ForceText => IsFaulted
        ? OpenVisionLanguageService.T("Io.FaultOverride")
        : OverrideValue switch
        {
            true => OpenVisionLanguageService.T("Io.ForcedOn"),
            false => OpenVisionLanguageService.T("Io.ForcedOff"),
            null => OpenVisionLanguageService.T("Io.ForceReleased")
        };

    internal void Apply(DigitalSignalSnapshot signal, bool isFaulted)
    {
        if (_value != signal.Value)
        {
            _value = signal.Value;
            OnPropertyChanged(nameof(Value));
            OnPropertyChanged(nameof(ValueText));
        }
        if (_nominalValue != signal.NominalValue)
        {
            _nominalValue = signal.NominalValue;
            OnPropertyChanged(nameof(NominalValue));
        }
        if (_overrideValue != signal.OverrideValue)
        {
            _overrideValue = signal.OverrideValue;
            OnPropertyChanged(nameof(OverrideValue));
            OnPropertyChanged(nameof(ForceText));
        }
        if (_isFaulted != isFaulted)
        {
            _isFaulted = isFaulted;
            OnPropertyChanged(nameof(IsFaulted));
            OnPropertyChanged(nameof(ForceText));
        }
    }

    internal void RefreshLocalization()
    {
        OnPropertyChanged(nameof(KindText));
        OnPropertyChanged(nameof(ValueText));
        OnPropertyChanged(nameof(ForceText));
    }

    private static string FormatSignal(bool value) => OpenVisionLanguageService.T(
        value ? "Shell.SignalOn" : "Shell.SignalOff");
}

public sealed class DigitalIoCommissioningViewModel : ViewModelBase
{
    private readonly Func<SimulationCommand, Task<SimulationCommandResult>> _dispatch;
    private DigitalIoSignalItemViewModel? _selectedSignal;
    private SimulationRunMode _runMode;
    private SimulationControlOwner _controlOwner = SimulationControlOwner.Definition;
    private long _signalRevision;
    private bool _automaticRunActive;
    private bool _sequenceRunActive;
    private bool _isEnabled;
    private bool _isOperationPending;
    private ICommand? _startManualControlCommand;
    private ICommand? _forceOnCommand;
    private ICommand? _forceOffCommand;
    private ICommand? _clearForceCommand;

    public DigitalIoCommissioningViewModel(
        Func<SimulationCommand, Task<SimulationCommandResult>> dispatch)
    {
        _dispatch = dispatch ?? throw new ArgumentNullException(nameof(dispatch));
    }

    public ObservableCollection<DigitalIoSignalItemViewModel> Signals { get; } = new();

    public DigitalIoSignalItemViewModel? SelectedSignal
    {
        get => _selectedSignal;
        set
        {
            if (SetProperty(ref _selectedSignal, value))
            {
                NotifySelectionChanged();
            }
        }
    }

    public bool HasSignals => Signals.Count > 0;
    public bool HasSelectedSignal => SelectedSignal is not null;
    public bool IsSelectedInput => SelectedSignal?.IsInput == true;
    public bool IsSelectedFaulted => SelectedSignal?.IsFaulted == true;
    public string SelectedValueText => SelectedSignal?.ValueText
        ?? OpenVisionLanguageService.T("Shell.NotConfigured");
    public string SelectedNominalValueText => SelectedSignal is null
        ? OpenVisionLanguageService.T("Shell.NotConfigured")
        : OpenVisionLanguageService.T(
            SelectedSignal.NominalValue ? "Shell.SignalOn" : "Shell.SignalOff");
    public string SelectedForceText => SelectedSignal?.ForceText
        ?? OpenVisionLanguageService.T("Shell.NotConfigured");
    public string ControlOwnerText => OpenVisionLanguageService.T(
        $"Shell.ControlOwnerLabel.{_controlOwner}",
        _controlOwner.ToString(),
        _controlOwner.ToString());
    public string SignalRevisionText => _signalRevision.ToString(CultureInfo.InvariantCulture);
    public string OperationHintText => GetOperationHintText();
    public bool CanForceOn => CanForceSelected()
        && SelectedSignal?.OverrideValue != true;
    public bool CanForceOff => CanForceSelected()
        && SelectedSignal?.OverrideValue != false;
    public bool CanClearForce => CanForceSelected()
        && SelectedSignal?.OverrideValue.HasValue == true;

    public bool IsEnabled
    {
        get => _isEnabled;
        set => SetEnabled(value, invalidateCommands: true);
    }

    internal void SetEnabled(bool value, bool invalidateCommands)
    {
        if (SetProperty(ref _isEnabled, value))
        {
            NotifySelectionChanged(invalidateCommands);
        }
    }

    internal void InvalidateCommands()
    {
        (_startManualControlCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (_forceOnCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (_forceOffCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (_clearForceCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
    }

    public bool IsOperationPending
    {
        get => _isOperationPending;
        private set
        {
            if (SetProperty(ref _isOperationPending, value))
            {
                NotifySelectionChanged();
            }
        }
    }

    public ICommand StartManualControlCommand => _startManualControlCommand ??=
        new AsyncRelayCommand(
            _ => DispatchAsync(new StartManualControlCommand()),
            _ => CanStartManualControl());

    public ICommand ForceOnCommand => _forceOnCommand ??= new AsyncRelayCommand(
        _ => SetForceAsync(true),
        _ => CanForceOn);

    public ICommand ForceOffCommand => _forceOffCommand ??= new AsyncRelayCommand(
        _ => SetForceAsync(false),
        _ => CanForceOff);

    public ICommand ClearForceCommand => _clearForceCommand ??= new AsyncRelayCommand(
        _ => SetForceAsync(null),
        _ => CanClearForce);

    public void ApplySnapshot(SimulationSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        string? selectedId = SelectedSignal?.Id;
        var orderedSignals = snapshot.Signals
            .OrderBy(signal => signal.Id, StringComparer.Ordinal)
            .ToArray();
        bool sameConfiguration = orderedSignals.Length == Signals.Count
            && orderedSignals.Select(signal => (signal.Id, signal.Name, signal.Kind))
                .SequenceEqual(Signals.Select(signal => (signal.Id, signal.Name, signal.Kind)));
        if (!sameConfiguration)
        {
            Signals.Clear();
            foreach (var signal in orderedSignals)
            {
                Signals.Add(new DigitalIoSignalItemViewModel(signal.Id, signal.Name, signal.Kind));
            }
        }

        var faultedInputs = snapshot.Faults
            .Where(fault => fault.Kind == SimulationFaultKind.StuckDigitalInput)
            .Select(fault => fault.TargetId)
            .ToHashSet(StringComparer.Ordinal);
        for (var index = 0; index < orderedSignals.Length; index++)
        {
            Signals[index].Apply(
                orderedSignals[index],
                faultedInputs.Contains(orderedSignals[index].Id));
        }

        SelectedSignal = Signals.FirstOrDefault(signal =>
                string.Equals(signal.Id, selectedId, StringComparison.Ordinal))
            ?? Signals.FirstOrDefault(signal => signal.IsInput)
            ?? Signals.FirstOrDefault();
        _runMode = snapshot.RunMode;
        _controlOwner = snapshot.ControlOwner;
        _signalRevision = snapshot.SignalRevision;
        _automaticRunActive = snapshot.AutomaticRun.IsActive;
        _sequenceRunActive = snapshot.Sequences.Any(sequence =>
            sequence.Status == OpenVisionLab.Machine.Sequence.Runtime.SequenceExecutionStatus.Running);

        OnPropertyChanged(nameof(HasSignals));
        OnPropertyChanged(nameof(ControlOwnerText));
        OnPropertyChanged(nameof(SignalRevisionText));
        NotifySelectionChanged();
    }

    public void RefreshLocalization()
    {
        foreach (var signal in Signals)
        {
            signal.RefreshLocalization();
        }
        OnPropertyChanged(nameof(ControlOwnerText));
        NotifySelectionChanged();
    }

    private bool CanStartManualControl() => IsEnabled
        && !IsOperationPending
        && _runMode == SimulationRunMode.Paused
        && _controlOwner != SimulationControlOwner.Manual
        && !_automaticRunActive
        && !_sequenceRunActive;

    private bool CanForceSelected() => IsEnabled
        && !IsOperationPending
        && _controlOwner == SimulationControlOwner.Manual
        && SelectedSignal?.IsInput == true
        && !SelectedSignal.IsFaulted;

    private Task SetForceAsync(bool? forcedValue) => SelectedSignal is null
        ? Task.CompletedTask
        : DispatchAsync(new SetVirtualInputForceCommand(SelectedSignal.Id, forcedValue));

    private async Task DispatchAsync(SimulationCommand command)
    {
        IsOperationPending = true;
        try
        {
            await _dispatch(command);
        }
        finally
        {
            IsOperationPending = false;
        }
    }

    private string GetOperationHintText()
    {
        if (!HasSignals)
        {
            return OpenVisionLanguageService.T("Io.NoSignalsHint");
        }
        if (SelectedSignal is null)
        {
            return OpenVisionLanguageService.T("Io.SelectSignalHint");
        }
        if (!SelectedSignal.IsInput)
        {
            return OpenVisionLanguageService.T("Io.OutputReadOnlyHint");
        }
        if (SelectedSignal.IsFaulted)
        {
            return OpenVisionLanguageService.T("Io.FaultInterlockHint");
        }
        if (_controlOwner != SimulationControlOwner.Manual)
        {
            return _runMode == SimulationRunMode.Paused
                ? OpenVisionLanguageService.T("Io.StartManualHint")
                : OpenVisionLanguageService.T("Io.PauseForManualHint");
        }
        return SelectedSignal.OverrideValue.HasValue
            ? OpenVisionLanguageService.T("Io.ForcedHint")
            : OpenVisionLanguageService.T("Io.ManualHint");
    }

    private void NotifySelectionChanged(bool invalidateCommands = true)
    {
        OnPropertyChanged(nameof(HasSelectedSignal));
        OnPropertyChanged(nameof(IsSelectedInput));
        OnPropertyChanged(nameof(IsSelectedFaulted));
        OnPropertyChanged(nameof(SelectedValueText));
        OnPropertyChanged(nameof(SelectedNominalValueText));
        OnPropertyChanged(nameof(SelectedForceText));
        OnPropertyChanged(nameof(OperationHintText));
        OnPropertyChanged(nameof(CanForceOn));
        OnPropertyChanged(nameof(CanForceOff));
        OnPropertyChanged(nameof(CanClearForce));
        if (invalidateCommands)
        {
            CommandManager.InvalidateRequerySuggested();
        }
    }
}
