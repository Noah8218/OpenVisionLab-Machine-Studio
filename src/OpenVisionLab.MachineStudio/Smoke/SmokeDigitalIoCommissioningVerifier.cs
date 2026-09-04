using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using OpenVisionLab;
using OpenVisionLab.Machine.Simulation.Engine;
using OpenVisionLab.MachineStudio.View.Inspector;
using OpenVisionLab.MachineStudio.View.Shell;
using OpenVisionLab.MachineStudio.ViewModel;

namespace OpenVisionLab.MachineStudio;

internal sealed class SmokeDigitalIoCommissioningReport
{
    public string Schema { get; init; } = "1.0";
    public DateTimeOffset CapturedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public required IReadOnlyDictionary<string, bool> Checks { get; init; }
    public required IReadOnlyList<string> Failures { get; init; }
    public SmokeMonitorEvidence? Monitor { get; init; }
    public bool IsValid => Failures.Count == 0 && Checks.Values.All(value => value);

    public void Save(string path)
    {
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(
            fullPath,
            JsonSerializer.Serialize(this, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = true
            }));
    }
}

internal static class SmokeDigitalIoCommissioningVerifier
{
    private const uint MouseEventLeftDown = 0x0002;
    public static async Task<SmokeDigitalIoCommissioningReport> VerifyAsync(
        ShellWindow window,
        MainViewModel viewModel,
        string? projectPath,
        Func<Task> scrollIntoView)
    {
        if (!viewModel.IsRunMode || string.IsNullOrWhiteSpace(projectPath))
        {
            throw new ArgumentException(
                "--smoke-io-commissioning-report requires --smoke-run-layout and --smoke-project.");
        }

        var checks = new Dictionary<string, bool>(StringComparer.Ordinal);
        var failures = new List<string>();
        void Check(string name, bool passed)
        {
            checks[name] = passed;
            if (!passed)
            {
                failures.Add(name);
            }
        }

        async Task WaitForAsync(Func<bool> condition, string failureMessage)
        {
            for (var attempt = 0; attempt < 100; attempt++)
            {
                if (condition())
                {
                    return;
                }
                await Task.Delay(50);
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            }
            throw new InvalidOperationException(failureMessage);
        }

        async Task ExecuteAndWaitAsync(ICommand command, string actionKey)
        {
            string action = OpenVisionLanguageService.T(actionKey);
            int previousCount = viewModel.LogMessages.Count(line =>
                line.Contains(action, StringComparison.CurrentCulture));
            command.Execute(null);
            await WaitForAsync(
                () => viewModel.LogMessages.Count(line =>
                    line.Contains(action, StringComparison.CurrentCulture)) > previousCount,
                $"I/O action '{action}' was not logged.");
        }

        var commissioning = viewModel.DigitalIo;
        await WaitForAsync(
            () => commissioning.IsEnabled && commissioning.HasSignals,
            "Digital I/O commissioning did not become available from the runtime snapshot.");
        var initialSnapshot = viewModel.SceneSnapshots.Latest
            ?? throw new InvalidOperationException("The initial runtime snapshot was unavailable.");
        Check("snapshotSignalsProjected", commissioning.Signals.Count == initialSnapshot.Signals.Count);

        commissioning.SelectedSignal = commissioning.Signals.FirstOrDefault(signal => !signal.IsInput);
        Check("digitalOutputReadOnly", commissioning.SelectedSignal is not null
            && !commissioning.CanForceOn
            && !commissioning.CanForceOff
            && !commissioning.CanClearForce);

        commissioning.SelectedSignal = commissioning.Signals.FirstOrDefault(signal => signal.IsInput);
        var selectedInput = commissioning.SelectedSignal
            ?? throw new InvalidOperationException("No digital input was available for commissioning.");
        var initialInput = initialSnapshot.Signals.Single(signal => signal.Id == selectedInput.Id);
        Check("selectedValueUsesSnapshot", selectedInput.Value == initialInput.Value
            && selectedInput.NominalValue == initialInput.NominalValue
            && selectedInput.OverrideValue == initialInput.OverrideValue);
        Check("snapshotOwnerAndRevisionProjected",
            commissioning.ControlOwnerText == viewModel.ControlOwnerText
            && commissioning.SignalRevisionText == initialSnapshot.SignalRevision.ToString(
                System.Globalization.CultureInfo.InvariantCulture));

        await ExecuteAndWaitAsync(
            commissioning.StartManualControlCommand,
            "Io.ActionStartManual");
        await WaitForAsync(
            () => viewModel.IsRunning
                && viewModel.SceneSnapshots.Latest?.ControlOwner == SimulationControlOwner.Manual,
            "Manual I/O control did not start.");
        viewModel.PauseCommand.Execute(null);
        await WaitForAsync(() => !viewModel.IsRunning, "Manual I/O control did not pause.");

        long revisionBeforeForce = viewModel.SceneSnapshots.Latest!.SignalRevision;
        await ExecuteAndWaitAsync(commissioning.ForceOnCommand, "Io.ActionForceOn");
        await WaitForAsync(
            () => commissioning.SelectedSignal?.OverrideValue == true,
            "Forced-ON I/O state was not published.");
        Check("forceOnUpdatesImmutableSnapshot", commissioning.SelectedSignal?.Value == true
            && viewModel.SceneSnapshots.Latest!.SignalRevision > revisionBeforeForce);

        long tickBeforeStep = viewModel.SceneSnapshots.Latest!.TickIndex;
        viewModel.StepCommand.Execute(null);
        await WaitForAsync(
            () => viewModel.SceneSnapshots.Latest?.TickIndex == tickBeforeStep + 1,
            "I/O commissioning Step did not advance exactly one tick.");
        Check("pauseStepRetainsForce", commissioning.SelectedSignal?.OverrideValue == true
            && commissioning.SelectedSignal.Value);

        await ExecuteAndWaitAsync(commissioning.ClearForceCommand, "Io.ActionClearForce");
        await WaitForAsync(
            () => commissioning.SelectedSignal?.OverrideValue is null,
            "Cleared I/O force state was not published.");
        Check("clearRestoresNominal", commissioning.SelectedSignal?.Value
            == commissioning.SelectedSignal?.NominalValue);

        await ExecuteAndWaitAsync(commissioning.ForceOffCommand, "Io.ActionForceOff");
        await WaitForAsync(
            () => commissioning.SelectedSignal?.OverrideValue == false,
            "Forced-OFF I/O state was not published.");
        viewModel.ResetCommand.Execute(null);
        await WaitForAsync(
            () => viewModel.SceneSnapshots.Latest is { TickIndex: 0, ControlOwner: SimulationControlOwner.Definition }
                && commissioning.Signals.All(signal => signal.OverrideValue is null),
            "Reset did not clear the I/O force and restore the authored runtime.");
        Check("resetClearsAllForces", commissioning.Signals.All(signal => signal.OverrideValue is null));

        if (!await viewModel.OpenProjectAsync(projectPath))
        {
            throw new InvalidOperationException("The I/O commissioning project could not be reopened.");
        }
        await WaitForAsync(
            () => commissioning.HasSignals
                && commissioning.Signals.All(signal => signal.OverrideValue is null),
            "Project reopen restored a runtime-only I/O force.");
        Check("reopenDoesNotRestoreRuntimeForce",
            commissioning.Signals.All(signal => signal.OverrideValue is null)
            && viewModel.SceneSnapshots.Latest?.ControlOwner == SimulationControlOwner.Definition);

        viewModel.IsRunMode = true;
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        await scrollIntoView();
        return new SmokeDigitalIoCommissioningReport
        {
            Checks = checks,
            Failures = failures
        };
    }

