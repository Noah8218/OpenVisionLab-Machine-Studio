using OpenVisionLab;
using OpenVisionLab.Machine.Simulation.Engine;
using OpenVisionLab.MachineStudio.ViewModel;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

public sealed class SimulationCommandTraceViewModelTests
{
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
