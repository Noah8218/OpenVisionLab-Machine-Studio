using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using OpenVisionLab.Machine.Simulation.Faults;
using OpenVisionLab.MachineStudio.ViewModel;

namespace OpenVisionLab.MachineStudio;

internal static class SmokeTestScenarioRuntimeVerifier
{
    internal static async Task VerifyConditionScenarioAsync(
        Window window,
        MainViewModel viewModel,
        bool startSimulation)
    {
        if (!startSimulation)
        {
            throw new ArgumentException(
                "--smoke-test-condition-scenario requires --smoke-start-simulation.");
        }

        viewModel.SimulationWorkspace.IsScheduledFaultEnabled = false;
        for (var attempt = 0; attempt < 20 && !viewModel.StartTestScenarioCommand.CanExecute(null); attempt++)
        {
            await Task.Delay(50);
        }

        if (!viewModel.StartTestScenarioCommand.CanExecute(null))
        {
            throw new InvalidOperationException("Test Scenario start was unavailable during the smoke run.");
        }

        viewModel.StartTestScenarioCommand.Execute(null);
        for (var attempt = 0; attempt < 20 && !viewModel.ConditionScenario.IsActive; attempt++)
        {
            await Task.Delay(50);
        }

        if (!viewModel.ConditionScenario.IsActive)
        {
            throw new InvalidOperationException("Test Scenario did not become active in the runtime snapshot.");
        }

        viewModel.PauseCommand.Execute(null);
        for (var attempt = 0; attempt < 20 && viewModel.IsRunning; attempt++)
        {
            await Task.Delay(50);
        }

        var pausedScenarioTick = viewModel.ConditionScenario.ExecutedTicks;
        viewModel.StepCommand.Execute(null);
        for (var attempt = 0;
             attempt < 20 && viewModel.ConditionScenario.ExecutedTicks <= pausedScenarioTick;
             attempt++)
        {
            await Task.Delay(50);
        }

        if (viewModel.ConditionScenario.ExecutedTicks != pausedScenarioTick + 1)
        {
            throw new InvalidOperationException(
                $"Test Scenario Step advanced {viewModel.ConditionScenario.ExecutedTicks - pausedScenarioTick} ticks; expected exactly one.");
        }

        viewModel.ReplayTestScenarioCommand.Execute(null);
        for (var attempt = 0; attempt < 20 && viewModel.ConditionScenario.ExecutedTicks != 0; attempt++)
        {
            await Task.Delay(50);
        }

        if (!viewModel.ConditionScenario.IsActive || viewModel.ConditionScenario.ExecutedTicks != 0)
        {
            throw new InvalidOperationException("Test Scenario Replay did not restore the initial active state.");
        }

        viewModel.ResetCommand.Execute(null);
        for (var attempt = 0; attempt < 20 && viewModel.ConditionScenario.IsConfigured; attempt++)
        {
            await Task.Delay(50);
        }

        if (!viewModel.ConditionScenario.IsConfigured
            || viewModel.ConditionScenario.IsActive
            || viewModel.ConditionScenario.ExecutedTicks != 0)
        {
            throw new InvalidOperationException(
                "Test Scenario Reset did not restore the declared initial state.");
        }

        Console.WriteLine("Test Scenario smoke passed: start, pause, one-step, replay, reset.");
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
    }

