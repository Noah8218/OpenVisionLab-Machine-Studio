using OpenVisionLab.Machine.Core.Devices;
using OpenVisionLab.Machine.Simulation.Camera;
using OpenVisionLab.Machine.Vision.Models;
using OpenVisionLab.MachineStudio.ViewModel;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

public sealed class VirtualCameraInspectionWorkflowTests
{
    [Fact]
    public async Task AcquireInspectAndCreateCommandPreservesDeterministicEvidence()
    {
        using var project = new TemporaryProject();
        await project.WriteAsync("assets/input.raw", [0x10, 0x20, 0x30, 0x40]);
        var workflow = new VirtualCameraInspectionWorkflow();
        var request = CreateRequest(
            project.ProjectPath,
            PlaceholderInspectionDecision.Pass,
            acquisitionOrdinal: 3);

        var frame = await workflow.AcquireFrameAsync(request);
        var inspection = await workflow.RunInspectionAsync(request, frame);
        var command = workflow.CreateTriggerCommand(frame, inspection);

        Assert.Equal("camera-top/frame/00000004", frame.AcquisitionId);
        Assert.Equal("camera-top", frame.CameraId);
        Assert.Equal("presence-check", frame.RecipeId);
        Assert.Equal(20, frame.SimulationTick);
        Assert.Equal(4L, frame.ContentLength);
        Assert.Equal(VisionJudgment.OK, inspection.Judgment);
        Assert.Equal(frame.FrameId, inspection.FrameId);
        Assert.Equal(frame.FrameId, command.FrameEvidence.FrameId);
        Assert.Equal(PlaceholderInspectionDecision.Pass, command.InspectionEvidence?.Decision);
        Assert.Equal(frame.ContentSha256, command.FrameEvidence.ContentSha256);
        Assert.Equal(inspection.InspectionId, command.InspectionEvidence?.InspectionId);
    }

    [Fact]
    public async Task PlaceholderFailDecisionProducesNgInspectionEvidence()
    {
        using var project = new TemporaryProject();
        await project.WriteAsync("input.raw", [0x01, 0x02]);
        var workflow = new VirtualCameraInspectionWorkflow();
        var request = CreateRequest(
            project.ProjectPath,
            PlaceholderInspectionDecision.Fail,
            acquisitionOrdinal: 0,
            sourceRelativePath: "input.raw",
            width: 2,
            height: 1);

        var frame = await workflow.AcquireFrameAsync(request);
        var inspection = await workflow.RunInspectionAsync(request, frame);
        var command = workflow.CreateTriggerCommand(frame, inspection);

        Assert.Equal(VisionJudgment.NG, inspection.Judgment);
        Assert.Equal(PlaceholderInspectionDecision.Fail, command.InspectionEvidence?.Decision);
        Assert.Equal("presence-check", command.RecipeId);
    }

    private static VirtualCameraInspectionRequest CreateRequest(
        string projectPath,
        PlaceholderInspectionDecision decision,
        long acquisitionOrdinal,
        string sourceRelativePath = "assets/input.raw",
        int width = 2,
        int height = 2) => new(
            projectPath,
            "camera-top",
            "presence-check",
            acquisitionOrdinal,
            decision,
            sourceRelativePath,
            width,
            height,
            "Mono8",
            SimulationTick: 20,
            SimulationTime: TimeSpan.FromMilliseconds(100),
            Seed: 1234,
            new Dictionary<string, double>(StringComparer.Ordinal)
            {
                ["axis-x"] = 10.5
            });

    private sealed class TemporaryProject : IDisposable
    {
        public TemporaryProject()
        {
            Root = Path.Combine(
                "D:\\OpenVisionLab-TestData\\OpenVisionLab-Machine-Studio\\camera-workflow-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            ProjectPath = Path.Combine(Root, "camera.ovmachine");
        }

        public string Root { get; }

        public string ProjectPath { get; }

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
