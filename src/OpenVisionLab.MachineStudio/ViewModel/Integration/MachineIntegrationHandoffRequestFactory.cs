using System.IO;
using System.Security.Cryptography;
using OpenVisionLab.Integration.Contracts;
using OpenVisionLab.Machine.Infrastructure.Integration;
using OpenVisionLab.Machine.Infrastructure.Vision;
using OpenVisionLab.Machine.Simulation.Camera;

namespace OpenVisionLab.MachineStudio.ViewModel;

internal sealed record MachineIntegrationHandoffRequestInput(
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
    VirtualCameraFrameEvidence FrameEvidence,
    IntegrationApplicationIdentity Producer,
    IntegrationApplicationIdentity Consumer);

internal sealed class MachineIntegrationHandoffRequestFactory
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

            return new MachineInspectionHandoffRequest(
                input.ProjectId,
                input.ProjectSchema,
                input.SequenceId,
                input.StepId,
                input.CameraId,
                input.AcquisitionId,
                input.FrameEvidence.FrameId,
                "mm",
                projectPath,
                source.FullPath,
                Path.GetFullPath(input.InspectionRecipePath),
                IntegrationInspectionModality.TwoD,
                IntegrationInspectionInputKind.Image,
                input.Producer,
                input.Consumer)
            {
                ProjectionProfile = MachineCoordinateProjectionContract.CreateDefault(
                    MachineCoordinateProjectionContract.CreateProjectionId(
                        input.ProjectId,
                        input.CameraId,
                        input.AcquisitionId),
                    input.SourceWidth,
                    input.SourceHeight)
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
