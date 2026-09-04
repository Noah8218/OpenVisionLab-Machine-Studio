using System.Globalization;
using System.IO;
using OpenVisionLab.Machine.Core.Devices;
using OpenVisionLab.Machine.Infrastructure.Vision;
using OpenVisionLab.Machine.Simulation.Camera;
using OpenVisionLab.Machine.Simulation.Commands;
using OpenVisionLab.Machine.Vision.Contracts;
using OpenVisionLab.Machine.Vision.Models;

namespace OpenVisionLab.MachineStudio.ViewModel;

internal sealed record VirtualCameraInspectionRequest(
    string ProjectPath,
    string CameraId,
    string RecipeId,
    long AcquisitionOrdinal,
    PlaceholderInspectionDecision PlaceholderDecision,
    string SourceRelativePath,
    int Width,
    int Height,
    string PixelFormat,
    long SimulationTick,
    TimeSpan SimulationTime,
    int Seed,
    IReadOnlyDictionary<string, double> AxisPositions);

internal sealed class VirtualCameraInspectionWorkflow
{
    public async ValueTask<VirtualFrameDescriptor> AcquireFrameAsync(
        VirtualCameraInspectionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var context = new VirtualAcquisitionContext(
            CreateAcquisitionId(request),
            request.CameraId,
            request.RecipeId,
            request.SimulationTick,
            request.SimulationTime,
            request.Seed,
            request.AxisPositions);
        var source = new ProjectRelativeSingleImageSource(
            Path.GetDirectoryName(request.ProjectPath)!,
            request.SourceRelativePath,
            request.Width,
            request.Height,
            request.PixelFormat);

        return await source.AcquireAsync(context, cancellationToken);
    }

    public Task<VisionRunResult> RunInspectionAsync(
        VirtualCameraInspectionRequest request,
        VirtualFrameDescriptor frame,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(frame);

        var judgment = request.PlaceholderDecision switch
        {
            PlaceholderInspectionDecision.Pass => VisionJudgment.OK,
            PlaceholderInspectionDecision.Fail => VisionJudgment.NG,
            _ => throw new InvalidOperationException("Camera inspection judgment is invalid.")
        };
        IVisionInspectionRunner runner = new DeterministicMockVisionInspectionRunner(
            new Dictionary<string, VisionJudgment>(StringComparer.Ordinal)
            {
                [request.RecipeId] = judgment
            });

        return runner.RunAsync(
            new VisionRecipeReference(
                request.RecipeId,
                $"recipes/{request.RecipeId}.ovrecipe"),
            frame,
            cancellationToken);
    }

    public TriggerVirtualCameraCommand CreateTriggerCommand(
        VirtualFrameDescriptor frame,
        VisionRunResult inspectionResult)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(inspectionResult);

        var frameEvidence = new VirtualCameraFrameEvidence(
            frame.FrameId,
            frame.SourceRelativePath,
            frame.ContentSha256,
            frame.ContentLength,
            frame.Width,
            frame.Height,
            frame.PixelFormat);
        var inspectionEvidence = new VirtualCameraInspectionEvidence(
            inspectionResult.InspectionId,
            inspectionResult.AcquisitionId,
            inspectionResult.CameraId,
            inspectionResult.RecipeId,
            inspectionResult.FrameId,
            inspectionResult.Judgment switch
            {
                VisionJudgment.OK => PlaceholderInspectionDecision.Pass,
                VisionJudgment.NG => PlaceholderInspectionDecision.Fail,
                _ => throw new InvalidOperationException(
                    $"Unsupported manual inspection judgment: {inspectionResult.Judgment}.")
            },
            inspectionResult.Message,
            inspectionResult.Metrics);

        return new TriggerVirtualCameraCommand(
            frame.CameraId,
            frame.RecipeId,
            frameEvidence,
            inspectionEvidence);
    }

    private static string CreateAcquisitionId(VirtualCameraInspectionRequest request) =>
        string.Concat(
            request.CameraId,
            "/frame/",
            (request.AcquisitionOrdinal + 1).ToString("D8", CultureInfo.InvariantCulture));
}
