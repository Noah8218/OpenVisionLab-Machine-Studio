#nullable enable

using System;
using System.Linq;
using System.Threading.Tasks;
using OpenVisionLab.Machine.Simulation.Camera;
using OpenVisionLab.Machine.Simulation.Engine;
using OpenVisionLab.Machine.Simulation.Snapshots;
using OpenVisionLab.MachineStudio.ViewModel;

namespace OpenVisionLab.MachineStudio.Smoke;

internal static class MachineIntegrationExeSmokeCameraScenario
{
    public static async Task PrepareAsync(MainViewModel viewModel)
    {
        await WaitForAsync(
            () => viewModel.SelectedCameraId is not null
                && viewModel.SelectedCameraRecipe is not null
                && GetCurrentCamera(viewModel) is not null,
            TimeSpan.FromSeconds(30),
            "Machine sample camera was not initialized.");

        if (!viewModel.ResetCommand.CanExecute(null))
        {
            throw new InvalidOperationException("Machine Reset command was not available for camera preparation.");
        }

        viewModel.ResetCommand.Execute(null);
        await WaitForAsync(
            () => GetCurrentCamera(viewModel)?.State == VirtualCameraState.Idle
                && viewModel.SceneSnapshots.Latest?.RunMode == SimulationRunMode.Paused,
            TimeSpan.FromSeconds(30),
            "Machine camera did not reach reset/idle state.");

        if (!viewModel.StartManualCameraControlCommand.CanExecute(null))
        {
            throw new InvalidOperationException("Machine manual camera control command was not available.");
        }

        viewModel.StartManualCameraControlCommand.Execute(null);
        await WaitForAsync(
            () => viewModel.IsRunning
                && viewModel.SceneSnapshots.Latest?.ControlOwner == SimulationControlOwner.Manual,
            TimeSpan.FromSeconds(30),
            "Machine camera did not enter manual control.");

        await WaitForAsync(
            () => viewModel.PauseCommand.CanExecute(null),
            TimeSpan.FromSeconds(30),
            "Machine Pause command was not available after manual start.");

        viewModel.PauseCommand.Execute(null);
        await WaitForAsync(
            () => !viewModel.IsRunning
                && viewModel.SceneSnapshots.Latest?.RunMode == SimulationRunMode.Paused,
            TimeSpan.FromSeconds(30),
            "Machine camera did not pause before trigger.");

        if (!viewModel.TriggerCameraCommand.CanExecute(null))
        {
            throw new InvalidOperationException("Machine Trigger Camera command was not available.");
        }

        viewModel.TriggerCameraCommand.Execute(null);
        await WaitForAsync(
            () => GetCurrentCamera(viewModel)?.State is
                VirtualCameraState.Exposing
                or VirtualCameraState.Transferring
                or VirtualCameraState.FrameReady,
            TimeSpan.FromSeconds(30),
            "Machine camera did not accept the trigger.");

        await WaitForAsync(
            () =>
            {
                if (GetCurrentCamera(viewModel)?.State == VirtualCameraState.FrameReady)
                {
                    return true;
                }

                if (viewModel.StepCommand.CanExecute(null))
                {
                    viewModel.StepCommand.Execute(null);
                }

                return false;
            },
            TimeSpan.FromSeconds(30),
            "Machine camera did not reach FrameReady after deterministic steps.");
    }

    public static VirtualCameraSnapshot? GetCurrentCamera(MainViewModel viewModel)
    {
        var id = viewModel.SelectedCameraId;
        return viewModel.SceneSnapshots.Latest?.Cameras.FirstOrDefault(camera =>
            string.Equals(camera.Id, id, StringComparison.Ordinal));
    }

    private static async Task WaitForAsync(
        Func<bool> condition,
        TimeSpan timeout,
        string failureMessage)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException(failureMessage);
    }
}
