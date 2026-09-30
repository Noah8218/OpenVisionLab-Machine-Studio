using OpenVisionLab.Machine.Core.Devices;
using OpenVisionLab.Machine.Sequence.Runtime;
using OpenVisionLab.Machine.Simulation.Camera;
using OpenVisionLab.Machine.Simulation.Engine;
using OpenVisionLab.MachineStudio.Converter;
using OpenVisionLab.MachineStudio.ViewModel;
using OpenVisionLab.TestSupport;
using System.Globalization;
using System.Windows;
using System.Windows.Media.Imaging;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

public sealed class CameraCommissioningPresentationTests
{
    [Fact]
    public void InputKindDescribesConfiguredPixelsWithoutClaimingAcquisition()
    {
        var presentation = new CameraCommissioningPresentation();
        presentation.ApplyProjection(CreateProjection(VirtualCameraState.Idle, true, false, SimulationControlOwner.Definition, hasResult: false));
        Assert.Contains("images/part.png", presentation.CurrentCameraInputKindText, StringComparison.Ordinal);
        Assert.Contains("10×10", presentation.CurrentCameraInputKindText, StringComparison.Ordinal);
        Assert.Contains("Mono8", presentation.CurrentCameraInputKindText, StringComparison.Ordinal);
        Assert.False(presentation.HasCurrentCameraImage);
        presentation.ApplyProjection(CreateProjection(VirtualCameraState.Idle, true, false, SimulationControlOwner.Definition, hasUsableSource: false));
        Assert.Equal(OpenVisionLab.OpenVisionLanguageService.T("Camera.InputNotConfigured"), presentation.CurrentCameraInputKindText);
    }

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
        Assert.Equal(
            OpenVisionLanguageService.T("Camera.SourceModeManual"),
            presentation.CurrentCameraSourceModeText);
        Assert.Equal(new string('A', 64), presentation.CurrentCameraFrameHashText);
        Assert.Equal(
            OpenVisionLanguageService.T("Camera.ResultSourceMock"),
            presentation.CurrentCameraResultSourceText);
        Assert.Equal(
            OpenVisionLanguageService.T("Camera.VerificationModelOnly"),
            presentation.CurrentCameraVerificationLevelText);
        Assert.Equal(new string('A', 64), presentation.CurrentCameraInputHashText);
        Assert.Equal(
            OpenVisionLanguageService.T("Camera.ModelDeterministicMock"),
            presentation.CurrentCameraModelKindText);
        Assert.Equal(
            string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                OpenVisionLanguageService.T(
                    "Camera.ClockFixedStep",
                    "고정 스텝 시뮬레이션 ({0} ms/tick)",
                    "Fixed-step simulation ({0} ms/tick)"),
                "5"),
            presentation.CurrentCameraClockModeText);
        Assert.Equal("inspection-3", presentation.CurrentCameraInspectionIdText);
        Assert.Equal("Pass", presentation.CurrentCameraInspectionMessageText);
        Assert.Contains("score=0.75", presentation.CurrentCameraInspectionMetricsText);
        Assert.True(presentation.CanTriggerCamera);
        Assert.False(presentation.CanStartManualCameraControl);
    }

    [Theory]
    [InlineData(PlaceholderInspectionDecision.Pass)]
    [InlineData(PlaceholderInspectionDecision.Fail)]
    public void PlaceholderResultWithoutDetailedEvidenceStillIdentifiesMockSource(
        PlaceholderInspectionDecision decision)
    {
        CameraCommissioningProjection projection = CreateProjection(
            VirtualCameraState.FrameReady,
            hasCameraDefinition: true,
            isRunning: false,
            controlOwner: SimulationControlOwner.Manual);
        VirtualCameraSnapshot snapshot = Assert.IsType<VirtualCameraSnapshot>(projection.Snapshot);
        VirtualCameraAcquisitionResult result = Assert.IsType<VirtualCameraAcquisitionResult>(snapshot.Result);
        projection = projection with
        {
            Snapshot = snapshot with
            {
                Result = result with { Decision = decision, InspectionEvidence = null }
            }
        };
        var presentation = new CameraCommissioningPresentation();
        presentation.ApplyProjection(projection);

        Assert.Equal(
            decision == PlaceholderInspectionDecision.Pass
                ? OpenVisionLanguageService.T("Shell.ResultPass")
                : OpenVisionLanguageService.T("Shell.ResultFail"),
            presentation.CurrentCameraResultText);
        Assert.Equal(
            OpenVisionLanguageService.T("Camera.ResultSourceMock"),
            presentation.CurrentCameraResultSourceText);
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
        Assert.Equal(
            OpenVisionLanguageService.T("Camera.SourceModeManualReady"),
            presentation.CurrentCameraSourceModeText);

        presentation.ApplyProjection(CreateProjection(
            VirtualCameraState.Idle,
            hasCameraDefinition: true,
            isRunning: false,
            controlOwner: SimulationControlOwner.Manual,
            hasUsableSource: false));
        Assert.False(presentation.CanTriggerCamera);
        Assert.Equal(
            OpenVisionLanguageService.T("Camera.SourceModeManualUnavailable"),
            presentation.CurrentCameraSourceModeText);

        presentation.ApplyProjection(CreateProjection(
            VirtualCameraState.Idle,
            hasCameraDefinition: false,
            isRunning: false,
            controlOwner: SimulationControlOwner.Definition,
            hasUsableSource: false));
        Assert.True(presentation.CanStartManualCameraControl);
        Assert.False(presentation.HasUsableCameraImageSource);
        Assert.Equal(
            OpenVisionLanguageService.T("Camera.SourceModeUnavailable"),
            presentation.CurrentCameraSourceModeText);

        presentation.ApplyProjection(CreateProjection(
            VirtualCameraState.Idle,
            hasCameraDefinition: true,
            isRunning: true,
            controlOwner: SimulationControlOwner.EmbeddedSequence,
            isAutomaticRunActive: true));
        Assert.Equal(
            OpenVisionLanguageService.T("Camera.SourceModeAutomatic"),
            presentation.CurrentCameraSourceModeText);
    }

    [Fact]
    public void ExternalResultProjectionDistinguishesVerifiedAndUnverifiedEvidence()
    {
        var presentation = new CameraCommissioningPresentation();
        var verified = CreateExternalEvidence(
            ExternalInspectionResultStatus.Completed,
            ExternalInspectionOutcome.Pass);
        presentation.ApplyProjection(CreateProjection(
            VirtualCameraState.FrameReady,
            hasCameraDefinition: true,
            isRunning: false,
            controlOwner: SimulationControlOwner.Manual,
            externalResultEvidence: verified));

        Assert.Equal(
            OpenVisionLanguageService.T("Camera.ResultSourceExternalVerified"),
            presentation.CurrentCameraResultSourceText);
        Assert.Equal(
            OpenVisionLanguageService.T("Camera.VerificationExternal"),
            presentation.CurrentCameraVerificationLevelText);
        Assert.Equal(new string('B', 64), presentation.CurrentCameraInputHashText);
        Assert.Contains("VisionConsumer", presentation.CurrentCameraModelKindText, StringComparison.Ordinal);

        presentation.ApplyProjection(CreateProjection(
            VirtualCameraState.Faulted,
            hasCameraDefinition: true,
            isRunning: false,
            controlOwner: SimulationControlOwner.Manual,
            externalResultEvidence: verified with
            {
                Status = ExternalInspectionResultStatus.Failed,
                Outcome = ExternalInspectionOutcome.ExecutionError,
                Decision = null
            }));
        Assert.Equal(
            OpenVisionLanguageService.T("Camera.ResultSourceExternalUnverified"),
            presentation.CurrentCameraResultSourceText);
        Assert.Equal(
            OpenVisionLanguageService.T("Camera.VerificationNotVerified"),
            presentation.CurrentCameraVerificationLevelText);
    }

    [Fact]
    public void PendingProjectionDoesNotClaimAResultOrInspectionModel()
    {
        var presentation = new CameraCommissioningPresentation();
        presentation.ApplyProjection(CreateProjection(
            VirtualCameraState.AwaitingExternalResult,
            hasCameraDefinition: true,
            isRunning: true,
            controlOwner: SimulationControlOwner.Manual,
            hasResult: false));

        Assert.Equal(
            OpenVisionLanguageService.T("Camera.ResultSourcePending"),
            presentation.CurrentCameraResultSourceText);
        Assert.Equal(
            OpenVisionLanguageService.T("Camera.VerificationNotVerified"),
            presentation.CurrentCameraVerificationLevelText);
        Assert.Equal(new string('A', 64), presentation.CurrentCameraInputHashText);
        Assert.Equal("—", presentation.CurrentCameraModelKindText);
    }

    [Fact]
    public void FrameReadyProjectionExposesCapturedProjectImagePath()
    {
        var root = Path.Combine(TestStorage.RootPath, "camera-preview", Guid.NewGuid().ToString("N"));
        var imageDirectory = Path.Combine(root, "assets");
        var projectPath = Path.Combine(root, "cell.ovmachine");
        var imagePath = Path.Combine(imageDirectory, "part.pgm");
        Directory.CreateDirectory(imageDirectory);
        File.WriteAllText(projectPath, "{}", System.Text.Encoding.UTF8);
        File.WriteAllBytes(imagePath, "P5\n2 2\n255\n"u8.ToArray().Concat(new byte[] { 0, 64, 128, 255 }).ToArray());

        try
        {
            var presentation = new CameraCommissioningPresentation();
            presentation.ApplyProjection(CreateProjection(
                VirtualCameraState.FrameReady,
                hasCameraDefinition: true,
                isRunning: false,
                controlOwner: SimulationControlOwner.Manual,
                projectPath: projectPath,
                sourceRelativePath: "assets/part.pgm"));

            Assert.Equal(Path.GetFullPath(imagePath), presentation.CurrentCameraImagePath);
            Assert.True(presentation.HasCurrentCameraImage);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ProjectImageSourceConverterLoadsMono8Pgm()
    {
        var root = Path.Combine(TestStorage.RootPath, "camera-preview", Guid.NewGuid().ToString("N"));
        var imagePath = Path.Combine(root, "part.pgm");
        Directory.CreateDirectory(root);
        File.WriteAllBytes(imagePath, "P5\n2 2\n255\n"u8.ToArray().Concat(new byte[] { 0, 64, 128, 255 }).ToArray());

        try
        {
            var result = new ProjectImageSourceConverter().Convert(
                imagePath,
                typeof(BitmapSource),
                parameter: null!,
                CultureInfo.InvariantCulture);

            var bitmap = Assert.IsAssignableFrom<BitmapSource>(result);
            Assert.Equal(2, bitmap.PixelWidth);
            Assert.Equal(2, bitmap.PixelHeight);
            var pixels = new byte[4];
            bitmap.CopyPixels(pixels, 2, 0);
            Assert.Equal(new byte[] { 0, 64, 128, 255 }, pixels);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ProjectImageSourceConverterRecoversAfterTruncatedPng()
    {
        var root = Path.Combine(TestStorage.RootPath, "camera-preview", Guid.NewGuid().ToString("N"));
        var imagePath = Path.Combine(root, "part.png");
        Directory.CreateDirectory(root);
        File.WriteAllBytes(imagePath, new byte[] { 137, 80, 78, 71, 13, 10, 26, 10, 0, 0 });
        var converter = new ProjectImageSourceConverter();
        try
        {
            Assert.Same(DependencyProperty.UnsetValue,
                converter.Convert(imagePath, typeof(BitmapSource), null!, CultureInfo.InvariantCulture));
            var pixels = new byte[] { 0, 64, 128, 255 };
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(2, 2, 96, 96, System.Windows.Media.PixelFormats.Gray8, null, pixels, 2)));
            using (var stream = File.Create(imagePath)) encoder.Save(stream);
            var bitmap = Assert.IsAssignableFrom<BitmapSource>(converter.Convert(imagePath, typeof(BitmapSource), null!, CultureInfo.InvariantCulture));
            Assert.Equal(2, bitmap.PixelWidth);
            Assert.Equal(2, bitmap.PixelHeight);
            Assert.True(bitmap.IsFrozen);
            var actual = new byte[4];
            bitmap.CopyPixels(actual, 2, 0);
            Assert.Equal(pixels, actual);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ProjectImageSourceConverterRecoversAsciiBlackPixelsAfterRejectedInput()
    {
        var root = Path.Combine(TestStorage.RootPath, "camera-preview", Guid.NewGuid().ToString("N"));
        var imagePath = Path.Combine(root, "part.pgm");
        Directory.CreateDirectory(root);
        var converter = new ProjectImageSourceConverter();
        string[] rejected =
        [
            "P2\n0 2\n15\n0 1 8 15",
            "P2\n2 0\n15\n0 1 8 15",
            "P2\n2 2\n0\n0 1 8 15",
            "P2\n2 2\n15\n0 1 8 16",
            "P2\n2 2\n15\n0 1 8 -1",
            "P2\n2 2\n15\n0 1 8",
            "P2\n2147483647 1\n255\n0",
            "P5\n2147483647 1\n255\n0"
        ];
        try
        {
            foreach (var input in rejected)
            {
                File.WriteAllText(imagePath, input);
                Assert.Same(DependencyProperty.UnsetValue,
                    converter.Convert(imagePath, typeof(BitmapSource), null!, CultureInfo.InvariantCulture));
                File.WriteAllText(imagePath, "P2\n# black and non-255 scale\n2 2\n15\n0 1 8 15\n");
                var bitmap = Assert.IsAssignableFrom<BitmapSource>(
                    converter.Convert(imagePath, typeof(BitmapSource), null!, CultureInfo.InvariantCulture));
                Assert.Equal(2, bitmap.PixelWidth);
                Assert.Equal(2, bitmap.PixelHeight);
                Assert.True(bitmap.IsFrozen);
                var pixels = new byte[4];
                bitmap.CopyPixels(pixels, 2, 0);
                Assert.Equal(new byte[] { 0, 17, 136, 255 }, pixels);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void AutomaticAcquisitionUsesConfiguredProjectImageWhenFrameEvidenceIsUnavailable()
    {
        var root = Path.Combine(TestStorage.RootPath, "camera-preview", Guid.NewGuid().ToString("N"));
        var imageDirectory = Path.Combine(root, "assets");
        var projectPath = Path.Combine(root, "cell.ovmachine");
        var imagePath = Path.Combine(imageDirectory, "part.pgm");
        Directory.CreateDirectory(imageDirectory);
        File.WriteAllText(projectPath, "{}", System.Text.Encoding.UTF8);
        File.WriteAllBytes(imagePath, "P2\n2 1\n255\n0 255\n"u8.ToArray());

        try
        {
            var presentation = new CameraCommissioningPresentation();
            presentation.ApplyProjection(new CameraCommissioningProjection(
                new VirtualCameraSnapshot(
                    "camera-1",
                    "Camera",
                    VirtualCameraState.FrameReady,
                    1,
                    "camera-1/frame/00000001",
                    "recipe-1",
                    0,
                    0,
                    new VirtualCameraAcquisitionResult(
                        "camera-1/frame/00000001",
                        "camera-1",
                        "recipe-1",
                        1,
                        PlaceholderInspectionDecision.Pass)),
                HasCameraDefinition: true,
                FallbackCameraName: "Camera",
                ImageSource: new VirtualSingleImageSourceDefinition
                {
                    SourceRelativePath = "assets/part.pgm",
                    Width = 2,
                    Height = 1,
                    PixelFormat = "Mono8"
                },
                ProjectPath: projectPath,
                SelectedCameraRecipe: "recipe-1",
                SimulationFixedStep: TimeSpan.FromMilliseconds(5),
                RuntimeRunMode: SimulationRunMode.Paused,
                IsRunMode: true,
                IsApplyingProject: false,
                IsValidationBusy: false,
                IsRuntimeDefinitionDirty: false,
                IsRunning: false,
                ControlOwner: SimulationControlOwner.EmbeddedSequence,
                IsAutomaticRunActive: false,
                ActiveSequenceStatus: SequenceExecutionStatus.Completed));

            Assert.Equal(Path.GetFullPath(imagePath), presentation.CurrentCameraImagePath);
            Assert.True(presentation.HasCurrentCameraImage);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static VirtualCameraExternalResultEvidence CreateExternalEvidence(
        ExternalInspectionResultStatus status,
        ExternalInspectionOutcome outcome) => new(
        Guid.Empty,
        Guid.Empty,
        Guid.Empty,
        Guid.Empty,
        new string('D', 64),
        new ExternalInspectionCorrelationIdentity(
            "project-1",
            "1.0",
            "sequence-1",
            "camera-trigger",
            "camera-1",
            "acquisition-3",
            "frame-3",
            "unit-1",
            "Mono8",
            "image",
            new string('B', 64),
            new string('C', 64),
            new ExternalInspectionConsumerIdentity(
                "VisionConsumer",
                "2.1.0",
                "commit",
                "clean")),
        status,
        outcome,
        "run-1",
        PlaceholderInspectionDecision.Pass);

    private static CameraCommissioningProjection CreateProjection(
        VirtualCameraState state,
        bool hasCameraDefinition,
        bool isRunning,
        SimulationControlOwner controlOwner,
        bool hasUsableSource = true,
        bool isAutomaticRunActive = false,
        bool hasResult = true,
        VirtualCameraExternalResultEvidence? externalResultEvidence = null,
        string? projectPath = null,
        string sourceRelativePath = "images/part.png") => new(
        new VirtualCameraSnapshot(
            "camera-1",
            "Camera",
            state,
            3,
            "acquisition-3",
            "recipe-1",
            2,
            3,
            hasResult
                ? new VirtualCameraAcquisitionResult(
                "acquisition-3",
                "camera-1",
                "recipe-1",
                3,
                PlaceholderInspectionDecision.Pass,
                new VirtualCameraFrameEvidence(
                    "frame-3",
                    sourceRelativePath,
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
                    new Dictionary<string, double> { ["score"] = 0.75 }),
                externalResultEvidence)
                : null,
            new VirtualCameraFrameEvidence(
                "frame-3",
                sourceRelativePath,
                new string('A', 64),
                10,
                10,
                10,
                "Mono8"),
            externalResultEvidence),
        hasCameraDefinition,
        "Camera",
        hasUsableSource
            ? new VirtualSingleImageSourceDefinition
            {
                SourceRelativePath = sourceRelativePath,
                Width = 10,
                Height = 10,
                PixelFormat = "Mono8"
            }
            : null,
        hasUsableSource ? projectPath ?? "C:\\Project" : null,
        hasUsableSource ? "recipe-1" : null,
        TimeSpan.FromMilliseconds(5),
        SimulationRunMode.Paused,
        IsRunMode: true,
        IsApplyingProject: false,
        IsValidationBusy: false,
        IsRuntimeDefinitionDirty: false,
        isRunning,
        controlOwner,
        IsAutomaticRunActive: isAutomaticRunActive,
        ActiveSequenceStatus: null);
}
