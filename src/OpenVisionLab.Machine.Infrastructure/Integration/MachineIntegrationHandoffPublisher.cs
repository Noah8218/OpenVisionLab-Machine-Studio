using System.Security.Cryptography;
using System.Text;
using OpenVisionLab.Integration.Contracts;

namespace OpenVisionLab.Machine.Infrastructure.Integration;

public sealed record MachineInspectionHandoffRequest(
    string ProjectId,
    string ProjectSchema,
    string SequenceId,
    string StepId,
    string CameraId,
    string AcquisitionId,
    string FrameId,
    string Unit,
    string MachineProjectPath,
    string InspectionSourcePath,
    string InspectionRecipePath,
    IntegrationInspectionModality Modality,
    IntegrationInspectionInputKind InputKind,
    IntegrationApplicationIdentity Producer,
    IntegrationApplicationIdentity Consumer)
{
    /// <summary>
    /// Optional atomic sidecar that declares the software image/grid mapping.
    /// The publisher copies it as a normal Handoff artifact; it never infers
    /// calibration or executes either consumer.
    /// </summary>
    public MachineCoordinateProjectionProfile? ProjectionProfile { get; init; }
}

/// <summary>
/// Builds the two consumer-specific Handoff shapes from project-owned files
/// and delegates the actual transactional copy/hash publication to
/// MachineIntegrationExchange. It deliberately does not start a consumer
/// inspection.
/// </summary>
public static class MachineIntegrationHandoffPublisher
{
    public static Task<IntegrationHandoffV2> PublishAsync(
        string exchangeRoot,
        MachineInspectionHandoffRequest request,
        IProgress<MachineIntegrationTransferProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        PublishCoreAsync(
            exchangeRoot,
            request,
            progress,
            cancellationToken);

    private static async Task<IntegrationHandoffV2> PublishCoreAsync(
        string exchangeRoot,
        MachineInspectionHandoffRequest request,
        IProgress<MachineIntegrationTransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exchangeRoot);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Producer);
        ArgumentNullException.ThrowIfNull(request.Consumer);
        ValidateRequest(request);
        var locatorTemplatePath = request.Modality == IntegrationInspectionModality.TwoD
            ? MachineLocatorIntegrationRecipeContract.ResolveTemplatePath(request.InspectionRecipePath)
            : null;

        var project = CreateArtifactReference(
            IntegrationArtifactRoles.MachineProject,
            "machine-project",
            request.MachineProjectPath,
            "artifacts/machine-project.ovmachine");
        var source = CreateArtifactReference(
            IntegrationArtifactRoles.InspectionSource,
            "inspection-source",
            request.InspectionSourcePath,
            $"artifacts/inspection-source{ResolveSourceExtension(request.InspectionSourcePath)}");
        var recipe = CreateArtifactReference(
            IntegrationArtifactRoles.InspectionRecipe,
            "inspection-recipe",
            request.InspectionRecipePath,
            $"artifacts/inspection-recipe{ResolveRecipeExtension(request.InspectionRecipePath)}");
        string? projectionProfileSourcePath = null;
        try
        {
            var artifacts = new List<IntegrationArtifactReference>
            {
                project,
                source,
                recipe
            };
            var artifactSourcePaths = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [project.ArtifactId] = Path.GetFullPath(request.MachineProjectPath),
                [source.ArtifactId] = Path.GetFullPath(request.InspectionSourcePath),
                [recipe.ArtifactId] = Path.GetFullPath(request.InspectionRecipePath)
            };

            if (locatorTemplatePath is not null)
            {
                var locatorTemplate = CreateArtifactReference(
                    MachineLocatorIntegrationRecipeContract.TemplateArtifactRole,
                    MachineLocatorIntegrationRecipeContract.TemplateArtifactId,
                    locatorTemplatePath,
                    $"artifacts/locator-template{ResolveSourceExtension(locatorTemplatePath)}");
                artifacts.Add(locatorTemplate);
                artifactSourcePaths[locatorTemplate.ArtifactId] = locatorTemplatePath;
            }

            if (request.ProjectionProfile is { } projectionProfile)
            {
                MachineCoordinateProjectionContract.Validate(projectionProfile);
                projectionProfileSourcePath = WriteTemporaryProjectionProfile(
                    exchangeRoot,
                    projectionProfile);
                var profileArtifact = CreateArtifactReference(
                    MachineCoordinateProjectionContract.ProfileArtifactRole,
                    MachineCoordinateProjectionContract.ProfileArtifactId,
                    projectionProfileSourcePath,
                    "artifacts/coordinate-projection-profile.json");
                artifacts.Add(profileArtifact);
                artifactSourcePaths[profileArtifact.ArtifactId] = projectionProfileSourcePath;
            }

