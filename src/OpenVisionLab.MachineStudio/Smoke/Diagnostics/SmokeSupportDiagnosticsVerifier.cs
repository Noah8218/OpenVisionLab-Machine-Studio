using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using OpenVisionLab;
using OpenVisionLab.Machine.Core.Diagnostics;
using OpenVisionLab.MachineStudio.View.Diagnostics;
using OpenVisionLab.MachineStudio.View.Shell;
using OpenVisionLab.MachineStudio.ViewModel;
using static OpenVisionLab.MachineStudio.SmokeVisualTreeQuery;

namespace OpenVisionLab.MachineStudio;

internal sealed class SmokeSupportDiagnosticsReport
{
    public string Schema { get; init; } = "1.0";
    public DateTimeOffset CapturedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public required string ExportPath { get; init; }
    public required IReadOnlyDictionary<string, bool> Checks { get; init; }
    public required IReadOnlyList<string> Failures { get; init; }
    public SmokeMonitorEvidence? Monitor { get; init; }
    public string? ExportSha256 { get; init; }
    public string? PreviewText { get; init; }
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

internal static class SmokeSupportDiagnosticsVerifier
{
    public static async Task<SmokeSupportDiagnosticsReport> VerifyAsync(
        ShellWindow window,
        MainViewModel viewModel,
        string exportPath,
        SmokeNativeInput nativeInput,
        SmokeWindowCapture windowCapture,
        string? screenshotPath)
    {
        var fullExportPath = Path.GetFullPath(exportPath);
        var checks = new Dictionary<string, bool>(StringComparer.Ordinal);
        var failures = new List<string>();
        var monitor = SmokeDpiTestHook.CaptureMonitorEvidence(window);
        var projectPath = viewModel.CurrentProjectPath;
        var projectHashBefore = TryHash(projectPath);
        var sceneSnapshotBefore = viewModel.SceneSnapshots.Latest;
        var dirtyBefore = viewModel.HasUnsavedChanges;
        string? previewText = null;
        string? exportHash = null;

        void Check(string name, bool passed)
        {
            checks[name] = passed;
            if (!passed)
            {
                failures.Add(name);
            }
        }

        Check("monitor-intersects", monitor.WindowIntersectsMonitor);
        Check("monitor-contained", monitor.WindowContainedByMonitor);

        var journal = FindVisualDescendant<EventJournalView>(window);
        Check("event-journal-present", journal is not null);
        var toggle = FindVisualDescendant<ToggleButton>(
            journal is null ? window : journal,
            candidate => string.Equals(candidate.Name, "EvidenceDrawerToggle", StringComparison.Ordinal));
        Check("evidence-drawer-toggle-present", toggle is not null);
        if (toggle is null)
        {
            return CreateReport(fullExportPath, checks, failures, monitor, previewText, exportHash);
        }

        await ClickToggleAsync(window, toggle, nativeInput);
        if (toggle.IsChecked != true)
        {
            viewModel.Navigation.IsEvidenceExpanded = true;
            await WaitForIdleAsync(window);
        }
        Check("evidence-drawer-expanded", toggle.IsChecked == true);
        viewModel.Navigation.SelectedEvidenceTabIndex = 3;
        await WaitForIdleAsync(window);

        var exportButton = FindVisualDescendant<Button>(
            journal is null ? window : journal,
            candidate => string.Equals(
                candidate.Content?.ToString(),
                OpenVisionLanguageService.T("SupportDiagnostics.Export"),
                StringComparison.Ordinal));
        Check("export-button-visible", exportButton is { IsVisible: true, IsEnabled: true });
        Check("export-command-bound", exportButton?.Command is not null);
        Check("export-command-enabled", exportButton?.Command?.CanExecute(fullExportPath) == true);

        const string secret = "token=smoke-support-secret";
        const string userPath = "C:\\Users\\Smoke\\customer\\selected.png";
        viewModel.AppendLog(
            TimeSpan.Zero,
            "Error",
            $"Support redaction probe {secret} path={userPath}");
        await WaitForIdleAsync(window);
        viewModel.SupportDiagnostics.Refresh();
        await WaitForIdleAsync(window);

        previewText = viewModel.SupportDiagnostics.PreviewText;
        Check("preview-has-build-identity", previewText.Contains(
            BuildIdentity.Current,
            StringComparison.OrdinalIgnoreCase));
        Check("preview-has-redaction-marker", previewText.Contains("<redacted>", StringComparison.Ordinal));
        Check("preview-hides-secret", !previewText.Contains("smoke-support-secret", StringComparison.Ordinal));
        Check("preview-hides-user-path", !previewText.Contains(userPath, StringComparison.Ordinal));

        var previewBlock = FindVisualDescendant<TextBlock>(
            journal is null ? window : journal,
            candidate => candidate.IsVisible
                && string.Equals(candidate.Text, previewText, StringComparison.Ordinal));
        Check("preview-visible", previewBlock is not null);

        var bundle = viewModel.SupportDiagnostics.CreateCurrentBundle();
        Check("bundle-schema-present", string.Equals(
            bundle.Schema,
            "openvisionlab-machine-support-diagnostics/1",
            StringComparison.Ordinal));
        Check("bundle-excludes-sensitive-data", bundle.Excluded.Count > 0 && !bundle.Replay.IsReplayable);
        Check("bundle-artifacts-relative", bundle.Artifacts.All(artifact =>
            !Path.IsPathRooted(artifact.RelativePath)
            && !artifact.RelativePath.Split('/').Any(segment => segment is "." or "..")));

        if (exportButton?.Command is { } exportCommand && exportCommand.CanExecute(fullExportPath))
        {
            exportCommand.Execute(fullExportPath);
            await WaitForIdleAsync(window);
        }

        Check("export-created", File.Exists(fullExportPath));
        if (File.Exists(fullExportPath))
        {
            exportHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(fullExportPath)));
            try
            {
                var exported = JsonSerializer.Deserialize<SupportDiagnosticBundle>(
                    File.ReadAllText(fullExportPath),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                Check("export-json-parseable", exported is not null);
                Check("export-json-redacted", exported is not null
                    && exported.Errors.All(error =>
                        !error.Message.Contains(secret, StringComparison.Ordinal)
                        && !error.Message.Contains(userPath, StringComparison.Ordinal)));
                Check("export-json-replay-limitation", exported?.Replay.IsReplayable == false);
            }
            catch (JsonException)
            {
                Check("export-json-parseable", false);
            }
        }

