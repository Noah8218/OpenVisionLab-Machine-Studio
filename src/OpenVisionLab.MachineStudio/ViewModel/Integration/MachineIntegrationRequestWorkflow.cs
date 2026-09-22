using OpenVisionLab.Integration.Contracts;
using OpenVisionLab.Machine.Core.Devices;
using OpenVisionLab.Machine.Core.Sequences;
using OpenVisionLab.Machine.Infrastructure.Integration;
using OpenVisionLab.Machine.Simulation.Camera;

namespace OpenVisionLab.MachineStudio.ViewModel;

internal sealed record MachineIntegrationRequestContext(
    bool IsExactCommit,
    string ProjectId,
    string ProjectSchema,
    IReadOnlyList<SequenceDefinition> Sequences,
    string? ProjectPath,
    string? CameraId,
    string? CameraRecipe,
    VirtualCameraSnapshot? CurrentCamera,
    VirtualSingleImageSourceDefinition? SourceDefinition,
    MachineIntegrationHeightMapSourceDefinition? HeightMapSource = null,
    MachineIntegrationArtifactEvidence? InspectionRecipeEvidence = null,
    string? ExpectedSequenceId = null,
    string? ExpectedStepId = null,
    string? ExpectedDeviceId = null);

/// <summary>
/// Applies the policy that makes a current Machine Studio sequence acquisition
/// eligible for a 2D Image or 3D HeightMap inspection handoff.
/// </summary>
internal sealed class MachineIntegrationRequestWorkflow
{
    private readonly MachineIntegrationHandoffRequestFactory _factory = new();
    private readonly MachineIntegrationPathReadinessPolicy _pathReadiness;

    internal MachineIntegrationRequestWorkflow(
        MachineIntegrationPathReadinessPolicy? pathReadiness = null) =>
        _pathReadiness = pathReadiness ?? new MachineIntegrationPathReadinessPolicy();

    // Preserve the shell's refresh identity: metadata-only changes do not invalidate this key.
    internal static string CreateRefreshKey(MachineIntegrationRequestContext context)
    {
        var frame = context.CurrentCamera?.Result?.FrameEvidence ??
            context.CurrentCamera?.FrameEvidence;
        return string.Join(
            "\u001F",
            context.ProjectId,
            context.ProjectPath ?? string.Empty,
            context.CameraId ?? string.Empty,
            context.CameraRecipe ?? string.Empty,
            context.CurrentCamera?.State.ToString() ?? string.Empty,
            context.CurrentCamera?.CurrentAcquisitionId ?? string.Empty,
            frame?.FrameId ?? string.Empty,
            frame?.ContentSha256 ?? string.Empty,
            frame?.SourceRelativePath ?? string.Empty);
    }

