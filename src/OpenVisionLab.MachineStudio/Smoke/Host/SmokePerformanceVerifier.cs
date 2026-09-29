using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using OpenVisionLab.MachineStudio.View.Shell;
using OpenVisionLab.MachineStudio.ViewModel;

namespace OpenVisionLab.MachineStudio;

internal sealed class SmokePerformanceReport
{
    public string Schema { get; init; } = "1.0";
    public string ModeTimingContract { get; init; } = "transition-complete-and-idle";
    public DateTimeOffset CapturedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public required string WindowTitle { get; init; }
    public required string RequestedSize { get; init; }
    public required int RequestedScalePercent { get; init; }
    public required SmokeMonitorEvidence Monitor { get; init; }
    public required double StartupToIdleMs { get; init; }
    public required IReadOnlyList<double> NavigationTimingsMs { get; init; }
    public required IReadOnlyList<double> NavigationSelectionTimingsMs { get; init; }
    public required IReadOnlyList<double> NavigationDispatcherTimingsMs { get; init; }
    public required IReadOnlyList<double> SteadyInteractionTimingsMs { get; init; }
    public required IReadOnlyList<double> SteadyModeMutationTimingsMs { get; init; }
    public required IReadOnlyList<double> SteadyDispatcherTimingsMs { get; init; }
    public required IReadOnlyList<double> SteadyDesignModeMutationTimingsMs { get; init; }
    public required IReadOnlyList<double> SteadyDesignModeDispatcherTimingsMs { get; init; }
    public required IReadOnlyList<double> SteadyRunModeMutationTimingsMs { get; init; }
    public required IReadOnlyList<double> SteadyRunModeDispatcherTimingsMs { get; init; }
    public required double NavigationMeanMs { get; init; }
    public required double NavigationP95Ms { get; init; }
    public required double SteadyInteractionMeanMs { get; init; }
    public required double SteadyInteractionP95Ms { get; init; }

    public void Save(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        };
        File.WriteAllText(fullPath, JsonSerializer.Serialize(this, options));
    }
}

internal static class SmokePerformanceVerifier
{
    public static async Task<SmokePerformanceReport> MeasureAsync(
        ShellWindow window,
        MainViewModel viewModel,
        string requestedSize,
        int requestedScalePercent,
        double startupToIdleMs,
        int navigationSampleCount,
        int steadySampleCount)
    {
        var dispatcher = window.Dispatcher;
        var navigationTimings = await MeasureNavigationTimingsAsync(
            viewModel,
            dispatcher,
            Math.Max(1, navigationSampleCount));
        var steadyTimings = await MeasureSteadyInteractionTimingsAsync(
            window,
            viewModel,
            dispatcher,
            Math.Max(1, steadySampleCount));

        return new SmokePerformanceReport
        {
            WindowTitle = window.Title,
            RequestedSize = requestedSize,
            RequestedScalePercent = requestedScalePercent,
            Monitor = SmokeDpiTestHook.CaptureMonitorEvidence(window),
            StartupToIdleMs = startupToIdleMs,
            NavigationTimingsMs = navigationTimings.Total,
            NavigationSelectionTimingsMs = navigationTimings.Synchronous,
            NavigationDispatcherTimingsMs = navigationTimings.Dispatcher,
            SteadyInteractionTimingsMs = steadyTimings.Total,
            SteadyModeMutationTimingsMs = steadyTimings.Synchronous,
            SteadyDispatcherTimingsMs = steadyTimings.Dispatcher,
            SteadyDesignModeMutationTimingsMs = steadyTimings.DesignModeMutation,
            SteadyDesignModeDispatcherTimingsMs = steadyTimings.DesignModeDispatcher,
            SteadyRunModeMutationTimingsMs = steadyTimings.RunModeMutation,
            SteadyRunModeDispatcherTimingsMs = steadyTimings.RunModeDispatcher,
            NavigationMeanMs = CalculateMean(navigationTimings.Total),
            NavigationP95Ms = CalculatePercentile(navigationTimings.Total, 0.95),
            SteadyInteractionMeanMs = CalculateMean(steadyTimings.Total),
            SteadyInteractionP95Ms = CalculatePercentile(steadyTimings.Total, 0.95)
        };
    }

