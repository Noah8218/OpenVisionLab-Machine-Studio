using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using OpenVisionLab;
using OpenVisionLab.Machine.Core.Sequences;
using OpenVisionLab.MachineStudio.View.Project;
using OpenVisionLab.MachineStudio.View.Shell;
using OpenVisionLab.MachineStudio.ViewModel;

namespace OpenVisionLab.MachineStudio;

internal sealed class SmokeProcessBlockStepStatusResult
{
    public SmokeWorkflowReport? Report { get; init; }
}

internal static class SmokeProcessBlockStepStatusVerifier
{
    private const uint MouseEventLeftDown = 0x0002;

    public static bool IsSupportedState(string? state) => state?.ToLowerInvariant() is
        "process-block-step-current"
        or "process-block-step-conflict"
        or "process-block-step-filter"
        or "process-block-step-focus"
        or "process-block-step-pressed"
        or "process-block-step-disabled"
        or "process-block-step-proposed"
        or "process-block-step-removal";

    public static bool RequiresAppliedContext(string? state) =>
        IsSupportedState(state)
        && !state!.Equals("process-block-step-proposed", StringComparison.OrdinalIgnoreCase);

    public static async Task<SmokeProcessBlockStepStatusResult> VerifyAsync(
        ShellWindow window,
        MainViewModel vm,
        string statusState,
        RecipeConnectionWorkbenchView workbench,
        SmokeProcessBlockContext context,
        SmokeAppliedProcessBlockContext? appliedContext,
        string? savePath,
        bool createReport,
        Func<DependencyObject, Func<Button, bool>, Button?> findButton,
        Func<DependencyObject, Func<ListBox, bool>, ListBox?> findListBox,
        Func<DependencyObject, Func<RadioButton, bool>, RadioButton?> findRadioButton,
        Func<DependencyObject, Func<TextBlock, bool>, TextBlock?> findTextBlock,
        Action<FrameworkElement> movePointerToCenter,
        Action<uint, uint, uint, uint, UIntPtr> mouseEvent,
        Action markSmokePointerHeld)
    {
        var normalizedState = statusState.ToLowerInvariant();
        if (!IsSupportedState(normalizedState))
        {
            throw new ArgumentException(
                $"Unsupported --smoke-connection-workbench-state '{statusState}'. " +
                "Expected a process-block step status/filter state.");
        }

        var processBlocks = vm.RecipeConnections.ProcessBlocks;
        if (normalizedState == "process-block-step-proposed")
        {
            var proposedItem = processBlocks.ProcessBlockItems[0];
            var proposedStepButton = findButton(context.Panel, candidate => string.Equals(
                candidate.Name,
                "OpenProcessBlockSequenceStepButton",
                StringComparison.Ordinal));
            Check(
                processBlocks.ProcessBlockItems.All(item => !item.CanOpenSequenceStep)
                && processBlocks.ProcessBlockItems.All(item => !item.DetailText.Contains(
                    $"{OpenVisionLanguageService.T("Sequence.Value")}: ",
                    StringComparison.Ordinal))
                && !vm.RecipeConnections.OpenSequenceStepCommand.CanExecute(proposedItem)
                && (proposedStepButton is null || !proposedStepButton.IsVisible),
                "A proposed process step exposed current settings or navigation before it had an owning Sequence step.");
            return new SmokeProcessBlockStepStatusResult();
        }

        var applied = appliedContext
            ?? throw new InvalidOperationException("An applied process-block context is required for this step status.");
        var processProject = context.Project;
        var processStore = context.Store;
        var processRuntimeBefore = context.RuntimeBefore;
        var processPanel = context.Panel;
        var processApplyButton = context.ApplyButton;
        var appliedBeforeEdit = applied.ProjectAfterApply;
        var currentProcessSequence = processProject.Sequences.FirstOrDefault(sequence => string.Equals(
            sequence.Id,
            processProject.Simulation.AutomaticRun?.SequenceId,
            StringComparison.Ordinal));
        var existingItem = processBlocks.ProcessBlockItems.Single(item => string.Equals(
            item.StepId,
            "process-block.inspect.confirm-position",
            StringComparison.Ordinal));
        var openStepButton = findButton(processPanel, candidate => string.Equals(
                candidate.Name,
                "OpenProcessBlockSequenceStepButton",
                StringComparison.Ordinal))
            ?? throw new InvalidOperationException("Existing process-step navigation button was not available.");
        Check(
            existingItem.CanOpenSequenceStep
            && vm.RecipeConnections.OpenSequenceStepCommand.CanExecute(existingItem)
            && openStepButton.IsVisible,
            "An existing process step did not expose enabled Sequence navigation.");

        if (normalizedState == "process-block-step-conflict")
        {
            var conflictingStep = currentProcessSequence!.Steps.Single(step => string.Equals(
                step.Id,
                existingItem.StepId,
                StringComparison.Ordinal));
            conflictingStep.Action = SequenceStepAction.SetSignal;
            processBlocks.PreviewProcessBlockCommand.Execute(null);
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var conflictItem = processBlocks.ProcessBlockItems.Single(item => string.Equals(
                item.StepId,
                existingItem.StepId,
                StringComparison.Ordinal));
            processBlocks.SelectedProcessBlockItem = conflictItem;
            var conflictItemsList = findListBox(workbench, candidate => string.Equals(
                    candidate.Name,
                    "ProcessBlockItemsListBox",
                    StringComparison.Ordinal))
                ?? throw new InvalidOperationException("Process block step list was not available for conflict evidence.");
            conflictItemsList.ScrollIntoView(conflictItem);
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Check(
                conflictItem.IsUnavailable
                && !conflictItem.IsCustomized
                && !conflictItem.CanOpenSequenceStep
                && processBlocks.HasProcessBlockPlanError
                && !processApplyButton.IsEnabled
                && conflictItem.DetailText.Contains(
                    $"{OpenVisionLanguageService.T("Sequence.Action")}: {SequenceStepAction.SetSignal}",
                    StringComparison.Ordinal)
                && conflictItem.DetailText.Contains(
                    string.Format(
                        System.Globalization.CultureInfo.CurrentCulture,
                        OpenVisionLanguageService.T("Connections.ProcessBlockTemplateValueFormat"),
                        SequenceStepAction.WaitSignal),
                    StringComparison.Ordinal),
                "An Action-conflicting managed step was not blocked and explained as unavailable.");
            return new SmokeProcessBlockStepStatusResult();
        }

        if (normalizedState == "process-block-step-filter")
        {
            var customizedStep = currentProcessSequence!.Steps.Single(step => string.Equals(
                step.Id,
                existingItem.StepId,
                StringComparison.Ordinal));
            customizedStep.TimeoutMs += 100;
            var conflictingStep = currentProcessSequence.Steps.Single(step => string.Equals(
                step.Id,
                "process-block.load.wait-entry",
                StringComparison.Ordinal));
            conflictingStep.Action = SequenceStepAction.SetSignal;
            processBlocks.IsProcessBlockSelected = false;
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);

            var allFilterButton = findRadioButton(processPanel, candidate => string.Equals(
                    candidate.Name,
                    "ProcessBlockFilterAllRadioButton",
                    StringComparison.Ordinal))
                ?? throw new InvalidOperationException("All process-step filter was not available.");
            var customizedFilterButton = findRadioButton(processPanel, candidate => string.Equals(
                    candidate.Name,
                    "ProcessBlockFilterCustomizedRadioButton",
                    StringComparison.Ordinal))
                ?? throw new InvalidOperationException("Customized process-step filter was not available.");
            var removalFilterButton = findRadioButton(processPanel, candidate => string.Equals(
                    candidate.Name,
                    "ProcessBlockFilterRemovalRadioButton",
                    StringComparison.Ordinal))
                ?? throw new InvalidOperationException("Removal process-step filter was not available.");
            var conflictFilterButton = findRadioButton(processPanel, candidate => string.Equals(
                    candidate.Name,
                    "ProcessBlockFilterConflictRadioButton",
                    StringComparison.Ordinal))
                ?? throw new InvalidOperationException("Conflict process-step filter was not available.");
            var emptyFilterText = findTextBlock(processPanel, candidate => string.Equals(
                    candidate.Text,
                    OpenVisionLanguageService.T("Connections.ProcessBlockFilterEmpty"),
                    StringComparison.Ordinal))
                ?? throw new InvalidOperationException("Empty process-step filter status was not available.");
            var filterSource = processStore.SerializeForEvidence(processProject);
            Check(
                allFilterButton.IsChecked == true
                && processBlocks.VisibleProcessBlockItems.Count == 13
                && emptyFilterText.Visibility == Visibility.Collapsed
                && processBlocks.ProcessBlockFilterAllText.Contains("13", StringComparison.Ordinal)
                && processBlocks.ProcessBlockFilterCustomizedText.Contains("1", StringComparison.Ordinal)
                && processBlocks.ProcessBlockFilterRemovalText.Contains("4", StringComparison.Ordinal)
                && processBlocks.ProcessBlockFilterConflictText.Contains("1", StringComparison.Ordinal),
                "The default process-step filter did not show the full plan and current status counts.");

            processBlocks.IsProcessBlockFilterCustomized = true;
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Check(
                customizedFilterButton.IsChecked == true
                && processBlocks.VisibleProcessBlockItems.Count == 1
                && processBlocks.VisibleProcessBlockItems.All(item => item.IsCustomized),
                "The customized process-step filter showed another status.");

            processBlocks.IsProcessBlockFilterRemoval = true;
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Check(
                removalFilterButton.IsChecked == true
                && processBlocks.VisibleProcessBlockItems.Count == 4
                && processBlocks.VisibleProcessBlockItems.All(item => item.IsProposedRemoval),
                "The removal process-step filter showed another status.");
            processBlocks.IsProcessBlockSelected = true;
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Check(
                processBlocks.VisibleProcessBlockItems.Count == 0
                && !processBlocks.HasVisibleProcessBlockItems
                && emptyFilterText.Visibility == Visibility.Visible,
                "An empty process-step filter did not explain that no steps match.");
            processBlocks.IsProcessBlockSelected = false;
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);

