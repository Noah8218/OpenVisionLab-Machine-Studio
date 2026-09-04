using System.Globalization;
using OpenVisionLab;
using OpenVisionLab.Machine.Simulation.Commands;
using OpenVisionLab.Machine.Simulation.Engine;

namespace OpenVisionLab.MachineStudio.ViewModel;

/// <summary>
/// Enqueues equipment commands and presents their existing localized result.
/// Selection and command-availability policy remain in MainViewModel.
/// </summary>
internal sealed class EquipmentCommandDispatcher
{
    private readonly ISimulationEngine _engine;
    private readonly Action<string> _setStatus;
    private readonly Action<string, string> _log;

    internal EquipmentCommandDispatcher(
        ISimulationEngine engine,
        Action<string> setStatus,
        Action<string, string> log)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _setStatus = setStatus ?? throw new ArgumentNullException(nameof(setStatus));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    internal Task<SimulationCommandResult> DispatchAxisCommandAsync(
        SimulationCommand command,
        string actionKey) => DispatchAsync(
            command,
            actionKey,
            "Motion",
            "Axis.StatusAccepted",
            "Axis.StatusRejected",
            "Axis.CommandAccepted",
            "Axis.CommandRejected");

    internal Task<SimulationCommandResult> DispatchCameraCommandAsync(
        SimulationCommand command,
        string actionKey) => DispatchAsync(
            command,
            actionKey,
            "Camera",
            "Camera.StatusAccepted",
            "Camera.StatusRejected",
            "Camera.CommandAccepted",
            "Camera.CommandRejected",
            statusUsesAction: false);

    internal Task<SimulationCommandResult> DispatchSensorCommandAsync(
        SimulationCommand command,
        string actionKey) => DispatchAsync(
            command,
            actionKey,
            "Sensor",
            "Sensor.StatusAccepted",
            "Sensor.StatusRejected",
            "Sensor.CommandAccepted",
            "Sensor.CommandRejected");

    internal Task<SimulationCommandResult> DispatchCylinderCommandAsync(
        SimulationCommand command,
        string actionKey) => DispatchAsync(
            command,
            actionKey,
            "Cylinder",
            "Cylinder.StatusAccepted",
            "Cylinder.StatusRejected",
            "Cylinder.CommandAccepted",
            "Cylinder.CommandRejected");

    internal Task<SimulationCommandResult> DispatchConveyorCommandAsync(
        SimulationCommand command,
        string actionKey) => DispatchAsync(
            command,
            actionKey,
            "Conveyor",
            "Conveyor.StatusAccepted",
            "Conveyor.StatusRejected",
            "Conveyor.CommandAccepted",
            "Conveyor.CommandRejected");

    private async Task<SimulationCommandResult> DispatchAsync(
        SimulationCommand command,
        string actionKey,
        string category,
        string acceptedStatusKey,
        string rejectedStatusKey,
        string acceptedMessageKey,
        string rejectedMessageKey,
        bool statusUsesAction = true)
    {
        ArgumentNullException.ThrowIfNull(command);
        var result = await _engine.EnqueueCommandAsync(command);
        var action = OpenVisionLanguageService.T(actionKey);
        var status = OpenVisionLanguageService.T(
            result.IsAccepted ? acceptedStatusKey : rejectedStatusKey);
        _setStatus(statusUsesAction
            ? string.Format(CultureInfo.CurrentCulture, status, action)
            : status);

        var message = OpenVisionLanguageService.T(
            result.IsAccepted ? acceptedMessageKey : rejectedMessageKey);
        _log(
            category,
            result.IsAccepted
                ? string.Format(
                    CultureInfo.CurrentCulture,
                    message,
                    action,
                    ShortCommandId(command))
                : string.Format(
                    CultureInfo.CurrentCulture,
                    message,
                    action,
                    result.ErrorCode,
                    result.Detail));
        return result;
    }

    private static string ShortCommandId(SimulationCommand command) =>
        $"CMD-{command.CommandId[..8].ToUpperInvariant()}";
}
