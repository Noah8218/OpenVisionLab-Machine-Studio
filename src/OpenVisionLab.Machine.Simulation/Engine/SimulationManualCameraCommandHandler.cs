using OpenVisionLab.Machine.Simulation.Camera;
using OpenVisionLab.Machine.Simulation.Commands;

namespace OpenVisionLab.Machine.Simulation.Engine;

internal sealed class SimulationManualCameraCommandHandler
{
    internal SimulationManualControlOutcome Apply(
        SimulationCommand command,
        SimulationManualControlContext context)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        return command switch
        {
            TriggerVirtualCameraCommand triggerCamera => ApplyManualCameraTrigger(command, triggerCamera, context),
            _ => SimulationManualControlCommandHandler.Reject(
                command,
                context,
                SimulationCommandErrorCode.UnsupportedCommand,
                $"Command '{command.GetType().Name}' is not supported by the manual camera handler.")
        };
    }

    private static SimulationManualControlOutcome ApplyManualCameraTrigger(
        SimulationCommand command,
        TriggerVirtualCameraCommand triggerCamera,
        SimulationManualControlContext context)
    {
        if (context.ControlOwner != SimulationControlOwner.Manual)
        {
            return SimulationManualControlCommandHandler.Reject(
                command,
                context,
                SimulationCommandErrorCode.ControlOwnerNotAllowed,
                $"Manual camera acquisition is unavailable while owner is {context.ControlOwner}.");
        }

        if (context.RunMode != SimulationRunMode.Paused)
        {
            return SimulationManualControlCommandHandler.Reject(
                command,
                context,
                SimulationCommandErrorCode.InvalidRunMode,
                "Manual camera acquisition can be triggered only while paused.");
        }

        var camera = context.Cameras.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, triggerCamera.CameraId, StringComparison.Ordinal));
        if (camera is null)
        {
            return SimulationManualControlCommandHandler.Reject(
                command,
                context,
                SimulationCommandErrorCode.CameraNotFound,
                $"Virtual camera '{triggerCamera.CameraId}' was not found.");
        }

        var trigger = camera.Trigger(
            triggerCamera.RecipeId,
            triggerCamera.FrameEvidence,
            triggerCamera.InspectionEvidence);
        if (!trigger.IsAccepted || string.IsNullOrWhiteSpace(trigger.AcquisitionId))
        {
            return SimulationManualControlCommandHandler.Reject(
                command,
                context,
                SimulationCommandErrorCode.CameraTriggerRejected,
                $"Virtual camera '{triggerCamera.CameraId}' trigger failed: {trigger.ErrorCode}.");
        }

        return SimulationManualControlCommandHandler.Accept(
            command,
            context,
            $"Virtual camera '{triggerCamera.CameraId}' started {trigger.AcquisitionId}.",
            new SimulationManualControlEvent(
                "Camera",
                "CameraTriggered",
                $"{triggerCamera.CameraId} started {trigger.AcquisitionId} for recipe " +
                $"'{triggerCamera.RecipeId}' with frame SHA-256 " +
                $"{triggerCamera.FrameEvidence.ContentSha256}" +
                (triggerCamera.InspectionEvidence is null
                    ? "."
                    : $" and inspection {triggerCamera.InspectionEvidence.InspectionId}.")));
    }
}
