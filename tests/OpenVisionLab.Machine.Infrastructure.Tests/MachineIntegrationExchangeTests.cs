using System.Security.Cryptography;
using OpenVisionLab.Integration.Contracts;
using OpenVisionLab.Machine.Infrastructure.Integration;
using Xunit;

namespace OpenVisionLab.Machine.Infrastructure.Tests;

public sealed class MachineIntegrationExchangeTests
{
    [Fact]
    public void PublishHandoff_CopiesIdentifiedArtifactsWithoutExecutingAnything()
    {
        using var directory = new TemporaryDirectory();
        var project = directory.Write("source/project.ovmachine", [1, 2, 3]);
        var source = directory.Write("source/frame.c3d", [4, 5, 6, 7]);

        var handoff = MachineIntegrationExchange.PublishHandoff(CreateRequest(
            directory.Path,
            project,
            source));

        var transaction = TransactionDirectory(directory.Path, handoff.TransactionId);
        var persisted = IntegrationContractJson.DeserializeHandoff(
            File.ReadAllBytes(Path.Combine(transaction, "handoff.json")));
        Assert.Equal(handoff.MessageId, persisted.MessageId);
        Assert.Equal(handoff.TransactionId, persisted.TransactionId);
        Assert.Equal(handoff.Producer, persisted.Producer);
        Assert.Equal(handoff.Context.ProjectId, persisted.Context.ProjectId);
        Assert.Equal(handoff.Context.SequenceId, persisted.Context.SequenceId);
        Assert.Equal(handoff.Context.Artifacts, persisted.Context.Artifacts);
        Assert.False(File.Exists(Path.Combine(transaction, "acknowledgement.json")));
        Assert.False(File.Exists(Path.Combine(transaction, "result.json")));
        foreach (var artifact in persisted.Context.Artifacts)
        {
            Assert.True(
                IntegrationContractValidator.ValidateArtifactFile(artifact, transaction).IsValid);
        }
    }

    [Fact]
    public void ReadResult_ValidatesTheCompleteSequenceAndRunRecord()
    {
        using var directory = new TemporaryDirectory();
        var project = directory.Write("source/project.ovmachine", [1, 2, 3]);
        var source = directory.Write("source/frame.c3d", [4, 5, 6, 7]);
        var handoff = MachineIntegrationExchange.PublishHandoff(CreateRequest(
            directory.Path,
            project,
            source));
        var transaction = TransactionDirectory(directory.Path, handoff.TransactionId);
        var acknowledgement = new IntegrationAcknowledgement(
            IntegrationContractSchema.Legacy,
            IntegrationMessageKind.Acknowledgement,
            Guid.NewGuid(),
            handoff.TransactionId,
            handoff.MessageId,
            handoff.CreatedAtUtc.AddSeconds(1),
            ThreeDIdentity(),
            IntegrationAcknowledgementStatus.Accepted,
            null);
        File.WriteAllBytes(
            Path.Combine(transaction, "acknowledgement.json"),
            IntegrationContractJson.Serialize(acknowledgement));
        var runRecordPath = directory.WriteRelative(transaction, "artifacts/run-record.json", [8, 9]);
        var runRecordBytes = File.ReadAllBytes(runRecordPath);
        var result = new IntegrationResult(
            IntegrationContractSchema.Legacy,
            IntegrationMessageKind.Result,
            Guid.NewGuid(),
            handoff.TransactionId,
            handoff.MessageId,
            acknowledgement.MessageId,
            acknowledgement.CreatedAtUtc.AddSeconds(1),
            ThreeDIdentity(),
            IntegrationResultStatus.Completed,
            IntegrationInspectionDisposition.Pass,
            "run-1",
            new IntegrationArtifactReference(
                IntegrationArtifactRoles.RunRecord,
                "run-1",
                "artifacts/run-record.json",
                runRecordBytes.LongLength,
                Convert.ToHexString(SHA256.HashData(runRecordBytes))),
            null);
        File.WriteAllBytes(
            Path.Combine(transaction, "result.json"),
            IntegrationContractJson.Serialize(result));

        var imported = MachineIntegrationExchange.ReadResult(
            directory.Path,
            handoff.TransactionId);

        Assert.Equal(result, imported.Result);
        Assert.Equal(IntegrationInspectionDisposition.Pass, imported.Result.Disposition);
    }

    [Fact]
    public void ReadProgress_ReturnsPendingThenAcknowledgedWithoutRequiringResult()
    {
        using var directory = new TemporaryDirectory();
        var project = directory.Write("source/project.ovmachine", [1, 2, 3]);
        var source = directory.Write("source/frame.c3d", [4, 5, 6]);
        var handoff = MachineIntegrationExchange.PublishHandoff(CreateRequest(
            directory.Path,
            project,
            source));

        var pending = MachineIntegrationExchange.ReadProgress(
            directory.Path,
            handoff.TransactionId);

        Assert.Equivalent(handoff, pending.Handoff, strict: true);
        Assert.Null(pending.Acknowledgement);
        Assert.Null(pending.Result);

        var acknowledgement = new IntegrationAcknowledgement(
            IntegrationContractSchema.Legacy,
            IntegrationMessageKind.Acknowledgement,
            Guid.NewGuid(),
            handoff.TransactionId,
            handoff.MessageId,
            handoff.CreatedAtUtc.AddSeconds(1),
            ThreeDIdentity(),
            IntegrationAcknowledgementStatus.Accepted,
            null);
        File.WriteAllBytes(
            Path.Combine(
                TransactionDirectory(directory.Path, handoff.TransactionId),
                IntegrationTransactionLayout.AcknowledgementFileName),
            IntegrationContractJson.Serialize(acknowledgement));

        var acknowledged = MachineIntegrationExchange.ReadProgress(
            directory.Path,
            handoff.TransactionId);

        Assert.Equal(acknowledgement, acknowledged.Acknowledgement);
        Assert.Null(acknowledged.Result);
        var exception = Assert.Throws<IntegrationContractException>(() =>
            MachineIntegrationExchange.ReadResult(directory.Path, handoff.TransactionId));
        Assert.Equal(IntegrationErrorCode.InvalidState, exception.ErrorCode);
    }

