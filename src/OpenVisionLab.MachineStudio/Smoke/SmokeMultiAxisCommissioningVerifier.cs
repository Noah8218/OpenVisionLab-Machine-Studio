using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Simulation.Axis;
using OpenVisionLab.Machine.Simulation.Commissioning;
using OpenVisionLab.Machine.Simulation.Engine;
using OpenVisionLab.MachineStudio.View.Scene;
using OpenVisionLab.MachineStudio.View.Shell;
using OpenVisionLab.MachineStudio.ViewModel;

namespace OpenVisionLab.MachineStudio;

internal sealed class SmokeMultiAxisCommissioningReport
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

internal static class SmokeMultiAxisCommissioningVerifier
{
    public static async Task<SmokeMultiAxisCommissioningReport> VerifyAsync(
        ShellWindow window,
        MainViewModel viewModel,
        string? savePath,
        Func<DependencyObject, MachineSceneViewport?> findViewport)
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

        async Task WaitForAsync(Func<bool> condition, string failureMessage)
        {
            for (var attempt = 0; attempt < 100; attempt++)
            {
                if (condition())
                {
                    return;
                }

                await Task.Delay(25);
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            }

            throw new InvalidOperationException(failureMessage);
        }

        var recipe = viewModel.MultiAxisCommissioningRecipe;
        Check("recipeConfigured", recipe.IsConfigured);
        Check("recipeValid", recipe.IsValid);
        Check("orderedTargets", recipe.Targets.Select(target => target.AxisId)
            .SequenceEqual(new[] { "y", "x" }, StringComparer.Ordinal));
        Check("loadedWithoutExecution", viewModel.IsDesignMode
            && !viewModel.IsRunning
            && viewModel.SceneSnapshots.Latest?.TickIndex == 0
            && viewModel.SceneSnapshots.Latest?.ControlOwner == SimulationControlOwner.Definition);

        if (!string.IsNullOrWhiteSpace(savePath))
        {
            await viewModel.SaveProjectAsync(savePath);
            Check("savedRecipeOrder", new ProjectDocumentStore().Load(File.ReadAllText(savePath))
                .MultiAxisCommissioningRecipe?.Targets.Select(target => target.AxisId)
                .SequenceEqual(new[] { "y", "x" }, StringComparer.Ordinal) == true);
            Check("reopenAccepted", await viewModel.OpenProjectAsync(savePath));
            Check("reopenDoesNotExecute", viewModel.IsDesignMode
                && !viewModel.IsRunning
                && viewModel.SceneSnapshots.Latest?.TickIndex == 0
                && viewModel.SceneSnapshots.Latest?.ControlOwner == SimulationControlOwner.Definition
                && viewModel.SceneSnapshots.Latest.Axes.All(axis => axis.State == AxisState.Idle));
            Check("reopenPreservesTargets", viewModel.MultiAxisCommissioningRecipe.Targets
                .Select(target => $"{target.AxisId}:{target.TargetPosition:F3}")
                .SequenceEqual(new[] { "y:120.000", "x:240.000" }, StringComparer.Ordinal)
                && viewModel.MultiAxisCommissioningRecipe.ValidationRepetitions == 3);
            Check("reopenPreservesDistinctAxisLayout",
                viewModel.Layout.Items.Single(item => item.Id == "x").Position.Y == 200
                && viewModel.Layout.Items.Single(item => item.Id == "y").Position.Y == 400);
        }