            processBlocks.IsProcessBlockFilterConflict = true;
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Check(
                conflictFilterButton.IsChecked == true
                && processBlocks.VisibleProcessBlockItems.Count == 1
                && processBlocks.VisibleProcessBlockItems.All(item => item.IsUnavailable),
                "The conflict process-step filter showed another status.");

            processBlocks.IsProcessBlockFilterAll = true;
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Check(
                allFilterButton.IsChecked == true
                && processBlocks.VisibleProcessBlockItems.Count == processBlocks.ProcessBlockItems.Count,
                "Returning to the All process-step filter did not restore the full plan.");

            processBlocks.IsProcessBlockFilterCustomized = true;
            var filteredItem = processBlocks.VisibleProcessBlockItems.Single();
            processBlocks.SelectedProcessBlockItem = filteredItem;
            var filterItemsList = findListBox(processPanel, candidate => string.Equals(
                    candidate.Name,
                    "ProcessBlockItemsListBox",
                    StringComparison.Ordinal))
                ?? throw new InvalidOperationException("Filtered process-step list was not available.");
            filterItemsList.ScrollIntoView(filteredItem);
            window.Activate();
            customizedFilterButton.BringIntoView();
            customizedFilterButton.Focus();
            Keyboard.Focus(customizedFilterButton);
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Check(
                customizedFilterButton.IsKeyboardFocused,
                "The process-step filter did not expose keyboard focus.");
            movePointerToCenter(customizedFilterButton);
            Mouse.Capture(customizedFilterButton, CaptureMode.SubTree);
            Mouse.Synchronize();
            await Task.Delay(200);
            Check(
                customizedFilterButton.IsMouseOver,
                "The process-step filter did not enter hover state.");
            mouseEvent(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
            markSmokePointerHeld();
            customizedFilterButton.RaiseEvent(new MouseButtonEventArgs(
                Mouse.PrimaryDevice,
                Environment.TickCount,
                MouseButton.Left)
            {
                RoutedEvent = Mouse.MouseDownEvent
            });
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Check(
                customizedFilterButton.IsPressed,
                "The process-step filter did not enter pointer-down state.");
            Check(
                filterSource == processStore.SerializeForEvidence(processProject)
                && processRuntimeBefore?.TickIndex == vm.SceneSnapshots.Latest?.TickIndex
                && processRuntimeBefore?.SimulationTime == vm.SceneSnapshots.Latest?.SimulationTime
                && vm.IsDesignMode
                && !vm.IsRunning,
                "Filtering process steps changed the project or runtime.");
            if (!string.IsNullOrWhiteSpace(savePath))
            {
                Check(
                    !File.Exists(Path.GetFullPath(savePath)),
                    "Filtering process steps unexpectedly saved a project file.");
            }

            return new SmokeProcessBlockStepStatusResult
            {
                Report = createReport
                    ? new SmokeWorkflowReport
                    {
                        Checks = new Dictionary<string, bool>(StringComparer.Ordinal)
                        {
                            ["all-filter-shows-full-plan"] = true,
                            ["filter-labels-show-status-counts"] = true,
                            ["customized-filter-is-exact"] = true,
                            ["removal-filter-is-exact"] = true,
                            ["empty-filter-state-is-explained"] = true,
                            ["conflict-filter-is-exact"] = true,
                            ["all-filter-restores-full-plan"] = true,
                            ["filtered-card-remains-selectable"] = true,
                            ["filter-keyboard-focus-visible"] = true,
                            ["filter-pointer-down-visible"] = true,
                            ["filter-project-runtime-and-save-unchanged"] = true
                        },
                        Failures = []
                    }
                    : null
            };
        }