    [Fact]
    public void ReadResult_WhenRunRecordChanges_FailsClosed()
    {
        using var directory = new TemporaryDirectory();
        var project = directory.Write("source/project.ovmachine", [1]);
        var source = directory.Write("source/frame.c3d", [2]);
        var handoff = MachineIntegrationExchange.PublishHandoff(CreateRequest(
            directory.Path,
            project,
            source));
        var transaction = TransactionDirectory(directory.Path, handoff.TransactionId);
        var acknowledgement = new IntegrationAcknowledgement(
            IntegrationContractSchema.Legacy,
            IntegrationMessageKind.Acknowledgement,
            Guid.NewGuid(),
            handoff.TransactionId,
            handoff.MessageId,
            handoff.CreatedAtUtc.AddSeconds(1),
            ThreeDIdentity(),
            IntegrationAcknowledgementStatus.Accepted,
            null);
        File.WriteAllBytes(Path.Combine(transaction, "acknowledgement.json"), IntegrationContractJson.Serialize(acknowledgement));
        directory.WriteRelative(transaction, "artifacts/run-record.json", [3]);
        var result = new IntegrationResult(
            IntegrationContractSchema.Legacy,
            IntegrationMessageKind.Result,
            Guid.NewGuid(),
            handoff.TransactionId,
            handoff.MessageId,
            acknowledgement.MessageId,
            acknowledgement.CreatedAtUtc.AddSeconds(1),
            ThreeDIdentity(),
            IntegrationResultStatus.Completed,
            IntegrationInspectionDisposition.Pass,
            "run-1",
            new IntegrationArtifactReference(
                IntegrationArtifactRoles.RunRecord,
                "run-1",
                "artifacts/run-record.json",
                1,
                Convert.ToHexString(SHA256.HashData([3]))),
            null);
        File.WriteAllBytes(Path.Combine(transaction, "result.json"), IntegrationContractJson.Serialize(result));
        directory.WriteRelative(transaction, "artifacts/run-record.json", [4, 5]);

        var exception = Assert.Throws<IntegrationContractException>(() =>
            MachineIntegrationExchange.ReadResult(directory.Path, handoff.TransactionId));

        Assert.Equal(IntegrationErrorCode.ArtifactLengthMismatch, exception.ErrorCode);
    }

    [Fact]
    public void PublishHandoff_RejectsTraversingTargetNameBeforeCreatingTransaction()
    {
        using var directory = new TemporaryDirectory();
        var project = directory.Write("source/project.ovmachine", [1]);
        var request = CreateRequest(directory.Path, project, project) with
        {
            Artifacts =
            [
                new(
                    IntegrationArtifactRoles.MachineProject,
                    "project-1",
                    project,
                    "../project.ovmachine")
            ]
        };

        Assert.Throws<ArgumentException>(() =>
            MachineIntegrationExchange.PublishHandoff(request));
        Assert.False(Directory.Exists(Path.Combine(directory.Path, "transactions")));
    }

    [Fact]
    public void PublishHandoff_WhenContractIsInvalid_RemovesUnpublishedTransaction()
    {
        using var directory = new TemporaryDirectory();
        var source = directory.Write("source/frame.c3d", [2]);
        var request = CreateRequest(directory.Path, source, source) with
        {
            Artifacts =
            [
                new(
                    IntegrationArtifactRoles.InspectionSource,
                    "frame-1",
                    source,
                    "frame.c3d")
            ]
        };

        Assert.Throws<IntegrationContractException>(() =>
            MachineIntegrationExchange.PublishHandoff(request));
        Assert.Empty(Directory.GetDirectories(
            Path.Combine(directory.Path, "transactions")));
    }

    private static MachineHandoffRequest CreateRequest(
        string exchangeRoot,
        string project,
        string source) => new(
        exchangeRoot,
        new IntegrationApplicationIdentity(
            IntegrationApplicationIds.MachineStudio,
            "0.1.0-rc.4",
            "1111111111111111111111111111111111111111",
            IntegrationSourceState.Clean),
        "project-1",
        "1.11",
        "automatic",
        "inspect",
        "camera-1",
        "mm",
        "camera-frame",
        [
            new(
                IntegrationArtifactRoles.MachineProject,
                "project-1",
                project,
                "project.ovmachine"),
            new(
                IntegrationArtifactRoles.InspectionSource,
                "frame-1",
                source,
                "frame.c3d")
        ]);

    private static IntegrationApplicationIdentity ThreeDIdentity() => new(
        IntegrationApplicationIds.ThreeDStudio,
        "0.2.0-dev",
        "2222222222222222222222222222222222222222",
        IntegrationSourceState.Clean);

    private static string TransactionDirectory(string root, Guid transactionId) =>
        Path.Combine(root, "transactions", transactionId.ToString("D"));

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "OpenVisionLab-Machine-Integration-Tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string Write(string relativePath, byte[] bytes) =>
            WriteRelative(Path, relativePath, bytes);

        public string WriteRelative(string root, string relativePath, byte[] bytes)
        {
            var fullPath = System.IO.Path.Combine(
                root,
                relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(fullPath)!);
            File.WriteAllBytes(fullPath, bytes);
            return fullPath;
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
