using OpenVisionLab;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Simulation.Scenarios;
using OpenVisionLab.MachineStudio.ViewModel;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

[Collection(LayoutStartupTestCollection.Name)]
public sealed class UnifiedCommissioningEvidenceWorkflowViewModelTests
{
    private static string SamplePath => Path.Combine(
        AppContext.BaseDirectory,
        "Samples",
        "AutomaticTransferCell.ovmachine");

    private static string EvidenceRoot =>
        "D:\\OpenVisionLab-TestData\\OpenVisionLab-Machine-Studio\\pl-0030-unified-evidence\\viewmodel";

    [Fact]
    public async Task ExportRequiresCompletedBatchAndExplicitTraceCapture()
    {
        var project = new ProjectDocumentStore().Load(File.ReadAllText(SamplePath));
        using var viewModel = new MainViewModel(project);
        await WaitForAsync(() => viewModel.SceneSnapshots.Latest?.Axes.Count > 0);

        viewModel.IsRunMode = true;

        Assert.False(viewModel.CanExportUnifiedCommissioningEvidence);
        Assert.False(viewModel.ExportUnifiedCommissioningEvidenceCommand.CanExecute(null));
        Assert.Contains(
            OpenVisionLanguageService.T("Simulation.UnifiedEvidenceNotReady"),
            viewModel.UnifiedCommissioningEvidenceStatusText,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExportAndImportRestoreArtifactsWithoutExecutionOrProjectMutation()
    {
        var project = new ProjectDocumentStore().Load(File.ReadAllText(SamplePath));
        using var viewModel = new MainViewModel(project);
        await WaitForAsync(() => viewModel.SceneSnapshots.Latest?.Axes.Count > 0);

        viewModel.IsRunMode = true;
        viewModel.SimulationWorkspace.BatchRepetitionCount = 2;
        viewModel.SimulationWorkspace.ScenarioDurationCycles = 1200;
        viewModel.SimulationWorkspace.IsScheduledFaultEnabled = false;
        viewModel.SimulationWorkspace.RequireAutomaticCycleCompleted = false;
        viewModel.SimulationWorkspace.RequireNoActiveFaults = false;
        viewModel.SimulationWorkspace.RequireFinalEquipmentState = false;
        await RunBatchAsync(viewModel);
        viewModel.AcceptBatchBaselineCommand.Execute(null);
        Assert.True(viewModel.HasAcceptedBatchBaseline);

        viewModel.StartSimulationCommandTraceCaptureCommand.Execute(null);
        viewModel.ResetCommand.Execute(null);
        await WaitForAsync(() => viewModel.SimulationCommandTraceEntryCount == 1);
        Assert.True(viewModel.CanExportUnifiedCommissioningEvidence);

        Directory.CreateDirectory(EvidenceRoot);
        var bundlePath = Path.Combine(EvidenceRoot, "viewmodel-roundtrip.ovsim-commissioning.json");
        var beforeTick = viewModel.SceneSnapshots.Latest?.TickIndex;
        var beforeDirty = viewModel.HasUnsavedChanges;
        var beforeAlarmHistoryCount = viewModel.RuntimeDebugger.AlarmHistory.Count;

        Assert.True(viewModel.TryExportUnifiedCommissioningEvidence(bundlePath));
        var json = await File.ReadAllTextAsync(bundlePath);
        var package = DeterministicUnifiedCommissioningEvidencePackage.LoadFromJson(bundlePath);

        Assert.NotNull(package);
        Assert.True(package!.HasValidEvidenceHash());
        Assert.True(package.CanReplayCommandTrace);
        Assert.False(package.ContainsNonReplayableVisionEvidence);
        Assert.DoesNotContain(SamplePath, json, StringComparison.Ordinal);
        Assert.DoesNotContain("RuntimeDebugger", json, StringComparison.Ordinal);
        Assert.Equal(beforeDirty, viewModel.HasUnsavedChanges);
        Assert.Equal(package.EvidenceHash[..12], viewModel.LatestUnifiedCommissioningEvidence?.EvidenceHash[..12]);

        viewModel.ClearBatchBaselineCommand.Execute(null);
        Assert.False(viewModel.HasAcceptedBatchBaseline);
        Assert.True(viewModel.TryImportUnifiedCommissioningEvidence(bundlePath));

        Assert.True(viewModel.HasAcceptedBatchBaseline);
        Assert.Equal(package.EvidenceHash, viewModel.LatestUnifiedCommissioningEvidence?.EvidenceHash);
        Assert.Equal(package.CommandTrace.TraceHash, viewModel.LatestUnifiedCommissioningEvidence?.CommandTrace.TraceHash);
        Assert.False(viewModel.LastSimulationCommandTraceReplaySucceeded);
        Assert.Equal(beforeTick, viewModel.SceneSnapshots.Latest?.TickIndex);
        Assert.Equal(beforeDirty, viewModel.HasUnsavedChanges);
        Assert.Equal(beforeAlarmHistoryCount, viewModel.RuntimeDebugger.AlarmHistory.Count);
        Assert.DoesNotContain(
            Directory.EnumerateFiles(EvidenceRoot, "*.batch-*.json", SearchOption.TopDirectoryOnly),
            path => path.EndsWith(".batch-result.json", StringComparison.Ordinal)
                || path.EndsWith(".batch-baseline.json", StringComparison.Ordinal));
    }

    private static async Task RunBatchAsync(MainViewModel viewModel)
    {
        for (var attempt = 0; attempt < 40 && !viewModel.RunScenarioBatchCommand.CanExecute(null); attempt++)
        {
            await Task.Delay(50);
        }

        Assert.True(
            viewModel.RunScenarioBatchCommand.CanExecute(null),
            $"Batch unavailable: runMode={viewModel.IsRunMode}, running={viewModel.IsRunning}, " +
            $"target={viewModel.SimulationWorkspace.ScenarioTargetId}, " +
            $"faultValid={viewModel.SimulationWorkspace.IsScheduledFaultConfigurationValid}, " +
            $"assertionValid={viewModel.SimulationWorkspace.IsAssertionConfigurationValid}");
        viewModel.RunScenarioBatchCommand.Execute(null);
        await WaitForAsync(
            () => !viewModel.IsBatchRunning
                && viewModel.LatestBatchResult is { IsComplete: true, IsSuccess: true, CompletedRuns: 2 },
            timeoutSeconds: 15);
    }

    private static async Task WaitForAsync(Func<bool> condition, int timeoutSeconds = 5)
    {
        var timeout = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (!condition() && DateTime.UtcNow < timeout)
        {
            await Task.Delay(20);
        }

        Assert.True(condition(), "The deterministic runtime did not become observable.");
    }
}
