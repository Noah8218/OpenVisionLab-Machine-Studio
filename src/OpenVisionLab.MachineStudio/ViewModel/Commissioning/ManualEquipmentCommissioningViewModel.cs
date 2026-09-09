using System.Windows.Input;
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
        nameof(CanStartManualEquipmentControl)
    ];

    private readonly Func<SimulationSnapshot?, ManualEquipmentProjection> _projectionFactory;
    private readonly ManualEquipmentPresentation _presentation = new();
    private readonly ManualControlCommandWorkflow _commandWorkflow;
    private bool _sessionCloseRequested;
    private bool _disposed;

    internal ManualEquipmentCommissioningViewModel(
        EquipmentCommandDispatcher dispatcher,
        Func<SimulationSnapshot?, ManualEquipmentProjection> projectionFactory,
        Func<LayoutComponentKind?> selectedComponentKind,
        Action markManualControlStarted,
        Action<Exception> handleCommandException)
    {
        ArgumentNullException.ThrowIfNull(handleCommandException);
        _projectionFactory = projectionFactory
            ?? throw new ArgumentNullException(nameof(projectionFactory));
        _commandWorkflow = new(
            dispatcher ?? throw new ArgumentNullException(nameof(dispatcher)),
            _presentation,
            selectedComponentKind ?? throw new ArgumentNullException(nameof(selectedComponentKind)),
            markManualControlStarted ?? throw new ArgumentNullException(nameof(markManualControlStarted)));

        StartManualEquipmentControlCommand = CreateCommand(
            _commandWorkflow.StartEquipmentControlAsync,
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
    public bool CanStartManualEquipmentControl => CanExecute(_presentation.CanStartManualEquipmentControl);

    internal DigitalSignalSnapshot? CurrentSelectedSensorSignal =>
        IsReady ? _presentation.CurrentSelectedSensorSignal : null;

    internal bool IsDisposed => _disposed;

    internal Task StartManualCameraControlAsync() => _disposed || _sessionCloseRequested
        ? Task.CompletedTask
        : _commandWorkflow.StartCameraControlAsync();

    internal void ApplyProjection(
        ManualEquipmentProjection projection,
        bool invalidateCommands = true)
    {
        if (_disposed)
        {
            return;
        }

        _presentation.ApplyProjection(projection);
        RaisePresentationChanged();
        if (invalidateCommands)
        {
            InvalidateCommands();
        }
    }

    internal void RefreshProjection(
        SimulationSnapshot? snapshot = null,
        bool invalidateCommands = true) =>
        ApplyProjection(_projectionFactory(snapshot), invalidateCommands);

    internal void RefreshLocalization() => RaisePresentationChanged();

    internal void SetSessionCloseAdmission(bool isRequested)
    {
        if (_sessionCloseRequested == isRequested)
        {
            return;
        }

        _sessionCloseRequested = isRequested;
        InvalidateCommands();
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
        IsReady && !_sessionCloseRequested && presentationGate;

    private void RaisePresentationChanged()
    {
        foreach (var propertyName in PresentationPropertyNames)
        {
            OnPropertyChanged(propertyName);
        }
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
