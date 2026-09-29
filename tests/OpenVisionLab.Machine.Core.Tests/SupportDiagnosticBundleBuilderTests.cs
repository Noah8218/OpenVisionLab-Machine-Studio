using OpenVisionLab.Machine.Core.Diagnostics;
using Xunit;

namespace OpenVisionLab.Machine.Core.Tests;

public sealed class SupportDiagnosticBundleBuilderTests
{
    [Fact]
    public void Build_RedactsSecretsPathsAndImages_WhileKeepingSupportFields()
    {
        var builder = new SupportDiagnosticBundleBuilder();
        var request = new SupportDiagnosticBundleRequest(
            "0.2.0-dev.60",
            SourceCommit: "0123456789abcdef0123456789abcdef01234567",
            SourceState: "dirty",
            ProjectId: "project-1",
            RunId: "run-42",
            Errors:
            [
                new(
                    "InspectionFailed",
                    "secret=super-secret token: bearer-token failed at C:\\Users\\Alice\\selected.png",
                    "password=another-secret")
            ],
            Queue:
            [
                new("external-result", "waiting", TimedOut: true, TimeoutMilliseconds: 5000,
                    Detail: "C:\\Users\\Alice\\Machine\\exchange")
            ],
            Artifacts:
            [
                new("logs/run.json", "diagnostic", 42, "abcdef"),
                new("images/selected.png", "selected-image"),
                new("credentials/api.key", "secret")
            ],
            Replayable: true,
            SensitiveValues: ["super-secret", "bearer-token", "another-secret"],
            SensitivePaths: [@"C:\Users\Alice\selected.png"]);

        var bundle = builder.Build(request, new DateTimeOffset(2026, 9, 16, 0, 0, 0, TimeSpan.Zero));
        var json = builder.Serialize(request, bundle.ExportedAtUtc);

        Assert.Equal("openvisionlab-machine-support-diagnostics/1", bundle.Schema);
        Assert.Equal("0.2.0-dev.60", bundle.Identity.ApplicationVersion);
        Assert.Equal("run-42", bundle.Run.RunId);
        Assert.Contains(bundle.Artifacts, artifact => artifact.RelativePath == "logs/run.json");
        Assert.DoesNotContain(bundle.Artifacts, artifact => artifact.RelativePath.Contains("image", StringComparison.OrdinalIgnoreCase));
        Assert.False(bundle.Replay.IsReplayable);
        Assert.Contains("original image", bundle.Replay.Limitation, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(bundle.Excluded, item => item.Category == "selected-image");
        Assert.Contains(bundle.Excluded, item => item.Category == "secret-value");
        Assert.DoesNotContain("super-secret", json, StringComparison.Ordinal);
        Assert.DoesNotContain("bearer-token", json, StringComparison.Ordinal);
        Assert.DoesNotContain("another-secret", json, StringComparison.Ordinal);
        Assert.DoesNotContain(@"C:\Users\Alice", json, StringComparison.Ordinal);
        Assert.Contains("InspectionFailed", json, StringComparison.Ordinal);
        Assert.Contains("external-result", json, StringComparison.Ordinal);
        Assert.Contains("logs/run.json", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_RejectsTraversalAndAbsoluteArtifacts_AndPreservesTimeoutContract()
    {
        var builder = new SupportDiagnosticBundleBuilder();
        var request = new SupportDiagnosticBundleRequest(
            "0.2.0-dev.60",
            RunId: "run-7",
            Queue:
            [
                new("publish", "timed-out", TimedOut: true, TimeoutMilliseconds: 250)
            ],
            Artifacts:
            [
                new("..\\outside.json", "diagnostic"),
                new(@"C:\Users\Alice\report.json", "diagnostic"),
                new("reports/summary.json", "diagnostic", 10, "0123")
            ]);

        var bundle = builder.Build(request);

        var artifact = Assert.Single(bundle.Artifacts);
        Assert.Equal("reports/summary.json", artifact.RelativePath);
        Assert.Equal("0123", artifact.Sha256);
        Assert.Contains(bundle.Excluded, item => item.Category == "traversal-path");
        Assert.Contains(bundle.Excluded, item => item.Category == "absolute-path");
        var timeout = Assert.Single(bundle.Queue);
        Assert.True(timeout.TimedOut);
        Assert.Equal(250, timeout.TimeoutMilliseconds);
    }

    [Fact]
    public void WriteJson_ProducesAnExplicitSanitizedExportOnTheTestDrive()
    {
        var root = Path.Combine(
            @"D:\OpenVisionLab-TestData\Machine",
            "mch-054-support-diagnostics-tests");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "support-diagnostics.json");
        var builder = new SupportDiagnosticBundleBuilder();

        builder.WriteJson(
            path,
            new SupportDiagnosticBundleRequest(
                "0.2.0-dev.60",
                RunId: "run-export",
                Errors: [new("E1", "failed")],
                Artifacts: [new("logs/run.json", "diagnostic")]));

        var json = File.ReadAllText(path);
        Assert.Contains("run-export", json, StringComparison.Ordinal);
        Assert.Contains("logs/run.json", json, StringComparison.Ordinal);
    }
}
