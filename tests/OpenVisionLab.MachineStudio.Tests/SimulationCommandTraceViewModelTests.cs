using System.Collections.Immutable;
using OpenVisionLab;
using OpenVisionLab.Machine.Simulation.Commands;
using OpenVisionLab.Machine.Simulation.Engine;
using OpenVisionLab.Machine.Simulation.Scenarios;
using OpenVisionLab.MachineStudio.ViewModel;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

public sealed class SimulationCommandTraceViewModelTests
{
    [Fact]
    public async Task DisposeSuppressesLateReplayAndDisablesCommands()
    {
        OpenVisionLanguageService.Load();
        var root = Path.Combine(
            "D:\\OpenVisionLab-TestData\\Machine",
            "pl-0228-command-trace-lifetime",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var tracePath = Path.Combine(root, "held-replay.ovsim-trace.json");
        var package = DeterministicSimulationCommandTracePackage.Create(
            TimeSpan.FromMilliseconds(5),
            Array.Empty<DeterministicSimulationCommandTraceEntry>());
        DeterministicSimulationCommandTracePackage.SaveToJson(package, tracePath);

        using var engine = new FixedStepSimulationEngine(
            new SimulationSettings { FixedStep = TimeSpan.FromMilliseconds(5) });
        var replayStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseReplay = new TaskCompletionSource<DeterministicSimulationCommandTraceReplayResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var snapshotApplyCount = 0;
        var parentNotificationCount = 0;
        var viewModel = new SimulationCommandTraceViewModel(
            () => true,
            () => engine,
            _ => snapshotApplyCount++,
            () => { },
            () => parentNotificationCount++,
            _ => { },
            _ => { },
            () => { },
            () => Task.CompletedTask,
            _ => { },
            (_, _) =>
            {
                replayStarted.SetResult(true);
                return releaseReplay.Task;
            });

        try
        {
            Task<bool> replayTask = viewModel.TryReplayAsync(tracePath);
            await replayStarted.Task;

            viewModel.Dispose();
            viewModel.Dispose();
            releaseReplay.SetResult(new(
                false,
                0,
                ImmutableArray<SimulationCommandResult>.Empty,
                null,
                "held"));

            Assert.False(await replayTask);
            Assert.Equal(0, snapshotApplyCount);
            Assert.Equal(0, parentNotificationCount);
            Assert.False(viewModel.IsCaptureStarted);
            Assert.False(viewModel.LastReplaySucceeded);
            Assert.False(viewModel.StartCaptureCommand.CanExecute(null));
            Assert.False(viewModel.ExportCommand.CanExecute(null));
            Assert.False(viewModel.ReplayCommand.CanExecute(null));
        }
        finally
        {
            viewModel.Dispose();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void CaptureAndResetOwnsTraceStateAndNotifiesUnifiedEvidence()
    {
        OpenVisionLanguageService.Load();
        using var engine = new FixedStepSimulationEngine(new SimulationSettings
        {
            FixedStep = TimeSpan.FromMilliseconds(5)
        });
        var unifiedEvidenceClearCount = 0;
        var unifiedEvidenceNotificationCount = 0;
        var viewModel = new SimulationCommandTraceViewModel(
            () => true,
            () => engine,
            _ => { },
            () => unifiedEvidenceClearCount++,
            () => unifiedEvidenceNotificationCount++,
            _ => { },
            _ => { },
            () => { },
            () => Task.CompletedTask,
            _ => { });

        Assert.True(viewModel.CanStartCapture);
        viewModel.StartCaptureCommand.Execute(null);

        Assert.True(viewModel.IsCaptureStarted);
        Assert.Equal(1, unifiedEvidenceClearCount);
        Assert.Equal(1, unifiedEvidenceNotificationCount);
        Assert.False(viewModel.CanExportTrace);

        viewModel.Reset();

        Assert.False(viewModel.IsCaptureStarted);
        Assert.False(viewModel.LastReplaySucceeded);
        Assert.False(viewModel.CanExportTrace);
    }

    [Fact]
    public void RuntimePredicateAndEngineGuardDisableCommands()
    {
        OpenVisionLanguageService.Load();
        var isAllowed = true;
        var viewModel = new SimulationCommandTraceViewModel(
            () => isAllowed,
            () => null,
            _ => { },
            () => { },
            () => { },
            _ => { },
            _ => { },
            () => { },
            () => Task.CompletedTask,
            _ => { });

        Assert.False(viewModel.CanStartCapture);
        Assert.False(viewModel.StartCaptureCommand.CanExecute(null));

        isAllowed = false;
        viewModel.InvalidateCommands();
        Assert.False(viewModel.CanReplayTrace);
        Assert.False(viewModel.ReplayCommand.CanExecute(null));
    }
}
