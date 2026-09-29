using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using OpenVisionLab;
using OpenVisionLab.Machine.Core.Layouts;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Core.Sequences;
using OpenVisionLab.MachineStudio.View.Project;
using OpenVisionLab.MachineStudio.View.Shell;
using OpenVisionLab.MachineStudio.ViewModel;
using static OpenVisionLab.MachineStudio.SmokeVisualTreeQuery;

namespace OpenVisionLab.MachineStudio;

internal static class SmokeRecipeConnectionStateVerifier
{
    public static bool IsSupportedState(string? state) => state?.ToLowerInvariant() is
        "normal"
        or "focus"
        or "hover"
        or "pressed"
        or "disabled"
        or "validation"
        or "readiness";

    public static async Task ApplyAsync(
        ShellWindow window,
        MainViewModel vm,
        RecipeConnectionWorkbenchView workbench,
        Button addStageButton,
        Button addRotaryStageButton,
        Button readinessButton,
        Button dryRunButton,
        Button stationSkeletonButton,
        Button processBlockButton,
        Button loadLockSetupButton,
        Button checkpointTemplateButton,
        string connectionWorkbenchState,
        SmokeUiInteraction interaction)
    {
        switch (connectionWorkbenchState.ToLowerInvariant())
        {
        case "normal":
            AssertSmoke(addStageButton.IsEnabled, "Axis + stage button was unexpectedly disabled.");
            AssertSmoke(addRotaryStageButton.IsEnabled, "Rotary axis + stage button was unexpectedly disabled.");
            AssertSmoke(readinessButton.IsEnabled, "Simulation readiness button was unexpectedly disabled.");
            AssertSmoke(stationSkeletonButton.IsEnabled, "Semiconductor station button was unexpectedly disabled.");
            AssertSmoke(processBlockButton.IsEnabled, "Process block composer button was unexpectedly disabled.");
            AssertSmoke(loadLockSetupButton.IsEnabled, "Load-lock setup button was unexpectedly disabled.");
            AssertSmoke(checkpointTemplateButton.IsEnabled, "Checkpoint template button was unexpectedly disabled.");
            AssertSmoke(!dryRunButton.IsEnabled, "Recipe dry run was enabled before readiness passed.");
            AssertSmoke(
                interaction.FindButton(workbench, candidate =>
                    string.Equals(candidate.Name, "OpenConnectionSequenceStepButton", StringComparison.Ordinal)
                    && candidate.IsVisible) is not null,
                "No visible linked Sequence step action was available.");
            break;
        case "focus":
            await SmokeButtonPointerState.FocusAsync(
                window,
                addRotaryStageButton,
                interaction,
                "Rotary axis + stage button did not receive focus.");
            break;
        case "hover":
        case "pressed":
            window.Topmost = true;
            await SmokeButtonPointerState.FocusAsync(
                window,
                addRotaryStageButton,
                interaction,
                "Rotary axis + stage button did not receive focus.");
            Func<string> hoverFailureMessage = () =>
            {
                var cursorPosition = interaction.GetCursorPosition();
                var cursorInButton = addRotaryStageButton.PointFromScreen(
                    new Point(cursorPosition.X, cursorPosition.Y));
                return $"Rotary axis + stage button did not enter hover state. " +
                    $"Cursor=({cursorPosition.X},{cursorPosition.Y}), " +
                    $"button=({cursorInButton.X:F1},{cursorInButton.Y:F1})/" +
                    $"{addRotaryStageButton.ActualWidth:F1}x{addRotaryStageButton.ActualHeight:F1}, " +
                    $"direct={Mouse.DirectlyOver?.GetType().Name ?? "null"}.";
            };
            if (connectionWorkbenchState.Equals("pressed", StringComparison.OrdinalIgnoreCase))
            {
                await SmokeButtonPointerState.HoverThenPressAsync(
                    window,
                    addRotaryStageButton,
                    interaction,
                    hoverFailureMessage,
                    "Rotary axis + stage button did not enter pointer-down state.");
            }
            else
            {
                await SmokeButtonPointerState.HoverAsync(
                    addRotaryStageButton,
                    interaction,
                    hoverFailureMessage);
            }
            break;
        case "disabled":
            AssertSmoke(!addStageButton.IsEnabled, "Axis + stage button remained enabled in Run mode.");
            AssertSmoke(!addRotaryStageButton.IsEnabled, "Rotary axis + stage button remained enabled in Run mode.");
            AssertSmoke(!readinessButton.IsEnabled, "Simulation readiness button remained enabled in Run mode.");
            AssertSmoke(!stationSkeletonButton.IsEnabled, "Semiconductor station button remained enabled in Run mode.");
            AssertSmoke(!processBlockButton.IsEnabled, "Process block composer button remained enabled in Run mode.");
            AssertSmoke(!dryRunButton.IsEnabled, "Recipe dry run remained enabled in Run mode.");
            AssertSmoke(!checkpointTemplateButton.IsEnabled, "Checkpoint template button remained enabled in Run mode.");
            break;
        case "validation":
            var project = vm.ProjectTree.Roots.Single().Model as MachineProjectDocument
                ?? throw new InvalidOperationException("The current machine project was unavailable.");
            var expectedRow = vm.RecipeConnections.Rows.FirstOrDefault(row => row.HasSequenceUse)
                ?? throw new InvalidOperationException("The smoke project did not contain a sequence-linked component.");
            var component = project.Layouts
                .SelectMany(layout => layout.Components)
                .FirstOrDefault(candidate => string.Equals(candidate.Id, expectedRow.ComponentId, StringComparison.Ordinal))
                ?? throw new InvalidOperationException("The smoke project did not contain a behavior-bound component.");
            var expectedSequenceId = expectedRow.FirstSequenceId;
            var expectedStepId = expectedRow.FirstSequenceStepId;
            component.Size.Width = 0;
            vm.RecipeConnections.Load(project, component.Id);
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var issueList = FindVisualDescendant<ListBox>(workbench, candidate =>
                    string.Equals(candidate.Name, "RecipeConnectionValidationIssuesListBox", StringComparison.Ordinal))
                ?? throw new InvalidOperationException("The typed connection validation issue list was unavailable.");
            AssertSmoke(issueList.Items.Count > 0, "Invalid connection input produced no validation issue.");
            issueList.BringIntoView();
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            AssertSmoke(issueList.IsVisible, "The connection validation issue list was not visible in the authoring surface.");
            issueList.SelectedIndex = 0;
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var selectedIssue = vm.RecipeConnections.SelectedValidationIssue;
            AssertSmoke(
                selectedIssue is { ComponentId: { } issueComponentId }
                && string.Equals(issueComponentId, component.Id, StringComparison.Ordinal)
                && string.Equals(vm.RecipeConnections.SelectedRow?.ComponentId, component.Id, StringComparison.Ordinal)
                && selectedIssue.TargetKind == RecipeConnectionValidationTargetKind.SequenceStep
                && string.Equals(selectedIssue.SequenceId, expectedSequenceId, StringComparison.Ordinal)
                && string.Equals(selectedIssue.StepId, expectedStepId, StringComparison.Ordinal)
                && string.Equals(vm.SequenceEditor.SelectedSequence?.Id, expectedSequenceId, StringComparison.Ordinal)
                && string.Equals(vm.SequenceEditor.SelectedStep?.Id, expectedStepId, StringComparison.Ordinal)
                && vm.SelectedDocumentTabIndex == 2
                && !vm.IsRunning,
                $"Selecting a connection validation issue did not navigate to the linked component and Sequence step safely. "
                + $"component={component.Id}, expectedSequence={expectedSequenceId ?? "null"}, expectedStep={expectedStepId ?? "null"}, "
                + $"selectedIssue={selectedIssue?.TargetKind}/{selectedIssue?.ComponentId}/{selectedIssue?.SequenceId}/{selectedIssue?.StepId}, "
                + $"selectedRow={vm.RecipeConnections.SelectedRow?.ComponentId ?? "null"}, "
                + $"selectedSequence={vm.SequenceEditor.SelectedSequence?.Id ?? "null"}, selectedStep={vm.SequenceEditor.SelectedStep?.Id ?? "null"}, "
                + $"tab={vm.SelectedDocumentTabIndex}, running={vm.IsRunning}.");
            break;
        case "readiness":
            vm.RecipeConnections.ValidateSimulationReadinessCommand.Execute(null);
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            AssertSmoke(
                vm.RecipeConnections.ReadinessPassed == true && !vm.IsRunning,
                "Simulation readiness did not pass safely without starting simulation.");
            var readinessComparison = FindVisualDescendant<TextBlock>(workbench, candidate =>
                    string.Equals(candidate.Name, "RecipeReadinessComparisonText", StringComparison.Ordinal))
                ?? throw new InvalidOperationException("The readiness comparison text was unavailable.");
            AssertSmoke(
                !string.IsNullOrWhiteSpace(readinessComparison.Text),
                "Simulation readiness did not publish a comparison result.");
            readinessComparison.BringIntoView();
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            AssertSmoke(readinessComparison.IsVisible, "The readiness comparison result was not visible in the authoring surface.");
            break;
        default:
            throw new ArgumentException(
                $"Unsupported --smoke-connection-workbench-state '{connectionWorkbenchState}'. " +
                "Expected a supported connection-workbench smoke state, including dry-run, dry-run-playback, or dry-run-wafer-handler-fault-playback.");
        }
    }

    private static void AssertSmoke(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
