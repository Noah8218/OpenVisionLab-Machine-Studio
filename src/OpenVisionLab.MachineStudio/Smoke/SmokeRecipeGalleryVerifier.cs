using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using OpenVisionLab;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.MachineStudio.View.Shell;
using OpenVisionLab.MachineStudio.ViewModel;

namespace OpenVisionLab.MachineStudio;

internal sealed class SmokeRecipeGalleryReport
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
internal static class SmokeRecipeGalleryVerifier
{
    private const uint MouseEventLeftDown = 0x0002;

    public static async Task<SmokeRecipeGalleryReport> VerifyAsync(
        ShellWindow window,
        MainViewModel vm,
        string recipeGalleryState,
        MachineProjectDocument? initialProject,
        string? recipeGalleryCopyPath,
        string? recipeGalleryCompatibilityReportPath,
        string? recipeGalleryBaselineReportPath,
        string? recipeGalleryCurrentReportPath,
        bool recipeGalleryExpectFailure,
        Func<DependencyObject, Func<Button, bool>, Button?> findButton,
        Action activateWindow,
        Action<FrameworkElement> movePointerToCenter,
        Action<uint, uint, uint, uint, UIntPtr> mouseEvent,
        Action markSmokePointerHeld)
    {
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

        var titleBeforePreview = vm.Title;
        var runningBeforePreview = vm.IsRunning;
        var designModeBeforePreview = vm.IsDesignMode;
        var mainSnapshotBeforePreview = vm.SceneSnapshots.Latest;
        var projectStoreBeforePreview = new ProjectDocumentStore();
        var projectBeforePreview = initialProject is null
            ? string.Empty
            : projectStoreBeforePreview.Serialize(initialProject);
        vm.SemiconductorRecipes.Open();
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);

        Check("gallery-open", vm.SemiconductorRecipes.IsOpen);
        Check("ten-bundled-recipes", vm.SemiconductorRecipes.Items.Count == 10);
        Check("default-selection", vm.SemiconductorRecipes.SelectedItem is not null);
        Check("all-topology-summaries-visible", vm.SemiconductorRecipes.Items.All(item =>
            !string.IsNullOrWhiteSpace(item.TopologySummary)));
        Check("all-distinctive-equipment-visible", vm.SemiconductorRecipes.Items.All(item =>
            !string.IsNullOrWhiteSpace(item.EquipmentFocus)));
        Check("materially-varied-count-profiles", vm.SemiconductorRecipes.Items
            .Select(item => $"{item.AxisCount}:{item.SensorCount}:{item.CylinderCount}:{item.ConveyorCount}:{item.WorkpieceCount}:{item.StepCount}")
            .Distinct(StringComparer.Ordinal)
            .Count() >= 9);
        Check("preview-title-unchanged", vm.Title == titleBeforePreview);
        Check("preview-run-state-unchanged", vm.IsRunning == runningBeforePreview);
        Check("preview-mode-unchanged", vm.IsDesignMode == designModeBeforePreview);

