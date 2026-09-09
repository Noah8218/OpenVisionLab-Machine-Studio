using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
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
        var normalizedState = connectionWorkbenchState.ToLowerInvariant();
        if (normalizedState is "checkpoint-coverage" or "checkpoint-template-existing")
        {
            var checkpointFixture = CreateExistingCheckpointFixture(
                initialProject
                ?? throw new InvalidOperationException("A project is required for checkpoint coverage smoke."));
            vm.RecipeConnections.Load(checkpointFixture, vm.Layout.SelectedItem?.Id);
        }

        switch (normalizedState)
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
            await SmokeButtonPointerState.FocusAsync(
                window,
                checkpointTemplateButton,
                interaction,
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

            var checkpointTargetButton = connectionWorkbenchState.StartsWith(
                "checkpoint-template-cancel-",
                StringComparison.OrdinalIgnoreCase)
                ? templateCancelButton
                : templateApplyButton;
            await SmokeButtonPointerState.FocusAsync(
                window,
                checkpointTargetButton,
                interaction,
                "Checkpoint template target button did not receive focus.");
            if (connectionWorkbenchState.EndsWith("-focus", StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            if (connectionWorkbenchState.EndsWith("-pressed", StringComparison.OrdinalIgnoreCase))
            {
                await SmokeButtonPointerState.HoverThenPressAsync(
                    window,
                    checkpointTargetButton,
                    interaction,
                    () => "Checkpoint template target button did not enter hover state.",
                    "Checkpoint template target button did not enter pointer-down state.");
            }
            else
            {
                await SmokeButtonPointerState.HoverAsync(
                    checkpointTargetButton,
                    interaction,
                    () => "Checkpoint template target button did not enter hover state.");
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
            await SmokeButtonPointerState.FocusAsync(
                window,
                previewButton,
                interaction,
                "Step preview button did not receive focus.");
            if (connectionWorkbenchState.Equals("preview-pressed", StringComparison.OrdinalIgnoreCase))
            {
                await SmokeButtonPointerState.HoverThenPressAsync(
                    window,
                    previewButton,
                    interaction,
                    () => "Step preview button did not enter hover state.",
                    "Step preview button did not enter pointer-down state.");
            }
            else
            {
                await SmokeButtonPointerState.HoverAsync(
                    previewButton,
                    interaction,
                    () => "Step preview button did not enter hover state.");
            }
            break;
        case "add-step":
            AssertSmoke(
                vm.TryAddLayoutComponent(LayoutComponentKind.LinearStage),
                "A stage could not be added for target-step evidence.");
            var targetComponentId = vm.Layout.SelectedItem?.Id;
            var addStepFixture = CreateStrictLinearSequenceFixture(
                initialProject
                ?? throw new InvalidOperationException("A project is required for target-step smoke."));
            vm.RecipeConnections.Load(addStepFixture, targetComponentId);
            var targetRow = vm.RecipeConnections.Rows.FirstOrDefault(row =>
                row.ComponentId == targetComponentId);
            AssertSmoke(
                targetRow is { IsValid: true, CanAddSequenceStep: true },
                "The added stage did not expose a valid unused target-step model state.");
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

    private static MachineProjectDocument CreateExistingCheckpointFixture(MachineProjectDocument source)
    {
        var store = new ProjectDocumentStore();
        var fixture = store.Load(store.Serialize(source));
        var sequenceId = fixture.Simulation.AutomaticRun?.SequenceId
            ?? fixture.Sequences.FirstOrDefault()?.Id
            ?? throw new InvalidOperationException("A sequence was required for checkpoint coverage smoke.");
        var sequence = fixture.Sequences.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, sequenceId, StringComparison.Ordinal))
            ?? throw new InvalidOperationException("The checkpoint coverage sequence was not available.");
        var fixtureStepIds = new[]
        {
            "cycle-active-on",
            "extend-stopper",
            "wait-stopper-extended",
            "conveyor-forward",
            "conveyor-run-forward",
            "move-station",
            "wait-station-position",
            "wait-station-sensor",
            "conveyor-stop-at-station",
            "retract-stopper",
            "wait-stopper-retracted",
            "complete"
        };
        var steps = fixtureStepIds.Select(stepId => sequence.Steps.FirstOrDefault(step =>
                string.Equals(step.Id, stepId, StringComparison.Ordinal))
            ?? throw new InvalidOperationException(
                $"The checkpoint coverage fixture step '{stepId}' was not available.")).ToList();
        for (var index = 0; index < steps.Count; index++)
        {
            var step = steps[index];
            step.NextStepId = index + 1 < steps.Count ? steps[index + 1].Id : null;
            step.ErrorStepId = null;
            step.FailureStepId = null;
            step.ExpectedTargetId = null;
            step.ExpectedState = null;
        }

        SetCheckpoint(steps, "wait-stopper-extended", "cylinder-1", "Extended");
        SetCheckpoint(steps, "wait-station-sensor", "sensor-1", "Detected");
        SetCheckpoint(steps, "wait-station-position", "x", "Idle");
        SetCheckpoint(steps, "conveyor-stop-at-station", "conveyor-1", "Stopped");
        SetCheckpoint(steps, "wait-stopper-retracted", "cylinder-1", "Retracted");
        sequence.Steps = steps;
        return fixture;
    }

    private static MachineProjectDocument CreateStrictLinearSequenceFixture(MachineProjectDocument source)
    {
        var store = new ProjectDocumentStore();
        var fixture = store.Load(store.Serialize(source));
        var sequenceId = fixture.Simulation.AutomaticRun?.SequenceId
            ?? fixture.Sequences.FirstOrDefault()?.Id
            ?? throw new InvalidOperationException("A sequence was required for target-step smoke.");
        var sequence = fixture.Sequences.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, sequenceId, StringComparison.Ordinal))
            ?? throw new InvalidOperationException("The target-step sequence was not available.");
        var steps = sequence.Steps.ToList();
        for (var index = 0; index < steps.Count; index++)
        {
            steps[index].NextStepId = index + 1 < steps.Count ? steps[index + 1].Id : null;
            steps[index].ErrorStepId = null;
            steps[index].FailureStepId = null;
        }

        sequence.Steps = steps;
        return fixture;
    }

    private static void SetCheckpoint(
        IReadOnlyList<SequenceStepDefinition> steps,
        string stepId,
        string targetId,
        string state)
    {
        var step = steps.First(candidate => string.Equals(candidate.Id, stepId, StringComparison.Ordinal));
        step.ExpectedTargetId = targetId;
        step.ExpectedState = state;
    }

    private static void AssertSmoke(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
