using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.MachineStudio.View.Project;
using OpenVisionLab.MachineStudio.View.Shell;
using OpenVisionLab.MachineStudio.ViewModel;
using static OpenVisionLab.MachineStudio.SmokeVisualTreeQuery;

namespace OpenVisionLab.MachineStudio;

internal sealed class DirectExeConnectionWorkbenchResult
{
    public SmokeLoadLockSetupReport? LoadLockSetupReport { get; init; }
    public SmokeStationSkeletonReport? StationSkeletonReport { get; init; }
    public SmokeWorkflowReport? WorkflowReport { get; init; }
}

internal static class DirectExeConnectionWorkbenchWorkflow
{
    public static async Task<SmokeConnectionWorkbenchReport?> VerifyDefaultAsync(
        ShellWindow window,
        MainViewModel vm,
        MachineProjectDocument? initialProject,
        string? connectionWorkbenchState,
        string? connectionWorkbenchReportPath,
        string? connectionWorkbenchSavePath)
    {
        if (string.IsNullOrWhiteSpace(connectionWorkbenchReportPath)
            || IsDedicatedState(connectionWorkbenchState))
        {
            return null;
        }

        var report = await SmokeConnectionWorkbenchVerifier.VerifyAsync(
            window,
            vm,
            initialProject!,
            connectionWorkbenchSavePath!,
            (root, predicate) => FindVisualDescendant<TextBox>(root, predicate));
        report.Save(connectionWorkbenchReportPath);
        Console.WriteLine($"Connection workbench smoke {(report.IsValid ? "passed" : "failed")}.");
        return report;
    }

