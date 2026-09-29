using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using OpenVisionLab.MachineStudio.View.Project;
using OpenVisionLab.MachineStudio.View.Shell;
using OpenVisionLab.MachineStudio.ViewModel;
using static OpenVisionLab.MachineStudio.SmokeVisualTreeQuery;

namespace OpenVisionLab.MachineStudio;

internal sealed class SmokeProjectDiagnosticsReport
{
    public string Schema { get; init; } = "1.0";
    public DateTimeOffset CapturedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public required string ProjectPath { get; init; }
    public required IReadOnlyDictionary<string, bool> Checks { get; init; }
    public required IReadOnlyList<string> Failures { get; init; }
    public SmokeMonitorEvidence? Monitor { get; init; }
    public string? FinalSummary { get; init; }
    public IReadOnlyList<string>? FinalDiagnosticCodes { get; init; }
    public string? FinalCurrentState { get; init; }
    public bool IsValid => Failures.Count == 0 && Checks.Values.All(value => value);

    public void Save(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(
            fullPath,
            JsonSerializer.Serialize(this, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = true
            }));
    }
}

internal static class SmokeProjectDiagnosticsVerifier
{
    public static async Task<SmokeProjectDiagnosticsReport> VerifyAsync(
        ShellWindow window,
        MainViewModel viewModel,
        string projectPath,
        SmokeNativeInput nativeInput,
        SmokeWindowCapture windowCapture,
        string? screenshotPath)
    {
        var fullPath = Path.GetFullPath(projectPath);
        var backupPath = fullPath + ".bak";
        var checks = new Dictionary<string, bool>(StringComparer.Ordinal);
        var failures = new List<string>();
        var monitor = SmokeDpiTestHook.CaptureMonitorEvidence(window);

        void Check(string name, bool passed)
        {
            checks[name] = passed;
            if (!passed)
            {
                failures.Add(name);
            }
        }

        Check("project-path-loaded", string.Equals(
            Path.GetFullPath(viewModel.CurrentProjectPath ?? string.Empty),
            fullPath,
            StringComparison.OrdinalIgnoreCase));
        Check("monitor-intersects", monitor.WindowIntersectsMonitor);
        Check("monitor-contained", monitor.WindowContainedByMonitor);

        var leftRegion = FindVisualDescendant<LeftToolRegionView>(window);
        Check("left-tool-region-present", leftRegion is not null);
        if (leftRegion is null)
        {
            return new SmokeProjectDiagnosticsReport
            {
                ProjectPath = fullPath,
                Checks = checks,
                Failures = failures,
                Monitor = monitor
            };
        }

        var tabs = FindVisualDescendant<TabControl>(leftRegion);
        Check("left-tool-tabs-present", tabs is not null);
        var diagnosticsTab = tabs?.Items.OfType<TabItem>().FirstOrDefault(tab =>
            string.Equals(
                tab.Header?.ToString(),
                OpenVisionLanguageService.T("Project.Diagnostics"),
                StringComparison.Ordinal));
        Check("diagnostics-tab-present", diagnosticsTab is not null);
        if (tabs is null || diagnosticsTab is null)
        {
            return new SmokeProjectDiagnosticsReport
            {
                ProjectPath = fullPath,
                Checks = checks,
                Failures = failures,
                Monitor = monitor
            };
        }

        diagnosticsTab.IsSelected = true;
        await WaitForIdleAsync(window);
        Check("diagnostics-tab-bound-index", viewModel.SelectedLeftToolTabIndex == tabs.Items.IndexOf(diagnosticsTab));

        var diagnosticsViewModel = viewModel.ProjectDiagnostics;
        Check("diagnostics-report-present", diagnosticsViewModel.HasReport);
        Check("diagnostics-summary-visible", FindVisualDescendant<TextBlock>(
            leftRegion,
            text => text.IsVisible && !string.IsNullOrWhiteSpace(text.Text)
                && string.Equals(text.Text, diagnosticsViewModel.SummaryText, StringComparison.Ordinal)) is not null);
        Check("diagnostics-current-clean-visible", !viewModel.HasUnsavedChanges);

        var backupBytes = File.Exists(backupPath) ? File.ReadAllBytes(backupPath) : Array.Empty<byte>();
        Check("backup-fixture-present", backupBytes.Length > 0);
        if (backupBytes.Length == 0 || !File.Exists(fullPath))
        {
            return new SmokeProjectDiagnosticsReport
            {
                ProjectPath = fullPath,
                Checks = checks,
                Failures = failures,
                Monitor = monitor
            };
        }

        await File.WriteAllTextAsync(fullPath, "corrupted primary");
        var corruptedPrimaryBytes = File.ReadAllBytes(fullPath);
        var openedFromBackup = await viewModel.OpenProjectAsync(fullPath);
        await WaitForIdleAsync(window);
        Check("open-fallback-recovered", openedFromBackup && diagnosticsViewModel.HasLoadSourceNotice);
        Check("open-fallback-preserves-primary", corruptedPrimaryBytes.SequenceEqual(File.ReadAllBytes(fullPath)));
        Check("open-fallback-preserves-backup", backupBytes.SequenceEqual(File.ReadAllBytes(backupPath)));
        Check("open-fallback-notice-visible", FindVisualDescendant<TextBlock>(
            leftRegion,
            text => text.IsVisible
                && string.Equals(text.Text, diagnosticsViewModel.LoadSourceNoticeText, StringComparison.Ordinal)) is not null);

        File.Delete(fullPath);
        Check("primary-deleted-for-recovery", !File.Exists(fullPath));
        diagnosticsViewModel.RefreshCommand.Execute(null);
        await WaitForIdleAsync(window);
        Check("recovery-candidate-visible", diagnosticsViewModel.HasRecoveryCandidate);
        Check("preview-command-enabled", diagnosticsViewModel.PreviewRecoveryCommand.CanExecute(null));

        var refreshButton = FindVisualDescendant<Button>(leftRegion, button =>
            string.Equals(
                button.Content?.ToString(),
                OpenVisionLanguageService.T("ProjectDiagnostics.Refresh"),
                StringComparison.Ordinal));
        var previewButton = FindVisualDescendant<Button>(leftRegion, button =>
            string.Equals(
                button.Content?.ToString(),
                OpenVisionLanguageService.T("ProjectDiagnostics.Preview"),
                StringComparison.Ordinal));
        Check("refresh-button-visible", refreshButton is { IsVisible: true, IsEnabled: true });
        Check("preview-button-visible", previewButton is { IsVisible: true, IsEnabled: true });
        Check("preview-button-command-bound", previewButton?.Command is not null);
        if (previewButton is null || !previewButton.IsEnabled)
        {
            return new SmokeProjectDiagnosticsReport
            {
                ProjectPath = fullPath,
                Checks = checks,
                Failures = failures,
                Monitor = monitor
            };
        }

        await ClickAsync(window, previewButton, nativeInput);
        if (!diagnosticsViewModel.HasRecoveryPreview
            && previewButton.Command is { } previewCommand
            && previewCommand.CanExecute(previewButton.CommandParameter))
        {
            previewCommand.Execute(previewButton.CommandParameter);
            await WaitForIdleAsync(window);
        }
        var bytesAfterPreview = File.ReadAllBytes(backupPath);
        Check("preview-does-not-write-primary", !File.Exists(fullPath));
        Check("preview-does-not-change-backup", backupBytes.SequenceEqual(bytesAfterPreview));
        Check("recovery-preview-visible", diagnosticsViewModel.HasRecoveryPreview);

        var previewText = FindVisualDescendant<TextBlock>(leftRegion, text =>
            text.IsVisible
            && !string.IsNullOrWhiteSpace(text.Text)
            && string.Equals(text.Text, diagnosticsViewModel.RecoveryPreviewText, StringComparison.Ordinal));
        Check("recovery-preview-text-visible", previewText is not null);
        if (!string.IsNullOrWhiteSpace(screenshotPath))
        {
            windowCapture.Capture(window, screenshotPath);
        }

        var applyButton = FindVisualDescendant<Button>(leftRegion, button =>
            string.Equals(
                button.Content?.ToString(),
                OpenVisionLanguageService.T("ProjectDiagnostics.Apply"),
                StringComparison.Ordinal));
        var cancelButton = FindVisualDescendant<Button>(leftRegion, button =>
            string.Equals(
                button.Content?.ToString(),
                OpenVisionLanguageService.T("ProjectDiagnostics.Cancel"),
                StringComparison.Ordinal));
        Check("apply-button-visible", applyButton is { IsVisible: true, IsEnabled: true });
        Check("cancel-button-visible", cancelButton is { IsVisible: true, IsEnabled: true });
        if (applyButton is not null && applyButton.IsEnabled)
        {
            await ClickAsync(window, applyButton, nativeInput);
            if (File.Exists(backupPath)
                && diagnosticsViewModel.HasRecoveryPreview
                && applyButton.Command is { } applyCommand
                && applyCommand.CanExecute(applyButton.CommandParameter))
            {
                applyCommand.Execute(applyButton.CommandParameter);
            }

            for (var attempt = 0; attempt < 80 && (!File.Exists(fullPath) || diagnosticsViewModel.HasRecoveryPreview); attempt++)
            {
                await Task.Delay(50);
                await WaitForIdleAsync(window);
            }
        }

        Check("recovery-applied", File.Exists(fullPath)
            && string.Equals(
                Path.GetFullPath(viewModel.CurrentProjectPath ?? string.Empty),
                fullPath,
                StringComparison.OrdinalIgnoreCase)
            && !viewModel.HasUnsavedChanges);
        Check("restored-primary-matches-backup", File.Exists(fullPath)
            && backupBytes.SequenceEqual(File.ReadAllBytes(fullPath)));
        Check("diagnostics-refreshed-healthy", diagnosticsViewModel.Report?.IsHealthy == true
            && !diagnosticsViewModel.HasRecoveryPreview);

        return new SmokeProjectDiagnosticsReport
        {
            ProjectPath = fullPath,
            Checks = checks,
            Failures = failures,
            Monitor = monitor,
            FinalSummary = diagnosticsViewModel.SummaryText,
            FinalDiagnosticCodes = diagnosticsViewModel.Report?.Items.Select(item => item.Code.ToString()).ToArray(),
            FinalCurrentState = diagnosticsViewModel.CurrentStateText
        };
    }

    private static async Task ClickAsync(Window window, Button button, SmokeNativeInput nativeInput)
    {
        window.Activate();
        nativeInput.ActivateWindow(window);
        button.Focus();
        nativeInput.MovePointerToCenter(button);
        await WaitForIdleAsync(window);
        nativeInput.PressLeftButton();
        nativeInput.MarkPointerHeld();
        await WaitForIdleAsync(window);
        nativeInput.ReleasePointer();
        await WaitForIdleAsync(window);
    }

    private static async Task WaitForIdleAsync(Window window) =>
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
}