        viewModel.IsRunMode = true;
        var scene = findViewport(window)
            ?? throw new InvalidOperationException("Machine scene was unavailable.");
        var bottomRailY = Math.Clamp(
            scene.ActualHeight * 0.62,
            Math.Min(160, Math.Max(0, scene.ActualHeight - 90)),
            Math.Max(0, scene.ActualHeight - 90));
        Check("xAxisSelectableOnDistinctRail",
            scene.SelectItemAt(new Point(72, bottomRailY - 96))
            && viewModel.Layout.SelectedItem?.Id == "x");
        Check("yAxisSelectableOnDistinctRail",
            scene.SelectItemAt(new Point(72, bottomRailY))
            && viewModel.Layout.SelectedItem?.Id == "y");
        Check("runCommandAvailable", viewModel.RunMultiAxisCommissioningRecipeCommand.CanExecute(null));
        viewModel.RunMultiAxisCommissioningRecipeCommand.Execute(null);
        await WaitForAsync(
            () => viewModel.SceneSnapshots.Latest?.Axes.Any(axis => axis.State == AxisState.Moving) == true,
            "The multi-axis recipe did not start through manual group motion.");
        await WaitForAsync(
            () => viewModel.IsRunning && viewModel.PauseCommand.CanExecute(null),
            "Recipe motion did not enter the running command state.");
        Check("manualOwner", viewModel.SceneSnapshots.Latest!.ControlOwner == SimulationControlOwner.Manual);
        Check("bothAxesMove", viewModel.SceneSnapshots.Latest!.Axes.Count(axis => axis.State == AxisState.Moving) == 2);
        await WaitForAsync(
            () => viewModel.LogMessages.Any(message =>
                message.Contains("Targets: y = 120.000, x = 240.000.", StringComparison.Ordinal)),
            "Ordered recipe move evidence was not published.");
        Check("orderedMoveEvidence", true);

        Check("pauseAvailable", true);
        viewModel.PauseCommand.Execute(null);
        await WaitForAsync(() => !viewModel.IsRunning, "Recipe motion did not pause.");
        var paused = viewModel.SceneSnapshots.Latest!;
        var pausedPositions = paused.Axes.Select(axis => axis.Position).ToArray();
        await Task.Delay(100);
        Check("pauseFreezesTick", viewModel.SceneSnapshots.Latest!.TickIndex == paused.TickIndex);
        Check("pauseFreezesPositions", pausedPositions.SequenceEqual(
            viewModel.SceneSnapshots.Latest.Axes.Select(axis => axis.Position)));

        Check("stepAvailable", viewModel.StepCommand.CanExecute(null));
        viewModel.StepCommand.Execute(null);
        await WaitForAsync(
            () => viewModel.SceneSnapshots.Latest!.TickIndex > paused.TickIndex,
            "Recipe Step did not advance.");
        var stepped = viewModel.SceneSnapshots.Latest!;
        Check("stepAdvancesOneTick", stepped.TickIndex == paused.TickIndex + 1);
        Check("stepAdvancesBothAxes", stepped.Axes.Zip(pausedPositions)
            .All(pair => pair.First.Position > pair.Second));

        await WaitForAsync(
            () => viewModel.StopMultiAxisCommissioningRecipeCommand.CanExecute(null),
            "Recipe group stop was unavailable after Step.");
        Check("stopAvailable", true);
        viewModel.StopMultiAxisCommissioningRecipeCommand.Execute(null);
        await WaitForAsync(
            () => viewModel.SceneSnapshots.Latest!.Axes.All(axis => axis.State == AxisState.Stopped),
            "Recipe group stop did not stop every target axis.");
        var stopped = viewModel.SceneSnapshots.Latest!;
        var stoppedPositions = stopped.Axes.Select(axis => axis.Position).ToArray();
        viewModel.StepCommand.Execute(null);
        await WaitForAsync(
            () => viewModel.SceneSnapshots.Latest!.TickIndex > stopped.TickIndex,
            "Stopped recipe Step did not advance.");
        Check("stopFreezesBothAxes", stoppedPositions.SequenceEqual(
            viewModel.SceneSnapshots.Latest!.Axes.Select(axis => axis.Position)));
        await WaitForAsync(
            () => viewModel.LogMessages.Any(message =>
                message.Contains("Stopped: y = ", StringComparison.Ordinal)
                && message.Contains(", x = ", StringComparison.Ordinal)),
            "Ordered recipe stop evidence was not published.");
        Check("orderedStopEvidence", true);

