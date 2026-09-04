using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using OpenVisionLab.Machine.Sequence.Authoring;
using OpenVisionLab.MachineStudio.View.Shell;
using OpenVisionLab.MachineStudio.ViewModel;

namespace OpenVisionLab.MachineStudio;

internal sealed class SmokeProcessBlockTimeoutResult
{
    public SmokeWorkflowReport? Report { get; init; }
}

internal static class SmokeProcessBlockTimeoutVerifier
{
    private const uint MouseEventLeftDown = 0x0002;

    public static bool IsSupportedState(string? state) =>
        state?.Equals("process-block-timeout-batch", StringComparison.OrdinalIgnoreCase) == true;

    public static async Task<SmokeProcessBlockTimeoutResult> VerifyAsync(
        ShellWindow window,
        MainViewModel vm,
        string timeoutState,
        SmokeAppliedProcessBlockContext appliedContext,
        string? savePath,
        bool createReport,
        Func<DependencyObject, Func<TextBox, bool>, TextBox?> findTextBox,
        Func<DependencyObject, Func<Button, bool>, Button?> findButton,
        Func<DependencyObject, Func<ItemsControl, bool>, ItemsControl?> findItemsControl,
        Action<FrameworkElement> movePointerToCenter,
        Action<uint, uint, uint, uint, UIntPtr> mouseEvent,
        Action markSmokePointerHeld)
    {
        if (!IsSupportedState(timeoutState))
        {
            throw new ArgumentException(
                $"Unsupported --smoke-connection-workbench-state '{timeoutState}'. " +
                "Expected process-block-timeout-batch.");
        }

        var context = appliedContext.PreviewContext;
        var processBlocks = vm.RecipeConnections.ProcessBlocks;
        var existingItem = processBlocks.ProcessBlockItems.Single(item => string.Equals(
            item.StepId,
            "process-block.inspect.confirm-position",
            StringComparison.Ordinal));
        var openStepButton = findButton(context.Panel, candidate => string.Equals(
                candidate.Name,
                "OpenProcessBlockSequenceStepButton",
                StringComparison.Ordinal))
            ?? throw new InvalidOperationException("Existing process-step navigation button was not available.");
        Check(
            existingItem.CanOpenSequenceStep
            && vm.RecipeConnections.OpenSequenceStepCommand.CanExecute(existingItem)
            && openStepButton.IsVisible,
            "An existing process step did not expose enabled Sequence navigation.");

        var timeoutTextBox = findTextBox(context.Panel, candidate => string.Equals(
                candidate.Name,
                "ProcessBlockTimeoutTextBox",
                StringComparison.Ordinal))
            ?? throw new InvalidOperationException("Managed timeout input was not available.");
        var previewTimeoutButton = findButton(context.Panel, candidate => string.Equals(
                candidate.Name,
                "PreviewProcessBlockTimeoutButton",
                StringComparison.Ordinal))
            ?? throw new InvalidOperationException("Managed timeout preview button was not available.");
        var applyTimeoutButton = findButton(context.Panel, candidate => string.Equals(
                candidate.Name,
                "ApplyProcessBlockTimeoutButton",
                StringComparison.Ordinal))
            ?? throw new InvalidOperationException("Managed timeout Apply button was not available.");
        var cancelTimeoutButton = findButton(context.Panel, candidate => string.Equals(
                candidate.Name,
                "CancelProcessBlockTimeoutButton",
                StringComparison.Ordinal))
            ?? throw new InvalidOperationException("Managed timeout Cancel button was not available.");

        processBlocks.ProcessBlockTimeoutText = "-1";
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Check(
            !processBlocks.IsProcessBlockTimeoutValid
            && !previewTimeoutButton.IsEnabled
            && string.Equals(timeoutTextBox.Text, "-1", StringComparison.Ordinal),
            "Invalid managed timeout input was not rendered and blocked.");

        processBlocks.ProcessBlockTimeoutText = "6000";
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        var timeoutSourceBeforePreview = context.Store.SerializeForEvidence(context.Project);
        Check(
            processBlocks.CompatibleProcessBlockTimeoutCount == 6
            && previewTimeoutButton.IsEnabled,
            "The All filter did not expose its six compatible managed wait steps.");
        processBlocks.PreviewProcessBlockTimeoutsCommand.Execute(null);
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Check(
            processBlocks.IsProcessBlockTimeoutPreviewVisible
            && processBlocks.ProcessBlockTimeoutItems.Count == 6
            && processBlocks.ProcessBlockTimeoutItems.All(item =>
                item.DetailText.Contains("6,000", StringComparison.Ordinal)
                || item.DetailText.Contains("6000", StringComparison.Ordinal))
            && applyTimeoutButton.IsVisible
            && applyTimeoutButton.IsEnabled
            && cancelTimeoutButton.IsVisible
            && timeoutSourceBeforePreview == context.Store.SerializeForEvidence(context.Project),
            "Managed timeout preview did not show six per-step changes without mutation.");

        processBlocks.ApplyProcessBlockTimeoutsCommand.Execute(null);
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        var currentProcessSequence = context.Project.Sequences.Single(sequence => string.Equals(
            sequence.Id,
            context.Project.Simulation.AutomaticRun?.SequenceId,
            StringComparison.Ordinal));
        var adjustedWaits = currentProcessSequence.Steps.Where(step =>
            step.Id.StartsWith("process-block.", StringComparison.Ordinal)
            && SemiconductorProcessBlockComposer.CanAdjustTimeout(step.Action)).ToArray();
        Check(
            adjustedWaits.Length == 6
            && adjustedWaits.All(step => step.TimeoutMs == 6000)
            && processBlocks.IsProcessBlockPreviewVisible
            && processBlocks.IsProcessBlockFilterAll
            && vm.IsDesignMode
            && !vm.IsRunning,
            "Managed timeout Apply did not atomically update six waits and preserve the open plan.");
        var timeoutSourceAfterApply = context.Store.SerializeForEvidence(context.Project);
        Check(
            timeoutSourceAfterApply != timeoutSourceBeforePreview
            && context.RuntimeBefore?.TickIndex == vm.SceneSnapshots.Latest?.TickIndex
            && context.RuntimeBefore?.SimulationTime == vm.SceneSnapshots.Latest?.SimulationTime,
            "Managed timeout Apply changed the runtime or failed to change the authored project.");

        processBlocks.ProcessBlockTimeoutText = "6500";
        processBlocks.PreviewProcessBlockTimeoutsCommand.Execute(null);
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Check(
            processBlocks.ProcessBlockTimeoutItems.Count == 6
            && timeoutSourceAfterApply == context.Store.SerializeForEvidence(context.Project),
            "The post-apply timeout preview changed the project or lost its six-step scope.");
        var timeoutItems = findItemsControl(context.Panel, candidate => string.Equals(
                candidate.Name,
                "ProcessBlockTimeoutItemsControl",
                StringComparison.Ordinal))
            ?? throw new InvalidOperationException("Managed timeout preview items were not available.");
        if (vm.IsCompactLayout)
        {
            timeoutItems.BringIntoView();
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        }

        window.Activate();
        timeoutTextBox.Focus();
        Keyboard.Focus(timeoutTextBox);
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Check(
            timeoutTextBox.IsKeyboardFocused
            && string.Equals(timeoutTextBox.Text, "6500", StringComparison.Ordinal),
            "Managed timeout input did not expose focused non-empty text.");
        movePointerToCenter(applyTimeoutButton);
        Mouse.Capture(applyTimeoutButton, CaptureMode.SubTree);
        Mouse.Synchronize();
        await Task.Delay(200);
        Check(
            applyTimeoutButton.IsMouseOver,
            "Managed timeout Apply did not enter hover state.");
        mouseEvent(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
        markSmokePointerHeld();
        applyTimeoutButton.RaiseEvent(new MouseButtonEventArgs(
            Mouse.PrimaryDevice,
            Environment.TickCount,
            MouseButton.Left)
        {
            RoutedEvent = Mouse.MouseDownEvent
        });
        for (var attempt = 0; attempt < 10 && !applyTimeoutButton.IsPressed; attempt++)
        {
            await Task.Delay(50);
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        }
        Check(
            applyTimeoutButton.IsPressed,
            "Managed timeout Apply did not enter pointer-down state.");

        if (!string.IsNullOrWhiteSpace(savePath))
        {
            Check(
                !File.Exists(Path.GetFullPath(savePath)),
                "Managed timeout workflow unexpectedly saved a project file.");
        }

        return new SmokeProcessBlockTimeoutResult
        {
            Report = createReport
                ? new SmokeWorkflowReport
                {
                    Checks = new Dictionary<string, bool>(StringComparer.Ordinal)
                    {
                        ["invalid-timeout-blocked"] = true,
                        ["six-compatible-filtered-waits"] = true,
                        ["preview-listed-six-step-changes"] = true,
                        ["preview-left-project-unchanged"] = true,
                        ["atomic-apply-updated-six-waits"] = true,
                        ["apply-preserved-open-filtered-plan"] = true,
                        ["apply-left-runtime-stopped"] = true,
                        ["second-preview-left-project-unchanged"] = true,
                        ["non-empty-input-visible"] = true,
                        ["keyboard-focus-visible"] = true,
                        ["hover-and-pointer-down-visible"] = true,
                        ["workflow-did-not-save-project"] = true
                    },
                    Failures = []
                }
                : null
        };
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