            var handoff = new IntegrationHandoffV2(
                IntegrationContractSchema.V2,
                IntegrationMessageKind.Handoff,
                Guid.NewGuid(),
                Guid.NewGuid(),
                DateTimeOffset.UtcNow,
                request.Producer,
                new IntegrationInspectionContextV2(
                    request.ProjectId,
                    request.ProjectSchema,
                    request.SequenceId,
                    request.StepId,
                    request.CameraId,
                    request.AcquisitionId,
                    request.FrameId,
                    request.Unit,
                    request.Modality,
                    request.InputKind,
                    source.Sha256,
                    recipe.Sha256,
                    request.Consumer,
                    artifacts));

            return await MachineIntegrationExchange.PublishHandoffAsync(
                    exchangeRoot,
                    handoff,
                    artifactSourcePaths,
                    progress,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            TryDeleteFile(projectionProfileSourcePath);
        }
    }

    private static void ValidateRequest(MachineInspectionHandoffRequest request)
    {
        if (request.Modality == IntegrationInspectionModality.TwoD
            && request.InputKind != IntegrationInspectionInputKind.Image)
        {
            throw new ArgumentException(
                "A TwoD Handoff must declare Image input.",
                nameof(request));
        }
        if (request.Modality == IntegrationInspectionModality.ThreeD
            && request.InputKind != IntegrationInspectionInputKind.HeightMap)
        {
            throw new ArgumentException(
                "The current 3D Handoff path requires HeightMap input.",
                nameof(request));
        }
        if (request.Modality is not (IntegrationInspectionModality.TwoD or IntegrationInspectionModality.ThreeD))
        {
            throw new ArgumentException(
                "Only TwoD and ThreeD Handoff publication is supported by this local slice.",
                nameof(request));
        }
        var expectedConsumer = request.Modality == IntegrationInspectionModality.TwoD
            ? IntegrationApplicationIds.TwoDStudio
            : IntegrationApplicationIds.ThreeDStudio;
        if (!string.Equals(
                request.Consumer.ApplicationId,
                expectedConsumer,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"The consumer application must be '{expectedConsumer}' for the declared modality.",
                nameof(request));
        }
        if (!string.Equals(
                request.Producer.ApplicationId,
                IntegrationApplicationIds.MachineStudio,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The Handoff producer must be Machine Studio.",
                nameof(request));
        }

        RequireText(request.ProjectId, nameof(request.ProjectId));
        RequireText(request.ProjectSchema, nameof(request.ProjectSchema));
        RequireText(request.SequenceId, nameof(request.SequenceId));
        RequireText(request.StepId, nameof(request.StepId));
        RequireText(request.CameraId, nameof(request.CameraId));
        RequireText(request.AcquisitionId, nameof(request.AcquisitionId));
        RequireText(request.FrameId, nameof(request.FrameId));
        RequireText(request.Unit, nameof(request.Unit));
        RequireFile(request.MachineProjectPath, nameof(request.MachineProjectPath));
        RequireFile(request.InspectionSourcePath, nameof(request.InspectionSourcePath));
        RequireFile(request.InspectionRecipePath, nameof(request.InspectionRecipePath));
        if (request.ProjectionProfile is { } projectionProfile)
        {
            MachineCoordinateProjectionContract.Validate(projectionProfile);
        }
    }

    private static IntegrationArtifactReference CreateArtifactReference(
        string role,
        string artifactId,
        string sourcePath,
        string relativePath)
    {
        string fullPath = Path.GetFullPath(sourcePath);
        using var stream = File.OpenRead(fullPath);
        return new(
            role,
            artifactId,
            relativePath,
            stream.Length,
            Convert.ToHexString(SHA256.HashData(stream)));
    }

    private static string ResolveSourceExtension(string sourcePath)
    {
        var extension = Path.GetExtension(sourcePath);
        return string.IsNullOrWhiteSpace(extension)
            ? ".bin"
            : extension.ToLowerInvariant();
    }

    private static string ResolveRecipeExtension(string recipePath)
    {
        var extension = Path.GetExtension(recipePath);
        return string.IsNullOrWhiteSpace(extension)
            ? ".json"
            : extension.ToLowerInvariant();
    }

    private static void RequireFile(string path, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(path)
            || !File.Exists(path)
            || new FileInfo(path).Length <= 0)
        {
            throw new ArgumentException(
                "A non-empty source file is required.",
                parameterName);
        }
    }

    private static void RequireText(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException(
                "A non-empty context identity is required.",
                parameterName);
        }
    }

    private static string WriteTemporaryProjectionProfile(
        string exchangeRoot,
        MachineCoordinateProjectionProfile profile)
    {
        var root = Path.GetFullPath(exchangeRoot);
        Directory.CreateDirectory(root);
        var path = Path.Combine(
            root,
            $".coordinate-projection-profile-{Guid.NewGuid():N}.json");
        File.WriteAllText(
            path,
            MachineCoordinateProjectionContract.SerializeProfile(profile),
            new UTF8Encoding(false));
        return path;
    }

    private static void TryDeleteFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Preserve the publication result; the exchange owns the durable copy.
        }
    }
}
