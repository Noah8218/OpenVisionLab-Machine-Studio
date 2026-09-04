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
using OpenVisionLab.Machine.Simulation.Sequences;
using OpenVisionLab.MachineStudio.View.Project;
using OpenVisionLab.MachineStudio.View.Shell;
using OpenVisionLab.MachineStudio.ViewModel;

namespace OpenVisionLab.MachineStudio;

internal static class SmokeRecipeCheckpointStateVerifier
{
    private const uint MouseEventMove = 0x0001;
    private const uint MouseEventLeftDown = 0x0002;

    public static bool IsSupportedState(string? state) => state?.ToLowerInvariant() is
        "checkpoint-coverage"
        or "checkpoint-template-focus"
        or "checkpoint-template-existing"
        or "checkpoint-template-preview"
        or "checkpoint-template-apply-focus"
        or "checkpoint-template-apply-hover"
        or "checkpoint-template-apply-pressed"
        or "checkpoint-template-cancel-focus"
        or "checkpoint-template-cancel-hover"
        or "checkpoint-template-cancel-pressed"
        or "checkpoint-template-applied"
        or "preview"
        or "preview-hover"
        or "preview-pressed"
        or "add-step"
        or "validation";

    public static async Task ApplyAsync(
        ShellWindow window,
        MainViewModel vm,
        MachineProjectDocument? initialProject,
        RecipeConnectionWorkbenchView workbench,
        Button addStageButton,
        Button checkpointTemplateButton,
        string connectionWorkbenchState,
        string? connectionWorkbenchSavePath,
        SmokeUiInteraction interaction,
        Func<DependencyObject, Func<Border, bool>, Border?> findBorder,
        Func<DependencyObject, Func<ListBox, bool>, ListBox?> findListBox)
    {
        void ClearInitialRecipeCheckpoints()
        {
            var project = initialProject
                ?? throw new InvalidOperationException("A project is required for checkpoint template smoke.");
            foreach (var step in project.Sequences.SelectMany(sequence => sequence.Steps))
            {
                step.ExpectedTargetId = null;
                step.ExpectedState = null;
            }
            vm.RecipeConnections.Load(project, vm.Layout.SelectedItem?.Id);
        }
        switch (connectionWorkbenchState.ToLowerInvariant())
        {
        case "checkpoint-coverage":
            var checkpointCoverageText = interaction.FindTextBlock(
                workbench,
                candidate => string.Equals(
                    candidate.Name,
                    "RecipeCheckpointCoverageText",
                    StringComparison.Ordinal))
                ?? throw new InvalidOperationException("Recipe checkpoint coverage was not available.");
            AssertSmoke(
                vm.RecipeConnections.CheckpointStepCount == 5
                && vm.RecipeConnections.RecipeStepCount == 12,
                "Recipe checkpoint coverage did not report 5 of 12 steps.");
            AssertSmoke(
                checkpointCoverageText.IsVisible
                && checkpointCoverageText.Text.Contains("5", StringComparison.Ordinal)
                && checkpointCoverageText.Text.Contains("12", StringComparison.Ordinal),
                "Recipe checkpoint coverage was not visible before dry run.");
            AssertSmoke(
                !vm.RecipeConnections.HasRecipeDryRunResult && !vm.IsRunning,
                "Checkpoint coverage display caused an unintended run.");
            break;
        case "checkpoint-template-focus":
            interaction.ActivateWindow();
            checkpointTemplateButton.Focus();
            Keyboard.Focus(checkpointTemplateButton);
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            AssertSmoke(
                checkpointTemplateButton.IsKeyboardFocused,
                "Checkpoint template button did not receive focus.");
            break;
        case "checkpoint-template-existing":
            vm.RecipeConnections.CheckpointTemplate.PreviewCommand.Execute(null);
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var existingTemplateApplyButton = interaction.FindButton(
                workbench,
                candidate => string.Equals(
                    candidate.Name,
                    "ApplyRecipeCheckpointTemplateButton",
                    StringComparison.Ordinal))
                ?? throw new InvalidOperationException("Checkpoint template Apply button was not available.");
            AssertSmoke(
                vm.RecipeConnections.CheckpointTemplate.IsPreviewVisible
                && vm.RecipeConnections.CheckpointTemplate.ProposedCount == 0
                && vm.RecipeConnections.CheckpointTemplate.Items.Count(item =>
                    item.IsAlreadyConfigured) == 5,
                "Existing representative checkpoints were not recognized.");
            AssertSmoke(
                !existingTemplateApplyButton.IsEnabled,
                "Checkpoint template Apply button was enabled without additions.");
            break;
        case "checkpoint-template-preview":
        case "checkpoint-template-apply-focus":
        case "checkpoint-template-apply-hover":
        case "checkpoint-template-apply-pressed":
        case "checkpoint-template-cancel-focus":
        case "checkpoint-template-cancel-hover":
        case "checkpoint-template-cancel-pressed":
        case "checkpoint-template-applied":
            ClearInitialRecipeCheckpoints();
            var templateProject = initialProject!;
            var templateStore = new ProjectDocumentStore();
            var templateRecipeStepCountBefore = vm.RecipeConnections.RecipeStepCount;
            var templateBeforePreview = templateStore.Serialize(templateProject);
            var templateRuntimeBefore = vm.SceneSnapshots.Latest;
            vm.RecipeConnections.CheckpointTemplate.PreviewCommand.Execute(null);
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var templatePreviewPanel = findBorder(
                workbench,
                candidate => string.Equals(
                    candidate.Name,
                    "RecipeCheckpointTemplatePreview",
                    StringComparison.Ordinal))
                ?? throw new InvalidOperationException("Checkpoint template preview panel was not available.");
            var templateApplyButton = interaction.FindButton(
                workbench,
                candidate => string.Equals(
                    candidate.Name,
                    "ApplyRecipeCheckpointTemplateButton",
                    StringComparison.Ordinal))
                ?? throw new InvalidOperationException("Checkpoint template Apply button was not available.");
            var templateCancelButton = interaction.FindButton(
                workbench,
                candidate => string.Equals(
                    candidate.Name,
                    "CancelRecipeCheckpointTemplateButton",
                    StringComparison.Ordinal))
                ?? throw new InvalidOperationException("Checkpoint template Cancel button was not available.");
            AssertSmoke(
                templatePreviewPanel.IsVisible
                && vm.RecipeConnections.CheckpointTemplate.ProposedCount == 5
                && vm.RecipeConnections.CheckpointTemplate.Items.Count == 5
                && vm.RecipeConnections.CheckpointTemplate.Items.All(item => item.IsProposed),
                "Five representative checkpoint additions were not previewed.");
            AssertSmoke(
                templateApplyButton.IsEnabled
                && templateBeforePreview == templateStore.Serialize(templateProject)
                && !vm.IsRunning,
                "Checkpoint preview changed the recipe or runtime before Apply.");
            if (connectionWorkbenchState.Equals(
                    "checkpoint-template-applied",
                    StringComparison.OrdinalIgnoreCase))
            {
                vm.RecipeConnections.CheckpointTemplate.ApplyCommand.Execute(null);
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                AssertSmoke(
                    !vm.RecipeConnections.CheckpointTemplate.IsPreviewVisible
                    && vm.RecipeConnections.CheckpointStepCount == 5
                    && vm.RecipeConnections.RecipeStepCount == templateRecipeStepCountBefore,
                    "Checkpoint template did not preserve the authored sequence while applying five checks.");
                AssertSmoke(
                    templateProject.Sequences.SelectMany(sequence => sequence.Steps).Count(step =>
                        !string.IsNullOrWhiteSpace(step.ExpectedTargetId)
                        && !string.IsNullOrWhiteSpace(step.ExpectedState)) == 5
                    && templateBeforePreview != templateStore.Serialize(templateProject),
                    "Checkpoint template did not update the authored recipe.");
                AssertSmoke(
                    templateRuntimeBefore?.TickIndex == vm.SceneSnapshots.Latest?.TickIndex
                    && templateRuntimeBefore?.SimulationTime == vm.SceneSnapshots.Latest?.SimulationTime
                    && !vm.IsRunning
                    && vm.IsDesignMode
                    && vm.RecipeConnections.ReadinessPassed is null,
                    "Checkpoint template Apply caused an unintended runtime action.");
                if (!string.IsNullOrWhiteSpace(connectionWorkbenchSavePath))
                {
                    var templateSavePath = Path.GetFullPath(connectionWorkbenchSavePath);
                    Directory.CreateDirectory(Path.GetDirectoryName(templateSavePath)!);
                    await vm.SaveProjectAsync(templateSavePath);
                    AssertSmoke(
                        await vm.OpenProjectAsync(templateSavePath),
                        "Checkpoint template project did not reopen.");
                    AssertSmoke(
                        vm.RecipeConnections.CheckpointStepCount == 5
                        && vm.RecipeConnections.RecipeStepCount == templateRecipeStepCountBefore
                        && !vm.IsRunning
                        && vm.IsDesignMode,
                        "Reopened project did not retain the applied checkpoints safely.");
                }
                break;
            }
            if (connectionWorkbenchState.Equals(
                    "checkpoint-template-preview",
                    StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            interaction.ActivateWindow();
            for (var attempt = 0; attempt < 10 && !window.IsActive; attempt++)
            {
                await Task.Delay(50);
                interaction.ActivateWindow();
            }
            AssertSmoke(window.IsActive, "Machine Studio did not become active for checkpoint Apply pointer testing.");
            var checkpointTargetButton = connectionWorkbenchState.StartsWith(
                    "checkpoint-template-cancel-",
                    StringComparison.OrdinalIgnoreCase)
                ? templateCancelButton
                : templateApplyButton;
            checkpointTargetButton.BringIntoView();
            checkpointTargetButton.UpdateLayout();
            checkpointTargetButton.Focus();
            Keyboard.Focus(checkpointTargetButton);
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            AssertSmoke(
                checkpointTargetButton.IsKeyboardFocused,
                "Checkpoint template target button did not receive focus.");
            if (connectionWorkbenchState.EndsWith("-focus", StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            interaction.MovePointerToCenter(checkpointTargetButton);
            Mouse.Capture(checkpointTargetButton, CaptureMode.SubTree);
            Mouse.Synchronize();
            await Task.Delay(200);
            AssertSmoke(
                checkpointTargetButton.IsMouseOver,
                "Checkpoint template target button did not enter hover state.");
            if (connectionWorkbenchState.EndsWith("-pressed", StringComparison.OrdinalIgnoreCase))
            {
                interaction.MouseEvent(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
                interaction.MarkSmokePointerHeld();
                checkpointTargetButton.RaiseEvent(new MouseButtonEventArgs(
                    Mouse.PrimaryDevice,
                    Environment.TickCount,
                    MouseButton.Left)
                {
                    RoutedEvent = Mouse.MouseDownEvent
                });
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                AssertSmoke(
                    checkpointTargetButton.IsPressed,
                    "Checkpoint template target button did not enter pointer-down state.");
            }
            break;
        case "preview":
            var previewRow = vm.RecipeConnections.Rows.FirstOrDefault(row =>
                row.Kind == LayoutComponentKind.PneumaticCylinder
                && row.CanPreviewSequenceStep)
                ?? throw new InvalidOperationException("No previewable cylinder row was available.");
            AssertSmoke(
                !vm.RecipeConnections.PreviewSequenceStepCommand.CanExecute(previewRow),
                "Step preview was enabled before readiness passed.");
            vm.RecipeConnections.ValidateSimulationReadinessCommand.Execute(null);
            AssertSmoke(
                vm.RecipeConnections.PreviewSequenceStepCommand.CanExecute(previewRow),
                "Step preview was not enabled after readiness passed.");
            vm.RecipeConnections.PreviewSequenceStepCommand.Execute(previewRow);
            for (var attempt = 0; attempt < 100 && !previewRow.HasPreviewResult; attempt++)
            {
                await Task.Delay(20);
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            }
            AssertSmoke(
                previewRow.PreviewResult?.Outcome == SequenceStepPreviewOutcome.Completed,
                "The isolated cylinder step preview did not complete.");
            var previewRows = findListBox(workbench, candidate =>
                string.Equals(candidate.Name, "ConnectionRowsListBox", StringComparison.Ordinal))
                ?? throw new InvalidOperationException("Connection rows were not available.");
            previewRows.ScrollIntoView(previewRow);
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            AssertSmoke(
                interaction.FindButton(workbench, candidate =>
                    string.Equals(candidate.Name, "PreviewConnectionSequenceStepButton", StringComparison.Ordinal)
                    && candidate.IsVisible
                    && candidate.IsEnabled) is not null,
                "The preview step action was not visible and enabled.");
            break;
        case "preview-hover":
        case "preview-pressed":
            vm.RecipeConnections.ValidateSimulationReadinessCommand.Execute(null);
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var pressedPreviewRow = vm.RecipeConnections.Rows.First(row =>
                row.Kind == LayoutComponentKind.LinearStage
                && row.CanPreviewSequenceStep);
            var pressedPreviewRows = findListBox(workbench, candidate =>
                string.Equals(candidate.Name, "ConnectionRowsListBox", StringComparison.Ordinal))
                ?? throw new InvalidOperationException("Connection rows were not available.");
            pressedPreviewRows.ScrollIntoView(pressedPreviewRow);
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var previewButton = interaction.FindButton(workbench, candidate =>
                string.Equals(candidate.Name, "PreviewConnectionSequenceStepButton", StringComparison.Ordinal)
                && ReferenceEquals(candidate.DataContext, pressedPreviewRow)
                && candidate.IsVisible
                && candidate.IsEnabled)
                ?? throw new InvalidOperationException("No enabled step preview button was visible.");
            interaction.ActivateWindow();
            previewButton.BringIntoView();
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            previewButton.UpdateLayout();
            previewButton.Focus();
            Keyboard.Focus(previewButton);
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            AssertSmoke(previewButton.IsKeyboardFocused, "Step preview button did not receive focus.");
            interaction.MovePointerToCenter(previewButton);
            await Task.Delay(200);
            AssertSmoke(previewButton.IsMouseOver, "Step preview button did not enter hover state.");
            if (connectionWorkbenchState.Equals("preview-pressed", StringComparison.OrdinalIgnoreCase))
            {
                interaction.MouseEvent(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
                interaction.MarkSmokePointerHeld();
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                AssertSmoke(previewButton.IsPressed, "Step preview button did not enter pointer-down state.");
            }
            break;
        case "add-step":
            AssertSmoke(
                vm.TryAddLayoutComponent(LayoutComponentKind.LinearStage),
                "A stage could not be added for target-step evidence.");
            var targetRow = vm.RecipeConnections.Rows.First(row =>
                row.ComponentId == vm.Layout.SelectedItem?.Id);
            vm.RecipeConnections.SelectedRow = targetRow;
            var rows = findListBox(workbench, candidate =>
                string.Equals(candidate.Name, "ConnectionRowsListBox", StringComparison.Ordinal))
                ?? throw new InvalidOperationException("Connection rows were not available.");
            rows.ScrollIntoView(targetRow);
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            AssertSmoke(
                interaction.FindButton(workbench, candidate =>
                    string.Equals(candidate.Name, "AddConnectionSequenceStepButton", StringComparison.Ordinal)
                    && candidate.IsVisible
                    && candidate.IsEnabled) is not null,
                "The unused connection did not expose an enabled target-step action.");
            break;
        case "validation":
            var stage = vm.Layout.Items.FirstOrDefault(item =>
                item.Component?.Kind == LayoutComponentKind.LinearStage)
                ?? throw new InvalidOperationException("No stage was available for validation evidence.");
            vm.Layout.Select(stage.Id);
            var editor = vm.Layout.SelectedComponentEditor
                ?? throw new InvalidOperationException("Stage binding editor was not available.");
            editor.BehaviorBindingId = "missing-smoke-axis";
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            AssertSmoke(
                vm.RecipeConnections.HasValidationErrors
                && vm.RecipeConnections.Rows.Any(row =>
                    row.ComponentId == stage.Id && !row.IsValid),
                "Invalid stage binding did not appear in the connection workbench.");
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
