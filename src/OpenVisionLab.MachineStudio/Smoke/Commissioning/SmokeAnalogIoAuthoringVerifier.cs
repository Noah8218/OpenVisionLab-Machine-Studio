using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using OpenVisionLab.MachineStudio.Model;
using OpenVisionLab.MachineStudio.View.Inspector;
using OpenVisionLab.MachineStudio.View.Scene;
using OpenVisionLab.MachineStudio.View.Shell;
using OpenVisionLab.MachineStudio.ViewModel;

namespace OpenVisionLab.MachineStudio;

internal sealed class SmokeAnalogIoAuthoringReport
{
    public string Schema { get; init; } = "1.0";
    public DateTimeOffset CapturedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public required IReadOnlyDictionary<string, bool> Checks { get; init; }
    public required IReadOnlyList<string> Failures { get; init; }
    public SmokeMonitorEvidence? Monitor { get; init; }
    public bool IsValid => Failures.Count == 0 && Checks.Values.All(value => value);

    public void Save(string path)
    {
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(
            fullPath,
            JsonSerializer.Serialize(this, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = true
            }));
    }
}

internal static class SmokeAnalogIoAuthoringVerifier
{
    public static async Task<SmokeAnalogIoAuthoringReport> VerifyAsync(
        ShellWindow window,
        MainViewModel viewModel,
        string state,
        string? savePath,
        string? screenshotPath,
        Func<DependencyObject, RightToolRegionView?> findInspector,
        Func<DependencyObject, SceneDocumentView?> findScene,
        Action<Window, string> captureScreenshot)
    {
        var checks = new Dictionary<string, bool>(StringComparer.Ordinal);
        var failures = new List<string>();
        var exitPlaybackAfterScreenshot = false;
        void Check(string name, bool passed)
        {
            checks[name] = passed;
            if (!passed)
            {
                failures.Add(name);
            }
        }

        var root = viewModel.ProjectTree.Roots.Single();
        var channels = root.Children.Single(node => node.Kind == TreeNodeKind.Channels);
        var targetId = state.Equals("output", StringComparison.OrdinalIgnoreCase)
            ? "ao.setpoint"
            : state.Equals("digital", StringComparison.OrdinalIgnoreCase)
                ? "di.ready"
                : "ai.pressure";
        var target = channels.Children.SingleOrDefault(node =>
            string.Equals(node.Id, targetId, StringComparison.Ordinal));
        if (target is null)
        {
            throw new InvalidOperationException(
                $"Analog authoring smoke target '{targetId}' was not found.");
        }

        viewModel.ProjectTree.SelectedNode = target;
        target.IsSelected = true;
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);

        var inspector = findInspector(window)
            ?? throw new InvalidOperationException("The right inspector was not available.");
        var valueTextBox = inspector.AnalogInitialValueTextBox;
        Check(
            "design-mode",
            viewModel.IsDesignMode && !viewModel.IsRunning);

        if (state.Equals("digital", StringComparison.OrdinalIgnoreCase))
        {
            Check("digital-channel-outside-analog-editor", !viewModel.HasSelectedAnalogChannel);
            Check("analog-panel-hidden-for-digital", !valueTextBox.IsVisible);
        }
        else
        {
            Check("analog-selection-routed", viewModel.HasSelectedAnalogChannel);
            Check("analog-panel-visible", valueTextBox.IsVisible);
            Check("analog-field-enabled", valueTextBox.IsEnabled);
            Check("initial-value-projected", valueTextBox.Text == viewModel.AnalogIoAuthoring?.InitialValueText);

            var tickBeforeEdit = viewModel.SceneSnapshots.Latest?.TickIndex;
            Keyboard.Focus(valueTextBox);
            valueTextBox.SelectAll();
            Check("initial-value-field-focused", valueTextBox.IsKeyboardFocusWithin);

            if (state.Equals("playback-lock", StringComparison.OrdinalIgnoreCase))
            {
                var scene = findScene(window)
                    ?? throw new InvalidOperationException("The scene document was not available.");
                var playbackSnapshot = viewModel.SceneSnapshots.Latest
                    ?? throw new InvalidOperationException("The initial scene snapshot was not available.");
                var playbackStep = new RecipeDryRunStepPresentation(
                    "smoke-sequence",
                    "analog-playback-lock",
                    null,
                    "#1",
                    "Analog playback lock",
                    "tick 0",
                    false,
                    false,
                    false,
                    string.Empty,
                    playbackSnapshot);

                viewModel.DryRunPlayback.Show(playbackStep, [playbackStep]);
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);

                Check("dry-run-playback-active", viewModel.IsDryRunPlaybackActive);
                Check("scene-editing-locked-during-playback", !viewModel.IsSceneEditable);
                Check("analog-field-disabled-during-playback", !valueTextBox.IsEnabled);
                Check("scene-identity-hidden-during-playback", !scene.SceneIdentityCard.IsVisible);
                Check("playback-does-not-dirty-project", !viewModel.HasUnsavedChanges);
                Check("playback-does-not-run", !viewModel.IsRunning);
                Check("playback-keeps-live-tick", viewModel.SceneSnapshots.Latest?.TickIndex == tickBeforeEdit);
                exitPlaybackAfterScreenshot = true;
            }
            else if (state.Equals("invalid", StringComparison.OrdinalIgnoreCase))
            {
                valueTextBox.Text = "NaN";
                valueTextBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);