        if (!string.IsNullOrWhiteSpace(screenshotPath))
        {
            windowCapture.Capture(window, screenshotPath);
        }

        Check("project-file-unchanged", string.Equals(projectHashBefore, TryHash(projectPath), StringComparison.Ordinal));
        Check("project-dirty-state-unchanged", viewModel.HasUnsavedChanges == dirtyBefore);
        Check("runtime-snapshot-unchanged", ReferenceEquals(sceneSnapshotBefore, viewModel.SceneSnapshots.Latest));
        Check("status-reports-export", viewModel.SupportDiagnostics.StatusText.Contains(
            OpenVisionLanguageService.T("SupportDiagnostics.Exported"),
            StringComparison.OrdinalIgnoreCase)
            || viewModel.SupportDiagnostics.StatusText.Contains("내보냈", StringComparison.Ordinal));

        return CreateReport(fullExportPath, checks, failures, monitor, previewText, exportHash);
    }

    private static SmokeSupportDiagnosticsReport CreateReport(
        string exportPath,
        IReadOnlyDictionary<string, bool> checks,
        IReadOnlyList<string> failures,
        SmokeMonitorEvidence monitor,
        string? previewText,
        string? exportHash) =>
        new()
        {
            ExportPath = exportPath,
            Checks = checks,
            Failures = failures,
            Monitor = monitor,
            PreviewText = previewText,
            ExportSha256 = exportHash
        };

    private static async Task ClickToggleAsync(
        Window window,
        ToggleButton toggle,
        SmokeNativeInput nativeInput)
    {
        window.Activate();
        nativeInput.ActivateWindow(window);
        toggle.Focus();
        nativeInput.MovePointerToCenter(toggle);
        await WaitForIdleAsync(window);
        nativeInput.PressLeftButton();
        nativeInput.MarkPointerHeld();
        await WaitForIdleAsync(window);
        nativeInput.ReleasePointer();
        await WaitForIdleAsync(window);
    }

    private static async Task WaitForIdleAsync(Window window) =>
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);

    private static string? TryHash(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        return Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    }
}
