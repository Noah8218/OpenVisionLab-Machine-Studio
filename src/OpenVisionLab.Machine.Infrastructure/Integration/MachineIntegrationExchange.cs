using System.Security.Cryptography;
using OpenVisionLab.Integration.Contracts;

namespace OpenVisionLab.Machine.Infrastructure.Integration;

public sealed record MachineHandoffArtifactSource(
    string Role,
    string ArtifactId,
    string SourcePath,
    string TargetFileName);

public sealed record MachineHandoffRequest(
    string ExchangeRoot,
    IntegrationApplicationIdentity Producer,
    string ProjectId,
    string ProjectSchema,
    string SequenceId,
    string StepId,
    string CameraId,
    string Unit,
    string FrameId,
    IReadOnlyList<MachineHandoffArtifactSource> Artifacts);

public sealed record MachineIntegrationResult(
    IntegrationHandoff Handoff,
    IntegrationAcknowledgement Acknowledgement,
    IntegrationResult Result);

public sealed record MachineIntegrationProgress(
    IntegrationHandoff Handoff,
    IntegrationAcknowledgement? Acknowledgement,
    IntegrationResult? Result);

/// <summary>
/// Owns explicit Machine-side file exchange. It never saves a project, starts
/// simulation, or invokes a 3D workflow.
/// </summary>
public static class MachineIntegrationExchange
{
    public static IntegrationHandoff PublishHandoff(MachineHandoffRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ExchangeRoot);
        ValidateArtifactSources(request.Artifacts);

        var transactionId = Guid.NewGuid();
        var transactionDirectory = GetTransactionDirectory(
            request.ExchangeRoot,
            transactionId);
        var artifactsDirectory = Path.Combine(
            transactionDirectory,
            IntegrationTransactionLayout.ArtifactsDirectoryName);
        Directory.CreateDirectory(artifactsDirectory);

