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
    VirtualSingleImageSourceDefinition? SourceDefinition);

/// <summary>
/// Applies the policy that makes a current Machine Studio camera result
/// eligible for a two-dimensional inspection handoff.
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

        if (!context.IsExactCommit
            || consumer.ApplicationId != IntegrationApplicationIds.TwoDStudio
            || consumer.SourceState != IntegrationSourceState.Clean
            || string.IsNullOrWhiteSpace(context.ProjectPath)
            || string.IsNullOrWhiteSpace(context.CameraId)
            || string.IsNullOrWhiteSpace(context.CameraRecipe)
            || context.CurrentCamera is not
                {
                    State: VirtualCameraState.FrameReady,
                    CurrentAcquisitionId: { Length: > 0 } acquisitionId
                }
            || context.SourceDefinition is not { } sourceDefinition
            || string.IsNullOrWhiteSpace(inspectionRecipePath)
            || !_pathReadiness.IsInspectionRecipeAvailable(inspectionRecipePath))
        {
            return null;
        }

        var frame = context.CurrentCamera.Result?.FrameEvidence ??
            context.CurrentCamera.FrameEvidence;
        if (frame is null)
        {
            return null;
        }

        var trigger = context.Sequences
            .SelectMany(sequence => sequence.Steps.Select(step => (Sequence: sequence, Step: step)))
            .FirstOrDefault(candidate =>
                candidate.Step.Action == SequenceStepAction.TriggerCamera
                && string.Equals(candidate.Step.TargetId, context.CameraId, StringComparison.Ordinal)
                && string.Equals(candidate.Step.Parameter, context.CameraRecipe, StringComparison.Ordinal));
        if (trigger.Sequence is null)
        {
            return null;
        }

        return _factory.Create(
            new MachineIntegrationHandoffRequestInput(
                context.ProjectId,
                context.ProjectSchema,
                trigger.Sequence.Id,
                trigger.Step.Id,
                context.CameraId,
                acquisitionId,
                context.ProjectPath,
                sourceDefinition.SourceRelativePath,
                sourceDefinition.Width,
                sourceDefinition.Height,
                inspectionRecipePath,
                frame,
                producer,
                consumer));
    }
}
