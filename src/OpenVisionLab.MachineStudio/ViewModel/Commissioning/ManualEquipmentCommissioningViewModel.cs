using System.Windows.Input;
using OpenVisionLab;
using OpenVisionLab.Machine.Simulation.Engine;
using OpenVisionLab.Machine.Core.Layouts;
using OpenVisionLab.Machine.IO.Channels;
using OpenVisionLab.Machine.Simulation.Layout;
using OpenVisionLab.Machine.Simulation.Snapshots;

namespace OpenVisionLab.MachineStudio.ViewModel;

/// <summary>
/// Owns the selected equipment commissioning projection and manual command
/// lifetime. MainViewModel supplies the immutable runtime projection and keeps
/// only the shell-level compatibility facade.
/// </summary>
public sealed class ManualEquipmentCommissioningViewModel : ViewModelBase, IDisposable
{
    private static readonly string[] PresentationPropertyNames =
    [
        nameof(HasSelectedManualEquipment), nameof(HasSelectedDigitalSensor),
        nameof(IsCurrentSensorFaulted), nameof(IsCurrentSensorManuallyForced),
        nameof(CurrentSensorForceText), nameof(SensorCommissioningHintText),
        nameof(CanForceSensorOn), nameof(CanForceSensorOff), nameof(CanClearSensorForce),
        nameof(HasSelectedPneumaticCylinder), nameof(IsCurrentCylinderInterlocked),
        nameof(CurrentCylinderInterlockText), nameof(CylinderCommissioningHintText),
        nameof(CanExtendCylinder), nameof(CanRetractCylinder), nameof(HasSelectedConveyor),
        nameof(ConveyorCommissioningHintText), nameof(CanRunConveyorForward),
        nameof(CanRunConveyorReverse), nameof(CanStopConveyor),
        nameof(CanStartManualEquipmentControl), nameof(HasSelectedManualControlTarget),
        nameof(ManualControlOwnerText), nameof(ManualControlHintText), nameof(StartManualControlText)
    ];

    private readonly Func<SimulationSnapshot?, ManualEquipmentProjection> _projectionFactory;
    private readonly ManualEquipmentPresentation _presentation = new();
    private readonly ManualControlCommandWorkflow _commandWorkflow;
    private readonly Func<Task<bool>>? _prepareManualControlAsync;
    private readonly Action? _markManualControlPaused;
    private ManualEquipmentProjection? _lastProjection;
    private bool _isStartingManualControl;
    private bool _isManualControlAdmissionLocked;
    private string? _manualEntryNotice;
    private bool _sessionCloseRequested;
    private bool _disposed;