    public static async Task ApplyStateAsync(
        ShellWindow window,
        MainViewModel viewModel,
        string state,
        SmokeUiInteraction interaction)
    {
        if (!viewModel.IsRunMode)
        {
            throw new ArgumentException(
                "--smoke-io-commissioning-state requires --smoke-run-layout.");
        }

        async Task WaitForAsync(Func<bool> condition, string failureMessage)
        {
            for (var attempt = 0; attempt < 100; attempt++)
            {
                if (condition())
                {
                    return;
                }
                await Task.Delay(50);
            }
            throw new InvalidOperationException(failureMessage);
        }

        async Task ExecuteAndWaitAsync(ICommand command, string actionKey)
        {
            string action = OpenVisionLanguageService.T(actionKey);
            int previousCount = viewModel.LogMessages.Count(line =>
                line.Contains(action, StringComparison.CurrentCulture));
            command.Execute(null);
            await WaitForAsync(
                () => viewModel.LogMessages.Count(line =>
                    line.Contains(action, StringComparison.CurrentCulture)) > previousCount,
                $"I/O action '{action}' was not logged.");
        }

        var commissioning = viewModel.DigitalIo;
        await WaitForAsync(
            () => commissioning.HasSignals,
            "No digital I/O signals were published for the commissioning smoke state.");
        var inspector = SmokeVisualTreeQuery.FindVisualDescendant<RightToolRegionView>(window)
            ?? throw new InvalidOperationException("Run inspector was unavailable.");
        bool isStartButtonState = state.Equals("focus-start", StringComparison.OrdinalIgnoreCase)
            || state.Equals("hover-start", StringComparison.OrdinalIgnoreCase)
            || state.Equals("pressed-start", StringComparison.OrdinalIgnoreCase);
        bool isOutputState = state.Equals("output-disabled", StringComparison.OrdinalIgnoreCase);

        commissioning.SelectedSignal = isOutputState
            ? commissioning.Signals.FirstOrDefault(signal => !signal.IsInput)
            : commissioning.Signals.FirstOrDefault(signal => signal.IsInput);
        if (commissioning.SelectedSignal is null)
        {
            throw new InvalidOperationException(
                isOutputState
                    ? "No digital output was available for the smoke state."
                    : "No digital input was available for the smoke state.");
        }

        if (!state.Equals("ready", StringComparison.OrdinalIgnoreCase)
            && !isStartButtonState
            && !isOutputState)
        {
            await ExecuteAndWaitAsync(
                commissioning.StartManualControlCommand,
                "Io.ActionStartManual");
            await WaitForAsync(
                () => viewModel.IsRunning
                    && viewModel.SceneSnapshots.Latest?.ControlOwner == SimulationControlOwner.Manual,
                "Manual I/O control did not start for the smoke state.");
            viewModel.PauseCommand.Execute(null);
            await WaitForAsync(() => !viewModel.IsRunning, "Manual I/O control did not pause.");
        }

        if (state.Equals("focus-start", StringComparison.OrdinalIgnoreCase))
        {
            inspector.StartDigitalIoManualControlButton.Focus();
        }
        else if (state.Equals("hover-start", StringComparison.OrdinalIgnoreCase)
                 || state.Equals("pressed-start", StringComparison.OrdinalIgnoreCase))
        {
            interaction.MovePointerToCenter(inspector.StartDigitalIoManualControlButton);
            if (state.StartsWith("pressed", StringComparison.OrdinalIgnoreCase))
            {
                interaction.MouseEvent(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
                interaction.MarkSmokePointerHeld();
            }
        }
        else if (state.Equals("forced-on", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteAndWaitAsync(commissioning.ForceOnCommand, "Io.ActionForceOn");
            await WaitForAsync(
                () => commissioning.SelectedSignal?.OverrideValue == true,
                "Forced-ON I/O state was not published.");
        }
        else if (state.Equals("forced-off", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteAndWaitAsync(commissioning.ForceOffCommand, "Io.ActionForceOff");
            await WaitForAsync(
                () => commissioning.SelectedSignal?.OverrideValue == false,
                "Forced-OFF I/O state was not published.");
        }
        else if (state.Equals("cleared", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteAndWaitAsync(commissioning.ForceOnCommand, "Io.ActionForceOn");
            await ExecuteAndWaitAsync(commissioning.ClearForceCommand, "Io.ActionClearForce");
            await WaitForAsync(
                () => commissioning.SelectedSignal?.OverrideValue is null,
                "Cleared I/O force state was not published.");
        }
        else if (state.Equals("focus-on", StringComparison.OrdinalIgnoreCase))
        {
            inspector.DigitalIoForceOnButton.Focus();
        }
        else if (state.Equals("hover-off", StringComparison.OrdinalIgnoreCase)
                 || state.Equals("pressed-on", StringComparison.OrdinalIgnoreCase))
        {
            var button = state.StartsWith("hover", StringComparison.OrdinalIgnoreCase)
                ? inspector.DigitalIoForceOffButton
                : inspector.DigitalIoForceOnButton;
            interaction.MovePointerToCenter(button);
            if (state.StartsWith("pressed", StringComparison.OrdinalIgnoreCase))
            {
                interaction.MouseEvent(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
                interaction.MarkSmokePointerHeld();
            }
        }
        else if (!state.Equals("ready", StringComparison.OrdinalIgnoreCase)
                 && !state.Equals("manual", StringComparison.OrdinalIgnoreCase)
                 && !isOutputState)
        {
            throw new ArgumentException(
                $"Unsupported --smoke-io-commissioning-state '{state}'. Expected ready, manual, " +
                "forced-on, forced-off, cleared, output-disabled, focus-start, hover-start, " +
                "pressed-start, focus-on, hover-off, or pressed-on.");
        }

        await ScrollIntoViewAsync(window);
        await Task.Delay(150);
    }

    public static async Task ScrollIntoViewAsync(ShellWindow window)
    {
        var inspector = SmokeVisualTreeQuery.FindVisualDescendant<RightToolRegionView>(window)
            ?? throw new InvalidOperationException("Run inspector was unavailable.");
        var scrollViewer = inspector.RunInspectorScrollViewer;
        var targetPosition = inspector.DigitalIoSectionAnchor.TranslatePoint(new Point(), scrollViewer);
        scrollViewer.ScrollToVerticalOffset(
            scrollViewer.VerticalOffset + targetPosition.Y - 8);
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
    }
}
