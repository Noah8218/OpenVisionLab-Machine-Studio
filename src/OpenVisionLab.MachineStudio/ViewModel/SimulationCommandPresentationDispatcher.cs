using System.Globalization;
using OpenVisionLab;
using OpenVisionLab.Machine.Simulation.Commands;
using OpenVisionLab.Machine.Simulation.Engine;

namespace OpenVisionLab.MachineStudio.ViewModel;

/// <summary>
/// Enqueues non-equipment simulation commands and presents their localized result.
/// Runtime snapshot projection and public ViewModel wiring remain in the shell.
/// </summary>
internal sealed class SimulationCommandPresentationDispatcher
{
    private readonly ISimulationEngine _engine;
    private readonly Action<string> _setStatus;
    private readonly Action<string, string> _log;

    internal SimulationCommandPresentationDispatcher(
        ISimulationEngine engine,
        Action<string> setStatus,
        Action<string, string> log)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _setStatus = setStatus ?? throw new ArgumentNullException(nameof(setStatus));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    internal async Task<SimulationCommandResult> DispatchRuntimeDebuggerAsync(
        SimulationCommand command,
        Action applySnapshot)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(applySnapshot);

        var result = await _engine.EnqueueCommandAsync(command);
        applySnapshot();
        _setStatus(OpenVisionLanguageService.T(
            result.IsAccepted ? "Debugger.CommandAcceptedStatus" : "Debugger.CommandRejectedStatus",
            result.IsAccepted ? "디버거 명령을 적용했습니다." : "디버거 명령이 거부되었습니다.",
            result.IsAccepted ? "Debugger command applied." : "Debugger command rejected."));
        _log("Sequence", result.IsAccepted
            ? $"Debugger command accepted · {ShortCommandId(command)}"
            : $"Debugger command rejected · {result.ErrorCode}: {result.Detail}");
        return result;
    }

    internal async Task<SimulationCommandResult> DispatchDigitalIoAsync(
        SimulationCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var result = await _engine.EnqueueCommandAsync(command);
        var actionKey = command switch
        {
            StartManualControlCommand => "Io.ActionStartManual",
            SetVirtualInputForceCommand { ForcedValue: true } => "Io.ActionForceOn",
            SetVirtualInputForceCommand { ForcedValue: false } => "Io.ActionForceOff",
            SetVirtualInputForceCommand => "Io.ActionClearForce",
            _ => "Io.ActionCommand"
        };
        var action = OpenVisionLanguageService.T(actionKey);
        _setStatus(string.Format(
            CultureInfo.CurrentCulture,
            OpenVisionLanguageService.T(
                result.IsAccepted ? "Io.StatusAccepted" : "Io.StatusRejected"),
            action));
        _log("I/O", result.IsAccepted
            ? string.Format(
                CultureInfo.CurrentCulture,
                OpenVisionLanguageService.T("Io.CommandAccepted"),
                action,
                ShortCommandId(command))
            : string.Format(
                CultureInfo.CurrentCulture,
                OpenVisionLanguageService.T("Io.CommandRejected"),
                action,
                result.ErrorCode,
                result.Detail));
        return result;
    }

    internal async Task<SimulationCommandResult> DispatchFaultAsync(
        SimulationCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var result = await _engine.EnqueueCommandAsync(command);
        var isInject = command is InjectSimulationFaultCommand;
        var action = OpenVisionLanguageService.T(isInject ? "Fault.ActionInject" : "Fault.ActionClear");
        if (!result.IsAccepted)
        {
            _setStatus(string.Format(
                CultureInfo.CurrentCulture,
                OpenVisionLanguageService.T("Fault.StatusRejected"),
                action));
            _log("Fault", string.Format(
                CultureInfo.CurrentCulture,
                OpenVisionLanguageService.T("Fault.CommandRejected"),
                action,
                result.ErrorCode,
                result.Detail));
            return result;
        }

        _setStatus(OpenVisionLanguageService.T(
            isInject ? "Fault.InjectAcceptedStatus" : "Fault.ClearAcceptedStatus"));
        _log("Fault", string.Format(
            CultureInfo.CurrentCulture,
            OpenVisionLanguageService.T("Fault.CommandAccepted"),
            action,
            ShortCommandId(command)));
        return result;
    }

    private static string ShortCommandId(SimulationCommand command) =>
        $"CMD-{command.CommandId[..8].ToUpperInvariant()}";
}
