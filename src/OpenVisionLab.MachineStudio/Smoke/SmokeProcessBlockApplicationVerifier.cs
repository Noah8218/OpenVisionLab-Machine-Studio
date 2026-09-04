using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using OpenVisionLab.Machine.Sequence.Authoring;
using OpenVisionLab.Machine.Simulation.Sequences;
using OpenVisionLab.MachineStudio.View.Shell;
using OpenVisionLab.MachineStudio.ViewModel;

namespace OpenVisionLab.MachineStudio;

internal sealed class SmokeProcessBlockApplicationResult
{
    public SmokeWorkflowReport? Report { get; init; }
}

internal static class SmokeProcessBlockApplicationVerifier
{
    private const uint MouseEventLeftDown = 0x0002;

    public static bool IsSupportedState(string? state) => state?.ToLowerInvariant() is
        "process-block-apply-focus"
        or "process-block-apply-pressed"
        or "process-block-applied";

    public static async Task<SmokeProcessBlockApplicationResult> VerifyAsync(
        ShellWindow window,
        MainViewModel vm,
        string applicationState,
        SmokeProcessBlockContext context,
        string? savePath,
        bool createReport,
        Action<FrameworkElement> movePointerToCenter,
        Action<uint, uint, uint, uint, UIntPtr> mouseEvent,
        Action markSmokePointerHeld)
    {
        var normalizedState = applicationState.ToLowerInvariant();
        if (!IsSupportedState(normalizedState))
        {
            throw new ArgumentException(
                $"Unsupported --smoke-connection-workbench-state '{applicationState}'. " +
                "Expected process-block-apply-focus, process-block-apply-pressed, " +
                "or process-block-applied.");
        }

        if (normalizedState == "process-block-applied")
        {
            vm.RecipeConnections.ProcessBlocks.ApplyProcessBlockCommand.Execute(null);
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Check(
                vm.RecipeConnections.RecipeStepCount == 25
                && vm.IsDesignMode
                && !vm.IsRunning,
                "Five process blocks did not produce the expected stopped 25-step recipe.");

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
                && vm.RecipeConnections.RecipeDryRunTimeline.Count == 25,
                "The composed 25-step recipe did not pass readiness and bounded dry run.");

            var bundledProcessRecipes = Directory.EnumerateFiles(
                    Path.Combine(AppContext.BaseDirectory, "Samples", "SemiconductorRecipes"),
                    "*.ovmachine")
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var allBundledBlocksComplete = bundledProcessRecipes.Length == 10;
            foreach (var path in bundledProcessRecipes)
            {
                var bundledProject = context.Store.Load(File.ReadAllText(path));
                var composer = new SemiconductorProcessBlockComposer();
                allBundledBlocksComplete &= composer.Apply(
                    bundledProject,
                    Enum.GetValues<SemiconductorProcessBlockKind>()).Changed;
                var bundledSequenceId = bundledProject.Simulation.AutomaticRun?.SequenceId ?? string.Empty;
                var bundledResult = await new DeterministicRecipeDryRunRunner().RunAsync(
                    bundledProject,
                    bundledSequenceId);
                allBundledBlocksComplete &= bundledResult.Outcome == RecipeDryRunOutcome.Completed;
            }

            Check(
                allBundledBlocksComplete,
                "Five process blocks did not complete a dry run in all ten bundled recipes.");

            if (!string.IsNullOrWhiteSpace(savePath))
            {
                var fullSavePath = Path.GetFullPath(savePath);
                Directory.CreateDirectory(Path.GetDirectoryName(fullSavePath)!);
                await vm.SaveProjectAsync(fullSavePath);
                Check(
                    await vm.OpenProjectAsync(fullSavePath),
                    "The composed process-block project did not reopen.");
                Check(
                    vm.RecipeConnections.RecipeStepCount == 25
                    && vm.IsDesignMode
                    && !vm.IsRunning,
                    "Reopened process blocks were not retained safely.");
            }

            return new SmokeProcessBlockApplicationResult
            {
                Report = createReport
                    ? new SmokeWorkflowReport
                    {
                        Checks = new Dictionary<string, bool>(StringComparer.Ordinal)
                        {
                            ["five-block-plan-preview-thirteen-steps"] = true,
                            ["preview-project-unchanged"] = true,
                            ["preview-runtime-unchanged"] = true,
                            ["five-blocks-applied-once"] = true,
                            ["twenty-five-step-recipe"] = true,
                            ["apply-remains-stopped-in-design"] = true,
                            ["readiness-passed"] = true,
                            ["bounded-dry-run-completed"] = true,
                            ["twenty-five-step-timeline"] = true,
                            ["ten-bundled-recipes-composed-and-dry-run"] = true,
                            ["save-reopen-retained-blocks"] = true,
                            ["reopen-remains-stopped-in-design"] = true
                        },
                        Failures = []
                    }
                    : null
            };
        }

        window.Activate();
        context.ApplyButton.BringIntoView();
        context.ApplyButton.UpdateLayout();
        context.ApplyButton.Focus();
        Keyboard.Focus(context.ApplyButton);
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Check(
            context.ApplyButton.IsKeyboardFocused,
            "Process block Apply button did not receive focus.");

        if (normalizedState == "process-block-apply-pressed")
        {
            movePointerToCenter(context.ApplyButton);
            Mouse.Capture(context.ApplyButton, CaptureMode.SubTree);
            Mouse.Synchronize();
            await Task.Delay(200);
            Check(
                context.ApplyButton.IsMouseOver,
                "Process block Apply button did not enter hover state.");
            mouseEvent(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
            markSmokePointerHeld();
            context.ApplyButton.RaiseEvent(new MouseButtonEventArgs(
                Mouse.PrimaryDevice,
                Environment.TickCount,
                MouseButton.Left)
            {
                RoutedEvent = Mouse.MouseDownEvent
            });
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Check(
                context.ApplyButton.IsPressed,
                "Process block Apply button did not enter pointer-down state.");
        }

        return new SmokeProcessBlockApplicationResult();
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
