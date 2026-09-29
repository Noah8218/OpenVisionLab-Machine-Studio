using System.IO;
using System.Text.Json;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using OpenVisionLab.Localization;
using OpenVisionLab.MachineStudio.View.Mmi;
using OpenVisionLab.MachineStudio.View.Inspector;
using OpenVisionLab.MachineStudio.View.Project;
using OpenVisionLab.MachineStudio.View.Sequence;
using OpenVisionLab.MachineStudio.View.Scene;
using OpenVisionLab.MachineStudio.View.Shell;
using OpenVisionLab.MachineStudio.View.Simulation;
using OpenVisionLab.MachineStudio.ViewModel;

namespace OpenVisionLab.MachineStudio;

internal static class SmokeWorkspaceNavigationVerifier
{
    internal static async Task ApplyAsync(ShellWindow window, MainViewModel viewModel, string workspace,
        string? reportPath, SmokeNativeInput input, SmokeWindowCapture capture)
    {
        var target = workspace.ToLowerInvariant() switch
        {
            "equipment" => 0,
            "simulation" or "execution" or "sequence" or "tests" => 1,
            "inspection" or "teaching" or "integration" => 2,
            "results" => 3,
            _ => throw new ArgumentException("Expected equipment, simulation, inspection, sequence, tests, integration, or results.")
        };
        var sequenceWorkspace = workspace.Equals("sequence", StringComparison.OrdinalIgnoreCase);
        var testWorkspace = workspace.Equals("tests", StringComparison.OrdinalIgnoreCase);
        var integrationWorkspace = workspace.Equals("integration", StringComparison.OrdinalIgnoreCase);
        var snapshotBeforeNavigation = viewModel.SceneSnapshots.Latest;
        var dirtyBeforeNavigation = viewModel.HasUnsavedChanges;
        var runningBeforeNavigation = viewModel.IsRunning;
        viewModel.Navigation.SelectedWorkspaceIndex = target;
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        if (string.IsNullOrWhiteSpace(reportPath))
        {
            if (sequenceWorkspace) viewModel.Navigation.SelectedExecutionTabIndex = 1;
            if (testWorkspace) viewModel.Navigation.SelectedExecutionTabIndex = 2;
            if (integrationWorkspace) viewModel.Navigation.SelectedInspectionTabIndex = 1;
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var sceneDocument = SmokeVisualTreeQuery.FindVisualDescendant<SceneDocumentView>(window);
            var inspection = SmokeVisualTreeQuery.FindVisualDescendant<MmiOperatorLayoutView>(window);
            var sceneVisible = sceneDocument?.SceneViewport.IsVisible == true;
            var overviewVisible = sceneDocument is not null
                && SmokeVisualTreeQuery.FindVisualDescendant<ScrollViewer>(sceneDocument,
                    view => view.Name == "LargeOverviewScrollViewer")?.IsVisible == true;
            var sequenceVisible = sceneDocument?.SequenceWorkspaceTab.IsSelected == true;
            var testsVisible = sceneDocument?.TestScenarioWorkspaceTab.IsSelected == true;
            var connectionsVisible = SmokeVisualTreeQuery.FindVisualDescendant<RecipeConnectionWorkbenchView>(window)?.IsVisible == true;
            if (sceneDocument is null
                || sceneVisible != (target == 0 && !viewModel.Layout.IsLargeOverview
                    || target == 1 && !sequenceWorkspace && !testWorkspace)
                || overviewVisible != (target == 0 && viewModel.Layout.IsLargeOverview)
                || sequenceVisible != sequenceWorkspace
                || testsVisible != testWorkspace
                || connectionsVisible != (target == 2 && !integrationWorkspace)
                || (inspection?.IsVisible ?? false) != integrationWorkspace
                || sceneDocument.ResultsWorkspacePanel.IsVisible != (target == 3))
            {
                throw new InvalidOperationException("R19 workspace content did not match the selected route.");
            }
            if (SmokeVisualTreeQuery.FindVisualDescendants<TextBlock>(sceneDocument)
                .Any(text => text.IsVisible && System.Text.RegularExpressions.Regex.IsMatch(
                    text.Text, @"\bMMI\b(?![-\\/])", System.Text.RegularExpressions.RegexOptions.IgnoreCase)))
                throw new InvalidOperationException("A retired product label is still visible.");
            if (target == 1 && viewModel.Navigation.SelectedExecutionTabIndex == 0)
            {
                var selector = SmokeVisualTreeQuery.FindVisualDescendant<ComboBox>(sceneDocument,
                    control => control.Name == "SimulationUnitSelector");
                var unitScopePlaceholder = SmokeVisualTreeQuery.FindVisualDescendant<TextBlock>(sceneDocument,
                    control => control.Name == "SimulationUnitScopePlaceholder");
                var wholeMachine = SmokeVisualTreeQuery.FindVisualDescendant<Button>(sceneDocument,
                    control => control.Name == "SimulationWholeMachineButton");
                var inspectorToggle = SmokeVisualTreeQuery.FindVisualDescendant<Button>(sceneDocument,
                    control => control.Name == "SimulationInspectorToggleButton");
                var shellInspectorToggle = SmokeVisualTreeQuery.FindVisualDescendant<Button>(window,
                    control => control.Name == "WorkspaceInspectorToggleButton");
                if (selector is null || !selector.IsVisible || selector.Items.Count != viewModel.Layout.UnitOverviewItems.Count
                    || !Equals(selector.SelectedValue, viewModel.Layout.ActiveUnitId)
                    || unitScopePlaceholder?.IsVisible != (viewModel.Layout.ActiveUnitId is null)
                    || wholeMachine?.IsVisible != true
                    || inspectorToggle?.IsVisible != true
                    || !ReferenceEquals(inspectorToggle.Command, viewModel.Navigation.ToggleInspectorCommand)
                    || shellInspectorToggle?.IsVisible == true)
                {
                    throw new InvalidOperationException("Simulation Unit controls do not match the current project state.");
                }

                var inspectorWasOpen = viewModel.Navigation.IsInspectorOpen;
                try
                {
                    if (!inspectorWasOpen) inspectorToggle.Command.Execute(null);
                    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    var inspector = SmokeVisualTreeQuery.FindVisualDescendant<RightToolRegionView>(window);
                    var sceneTop = sceneDocument.SceneViewport.TranslatePoint(new System.Windows.Point(), window).Y;
                    var inspectorTop = inspector?.TranslatePoint(new System.Windows.Point(), window).Y ?? double.NaN;
                    if (inspector?.IsVisible != true || !double.IsFinite(inspectorTop) || inspectorTop < sceneTop - 1)
                    {
                        throw new InvalidOperationException("Simulation properties overlay overlaps the pagebar or scene header.");
                    }
                }
                finally
                {
                    if (viewModel.Navigation.IsInspectorOpen != inspectorWasOpen)
                    {
                        viewModel.Navigation.ToggleInspectorCommand.Execute(null);
                    }
                }
            }
            if (target == 1 && !string.Equals(
                    sceneDocument.SequenceWorkspaceTab.Header?.ToString(),
                    OpenVisionLanguageService.T("Simulation.EditActions"),
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Simulation sequence tab does not expose the R19 action-edit label.");
            }
            if (runningBeforeNavigation != viewModel.IsRunning
                || (!runningBeforeNavigation && !ReferenceEquals(snapshotBeforeNavigation, viewModel.SceneSnapshots.Latest))
                || dirtyBeforeNavigation != viewModel.HasUnsavedChanges)
                throw new InvalidOperationException("Workspace navigation changed the runtime or project.");
            Console.WriteLine("R19 workspace route and navigation isolation passed.");
            return;
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(reportPath))!;
        Directory.CreateDirectory(directory);
        var document = SmokeVisualTreeQuery.FindVisualDescendant<SceneDocumentView>(window)
            ?? throw new InvalidOperationException("Scene document was unavailable.");
        RadioButton[] buttons = [window.EquipmentWorkspaceButton, window.SimulationWorkspaceButton,
            window.InspectionWorkspaceButton, window.ResultsWorkspaceButton];
        var running = viewModel.IsRunning;
        var runMode = viewModel.IsRunMode;
        var dirty = viewModel.HasUnsavedChanges;
        var projectRoot = viewModel.ProjectTree.Roots.Single();
        var selectedNode = viewModel.ProjectTree.SelectedNode;
        var selectedItem = viewModel.Layout.SelectedItem;
        var stoppedTick = viewModel.TickStatusText;
        var wasTopmost = window.Topmost;
        var passed = new List<string>();
        string? failure = null;
        try
        {
            window.Topmost = true;
            window.Activate();
            for (var index = 0; index < buttons.Length; index++)
            {
                var button = buttons[index];
                input.ActivateWindow(window);
                input.MovePointerToCenter(button);
                await Task.Delay(100);
                var ownership = input.CheckPointerOwnership(window);
                if (index == 0 && !ownership.IsOwned)
                {
                    // Retry foreground activation, but never send input until this window owns it.
                    var activationDeadline = Environment.TickCount64 + 2000;
                    while (!ownership.IsOwned && Environment.TickCount64 < activationDeadline)
                    {
                        await Task.Delay(50);
                        input.ActivateWindow(window);
                        ownership = input.CheckPointerOwnership(window);
                    }
                }
                capture.Capture(window, Path.Combine(directory, $"workspace-{index}-hover.png"));
                Require(ownership.IsOwned, ownership.Diagnostic);
                Require(button.IsMouseOver, $"Workspace {index}: hover missing.");
                input.PressLeftButton();
                input.MarkPointerHeld();
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                Require(button.IsPressed, $"Workspace {index}: pressed missing.");
                capture.Capture(window, Path.Combine(directory, $"workspace-{index}-pressed.png"));
                // As in the project-exit smoke, deliver mouse-up before cleanup
                // releases capture; otherwise WPF cancels the pending click.
                input.SendMouseEvent(0x0004, 0, 0, 0, UIntPtr.Zero);
                for (var attempt = 0; attempt < 100 && button.IsPressed; attempt++)
                {
                    await Task.Delay(10);
                }
                input.ReleasePointer();
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                Require(button.IsChecked == true && viewModel.Navigation.SelectedWorkspaceIndex == index,
                    $"Workspace {index}: rendered selection and binding disagree.");
                Require(buttons.Count(candidate => candidate.IsChecked == true) == 1,
                    "Workspace selection must be exclusive.");
                Require(viewModel.IsRunning == running && viewModel.IsRunMode == runMode
                    && viewModel.HasUnsavedChanges == dirty && ReferenceEquals(viewModel.ProjectTree.Roots.Single(), projectRoot)
                    && ReferenceEquals(viewModel.ProjectTree.SelectedNode, selectedNode)
                    && ReferenceEquals(viewModel.Layout.SelectedItem, selectedItem)
                    && (running || viewModel.TickStatusText == stoppedTick),
                    "Workspace navigation changed runtime, project, tick, or equipment selection.");
                Require(document.ResultsWorkspacePanel.IsVisible == (index == 3), "Results visibility disagrees.");
                var simulationTab = viewModel.Navigation.SelectedExecutionTabIndex;
                Require(document.SceneViewport.IsVisible == (index == 0 || index == 1 && simulationTab == 0),
                    "Equipment and simulation did not retain the shared scene route.");
                Require(document.SequenceWorkspaceTab.IsSelected == (index == 1 && simulationTab == 1),
                    "Sequence editor tab selection disagrees.");
                Require(document.TestScenarioWorkspaceTab.IsSelected == (index == 1 && simulationTab == 2),
                    "Simulation test tab selection disagrees.");
                var inspection = SmokeVisualTreeQuery.FindVisualDescendant<MmiOperatorLayoutView>(document);
                var connectionWorkbench = SmokeVisualTreeQuery.FindVisualDescendant<RecipeConnectionWorkbenchView>(document);
                Require((inspection?.IsVisible ?? false) == (index == 2 && viewModel.Navigation.SelectedInspectionTabIndex == 1)
                    && (connectionWorkbench?.IsVisible ?? false) == (index == 2 && viewModel.Navigation.SelectedInspectionTabIndex == 0),
                    "Inspection settings and status routes disagree.");
                if (index == 1 && simulationTab == 0)
                {
                    var summary = document.MachineExecutionSummary;
                    var summaryBottom = summary.TranslatePoint(new System.Windows.Point(0, summary.ActualHeight), document).Y;
                    var sceneTop = document.SceneViewport.TranslatePoint(new System.Windows.Point(), document).Y;
                    Require(summary.IsVisible && summaryBottom <= sceneTop + 1,
                        "The simulation summary is clipped by the shared machine scene.");

                    if (selectedNode is null && selectedItem is null && viewModel.Layout.ActiveUnitId is null
                        && viewModel.Layout.UnitOverviewItems.Count > 0)
                    {
                        var unit = viewModel.Layout.UnitOverviewItems[0];
                        var selector = SmokeVisualTreeQuery.FindVisualDescendant<ComboBox>(document,
                            control => control.Name == "SimulationUnitSelector")
                            ?? throw new InvalidOperationException("Simulation Unit selector was not found.");
                        var wholeMachine = SmokeVisualTreeQuery.FindVisualDescendant<Button>(document,
                            control => control.Name == "SimulationWholeMachineButton")
                            ?? throw new InvalidOperationException("Simulation whole-machine button was not found.");
                        Require(selector.IsVisible && selector.Items.Count == viewModel.Layout.UnitOverviewItems.Count,
                            "Simulation Unit selector does not show the current project units.");
                        Require(wholeMachine.IsVisible,
                            "Simulation whole-machine command is not visible with the Unit selector.");
                        var unitsSnapshot = viewModel.Layout.UnitOverviewItems.ToArray();
                        var dirtyBeforeSelection = viewModel.HasUnsavedChanges;

                        input.ActivateWindow(window);
                        Keyboard.Focus(selector);
                        Require(selector.IsKeyboardFocused && input.CheckPointerOwnership(window).IsOwned,
                            "Simulation Unit selector did not receive keyboard input from the target window.");
                        input.SendKey(0x73);
                        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                        Require(selector.IsDropDownOpen, "F4 did not open the Simulation Unit selector.");
                        input.SendKey(0x24);
                        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                        input.SendKey(0x0D);
                        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                        Require(string.Equals(viewModel.Layout.ActiveUnitId, unit.UnitId, StringComparison.Ordinal),
                            $"Unit command did not set the scene scope: expected={unit.UnitId}, active={viewModel.Layout.ActiveUnitId ?? "<none>"}, " +
                            $"selector={selector.SelectedValue as string ?? "<none>"}, index={selector.SelectedIndex}, dropdown={selector.IsDropDownOpen}, " +
                            $"commandCanExecute={viewModel.ShowEquipmentUnitCommand.CanExecute(selector.SelectedValue)}, " +
                            $"sceneUnits={string.Join(",", viewModel.Layout.SceneItems.Select(item => item.UnitId).Distinct())}.");
                        Require(string.Equals(selector.SelectedValue as string, unit.UnitId, StringComparison.Ordinal),
                            $"Rendered Unit selection disagreed with the command: expected={unit.UnitId}, actual={selector.SelectedValue as string ?? "<none>"}, index={selector.SelectedIndex}.");
                        var sceneUnitIds = viewModel.Layout.SceneItems.Select(item => item.UnitId).Distinct().ToArray();
                        Require(sceneUnitIds.Length == 1 && string.Equals(sceneUnitIds[0], unit.UnitId, StringComparison.Ordinal),
                            $"Unit scene contents disagreed with the selected scope: expected={unit.UnitId}, actual={string.Join(",", sceneUnitIds)}.");
                        capture.Capture(window, Path.Combine(directory, "simulation-unit-selected.png"));

                        Keyboard.Focus(wholeMachine);
                        Require(wholeMachine.IsKeyboardFocused && input.CheckPointerOwnership(window).IsOwned,
                            "Whole-machine command did not receive keyboard input from the target window.");
                        input.SendKey(0x20);
                        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                        Require(viewModel.Layout.ActiveUnitId is null && selector.SelectedValue is null
                            && viewModel.Layout.SceneItems.Count() == viewModel.Layout.Items.Count
                            && viewModel.Layout.UnitOverviewItems.SequenceEqual(unitsSnapshot)
                            && viewModel.HasUnsavedChanges == dirtyBeforeSelection,
                            "Whole-machine restoration changed the project or did not restore the complete scene.");
                        capture.Capture(window, Path.Combine(directory, "simulation-whole-machine.png"));
                        passed.Add("Simulation Unit selector F4/Home/Enter route, project-unit scene scope, whole-machine restore, no project mutation");
                    }
                }
                input.MovePointerToCenter(window.EquipmentWorkspaceButton == button
                    ? window.ResultsWorkspaceButton : window.EquipmentWorkspaceButton);
                await Task.Delay(75);
                Require(!button.IsMouseOver && !button.IsPressed, "Pointer leave did not recover.");
                capture.Capture(window, Path.Combine(directory, $"workspace-{index}-selected.png"));
                passed.Add($"workspace-{index}: hover/pressed/click/checked/leave, content and state preservation");
            }

            viewModel.Navigation.IsEquipmentWorkspace = true;
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Require(buttons[0].IsChecked == true, "VM-to-control selection failed.");
            input.ActivateWindow(window);
            Keyboard.Focus(buttons[0]);
            Require(buttons[0].IsKeyboardFocused, "Workspace keyboard focus missing.");
            Require(input.CheckPointerOwnership(window).IsOwned, "Workspace keyboard input lost window ownership.");
            input.SendKey(0x27);
            await Task.Delay(100);
            Require(buttons[1].IsKeyboardFocused, "Arrow-key workspace focus failed.");
            Require(input.CheckPointerOwnership(window).IsOwned, "Workspace keyboard input lost window ownership.");
            input.SendKey(0x20);
            await Task.Delay(100);
            Require(viewModel.Navigation.IsSimulationWorkspace && buttons[1].IsChecked == true,
                "Space-key workspace selection failed.");
            capture.Capture(window, Path.Combine(directory, "workspace-keyboard.png"));
            passed.Add("VM-to-control binding, Right-arrow focus and Space selection");
            viewModel.Navigation.SelectedInspectionTabIndex = 1;
            viewModel.Navigation.IsResultsWorkspace = true;
            viewModel.Navigation.IsInspectionWorkspace = true;
            Require(viewModel.SelectedDocumentTabIndex == 1 && viewModel.Navigation.SelectedInspectionTabIndex == 1,
                "Inspection subtab was not retained.");
            viewModel.SelectedDocumentTabIndex = 2;
            Require(viewModel.Navigation.IsSimulationWorkspace && viewModel.Navigation.SelectedExecutionTabIndex == 1,
                "Sequence entry did not route through the simulation workspace.");
            passed.Add("Sequence and inspection routes retain their selected subtabs");
            if (sequenceWorkspace)
            {
                viewModel.Navigation.IsSimulationWorkspace = true;
                viewModel.Navigation.SelectedExecutionTabIndex = 0;
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                var tab = document.SequenceWorkspaceTab;
                input.ActivateWindow(window);
                input.MovePointerToCenter(tab);
                await Task.Delay(100);
                Require(input.CheckPointerOwnership(window).IsOwned && tab.IsMouseOver, "Sequence tab hover missing.");
                capture.Capture(window, Path.Combine(directory, "sequence-hover.png"));
                input.PressLeftButton();
                input.MarkPointerHeld();
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                capture.Capture(window, Path.Combine(directory, "sequence-pointer-down.png"));
                input.SendMouseEvent(0x0004, 0, 0, 0, UIntPtr.Zero);
                await Task.Delay(100);
                input.ReleasePointer();
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                Require(tab.IsSelected && viewModel.Navigation.SelectedExecutionTabIndex == 1
                    && SmokeVisualTreeQuery.FindVisualDescendant<SequenceEditorView>(document)?.IsVisible == true,
                    "Sequence tab click or content visibility failed.");
                capture.Capture(window, Path.Combine(directory, "sequence-selected.png"));
                passed.Add("Sequence tab pointer/click selection and editor visibility");
            }
            if (testWorkspace)
            {
                viewModel.Navigation.IsSimulationWorkspace = true;
                viewModel.Navigation.SelectedExecutionTabIndex = 0;
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                var tab = document.TestScenarioWorkspaceTab;
                input.ActivateWindow(window);
                input.MovePointerToCenter(tab);
                await Task.Delay(100);
                Require(input.CheckPointerOwnership(window).IsOwned && tab.IsMouseOver, "Test tab hover missing.");
                capture.Capture(window, Path.Combine(directory, "tests-hover.png"));
                input.PressLeftButton();
                input.MarkPointerHeld();
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                capture.Capture(window, Path.Combine(directory, "tests-pointer-down.png"));
                input.SendMouseEvent(0x0004, 0, 0, 0, UIntPtr.Zero);
                await Task.Delay(100);
                input.ReleasePointer();
                Require(tab.IsSelected && viewModel.Navigation.SelectedExecutionTabIndex == 2, "Test tab click failed.");
                var tests = SmokeVisualTreeQuery.FindVisualDescendant<SimulationTestWorkspaceView>(document)
                    ?? throw new InvalidOperationException("Test workspace was not rendered.");
                Require(viewModel.IsRunning == running && viewModel.TickStatusText == stoppedTick
                    && viewModel.HasUnsavedChanges == dirty, "Opening tests mutated runtime or project.");
                var originalSeed = viewModel.SimulationWorkspace.ScenarioSeed;
                tests.ScenarioSeedTextBox.Focus();
                tests.ScenarioSeedTextBox.SelectAll();
                Require(tests.ScenarioSeedTextBox.IsKeyboardFocused, "Seed input focus missing.");
                Require(input.CheckPointerOwnership(window).IsOwned, "Seed input lost window ownership.");
                input.SendKey(0x39);
                await Task.Delay(100);
                Require(tests.ScenarioSeedTextBox.Text == "9" && viewModel.SimulationWorkspace.ScenarioSeed == 9,
                    "Keyboard seed edit did not reach the settings owner.");
                capture.Capture(window, Path.Combine(directory, "tests-input-focused.png"));
                viewModel.Navigation.IsEquipmentWorkspace = true;
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                Require(document.ExecutionWorkspaceTabs.SelectedIndex == 0, "Equipment workspace retained tests.");
                viewModel.Navigation.IsResultsWorkspace = true;
                viewModel.Navigation.IsSimulationWorkspace = true;
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                Require(document.ExecutionWorkspaceTabs.SelectedIndex == 2 && tests.ScenarioSeedTextBox.Text == "9",
                    "Workspace round trip lost the test tab or draft.");
                viewModel.SimulationWorkspace.ScenarioSeed = originalSeed;
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                Require(tests.ScenarioSeedTextBox.Text == originalSeed.ToString(), "VM-to-control seed binding failed.");
                tab.Focus();
                Require(input.CheckPointerOwnership(window).IsOwned, "Scenario keyboard input lost window ownership.");
                input.SendKey(0x25);
                await Task.Delay(100);
                Require(document.ExecutionWorkspaceTabs.SelectedIndex == 1, "Left arrow did not select sequence editing.");
                Require(input.CheckPointerOwnership(window).IsOwned, "Scenario keyboard input lost window ownership.");
                input.SendKey(0x27);
                await Task.Delay(100);
                Require(document.ExecutionWorkspaceTabs.SelectedIndex == 2, "Right arrow did not restore tests.");
                input.MovePointerToCenter(window.SimulationWorkspaceButton);
                await Task.Delay(100);
                Require(!tab.IsMouseOver && viewModel.IsRunning == running && viewModel.TickStatusText == stoppedTick,
                    "Test navigation did not recover pointer state or changed runtime.");
                capture.Capture(window, Path.Combine(directory, "tests-selected.png"));
                passed.Add("Test tab pointer/keyboard selection, seed binding both directions, draft retention, no implicit run");
            }
            if (integrationWorkspace)
            {
                viewModel.Navigation.IsInspectionWorkspace = true;
                viewModel.Navigation.SelectedInspectionTabIndex = 0;
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                var tab = document.IntegrationWorkspaceTab;
                var integration = SmokeVisualTreeQuery.FindVisualDescendant<SimulationIntegrationWorkspaceView>(document)
                    ?? throw new InvalidOperationException("Integration workspace was not rendered.");
                input.ActivateWindow(window);
                input.MovePointerToCenter(tab);
                await Task.Delay(100);
                Require(input.CheckPointerOwnership(window).IsOwned && tab.IsMouseOver,
                    "Integration tab hover missing.");
                capture.Capture(window, Path.Combine(directory, "integration-hover.png"));
                input.PressLeftButton();
                input.MarkPointerHeld();
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                capture.Capture(window, Path.Combine(directory, "integration-pointer-down.png"));
                input.SendMouseEvent(0x0004, 0, 0, 0, UIntPtr.Zero);
                await Task.Delay(100);
                input.ReleasePointer();
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                Require(tab.IsSelected && integration.IsVisible
                    && viewModel.Navigation.SelectedInspectionTabIndex == 1,
                    "Integration tab click or content visibility failed.");
                Require(viewModel.IsRunning == running && viewModel.TickStatusText == stoppedTick
                    && viewModel.HasUnsavedChanges == dirty,
                    "Opening integration workspace mutated runtime or project.");
                input.MovePointerToCenter(window.InspectionWorkspaceButton);
                await Task.Delay(100);
                Require(!tab.IsMouseOver && !integration.IsMouseOver,
                    "Integration tab pointer leave did not recover.");
                capture.Capture(window, Path.Combine(directory, "integration-selected.png"));
                passed.Add("Integration tab pointer/click selection, status surface visibility, pointer leave, and no implicit run");
            }
        }
        catch (Exception exception)
        {
            failure = exception.ToString();
            throw;
        }
        finally
        {
            input.ReleasePointer();
            window.Topmost = wasTopmost;
            viewModel.Navigation.SelectedWorkspaceIndex = target;
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            File.WriteAllText(reportPath, JsonSerializer.Serialize(new
            {
                IsValid = failure is null, Workspace = workspace, Running = running, RunMode = runMode,
                Monitor = SmokeDpiTestHook.CaptureMonitorEvidence(window), Passed = passed, Failure = failure
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
