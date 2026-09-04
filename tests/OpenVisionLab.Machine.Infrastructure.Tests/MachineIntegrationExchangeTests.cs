using System.Security.Cryptography;
using OpenVisionLab.Integration.Contracts;
using OpenVisionLab.Machine.Infrastructure.Integration;
using Xunit;

namespace OpenVisionLab.Machine.Infrastructure.Tests;

public sealed class MachineIntegrationExchangeTests
{
    [Fact]
    public void PublishHandoff_StagesHashesAndPublishesOneTransactionDirectory()
    {
        using var fixture = new ExchangeFixture();

        var published = MachineIntegrationExchange.PublishHandoff(
            fixture.Root,
            fixture.Handoff,
            fixture.SourcePaths);

        Assert.Equal(fixture.Handoff, published);
        var read = MachineIntegrationExchange.ReadHandoff(
            fixture.Root,
            fixture.Handoff.TransactionId);
        Assert.Equal(fixture.Handoff.MessageId, read.MessageId);
        Assert.Equal(
            fixture.Handoff.Context.Artifacts.Count,
            Directory.EnumerateFiles(
                    Path.Combine(
                        fixture.Root,
                        IntegrationTransactionLayout.TransactionsDirectoryName,
                        fixture.Handoff.TransactionId.ToString("D"),
                        IntegrationTransactionLayout.ArtifactsDirectoryName),
                    "*",
                    SearchOption.AllDirectories)
                .Count());

        var discovered = MachineIntegrationExchange.DiscoverTransactions(fixture.Root);
        Assert.Single(discovered);
        Assert.False(discovered[0].HasAcknowledgement);
        Assert.False(discovered[0].HasResult);
    }

    [Fact]
    public void PublishHandoff_WhenSourceIdentityDiffers_DoesNotPublishPartialTransaction()
    {
        using var fixture = new ExchangeFixture();
        File.WriteAllBytes(
            fixture.SourcePaths["inspection-source"],
            [0xFF, 0xEE, 0xDD]);

        var exception = Assert.Throws<IntegrationContractException>(() =>
            MachineIntegrationExchange.PublishHandoff(
                fixture.Root,
                fixture.Handoff,
                fixture.SourcePaths));

        Assert.Equal(IntegrationErrorCode.ArtifactLengthMismatch, exception.ErrorCode);
        var transactionsRoot = Path.Combine(
            fixture.Root,
            IntegrationTransactionLayout.TransactionsDirectoryName);
        Assert.Empty(MachineIntegrationExchange.DiscoverTransactions(fixture.Root));
        var diagnostic = Assert.Single(
            MachineIntegrationExchange.DiagnoseTransactions(fixture.Root));
        Assert.Equal(
            MachineIntegrationTransactionState.Quarantined,
            diagnostic.State);
        Assert.Contains("publish-failed", diagnostic.Detail);
        var quarantineDirectory = Assert.Single(
            Directory.EnumerateDirectories(Path.Combine(
                transactionsRoot,
                ".quarantine")));
        Assert.True(File.Exists(Path.Combine(quarantineDirectory, "quarantine.json")));
        Assert.DoesNotContain(
            Directory.EnumerateDirectories(transactionsRoot),
            path => Guid.TryParse(Path.GetFileName(path), out _));
    }

    [Fact]
    public void ReadResult_ValidatesTheFullV2SequenceAndResultArtifacts()
    {
        using var fixture = new ExchangeFixture();
        MachineIntegrationExchange.PublishHandoff(
            fixture.Root,
            fixture.Handoff,
            fixture.SourcePaths);
        var acknowledgement = new IntegrationAcknowledgementV2(
            IntegrationContractSchema.V2,
            IntegrationMessageKind.Acknowledgement,
            Guid.NewGuid(),
            fixture.Handoff.TransactionId,
            fixture.Handoff.MessageId,
            fixture.Handoff.CreatedAtUtc,
            ExchangeFixture.Consumer,
            IntegrationAcknowledgementStatus.Accepted,
            null);
        var resultPath = Path.Combine(
            fixture.TransactionDirectory,
            IntegrationTransactionLayout.ArtifactsDirectoryName,
            "run-record.json");
        File.WriteAllText(resultPath, "{\"runId\":\"run-1\"}");
        var resultBytes = File.ReadAllBytes(resultPath);
        var result = new IntegrationResultV2(
            IntegrationContractSchema.V2,
            IntegrationMessageKind.Result,
            Guid.NewGuid(),
            fixture.Handoff.TransactionId,
            fixture.Handoff.MessageId,
            acknowledgement.MessageId,
            acknowledgement.CreatedAtUtc,
            ExchangeFixture.Consumer,
            IntegrationResultStatus.Completed,
            IntegrationInspectionOutcome.Pass,
            "run-1",
            new IntegrationArtifactReference(
                IntegrationArtifactRoles.RunRecord,
                "run-1",
                "artifacts/run-record.json",
                resultBytes.LongLength,
                Convert.ToHexString(SHA256.HashData(resultBytes))),
            IntegrationRunCorrelation.FromContext(fixture.Handoff.Context),
            [],
            [],
            null);

        File.WriteAllBytes(
            Path.Combine(
                fixture.TransactionDirectory,
                IntegrationTransactionLayout.AcknowledgementFileName),
            IntegrationContractJson.SerializeCanonical(acknowledgement));
        File.WriteAllBytes(
            Path.Combine(
                fixture.TransactionDirectory,
                IntegrationTransactionLayout.ResultFileName),
            IntegrationContractJson.SerializeCanonical(result));

        var read = MachineIntegrationExchange.ReadResult(
            fixture.Root,
            fixture.Handoff.TransactionId);

        Assert.Equal(IntegrationInspectionOutcome.Pass, read.Outcome);
        Assert.Equal("run-1", read.RunId);
    }