        viewModel.ResetCommand.Execute(null);
        await WaitForAsync(
            () => viewModel.SceneSnapshots.Latest is
            {
                TickIndex: 0,
                ControlOwner: SimulationControlOwner.Definition
            },
            "Recipe Reset did not restore the authored runtime boundary.");
        var reset = viewModel.SceneSnapshots.Latest!;
        Check("resetRestoresAuthoredHome", reset.Axes.All(axis =>
            axis.State == AxisState.Idle && Math.Abs(axis.Position) <= 1e-9));

        Check("repeatValidationAvailable", viewModel.ValidateMultiAxisCommissioningRecipeCommand.CanExecute(null));
        var mainSnapshotBeforeValidation = viewModel.SceneSnapshots.Latest!;
        viewModel.ValidateMultiAxisCommissioningRecipeCommand.Execute(null);
        await WaitForAsync(
            () => !viewModel.IsCommissioningValidationRunning
                && viewModel.LatestCommissioningResult is not null,
            "Recipe repeat validation did not complete.");
        var validation = viewModel.LatestCommissioningResult!;
        Check("repeatValidationPassed", validation.IsSuccess
            && validation.CompletedRuns == viewModel.MultiAxisCommissioningRecipe.ValidationRepetitions
            && validation.Runs.All(run => run.IsMatch));
        Check("repeatEvidenceValid", validation.HasValidEvidenceHash()
            && validation.Runs.Select(run => run.SnapshotHash).Distinct(StringComparer.Ordinal).Count() == 1
            && validation.Runs.Select(run => run.EventHash).Distinct(StringComparer.Ordinal).Count() == 1);
        Check("historyAppended", viewModel.CommissioningResultHistory.Entries.Length == 1
            && viewModel.SelectedCommissioningHistoryEntry?.Sequence == 1);
        Check("baselineAcceptanceAvailable", viewModel.AcceptCommissioningBaselineCommand.CanExecute(null));
        viewModel.AcceptCommissioningBaselineCommand.Execute(null);
        Check("baselineAccepted", viewModel.AcceptedCommissioningBaseline?.HasValidEvidenceHash() == true
            && viewModel.CommissioningBaselineComparison?.IsMatch == true);
        Check("repeatValidationLeavesMainRuntimeUnchanged",
            viewModel.SceneSnapshots.Latest!.TickIndex == mainSnapshotBeforeValidation.TickIndex
            && viewModel.SceneSnapshots.Latest.ControlOwner == mainSnapshotBeforeValidation.ControlOwner
            && viewModel.SceneSnapshots.Latest.Axes.Select(axis => axis.Position)
                .SequenceEqual(mainSnapshotBeforeValidation.Axes.Select(axis => axis.Position)));

