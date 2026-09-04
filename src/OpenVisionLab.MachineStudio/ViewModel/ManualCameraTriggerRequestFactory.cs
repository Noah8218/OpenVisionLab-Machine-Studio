using OpenVisionLab.Machine.Core.Devices;
using OpenVisionLab.Machine.Simulation.Camera;
using OpenVisionLab.Machine.Simulation.Engine;
using OpenVisionLab.Machine.Simulation.Snapshots;

namespace OpenVisionLab.MachineStudio.ViewModel;

internal sealed record ManualCameraTriggerRequestInput(
    bool IsAllowed,
    string ProjectId,
    string ProjectName,
    string? ProjectPath,
    string ProjectJson,
    string BuildIdentity,
    TimeSpan SimulationFixedStep,
    SimulationSnapshot Snapshot,
    VirtualCameraSnapshot? CurrentCamera,
    string? SelectedRecipe,
    VirtualCameraDefinition? CameraDefinition,
    VirtualSingleImageSourceDefinition? SourceDefinition,
    int SimulationSeed);

/// <summary>
/// Builds the immutable input for one manual camera trigger. Runtime state,
/// file access, cancellation, and command dispatch remain owned by the
/// existing ManualCameraTriggerWorkflow and its collaborators.
/// </summary>
internal sealed class ManualCameraTriggerRequestFactory
{
    internal ManualCameraTriggerRequest? TryCreate(
        ManualCameraTriggerRequestInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (!input.IsAllowed
            || string.IsNullOrWhiteSpace(input.ProjectPath)
            || input.CurrentCamera is not { } camera
            || string.IsNullOrWhiteSpace(input.SelectedRecipe)
            || input.CameraDefinition is not { } cameraDefinition
            || input.SourceDefinition is not { } sourceDefinition)
        {
            return null;
        }

        var snapshot = input.Snapshot;
        return new ManualCameraTriggerRequest(
            input.ProjectId,
            input.ProjectName,
            input.ProjectPath,
            input.ProjectJson,
            input.BuildIdentity,
            input.SimulationFixedStep,
            snapshot,
            camera,
            new VirtualCameraInspectionRequest(
                input.ProjectPath,
                camera.Id,
                input.SelectedRecipe,
                camera.AcquisitionOrdinal,
                cameraDefinition.PlaceholderDecision,
                sourceDefinition.SourceRelativePath,
                sourceDefinition.Width,
                sourceDefinition.Height,
                sourceDefinition.PixelFormat,
                snapshot.TickIndex,
                snapshot.SimulationTime,
                input.SimulationSeed,
                snapshot.Axes.ToDictionary(
                    axis => axis.Id,
                    axis => axis.Position,
                    StringComparer.Ordinal)));
    }
}