                Check(
                    "invalid-value-rejected",
                    viewModel.AnalogIoAuthoring?.HasValidationErrors == true);
                Check(
                    "invalid-value-preserved",
                    viewModel.AnalogIoAuthoring?.InitialValue == 1.5);
                Check("invalid-value-visible", inspector.AnalogValueValidationMessage.IsVisible);
                Check("invalid-value-does-not-dirty-project", !viewModel.HasUnsavedChanges);
            }
            else if (state.Equals("save-reload", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(savePath))
                {
                    throw new ArgumentException(
                        "--smoke-analog-authoring-save is required for save-reload state.");
                }

                valueTextBox.Text = "42.25";
                valueTextBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                Check("valid-value-committed", viewModel.AnalogIoAuthoring?.InitialValue == 42.25);
                Check("valid-value-dirties-project", viewModel.HasUnsavedChanges);
                Check("valid-value-does-not-run", viewModel.SceneSnapshots.Latest?.TickIndex == tickBeforeEdit);

                await viewModel.SaveProjectAsync(savePath);
                Check("project-save-succeeded", File.Exists(savePath));
                Check("save-clears-dirty-state", !viewModel.HasUnsavedChanges);
                Check("project-reopen-succeeded", await viewModel.OpenProjectAsync(savePath));
                var reopenedTarget = SelectNode(
                    viewModel.ProjectTree,
                    $"{viewModel.ProjectTree.Roots.Single().Id}/channels/{targetId}");
                Check("reopened-analog-channel-selected", reopenedTarget is not null);
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                Check("reopened-value-restored", viewModel.AnalogIoAuthoring?.InitialValue == 42.25);
                Check("reopen-restores-clean-state", !viewModel.HasUnsavedChanges);
            }
            else
            {
                valueTextBox.Text = "42.25";
                valueTextBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                Check("valid-value-committed", viewModel.AnalogIoAuthoring?.InitialValue == 42.25);
                Check("valid-value-dirties-project", viewModel.HasUnsavedChanges);
                Check("valid-value-does-not-run", viewModel.SceneSnapshots.Latest?.TickIndex == tickBeforeEdit);
            }
        }

        if (!string.IsNullOrWhiteSpace(screenshotPath))
        {
            captureScreenshot(window, screenshotPath);
        }

        if (exitPlaybackAfterScreenshot)
        {
            viewModel.DryRunPlayback.Exit();
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Check("dry-run-playback-exited", !viewModel.IsDryRunPlaybackActive);
            Check("analog-field-reenabled-after-playback", valueTextBox.IsEnabled);
            var scene = findScene(window)
                ?? throw new InvalidOperationException("The scene document was not available.");
            Check("scene-identity-restored-after-playback", scene.SceneIdentityCard.IsVisible);
        }

        var monitor = SmokeDpiTestHook.CaptureMonitorEvidence(window);
        Check("window-intersects-selected-monitor", monitor.WindowIntersectsMonitor);
        Check("window-contained-by-selected-monitor", monitor.WindowContainedByMonitor);

        return new SmokeAnalogIoAuthoringReport
        {
            Checks = checks,
            Failures = failures,
            Monitor = monitor
        };
    }

    private static TreeNodeViewModel? SelectNode(ProjectTreeViewModel tree, string path)
    {
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return null;
        }

        TreeNodeViewModel? current = FindByName(tree.Roots, parts[0]);
        if (current is null)
        {
            return null;
        }

        for (var index = 1; index < parts.Length; index++)
        {
            current = FindByName(current.Children, parts[index]);
            if (current is null)
            {
                return null;
            }
        }

        tree.SelectedNode = current;
        return current;
    }

    private static TreeNodeViewModel? FindByName(
        IEnumerable<TreeNodeViewModel> nodes,
        string name)
    {
        foreach (var node in nodes)
        {
            if (string.Equals(node.Id, name, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(node.DisplayName, name, StringComparison.OrdinalIgnoreCase))
            {
                return node;
            }

            var child = FindByName(node.Children, name);
            if (child is not null)
            {
                return child;
            }
        }

        return null;
    }
}
