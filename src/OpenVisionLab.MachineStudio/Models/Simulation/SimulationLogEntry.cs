using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using OpenVisionLab;

namespace OpenVisionLab.MachineStudio.Models.Simulation;

public sealed class SimulationLogEntry : INotifyPropertyChanged
{
    public SimulationLogEntry(DateTimeOffset timestamp, SimulationLogSeverity severity, string message)
    {
        Timestamp = timestamp;
        Severity = severity;
        Message = message;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public DateTimeOffset Timestamp { get; }

    public SimulationLogSeverity Severity { get; }

    public string Message { get; }

    public string TimestampText => Timestamp.ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    public string SeverityText => Severity switch
    {
        SimulationLogSeverity.Warning => OpenVisionLanguageService.T("Runtime.Warning"),
        SimulationLogSeverity.Alarm => OpenVisionLanguageService.T("Runtime.Alarm"),
        SimulationLogSeverity.Recovery => OpenVisionLanguageService.T("Runtime.Recovery"),
        _ => OpenVisionLanguageService.T("Runtime.Info")
    };

    public string SeverityGlyph => Severity switch
    {
        SimulationLogSeverity.Warning => "W",
        SimulationLogSeverity.Alarm => "!",
        SimulationLogSeverity.Recovery => "R",
        _ => "I"
    };

    public string DisplayText => $"[{TimestampText}] [{SeverityText}] {LocalizedMessage}";

    private string LocalizedMessage => LocalizeMessage(Message);

    public static string LocalizeMessage(string message) => message switch
    {
        "Simulation workspace initialized. Press Start Simulation to run virtual equipment."
            => OpenVisionLanguageService.T("Runtime.WorkspaceInitialized"),
        "Simulation started." => OpenVisionLanguageService.T("Runtime.SimulationStarted"),
        "Simulation resumed." => OpenVisionLanguageService.T("Runtime.SimulationResumed"),
        "Simulation paused." => OpenVisionLanguageService.T("Runtime.SimulationPaused"),
        "Simulation stopped." => OpenVisionLanguageService.T("Runtime.SimulationStopped"),
        "Emergency stop activated. All equipment forced offline."
            => OpenVisionLanguageService.T("Runtime.EmergencyStop"),
        "Simulation reset to idle state." => OpenVisionLanguageService.T("Runtime.SimulationReset"),
        "All equipment alarms were acknowledged." => OpenVisionLanguageService.T("Runtime.AlarmsAcknowledged"),
        "Scenario JSON path is empty. Please enter a file path."
            => OpenVisionLanguageService.T("Runtime.ScenarioPathEmpty"),
        _ => LocalizeParameterizedMessage(message)
    };

    public void RefreshLocalization()
    {
        OnPropertyChanged(nameof(SeverityText));
        OnPropertyChanged(nameof(DisplayText));
    }

    private static string LocalizeParameterizedMessage(string message)
    {
        if (message.StartsWith("Scenario profile not found: ", StringComparison.Ordinal))
        {
            return string.Format(
                CultureInfo.CurrentCulture,
                OpenVisionLanguageService.T("Runtime.ScenarioProfileNotFound"),
                message["Scenario profile not found: ".Length..]);
        }

        if (message.Equals("Deterministic machine runtime ready · fixed step 5 ms", StringComparison.Ordinal))
        {
            return OpenVisionLanguageService.T("Runtime.MachineRuntimeReady");
        }

        if (message.StartsWith("Configured ", StringComparison.Ordinal))
        {
            return message
                .Replace("Configured ", $"{OpenVisionLanguageService.T("Runtime.ConfiguredPrefix")} ", StringComparison.Ordinal)
                .Replace("axis/axes", OpenVisionLanguageService.T("Runtime.AxisCount"), StringComparison.Ordinal)
                .Replace("signal(s)", OpenVisionLanguageService.T("Runtime.SignalCount"), StringComparison.Ordinal)
                .Replace("camera(s)", OpenVisionLanguageService.T("Runtime.CameraCount"), StringComparison.Ordinal)
                .Replace("sequence(s)", OpenVisionLanguageService.T("Runtime.SequenceCount"), StringComparison.Ordinal)
                .Replace("layout component(s)", OpenVisionLanguageService.T("Runtime.LayoutCount"), StringComparison.Ordinal)
                .Replace(" and ", $" {OpenVisionLanguageService.T("Runtime.And")} ", StringComparison.Ordinal);
        }

        if (message.Equals("Runtime configuration applied atomically.", StringComparison.Ordinal))
        {
            return OpenVisionLanguageService.T("Runtime.ConfigurationApplied");
        }

        if (message.StartsWith("Simulation ON requested · ", StringComparison.Ordinal))
        {
            return string.Format(
                CultureInfo.CurrentCulture,
                OpenVisionLanguageService.T("Runtime.SimulationOnRequested"),
                message["Simulation ON requested · ".Length..]);
        }

        if (message.StartsWith("Automatic sequence '", StringComparison.Ordinal)
            && message.EndsWith("' started.", StringComparison.Ordinal))
        {
            string sequenceId = message.Substring(
                "Automatic sequence '".Length,
                message.Length - "Automatic sequence '".Length - "' started.".Length);
            return string.Format(
                CultureInfo.CurrentCulture,
                OpenVisionLanguageService.T("Runtime.AutomaticSequenceStarted"),
                LocalizeSequenceName(sequenceId));
        }

        var sequenceMarker = " entered ";
        var sequenceMarkerIndex = message.IndexOf(sequenceMarker, StringComparison.Ordinal);
        if (sequenceMarkerIndex > 0 && message.EndsWith(".", StringComparison.Ordinal))
        {
            string sequenceId = message[..sequenceMarkerIndex];
            string stepId = message[(sequenceMarkerIndex + sequenceMarker.Length)..^1];
            return string.Format(
                CultureInfo.CurrentCulture,
                OpenVisionLanguageService.T("Runtime.SequenceEntered"),
                LocalizeSequenceName(sequenceId),
                LocalizeStepName(sequenceId, stepId));
        }

        var transitionMarker = ": ";
        var transitionIndex = message.IndexOf(transitionMarker, StringComparison.Ordinal);
        if (transitionIndex > 0 && message.EndsWith(".", StringComparison.Ordinal))
        {
            string sequenceId = message[..transitionIndex];
            string transition = message[(transitionIndex + transitionMarker.Length)..^1];
            var stepTransition = transition.Split(" -> ", StringSplitOptions.None);
            if (stepTransition.Length == 2)
            {
                return $"{LocalizeSequenceName(sequenceId)}: "
                    + $"{LocalizeStepName(sequenceId, stepTransition[0])} -> "
                    + $"{LocalizeStepName(sequenceId, stepTransition[1])}.";
            }
        }

        if (message.Equals("do.cycle-active = ON.", StringComparison.Ordinal))
        {
            return OpenVisionLanguageService.T("Runtime.CycleActiveOn");
        }

        if (message.StartsWith("Could not parse scenario profile from: ", StringComparison.Ordinal))
        {
            return string.Format(
                CultureInfo.CurrentCulture,
                OpenVisionLanguageService.T("Runtime.ScenarioParseFailed"),
                message["Could not parse scenario profile from: ".Length..]);
        }

        if (message.StartsWith("Loaded scenario profile: ", StringComparison.Ordinal))
        {
            return string.Format(
                CultureInfo.CurrentCulture,
                OpenVisionLanguageService.T("Runtime.ScenarioLoaded"),
                message["Loaded scenario profile: ".Length..]);
        }

        if (message.StartsWith("Scenario reset to Normal with seed 1001 and 200 cycles.", StringComparison.Ordinal))
        {
            return OpenVisionLanguageService.T("Runtime.ScenarioReset");
        }

        if (message.StartsWith("Scenario applied: ", StringComparison.Ordinal))
        {
            return string.Format(
                CultureInfo.CurrentCulture,
                OpenVisionLanguageService.T("Runtime.ScenarioApplied"),
                message["Scenario applied: ".Length..]);
        }

        var separatorIndex = message.IndexOf(": ", StringComparison.Ordinal);
        if (separatorIndex > 0)
        {
            var equipmentName = message[..separatorIndex];
            var detail = message[(separatorIndex + 2)..];
            if (detail.Equals("startup complete", StringComparison.Ordinal))
            {
                return string.Format(
                    CultureInfo.CurrentCulture,
                    OpenVisionLanguageService.T("Runtime.StartupComplete"),
                    equipmentName);
            }

            if (detail.StartsWith("warning - ", StringComparison.Ordinal))
            {
                return string.Format(
                    CultureInfo.CurrentCulture,
                    OpenVisionLanguageService.T("Runtime.EquipmentWarning"),
                    equipmentName,
                    LocalizeAlarm(detail["warning - ".Length..]));
            }

            if (detail.StartsWith("alarm - ", StringComparison.Ordinal))
            {
                return string.Format(
                    CultureInfo.CurrentCulture,
                    OpenVisionLanguageService.T("Runtime.EquipmentAlarm"),
                    equipmentName,
                    LocalizeAlarm(detail["alarm - ".Length..]));
            }

            if (detail.Equals("warning cleared, recovered to normal", StringComparison.Ordinal))
            {
                return string.Format(
                    CultureInfo.CurrentCulture,
                    OpenVisionLanguageService.T("Runtime.WarningCleared"),
                    equipmentName);
            }

            if (detail.Equals("warning escalated to alarm", StringComparison.Ordinal))
            {
                return string.Format(
                    CultureInfo.CurrentCulture,
                    OpenVisionLanguageService.T("Runtime.WarningEscalated"),
                    equipmentName);
            }

            if (detail.Equals("auto recovery sequence started", StringComparison.Ordinal))
            {
                return string.Format(
                    CultureInfo.CurrentCulture,
                    OpenVisionLanguageService.T("Runtime.AutoRecoveryStarted"),
                    equipmentName);
            }

            if (detail.Equals("recovered, waiting for restart", StringComparison.Ordinal))
            {
                return string.Format(
                    CultureInfo.CurrentCulture,
                    OpenVisionLanguageService.T("Runtime.RecoveredWaiting"),
                    equipmentName);
            }
        }

        return message;
    }

    private static string LocalizeAlarm(string alarm) =>
        OpenVisionLanguageService.T(
            $"Runtime.AlarmMessage.{alarm.Replace(' ', '_')}",
            alarm,
            alarm);

    private static string LocalizeSequenceName(string sequenceId) =>
        OpenVisionLanguageService.TUserText("sequence", $"{sequenceId}.name", sequenceId);

    private static string LocalizeStepName(string sequenceId, string stepId) =>
        OpenVisionLanguageService.TUserText(
            "sequence",
            $"{sequenceId}.step.{stepId}.name",
            stepId);

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
