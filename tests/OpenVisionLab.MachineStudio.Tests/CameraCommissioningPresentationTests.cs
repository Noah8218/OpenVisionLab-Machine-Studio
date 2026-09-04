using OpenVisionLab.Machine.Core.Devices;
using OpenVisionLab.Machine.Simulation.Camera;
using OpenVisionLab.Machine.Simulation.Engine;
using OpenVisionLab.MachineStudio.ViewModel;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

public sealed class CameraCommissioningPresentationTests
{
    [Fact]
    public void FrameReadyProjectionPreservesCameraPresentationValues()
    {
        var presentation = new CameraCommissioningPresentation();
        presentation.ApplyProjection(CreateProjection(
            VirtualCameraState.FrameReady,
            hasCameraDefinition: true,
            isRunning: false,
            controlOwner: SimulationControlOwner.Manual));

        Assert.Equal("Camera", presentation.CurrentCameraName);
        Assert.Equal("acquisition-3", presentation.CurrentCameraFrameText);
        Assert.Equal("2", presentation.CurrentCameraExposureTicksText);
        Assert.Equal("3", presentation.CurrentCameraTransferTicksText);
        Assert.Equal("images/part.png", presentation.CurrentCameraSourceText);
        Assert.Equal(new string('A', 64), presentation.CurrentCameraFrameHashText);
        Assert.Equal("inspection-3", presentation.CurrentCameraInspectionIdText);
        Assert.Equal("Pass", presentation.CurrentCameraInspectionMessageText);
        Assert.Contains("score=0.75", presentation.CurrentCameraInspectionMetricsText);
        Assert.True(presentation.CanTriggerCamera);
        Assert.False(presentation.CanStartManualCameraControl);
    }

    [Fact]
    public void CameraAvailabilityPreservesSourceAndModeGates()
    {
        var presentation = new CameraCommissioningPresentation();

        presentation.ApplyProjection(CreateProjection(
            VirtualCameraState.Idle,
            hasCameraDefinition: true,
            isRunning: false,
            controlOwner: SimulationControlOwner.Definition));
        Assert.True(presentation.CanStartManualCameraControl);
        Assert.False(presentation.CanTriggerCamera);
        Assert.True(presentation.HasUsableCameraImageSource);

        presentation.ApplyProjection(CreateProjection(
            VirtualCameraState.Idle,
            hasCameraDefinition: true,
            isRunning: false,
            controlOwner: SimulationControlOwner.Manual,
            hasUsableSource: false));
        Assert.False(presentation.CanTriggerCamera);

        presentation.ApplyProjection(CreateProjection(
            VirtualCameraState.Idle,
            hasCameraDefinition: false,
            isRunning: false,
            controlOwner: SimulationControlOwner.Definition,
            hasUsableSource: false));
        Assert.True(presentation.CanStartManualCameraControl);
        Assert.False(presentation.HasUsableCameraImageSource);
    }

    private static CameraCommissioningProjection CreateProjection(
        VirtualCameraState state,
        bool hasCameraDefinition,
        bool isRunning,
        SimulationControlOwner controlOwner,
        bool hasUsableSource = true) => new(
        new VirtualCameraSnapshot(
            "camera-1",
            "Camera",
            state,
            3,
            "acquisition-3",
            "recipe-1",
            2,
            3,
            new VirtualCameraAcquisitionResult(
                "acquisition-3",
                "camera-1",
                "recipe-1",
                3,
                PlaceholderInspectionDecision.Pass,
                new VirtualCameraFrameEvidence(
                    "frame-3",
                    "images/part.png",
                    new string('A', 64),
                    10,
                    10,
                    10,
                    "Mono8"),
                new VirtualCameraInspectionEvidence(
                    "inspection-3",
                    "acquisition-3",
                    "camera-1",
                    "recipe-1",
                    "frame-3",
                    PlaceholderInspectionDecision.Pass,
                    "Pass",
                    new Dictionary<string, double> { ["score"] = 0.75 })),
            new VirtualCameraFrameEvidence(
                "frame-3",
                "images/part.png",
                new string('A', 64),
                10,
                10,
                10,
                "Mono8")),
        hasCameraDefinition,
        "Camera",
        hasUsableSource
            ? new VirtualSingleImageSourceDefinition
            {
                SourceRelativePath = "images/part.png",
                Width = 10,
                Height = 10,
                PixelFormat = "Mono8"
            }
            : null,
        hasUsableSource ? "C:\\Project" : null,
        hasUsableSource ? "recipe-1" : null,
        SimulationRunMode.Paused,
        IsRunMode: true,
        IsApplyingProject: false,
        IsValidationBusy: false,
        IsRuntimeDefinitionDirty: false,
        isRunning,
        controlOwner,
        IsAutomaticRunActive: false,
        ActiveSequenceStatus: null);
}