    private static async Task<(
        IReadOnlyList<double> Total,
        IReadOnlyList<double> Synchronous,
        IReadOnlyList<double> Dispatcher)> MeasureNavigationTimingsAsync(
        MainViewModel viewModel,
        Dispatcher dispatcher,
        int sampleCount)
    {
        var samples = new List<double>();
        var synchronousSamples = new List<double>();
        var dispatcherSamples = new List<double>();
        var navigationPaths = BuildNavigationPaths(viewModel.ProjectTree).ToArray();
        if (navigationPaths.Length == 0)
        {
            return (samples, synchronousSamples, dispatcherSamples);
        }

        var firstPath = navigationPaths[0];
        var secondPath = navigationPaths[Math.Min(1, navigationPaths.Length - 1)];

        // Warm the first tree-selection transition so lazy WPF template creation
        // is not mixed into the repeated navigation measurement.
        SmokeProjectTreeQuery.SelectNode(viewModel.ProjectTree, firstPath);
        await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        SmokeProjectTreeQuery.SelectNode(viewModel.ProjectTree, secondPath);
        await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);

        for (var sample = 0; sample < sampleCount; sample++)
        {
            var targetPath = sample % 2 == 0 ? firstPath : secondPath;
            var stopwatch = Stopwatch.StartNew();
            var synchronousStartedAt = Stopwatch.GetTimestamp();
            var selected = SmokeProjectTreeQuery.SelectNode(viewModel.ProjectTree, targetPath);
            var synchronousCompletedAt = Stopwatch.GetTimestamp();
            if (selected is not null)
            {
                await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            }
            await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var completedAt = Stopwatch.GetTimestamp();
            stopwatch.Stop();
            samples.Add(stopwatch.Elapsed.TotalMilliseconds);
            synchronousSamples.Add(Stopwatch.GetElapsedTime(synchronousStartedAt, synchronousCompletedAt).TotalMilliseconds);
            dispatcherSamples.Add(Stopwatch.GetElapsedTime(synchronousCompletedAt, completedAt).TotalMilliseconds);
        }