    internal ManualEquipmentCommissioningViewModel(
        EquipmentCommandDispatcher dispatcher,
        Func<SimulationSnapshot?, ManualEquipmentProjection> projectionFactory,
        Func<LayoutComponentKind?> selectedComponentKind,
        Action markManualControlStarted,
        Action<Exception> handleCommandException,
        Func<Task<bool>>? prepareManualControlAsync = null,
        Action? markManualControlPaused = null)
    {
        ArgumentNullException.ThrowIfNull(handleCommandException);
        var controlStartedCallback = markManualControlStarted
            ?? throw new ArgumentNullException(nameof(markManualControlStarted));
        _prepareManualControlAsync = prepareManualControlAsync;
        _markManualControlPaused = markManualControlPaused;
        _projectionFactory = projectionFactory
            ?? throw new ArgumentNullException(nameof(projectionFactory));
        _commandWorkflow = new(
            dispatcher ?? throw new ArgumentNullException(nameof(dispatcher)),
            _presentation,
            selectedComponentKind ?? throw new ArgumentNullException(nameof(selectedComponentKind)),
            () => MarkManualControlStarted(controlStartedCallback));

        StartManualEquipmentControlCommand = CreateCommand(
            StartSelectedManualControlAsync,
            () => CanStartManualEquipmentControl,
            handleCommandException);
        ForceSensorOnCommand = CreateCommand(
            () => _commandWorkflow.SetSensorForceAsync(true),
            () => CanForceSensorOn,
            handleCommandException);
        ForceSensorOffCommand = CreateCommand(
            () => _commandWorkflow.SetSensorForceAsync(false),
            () => CanForceSensorOff,
            handleCommandException);
        ClearSensorForceCommand = CreateCommand(
            () => _commandWorkflow.SetSensorForceAsync(null),
            () => CanClearSensorForce,
            handleCommandException);
        ExtendCylinderCommand = CreateCommand(
            () => _commandWorkflow.SetCylinderAsync(extend: true),
            () => CanExtendCylinder,
            handleCommandException);
        RetractCylinderCommand = CreateCommand(
            () => _commandWorkflow.SetCylinderAsync(extend: false),
            () => CanRetractCylinder,
            handleCommandException);
        RunConveyorForwardCommand = CreateCommand(
            () => _commandWorkflow.SetConveyorAsync(true, ConveyorDirection.Forward),
            () => CanRunConveyorForward,
            handleCommandException);
        RunConveyorReverseCommand = CreateCommand(
            () => _commandWorkflow.SetConveyorAsync(true, ConveyorDirection.Reverse),
            () => CanRunConveyorReverse,
            handleCommandException);
        StopConveyorCommand = CreateCommand(
            _commandWorkflow.StopConveyorAsync,
            () => CanStopConveyor,
            handleCommandException);
    }

    public ICommand StartManualEquipmentControlCommand { get; }
    public ICommand ForceSensorOnCommand { get; }
    public ICommand ForceSensorOffCommand { get; }
    public ICommand ClearSensorForceCommand { get; }
    public ICommand ExtendCylinderCommand { get; }
    public ICommand RetractCylinderCommand { get; }
    public ICommand RunConveyorForwardCommand { get; }
    public ICommand RunConveyorReverseCommand { get; }
    public ICommand StopConveyorCommand { get; }

