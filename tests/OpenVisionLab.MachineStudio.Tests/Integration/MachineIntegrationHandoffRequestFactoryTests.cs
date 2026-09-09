using System.Security.Cryptography;
using OpenVisionLab.Integration.Contracts;
using OpenVisionLab.Machine.Infrastructure.Integration;
using OpenVisionLab.Machine.Simulation.Camera;
using OpenVisionLab.Machine.Vision.Models;
using OpenVisionLab.MachineStudio.ViewModel;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

public sealed class MachineIntegrationHandoffRequestFactoryTests
{
    [Fact]
    public async Task CreateBuildsTwoDRequestAndProjectionFromMatchingSourceEvidence()
    {
        using var fixture = new TemporaryProject();
        var content = new byte[] { 0x10, 0x20, 0x30, 0x40 };
        await fixture.WriteAsync("assets/input.raw", content);
        var factory = new MachineIntegrationHandoffRequestFactory();
        var request = factory.Create(CreateInput(
            fixture,
            new VirtualCameraFrameEvidence(
                "frame-1",
                "assets/input.raw",
                Convert.ToHexString(SHA256.HashData(content)),
                content.LongLength,
                640,
                480,
                "Mono8")));

        Assert.NotNull(request);
        var handoff = request!;
        Assert.Equal("machine-project", handoff.ProjectId);
        Assert.Equal("sequence-001", handoff.SequenceId);
        Assert.Equal("inspect-1", handoff.StepId);
        Assert.Equal("camera-virtual", handoff.CameraId);
        Assert.Equal("acquisition-1", handoff.AcquisitionId);
        Assert.Equal(Path.GetFullPath(fixture.ProjectPath), handoff.MachineProjectPath);
        Assert.Equal(Path.Combine(fixture.Root, "assets", "input.raw"), handoff.InspectionSourcePath);
        Assert.Equal(Path.GetFullPath(fixture.RecipePath), handoff.InspectionRecipePath);
        Assert.Equal(IntegrationInspectionModality.TwoD, handoff.Modality);
        Assert.Equal(IntegrationInspectionInputKind.Image, handoff.InputKind);
        Assert.Equal(640, handoff.ProjectionProfile?.Image.Width);
        Assert.Equal(480, handoff.ProjectionProfile?.Image.Height);
    }

    [Fact]
    public async Task CreateRejectsSourceWhenBytesNoLongerMatchFrameEvidence()
    {
        using var fixture = new TemporaryProject();
        var original = new byte[] { 0x01, 0x02, 0x03 };
        await fixture.WriteAsync("input.raw", original);
        var frame = new VirtualCameraFrameEvidence(
            "frame-1",
            "input.raw",
            Convert.ToHexString(SHA256.HashData(original)),
            original.LongLength,
            3,
            1,
            "Mono8");
        await fixture.WriteAsync("input.raw", [0x01, 0x02, 0x04]);

        var request = new MachineIntegrationHandoffRequestFactory().Create(
            CreateInput(fixture, frame, sourceRelativePath: "input.raw", width: 3, height: 1));

        Assert.Null(request);
    }

    private static MachineIntegrationHandoffRequestInput CreateInput(
        TemporaryProject fixture,
        VirtualCameraFrameEvidence frame,
        string sourceRelativePath = "assets/input.raw",
        int width = 640,
        int height = 480) => new(
            "machine-project",
            "machine-project/1.0",
            "sequence-001",
            "inspect-1",
            "camera-virtual",
            "acquisition-1",
            fixture.ProjectPath,
            sourceRelativePath,
            width,
            height,
            fixture.RecipePath,
            frame,
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
                "D:\\OpenVisionLab-TestData\\OpenVisionLab-Machine-Studio\\integration-handoff-factory-tests",
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