        return (samples, synchronousSamples, dispatcherSamples);
    }

    private static async Task<(
        IReadOnlyList<double> Total,
        IReadOnlyList<double> Synchronous,
        IReadOnlyList<double> Dispatcher,
        IReadOnlyList<double> DesignModeMutation,
        IReadOnlyList<double> DesignModeDispatcher,
        IReadOnlyList<double> RunModeMutation,
        IReadOnlyList<double> RunModeDispatcher)> MeasureSteadyInteractionTimingsAsync(
        ShellWindow window,
        MainViewModel viewModel,
        Dispatcher dispatcher,
        int sampleCount)
    {
        var samples = new List<double>();
        var synchronousSamples = new List<double>();
        var dispatcherSamples = new List<double>();
        var designModeMutationSamples = new List<double>();
        var designModeDispatcherSamples = new List<double>();
        var runModeMutationSamples = new List<double>();
        var runModeDispatcherSamples = new List<double>();
        var wasRunMode = viewModel.IsRunMode;

        // Warm the initial Design -> Run transition so lazy template creation
        // is not counted as a steady-state mode interaction.
        viewModel.IsRunMode = !wasRunMode;
        await WaitForModeAsync(viewModel, dispatcher, !wasRunMode);

        for (var sample = 0; sample < sampleCount; sample++)
        {
            // Measure both directions as one sample so the metric represents a
            // steady interaction cycle instead of alternating-direction bias.
            var stopwatch = Stopwatch.StartNew();
            var firstMutationStartedAt = Stopwatch.GetTimestamp();
            viewModel.IsRunMode = wasRunMode;
            var firstMutationCompletedAt = Stopwatch.GetTimestamp();
            await WaitForModeAsync(viewModel, dispatcher, wasRunMode);
            var firstDispatcherCompletedAt = Stopwatch.GetTimestamp();
            viewModel.IsRunMode = !wasRunMode;
            var secondMutationCompletedAt = Stopwatch.GetTimestamp();
            await WaitForModeAsync(viewModel, dispatcher, !wasRunMode);
            var completedAt = Stopwatch.GetTimestamp();
            stopwatch.Stop();
            var firstMutationMs = Stopwatch.GetElapsedTime(firstMutationStartedAt, firstMutationCompletedAt).TotalMilliseconds;
            var firstDispatcherMs = Stopwatch.GetElapsedTime(firstMutationCompletedAt, firstDispatcherCompletedAt).TotalMilliseconds;
            var secondMutationMs = Stopwatch.GetElapsedTime(firstDispatcherCompletedAt, secondMutationCompletedAt).TotalMilliseconds;
            var secondDispatcherMs = Stopwatch.GetElapsedTime(secondMutationCompletedAt, completedAt).TotalMilliseconds;
            samples.Add(stopwatch.Elapsed.TotalMilliseconds / 2d);
            synchronousSamples.Add((firstMutationMs + secondMutationMs) / 2d);
            dispatcherSamples.Add((firstDispatcherMs + secondDispatcherMs) / 2d);
            if (wasRunMode)
            {
                runModeMutationSamples.Add(firstMutationMs);
                runModeDispatcherSamples.Add(firstDispatcherMs);
                designModeMutationSamples.Add(secondMutationMs);
                designModeDispatcherSamples.Add(secondDispatcherMs);
            }
            else
            {
                designModeMutationSamples.Add(firstMutationMs);
                designModeDispatcherSamples.Add(firstDispatcherMs);
                runModeMutationSamples.Add(secondMutationMs);
                runModeDispatcherSamples.Add(secondDispatcherMs);
            }
        }

        if (viewModel.IsRunMode != wasRunMode)
        {
            viewModel.IsRunMode = wasRunMode;
            await WaitForModeAsync(viewModel, dispatcher, wasRunMode);
        }

        ValidateModeCommandSources(window, viewModel);
        viewModel.IsRunMode = !wasRunMode;
        await WaitForModeAsync(viewModel, dispatcher, !wasRunMode);
        ValidateModeCommandSources(window, viewModel);
        viewModel.IsRunMode = wasRunMode;
        await WaitForModeAsync(viewModel, dispatcher, wasRunMode);

        return (
            samples,
            synchronousSamples,
            dispatcherSamples,
            designModeMutationSamples,
            designModeDispatcherSamples,
            runModeMutationSamples,
            runModeDispatcherSamples);
    }

    internal static async Task WaitForModeAsync(MainViewModel viewModel, Dispatcher dispatcher, bool expectedRunMode)
    {
        var timeout = Stopwatch.StartNew();
        while (viewModel.IsModeTransitioning)
        {
            if (timeout.Elapsed > TimeSpan.FromSeconds(10))
                throw new TimeoutException("Workspace mode transition did not finish within 10 seconds.");
            await Task.Delay(1);
        }

        await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        if (viewModel.IsModeTransitioning || viewModel.IsRunMode != expectedRunMode)
            throw new InvalidOperationException("Workspace mode transition did not reach the requested mode.");
    }

    internal static void ValidateModeCommandSources(Window window, MainViewModel viewModel)
    {
        _ = viewModel.RunCommand;
        _ = viewModel.PauseCommand;
        _ = viewModel.StepCommand;
        _ = viewModel.ResetCommand;
        _ = viewModel.AddLayoutComponentCommand;
        var checkedSourceCount = 0;
        foreach (var button in SmokeVisualTreeQuery.FindVisualDescendants<Button>(window))
        {
            if (!button.IsVisible
                || button.Command is not (RelayCommand or AsyncRelayCommand))
            {
                continue;
            }

            checkedSourceCount++;
            var enabledValueSource = DependencyPropertyHelper.GetValueSource(button, UIElement.IsEnabledProperty);
            if (BindingOperations.IsDataBound(button, UIElement.IsEnabledProperty)
                || enabledValueSource.BaseValueSource != BaseValueSource.Default
                || !AreAncestorsEnabled(button))
            {
                continue;
            }

            var expected = button.Command.CanExecute(button.CommandParameter);
            if (button.IsEnabled != expected)
            {
                var label = button.Content is TextBlock textBlock ? textBlock.Text : button.Content?.ToString();
                var automationName = System.Windows.Automation.AutomationProperties.GetName(button);
                throw new InvalidOperationException(
                    $"Visible mode command source '{button.Name}' (automation='{automationName}', " +
                    $"label='{label}', command={button.Command.GetType().Name}, " +
                    $"enabledSource={enabledValueSource.BaseValueSource}) did not refresh its enabled state: " +
                    $"actual={button.IsEnabled}, expected={expected}.");
            }
        }

        if (checkedSourceCount == 0)
        {
            throw new InvalidOperationException("No visible mode command source was available for validation.");
        }
    }

    private static bool AreAncestorsEnabled(DependencyObject element)
    {
        var visited = new HashSet<DependencyObject>();
        var pending = new Stack<DependencyObject>();
        AddParents(element, pending);
        while (pending.Count > 0)
        {
            var ancestor = pending.Pop();
            if (!visited.Add(ancestor))
            {
                continue;
            }

            if ((ancestor is UIElement uiElement && !uiElement.IsEnabled)
                || (ancestor is ContentElement contentElement && !contentElement.IsEnabled))
            {
                return false;
            }

            AddParents(ancestor, pending);
        }

        return true;
    }

    private static void AddParents(DependencyObject element, Stack<DependencyObject> pending)
    {
        if (element is Visual or Visual3D)
        {
            var visualParent = VisualTreeHelper.GetParent(element);
            if (visualParent is not null)
            {
                pending.Push(visualParent);
            }
        }

        var logicalParent = LogicalTreeHelper.GetParent(element);
        if (logicalParent is not null)
        {
            pending.Push(logicalParent);
        }
    }

    private static IEnumerable<string> BuildNavigationPaths(ProjectTreeViewModel projectTree)
    {
        foreach (var root in projectTree.Roots)
        {
            foreach (var path in BuildNavigationPathsFromNode(root, root.Id))
            {
                yield return path;
            }
        }
    }

    private static IEnumerable<string> BuildNavigationPathsFromNode(TreeNodeViewModel node, string pathPrefix)
    {
        yield return pathPrefix;

        foreach (var child in node.Children)
        {
            foreach (var nested in BuildNavigationPathsFromNode(child, $"{pathPrefix}/{child.Id}"))
            {
                yield return nested;
            }
        }
    }

    private static double CalculateMean(IReadOnlyList<double> values)
    {
        if (values.Count == 0)
        {
            return 0;
        }

        return Math.Round(values.Average(), 3);
    }

    private static double CalculatePercentile(IReadOnlyList<double> values, double percentile)
    {
        if (values.Count == 0)
        {
            return 0;
        }

        var sorted = values.OrderBy(value => value).ToArray();
        var safePercentile = Math.Clamp(percentile, 0, 1);
        var index = (int)Math.Ceiling(safePercentile * sorted.Length) - 1;
        var clampedIndex = Math.Clamp(index, 0, sorted.Length - 1);
        return Math.Round(sorted[clampedIndex], 3);
    }

}