    [Fact]
    public void PublishHandoff_RejectsUnsafeArtifactPathBeforeCreatingTransaction()
    {
        using var fixture = new ExchangeFixture();
        var unsafeHandoff = fixture.Handoff with
        {
            Context = fixture.Handoff.Context with
            {
                Artifacts =
                [
                    fixture.Handoff.Context.Artifacts[0] with
                    {
                        RelativePath = "../outside.ovmachine"
                    },
                    fixture.Handoff.Context.Artifacts[1],
                    fixture.Handoff.Context.Artifacts[2]
                ]
            }
        };

        var exception = Assert.Throws<IntegrationContractException>(() =>
            MachineIntegrationExchange.PublishHandoff(
                fixture.Root,
                unsafeHandoff,
                fixture.SourcePaths));

        Assert.Equal(IntegrationErrorCode.UnsafeArtifactPath, exception.ErrorCode);
        Assert.False(Directory.Exists(fixture.TransactionDirectory));
    }

    [Fact]
    public async Task PublishHandoffAsync_ReportsTransferLifecycle()
    {
        using var fixture = new ExchangeFixture();
        var progress = new RecordingProgress();

        await MachineIntegrationExchange.PublishHandoffAsync(
            fixture.Root,
            fixture.Handoff,
            fixture.SourcePaths,
            progress,
            cancellationToken: CancellationToken.None);

        Assert.Contains(
            progress.Values,
            value => value.Phase == MachineIntegrationTransferPhase.Preflight);
        Assert.Contains(
            progress.Values,
            value => value.Phase == MachineIntegrationTransferPhase.Copying);
        Assert.Contains(
            progress.Values,
            value => value.Phase == MachineIntegrationTransferPhase.Validating);
        var published = Assert.Single(
            progress.Values.Where(value =>
                value.Phase == MachineIntegrationTransferPhase.Published));
        Assert.Equal(fixture.Handoff.TransactionId, published.TransactionId);
        Assert.Equal(fixture.Handoff.Context.Artifacts.Count, published.CompletedArtifacts);
        Assert.Equal(published.TotalBytes, published.BytesCopied);
        Assert.Equal(1, published.FractionCompleted);
    }

    [Fact]
    public async Task PublishHandoffAsync_WhenCancelled_QuarantinesStaging()
    {
        using var fixture = new ExchangeFixture();
        using var cancellation = new CancellationTokenSource();
        var progress = new RecordingProgress(value =>
        {
            if (value.Phase == MachineIntegrationTransferPhase.Preflight)
            {
                cancellation.Cancel();
            }
        });

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            MachineIntegrationExchange.PublishHandoffAsync(
                fixture.Root,
                fixture.Handoff,
                fixture.SourcePaths,
                progress,
                cancellationToken: cancellation.Token));