    internal MachineInspectionHandoffRequest? TryCreate(
        MachineIntegrationRequestContext context,
        string inspectionRecipePath,
        IntegrationApplicationIdentity producer,
        IntegrationApplicationIdentity consumer)
    {
        ArgumentNullException.ThrowIfNull(context);

        var isTwoD = consumer.ApplicationId == IntegrationApplicationIds.TwoDStudio;
        var isThreeD = consumer.ApplicationId == IntegrationApplicationIds.ThreeDStudio;
        if (!context.IsExactCommit
            || (!isTwoD && !isThreeD)
            || consumer.SourceState != IntegrationSourceState.Clean
            || string.IsNullOrWhiteSpace(context.ProjectPath)
            || string.IsNullOrWhiteSpace(context.CameraId)
            || string.IsNullOrWhiteSpace(context.CameraRecipe)
            || context.CurrentCamera is not
                {
                    CurrentAcquisitionId: { Length: > 0 } acquisitionId
                }
            || context.CurrentCamera.State is not (
                VirtualCameraState.FrameReady
                or VirtualCameraState.AwaitingExternalResult)
            || string.IsNullOrWhiteSpace(inspectionRecipePath)
            || !_pathReadiness.IsInspectionRecipeAvailable(inspectionRecipePath)
            || (isTwoD && (!_pathReadiness.IsInspectionRecipeSupportedByTwoD(inspectionRecipePath)
                || context.SourceDefinition is not { }))
            || (isThreeD && (!_pathReadiness.IsInspectionRecipeSupportedByThreeD(inspectionRecipePath)
                || context.HeightMapSource is not { }
                || context.InspectionRecipeEvidence is not { }
                || string.IsNullOrWhiteSpace(context.HeightMapSource.FrameId)
                || string.IsNullOrWhiteSpace(context.ExpectedSequenceId)
                || string.IsNullOrWhiteSpace(context.ExpectedStepId)
                || string.IsNullOrWhiteSpace(context.ExpectedDeviceId)))
            || (isThreeD && context.HeightMapSource is not { Evidence: { } }))
        {
            return null;
        }

        var frame = context.CurrentCamera.Result?.FrameEvidence ??
            context.CurrentCamera.FrameEvidence;
        if (frame is null)
        {
            return null;
        }

        if (isThreeD
            && context.HeightMapSource is { } configuredSource
            && (!string.Equals(
                    frame.SourceRelativePath,
                    configuredSource.SourceRelativePath,
                    StringComparison.OrdinalIgnoreCase)
                || !string.Equals(
                    frame.ContentSha256,
                    configuredSource.Evidence.ContentSha256,
                    StringComparison.OrdinalIgnoreCase)
                || frame.ContentLength != configuredSource.Evidence.ContentLength
                || frame.Width != configuredSource.Width
                || frame.Height != configuredSource.Height
                || !string.Equals(frame.PixelFormat, configuredSource.PixelFormat, StringComparison.Ordinal)))
        {
            return null;
        }

        var trigger = context.Sequences
            .SelectMany(sequence => sequence.Steps.Select(step => (Sequence: sequence, Step: step)))
            .FirstOrDefault(candidate =>
                candidate.Step.Action == SequenceStepAction.TriggerCamera
                && string.Equals(candidate.Step.TargetId, context.CameraId, StringComparison.Ordinal)
                && string.Equals(candidate.Step.Parameter, context.CameraRecipe, StringComparison.Ordinal)
                && (string.IsNullOrWhiteSpace(context.ExpectedSequenceId)
                    || string.Equals(candidate.Sequence.Id, context.ExpectedSequenceId, StringComparison.Ordinal))
                && (!isThreeD
                    || (string.Equals(candidate.Sequence.Id, context.ExpectedSequenceId, StringComparison.Ordinal)
                        && string.Equals(candidate.Step.Id, context.ExpectedStepId, StringComparison.Ordinal)
                        && string.Equals(candidate.Step.TargetId, context.ExpectedDeviceId, StringComparison.Ordinal))));
        if (trigger.Sequence is null)
        {
            return null;
        }

        var sourceRelativePath = isThreeD
            ? context.HeightMapSource!.SourceRelativePath
            : context.SourceDefinition!.SourceRelativePath;
        var sourceWidth = isThreeD
            ? context.HeightMapSource!.Width
            : context.SourceDefinition!.Width;
        var sourceHeight = isThreeD
            ? context.HeightMapSource!.Height
            : context.SourceDefinition!.Height;
        var unit = isThreeD ? context.HeightMapSource!.Unit : "mm";

        return _factory.Create(
            new MachineIntegrationHandoffRequestInput(
                context.ProjectId,
                context.ProjectSchema,
                trigger.Sequence.Id,
                trigger.Step.Id,
                context.CameraId,
                acquisitionId,
                context.ProjectPath,
                sourceRelativePath,
                sourceWidth,
                sourceHeight,
                inspectionRecipePath,
                new MachineIntegrationFrameEvidence(
                    isThreeD ? context.HeightMapSource!.FrameId : frame.FrameId,
                    frame.SourceRelativePath,
                    frame.ContentSha256,
                    frame.ContentLength),
                producer,
                consumer,
                isThreeD
                    ? IntegrationInspectionModality.ThreeD
                    : IntegrationInspectionModality.TwoD,
                isThreeD
                    ? IntegrationInspectionInputKind.HeightMap
                    : IntegrationInspectionInputKind.Image,
                unit,
                isThreeD ? context.InspectionRecipeEvidence : null));
    }
}
