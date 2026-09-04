using OpenVisionLab.Machine.Core.Channels;
using OpenVisionLab.Machine.Core.Devices;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Core.Sequences;
using OpenVisionLab.Machine.Sequence.Authoring;
using OpenVisionLab.Machine.Sequence.Compilation;
using OpenVisionLab.Machine.Simulation.Compilation;
using Xunit;

namespace OpenVisionLab.Machine.Simulation.Tests;

public sealed class VirtualCameraInspectionTemplateTests
{
    [Fact]
    public void Apply_CreatesExactCompilableCameraInspectionFlow()
    {
        var project = new MachineProjectDocument();

        var result = new VirtualCameraInspectionTemplate().Apply(project);

        Assert.True(result.Created);
        Assert.Equal("camera-1", result.CameraId);
        Assert.Equal("default", result.RecipeId);
        Assert.Equal("inspection-cycle", result.SequenceId);
        Assert.Equal("trigger-camera", result.TriggerStepId);

        var camera = Assert.Single(project.Devices);
        Assert.Equal("camera-1", camera.Id);
        Assert.Equal("Virtual Camera", camera.Name);
        Assert.Equal(DeviceKind.Camera, camera.Kind);
        Assert.NotNull(camera.Camera);
        Assert.Equal(20, camera.Camera.ExposureDelayMilliseconds);
        Assert.Equal(30, camera.Camera.TransferDelayMilliseconds);
        Assert.Equal(PlaceholderInspectionDecision.Pass, camera.Camera.PlaceholderDecision);
        Assert.Null(camera.Camera.SingleImageSource);
        Assert.Null(project.Simulation.AutomaticRun);

        var sequence = Assert.Single(project.Sequences);
        Assert.Equal("inspection-cycle", sequence.Id);
        Assert.Equal("Inspection Cycle", sequence.Name);
        Assert.Collection(
            sequence.Steps,
            step => AssertStep(
                step,
                "trigger-camera",
                SequenceStepAction.TriggerCamera,
                "camera-1",
                "default",
                0,
                "wait-vision-result",
                null,
                "inspection-fail"),
            step => AssertStep(
                step,
                "wait-vision-result",
                SequenceStepAction.WaitVisionResult,
                "camera-1",
                string.Empty,
                1000,
                "inspection-pass",
                "inspection-fail",
                "inspection-fail"),
            step => AssertStep(
                step,
                "inspection-pass",
                SequenceStepAction.Complete,
                string.Empty,
                string.Empty,
                0,
                null,
                null,
                null),
            step => AssertStep(
                step,
                "inspection-fail",
                SequenceStepAction.Complete,
                string.Empty,
                string.Empty,
                0,
                null,
                null,
                null));

        var compilation = new SequenceCompiler().Compile(
            sequence,
            new SequenceCompilationTargets(
                new Dictionary<string, ChannelKind>(StringComparer.Ordinal),
                Array.Empty<string>(),
                new[] { camera.Id }));

        Assert.True(compilation.IsSuccess, string.Join(Environment.NewLine, compilation.Errors));

        var runtimeCompilation = new MachineProjectRuntimeCompiler(TimeSpan.FromMilliseconds(5))
            .Compile(project);
        Assert.True(
            runtimeCompilation.IsSuccess,
            string.Join(
                Environment.NewLine,
                runtimeCompilation.Errors.Select(error =>
                    $"{error.Code} [{error.TargetId}]: {error.Message}")));
    }

    [Fact]
    public void Apply_WhenCameraExists_IsIdempotent()
    {
        var existingCamera = new DeviceDefinition
        {
            Id = "existing-camera",
            Name = "Existing Camera",
            Kind = DeviceKind.Camera,
            Camera = new VirtualCameraDefinition()
        };
        var existingSequence = new SequenceDefinition { Id = "existing-sequence" };
        var project = new MachineProjectDocument
        {
            Devices = { existingCamera },
            Sequences = { existingSequence }
        };
        var store = new ProjectDocumentStore();
        var before = store.Serialize(project);

        var result = new VirtualCameraInspectionTemplate().Apply(project);

        Assert.False(result.Created);
        Assert.Equal("existing-camera", result.CameraId);
        Assert.Equal("default", result.RecipeId);
        Assert.Equal(string.Empty, result.SequenceId);
        Assert.Equal(string.Empty, result.TriggerStepId);
        Assert.Equal(before, store.Serialize(project));
        Assert.Same(existingCamera, Assert.Single(project.Devices));
        Assert.Same(existingSequence, Assert.Single(project.Sequences));
    }

    [Fact]
    public void Apply_UsesCollisionSafeCameraAndSequenceIds()
    {
        var project = new MachineProjectDocument
        {
            Devices =
            {
                new DeviceDefinition { Id = "camera-1", Kind = DeviceKind.Light },
                new DeviceDefinition { Id = "camera-1-2", Kind = DeviceKind.Light }
            },
            Sequences =
            {
                new SequenceDefinition { Id = "inspection-cycle" },
                new SequenceDefinition { Id = "inspection-cycle-2" }
            }
        };

        var result = new VirtualCameraInspectionTemplate().Apply(project);

        Assert.True(result.Created);
        Assert.Equal("camera-1-3", result.CameraId);
        Assert.Equal("inspection-cycle-3", result.SequenceId);
        var createdSequence = project.Sequences.Single(sequence => sequence.Id == result.SequenceId);
        Assert.All(
            createdSequence.Steps.Where(step => step.Action != SequenceStepAction.Complete),
            step => Assert.Equal("camera-1-3", step.TargetId));
    }

    [Fact]
    public void Apply_DoesNotMutateUnrelatedProjectState()
    {
        var project = new MachineProjectDocument
        {
            Id = "project-1",
            Name = "Existing Project",
            CreatedAt = DateTimeOffset.UnixEpoch,
            ModifiedAt = DateTimeOffset.UnixEpoch.AddHours(1),
            Devices =
            {
                new DeviceDefinition { Id = "light-1", Name = "Light", Kind = DeviceKind.Light }
            },
            Sequences =
            {
                new SequenceDefinition
                {
                    Id = "existing-sequence",
                    Name = "Existing Sequence",
                    Steps =
                    {
                        new SequenceStepDefinition
                        {
                            Id = "complete",
                            Name = "Complete",
                            Action = SequenceStepAction.Complete
                        }
                    }
                }
            }
        };
        var store = new ProjectDocumentStore();
        var before = store.Serialize(project);

        var result = new VirtualCameraInspectionTemplate().Apply(project);

        project.Devices.RemoveAll(device => device.Id == result.CameraId);
        project.Sequences.RemoveAll(sequence => sequence.Id == result.SequenceId);
        Assert.Equal(before, store.Serialize(project));
    }

    private static void AssertStep(
        SequenceStepDefinition step,
        string id,
        SequenceStepAction action,
        string targetId,
        string parameter,
        int timeoutMs,
        string? nextStepId,
        string? failureStepId,
        string? errorStepId)
    {
        Assert.Equal(id, step.Id);
        Assert.Equal(action, step.Action);
        Assert.Equal(targetId, step.TargetId);
        Assert.Equal(parameter, step.Parameter);
        Assert.Equal(timeoutMs, step.TimeoutMs);
        Assert.Equal(nextStepId, step.NextStepId);
        Assert.Equal(failureStepId, step.FailureStepId);
        Assert.Equal(errorStepId, step.ErrorStepId);
    }
}
