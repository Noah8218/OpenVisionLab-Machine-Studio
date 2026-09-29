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
    private static readonly IntegrationApplicationIdentity Producer = new(
        IntegrationApplicationIds.MachineStudio, "1.0.0", new string('1', 40), IntegrationSourceState.Clean);
    private static readonly IntegrationApplicationIdentity Consumer = new(
        IntegrationApplicationIds.TwoDStudio,
        "2.0.0",
        new string('2', 40),
        IntegrationSourceState.Clean);
    private static readonly IntegrationApplicationIdentity ThreeDConsumer = new(
        IntegrationApplicationIds.ThreeDStudio,
        "0.2.0-dev",
        new string('3', 40),
        IntegrationSourceState.Clean);

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
            fixture.RecipePath,
            Producer,
            Consumer);

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
    public async Task TryCreateAcceptsTransferredFrameAwaitingExternalResult()
    {
        using var fixture = new TemporaryProject();
        byte[] content = [0x11, 0x22, 0x33];
        await fixture.WriteAsync("assets/input.raw", content);
        var frame = new VirtualCameraFrameEvidence(
            "frame-1",
            "assets/input.raw",
            Convert.ToHexString(SHA256.HashData(content)),
            content.LongLength,
            3,
            1,
            "Mono8");
        var context = CreateContext(fixture, frame);
        context = context with
        {
            CurrentCamera = context.CurrentCamera! with
            {
                State = VirtualCameraState.AwaitingExternalResult,
                Result = null
            }
        };

        var request = new MachineIntegrationRequestWorkflow().TryCreate(
            context,
            fixture.RecipePath,
            Producer,
            Consumer);

        Assert.NotNull(request);
        Assert.Equal("acquisition-1", request!.AcquisitionId);
        Assert.Equal(frame.FrameId, request.FrameId);
        Assert.Equal(
            Path.Combine(fixture.Root, "assets", "input.raw"),
            request.InspectionSourcePath);
    }

    [Fact]
    public async Task TryCreateUsesExpectedSequenceForAutomaticTwoDRequestWhenTriggersOverlap()
    {
        using var fixture = new TemporaryProject();
        byte[] content = [0x11, 0x22, 0x33];
        await fixture.WriteAsync("assets/input.raw", content);
        var frame = new VirtualCameraFrameEvidence(
            "frame-1",
            "assets/input.raw",
            Convert.ToHexString(SHA256.HashData(content)),
            content.LongLength,
            3,
            1,
            "Mono8");
        var context = CreateContext(fixture, frame) with
        {
            Sequences =
            [
                new SequenceDefinition
                {
                    Id = "manual-sequence",
                    Steps =
                    [new SequenceStepDefinition
                    {
                        Id = "manual-inspect",
                        Action = SequenceStepAction.TriggerCamera,
                        TargetId = "camera-virtual",
                        Parameter = "recipe-a"
                    }]
                },
                new SequenceDefinition
                {
                    Id = "automatic-sequence",
                    Steps =
                    [new SequenceStepDefinition
                    {
                        Id = "automatic-inspect",
                        Action = SequenceStepAction.TriggerCamera,
                        TargetId = "camera-virtual",
                        Parameter = "recipe-a"
                    }]
                }
            ],
            ExpectedSequenceId = "automatic-sequence"
        };

        var request = new MachineIntegrationRequestWorkflow().TryCreate(
            context,
            fixture.RecipePath,
            Producer,
            Consumer);

        Assert.NotNull(request);
        Assert.Equal("automatic-sequence", request!.SequenceId);
        Assert.Equal("automatic-inspect", request.StepId);
    }

    [Fact]
    public async Task TryCreateBuildsThreeDHeightMapRequestFromBoundSequenceAndSavedEvidence()
    {
        using var fixture = new TemporaryProject();
        byte[] sourceBytes = [0x01, 0x02, 0x03, 0x04];
        await fixture.WriteAsync("assets/input.c3d", sourceBytes);
        string recipeJson = "{\"recipeType\":\"c3d-warpage\",\"step\":{\"frameId\":\"frame.c3d-grid-index\"}}";
        await File.WriteAllTextAsync(fixture.ThreeDRecipePath, recipeJson);
        var sourceEvidence = new MachineIntegrationArtifactEvidence(
            Convert.ToHexString(SHA256.HashData(sourceBytes)),
            sourceBytes.LongLength);
        var recipeBytes = await File.ReadAllBytesAsync(fixture.ThreeDRecipePath);
        var recipeEvidence = new MachineIntegrationArtifactEvidence(
            Convert.ToHexString(SHA256.HashData(recipeBytes)),
            recipeBytes.LongLength);
        var frame = new VirtualCameraFrameEvidence(
            "height-frame-1",
            "assets/input.c3d",
            sourceEvidence.ContentSha256,
            sourceEvidence.ContentLength,
            2,
            2,
            "C3D-HeightMap");
        var context = CreateContext(fixture, frame) with
        {
            HeightMapSource = new MachineIntegrationHeightMapSourceDefinition(
                "assets/input.c3d",
                2,
                2,
                "C3D-HeightMap",
                "mm",
                "frame.c3d-grid-index",
                sourceEvidence),
            InspectionRecipeEvidence = recipeEvidence,
            ExpectedSequenceId = "sequence-001",
            ExpectedStepId = "inspect-1",
            ExpectedDeviceId = "camera-virtual"
        };

        var request = new MachineIntegrationRequestWorkflow().TryCreate(
            context,
            fixture.ThreeDRecipePath,
            Producer,
            ThreeDConsumer);

        Assert.NotNull(request);
        Assert.Equal(IntegrationInspectionModality.ThreeD, request!.Modality);
        Assert.Equal(IntegrationInspectionInputKind.HeightMap, request.InputKind);
        Assert.Equal("mm", request.Unit);
        Assert.Equal("frame.c3d-grid-index", request.FrameId);
        Assert.Equal(
            "assets/input.c3d",
            Path.GetRelativePath(fixture.Root, request.InspectionSourcePath).Replace('\\', '/'));
        Assert.Null(request.ProjectionProfile);
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
            Path.Combine(fixture.Root, "missing-recipe.json"),
            Producer,
            Consumer);

        Assert.Null(request);
    }

    [Fact]
    public void RefreshIdentityTracksOnlyTheExistingSourceFieldsAndPrefersResultEvidence()
    {
        using var fixture = new TemporaryProject();
        var frame = new VirtualCameraFrameEvidence("frame-1", "input.raw", new string('A', 64), 4, 4, 1, "Mono8");
        var context = CreateContext(fixture, frame);
        var camera = context.CurrentCamera!;
        var key = MachineIntegrationRequestWorkflow.CreateRefreshKey(context);
        MachineIntegrationRequestContext[] changes =
        [
            context with { ProjectId = "other-project" },
            context with { ProjectPath = "other-project.ovmachine" },
            context with { CameraId = "other-camera" },
            context with { CameraRecipe = "other-recipe" },
            context with { CurrentCamera = camera with { State = VirtualCameraState.Idle } },
            context with { CurrentCamera = camera with { CurrentAcquisitionId = "other-acquisition" } },
            context with { CurrentCamera = camera with { FrameEvidence = new("frame-2", "input.raw", new string('A', 64), 4, 4, 1, "Mono8") } },
            context with { CurrentCamera = camera with { FrameEvidence = new("frame-1", "input.raw", new string('B', 64), 4, 4, 1, "Mono8") } },
            context with { CurrentCamera = camera with { FrameEvidence = new("frame-1", "other.raw", new string('A', 64), 4, 4, 1, "Mono8") } }
        ];
        Assert.All(changes, changed => Assert.NotEqual(key, MachineIntegrationRequestWorkflow.CreateRefreshKey(changed)));
        Assert.Equal(key, MachineIntegrationRequestWorkflow.CreateRefreshKey(context with { ProjectSchema = "metadata-only" }));

        var result = new VirtualCameraAcquisitionResult("acquisition-1", "camera-virtual", "recipe-a",
            1, PlaceholderInspectionDecision.Pass, frame);
        Assert.Equal(key, MachineIntegrationRequestWorkflow.CreateRefreshKey(context with
        {
            CurrentCamera = camera with { Result = result, FrameEvidence = changes[6].CurrentCamera!.FrameEvidence }
        }));
        Assert.Equal(MachineIntegrationRequestWorkflow.CreateRefreshKey(context with { ProjectPath = null }),
            MachineIntegrationRequestWorkflow.CreateRefreshKey(context with { ProjectPath = string.Empty }));
    }

    [Fact]
    public async Task TryCreateRejectsWrongConsumerAndRevalidatesChangedSourceBytes()
    {
        using var fixture = new TemporaryProject();
        byte[] content = [1, 2, 3];
        await fixture.WriteAsync("input.raw", content);
        var frame = new VirtualCameraFrameEvidence("frame-1", "input.raw",
            Convert.ToHexString(SHA256.HashData(content)), content.LongLength, 3, 1, "Mono8");
        var context = CreateContext(fixture, frame);
        var workflow = new MachineIntegrationRequestWorkflow();
        var wrongConsumer = new IntegrationApplicationIdentity(IntegrationApplicationIds.MachineStudio,
            "1.0.0", new string('3', 40), IntegrationSourceState.Clean);
        Assert.Null(workflow.TryCreate(context, fixture.RecipePath, Producer, wrongConsumer));
        Assert.NotNull(workflow.TryCreate(context, fixture.RecipePath, Producer, Consumer));
        await fixture.WriteAsync("input.raw", [4, 5, 6]);
        Assert.Null(workflow.TryCreate(context, fixture.RecipePath, Producer, Consumer));
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
        });

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
            ThreeDRecipePath = Path.Combine(Root, "recipes", "heightmap.c3d.json");
            Directory.CreateDirectory(Path.GetDirectoryName(ThreeDRecipePath)!);
            File.WriteAllText(RecipePath, "{}");
        }

        public string Root { get; }

        public string ProjectPath { get; }

        public string RecipePath { get; }

        public string ThreeDRecipePath { get; }

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