        try
        {
            var artifacts = request.Artifacts
                .Select(source => CopyArtifact(source, artifactsDirectory))
                .ToArray();
            var handoff = new IntegrationHandoff(
                // The public Release 2 branch keeps this established V1 file
                // exchange readable while the shared package is upgraded.
                // TCP transfers are schema-agnostic; V2 domain qualification
                // remains a separate Release 2 gate.
                IntegrationContractSchema.Legacy,
                IntegrationMessageKind.Handoff,
                Guid.NewGuid(),
                transactionId,
                DateTimeOffset.UtcNow,
                request.Producer,
                new MachineInspectionContext(
                    request.ProjectId,
                    request.ProjectSchema,
                    request.SequenceId,
                    request.StepId,
                    request.CameraId,
                    request.Unit,
                    request.FrameId,
                    artifacts));

            WriteNewMessage(
                transactionDirectory,
                IntegrationTransactionLayout.HandoffFileName,
                IntegrationContractJson.Serialize(handoff));
            return handoff;
        }
        catch
        {
            try
            {
                Directory.Delete(transactionDirectory, recursive: true);
            }
            catch
            {
                // Preserve the contract or I/O failure that prevented publication.
            }
            throw;
        }
    }

    public static MachineIntegrationResult ReadResult(
        string exchangeRoot,
        Guid transactionId)
    {
        var progress = ReadProgress(exchangeRoot, transactionId);
        if (progress.Acknowledgement is null || progress.Result is null)
        {
            throw new IntegrationContractException(
                IntegrationErrorCode.InvalidState,
                "The integration transaction does not have a completed Result.");
        }

        return new(progress.Handoff, progress.Acknowledgement, progress.Result);
    }

    public static MachineIntegrationProgress ReadProgress(
        string exchangeRoot,
        Guid transactionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exchangeRoot);
        if (transactionId == Guid.Empty)
        {
            throw new ArgumentException(
                "Transaction identity cannot be empty.",
                nameof(transactionId));
        }

        var transactionDirectory = GetTransactionDirectory(exchangeRoot, transactionId);
        var handoff = IntegrationContractJson.DeserializeHandoff(
            ReadMessage(transactionDirectory, IntegrationTransactionLayout.HandoffFileName));
        foreach (var artifact in handoff.Context.Artifacts)
        {
            ThrowIfInvalid(IntegrationContractValidator.ValidateArtifactFile(
                artifact,
                transactionDirectory));
        }

        var acknowledgementPath = Path.Combine(
            transactionDirectory,
            IntegrationTransactionLayout.AcknowledgementFileName);
        if (!File.Exists(acknowledgementPath))
        {
            return new(handoff, null, null);
        }

        var acknowledgement = IntegrationContractJson.DeserializeAcknowledgement(
            File.ReadAllBytes(acknowledgementPath));
        ThrowIfInvalid(IntegrationContractValidator.ValidateSequence(
            handoff,
            acknowledgement));

        var resultPath = Path.Combine(
            transactionDirectory,
            IntegrationTransactionLayout.ResultFileName);
        if (!File.Exists(resultPath))
        {
            return new(handoff, acknowledgement, null);
        }

        var result = IntegrationContractJson.DeserializeResult(File.ReadAllBytes(resultPath));
        ThrowIfInvalid(IntegrationContractValidator.ValidateSequence(
            handoff,
            acknowledgement,
            result));
        if (result.RunRecord is not null)
        {
            ThrowIfInvalid(IntegrationContractValidator.ValidateArtifactFile(
                result.RunRecord,
                transactionDirectory));
        }

        return new(handoff, acknowledgement, result);
    }

    private static void ValidateArtifactSources(
        IReadOnlyList<MachineHandoffArtifactSource>? sources)
    {
        if (sources is null || sources.Count == 0)
        {
            throw new ArgumentException("At least one Handoff artifact is required.", nameof(sources));
        }

        foreach (var source in sources)
        {
            ArgumentNullException.ThrowIfNull(source);
            ArgumentException.ThrowIfNullOrWhiteSpace(source.Role);
            ArgumentException.ThrowIfNullOrWhiteSpace(source.ArtifactId);
            ArgumentException.ThrowIfNullOrWhiteSpace(source.SourcePath);
            if (!File.Exists(source.SourcePath))
            {
                throw new FileNotFoundException("Handoff artifact was not found.", source.SourcePath);
            }
            if (string.IsNullOrWhiteSpace(source.TargetFileName)
                || !string.Equals(
                    source.TargetFileName,
                    Path.GetFileName(source.TargetFileName),
                    StringComparison.Ordinal)
                || source.TargetFileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                throw new ArgumentException(
                    "Artifact target must be one safe file name.",
                    nameof(sources));
            }
        }

        if (sources.Select(source => source.TargetFileName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count() != sources.Count)
        {
            throw new ArgumentException("Artifact target file names must be unique.", nameof(sources));
        }
    }

    private static IntegrationArtifactReference CopyArtifact(
        MachineHandoffArtifactSource source,
        string artifactsDirectory)
    {
        var targetPath = Path.Combine(artifactsDirectory, source.TargetFileName);
        File.Copy(source.SourcePath, targetPath, overwrite: false);
        using var stream = File.OpenRead(targetPath);
        return new(
            source.Role,
            source.ArtifactId,
            $"{IntegrationTransactionLayout.ArtifactsDirectoryName}/{source.TargetFileName}",
            stream.Length,
            Convert.ToHexString(SHA256.HashData(stream)));
    }

    private static string GetTransactionDirectory(string exchangeRoot, Guid transactionId) =>
        Path.Combine(
            Path.GetFullPath(exchangeRoot),
            IntegrationTransactionLayout.TransactionsDirectoryName,
            transactionId.ToString("D"));

    private static byte[] ReadMessage(string transactionDirectory, string fileName) =>
        File.ReadAllBytes(Path.Combine(transactionDirectory, fileName));

    private static void WriteNewMessage(
        string transactionDirectory,
        string fileName,
        byte[] bytes)
    {
        var target = Path.Combine(transactionDirectory, fileName);
        var temporary = Path.Combine(
            transactionDirectory,
            $".{fileName}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, target);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static void ThrowIfInvalid(IntegrationValidationResult validation)
    {
        if (validation.IsValid)
        {
            return;
        }

        var issue = validation.Issues[0];
        throw new IntegrationContractException(
            issue.Code,
            $"{issue.Field}: {issue.Message}");
    }
}