    public bool HasSelectedManualEquipment => IsReady && _presentation.HasSelectedManualEquipment;
    public bool HasSelectedDigitalSensor => IsReady && _presentation.HasSelectedDigitalSensor;
    public bool IsCurrentSensorFaulted => IsReady && _presentation.IsCurrentSensorFaulted;
    public bool IsCurrentSensorManuallyForced => IsReady && _presentation.IsCurrentSensorManuallyForced;
    public string CurrentSensorForceText => IsReady ? _presentation.CurrentSensorForceText : string.Empty;
    public string SensorCommissioningHintText => IsReady ? _presentation.SensorCommissioningHintText : string.Empty;
    public bool CanForceSensorOn => CanExecute(_presentation.CanForceSensorOn);
    public bool CanForceSensorOff => CanExecute(_presentation.CanForceSensorOff);
    public bool CanClearSensorForce => CanExecute(_presentation.CanClearSensorForce);
    public bool HasSelectedPneumaticCylinder => IsReady && _presentation.HasSelectedPneumaticCylinder;
    public bool IsCurrentCylinderInterlocked => IsReady && _presentation.IsCurrentCylinderInterlocked;
    public string CurrentCylinderInterlockText => IsReady ? _presentation.CurrentCylinderInterlockText : string.Empty;
    public string CylinderCommissioningHintText => IsReady ? _presentation.CylinderCommissioningHintText : string.Empty;
    public bool CanExtendCylinder => CanExecute(_presentation.CanExtendCylinder);
    public bool CanRetractCylinder => CanExecute(_presentation.CanRetractCylinder);
    public bool HasSelectedConveyor => IsReady && _presentation.HasSelectedConveyor;
    public string ConveyorCommissioningHintText => IsReady ? _presentation.ConveyorCommissioningHintText : string.Empty;
    public bool CanRunConveyorForward => CanExecute(_presentation.CanRunConveyorForward);
    public bool CanRunConveyorReverse => CanExecute(_presentation.CanRunConveyorReverse);
    public bool CanStopConveyor => CanExecute(_presentation.CanStopConveyor);
    public bool HasSelectedManualControlTarget => IsReady && _presentation.HasSelectedManualControlTarget;
    public bool IsStartingManualControl => _isStartingManualControl;
    public bool IsManualControlAdmissionLocked => _isManualControlAdmissionLocked;
    public string StartManualControlText => OpenVisionLanguageService.T(
        "Equipment.EnterManualCommissioning", "수동 시운전 시작", "Start manual commissioning");
    public string ManualControlOwnerText => IsReady ? _presentation.ManualControlOwnerText : string.Empty;
    public string ManualControlHintText => !IsReady ? string.Empty
        : _sessionCloseRequested
            ? OpenVisionLanguageService.T("Equipment.ManualHint.Closing", "종료 중에는 수동 시운전을 시작할 수 없습니다.", "Manual commissioning cannot start while closing.")
        : _isStartingManualControl
            ? OpenVisionLanguageService.T("Equipment.ManualHint.Busy", "수동 제어를 준비하고 있습니다. 완료까지 기다려 주세요.", "Manual control is being prepared. Wait for it to finish.")
        : _prepareManualControlAsync is null && _lastProjection?.IsRunMode == false
            ? OpenVisionLanguageService.T("Equipment.ManualHint.RunMode", "운전 모드로 전환한 뒤 수동 시운전을 시작하세요.", "Switch to Run mode before starting manual commissioning.")
        : _manualEntryNotice is null ? _presentation.ManualControlHintText : OpenVisionLanguageService.T(_manualEntryNotice);
    public bool CanStartManualEquipmentControl => CanExecute(_prepareManualControlAsync is null
        ? _presentation.CanStartManualEquipmentControl : _presentation.CanPrepareManualControl);

    internal DigitalSignalSnapshot? CurrentSelectedSensorSignal =>
        IsReady ? _presentation.CurrentSelectedSensorSignal : null;

    internal bool IsDisposed => _disposed;

    internal Task StartManualCameraControlAsync() => _disposed || _sessionCloseRequested || _isStartingManualControl
        ? Task.CompletedTask
        : _commandWorkflow.StartCameraControlAsync();

    internal async Task<bool> PrepareInspectorActionAsync()
    {
        if (_disposed || _sessionCloseRequested || _isStartingManualControl) return false;
        RefreshProjection();
        if (_lastProjection is { ControlOwner: SimulationControlOwner.Manual, IsRuntimeDefinitionDirty: false }
            && !_lastProjection.IsAutomaticRunActive) return true;
        await StartSelectedManualControlAsync();
        return !_disposed && !_sessionCloseRequested && _lastProjection is { ControlOwner: SimulationControlOwner.Manual, IsRuntimeDefinitionDirty: false };
    }

    internal Task ExecuteInspectorActionAsync(string action) => _commandWorkflow.ExecuteInspectorActionAsync(action);

    internal async Task PauseInspectorControlAsync()
    {
        var result = await _commandWorkflow.PauseManualControlAsync(_lastProjection?.SelectedComponentKind);
        if (result.IsAccepted && !_disposed && !_sessionCloseRequested) _markManualControlPaused?.Invoke();
    }

    internal void ApplyProjection(
        ManualEquipmentProjection projection,
        bool invalidateCommands = true)
    {
        if (_disposed)
        {
            return;
        }

        if (_lastProjection is { } previous && (previous.SelectedComponentId != projection.SelectedComponentId
            || previous.SelectedComponentKind != projection.SelectedComponentKind
            || previous.Snapshot.ProjectId != projection.Snapshot.ProjectId
            || previous.IsRunMode != projection.IsRunMode)) _manualEntryNotice = null;
        var isModeOnlyChange = IsModeOnlyChange(_lastProjection, projection);
        _lastProjection = projection;
        _presentation.ApplyProjection(projection);
        if (isModeOnlyChange)
        {
            RaiseModePresentationChanged();
        }
        else
        {
            RaisePresentationChanged();
        }
        if (invalidateCommands)
        {
            InvalidateCommands();
        }
    }

