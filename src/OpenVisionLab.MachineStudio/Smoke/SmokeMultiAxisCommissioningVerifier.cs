using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Simulation.Axis;
using OpenVisionLab.Machine.Simulation.Commissioning;
using OpenVisionLab.Machine.Simulation.Engine;
using OpenVisionLab.MachineStudio.View.Inspector;
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
    private const uint MouseEventLeftDown = 0x0002;
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

    public static async Task ApplyStateAsync(
        ShellWindow window,
        MainViewModel viewModel,
        string state,
        SmokeUiInteraction interaction)
    {
        var inspector = SmokeVisualTreeQuery.FindVisualDescendant<RightToolRegionView>(window)
            ?? throw new InvalidOperationException("Right inspector was unavailable.");
        switch (state.ToLowerInvariant())
        {
            case "design":
            case "design-focus":
            case "design-popup":
                if (!viewModel.IsDesignMode)
                {
                    throw new InvalidOperationException("Recipe design state requires Design mode.");
                }
                viewModel.ProjectTree.SelectedNode = viewModel.ProjectTree.Roots.Single();
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                inspector.MultiAxisRecipeDesignPanel.BringIntoView();
                if (state.Equals("design-focus", StringComparison.OrdinalIgnoreCase))
                {
                    inspector.MultiAxisRecipeNameTextBox.Text = "Pick position smoke";
                    inspector.MultiAxisRecipeNameTextBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
                    interaction.ActivateWindow();
                    inspector.MultiAxisRecipeNameTextBox.Focus();
                    Keyboard.Focus(inspector.MultiAxisRecipeNameTextBox);
                    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    if (!inspector.MultiAxisRecipeNameTextBox.IsKeyboardFocusWithin
                        || inspector.MultiAxisRecipeNameTextBox.Text != "Pick position smoke")
                    {
                        throw new InvalidOperationException("Recipe name did not render its focused non-empty value.");
                    }
                }
                else if (state.Equals("design-popup", StringComparison.OrdinalIgnoreCase))
                {
                    var comboBox = SmokeVisualTreeQuery.FindVisualDescendant<ComboBox>(
                        inspector.MultiAxisRecipeDesignPanel,
                        candidate => candidate.IsVisible && candidate.IsEnabled)
                        ?? throw new InvalidOperationException("Recipe axis selector was unavailable.");
                    interaction.ActivateWindow();
                    comboBox.Focus();
                    comboBox.IsDropDownOpen = true;
                    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    if (!comboBox.IsDropDownOpen)
                    {
                        throw new InvalidOperationException("Recipe axis selector popup did not open.");
                    }
                }
                break;
            case "ready":
            case "run-hover":
            case "run-pressed":
            case "validation-focus":
            case "validation-hover":
            case "validation-pressed":
            case "validated":
            case "validating":
            case "history-selected":
            case "baseline-pressed":
            case "baseline-accepted":
            case "baseline-mismatch":
            case "baseline-mismatch-x":
                viewModel.IsRunMode = true;
                inspector.RunInspectorScrollViewer.ScrollToTop();
                inspector.MultiAxisRecipeRunPanel.BringIntoView();
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                if (!viewModel.RunMultiAxisCommissioningRecipeCommand.CanExecute(null))
                {
                    throw new InvalidOperationException("Recipe Run was unavailable in its ready state.");
                }
                if (state.Equals("validation-focus", StringComparison.OrdinalIgnoreCase))
                {
                    viewModel.MultiAxisCommissioningRecipe.ValidationRepetitions = 4;
                    interaction.ActivateWindow();
                    inspector.CommissioningValidationRepetitionsTextBox.Focus();
                    Keyboard.Focus(inspector.CommissioningValidationRepetitionsTextBox);
                    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    if (!inspector.CommissioningValidationRepetitionsTextBox.IsKeyboardFocusWithin
                        || inspector.CommissioningValidationRepetitionsTextBox.Text != "4")
                    {
                        throw new InvalidOperationException("Commissioning repetitions did not render its focused value.");
                    }
                }
                else if (state.Equals("validation-hover", StringComparison.OrdinalIgnoreCase)
                    || state.Equals("validation-pressed", StringComparison.OrdinalIgnoreCase))
                {
                    interaction.ActivateWindow();
                    interaction.MovePointerToCenter(inspector.ValidateMultiAxisRecipeButton);
                    await Task.Delay(100);
                    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    if (!inspector.ValidateMultiAxisRecipeButton.IsMouseOver)
                    {
                        throw new InvalidOperationException("Recipe validation did not enter pointer-hover state.");
                    }
                    if (state.Equals("validation-pressed", StringComparison.OrdinalIgnoreCase))
                    {
                        interaction.MouseEvent(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
                        interaction.MarkSmokePointerHeld();
                        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                        if (!inspector.ValidateMultiAxisRecipeButton.IsPressed)
                        {
                            throw new InvalidOperationException("Recipe validation did not enter pointer-down state.");
                        }
                    }
                }
                else if (state.Equals("history-selected", StringComparison.OrdinalIgnoreCase)
                    || state.Equals("baseline-pressed", StringComparison.OrdinalIgnoreCase)
                    || state.Equals("baseline-accepted", StringComparison.OrdinalIgnoreCase)
                    || state.Equals("baseline-mismatch", StringComparison.OrdinalIgnoreCase)
                    || state.Equals("baseline-mismatch-x", StringComparison.OrdinalIgnoreCase))
                {
                    viewModel.MultiAxisCommissioningRecipe.ValidationRepetitions = 2;
                    var initialHistoryCount = viewModel.CommissioningResultHistory.Entries.Length;
                    viewModel.ValidateMultiAxisCommissioningRecipeCommand.Execute(null);
                    for (var attempt = 0; attempt < 200
                         && (viewModel.IsCommissioningValidationRunning
                             || viewModel.CommissioningResultHistory.Entries.Length <= initialHistoryCount);
                         attempt++)
                    {
                        await Task.Delay(25);
                        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    }
                    viewModel.SelectedCommissioningHistoryEntry =
                        viewModel.CommissioningResultHistory.Entries.LastOrDefault();
                    if (viewModel.SelectedCommissioningHistoryEntry is null)
                    {
                        throw new InvalidOperationException("Commissioning history selection was unavailable.");
                    }

                    if (!state.Equals("history-selected", StringComparison.OrdinalIgnoreCase))
                    {
                        if (state.Equals("baseline-pressed", StringComparison.OrdinalIgnoreCase))
                        {
                            inspector.AcceptCommissioningBaselineButton.BringIntoView();
                            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                            interaction.ActivateWindow();
                            interaction.MovePointerToCenter(inspector.AcceptCommissioningBaselineButton);
                            await Task.Delay(100);
                            interaction.MouseEvent(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
                            interaction.MarkSmokePointerHeld();
                            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                            if (!inspector.AcceptCommissioningBaselineButton.IsPressed)
                            {
                                throw new InvalidOperationException("Baseline accept did not enter pointer-down state.");
                            }
                        }
                        else
                        {
                            viewModel.AcceptCommissioningBaselineCommand.Execute(null);
                            if (viewModel.AcceptedCommissioningBaseline is null)
                            {
                                throw new InvalidOperationException("Commissioning baseline was not accepted.");
                            }
                            if (state.Equals("baseline-mismatch", StringComparison.OrdinalIgnoreCase)
                                || state.Equals("baseline-mismatch-x", StringComparison.OrdinalIgnoreCase))
                            {
                                var targetIndex = state.Equals("baseline-mismatch-x", StringComparison.OrdinalIgnoreCase)
                                    ? 1
                                    : 0;
                                viewModel.MultiAxisCommissioningRecipe.Targets[targetIndex].TargetPosition += 1;
                                var changedHistoryCount = viewModel.CommissioningResultHistory.Entries.Length;
                                viewModel.ValidateMultiAxisCommissioningRecipeCommand.Execute(null);
                                for (var attempt = 0; attempt < 200
                                     && (viewModel.IsCommissioningValidationRunning
                                         || viewModel.CommissioningResultHistory.Entries.Length <= changedHistoryCount);
                                     attempt++)
                                {
                                    await Task.Delay(25);
                                    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                                }
                                var expectedAxisId = viewModel.MultiAxisCommissioningRecipe.Targets[targetIndex].AxisId;
                                if (viewModel.CommissioningBaselineComparison?.FirstMismatch?.TargetId != expectedAxisId)
                                {
                                    throw new InvalidOperationException(
                                        $"Commissioning baseline mismatch did not target axis '{expectedAxisId}'.");
                                }
                                viewModel.NavigateToCommissioningMismatchCommand.Execute(null);
                                if (viewModel.Layout.SelectedItem?.Id != expectedAxisId)
                                {
                                    throw new InvalidOperationException(
                                        $"Commissioning mismatch navigation did not select axis '{expectedAxisId}'.");
                                }
                            }
                        }
                    }
                    if (state.Equals("baseline-mismatch", StringComparison.OrdinalIgnoreCase)
                        || state.Equals("baseline-mismatch-x", StringComparison.OrdinalIgnoreCase))
                    {
                        inspector.NavigateCommissioningMismatchButton.BringIntoView();
                    }
                    else if (state.Equals("baseline-accepted", StringComparison.OrdinalIgnoreCase))
                    {
                        inspector.AcceptCommissioningBaselineButton.BringIntoView();
                    }
                    else
                    {
                        inspector.CommissioningResultHistoryList.BringIntoView();
                    }
                }
                else if (state.Equals("validated", StringComparison.OrdinalIgnoreCase)
                    || state.Equals("validating", StringComparison.OrdinalIgnoreCase))
                {
                    viewModel.MultiAxisCommissioningRecipe.ValidationRepetitions =
                        state.Equals("validating", StringComparison.OrdinalIgnoreCase) ? 100 : 3;
                    viewModel.ValidateMultiAxisCommissioningRecipeCommand.Execute(null);
                    for (var attempt = 0; attempt < 200; attempt++)
                    {
                        var ready = state.Equals("validating", StringComparison.OrdinalIgnoreCase)
                            ? viewModel.IsCommissioningValidationRunning
                            : !viewModel.IsCommissioningValidationRunning
                                && viewModel.LatestCommissioningResult is not null;
                        if (ready)
                        {
                            break;
                        }
                        await Task.Delay(25);
                        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    }
                    if (state.Equals("validating", StringComparison.OrdinalIgnoreCase)
                        ? !viewModel.IsCommissioningValidationRunning
                        : viewModel.LatestCommissioningResult is not { IsSuccess: true })
                    {
                        throw new InvalidOperationException("Recipe validation smoke state was not reached.");
                    }
                }
                else if (!state.Equals("ready", StringComparison.OrdinalIgnoreCase))
                {
                    interaction.ActivateWindow();
                    interaction.MovePointerToCenter(inspector.RunMultiAxisRecipeButton);
                    await Task.Delay(100);
                    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    if (!inspector.RunMultiAxisRecipeButton.IsMouseOver)
                    {
                        throw new InvalidOperationException("Recipe Run did not enter pointer-hover state.");
                    }
                    if (state.Equals("run-pressed", StringComparison.OrdinalIgnoreCase))
                    {
                        interaction.MouseEvent(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
                        interaction.MarkSmokePointerHeld();
                        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                        if (!inspector.RunMultiAxisRecipeButton.IsPressed)
                        {
                            throw new InvalidOperationException("Recipe Run did not enter pointer-down state.");
                        }
                    }
                }
                break;
            case "running":
                viewModel.IsRunMode = true;
                viewModel.RunMultiAxisCommissioningRecipeCommand.Execute(null);
                for (var attempt = 0; attempt < 80 &&
                     !viewModel.MultiAxisCommissioningRecipe.Targets.Any(target =>
                         target.RuntimeState == AxisState.Moving); attempt++)
                {
                    await Task.Delay(25);
                    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                }
                inspector.RunInspectorScrollViewer.ScrollToTop();
                break;
            default:
                throw new ArgumentException(
                    $"Unsupported --smoke-multi-axis-recipe-state '{state}'. " +
                    "Expected design, design-focus, design-popup, ready, run-hover, run-pressed, running, " +
                    "validation-focus, validation-hover, validation-pressed, validated, validating, " +
                    "history-selected, baseline-pressed, baseline-accepted, baseline-mismatch, or baseline-mismatch-x.");
        }

        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        await Task.Delay(150);
    }
}
