using System.Security.Cryptography;
using OpenVisionLab.Integration.Contracts;
using OpenVisionLab.Machine.Core.Devices;
using OpenVisionLab.Machine.Core.Sequences;
using OpenVisionLab.Machine.Infrastructure.Integration;
using OpenVisionLab.Machine.Simulation.Camera;
using OpenVisionLab.MachineStudio.ViewModel;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

public sealed class MachineIntegrationRequestWorkflowTests
{
    [Fact]
    public async Task TryCreateBuildsEligibleTwoDRequestFromCurrentCameraContext()
    {
        using var fixture = new TemporaryProject();
        var content = new byte[] { 0x11, 0x22, 0x33 };
        await fixture.WriteAsync("assets/input.raw", content);
        var frame = new VirtualCameraFrameEvidence(
            "frame-1",
            "assets/input.raw",
            Convert.ToHexString(SHA256.HashData(content)),
            content.LongLength,
            3,
            1,
            "Mono8");
        var workflow = new MachineIntegrationRequestWorkflow();

        var request = workflow.TryCreate(
            CreateContext(fixture, frame),
            fixture.RecipePath);

        Assert.NotNull(request);
        Assert.Equal("sequence-001", request!.SequenceId);
        Assert.Equal("inspect-1", request.StepId);
        Assert.Equal("camera-virtual", request.CameraId);
        Assert.Equal("acquisition-1", request.AcquisitionId);
        Assert.Equal(Path.GetFullPath(fixture.ProjectPath), request.MachineProjectPath);
        Assert.Equal(
            Path.Combine(fixture.Root, "assets", "input.raw"),
            request.InspectionSourcePath);
    }

    [Fact]
    public void TryCreateRejectsDirtyBuildBeforeReadingFiles()
    {
        using var fixture = new TemporaryProject();
        var workflow = new MachineIntegrationRequestWorkflow();

        var request = workflow.TryCreate(
            CreateContext(
                fixture,
                new VirtualCameraFrameEvidence(
                    "frame-1",
                    "input.raw",
                    new string('A', 64),
                    1,
                    1,
                    1,
                    "Mono8"),
                isExactCommit: false),
            Path.Combine(fixture.Root, "missing-recipe.json"));

        Assert.Null(request);
    }

    private static MachineIntegrationRequestContext CreateContext(
        TemporaryProject fixture,
        VirtualCameraFrameEvidence frame,
        bool isExactCommit = true) => new(
        isExactCommit,
        "machine-project",
        "machine-project/1.0",
        [new SequenceDefinition
        {
            Id = "sequence-001",
            Steps =
            [new SequenceStepDefinition
            {
                Id = "inspect-1",
                Action = SequenceStepAction.TriggerCamera,
                TargetId = "camera-virtual",
                Parameter = "recipe-a"
            }]
        }],
        fixture.ProjectPath,
        "camera-virtual",
        "recipe-a",
        new VirtualCameraSnapshot(
            "camera-virtual",
            "Virtual Camera",
            VirtualCameraState.FrameReady,
            1,
            "acquisition-1",
            "recipe-a",
            0,
            0,
            null,
            frame),
        new VirtualSingleImageSourceDefinition
        {
            SourceRelativePath = frame.SourceRelativePath,
            Width = frame.Width,
            Height = frame.Height,
            PixelFormat = frame.PixelFormat
        },
        new IntegrationApplicationIdentity(
            IntegrationApplicationIds.MachineStudio,
            "1.0.0",
            new string('1', 40),
            IntegrationSourceState.Clean),
        new IntegrationApplicationIdentity(
            IntegrationApplicationIds.TwoDStudio,
            "2.0.0",
            new string('2', 40),
            IntegrationSourceState.Clean));

    private sealed class TemporaryProject : IDisposable
    {
        public TemporaryProject()
        {
            Root = Path.Combine(
                "D:\\OpenVisionLab-TestData\\OpenVisionLab-Machine-Studio\\integration-request-workflow-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            ProjectPath = Path.Combine(Root, "machine.ovmachine");
            RecipePath = Path.Combine(Root, "recipe.json");
            File.WriteAllText(RecipePath, "{}");
        }

        public string Root { get; }

        public string ProjectPath { get; }

        public string RecipePath { get; }

        public async Task WriteAsync(string relativePath, byte[] content)
        {
            var fullPath = Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            var directory = Path.GetDirectoryName(fullPath);
            if (directory is not null)
            {
                Directory.CreateDirectory(directory);
            }

            await File.WriteAllBytesAsync(fullPath, content);
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