    internal void RefreshProjection(
        SimulationSnapshot? snapshot = null,
        bool invalidateCommands = true) =>
        ApplyProjection(_projectionFactory(snapshot), invalidateCommands);

    internal void RefreshLocalization()
    {
        if (_disposed)
        {
            return;
        }

        RaisePresentationChanged();
    }

    internal void SetSessionCloseAdmission(bool isRequested)
    {
        if (_sessionCloseRequested == isRequested)
        {
            return;
        }

        _sessionCloseRequested = isRequested;
        OnPropertyChanged(nameof(ManualControlHintText));
        InvalidateCommands();
    }

    internal void BeginRuntimePreparation()
    {
        if (!_isStartingManualControl || _disposed || _sessionCloseRequested) return;
        _isManualControlAdmissionLocked = true;
        OnPropertyChanged(nameof(IsManualControlAdmissionLocked));
    }

    internal void InvalidateCommands()
    {
        RaiseCanExecuteChanged(StartManualEquipmentControlCommand);
        RaiseCanExecuteChanged(ForceSensorOnCommand);
        RaiseCanExecuteChanged(ForceSensorOffCommand);
        RaiseCanExecuteChanged(ClearSensorForceCommand);
        RaiseCanExecuteChanged(ExtendCylinderCommand);
        RaiseCanExecuteChanged(RetractCylinderCommand);
        RaiseCanExecuteChanged(RunConveyorForwardCommand);
        RaiseCanExecuteChanged(RunConveyorReverseCommand);
        RaiseCanExecuteChanged(StopConveyorCommand);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        InvalidateCommands();
    }

    private bool IsReady => !_disposed && _presentation.HasProjection;

    private bool CanExecute(bool presentationGate) =>
        IsReady && !_sessionCloseRequested && !_isStartingManualControl && presentationGate;

    private async Task StartSelectedManualControlAsync()
    {
        if (!CanStartManualEquipmentControl) return;
        var requested = _projectionFactory(null);
        _manualEntryNotice = null;
        _isStartingManualControl = true;
        NotifyManualEntryChanged();
        try
        {
            if (_prepareManualControlAsync is not null && !await _prepareManualControlAsync())
            {
                _manualEntryNotice = "Equipment.ManualHint.NotPrepared";
                return;
            }
            if (_disposed || _sessionCloseRequested) return;
            RefreshProjection();
            var prepared = _lastProjection!;
            if (prepared.SelectedComponentId != requested.SelectedComponentId
                || prepared.SelectedComponentKind != requested.SelectedComponentKind
                || prepared.Snapshot.ProjectId != requested.Snapshot.ProjectId
                || !_presentation.CanAcquireManualControl) return;

            bool IsCurrentRuntime()
            {
                if (_disposed || _sessionCloseRequested) return false;
                var current = _projectionFactory(null);
                return current.Snapshot.ProjectId == prepared.Snapshot.ProjectId
                    && current.Snapshot.RuntimeGeneration == prepared.Snapshot.RuntimeGeneration
                    && !current.IsApplyingProject && !current.IsValidationBusy
                    && !current.IsModeTransitioning;
            }

            var result = await _commandWorkflow.StartEquipmentControlAsync(IsCurrentRuntime);
            if (!IsCurrentRuntime()) return;
            if (result?.IsAccepted != true)
            {
                _manualEntryNotice = "Equipment.ManualHint.Rejected";
                return;
            }
            RefreshProjection();
            var manual = _lastProjection!;
            var selectionChanged = manual.SelectedComponentId != prepared.SelectedComponentId
                || manual.SelectedComponentKind != prepared.SelectedComponentKind;
            if (prepared.SelectedComponentKind != LayoutComponentKind.Camera && !selectionChanged) return;
            if (manual.ControlOwner != SimulationControlOwner.Manual || manual.IsAutomaticRunActive
                || manual.ActiveSequenceStatus == OpenVisionLab.Machine.Sequence.Runtime.SequenceExecutionStatus.Running) return;
            var pause = await _commandWorkflow.PauseManualControlAsync(prepared.SelectedComponentKind);
            if (!IsCurrentRuntime()) return;
            if (pause.IsAccepted)
            {
                _markManualControlPaused?.Invoke();
                if (selectionChanged) _manualEntryNotice = "Equipment.ManualHint.SelectionChanged";
            }
            else _manualEntryNotice = "Equipment.ManualHint.ManualPauseRejected";
        }
        finally
        {
            _isStartingManualControl = false;
            _isManualControlAdmissionLocked = false;
            if (!_disposed)
            {
                if (!_sessionCloseRequested) RefreshProjection();
                NotifyManualEntryChanged();
            }
        }
    }