        Assert.Empty(MachineIntegrationExchange.DiscoverTransactions(fixture.Root));
        Assert.Contains(
            progress.Values,
            value => value.Phase == MachineIntegrationTransferPhase.Quarantined);
        var diagnostic = Assert.Single(
            MachineIntegrationExchange.DiagnoseTransactions(fixture.Root));
        Assert.Equal(
            MachineIntegrationTransactionState.Quarantined,
            diagnostic.State);
        Assert.Contains("cancelled", diagnostic.Detail);
    }

    [Fact]
    public void PublishHandoff_WhenFreeSpaceRequirementCannotBeMet_FailsBeforeStaging()
    {
        using var fixture = new ExchangeFixture();

        var exception = Assert.Throws<IntegrationContractException>(() =>
            MachineIntegrationExchange.PublishHandoffAsync(
                    fixture.Root,
                    fixture.Handoff,
                    fixture.SourcePaths,
                    minimumFreeSpaceBytes: long.MaxValue)
                .GetAwaiter()
                .GetResult());

        Assert.Equal(IntegrationErrorCode.InvalidState, exception.ErrorCode);
        Assert.False(Directory.Exists(Path.Combine(
            fixture.Root,
            IntegrationTransactionLayout.TransactionsDirectoryName)));
    }

    [Fact]
    public void DiagnoseTransactions_ReportsPublishedArtifactAndStorageState()
    {
        using var fixture = new ExchangeFixture();
        MachineIntegrationExchange.PublishHandoff(
            fixture.Root,
            fixture.Handoff,
            fixture.SourcePaths);

        var diagnostic = Assert.Single(
            MachineIntegrationExchange.DiagnoseTransactions(fixture.Root));

        Assert.Equal(
            MachineIntegrationTransactionState.Published,
            diagnostic.State);
        Assert.Equal(fixture.Handoff.TransactionId, diagnostic.TransactionId);
        Assert.Equal(fixture.Handoff.Context.Artifacts.Count, diagnostic.ArtifactCount);
        Assert.Equal(
            fixture.Handoff.Context.Artifacts.Sum(artifact => artifact.ByteLength),
            diagnostic.DeclaredBytes);
        Assert.Equal(diagnostic.DeclaredBytes, diagnostic.MaterializedBytes);
        Assert.True(diagnostic.AvailableFreeBytes > 0);
        Assert.Null(diagnostic.Detail);
    }

    [Fact]
    public void DiagnoseTransactions_ReportsIncompleteAndUnknownDirectories()
    {
        using var fixture = new ExchangeFixture();
        var transactionsRoot = Path.Combine(
            fixture.Root,
            IntegrationTransactionLayout.TransactionsDirectoryName);
        Directory.CreateDirectory(transactionsRoot);
        var stagingDirectory = Path.Combine(
            transactionsRoot,
            $".{Guid.NewGuid():D}.{Guid.NewGuid():N}.staging");
        Directory.CreateDirectory(stagingDirectory);
        Directory.CreateDirectory(Path.Combine(transactionsRoot, "unexpected"));

        var diagnostics = MachineIntegrationExchange.DiagnoseTransactions(fixture.Root);

        Assert.Contains(
            diagnostics,
            diagnostic => diagnostic.State == MachineIntegrationTransactionState.Staging);
        Assert.Contains(
            diagnostics,
            diagnostic => diagnostic.State == MachineIntegrationTransactionState.Invalid);
    }

    [Fact]
    public void CleanupStaging_QuarantinesStaleDirectoriesAndPurgesRetentionExpiredEvidence()
    {
        using var fixture = new ExchangeFixture();
        var transactionsRoot = Path.Combine(
            fixture.Root,
            IntegrationTransactionLayout.TransactionsDirectoryName);
        Directory.CreateDirectory(transactionsRoot);
        var stagingDirectory = Path.Combine(
            transactionsRoot,
            $".{Guid.NewGuid():D}.{Guid.NewGuid():N}.staging");
        Directory.CreateDirectory(Path.Combine(
            stagingDirectory,
            IntegrationTransactionLayout.ArtifactsDirectoryName));
        File.WriteAllBytes(
            Path.Combine(
                stagingDirectory,
                IntegrationTransactionLayout.ArtifactsDirectoryName,
                "partial.bin"),
            [0x01, 0x02]);
        var now = DateTimeOffset.UtcNow;
        Directory.SetLastWriteTimeUtc(
            stagingDirectory,
            now.UtcDateTime.AddHours(-2));

        var report = MachineIntegrationExchange.CleanupStaging(
            fixture.Root,
            TimeSpan.FromMinutes(30),
            now);

        Assert.Equal(1, report.ScannedStagingDirectories);
        Assert.Equal(1, report.QuarantinedStagingDirectories);
        var diagnostic = Assert.Single(report.Diagnostics);
        Assert.Equal(
            MachineIntegrationTransactionState.Quarantined,
            diagnostic.State);
        Assert.Contains("stale-staging", diagnostic.Detail);
        Assert.False(Directory.Exists(stagingDirectory));

        var purged = MachineIntegrationExchange.PurgeQuarantine(
            fixture.Root,
            TimeSpan.Zero,
            now.AddHours(1));

        Assert.Equal(1, purged);
        Assert.Empty(MachineIntegrationExchange.DiagnoseTransactions(fixture.Root));
    }

    [Fact]
    public void PublishHandoff_RejectsReservedArtifactFileName()
    {
        using var fixture = new ExchangeFixture();
        var unsafeHandoff = fixture.Handoff with
        {
            Context = fixture.Handoff.Context with
            {
                Artifacts =
                [
                    fixture.Handoff.Context.Artifacts[0] with
                    {
                        RelativePath = "artifacts/CON"
                    },
                    fixture.Handoff.Context.Artifacts[1],
                    fixture.Handoff.Context.Artifacts[2]
                ]
            }
        };

        var exception = Assert.Throws<IntegrationContractException>(() =>
            MachineIntegrationExchange.PublishHandoff(
                fixture.Root,
                unsafeHandoff,
                fixture.SourcePaths));

        Assert.Equal(IntegrationErrorCode.UnsafeArtifactPath, exception.ErrorCode);
        Assert.False(Directory.Exists(Path.Combine(
            fixture.Root,
            IntegrationTransactionLayout.TransactionsDirectoryName)));
    }

    private sealed class RecordingProgress : IProgress<MachineIntegrationTransferProgress>
    {
        private readonly Action<MachineIntegrationTransferProgress>? _onReport;

        public RecordingProgress(
            Action<MachineIntegrationTransferProgress>? onReport = null)
        {
            _onReport = onReport;
        }

        public List<MachineIntegrationTransferProgress> Values { get; } = [];

        public void Report(MachineIntegrationTransferProgress value)
        {
            Values.Add(value);
            _onReport?.Invoke(value);
        }
    }

    private sealed class ExchangeFixture : IDisposable
    {
        public ExchangeFixture()
        {
            Root = Path.Combine(
                "D:\\OpenVisionLab-TestData\\OpenVisionLab-Machine-Studio",
                "integration-adapter-tests",
                Guid.NewGuid().ToString("N"));
            SourceRoot = Path.Combine(Root, "source");
            Directory.CreateDirectory(SourceRoot);

            var projectPath = Write("machine.ovmachine", [0x01, 0x02, 0x03]);
            var sourcePath = Write("inspection-source.pcd", [0x10, 0x20, 0x30, 0x40]);
            var recipePath = Write("inspection-recipe.json", [0x7B, 0x7D]);
            SourcePaths = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["machine-project"] = projectPath,
                ["inspection-source"] = sourcePath,
                ["inspection-recipe"] = recipePath
            };

            var sourceHash = Hash(sourcePath);
            var recipeHash = Hash(recipePath);
            var transactionId = Guid.NewGuid();
            var context = new IntegrationInspectionContextV2(
                "machine-project-1",
                "1.0",
                "sequence-1",
                "step-1",
                "camera-1",
                "acquisition-1",
                "frame-1",
                "mm",
                IntegrationInspectionModality.ThreeD,
                IntegrationInspectionInputKind.PointCloud,
                sourceHash,
                recipeHash,
                Consumer,
                [
                    Artifact(
                        IntegrationArtifactRoles.MachineProject,
                        "machine-project",
                        projectPath,
                        "artifacts/machine.ovmachine"),
                    Artifact(
                        IntegrationArtifactRoles.InspectionSource,
                        "inspection-source",
                        sourcePath,
                        "artifacts/inspection-source.pcd"),
                    Artifact(
                        IntegrationArtifactRoles.InspectionRecipe,
                        "inspection-recipe",
                        recipePath,
                        "artifacts/inspection-recipe.json")
                ]);
            Handoff = new(
                IntegrationContractSchema.V2,
                IntegrationMessageKind.Handoff,
                Guid.NewGuid(),
                transactionId,
                DateTimeOffset.UtcNow,
                new IntegrationApplicationIdentity(
                    IntegrationApplicationIds.MachineStudio,
                    "0.1.0-rc.1",
                    new string('1', 40),
                    IntegrationSourceState.Clean),
                context);
        }

        public string Root { get; }
        public string SourceRoot { get; }
        public string TransactionDirectory => Path.Combine(
            Root,
            IntegrationTransactionLayout.TransactionsDirectoryName,
            Handoff.TransactionId.ToString("D"));
        public Dictionary<string, string> SourcePaths { get; }
        public IntegrationHandoffV2 Handoff { get; }
        public static IntegrationApplicationIdentity Consumer { get; } = new(
            "OpenVisionLab.ThreeDStudio",
            "0.1.1",
            new string('2', 40),
            IntegrationSourceState.Clean);

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }

        private string Write(string name, byte[] bytes)
        {
            var path = Path.Combine(SourceRoot, name);
            File.WriteAllBytes(path, bytes);
            return path;
        }

        private static IntegrationArtifactReference Artifact(
            string role,
            string id,
            string sourcePath,
            string relativePath)
        {
            var info = new FileInfo(sourcePath);
            return new(
                role,
                id,
                relativePath,
                info.Length,
                Hash(sourcePath));
        }

        private static string Hash(string path)
        {
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream));
        }
    }
}