    internal static async Task VerifyAxisFaultScenarioAsync(
        MainViewModel viewModel,
        bool startSimulation,
        string? persistencePath)
    {
        if (!startSimulation)
        {
            throw new ArgumentException(
                "--smoke-test-axis-fault-scenario requires --smoke-start-simulation.");
        }
        if (viewModel.ConditionScenario.IsConfigured)
        {
            throw new InvalidOperationException(
                "Persisted Test Scenario settings started without an explicit action.");
        }

        viewModel.SimulationWorkspace.ScenarioTargetId = "x";
        viewModel.SimulationWorkspace.ScenarioDurationCycles = 2_000;
        viewModel.SimulationWorkspace.IsScheduledFaultEnabled = true;
        viewModel.SimulationWorkspace.ScheduledFaultKind = SimulationFaultKind.AxisMotionBlocked;
        viewModel.SimulationWorkspace.ScheduledFaultTargetId = "x";
        viewModel.SimulationWorkspace.ScheduledFaultInjectTick = 50;
        viewModel.SimulationWorkspace.ScheduledFaultHoldTicks = 3;
        viewModel.SimulationWorkspace.RestartSequenceAfterFault = true;
        var recoverySequenceId = viewModel.RecoverySequences.FirstOrDefault()?.Id
            ?? throw new InvalidOperationException(
                "Axis fault Test Scenario requires an authored recovery sequence.");
        viewModel.SimulationWorkspace.RecoverySequenceId = recoverySequenceId;
        for (var attempt = 0; attempt < 40 && !viewModel.StartTestScenarioCommand.CanExecute(null); attempt++)
        {
            await Task.Delay(50);
        }
        if (!viewModel.StartTestScenarioCommand.CanExecute(null))
        {
            throw new InvalidOperationException("Axis fault Test Scenario start was unavailable.");
        }

        viewModel.StartTestScenarioCommand.Execute(null);
        for (var attempt = 0; attempt < 40 && !viewModel.ConditionScenario.IsActive; attempt++)
        {
            await Task.Delay(25);
        }
        for (var attempt = 0;
             attempt < 40
             && !viewModel.IsRunning;
             attempt++)
        {
            await Task.Delay(25);
        }
        if (!viewModel.PauseCommand.CanExecute(null))
        {
            throw new InvalidOperationException("Axis fault Test Scenario did not enter RealTime mode.");
        }
        viewModel.PauseCommand.Execute(null);
        for (var attempt = 0;
             attempt < 40
             && viewModel.IsRunning;
             attempt++)
        {
            await Task.Delay(25);
        }
        if (!viewModel.ConditionScenario.IsActive
            || viewModel.ConditionScenario.ExecutedTicks > 50
            || viewModel.IsRunning)
        {
            throw new InvalidOperationException(
                "Axis fault Test Scenario could not be paused before its injection tick.");
        }

        while (viewModel.ConditionScenario.ExecutedTicks <= 50)
        {
            var before = viewModel.ConditionScenario.ExecutedTicks;
            viewModel.StepCommand.Execute(null);
            for (var attempt = 0;
                 attempt < 20 && viewModel.ConditionScenario.ExecutedTicks == before;
                 attempt++)
            {
                await Task.Delay(10);
            }
            if (viewModel.ConditionScenario.ExecutedTicks != before + 1)
            {
                throw new InvalidOperationException("Axis fault scenario Step was not exactly one Tick.");
            }
        }

        var faultSnapshot = viewModel.SceneSnapshots.Latest ?? throw new InvalidOperationException(
            "Axis fault scenario did not publish a snapshot.");
        AssertSmoke(
            faultSnapshot.Faults.Any(fault =>
                fault.Kind == SimulationFaultKind.AxisMotionBlocked && fault.TargetId == "x"),
            "Scheduled AxisMotionBlocked fault was not present in the immutable snapshot.");
        long pausedFaultTick = viewModel.ConditionScenario.ExecutedTicks;
        await Task.Delay(50);
        AssertSmoke(
            viewModel.ConditionScenario.ExecutedTicks == pausedFaultTick,
            "Axis fault schedule advanced while paused.");

        while (viewModel.ConditionScenario.ExecutedTicks <= 53)
        {
            var before = viewModel.ConditionScenario.ExecutedTicks;
            viewModel.StepCommand.Execute(null);
            for (var attempt = 0;
                 attempt < 20 && viewModel.ConditionScenario.ExecutedTicks == before;
                 attempt++)
            {
                await Task.Delay(10);
            }
        }
        AssertSmoke(
            (viewModel.SceneSnapshots.Latest?.Faults.Count ?? 0) == 0,
            "Scheduled axis fault did not clear after the authored hold ticks.");

        viewModel.StopTestScenarioCommand.Execute(null);
        for (var attempt = 0;
             attempt < 40 && (viewModel.ConditionScenario.IsActive || viewModel.IsRunning);
             attempt++)
        {
            await Task.Delay(25);
        }
        AssertSmoke(
            !viewModel.ConditionScenario.IsActive && !viewModel.IsRunning,
            "Stopping an axis fault Test Scenario did not stop its owned run.");

        viewModel.ReplayTestScenarioCommand.Execute(null);
        for (var attempt = 0;
             attempt < 40 && (!viewModel.ConditionScenario.IsActive || !viewModel.IsRunning);
             attempt++)
        {
            await Task.Delay(25);
        }
        AssertSmoke(
            viewModel.ConditionScenario.IsActive && viewModel.IsRunning,
            "Axis fault Test Scenario replay did not restore the active initial run.");

        viewModel.ResetCommand.Execute(null);
        for (var attempt = 0; attempt < 40 && viewModel.ConditionScenario.IsActive; attempt++)
        {
            await Task.Delay(25);
        }
        AssertSmoke(
            viewModel.ConditionScenario.IsConfigured
            && !viewModel.ConditionScenario.IsActive
            && viewModel.ConditionScenario.ExecutedTicks == 0
            && (viewModel.SceneSnapshots.Latest?.Faults.Count ?? 0) == 0,
            "Reset did not restore the authored initial scenario and fault state.");
        if (!string.IsNullOrWhiteSpace(persistencePath))
        {
            Directory.CreateDirectory(
                Path.GetDirectoryName(Path.GetFullPath(persistencePath))!);
            await viewModel.SaveProjectAsync(persistencePath);
            AssertSmoke(
                await viewModel.OpenProjectAsync(persistencePath),
                "Saved axis fault Test Scenario project could not be reopened.");
            AssertSmoke(
                viewModel.SimulationWorkspace.IsScheduledFaultEnabled
                && viewModel.SimulationWorkspace.ScheduledFaultKind == SimulationFaultKind.AxisMotionBlocked
                && viewModel.SimulationWorkspace.ScheduledFaultTargetId == "x"
                && viewModel.SimulationWorkspace.ScheduledFaultInjectTick == 50
                && viewModel.SimulationWorkspace.ScheduledFaultHoldTicks == 3
                && viewModel.SimulationWorkspace.RestartSequenceAfterFault
                && viewModel.SimulationWorkspace.RecoverySequenceId == recoverySequenceId
                && !viewModel.ConditionScenario.IsConfigured,
                "Axis fault settings did not round-trip without auto-running.");
        }
        Console.WriteLine(
            "Axis fault Test Scenario smoke passed: explicit start, pause, exact Step, clear, recovery, reset, persistence.");
    }

    private static void AssertSmoke(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