    private void NotifyManualEntryChanged()
    {
        OnPropertyChanged(nameof(IsStartingManualControl));
        OnPropertyChanged(nameof(IsManualControlAdmissionLocked));
        OnPropertyChanged(nameof(CanStartManualEquipmentControl));
        OnPropertyChanged(nameof(ManualControlHintText));
        InvalidateCommands();
    }

    private void MarkManualControlStarted(Action callback)
    {
        if (_disposed || _sessionCloseRequested)
        {
            return;
        }

        callback();
    }

    private void RaisePresentationChanged()
    {
        foreach (var propertyName in PresentationPropertyNames)
        {
            OnPropertyChanged(propertyName);
        }
    }

    private void RaiseModePresentationChanged()
    {
        OnPropertyChanged(nameof(CanStartManualEquipmentControl));
        OnPropertyChanged(nameof(ManualControlOwnerText));
        OnPropertyChanged(nameof(ManualControlHintText));
        switch (_lastProjection?.SelectedComponentKind)
        {
            case LayoutComponentKind.DigitalSensor:
                OnPropertyChanged(nameof(CanForceSensorOn));
                OnPropertyChanged(nameof(CanForceSensorOff));
                OnPropertyChanged(nameof(CanClearSensorForce));
                break;
            case LayoutComponentKind.PneumaticCylinder:
                OnPropertyChanged(nameof(CanExtendCylinder));
                OnPropertyChanged(nameof(CanRetractCylinder));
                break;
            case LayoutComponentKind.Conveyor:
                OnPropertyChanged(nameof(CanRunConveyorForward));
                OnPropertyChanged(nameof(CanRunConveyorReverse));
                OnPropertyChanged(nameof(CanStopConveyor));
                break;
        }
    }

    private static bool IsModeOnlyChange(
        ManualEquipmentProjection? previous,
        ManualEquipmentProjection current)
    {
        if (previous is null || previous.IsRunMode == current.IsRunMode)
        {
            return false;
        }

        return (previous with { IsRunMode = current.IsRunMode }) == current;
    }

    private static AsyncRelayCommand CreateCommand(
        Func<Task> execute,
        Func<bool> canExecute,
        Action<Exception> handleCommandException) => new(
        _ => execute(),
        _ => canExecute(),
        handleCommandException,
        useCommandManagerRequery: false);

    private static void RaiseCanExecuteChanged(ICommand command)
    {
        switch (command)
        {
            case AsyncRelayCommand asyncCommand:
                asyncCommand.RaiseCanExecuteChanged();
                break;
            case RelayCommand relayCommand:
                relayCommand.RaiseCanExecuteChanged();
                break;
        }
    }
}
