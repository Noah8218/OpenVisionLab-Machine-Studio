using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using OpenVisionLab;
using OpenVisionLab.Machine.Simulation.Workpieces;
using OpenVisionLab.MachineStudio.View.Scene;
using OpenVisionLab.MachineStudio.View.Shell;
using OpenVisionLab.MachineStudio.ViewModel;
using static OpenVisionLab.MachineStudio.SmokeVisualTreeQuery;

namespace OpenVisionLab.MachineStudio;

internal static class SmokePickAndPlaceStateVerifier
{
    public static async Task ApplyAsync(ShellWindow window, MainViewModel viewModel, string state)
    {
        var normalizedState = state.ToLowerInvariant();
        var expected = normalizedState switch
        {
            "available" => (AxisX: 0d, AxisY: 0d, Gripper: false, WorkpieceX: 240d, WorkpieceY: 120d, WorkpieceState: PickPlaceWorkpieceState.Available),
            "pick-held" => (AxisX: 240d, AxisY: 120d, Gripper: true, WorkpieceX: 240d, WorkpieceY: 120d, WorkpieceState: PickPlaceWorkpieceState.Attached),
            "place-held" => (AxisX: 400d, AxisY: 240d, Gripper: true, WorkpieceX: 400d, WorkpieceY: 240d, WorkpieceState: PickPlaceWorkpieceState.Attached),
            "released" => (AxisX: 400d, AxisY: 240d, Gripper: false, WorkpieceX: 400d, WorkpieceY: 240d, WorkpieceState: PickPlaceWorkpieceState.Placed),
            _ => throw new ArgumentException(
                $"Unsupported --smoke-pick-place-state '{state}'. " +
                "Expected available, pick-held, place-held, or released.")
        };

        if (viewModel.IsRunning)
        {
            viewModel.PauseCommand.Execute(null);
            for (var attempt = 0; attempt < 100 && viewModel.IsRunning; attempt++)
            {
                await Task.Delay(10);
            }
        }

        static double AxisPosition(MainViewModel viewModel, string id) =>
            viewModel.SceneSnapshots.Latest?.Axes.FirstOrDefault(axis =>
                string.Equals(axis.Id, id, StringComparison.Ordinal))?.Position ?? double.NaN;

        static bool? GripperValue(MainViewModel viewModel) =>
            viewModel.SceneSnapshots.Latest?.Signals.FirstOrDefault(signal =>
                string.Equals(signal.Id, "do.gripper", StringComparison.Ordinal))?.Value;

        static PickPlaceWorkpieceSnapshot? Workpiece(MainViewModel viewModel) =>
            viewModel.SceneSnapshots.Latest?.Workpieces.SingleOrDefault();

        if (normalizedState == "available")
        {
            viewModel.ResetCommand.Execute(null);
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        }

        bool IsExpectedState() =>
            Math.Abs(AxisPosition(viewModel, "x") - expected.AxisX) <= 1e-9 &&
            Math.Abs(AxisPosition(viewModel, "y") - expected.AxisY) <= 1e-9 &&
            GripperValue(viewModel) == expected.Gripper &&
            Workpiece(viewModel) is { } workpiece &&
            workpiece.State == expected.WorkpieceState &&
            Math.Abs(workpiece.X - expected.WorkpieceX) <= 1e-9 &&
            Math.Abs(workpiece.Y - expected.WorkpieceY) <= 1e-9;

        for (var step = 0; step < 2_000 && !IsExpectedState(); step++)
        {
            if (!viewModel.StepCommand.CanExecute(null))
            {
                throw new InvalidOperationException(
                    $"Step was unavailable before Pick-and-Place state '{state}'.");
            }

            var beforeTick = viewModel.SceneSnapshots.Latest?.TickIndex ?? -1;
            viewModel.StepCommand.Execute(null);
            for (var attempt = 0;
                 attempt < 100 && viewModel.SceneSnapshots.Latest?.TickIndex <= beforeTick;
                 attempt++)
            {
                await Task.Delay(5);
            }

            if (viewModel.SceneSnapshots.Latest?.TickIndex != beforeTick + 1)
            {
                throw new InvalidOperationException(
                    $"Pick-and-Place Step did not advance exactly one Tick from {beforeTick}.");
            }
        }

        if (!IsExpectedState())
        {
            throw new InvalidOperationException(
                $"Pick-and-Place state '{state}' was not reached within 2,000 fixed Ticks.");
        }

        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        var viewport = FindVisualDescendant<MachineSceneViewport>(window)
            ?? throw new InvalidOperationException("Machine scene viewport was not available.");
        var expectedText = OpenVisionLanguageService.T(
            expected.Gripper ? "Scene.GripperClosed" : "Scene.GripperOpen");
        if (viewport.LastRenderedGripperValue != expected.Gripper ||
            !string.Equals(viewport.LastRenderedGripperText, expectedText, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"The scene did not render the gripper snapshot state '{expectedText}'.");
        }
        var expectedWorkpiece = Workpiece(viewModel)!;
        var expectedWorkpieceText = OpenVisionLanguageService.T(expected.WorkpieceState switch
        {
            PickPlaceWorkpieceState.Attached => "Scene.WorkpieceAttached",
            PickPlaceWorkpieceState.Placed => "Scene.WorkpiecePlaced",
            _ => "Scene.WorkpieceAvailable"
        });
        if (!ReferenceEquals(viewport.LastRenderedWorkpiece, expectedWorkpiece) ||
            !string.Equals(viewport.LastRenderedWorkpieceText, expectedWorkpieceText, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"The scene did not render the workpiece snapshot state '{expectedWorkpieceText}'.");
        }

        var originalLanguage = OpenVisionLanguageService.CurrentLanguage;
        var alternateLanguage = originalLanguage == OpenVisionLanguage.Korean
            ? OpenVisionLanguage.English
            : OpenVisionLanguage.Korean;
        OpenVisionLanguageService.SetLanguage(alternateLanguage, save: false);
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        var alternateText = OpenVisionLanguageService.T(
            expected.Gripper ? "Scene.GripperClosed" : "Scene.GripperOpen");
        if (!string.Equals(viewport.LastRenderedGripperText, alternateText, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The gripper scene label did not follow the language switch.");
        }
        var alternateWorkpieceText = OpenVisionLanguageService.T(expected.WorkpieceState switch
        {
            PickPlaceWorkpieceState.Attached => "Scene.WorkpieceAttached",
            PickPlaceWorkpieceState.Placed => "Scene.WorkpiecePlaced",
            _ => "Scene.WorkpieceAvailable"
        });
        if (!string.Equals(viewport.LastRenderedWorkpieceText, alternateWorkpieceText, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The workpiece scene label did not follow the language switch.");
        }

        OpenVisionLanguageService.SetLanguage(originalLanguage, save: false);
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Console.WriteLine(
            $"Pick-and-Place visual state applied: {state} | " +
            $"x={expected.AxisX:F3}, y={expected.AxisY:F3}, gripper={expected.Gripper}, " +
            $"workpiece={expected.WorkpieceState}@({expected.WorkpieceX:F3},{expected.WorkpieceY:F3})");
    }
}
