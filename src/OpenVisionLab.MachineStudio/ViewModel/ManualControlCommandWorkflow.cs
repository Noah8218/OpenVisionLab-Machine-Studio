using OpenVisionLab.Machine.Core.Layouts;
using OpenVisionLab.Machine.Simulation.Commands;
using OpenVisionLab.Machine.Simulation.Layout;

namespace OpenVisionLab.MachineStudio.ViewModel;

/// <summary>
/// Converts the current manual-control selection into simulation commands.
/// The shell owns command availability and global run-state presentation.
/// </summary>
internal sealed class ManualControlCommandWorkflow
{
    private readonly EquipmentCommandDispatcher _dispatcher;
    private readonly ManualEquipmentPresentation _presentation;
    private readonly Func<LayoutComponentKind?> _selectedComponentKind;
    private readonly Action _markManualControlStarted;

    internal ManualControlCommandWorkflow(
        EquipmentCommandDispatcher dispatcher,
        ManualEquipmentPresentation presentation,
        Func<LayoutComponentKind?> selectedComponentKind,
        Action markManualControlStarted)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _presentation = presentation ?? throw new ArgumentNullException(nameof(presentation));
        _selectedComponentKind = selectedComponentKind
            ?? throw new ArgumentNullException(nameof(selectedComponentKind));
        _markManualControlStarted = markManualControlStarted
            ?? throw new ArgumentNullException(nameof(markManualControlStarted));
    }

    internal async Task StartEquipmentControlAsync()
    {
        var result = _selectedComponentKind() switch
        {
            LayoutComponentKind.LinearStage or LayoutComponentKind.RotaryStage =>
                await _dispatcher.DispatchAxisCommandAsync(
                    new StartManualControlCommand(),
                    "Axis.ActionStartManual"),
            LayoutComponentKind.DigitalSensor => await _dispatcher.DispatchSensorCommandAsync(
                new StartManualControlCommand(),
                "Sensor.ActionStartManual"),
            LayoutComponentKind.PneumaticCylinder => await _dispatcher.DispatchCylinderCommandAsync(
                new StartManualControlCommand(),
                "Cylinder.ActionStartManual"),
            LayoutComponentKind.Conveyor => await _dispatcher.DispatchConveyorCommandAsync(
                new StartManualControlCommand(),
                "Conveyor.ActionStartManual"),
            _ => null
        };

        if (result?.IsAccepted == true)
        {
            _markManualControlStarted();
        }
    }

    internal async Task StartCameraControlAsync()
    {
        var result = await _dispatcher.DispatchCameraCommandAsync(
            new StartManualControlCommand(),
            "Camera.ActionStartManual");
        if (result.IsAccepted)
        {
            _markManualControlStarted();
        }
    }

    internal Task SetCylinderAsync(bool extend)
    {
        var cylinderId = _presentation.SelectedCylinderId;
        return cylinderId is null
            ? Task.CompletedTask
            : _dispatcher.DispatchCylinderCommandAsync(
                new SetCylinderCommand(cylinderId, extend),
                extend ? "Cylinder.ActionExtend" : "Cylinder.ActionRetract");
    }

    internal Task SetSensorForceAsync(bool? forcedValue)
    {
        var sensorId = _presentation.SelectedSensorId;
        return sensorId is null
            ? Task.CompletedTask
            : _dispatcher.DispatchSensorCommandAsync(
                new SetDigitalSensorForceCommand(sensorId, forcedValue),
                forcedValue switch
                {
                    true => "Sensor.ActionForceOn",
                    false => "Sensor.ActionForceOff",
                    null => "Sensor.ActionClearForce"
                });
    }

    internal Task SetConveyorAsync(bool running, ConveyorDirection direction)
    {
        var conveyorId = _presentation.SelectedConveyorId;
        return conveyorId is null
            ? Task.CompletedTask
            : _dispatcher.DispatchConveyorCommandAsync(
                new SetConveyorCommand(conveyorId, running, direction),
                running
                    ? direction == ConveyorDirection.Forward
                        ? "Conveyor.ActionRunForward"
                        : "Conveyor.ActionRunReverse"
                    : "Conveyor.ActionStop");
    }

    internal Task StopConveyorAsync() => SetConveyorAsync(
        running: false,
        direction: _presentation.SelectedConveyorDirection ?? ConveyorDirection.Forward);
}