        if (string.Equals(recipeGalleryState, "selected-last", StringComparison.OrdinalIgnoreCase)
            || string.Equals(recipeGalleryState, "copy", StringComparison.OrdinalIgnoreCase)
            || string.Equals(recipeGalleryState, "pressed", StringComparison.OrdinalIgnoreCase))
        {
            vm.SemiconductorRecipes.SelectedItem = vm.SemiconductorRecipes.Items.LastOrDefault();
        }
        else if (!string.Equals(recipeGalleryState, "open", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(recipeGalleryState, "validate-all", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(recipeGalleryState, "validate-focus", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(recipeGalleryState, "validate-pressed", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(recipeGalleryState, "validate-disabled", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(recipeGalleryState, "compatibility-disabled", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(recipeGalleryState, "compatibility-pressed", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(recipeGalleryState, "compare", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(recipeGalleryState, "compare-close", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(recipeGalleryState, "compare-button-pressed", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(recipeGalleryState, "compare-close-pressed", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(recipeGalleryState, "compare-invalid", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"Unsupported --smoke-recipe-gallery-state '{recipeGalleryState}'. " +
                "Expected open, selected-last, pressed, validate-all, validate-focus, " +
                "validate-pressed, validate-disabled, compatibility-disabled, " +
                "compatibility-pressed, compare, compare-close, compare-button-pressed, " +
                "compare-close-pressed, compare-invalid, or copy.");
        }

        if (string.Equals(recipeGalleryState, "compare-button-pressed", StringComparison.OrdinalIgnoreCase))
        {
            var compareButton = findButton(
                window,
                candidate => ReferenceEquals(
                    candidate.Command,
                    vm.SemiconductorRecipes.CompareCompatibilityReportsCommand))
                ?? throw new InvalidOperationException(
                    "Recipe gallery comparison button was not available.");
            Check("comparison-button-enabled",
                vm.SemiconductorRecipes.CompareCompatibilityReportsCommand.CanExecute(null));
            window.Activate();
            activateWindow();
            compareButton.Focus();
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Check("comparison-button-keyboard-focus", compareButton.IsKeyboardFocusWithin);
            movePointerToCenter(compareButton);
            await Task.Delay(100);
            Check("comparison-button-pointer-hover", compareButton.IsMouseOver);
            mouseEvent(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
            markSmokePointerHeld();
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Check("comparison-button-pointer-down", compareButton.IsPressed);
        }

        if (string.Equals(recipeGalleryState, "compare", StringComparison.OrdinalIgnoreCase)
            || string.Equals(recipeGalleryState, "compare-close", StringComparison.OrdinalIgnoreCase)
            || string.Equals(recipeGalleryState, "compare-close-pressed", StringComparison.OrdinalIgnoreCase))
        {
            Check("comparison-loads-valid-reports",
                vm.SemiconductorRecipes.TryCompareCompatibilityReports(
                    recipeGalleryBaselineReportPath!,
                    recipeGalleryCurrentReportPath!));
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);

            Check("comparison-open", vm.SemiconductorRecipes.IsComparisonOpen);
            Check("comparison-all-results-projected", vm.SemiconductorRecipes.ComparisonItems.Count == 11);
            Check("comparison-new-failure", vm.SemiconductorRecipes.NewlyFailedCount > 0);
            Check("comparison-recovered", vm.SemiconductorRecipes.RecoveredCount > 0);
            Check("comparison-metadata-change", vm.SemiconductorRecipes.MetadataChangedCount > 0);
            Check("comparison-added", vm.SemiconductorRecipes.AddedCount > 0);
            Check("comparison-removed", vm.SemiconductorRecipes.RemovedCount > 0);
            Check("comparison-summary-visible",
                !string.IsNullOrWhiteSpace(vm.SemiconductorRecipes.ComparisonSummary)
                && !string.IsNullOrWhiteSpace(vm.SemiconductorRecipes.ProjectSchemaComparison));
            Check("comparison-title-unchanged", vm.Title == titleBeforePreview);
            Check("comparison-run-state-unchanged", vm.IsRunning == runningBeforePreview);
            Check("comparison-mode-unchanged", vm.IsDesignMode == designModeBeforePreview);
            Check("comparison-runtime-unchanged",
                mainSnapshotBeforePreview?.TickIndex == vm.SceneSnapshots.Latest?.TickIndex
                && mainSnapshotBeforePreview?.SimulationTime == vm.SceneSnapshots.Latest?.SimulationTime);
            Check("comparison-project-unchanged",
                initialProject is null
                || projectStoreBeforePreview.Serialize(initialProject) == projectBeforePreview);

            if (string.Equals(recipeGalleryState, "compare-close-pressed", StringComparison.OrdinalIgnoreCase))
            {
                var closeComparisonButton = findButton(
                    window,
                    candidate => ReferenceEquals(
                        candidate.Command,
                        vm.SemiconductorRecipes.CloseCompatibilityComparisonCommand))
                    ?? throw new InvalidOperationException(
                        "Recipe gallery close comparison button was not available.");
                Check("comparison-close-enabled",
                    vm.SemiconductorRecipes.CloseCompatibilityComparisonCommand.CanExecute(null));
                window.Activate();
                activateWindow();
                closeComparisonButton.Focus();
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                Check("comparison-close-keyboard-focus", closeComparisonButton.IsKeyboardFocusWithin);
                movePointerToCenter(closeComparisonButton);
                await Task.Delay(100);
                Check("comparison-close-pointer-hover", closeComparisonButton.IsMouseOver);
                mouseEvent(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
                markSmokePointerHeld();
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                Check("comparison-close-pointer-down", closeComparisonButton.IsPressed);
            }

            if (string.Equals(recipeGalleryState, "compare-close", StringComparison.OrdinalIgnoreCase))
            {
                vm.SemiconductorRecipes.CloseCompatibilityComparisonCommand.Execute(null);
                Check("comparison-close-restores-gallery", !vm.SemiconductorRecipes.IsComparisonOpen);
                Check("comparison-close-clears-results", vm.SemiconductorRecipes.ComparisonItems.Count == 0);
            }
        }

        if (string.Equals(recipeGalleryState, "compare-invalid", StringComparison.OrdinalIgnoreCase))
        {
            Check("comparison-rejects-invalid-report",
                !vm.SemiconductorRecipes.TryCompareCompatibilityReports(
                    recipeGalleryBaselineReportPath!,
                    recipeGalleryCurrentReportPath!));
            Check("comparison-invalid-error-visible", vm.SemiconductorRecipes.HasError);
            Check("comparison-invalid-remains-closed", !vm.SemiconductorRecipes.IsComparisonOpen);
            Check("comparison-invalid-project-unchanged",
                initialProject is null
                || projectStoreBeforePreview.Serialize(initialProject) == projectBeforePreview);
            Check("comparison-invalid-runtime-unchanged",
                mainSnapshotBeforePreview?.TickIndex == vm.SceneSnapshots.Latest?.TickIndex
                && mainSnapshotBeforePreview?.SimulationTime == vm.SceneSnapshots.Latest?.SimulationTime);
        }

        if (string.Equals(recipeGalleryState, "compatibility-disabled", StringComparison.OrdinalIgnoreCase))
        {
            Check("compatibility-report-disabled-before-validation",
                !vm.SemiconductorRecipes.SaveCompatibilityReportCommand.CanExecute(null));
        }

        if (string.Equals(recipeGalleryState, "compatibility-pressed", StringComparison.OrdinalIgnoreCase))
        {
            await vm.SemiconductorRecipes.ValidateAllForSmokeAsync();
            var reportButton = findButton(
                window,
                candidate => ReferenceEquals(
                    candidate.Command,
                    vm.SemiconductorRecipes.SaveCompatibilityReportCommand))
                ?? throw new InvalidOperationException(
                    "Recipe gallery compatibility report button was not available.");
            Check("compatibility-report-enabled-after-validation",
                vm.SemiconductorRecipes.SaveCompatibilityReportCommand.CanExecute(null));
            window.Activate();
            activateWindow();
            reportButton.Focus();
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Check("compatibility-report-keyboard-focus", reportButton.IsKeyboardFocusWithin);
            movePointerToCenter(reportButton);
            await Task.Delay(100);
            Check("compatibility-report-pointer-hover", reportButton.IsMouseOver);
            mouseEvent(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
            markSmokePointerHeld();
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Check("compatibility-report-pointer-down", reportButton.IsPressed);
        }

        if (recipeGalleryState?.StartsWith("validate-", StringComparison.OrdinalIgnoreCase) == true
            && !string.Equals(recipeGalleryState, "validate-all", StringComparison.OrdinalIgnoreCase))
        {
            var validateButton = findButton(
                window,
                candidate => ReferenceEquals(
                    candidate.Command,
                    vm.SemiconductorRecipes.ValidateAllCommand))
                ?? throw new InvalidOperationException(
                    "Recipe gallery Validate all 10 button was not available.");
            window.Activate();
            activateWindow();
            validateButton.Focus();
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Check("validate-button-keyboard-focus", validateButton.IsKeyboardFocusWithin);

            if (string.Equals(recipeGalleryState, "validate-pressed", StringComparison.OrdinalIgnoreCase))
            {
                movePointerToCenter(validateButton);
                await Task.Delay(100);
                Check("validate-button-pointer-hover", validateButton.IsMouseOver);
                mouseEvent(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
                markSmokePointerHeld();
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                Check("validate-button-pointer-down", validateButton.IsPressed);
            }

            if (string.Equals(recipeGalleryState, "validate-disabled", StringComparison.OrdinalIgnoreCase))
            {
                vm.SemiconductorRecipes.ValidateAllCommand.Execute(null);
                for (var attempt = 0; attempt < 100 && !vm.SemiconductorRecipes.IsBusy; attempt++)
                {
                    await Task.Delay(10);
                }

                Check("validate-button-disabled-while-running",
                    vm.SemiconductorRecipes.IsBusy
                    && !vm.SemiconductorRecipes.ValidateAllCommand.CanExecute(null));
                Check("create-copy-disabled-while-validating",
                    !vm.SemiconductorRecipes.CreateCopyCommand.CanExecute(null));
                Check("close-disabled-while-validating",
                    !vm.SemiconductorRecipes.CloseCommand.CanExecute(null));
            }
        }

        if (string.Equals(recipeGalleryState, "validate-all", StringComparison.OrdinalIgnoreCase))
        {
            await vm.SemiconductorRecipes.ValidateAllForSmokeAsync();
            Check("validation-queue-completed",
                vm.SemiconductorRecipes.ValidatedCount == vm.SemiconductorRecipes.Items.Count
                && vm.SemiconductorRecipes.ValidatedCount == 10);
            Check("validation-all-items-terminal", vm.SemiconductorRecipes.Items.All(item =>
                item.IsValidationPassed || item.IsValidationFailed));
            Check("validation-progress-10-of-10",
                vm.SemiconductorRecipes.ValidationProgressText.Contains("10/10", StringComparison.Ordinal));
            Check("validation-summary-visible", vm.SemiconductorRecipes.HasValidationSummary);
            Check("validation-schema-recorded", vm.SemiconductorRecipes.Items.All(item =>
                !string.IsNullOrWhiteSpace(item.ProjectSchema)));
            Check("validation-build-recorded", vm.SemiconductorRecipes.Items.All(item =>
                string.Equals(item.ValidationBuildIdentity, BuildIdentity.Current, StringComparison.Ordinal)
                && string.Equals(item.ValidationSourceCommit, BuildIdentity.SourceCommit, StringComparison.Ordinal)
                && string.Equals(item.ValidationSourceState, BuildIdentity.SourceState, StringComparison.Ordinal)
                && item.ValidationIsExactCommit == BuildIdentity.IsExactCommit));
            Check("compatibility-report-available",
                vm.SemiconductorRecipes.SaveCompatibilityReportCommand.CanExecute(null));
            if (!string.IsNullOrWhiteSpace(recipeGalleryCompatibilityReportPath))
            {
                vm.SemiconductorRecipes.SaveCompatibilityReport(recipeGalleryCompatibilityReportPath);
                Check("compatibility-report-created",
                    File.Exists(recipeGalleryCompatibilityReportPath)
                    && new FileInfo(recipeGalleryCompatibilityReportPath).Length > 0);
            }
            if (recipeGalleryExpectFailure)
            {
                Check("validation-first-failure-captured",
                    vm.SemiconductorRecipes.FailedCount > 0
                    && !string.IsNullOrWhiteSpace(vm.SemiconductorRecipes.FirstFailureRecipeName)
                    && !string.IsNullOrWhiteSpace(vm.SemiconductorRecipes.FirstFailureStepId)
                    && !string.IsNullOrWhiteSpace(vm.SemiconductorRecipes.FirstFailureDetail));
                Check("validation-first-failure-selected",
                    string.Equals(
                        vm.SemiconductorRecipes.SelectedItem?.DisplayName,
                        vm.SemiconductorRecipes.FirstFailureRecipeName,
                        StringComparison.Ordinal));
            }
            else
            {
                Check("validation-all-ten-passed",
                    vm.SemiconductorRecipes.PassedCount == 10
                    && vm.SemiconductorRecipes.FailedCount == 0
                    && vm.SemiconductorRecipes.Items.All(item => item.IsValidationPassed));
            }

            Check("validation-title-unchanged", vm.Title == titleBeforePreview);
            Check("validation-run-state-unchanged", vm.IsRunning == runningBeforePreview);
            Check("validation-mode-unchanged", vm.IsDesignMode == designModeBeforePreview);
            Check("validation-runtime-unchanged",
                mainSnapshotBeforePreview?.TickIndex == vm.SceneSnapshots.Latest?.TickIndex
                && mainSnapshotBeforePreview?.SimulationTime == vm.SceneSnapshots.Latest?.SimulationTime);
            Check("validation-project-unchanged",
                initialProject is null
                || projectStoreBeforePreview.Serialize(initialProject) == projectBeforePreview);
        }

        if (string.Equals(recipeGalleryState, "pressed", StringComparison.OrdinalIgnoreCase))
        {
            var createCopyButton = findButton(
                window,
                candidate => ReferenceEquals(
                    candidate.Command,
                    vm.SemiconductorRecipes.CreateCopyCommand))
                ?? throw new InvalidOperationException(
                    "Recipe gallery Create a copy button was not available.");
            window.Activate();
            activateWindow();
            createCopyButton.Focus();
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            movePointerToCenter(createCopyButton);
            await Task.Delay(100);
            Check("create-copy-pointer-hover", createCopyButton.IsMouseOver);
            mouseEvent(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
            markSmokePointerHeld();
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Check("create-copy-pointer-down", createCopyButton.IsPressed);
        }

        if (string.Equals(recipeGalleryState, "copy", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(recipeGalleryCopyPath))
            {
                throw new ArgumentException(
                    "--smoke-recipe-gallery-copy is required for copy state.");
            }

            var selectedRecipe = vm.SemiconductorRecipes.SelectedItem
                ?? throw new InvalidOperationException("Recipe gallery selection was not available.");
            var fullCopyPath = Path.GetFullPath(recipeGalleryCopyPath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullCopyPath)!);
            var store = new ProjectDocumentStore();
            var sourceProject = await store.LoadAsync(selectedRecipe.SourcePath);
            var created = await vm.SemiconductorRecipes.CreateCopyToAsync(fullCopyPath);
            var copiedProject = File.Exists(fullCopyPath)
                ? await store.LoadAsync(fullCopyPath)
                : null;

            Check("copy-created", created && copiedProject is not null);
            Check("copy-has-new-project-id", copiedProject?.Id != sourceProject.Id);
            Check("copy-preserves-axis-count", copiedProject?.Axes.Count == sourceProject.Axes.Count);
            Check("copy-preserves-device-count", copiedProject?.Devices.Count == sourceProject.Devices.Count);
            Check("copy-preserves-channel-count", copiedProject?.Channels.Count == sourceProject.Channels.Count);
            Check("copy-preserves-sequence-count", copiedProject?.Sequences.Count == sourceProject.Sequences.Count);
            Check("copy-opens-in-design-mode", vm.IsDesignMode);
            Check("copy-does-not-run-simulation", !vm.IsRunning);
            Check("copy-becomes-current-project",
                vm.Title.EndsWith(Path.GetFileNameWithoutExtension(fullCopyPath), StringComparison.Ordinal));
            Check("gallery-closes-after-copy", !vm.SemiconductorRecipes.IsOpen);
        }

        return new SmokeRecipeGalleryReport
        {
            Checks = checks,
            Failures = failures
        };
    }
}
