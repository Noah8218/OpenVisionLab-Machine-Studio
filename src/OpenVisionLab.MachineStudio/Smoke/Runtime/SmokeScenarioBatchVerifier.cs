using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Controls;
using OpenVisionLab.MachineStudio.View.Shell;
using OpenVisionLab.MachineStudio.ViewModel;

namespace OpenVisionLab.MachineStudio;

internal static class SmokeScenarioBatchVerifier
{
    public static async Task VerifyAsync(
        ShellWindow window,
        MainViewModel vm,
        string? projectPath,
        string? scenarioEvidenceExchangePath,
        string scenarioEvidenceExchangeState,
        string? scenarioReportPath,
        string scenarioReportState,
        string? unifiedCommissioningEvidencePath,
        string unifiedCommissioningEvidenceState,
        SmokeWindowCapture windowCapture,
        string? screenshotPath,
        SmokeUiInteraction uiInteraction)
    {
        vm.SimulationWorkspace.BatchRepetitionCount = 3;
        vm.SimulationWorkspace.ScenarioDurationCycles = 100_000;
        for (var attempt = 0; attempt < 40 && !vm.RunScenarioBatchCommand.CanExecute(null); attempt++)
        {
            await Task.Delay(50);
        }

        if (!vm.RunScenarioBatchCommand.CanExecute(null))
        {
            throw new InvalidOperationException("Repeat validation was unavailable during the smoke run.");
        }

        vm.RunScenarioBatchCommand.Execute(null);
        for (var attempt = 0; attempt < 40 && !vm.IsBatchRunning; attempt++)
        {
            await Task.Delay(25);
        }

        if (!vm.IsBatchRunning || !vm.CancelScenarioBatchCommand.CanExecute(null))
        {
            throw new InvalidOperationException("Repeat validation did not enter a cancellable state.");
        }

        var cancellationScreenshotPath = string.IsNullOrWhiteSpace(screenshotPath)
            ? null
            : Path.Combine(
                Path.GetDirectoryName(Path.GetFullPath(screenshotPath)) ?? AppContext.BaseDirectory,
                "scenario-batch-cancel-requested.png");
        var cancellationStatusText = cancellationScreenshotPath is null
            ? null
            : uiInteraction.FindTextBlock(
                window,
                candidate => string.Equals(candidate.Name, "ScenarioBatchStatusText", StringComparison.Ordinal))
                ?? throw new InvalidOperationException("Repeat validation status text was not rendered.");
        var cancellationScreenshotCaptured = false;
        string? cancellationScreenshotError = null;
        PropertyChangedEventHandler? cancellationHandler = null;
        if (cancellationStatusText is not null)
        {
            cancellationHandler = (_, args) =>
            {
                if (cancellationScreenshotCaptured
                    || !vm.IsBatchCancellationRequested
                    || !string.Equals(
                        args.PropertyName,
                        nameof(MainViewModel.IsBatchCancellationRequested),
                        StringComparison.Ordinal))
                {
                    return;
                }

                try
                {
                    cancellationStatusText.GetBindingExpression(TextBlock.TextProperty)?.UpdateTarget();
                    if (!string.Equals(cancellationStatusText.Text, vm.BatchStatusText, StringComparison.Ordinal)
                        || vm.CancelScenarioBatchCommand.CanExecute(null))
                    {
                        cancellationScreenshotError =
                            $"Repeat validation cancellation request was not rendered before confirmation "
                            + $"(ui='{cancellationStatusText.Text}', vm='{vm.BatchStatusText}', "
                            + $"cancelEnabled={vm.CancelScenarioBatchCommand.CanExecute(null)}).";
                        return;
                    }

                    cancellationStatusText.BringIntoView();
                    cancellationStatusText.UpdateLayout();
                    windowCapture.Capture(window, cancellationScreenshotPath!);
                    cancellationScreenshotCaptured = true;
                }
                catch (Exception exception)
                {
                    cancellationScreenshotError = exception.Message;
                }
            };
            vm.PropertyChanged += cancellationHandler;
        }

        try
        {
            vm.CancelScenarioBatchCommand.Execute(null);
        }
        finally
        {
            if (cancellationHandler is not null)
            {
                vm.PropertyChanged -= cancellationHandler;
            }
        }

        if (cancellationScreenshotError is not null)
        {
            throw new InvalidOperationException(cancellationScreenshotError);
        }
        if (cancellationScreenshotPath is not null && !cancellationScreenshotCaptured)
        {
            throw new InvalidOperationException("Repeat validation cancellation request was not rendered.");
        }
        for (var attempt = 0; attempt < 80 && vm.IsBatchRunning; attempt++)
        {
            await Task.Delay(25);
        }

        if (vm.IsBatchRunning || !vm.BatchWasCanceled || vm.IsBatchCancellationRequested)
        {
            throw new InvalidOperationException("Repeat validation cancellation did not complete.");
        }

        vm.SimulationWorkspace.BatchRepetitionCount = 2;
        vm.SimulationWorkspace.ScenarioDurationCycles = 1200;
        for (var attempt = 0; attempt < 40 && !vm.RunScenarioBatchCommand.CanExecute(null); attempt++)
        {
            await Task.Delay(50);
        }

        vm.RunScenarioBatchCommand.Execute(null);
        for (var attempt = 0;
             attempt < 120 && (vm.IsBatchRunning || vm.LatestBatchResult is null);
             attempt++)
        {
            await Task.Delay(25);
        }

        if (vm.LatestBatchResult is not { IsComplete: true, IsSuccess: true, CompletedRuns: 2 })
        {
            throw new InvalidOperationException("Repeat validation did not produce two identical runs.");
        }

        var outcomes = vm.LatestBatchResult.Runs.Last().Result.AssertionOutcomes;
        if (outcomes.Length != 3
            || outcomes.Any(outcome => !outcome.IsPassed)
            || vm.BatchAssertionOutcomes.Count != 3)
        {
            throw new InvalidOperationException("Repeat validation did not expose three passing acceptance results.");
        }

        vm.AcceptBatchBaselineCommand.Execute(null);
        if (!vm.HasAcceptedBatchBaseline)
        {
            throw new InvalidOperationException("Repeat validation baseline was not accepted.");
        }

        if (!string.IsNullOrWhiteSpace(scenarioEvidenceExchangePath))
        {
            await SmokeRuntimeEvidenceVerifier.VerifyScenarioEvidenceExchangeAsync(
                window,
                vm,
                scenarioEvidenceExchangePath,
                scenarioEvidenceExchangeState,
                projectPath,
                uiInteraction);
        }

        if (!string.IsNullOrWhiteSpace(scenarioReportPath))
        {
            await SmokeRuntimeEvidenceVerifier.VerifyScenarioBatchReportAsync(
                window,
                vm,
                scenarioReportPath,
                scenarioReportState,
                projectPath,
                uiInteraction);
        }

        if (!string.IsNullOrWhiteSpace(unifiedCommissioningEvidencePath))
        {
            await SmokeRuntimeEvidenceVerifier.VerifyUnifiedCommissioningEvidenceAsync(
                window,
                vm,
                unifiedCommissioningEvidencePath,
                unifiedCommissioningEvidenceState,
                projectPath,
                uiInteraction);
        }

        var previousBatch = vm.LatestBatchResult;
        for (var attempt = 0; attempt < 40 && !vm.RunScenarioBatchCommand.CanExecute(null); attempt++)
        {
            await Task.Delay(50);
        }

        vm.RunScenarioBatchCommand.Execute(null);
        for (var attempt = 0;
             attempt < 120 && (vm.IsBatchRunning || ReferenceEquals(vm.LatestBatchResult, previousBatch));
             attempt++)
        {
            await Task.Delay(25);
        }

        if (ReferenceEquals(vm.LatestBatchResult, previousBatch)
            || vm.LatestBatchResult is not { IsComplete: true, IsSuccess: true, CompletedRuns: 2 })
        {
            throw new InvalidOperationException("Accepted baseline comparison did not pass.");
        }

        previousBatch = vm.LatestBatchResult;
        vm.SimulationWorkspace.ScenarioSeed++;
        for (var attempt = 0; attempt < 40 && !vm.RunScenarioBatchCommand.CanExecute(null); attempt++)
        {
            await Task.Delay(50);
        }

        vm.RunScenarioBatchCommand.Execute(null);
        for (var attempt = 0;
             attempt < 120 && (vm.IsBatchRunning || ReferenceEquals(vm.LatestBatchResult, previousBatch));
             attempt++)
        {
            await Task.Delay(25);
        }

        var mismatch = vm.LatestBatchResult?.FirstMismatch;
        if (ReferenceEquals(vm.LatestBatchResult, previousBatch)
            || vm.LatestBatchResult is not { IsComplete: true, IsSuccess: false }
            || mismatch is null
            || !string.Equals(
                mismatch.TargetId,
                vm.SimulationWorkspace.ScenarioTargetId,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Changed repeat validation did not expose its first mismatch.");
        }

        vm.NavigateToBatchMismatchCommand.Execute(null);
        if (!string.Equals(vm.Layout.SelectedItem?.Id, mismatch.TargetId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("First mismatch navigation did not select its equipment target.");
        }

        vm.ClearBatchBaselineCommand.Execute(null);
        if (vm.HasAcceptedBatchBaseline)
        {
            throw new InvalidOperationException("Accepted baseline reset did not clear the baseline.");
        }

        previousBatch = vm.LatestBatchResult;
        for (var attempt = 0; attempt < 40 && !vm.RunScenarioBatchCommand.CanExecute(null); attempt++)
        {
            await Task.Delay(50);
        }

        vm.RunScenarioBatchCommand.Execute(null);
        for (var attempt = 0;
             attempt < 120 && (vm.IsBatchRunning || ReferenceEquals(vm.LatestBatchResult, previousBatch));
             attempt++)
        {
            await Task.Delay(25);
        }

        if (ReferenceEquals(vm.LatestBatchResult, previousBatch)
            || vm.LatestBatchResult is not { IsComplete: true, IsSuccess: true, CompletedRuns: 2 })
        {
            throw new InvalidOperationException("Changed scenario could not establish a new baseline candidate.");
        }

        Console.WriteLine(
            "Repeat validation smoke passed: cancel, baseline replay/reset, mismatch navigation.");
    }
}
