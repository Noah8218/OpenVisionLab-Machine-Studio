using OpenVisionLab;
using OpenVisionLab.Machine.Core.Diagnostics;
using OpenVisionLab.MachineStudio.ViewModel;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests.Diagnostics;

public sealed class SupportDiagnosticBundleViewModelTests
{
    [Fact]
    public void Preview_ListsSupportFieldsAndRedactionBoundaryBeforeExport()
    {
        var viewModel = CreateViewModel();

        var preview = viewModel.PreviewText;

        Assert.Contains("0.2.0-dev.60", preview, StringComparison.Ordinal);
        Assert.Contains("run-42", preview, StringComparison.Ordinal);
        Assert.Contains("InspectionFailed", preview, StringComparison.Ordinal);
        Assert.Contains("external-result", preview, StringComparison.Ordinal);
        Assert.Contains("logs/run.json", preview, StringComparison.Ordinal);
        Assert.Contains("Replay", preview, StringComparison.Ordinal);
        Assert.DoesNotContain("super-secret", preview, StringComparison.Ordinal);
    }

    [Fact]
    public void TryExport_WritesExplicitBundleAndDoesNotInvokeSideEffects()
    {
        var root = Path.Combine(
            @"D:\OpenVisionLab-TestData\Machine",
            "mch-054-support-diagnostics-vm-tests");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "support.json");
        var statusMessages = new List<string>();
        var logs = new List<string>();
        var dialogCalls = 0;
        var viewModel = new SupportDiagnosticBundleViewModel(
            new SupportDiagnosticBundleBuilder(),
            () => new SupportDiagnosticBundleRequest(
                "0.2.0-dev.60",
                RunId: "run-42",
                Errors: [new("InspectionFailed", "secret=super-secret")],
                Artifacts: [new("logs/run.json", "diagnostic")],
                SensitiveValues: ["super-secret"]),
            () =>
            {
                dialogCalls++;
                return null;
            },
            statusMessages.Add,
            logs.Add);

        Assert.True(viewModel.TryExport(path));
        var json = File.ReadAllText(path);

        Assert.Equal(0, dialogCalls);
        Assert.Contains("run-42", json, StringComparison.Ordinal);
        Assert.DoesNotContain("super-secret", json, StringComparison.Ordinal);
        Assert.Contains(
            statusMessages,
            message => message.Contains(OpenVisionLanguageService.T("SupportDiagnostics.Exported"), StringComparison.Ordinal));
        Assert.Contains(logs, message => message.Contains("Support diagnostic bundle exported", StringComparison.Ordinal));
    }

    [Fact]
    public void TryExport_ReportsFailureWithoutThrowingForInvalidDestination()
    {
        var statuses = new List<string>();
        var viewModel = new SupportDiagnosticBundleViewModel(
            new SupportDiagnosticBundleBuilder(),
            () => new SupportDiagnosticBundleRequest("0.2.0-dev.60"),
            () => null,
            statuses.Add,
            _ => { });

        Assert.False(viewModel.TryExport("bad\0path.json"));
        Assert.Contains(
            statuses,
            message => message.Contains(OpenVisionLanguageService.T("SupportDiagnostics.ExportFailed"), StringComparison.Ordinal));
    }

    private static SupportDiagnosticBundleViewModel CreateViewModel() =>
        new(
            new SupportDiagnosticBundleBuilder(),
            () => new SupportDiagnosticBundleRequest(
                "0.2.0-dev.60",
                RunId: "run-42",
                Errors: [new("InspectionFailed", "secret=super-secret")],
                Queue: [new("external-result", "waiting", TimedOut: true, TimeoutMilliseconds: 5000)],
                Artifacts:
                [
                    new("logs/run.json", "diagnostic"),
                    new("images/selected.png", "selected-image")
                ],
                SensitiveValues: ["super-secret"]),
            () => null,
            _ => { },
            _ => { });
}
