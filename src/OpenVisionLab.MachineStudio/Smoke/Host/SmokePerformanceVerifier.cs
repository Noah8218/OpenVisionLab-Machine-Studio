using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using OpenVisionLab.MachineStudio.View.Shell;
using OpenVisionLab.MachineStudio.ViewModel;

namespace OpenVisionLab.MachineStudio;

internal sealed class SmokePerformanceReport
{
    public string Schema { get; init; } = "1.0";
    public DateTimeOffset CapturedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public required string WindowTitle { get; init; }
    public required string RequestedSize { get; init; }
    public required int RequestedScalePercent { get; init; }
    public required SmokeMonitorEvidence Monitor { get; init; }
    public required double StartupToIdleMs { get; init; }
    public required IReadOnlyList<double> NavigationTimingsMs { get; init; }
    public required IReadOnlyList<double> SteadyInteractionTimingsMs { get; init; }
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
            NavigationTimingsMs = navigationTimings,
            SteadyInteractionTimingsMs = steadyTimings,
            NavigationMeanMs = CalculateMean(navigationTimings),
            NavigationP95Ms = CalculatePercentile(navigationTimings, 0.95),
            SteadyInteractionMeanMs = CalculateMean(steadyTimings),
            SteadyInteractionP95Ms = CalculatePercentile(steadyTimings, 0.95)
        };
    }

    private static async Task<IReadOnlyList<double>> MeasureNavigationTimingsAsync(
        MainViewModel viewModel,
        Dispatcher dispatcher,
        int sampleCount)
    {
        var samples = new List<double>();
        var navigationPaths = BuildNavigationPaths(viewModel.ProjectTree).ToArray();
        if (navigationPaths.Length == 0)
        {
            return samples;
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
            var selected = SmokeProjectTreeQuery.SelectNode(viewModel.ProjectTree, targetPath);
            if (selected is not null)
            {
                await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            }
            await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            stopwatch.Stop();
            samples.Add(stopwatch.Elapsed.TotalMilliseconds);
        }

        return samples;
    }

    private static async Task<IReadOnlyList<double>> MeasureSteadyInteractionTimingsAsync(
        ShellWindow window,
        MainViewModel viewModel,
        Dispatcher dispatcher,
        int sampleCount)
    {
        var samples = new List<double>();
        var wasRunMode = viewModel.IsRunMode;

        // Warm the initial Design -> Run transition so lazy template creation
        // is not counted as a steady-state mode interaction.
        viewModel.IsRunMode = !wasRunMode;
        await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);

        for (var sample = 0; sample < sampleCount; sample++)
        {
            // Measure both directions as one sample so the metric represents a
            // steady interaction cycle instead of alternating-direction bias.
            var stopwatch = Stopwatch.StartNew();
            viewModel.IsRunMode = wasRunMode;
            await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            viewModel.IsRunMode = !wasRunMode;
            await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            stopwatch.Stop();
            samples.Add(stopwatch.Elapsed.TotalMilliseconds / 2d);
        }

        if (viewModel.IsRunMode != wasRunMode)
        {
            viewModel.IsRunMode = wasRunMode;
            await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        }

        ValidateModeCommandSources(window, viewModel);
        viewModel.IsRunMode = !wasRunMode;
        await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        ValidateModeCommandSources(window, viewModel);
        viewModel.IsRunMode = wasRunMode;
        await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);

        return samples;
    }

    private static void ValidateModeCommandSources(ShellWindow window, MainViewModel viewModel)
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
            var expected = button.Command.CanExecute(button.CommandParameter);
            if (button.IsEnabled != expected)
            {
                throw new InvalidOperationException(
                    $"Visible mode command source '{button.Name}' ({button.Content}) did not refresh " +
                    $"its enabled state: actual={button.IsEnabled}, expected={expected}.");
            }
        }

        if (checkedSourceCount == 0)
        {
            throw new InvalidOperationException("No visible mode command source was available for validation.");
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