        if (normalizedState == "process-block-step-current")
        {
            return new SmokeProcessBlockStepStatusResult();
        }

        if (normalizedState == "process-block-step-disabled")
        {
            vm.RecipeConnections.IsEditable = false;
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Check(
                !openStepButton.IsEnabled
                && !vm.RecipeConnections.OpenSequenceStepCommand.CanExecute(existingItem),
                "Existing process-step navigation did not enter its disabled state.");
            return new SmokeProcessBlockStepStatusResult();
        }

        if (normalizedState is "process-block-step-focus" or "process-block-step-pressed")
        {
            window.Activate();
            openStepButton.BringIntoView();
            openStepButton.UpdateLayout();
            openStepButton.Focus();
            Keyboard.Focus(openStepButton);
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Check(openStepButton.IsKeyboardFocused, "Process-step navigation button did not receive focus.");
            if (normalizedState == "process-block-step-pressed")
            {
                movePointerToCenter(openStepButton);
                Mouse.Capture(openStepButton, CaptureMode.SubTree);
                Mouse.Synchronize();
                await Task.Delay(200);
                Check(openStepButton.IsMouseOver, "Process-step navigation button did not enter hover state.");
                mouseEvent(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
                markSmokePointerHeld();
                openStepButton.RaiseEvent(new MouseButtonEventArgs(
                    Mouse.PrimaryDevice,
                    Environment.TickCount,
                    MouseButton.Left)
                {
                    RoutedEvent = Mouse.MouseDownEvent
                });
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                Check(openStepButton.IsPressed, "Process-step navigation button did not enter pointer-down state.");
            }

            return new SmokeProcessBlockStepStatusResult();
        }

        processBlocks.IsInspectBlockSelected = false;
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        var removalItem = processBlocks.ProcessBlockItems.Single(item => string.Equals(
            item.StepId,
            existingItem.StepId,
            StringComparison.Ordinal));
        var removalStep = currentProcessSequence!.Steps.Single(step => string.Equals(
            step.Id,
            removalItem.StepId,
            StringComparison.Ordinal));
        var removalValue = string.IsNullOrWhiteSpace(removalStep.Parameter)
            ? "—"
            : removalStep.Parameter;
        Check(
            removalItem.IsProposedRemoval
            && !removalItem.CanOpenSequenceStep
            && removalItem.DetailText.Contains(
                $"{OpenVisionLanguageService.T("Sequence.Value")}: {removalValue}",
                StringComparison.Ordinal)
            && !vm.RecipeConnections.OpenSequenceStepCommand.CanExecute(removalItem)
            && appliedBeforeEdit == processStore.SerializeForEvidence(processProject),
            "A proposed-removal process step lost its current settings, exposed navigation, or changed the project.");
        return new SmokeProcessBlockStepStatusResult();
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
