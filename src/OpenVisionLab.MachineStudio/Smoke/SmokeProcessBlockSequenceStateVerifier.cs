using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using OpenVisionLab;
using OpenVisionLab.Machine.Core.Layouts;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Core.Sequences;
using OpenVisionLab.Machine.Sequence.Authoring;
using OpenVisionLab.MachineStudio.View.Project;
using OpenVisionLab.MachineStudio.View.Shell;
using OpenVisionLab.MachineStudio.ViewModel;

namespace OpenVisionLab.MachineStudio;

internal static class SmokeProcessBlockSequenceStateVerifier
{
    private const uint MouseEventMove = 0x0001;
    private const uint MouseEventLeftDown = 0x0002;

    public static bool IsSupportedState(string? state) => state?.ToLowerInvariant() is
        "process-block-step-open"
        or "process-block-step-return"
        or "process-block-step-return-sequence"
        or "process-block-step-return-focus"
        or "process-block-step-return-pressed"
        or "process-block-step-return-disabled"
        or "process-block-step-return-closed"
        or "process-block-step-return-reopen"
        or "process-block-step-review";

    public static async Task<SmokeWorkflowReport?> ApplyAsync(
        ShellWindow window,
        MainViewModel vm,
        MachineProjectDocument? initialProject,
        RecipeConnectionWorkbenchView workbench,
        Button processBlockButton,
        string? projectPath,
        string connectionWorkbenchState,
        string? connectionWorkbenchReportPath,
        string? connectionWorkbenchSavePath,
        SmokeUiInteraction interaction,
        Func<DependencyObject, Func<Border, bool>, Border?> findBorder,
        Func<DependencyObject, Func<CheckBox, bool>, CheckBox?> findCheckBox,
        Func<DependencyObject, Func<ListBox, bool>, ListBox?> findListBox)
    {
        SmokeWorkflowReport? report = null;

        switch (connectionWorkbenchState.ToLowerInvariant())
        {
        case "process-block-step-open":
        case "process-block-step-return":
        case "process-block-step-return-sequence":
        case "process-block-step-return-focus":
        case "process-block-step-return-pressed":
        case "process-block-step-return-disabled":
        case "process-block-step-return-closed":
        case "process-block-step-return-reopen":
        case "process-block-step-review":
            var processContext = await SmokeProcessBlockPreparation.PrepareAsync(
                window,
                vm,
                initialProject,
                workbench,
                (root, predicate) => findBorder(root, predicate),
                (root, predicate) => interaction.FindButton(root, predicate),
                (root, predicate) => findCheckBox(root, predicate));
            var processProject = processContext.Project;
            var processStore = processContext.Store;
            var processPanel = processContext.Panel;
            var processApplyButton = processContext.ApplyButton;
            var appliedContext = await SmokeProcessBlockPreparation.ApplyAndRecognizeAsync(
                window,
                vm,
                processContext);
            var appliedBeforeEdit = appliedContext.ProjectAfterApply;
            var currentProcessSequence = processProject.Sequences.FirstOrDefault(sequence => string.Equals(
                sequence.Id,
                processProject.Simulation.AutomaticRun?.SequenceId,
                StringComparison.Ordinal));
            var existingItem = vm.RecipeConnections.ProcessBlocks.ProcessBlockItems.Single(item => string.Equals(
                item.StepId,
                "process-block.inspect.confirm-position",
                StringComparison.Ordinal));
            var openStepButton = interaction.FindButton(processPanel, candidate => string.Equals(
                candidate.Name,
                "OpenProcessBlockSequenceStepButton",
                StringComparison.Ordinal))
                ?? throw new InvalidOperationException("Existing process-step navigation button was not available.");
            AssertSmoke(
                existingItem.CanOpenSequenceStep
                && vm.RecipeConnections.OpenSequenceStepCommand.CanExecute(existingItem)
                && openStepButton.IsVisible,
                "An existing process step did not expose enabled Sequence navigation.");

                    if (connectionWorkbenchState.Equals("process-block-step-review", StringComparison.OrdinalIgnoreCase))
                    {
                        var reviewStepIds = new[]
                        {
                            "process-block.load.wait-entry",
                            "process-block.inspect.confirm-position",
                            "process-block.unload.wait-clear"
                        };
                        foreach (var reviewStepId in reviewStepIds)
                        {
                            currentProcessSequence!.Steps.Single(step => string.Equals(
                                step.Id,
                                reviewStepId,
                                StringComparison.Ordinal)).TimeoutMs += 100;
                        }
                        vm.RecipeConnections.ProcessBlocks.PreviewProcessBlockCommand.Execute(null);
                        vm.RecipeConnections.ProcessBlocks.IsProcessBlockFilterCustomized = true;
                        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                        AssertSmoke(
                            vm.RecipeConnections.ProcessBlocks.VisibleProcessBlockItems.Select(item => item.StepId)
                                .SequenceEqual(reviewStepIds),
                            "The filtered three-step review list was not created in process order.");

                        var middleReviewItem = vm.RecipeConnections.ProcessBlocks.VisibleProcessBlockItems[1];
                        vm.RecipeConnections.ProcessBlocks.SelectedProcessBlockItem = middleReviewItem;
                        var reviewRuntimeBefore = vm.SceneSnapshots.Latest;
                        vm.RecipeConnections.OpenSequenceStepCommand.Execute(middleReviewItem);
                        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);

                        var previousReviewButton = interaction.FindButton(window, candidate => string.Equals(
                            candidate.Name,
                            "PreviousProcessPlanReviewStepButton",
                            StringComparison.Ordinal))
                            ?? throw new InvalidOperationException("Previous filtered-step review button was not available.");
                        var nextReviewButton = interaction.FindButton(window, candidate => string.Equals(
                            candidate.Name,
                            "NextProcessPlanReviewStepButton",
                            StringComparison.Ordinal))
                            ?? throw new InvalidOperationException("Next filtered-step review button was not available.");
                        AssertSmoke(
                            vm.SelectedDocumentTabIndex == 2
                            && string.Equals(vm.SequenceEditor.SelectedStep?.Id, reviewStepIds[1], StringComparison.Ordinal)
                            && string.Equals(vm.ProcessPlanReturnStepId, reviewStepIds[1], StringComparison.Ordinal)
                            && vm.ProcessPlanReviewPositionText.Contains("2/3", StringComparison.Ordinal)
                            && previousReviewButton.IsEnabled
                            && nextReviewButton.IsEnabled,
                            "Opening the middle filtered step did not create a 2/3 review context.");

                        vm.SequenceEditor.SelectedStep!.TimeoutMs += 50;
                        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                        AssertSmoke(
                            vm.HasProcessPlanReturnContext
                            && vm.RecipeConnections.ProcessBlocks.IsProcessBlockFilterCustomized
                            && vm.RecipeConnections.ProcessBlocks.VisibleProcessBlockItems.Count == 3
                            && vm.ProcessPlanReviewPositionText.Contains("2/3", StringComparison.Ordinal)
                            && vm.PreviousProcessPlanReviewStepCommand.CanExecute(null)
                            && vm.NextProcessPlanReviewStepCommand.CanExecute(null),
                            "Editing the current step discarded its filtered review context.");
                        var reviewSourceAfterEdit = processStore.SerializeForEvidence(processProject);

                        vm.NextProcessPlanReviewStepCommand.Execute(null);
                        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                        AssertSmoke(
                            string.Equals(vm.SequenceEditor.SelectedStep?.Id, reviewStepIds[2], StringComparison.Ordinal)
                            && string.Equals(vm.ProcessPlanReturnStepId, reviewStepIds[2], StringComparison.Ordinal)
                            && vm.ProcessPlanReviewPositionText.Contains("3/3", StringComparison.Ordinal)
                            && vm.PreviousProcessPlanReviewStepCommand.CanExecute(null)
                            && !vm.NextProcessPlanReviewStepCommand.CanExecute(null)
                            && previousReviewButton.IsEnabled
                            && !nextReviewButton.IsEnabled,
                            "Next review did not select the exact last filtered step and enforce its boundary.");

                        vm.PreviousProcessPlanReviewStepCommand.Execute(null);
                        vm.PreviousProcessPlanReviewStepCommand.Execute(null);
                        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                        AssertSmoke(
                            string.Equals(vm.SequenceEditor.SelectedStep?.Id, reviewStepIds[0], StringComparison.Ordinal)
                            && string.Equals(vm.ProcessPlanReturnStepId, reviewStepIds[0], StringComparison.Ordinal)
                            && vm.ProcessPlanReviewPositionText.Contains("1/3", StringComparison.Ordinal)
                            && !vm.PreviousProcessPlanReviewStepCommand.CanExecute(null)
                            && vm.NextProcessPlanReviewStepCommand.CanExecute(null)
                            && !previousReviewButton.IsEnabled
                            && nextReviewButton.IsEnabled,
                            "Previous review did not select the exact first filtered step and enforce its boundary.");

                        vm.NextProcessPlanReviewStepCommand.Execute(null);
                        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                        vm.RecipeConnections.IsEditable = false;
                        ((RelayCommand)vm.PreviousProcessPlanReviewStepCommand).RaiseCanExecuteChanged();
                        ((RelayCommand)vm.NextProcessPlanReviewStepCommand).RaiseCanExecuteChanged();
                        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                        AssertSmoke(
                            !previousReviewButton.IsEnabled
                            && !nextReviewButton.IsEnabled
                            && !vm.PreviousProcessPlanReviewStepCommand.CanExecute(null)
                            && !vm.NextProcessPlanReviewStepCommand.CanExecute(null),
                            "Filtered-step review navigation did not disable with the workbench.");
                        vm.RecipeConnections.IsEditable = true;
                        ((RelayCommand)vm.PreviousProcessPlanReviewStepCommand).RaiseCanExecuteChanged();
                        ((RelayCommand)vm.NextProcessPlanReviewStepCommand).RaiseCanExecuteChanged();
                        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);

                        interaction.ActivateWindow();
                        nextReviewButton.BringIntoView();
                        nextReviewButton.Focus();
                        Keyboard.Focus(nextReviewButton);
                        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                        AssertSmoke(
                            nextReviewButton.IsKeyboardFocused,
                            "Next filtered-step review did not expose keyboard focus.");
                        interaction.MovePointerToCenter(nextReviewButton);
                        Mouse.Capture(nextReviewButton, CaptureMode.SubTree);
                        Mouse.Synchronize();
                        await Task.Delay(200);
                        AssertSmoke(
                            nextReviewButton.IsMouseOver,
                            "Next filtered-step review did not enter hover state.");
                        interaction.MouseEvent(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
                        interaction.MarkSmokePointerHeld();
                        nextReviewButton.RaiseEvent(new MouseButtonEventArgs(
                            Mouse.PrimaryDevice,
                            Environment.TickCount,
                            MouseButton.Left)
                        {
                            RoutedEvent = Mouse.MouseDownEvent
                        });
                        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                        AssertSmoke(
                            nextReviewButton.IsPressed,
                            "Next filtered-step review did not enter pointer-down state.");
                        AssertSmoke(
                            reviewSourceAfterEdit == processStore.SerializeForEvidence(processProject)
                            && reviewRuntimeBefore?.TickIndex == vm.SceneSnapshots.Latest?.TickIndex
                            && reviewRuntimeBefore?.SimulationTime == vm.SceneSnapshots.Latest?.SimulationTime
                            && vm.IsDesignMode
                            && !vm.IsRunning,
                            "Filtered-step review navigation changed the project or runtime.");
                        if (!string.IsNullOrWhiteSpace(connectionWorkbenchSavePath))
                        {
                            AssertSmoke(
                                !File.Exists(Path.GetFullPath(connectionWorkbenchSavePath)),
                                "Filtered-step review unexpectedly saved a project file.");
                        }
                        if (!string.IsNullOrWhiteSpace(connectionWorkbenchReportPath))
                        {
                            report = new SmokeWorkflowReport
                            {
                                Checks = new Dictionary<string, bool>(StringComparer.Ordinal)
                                {
                                    ["filtered-review-list-captured-in-order"] = true,
                                    ["middle-review-position-shown"] = true,
                                    ["sequence-edit-preserved-review-context"] = true,
                                    ["next-selected-exact-filtered-step"] = true,
                                    ["last-step-disabled-next"] = true,
                                    ["previous-selected-exact-filtered-step"] = true,
                                    ["first-step-disabled-previous"] = true,
                                    ["disabled-workbench-disabled-review"] = true,
                                    ["review-keyboard-focus-visible"] = true,
                                    ["review-pointer-down-visible"] = true,
                                    ["review-project-and-runtime-unchanged"] = true,
                                    ["review-did-not-save-project"] = true
                                },
                                Failures = []
                            };
                            report.Save(connectionWorkbenchReportPath);
                        }
                        break;
                    }

                    var navigationRuntimeBefore = vm.SceneSnapshots.Latest;
                    vm.RecipeConnections.OpenSequenceStepCommand.Execute(existingItem);
                    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    AssertSmoke(
                        vm.SelectedDocumentTabIndex == 2
                        && string.Equals(vm.SequenceEditor.SelectedSequence?.Id, existingItem.SequenceId, StringComparison.Ordinal)
                        && string.Equals(vm.SequenceEditor.SelectedStep?.Id, existingItem.StepId, StringComparison.Ordinal),
                        $"Process-step navigation did not select its exact owning Sequence step. "
                        + $"tab={vm.SelectedDocumentTabIndex}, expectedSequence={existingItem.SequenceId}, "
                        + $"actualSequence={vm.SequenceEditor.SelectedSequence?.Id}, expectedStep={existingItem.StepId}, "
                        + $"actualStep={vm.SequenceEditor.SelectedStep?.Id}");
                    AssertSmoke(
                        appliedBeforeEdit == processStore.SerializeForEvidence(processProject)
                        && navigationRuntimeBefore?.TickIndex == vm.SceneSnapshots.Latest?.TickIndex
                        && navigationRuntimeBefore?.SimulationTime == vm.SceneSnapshots.Latest?.SimulationTime
                        && vm.IsDesignMode
                        && !vm.IsRunning,
                        "Process-step navigation changed the project or runtime.");

                    if (connectionWorkbenchState.StartsWith("process-block-step-return", StringComparison.OrdinalIgnoreCase))
                    {
                        var returnButton = interaction.FindButton(window, candidate => string.Equals(
                            candidate.Name,
                            "ReturnToProcessPlanButton",
                            StringComparison.Ordinal))
                            ?? throw new InvalidOperationException("Return-to-process-plan button was not available.");
                        var returnBar = findBorder(window, candidate => string.Equals(
                            candidate.Name,
                            "ProcessPlanReturnBar",
                            StringComparison.Ordinal))
                            ?? throw new InvalidOperationException("Return-to-process-plan bar was not available.");
                        AssertSmoke(
                            vm.HasProcessPlanReturnContext
                            && string.Equals(vm.ProcessPlanReturnStepId, existingItem.StepId, StringComparison.Ordinal)
                            && vm.ReturnToProcessPlanCommand.CanExecute(null)
                            && returnBar.IsVisible
                            && returnButton.IsEnabled,
                            "Opening a managed process step did not retain an enabled return context.");

                        if (connectionWorkbenchState.Equals("process-block-step-return-sequence", StringComparison.OrdinalIgnoreCase))
                        {
                            break;
                        }
                        if (connectionWorkbenchState.Equals("process-block-step-return-focus", StringComparison.OrdinalIgnoreCase)
                            || connectionWorkbenchState.Equals("process-block-step-return-pressed", StringComparison.OrdinalIgnoreCase))
                        {
                            interaction.ActivateWindow();
                            returnButton.Focus();
                            Keyboard.Focus(returnButton);
                            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                            AssertSmoke(returnButton.IsKeyboardFocused, "Return-to-process-plan button did not receive focus.");
                            if (connectionWorkbenchState.Equals("process-block-step-return-pressed", StringComparison.OrdinalIgnoreCase))
                            {
                                interaction.MovePointerToCenter(returnButton);
                                Mouse.Capture(returnButton, CaptureMode.SubTree);
                                Mouse.Synchronize();
                                await Task.Delay(200);
                                AssertSmoke(returnButton.IsMouseOver, "Return-to-process-plan button did not enter hover state.");
                                interaction.MouseEvent(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
                                interaction.MarkSmokePointerHeld();
                                returnButton.RaiseEvent(new MouseButtonEventArgs(
                                    Mouse.PrimaryDevice,
                                    Environment.TickCount,
                                    MouseButton.Left)
                                {
                                    RoutedEvent = Mouse.MouseDownEvent
                                });
                                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                                AssertSmoke(returnButton.IsPressed, "Return-to-process-plan button did not enter pointer-down state.");
                            }
                            break;
                        }
                        if (connectionWorkbenchState.Equals("process-block-step-return-disabled", StringComparison.OrdinalIgnoreCase))
                        {
                            vm.RecipeConnections.IsEditable = false;
                            if (vm.ReturnToProcessPlanCommand is RelayCommand returnCommand)
                            {
                                returnCommand.RaiseCanExecuteChanged();
                            }
                            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                            AssertSmoke(
                                !vm.ReturnToProcessPlanCommand.CanExecute(null) && !returnButton.IsEnabled,
                                "Return-to-process-plan button did not enter its disabled state.");
                            break;
                        }
                        if (connectionWorkbenchState.Equals("process-block-step-return-closed", StringComparison.OrdinalIgnoreCase))
                        {
                            vm.RecipeConnections.ProcessBlocks.CancelProcessBlockCommand.Execute(null);
                            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                            AssertSmoke(
                                !vm.HasProcessPlanReturnContext
                                && !vm.ReturnToProcessPlanCommand.CanExecute(null)
                                && !returnBar.IsVisible,
                                "Closing the process plan did not clear its return context.");
                            break;
                        }
                        if (connectionWorkbenchState.Equals("process-block-step-return-reopen", StringComparison.OrdinalIgnoreCase))
                        {
                            AssertSmoke(
                                !string.IsNullOrWhiteSpace(projectPath)
                                && await vm.OpenProjectAsync(Path.GetFullPath(projectPath)),
                                "The source project could not be reopened for return-context validation.");
                            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                            AssertSmoke(
                                !vm.HasProcessPlanReturnContext
                                && !vm.ReturnToProcessPlanCommand.CanExecute(null)
                                && !returnBar.IsVisible
                                && vm.IsDesignMode
                                && !vm.IsRunning,
                                "Reopening a project retained stale process-plan return context.");
                            break;
                        }

                        var selectedSequenceStep = vm.SequenceEditor.SelectedStep
                            ?? throw new InvalidOperationException("The managed Sequence step was not selected for tuning.");
                        var templateTimeoutMs = selectedSequenceStep.TimeoutMs;
                        selectedSequenceStep.TimeoutMs += 100;
                        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                        var customizedItem = vm.RecipeConnections.ProcessBlocks.ProcessBlockItems.Single(item => string.Equals(
                            item.StepId,
                            existingItem.StepId,
                            StringComparison.Ordinal));
                        AssertSmoke(
                            vm.HasProcessPlanReturnContext
                            && vm.RecipeConnections.ProcessBlocks.IsProcessBlockPreviewVisible
                            && customizedItem.IsCustomized
                            && !customizedItem.IsUnavailable
                            && customizedItem.CanOpenSequenceStep
                            && vm.RecipeConnections.OpenSequenceStepCommand.CanExecute(customizedItem)
                            && !vm.RecipeConnections.ProcessBlocks.HasProcessBlockPlanError
                            && !processApplyButton.IsEnabled
                            && customizedItem.DetailText.Contains(
                                $"{OpenVisionLanguageService.T("Sequence.Timeout")}: "
                                + $"{selectedSequenceStep.TimeoutMs.ToString("N0", System.Globalization.CultureInfo.CurrentCulture)} ms",
                                StringComparison.Ordinal)
                            && customizedItem.DetailText.Contains(
                                string.Format(
                                    System.Globalization.CultureInfo.CurrentCulture,
                                    OpenVisionLanguageService.T("Connections.ProcessBlockTemplateValueFormat"),
                                    $"{templateTimeoutMs.ToString("N0", System.Globalization.CultureInfo.CurrentCulture)} ms"),
                                StringComparison.Ordinal),
                            "Editing a managed timeout did not preserve, classify, explain, and keep the plan safely navigable.");
                        var editedBeforeReturn = processStore.SerializeForEvidence(processProject);
                        var runtimeBeforeReturn = vm.SceneSnapshots.Latest;
                        vm.ReturnToProcessPlanCommand.Execute(null);
                        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                        await Task.Delay(100);
                        var processItemsList = findListBox(workbench, candidate => string.Equals(
                            candidate.Name,
                            "ProcessBlockItemsListBox",
                            StringComparison.Ordinal))
                            ?? throw new InvalidOperationException("Process block step list was not available after return.");
                        var returnedItem = vm.RecipeConnections.ProcessBlocks.SelectedProcessBlockItem;
                        var returnedContainer = returnedItem is null
                            ? null
                            : processItemsList.ItemContainerGenerator.ContainerFromItem(returnedItem) as FrameworkElement;
                        var returnedBounds = returnedContainer is null
                            ? Rect.Empty
                            : new Rect(
                                returnedContainer.TranslatePoint(new Point(0, 0), processItemsList),
                                new Size(returnedContainer.ActualWidth, returnedContainer.ActualHeight));
                        var processListViewport = new Rect(
                            new Point(0, 0),
                            new Size(processItemsList.ActualWidth, processItemsList.ActualHeight));
                        var returnedWindowBounds = returnedContainer is null
                            ? Rect.Empty
                            : new Rect(
                                returnedContainer.TranslatePoint(new Point(0, 0), window),
                                new Size(returnedContainer.ActualWidth, returnedContainer.ActualHeight));
                        var windowViewport = new Rect(
                            new Point(0, 0),
                            new Size(window.ActualWidth, window.ActualHeight));
                        AssertSmoke(
                            vm.SelectedDocumentTabIndex == 1
                            && vm.RecipeConnections.ProcessBlocks.IsProcessBlockPreviewVisible
                            && returnedItem is not null
                            && string.Equals(returnedItem.StepId, existingItem.StepId, StringComparison.Ordinal)
                            && returnedContainer is not null
                            && returnedBounds.IntersectsWith(processListViewport)
                            && windowViewport.Contains(returnedWindowBounds.TopLeft)
                            && windowViewport.Contains(returnedWindowBounds.BottomRight),
                            "Return-to-process-plan did not restore and reveal the exact originating card.");
                        AssertSmoke(
                            editedBeforeReturn == processStore.SerializeForEvidence(processProject)
                            && runtimeBeforeReturn?.TickIndex == vm.SceneSnapshots.Latest?.TickIndex
                            && runtimeBeforeReturn?.SimulationTime == vm.SceneSnapshots.Latest?.SimulationTime
                            && vm.IsDesignMode
                            && !vm.IsRunning,
                            "Returning to the process plan changed the edited project or runtime.");
                        if (!string.IsNullOrWhiteSpace(connectionWorkbenchSavePath))
                        {
                            AssertSmoke(
                                !File.Exists(Path.GetFullPath(connectionWorkbenchSavePath)),
                                "Returning to the process plan unexpectedly saved a project file.");
                        }
                        if (!string.IsNullOrWhiteSpace(connectionWorkbenchReportPath))
                        {
                            report = new SmokeWorkflowReport
                            {
                                Checks = new Dictionary<string, bool>(StringComparer.Ordinal)
                                {
                                    ["process-card-return-context-created"] = true,
                                    ["return-command-visible-and-enabled"] = true,
                                    ["sequence-edit-preserved-open-plan"] = true,
                                    ["sequence-edit-refreshed-current-card-settings"] = true,
                                    ["sequence-edit-classified-customized"] = true,
                                    ["customized-card-explained-template-difference"] = true,
                                    ["customized-card-remained-navigable"] = true,
                                    ["connections-document-restored"] = true,
                                    ["exact-origin-card-selected"] = true,
                                    ["exact-origin-card-scrolled-into-view"] = true,
                                    ["return-project-unchanged"] = true,
                                    ["return-runtime-unchanged"] = true,
                                    ["return-remains-stopped-in-design"] = true,
                                    ["return-did-not-save-project"] = true
                                },
                                Failures = []
                            };
                            report.Save(connectionWorkbenchReportPath);
                        }
                        break;
                    }

                    var bundledNavigationRecipes = Directory.EnumerateFiles(
                            Path.Combine(AppContext.BaseDirectory, "Samples", "SemiconductorRecipes"),
                            "*.ovmachine")
                        .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                    var allBundledTargetsExact = bundledNavigationRecipes.Length == 10;
                    foreach (var path in bundledNavigationRecipes)
                    {
                        var bundledProject = processStore.Load(File.ReadAllText(path));
                        var composer = new SemiconductorProcessBlockComposer();
                        composer.Apply(bundledProject, Enum.GetValues<SemiconductorProcessBlockKind>());
                        var preview = composer.Preview(
                            bundledProject,
                            Enum.GetValues<SemiconductorProcessBlockKind>());
                        var sequenceId = bundledProject.Simulation.AutomaticRun?.SequenceId;
                        var sequence = bundledProject.Sequences.FirstOrDefault(candidate => string.Equals(
                            candidate.Id,
                            sequenceId,
                            StringComparison.Ordinal));
                        allBundledTargetsExact &= preview.Steps.Count == 13
                            && preview.Steps.All(step => step.Status == SemiconductorProcessBlockStepStatus.Existing)
                            && sequence is not null
                            && preview.Steps.All(entry => sequence.Steps.Any(step => string.Equals(
                                step.Id,
                                entry.StepId,
                                StringComparison.Ordinal)));
                    }
                    AssertSmoke(
                        allBundledTargetsExact,
                        "All ten bundled recipes did not resolve every managed process card to an exact Sequence step.");
                    if (!string.IsNullOrWhiteSpace(connectionWorkbenchSavePath))
                    {
                        AssertSmoke(
                            !File.Exists(Path.GetFullPath(connectionWorkbenchSavePath)),
                            "Process-step navigation unexpectedly wrote a project file.");
                    }
                    if (!string.IsNullOrWhiteSpace(connectionWorkbenchReportPath))
                    {
                        report = new SmokeWorkflowReport
                        {
                            Checks = new Dictionary<string, bool>(StringComparer.Ordinal)
                            {
                                ["existing-card-navigation-enabled"] = true,
                                ["exact-owning-sequence-selected"] = true,
                                ["exact-owning-step-selected"] = true,
                                ["sequence-document-opened"] = true,
                                ["navigation-project-unchanged"] = true,
                                ["navigation-runtime-unchanged"] = true,
                                ["navigation-remains-stopped-in-design"] = true,
                                ["ten-recipes-thirteen-managed-targets-resolved"] = true,
                                ["navigation-did-not-save-project"] = true
                            },
                            Failures = []
                        };
                        report.Save(connectionWorkbenchReportPath);
                    }
                    break;
                default:
                    throw new ArgumentException(
                        $"Unsupported --smoke-connection-workbench-state '{connectionWorkbenchState}'. " +
                        "Expected a supported connection-workbench smoke state, including dry-run, dry-run-playback, or dry-run-wafer-handler-fault-playback.");
        }

        return report;
    }

    private static void AssertSmoke(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
