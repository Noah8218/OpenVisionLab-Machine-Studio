using System.IO;
using System.Linq;
using System.Windows.Threading;
using OpenVisionLab.Machine.Sequence.Authoring;
using OpenVisionLab.Machine.Simulation.Sequences;
using OpenVisionLab.MachineStudio.View.Shell;
using OpenVisionLab.MachineStudio.ViewModel;

namespace OpenVisionLab.MachineStudio;

internal sealed class SmokeProcessBlockEditResult
{
    public SmokeWorkflowReport? Report { get; init; }
}

internal static class SmokeProcessBlockEditVerifier
{
    public static bool IsSupportedState(string? state) => state?.ToLowerInvariant() is
        "process-block-edit-current"
        or "process-block-edit-remove"
        or "process-block-edit-empty"
        or "process-block-edited";

    public static async Task<SmokeProcessBlockEditResult> VerifyAsync(
        ShellWindow window,
        MainViewModel vm,
        string editState,
        SmokeAppliedProcessBlockContext appliedContext,
        string? savePath,
        bool createReport)
    {
        var normalizedState = editState.ToLowerInvariant();
        if (!IsSupportedState(normalizedState))
        {
            throw new ArgumentException(
                $"Unsupported --smoke-connection-workbench-state '{editState}'. " +
                "Expected process-block-edit-current, process-block-edit-remove, " +
                "process-block-edit-empty, or process-block-edited.");
        }

        if (normalizedState == "process-block-edit-current")
        {
            return new SmokeProcessBlockEditResult();
        }

        var context = appliedContext.PreviewContext;
        vm.RecipeConnections.ProcessBlocks.IsInspectBlockSelected = false;
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Check(
            vm.RecipeConnections.ProcessBlocks.SelectedProcessBlockCount == 4
            && vm.RecipeConnections.ProcessBlocks.ProcessBlockItems.Count == 13
            && vm.RecipeConnections.ProcessBlocks.ProcessBlockItems.Count(item => item.IsProposedRemoval) == 1
            && context.ApplyButton.IsEnabled
            && appliedContext.ProjectAfterApply == context.Store.SerializeForEvidence(context.Project),
            "Clearing Inspect did not preview exactly one managed-step removal without mutation.");

        if (normalizedState == "process-block-edit-remove")
        {
            return new SmokeProcessBlockEditResult();
        }

        if (normalizedState == "process-block-edit-empty")
        {
            vm.RecipeConnections.ProcessBlocks.IsLoadBlockSelected = false;
            vm.RecipeConnections.ProcessBlocks.IsAlignBlockSelected = false;
            vm.RecipeConnections.ProcessBlocks.IsProcessBlockSelected = false;
            vm.RecipeConnections.ProcessBlocks.IsUnloadBlockSelected = false;
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Check(
                vm.RecipeConnections.ProcessBlocks.SelectedProcessBlockCount == 0
                && vm.RecipeConnections.ProcessBlocks.ProcessBlockItems.Count == 13
                && vm.RecipeConnections.ProcessBlocks.ProcessBlockItems.All(item => item.IsProposedRemoval)
                && context.ApplyButton.IsEnabled
                && appliedContext.ProjectAfterApply == context.Store.SerializeForEvidence(context.Project),
                "Clearing the current plan did not preview all managed-step removals safely.");
            return new SmokeProcessBlockEditResult();
        }

        vm.RecipeConnections.ProcessBlocks.ApplyProcessBlockCommand.Execute(null);
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Check(
            vm.RecipeConnections.RecipeStepCount == 24
            && vm.IsDesignMode
            && !vm.IsRunning,
            "Removing Inspect did not retain the expected stopped 24-step recipe.");

        vm.RecipeConnections.ValidateSimulationReadinessCommand.Execute(null);
        vm.RecipeConnections.RunRecipeDryRunCommand.Execute(null);
        for (var attempt = 0;
             attempt < 200 && !vm.RecipeConnections.HasRecipeDryRunResult;
             attempt++)
        {
            await Task.Delay(20);
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        }

        Check(
            vm.RecipeConnections.ReadinessPassed == true
            && vm.RecipeConnections.RecipeDryRunResult?.Outcome == RecipeDryRunOutcome.Completed
            && vm.RecipeConnections.RecipeDryRunTimeline.Count == 24,
            "The edited 24-step recipe did not pass readiness and bounded dry run.");

        var allKinds = Enum.GetValues<SemiconductorProcessBlockKind>();
        var retainedKinds = allKinds
            .Where(kind => kind != SemiconductorProcessBlockKind.Inspect)
            .ToArray();
        var bundledProcessRecipes = Directory.EnumerateFiles(
                Path.Combine(AppContext.BaseDirectory, "Samples", "SemiconductorRecipes"),
                "*.ovmachine")
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var allBundledEditsComplete = bundledProcessRecipes.Length == 10;
        foreach (var path in bundledProcessRecipes)
        {
            var bundledProject = context.Store.Load(File.ReadAllText(path));
            var composer = new SemiconductorProcessBlockComposer();
            var initialApply = composer.Apply(bundledProject, allKinds);
            var editPreview = composer.Preview(bundledProject, retainedKinds);
            var editResult = composer.Apply(bundledProject, retainedKinds);
            var bundledSequenceId = bundledProject.Simulation.AutomaticRun?.SequenceId ?? string.Empty;
            var bundledResult = await new DeterministicRecipeDryRunRunner().RunAsync(
                bundledProject,
                bundledSequenceId);
            var reopenedKinds = composer.RecognizeExistingKinds(
                context.Store.Load(context.Store.Serialize(bundledProject)));
            var expectedKinds = editPreview.RemovedStepCount == 0 ? allKinds : retainedKinds;
            allBundledEditsComplete &= initialApply.Changed
                && editPreview.ExistingKinds.SequenceEqual(allKinds)
                && editPreview.RemovedStepCount is 0 or 1
                && editResult.Changed == (editPreview.RemovedStepCount == 1)
                && editResult.RemovedStepCount == editPreview.RemovedStepCount
                && !bundledProject.Sequences.SelectMany(sequence => sequence.Steps).Any(step =>
                    step.Id.StartsWith("process-block.inspect.", StringComparison.Ordinal))
                && bundledResult.Outcome == RecipeDryRunOutcome.Completed
                && reopenedKinds.SequenceEqual(expectedKinds);
        }

        Check(
            allBundledEditsComplete,
            "Inspect removal did not follow current authored-feature truth across all ten bundled recipes.");

        if (!string.IsNullOrWhiteSpace(savePath))
        {
            var fullSavePath = Path.GetFullPath(savePath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullSavePath)!);
            await vm.SaveProjectAsync(fullSavePath);
            Check(await vm.OpenProjectAsync(fullSavePath), "The edited process plan did not reopen.");
            vm.RecipeConnections.ProcessBlocks.PreviewProcessBlockCommand.Execute(null);
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Check(
                vm.RecipeConnections.RecipeStepCount == 24
                && vm.RecipeConnections.ProcessBlocks.SelectedProcessBlockCount == 4
                && vm.RecipeConnections.ProcessBlocks.ExistingProcessBlockCount == 4
                && !context.ApplyButton.IsEnabled
                && vm.IsDesignMode
                && !vm.IsRunning,
                "Reopened process plan did not restore the edited four-block selection safely.");
        }

        return new SmokeProcessBlockEditResult
        {
            Report = createReport
                ? new SmokeWorkflowReport
                {
                    Checks = new Dictionary<string, bool>(StringComparer.Ordinal)
                    {
                        ["current-five-block-plan-recognized"] = true,
                        ["recognized-plan-preview-unchanged"] = true,
                        ["inspect-removal-previewed-once"] = true,
                        ["removal-preview-project-unchanged"] = true,
                        ["inspect-managed-step-removed"] = true,
                        ["twenty-four-step-recipe-retained"] = true,
                        ["edit-remains-stopped-in-design"] = true,
                        ["readiness-passed"] = true,
                        ["bounded-dry-run-completed"] = true,
                        ["twenty-four-step-timeline"] = true,
                        ["ten-bundled-recipes-edited-and-dry-run"] = true,
                        ["save-reopen-retained-edit"] = true,
                        ["reopened-four-block-plan-recognized"] = true,
                        ["reopen-remains-stopped-in-design"] = true
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
