using OpenVisionLab.Machine.Core.Devices;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Core.Sequences;

namespace OpenVisionLab.Machine.Sequence.Authoring;

public sealed record VirtualCameraInspectionTemplateResult(
    bool Created,
    string CameraId,
    string RecipeId,
    string SequenceId,
    string TriggerStepId);

public sealed class VirtualCameraInspectionTemplate
{
    private const string DefaultCameraId = "camera-1";
    private const string DefaultRecipeId = "default";
    private const string DefaultSequenceId = "inspection-cycle";
    private const string TriggerStepId = "trigger-camera";

    public VirtualCameraInspectionTemplateResult Apply(MachineProjectDocument project)
    {
        ArgumentNullException.ThrowIfNull(project);

        var existingCamera = project.Devices.FirstOrDefault(device =>
            device.Kind == DeviceKind.Camera);
        if (existingCamera is not null)
        {
            return new VirtualCameraInspectionTemplateResult(
                false,
                existingCamera.Id,
                DefaultRecipeId,
                string.Empty,
                string.Empty);
        }

        var cameraId = UniqueId(DefaultCameraId, project.Devices.Select(device => device.Id));
        var sequenceId = UniqueId(DefaultSequenceId, project.Sequences.Select(sequence => sequence.Id));
        var camera = new DeviceDefinition
        {
            Id = cameraId,
            Name = "Virtual Camera",
            Kind = DeviceKind.Camera,
            Camera = new VirtualCameraDefinition
            {
                ExposureDelayMilliseconds = 20,
                TransferDelayMilliseconds = 30,
                PlaceholderDecision = PlaceholderInspectionDecision.Pass
            }
        };
        var sequence = new SequenceDefinition
        {
            Id = sequenceId,
            Name = "Inspection Cycle",
            Steps =
            {
                new SequenceStepDefinition
                {
                    Id = TriggerStepId,
                    Name = "Trigger Camera",
                    Action = SequenceStepAction.TriggerCamera,
                    TargetId = cameraId,
                    Parameter = DefaultRecipeId,
                    NextStepId = "wait-vision-result",
                    ErrorStepId = "inspection-fail"
                },
                new SequenceStepDefinition
                {
                    Id = "wait-vision-result",
                    Name = "Wait Vision Result",
                    Action = SequenceStepAction.WaitVisionResult,
                    TargetId = cameraId,
                    TimeoutMs = 1000,
                    NextStepId = "inspection-pass",
                    FailureStepId = "inspection-fail",
                    ErrorStepId = "inspection-fail"
                },
                new SequenceStepDefinition
                {
                    Id = "inspection-pass",
                    Name = "Inspection Pass",
                    Action = SequenceStepAction.Complete
                },
                new SequenceStepDefinition
                {
                    Id = "inspection-fail",
                    Name = "Inspection Fail",
                    Action = SequenceStepAction.Complete
                }
            }
        };

        project.Devices.Add(camera);
        project.Sequences.Add(sequence);

        return new VirtualCameraInspectionTemplateResult(
            true,
            cameraId,
            DefaultRecipeId,
            sequenceId,
            TriggerStepId);
    }

    private static string UniqueId(string baseId, IEnumerable<string> existingIds)
    {
        var ids = existingIds.ToHashSet(StringComparer.Ordinal);
        if (!ids.Contains(baseId))
        {
            return baseId;
        }

        var suffix = 2;
        while (ids.Contains($"{baseId}-{suffix}"))
        {
            suffix++;
        }

        return $"{baseId}-{suffix}";
    }
}