        if (!string.IsNullOrWhiteSpace(savePath))
        {
            var evidencePath = $"{Path.GetFullPath(savePath)}.commissioning-result.json";
            var historyPath = $"{Path.GetFullPath(savePath)}.commissioning-history.json";
            var baselinePath = $"{Path.GetFullPath(savePath)}.commissioning-baseline.json";
            Check("repeatEvidenceSaved", File.Exists(evidencePath));
            Check("historyAndBaselineSaved", File.Exists(historyPath)
                && File.Exists(baselinePath)
                && DeterministicMultiAxisCommissioningResultHistory.LoadFromJson(historyPath)
                    is { Entries.Length: 1 } history
                && history.HasValidEvidenceHash()
                && DeterministicMultiAxisCommissioningBaseline.LoadFromJson(baselinePath)
                    is { } baseline
                && baseline.HasValidEvidenceHash());
            Check("repeatEvidenceRoundTrips",
                DeterministicMultiAxisCommissioningResultPackage.LoadFromJson(evidencePath) is
                { IsSuccess: true } saved
                && saved.HasValidEvidenceHash()
                && string.Equals(saved.EvidenceHash, validation.EvidenceHash, StringComparison.Ordinal));
            Check("repeatReopenAccepted", await viewModel.OpenProjectAsync(savePath));
            Check("repeatEvidenceRestoredWithoutExecution", viewModel.HasRestoredCommissioningResult
                && viewModel.LatestCommissioningResult?.EvidenceHash == validation.EvidenceHash
                && viewModel.IsDesignMode
                && !viewModel.IsRunning
                && viewModel.SceneSnapshots.Latest?.TickIndex == 0
                && viewModel.SceneSnapshots.Latest?.ControlOwner == SimulationControlOwner.Definition
                && viewModel.SceneSnapshots.Latest.Axes.All(axis => axis.State == AxisState.Idle));
            Check("historyAndBaselineRestoredWithoutExecution",
                viewModel.CommissioningResultHistory.Entries.Length == 1
                && viewModel.AcceptedCommissioningBaseline?.HasValidEvidenceHash() == true
                && viewModel.CommissioningBaselineComparison?.IsMatch == true
                && viewModel.SceneSnapshots.Latest?.TickIndex == 0);
            viewModel.MultiAxisCommissioningRecipe.Targets[0].TargetPosition += 1;
            Check("recipeChangeMarksEvidenceStale", viewModel.RejectedStaleCommissioningResult
                && viewModel.SceneSnapshots.Latest?.TickIndex == 0
                && viewModel.SceneSnapshots.Latest.Axes.All(axis => axis.State == AxisState.Idle));
            viewModel.IsRunMode = true;
            viewModel.ValidateMultiAxisCommissioningRecipeCommand.Execute(null);
            await WaitForAsync(
                () => !viewModel.IsCommissioningValidationRunning
                    && viewModel.CommissioningResultHistory.Entries.Length == 2,
                "Changed recipe validation did not complete.");
            var mismatch = viewModel.CommissioningBaselineComparison?.FirstMismatch;
            Check("intentionalChangeFindsFirstMismatch", mismatch is not null);
            Check("intentionalMismatchIsOrderedEvent", mismatch?.EvidenceKind == "Event");
            Check("intentionalMismatchTargetsChangedAxis", mismatch?.TargetId == "y");
            Check("intentionalMismatchHasTick", mismatch?.TickIndex >= 0);
            Check("mismatchNavigationAvailable",
                viewModel.NavigateToCommissioningMismatchCommand.CanExecute(null));
            viewModel.NavigateToCommissioningMismatchCommand.Execute(null);
            Check("yMismatchNavigatesToAxisStage",
                viewModel.Layout.SelectedItem?.Id == "y");

            viewModel.MultiAxisCommissioningRecipe.Targets[0].TargetPosition -= 1;
            viewModel.MultiAxisCommissioningRecipe.Targets[1].TargetPosition += 1;
            var secondAxisHistoryCount = viewModel.CommissioningResultHistory.Entries.Length;
            viewModel.ValidateMultiAxisCommissioningRecipeCommand.Execute(null);
            await WaitForAsync(
                () => !viewModel.IsCommissioningValidationRunning
                    && viewModel.CommissioningResultHistory.Entries.Length > secondAxisHistoryCount,
                "Second-axis recipe validation did not complete.");
            var secondAxisMismatch = viewModel.CommissioningBaselineComparison?.FirstMismatch;
            Check("secondAxisMismatchIsOrderedEvent", secondAxisMismatch?.EvidenceKind == "Event");
            Check("secondAxisMismatchTargetsChangedAxis", secondAxisMismatch?.TargetId == "x");
            Check("secondAxisMismatchHasTick", secondAxisMismatch?.TickIndex >= 0);
            Check("secondAxisMismatchNavigationAvailable",
                viewModel.NavigateToCommissioningMismatchCommand.CanExecute(null));
            viewModel.NavigateToCommissioningMismatchCommand.Execute(null);
            Check("xMismatchNavigatesToAxisStage",
                viewModel.Layout.SelectedItem?.Id == "x");
        }

        return new SmokeMultiAxisCommissioningReport
        {
            Checks = checks,
            Failures = failures
        };
    }
}