    public static async Task<DirectExeConnectionWorkbenchResult> ApplyAsync(
        ShellWindow window,
        MainViewModel vm,
        MachineProjectDocument? initialProject,
        string? projectPath,
        string connectionWorkbenchState,
        string? connectionWorkbenchReportPath,
        string? connectionWorkbenchSavePath,
        SmokeUiInteraction interaction)
    {
        if (SmokeStationSkeletonVerifier.RequiresProjectPreparation(connectionWorkbenchState))
        {
            await SmokeStationSkeletonVerifier.PrepareProjectAsync(window, vm, initialProject);
        }

        vm.SelectedDocumentTabIndex = 1;
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        var workbench = FindVisualDescendant<RecipeConnectionWorkbenchView>(window)
            ?? throw new InvalidOperationException("Connection workbench was not available.");
        var controls = FindControls(workbench);

        SmokeLoadLockSetupReport? loadLockSetupReport = null;
        SmokeStationSkeletonReport? stationSkeletonReport = null;
        SmokeWorkflowReport? workflowReport = null;

        if (SmokeStationSkeletonVerifier.IsSupportedState(connectionWorkbenchState))
        {
            var stationResult = await SmokeStationSkeletonVerifier.VerifyAsync(
                window,
                vm,
                connectionWorkbenchState,
                initialProject,
                workbench,
                controls.StationSkeletonButton,
                connectionWorkbenchSavePath,
                (root, predicate) => FindVisualDescendant<Border>(root, predicate),
                (root, predicate) => FindVisualDescendant<Button>(root, predicate),
                (root, predicate) => FindVisualDescendant<TextBox>(root, predicate),
                interaction.ActivateWindow,
                interaction.MovePointerToCenter,
                interaction.MouseEvent,
                interaction.MarkSmokePointerHeld);
            if (connectionWorkbenchState.Equals("station-skeleton-applied", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(connectionWorkbenchReportPath))
            {
                stationSkeletonReport = stationResult;
                stationSkeletonReport.Save(connectionWorkbenchReportPath);
            }

            var stationReportRequested = connectionWorkbenchState.Equals(
                "station-skeleton-applied",
                StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(connectionWorkbenchReportPath);
            if (!stationResult.IsValid && !stationReportRequested)
            {
                throw new InvalidOperationException(
                    stationResult.Failures.FirstOrDefault()
                    ?? "Station skeleton smoke failed.");
            }
        }
        else if (connectionWorkbenchState.StartsWith("load-lock-", StringComparison.OrdinalIgnoreCase))
        {
            var loadLockResult = await SmokeLoadLockSetupVerifier.VerifyAsync(
                window,
                vm,
                connectionWorkbenchState,
                initialProject!,
                workbench,
                controls.LoadLockSetupButton,
                connectionWorkbenchSavePath,
                (root, predicate) => FindVisualDescendant<Border>(root, predicate),
                (root, predicate) => FindVisualDescendant<Button>(root, predicate),
                (root, predicate) => FindVisualDescendant<TextBox>(root, predicate),
                (root, predicate) => FindVisualDescendant<ComboBox>(root, predicate),
                interaction.ActivateWindow,
                interaction.MovePointerToCenter,
                interaction.MouseEvent,
                interaction.MarkSmokePointerHeld,
                interaction.SetPopupContent);
            if (connectionWorkbenchState.Equals("load-lock-applied", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(connectionWorkbenchReportPath))
            {
                loadLockSetupReport = loadLockResult;
                loadLockSetupReport.Save(connectionWorkbenchReportPath);
            }

            var loadLockReportRequested = connectionWorkbenchState.Equals(
                "load-lock-applied",
                StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(connectionWorkbenchReportPath);
            if (!loadLockResult.IsValid && !loadLockReportRequested)
            {
                throw new InvalidOperationException(
                    loadLockResult.Failures.FirstOrDefault()
                    ?? "Load-lock setup smoke failed.");
            }
        }
        else if (SmokeSemanticSetupVerifier.IsSupportedState(connectionWorkbenchState))
        {
            await SmokeSemanticSetupVerifier.VerifyAsync(
                window,
                vm,
                connectionWorkbenchState,
                initialProject,
                workbench,
                (root, predicate) => FindVisualDescendant<FrameworkElement>(root, predicate),
                (root, predicate) => FindVisualDescendant<Button>(root, predicate),
                connectionWorkbenchSavePath);
        }
        else if (SmokeProcessBlockProposalVerifier.IsSupportedState(connectionWorkbenchState))
        {
            await SmokeProcessBlockProposalVerifier.VerifyAsync(
                window,
                vm,
                connectionWorkbenchState,
                initialProject,
                workbench,
                controls.ProcessBlockButton,
                (root, predicate) => FindVisualDescendant<Border>(root, predicate),
                (root, predicate) => FindVisualDescendant<Button>(root, predicate),
                (root, predicate) => FindVisualDescendant<CheckBox>(root, predicate),
                interaction.ActivateWindow,
                interaction.MovePointerToCenter,
                interaction.MouseEvent,
                interaction.MarkSmokePointerHeld);
        }
        else if (SmokeProcessBlockApplicationVerifier.IsSupportedState(connectionWorkbenchState))
        {
            var applicationContext = await SmokeProcessBlockPreparation.PrepareAsync(
                window,
                vm,
                initialProject,
                workbench,
                (root, predicate) => FindVisualDescendant<Border>(root, predicate),
                (root, predicate) => FindVisualDescendant<Button>(root, predicate),
                (root, predicate) => FindVisualDescendant<CheckBox>(root, predicate));
            var applicationResult = await SmokeProcessBlockApplicationVerifier.VerifyAsync(
                window,
                vm,
                connectionWorkbenchState,
                applicationContext,
                connectionWorkbenchSavePath,
                !string.IsNullOrWhiteSpace(connectionWorkbenchReportPath),
                interaction);
            if (applicationResult.Report is not null
                && !string.IsNullOrWhiteSpace(connectionWorkbenchReportPath))
            {
                workflowReport = applicationResult.Report;
                workflowReport.Save(connectionWorkbenchReportPath);
            }
        }
        else if (SmokeProcessBlockEditVerifier.IsSupportedState(connectionWorkbenchState))
        {
            var editPreviewContext = await SmokeProcessBlockPreparation.PrepareAsync(
                window,
                vm,
                initialProject,
                workbench,
                (root, predicate) => FindVisualDescendant<Border>(root, predicate),
                (root, predicate) => FindVisualDescendant<Button>(root, predicate),
                (root, predicate) => FindVisualDescendant<CheckBox>(root, predicate));
            var editAppliedContext = await SmokeProcessBlockPreparation.ApplyAndRecognizeAsync(
                window,
                vm,
                editPreviewContext);
            var editResult = await SmokeProcessBlockEditVerifier.VerifyAsync(
                window,
                vm,
                connectionWorkbenchState,
                editAppliedContext,
                connectionWorkbenchSavePath,
                !string.IsNullOrWhiteSpace(connectionWorkbenchReportPath));
            if (editResult.Report is not null
                && !string.IsNullOrWhiteSpace(connectionWorkbenchReportPath))
            {
                workflowReport = editResult.Report;
                workflowReport.Save(connectionWorkbenchReportPath);
            }
        }
        else if (SmokeProcessBlockTimeoutVerifier.IsSupportedState(connectionWorkbenchState))
        {
            var timeoutPreviewContext = await SmokeProcessBlockPreparation.PrepareAsync(
                window,
                vm,
                initialProject,
                workbench,
                (root, predicate) => FindVisualDescendant<Border>(root, predicate),
                (root, predicate) => FindVisualDescendant<Button>(root, predicate),
                (root, predicate) => FindVisualDescendant<CheckBox>(root, predicate));
            var timeoutAppliedContext = await SmokeProcessBlockPreparation.ApplyAndRecognizeAsync(
                window,
                vm,
                timeoutPreviewContext);
            var timeoutResult = await SmokeProcessBlockTimeoutVerifier.VerifyAsync(
                window,
                vm,
                connectionWorkbenchState,
                timeoutAppliedContext,
                connectionWorkbenchSavePath,
                !string.IsNullOrWhiteSpace(connectionWorkbenchReportPath),
                (root, predicate) => FindVisualDescendant<TextBox>(root, predicate),
                (root, predicate) => FindVisualDescendant<Button>(root, predicate),
                (root, predicate) => FindVisualDescendant<ItemsControl>(root, predicate),
                interaction.MovePointerToCenter,
                interaction.MouseEvent,
                interaction.MarkSmokePointerHeld);
            if (timeoutResult.Report is not null
                && !string.IsNullOrWhiteSpace(connectionWorkbenchReportPath))
            {
                workflowReport = timeoutResult.Report;
                workflowReport.Save(connectionWorkbenchReportPath);
            }
        }
        else if (SmokeProcessBlockStepStatusVerifier.IsSupportedState(connectionWorkbenchState))
        {
            var stepStatusPreviewContext = await SmokeProcessBlockPreparation.PrepareAsync(
                window,
                vm,
                initialProject,
                workbench,
                (root, predicate) => FindVisualDescendant<Border>(root, predicate),
                (root, predicate) => FindVisualDescendant<Button>(root, predicate),
                (root, predicate) => FindVisualDescendant<CheckBox>(root, predicate));
            var stepStatusAppliedContext = SmokeProcessBlockStepStatusVerifier.RequiresAppliedContext(
                connectionWorkbenchState)
                ? await SmokeProcessBlockPreparation.ApplyAndRecognizeAsync(
                    window,
                    vm,
                    stepStatusPreviewContext)
                : null;
            var stepStatusResult = await SmokeProcessBlockStepStatusVerifier.VerifyAsync(
                window,
                vm,
                connectionWorkbenchState,
                workbench,
                stepStatusPreviewContext,
                stepStatusAppliedContext,
                connectionWorkbenchSavePath,
                !string.IsNullOrWhiteSpace(connectionWorkbenchReportPath),
                (root, predicate) => FindVisualDescendant<Button>(root, predicate),
                (root, predicate) => FindVisualDescendant<ListBox>(root, predicate),
                (root, predicate) => FindVisualDescendant<RadioButton>(root, predicate),
                (root, predicate) => FindVisualDescendant<TextBlock>(root, predicate),
                interaction.MovePointerToCenter,
                interaction.MouseEvent,
                interaction.MarkSmokePointerHeld);
            if (stepStatusResult.Report is not null
                && !string.IsNullOrWhiteSpace(connectionWorkbenchReportPath))
            {
                workflowReport = stepStatusResult.Report;
                workflowReport.Save(connectionWorkbenchReportPath);
            }
        }
        else if (SmokeRecipeDryRunStateVerifier.IsSupportedState(connectionWorkbenchState))
        {
            await SmokeRecipeDryRunStateVerifier.ApplyAsync(
                window,
                vm,
                initialProject,
                workbench,
                controls.DryRunButton,
                connectionWorkbenchState,
                connectionWorkbenchSavePath,
                interaction);
        }
        else if (SmokeRecipeConnectionStateVerifier.IsSupportedState(connectionWorkbenchState))
        {
            await SmokeRecipeConnectionStateVerifier.ApplyAsync(
                window,
                vm,
                workbench,
                controls.AddStageButton,
                controls.AddRotaryStageButton,
                controls.ReadinessButton,
                controls.DryRunButton,
                controls.StationSkeletonButton,
                controls.ProcessBlockButton,
                controls.LoadLockSetupButton,
                controls.CheckpointTemplateButton,
                connectionWorkbenchState,
                interaction);
        }
        else if (SmokeProcessBlockSequenceStateVerifier.IsSupportedState(connectionWorkbenchState))
        {
            workflowReport = await SmokeProcessBlockSequenceStateVerifier.ApplyAsync(
                window,
                vm,
                initialProject,
                workbench,
                controls.ProcessBlockButton,
                projectPath,
                connectionWorkbenchState,
                connectionWorkbenchReportPath,
                connectionWorkbenchSavePath,
                interaction,
                (root, predicate) => FindVisualDescendant<Border>(root, predicate),
                (root, predicate) => FindVisualDescendant<CheckBox>(root, predicate),
                (root, predicate) => FindVisualDescendant<ListBox>(root, predicate));
        }
        else if (SmokeRecipeCheckpointStateVerifier.IsSupportedState(connectionWorkbenchState))
        {
            await SmokeRecipeCheckpointStateVerifier.ApplyAsync(
                window,
                vm,
                initialProject,
                workbench,
                controls.AddStageButton,
                controls.CheckpointTemplateButton,
                connectionWorkbenchState,
                connectionWorkbenchSavePath,
                interaction,
                (root, predicate) => FindVisualDescendant<Border>(root, predicate),
                (root, predicate) => FindVisualDescendant<ListBox>(root, predicate));
        }
        else
        {
            throw new ArgumentException(
                $"Unsupported --smoke-connection-workbench-state '{connectionWorkbenchState}'. " +
                "Expected a supported connection-workbench smoke state, including dry-run, dry-run-playback, or dry-run-wafer-handler-fault-playback.");
        }

        return new DirectExeConnectionWorkbenchResult
        {
            LoadLockSetupReport = loadLockSetupReport,
            StationSkeletonReport = stationSkeletonReport,
            WorkflowReport = workflowReport
        };
    }

    private static DirectExeConnectionWorkbenchControls FindControls(RecipeConnectionWorkbenchView workbench) =>
        new()
        {
            AddStageButton = FindButton(workbench, "AddConnectionStageButton", "Axis + stage button"),
            AddRotaryStageButton = FindButton(workbench, "AddConnectionRotaryStageButton", "Rotary axis + stage button"),
            ReadinessButton = FindButton(workbench, "ValidateSimulationReadinessButton", "Simulation readiness button"),
            DryRunButton = FindButton(workbench, "RunRecipeDryRunButton", "Recipe dry-run button"),
            CheckpointTemplateButton = FindButton(workbench, "PreviewRecipeCheckpointTemplateButton", "Recipe checkpoint template button"),
            StationSkeletonButton = FindButton(workbench, "PreviewSemiconductorStationButton", "Semiconductor station button"),
            ProcessBlockButton = FindButton(workbench, "PreviewProcessBlockComposerButton", "Process block composer button"),
            LoadLockSetupButton = FindButton(workbench, "PreviewLoadLockSetupButton", "Load-lock setup button")
        };

    private static Button FindButton(RecipeConnectionWorkbenchView workbench, string name, string description) =>
        FindVisualDescendant<Button>(
            workbench,
            candidate => string.Equals(candidate.Name, name, StringComparison.Ordinal))
        ?? throw new InvalidOperationException($"{description} was not available.");

    private static bool IsDedicatedState(string? state) =>
        state?.StartsWith("station-skeleton-", StringComparison.OrdinalIgnoreCase) == true
        || state?.StartsWith("load-lock-", StringComparison.OrdinalIgnoreCase) == true
        || state?.StartsWith("semantic-setup-", StringComparison.OrdinalIgnoreCase) == true
        || state?.StartsWith("process-block-", StringComparison.OrdinalIgnoreCase) == true
        || SmokeRecipeCheckpointStateVerifier.IsSupportedState(state);

    private sealed class DirectExeConnectionWorkbenchControls
    {
        public required Button AddStageButton { get; init; }
        public required Button AddRotaryStageButton { get; init; }
        public required Button ReadinessButton { get; init; }
        public required Button DryRunButton { get; init; }
        public required Button CheckpointTemplateButton { get; init; }
        public required Button StationSkeletonButton { get; init; }
        public required Button ProcessBlockButton { get; init; }
        public required Button LoadLockSetupButton { get; init; }
    }
}
