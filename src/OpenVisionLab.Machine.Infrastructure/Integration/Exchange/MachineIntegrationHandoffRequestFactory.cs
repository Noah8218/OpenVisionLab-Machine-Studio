using System.IO;
using System.Security.Cryptography;
using OpenVisionLab.Integration.Contracts;
using OpenVisionLab.Machine.Infrastructure.Vision;

namespace OpenVisionLab.Machine.Infrastructure.Integration;

/// <summary>
/// Immutable frame identity passed from a camera application into the
/// integration boundary. It contains no Simulation or WPF type.
/// </summary>
public sealed record MachineIntegrationFrameEvidence
{
    public MachineIntegrationFrameEvidence(
        string frameId,
        string sourceRelativePath,
        string contentSha256,
        long contentLength)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(frameId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRelativePath);
        if (Path.IsPathRooted(sourceRelativePath)
            || sourceRelativePath.Split(['/', '\\']).Any(segment => segment == ".."))
        {
            throw new ArgumentException(
                "Frame source must be a project-relative path without parent traversal.",
                nameof(sourceRelativePath));
        }
        if (string.IsNullOrWhiteSpace(contentSha256)
            || contentSha256.Length != 64
            || contentSha256.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException(
                "Content SHA-256 must contain exactly 64 hexadecimal characters.",
                nameof(contentSha256));
        }
        if (contentLength <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(contentLength));
        }

        FrameId = frameId;
        SourceRelativePath = sourceRelativePath;
        ContentSha256 = contentSha256.ToUpperInvariant();
        ContentLength = contentLength;
    }

    public string FrameId { get; }

    public string SourceRelativePath { get; }

    public string ContentSha256 { get; }

    public long ContentLength { get; }
}

/// <summary>
/// Input snapshot for creating a two-dimensional inspection handoff. The
/// caller owns Simulation-context eligibility; this owner verifies the source
/// file and composes the external request.
/// </summary>
public sealed record MachineIntegrationHandoffRequestInput(
    string ProjectId,
    string ProjectSchema,
    string SequenceId,
    string StepId,
    string CameraId,
    string AcquisitionId,
    string ProjectPath,
    string SourceRelativePath,
    int SourceWidth,
    int SourceHeight,
    string InspectionRecipePath,
    MachineIntegrationFrameEvidence FrameEvidence,
    IntegrationApplicationIdentity Producer,
    IntegrationApplicationIdentity Consumer,
    IntegrationInspectionModality Modality = IntegrationInspectionModality.TwoD,
    IntegrationInspectionInputKind InputKind = IntegrationInspectionInputKind.Image,
    string Unit = "mm",
    MachineIntegrationArtifactEvidence? InspectionRecipeEvidence = null);

/// <summary>
/// Verifies a project-owned camera source against immutable frame evidence and
/// creates the external inspection request. It is independent
/// of the WPF host and Simulation project.
/// </summary>
public sealed class MachineIntegrationHandoffRequestFactory
{
    public MachineInspectionHandoffRequest? Create(
        MachineIntegrationHandoffRequestInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        try
        {
            var projectPath = Path.GetFullPath(input.ProjectPath);
            var projectRoot = Path.GetDirectoryName(projectPath);
            if (string.IsNullOrWhiteSpace(projectRoot))
            {
                return null;
            }

            var source = new ProjectAssetPathResolver(projectRoot)
                .ResolveExistingFile(input.SourceRelativePath);
            var sourceInfo = new FileInfo(source.FullPath);
            var sourceHash = Convert.ToHexString(
                SHA256.HashData(File.ReadAllBytes(source.FullPath)));
            if (sourceInfo.Length != input.FrameEvidence.ContentLength
                || !string.Equals(
                    sourceHash,
                    input.FrameEvidence.ContentSha256,
                    StringComparison.OrdinalIgnoreCase)
                || !string.Equals(
                    NormalizeProjectRelativePath(source.RelativePath),
                    NormalizeProjectRelativePath(input.FrameEvidence.SourceRelativePath),
                    StringComparison.Ordinal))
            {
                return null;
            }

            if (input.InspectionRecipeEvidence is { } expectedRecipe)
            {
                var recipeInfo = new FileInfo(Path.GetFullPath(input.InspectionRecipePath));
                if (recipeInfo.Length != expectedRecipe.ContentLength
                    || !string.Equals(
                        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(recipeInfo.FullName))),
                        expectedRecipe.ContentSha256,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }
            }

            return new MachineInspectionHandoffRequest(
                input.ProjectId,
                input.ProjectSchema,
                input.SequenceId,
                input.StepId,
                input.CameraId,
                input.AcquisitionId,
                input.FrameEvidence.FrameId,
                input.Unit,
                projectPath,
                source.FullPath,
                Path.GetFullPath(input.InspectionRecipePath),
                input.Modality,
                input.InputKind,
                input.Producer,
                input.Consumer)
            {
                ProjectionProfile = input.Modality == IntegrationInspectionModality.TwoD
                    ? MachineCoordinateProjectionContract.CreateDefault(
                        MachineCoordinateProjectionContract.CreateProjectionId(
                            input.ProjectId,
                            input.CameraId,
                            input.AcquisitionId),
                        input.SourceWidth,
                        input.SourceHeight)
                    : null
            };
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or InvalidOperationException
            or IntegrationContractException)
        {
            return null;
        }
    }

    private static string NormalizeProjectRelativePath(string path) =>
        path.Replace(Path.DirectorySeparatorChar, '/')
            .Replace(Path.AltDirectorySeparatorChar, '/');
}
