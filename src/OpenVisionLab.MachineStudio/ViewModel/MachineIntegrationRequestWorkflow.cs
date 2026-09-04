using System.IO;
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
    IntegrationApplicationIdentity Producer,
    IntegrationApplicationIdentity Consumer);

/// <summary>
/// Applies the policy that makes a current Machine Studio camera result
/// eligible for a two-dimensional inspection handoff.
/// </summary>
internal sealed class MachineIntegrationRequestWorkflow
{
    private readonly MachineIntegrationHandoffRequestFactory _factory = new();

    internal MachineInspectionHandoffRequest? TryCreate(
        MachineIntegrationRequestContext context,
        string inspectionRecipePath)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!context.IsExactCommit
            || context.Consumer.ApplicationId != IntegrationApplicationIds.TwoDStudio
            || context.Consumer.SourceState != IntegrationSourceState.Clean
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
            || !File.Exists(inspectionRecipePath))
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
                context.Producer,
                context.Consumer));
    }
}
