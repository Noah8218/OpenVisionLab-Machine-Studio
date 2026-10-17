using OpenVisionLab;
using OpenVisionLab.Machine.Core.Devices;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Core.Sequences;
using OpenVisionLab.Machine.Persistence.Projects;
using OpenVisionLab.Machine.Simulation.Camera;
using OpenVisionLab.Machine.Sequence.Runtime;
using OpenVisionLab.Machine.Simulation.Commands;
using OpenVisionLab.Machine.Simulation.Compilation;
using OpenVisionLab.Machine.Simulation.Engine;
using OpenVisionLab.MachineStudio.ViewModel;
using System.Security.Cryptography;
using System.Text.Json;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

public sealed class CameraExecutionInputTests
{
    [Fact]
    public async Task NativeFixtureProducesActualNumericalInputsThenResetClearsThem()
    {
        var store = new ProjectDocumentStore();
        string? fixturePath = Environment.GetEnvironmentVariable("OPENVISIONLAB_CAMERA_NATIVE_FIXTURE");
        MachineProjectDocument project;
        if (!string.IsNullOrWhiteSpace(fixturePath)) project = store.Load(File.ReadAllText(fixturePath));
        else
        {
            string json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Samples", "R19InspectionFlow.ovmachine"))
                .Replace("\"device.camera-1\"", "\"camera-2\"", StringComparison.Ordinal)
                .Replace("\"device.height-verdict\"", "\"camera-4\"", StringComparison.Ordinal);
            project = store.Load(json);
            project.Id = "current-camera-input-native-fixture";
            foreach (var device in project.Devices.Where(device => device.Kind == DeviceKind.Camera))
                device.Camera!.NumericalInput = new NumericalCameraSettings { InputKind = device.Id == "camera-2" ? NumericalCameraInputKind.Image2D : NumericalCameraInputKind.HeightMap3D,
                    RecipeName = device.Id == "camera-2" ? "presence-check" : "height-verdict-mock", GenerationDelayMilliseconds = 40, ResponseDelayMilliseconds = 20, Defect = device.Id == "camera-4" };
            project.Devices.Insert(0, new DeviceDefinition { Id = "camera-1", Name = "Uncaptured camera", Kind = DeviceKind.Camera, Camera = new VirtualCameraDefinition() });
        }
        var fixedStep = TimeSpan.FromMilliseconds(project.Simulation.FixedStepMilliseconds);
        var compiled = new MachineProjectRuntimeCompiler(fixedStep).Compile(project);
        Assert.True(compiled.IsSuccess, string.Join("; ", compiled.Errors.Select(error => $"{error.Code}: {error.Message}")));
        using var engine = new FixedStepSimulationEngine(new SimulationSettings { FixedStep = fixedStep, Seed = project.Simulation.Seed });
        await engine.StartAsync();
        try
        {
            Assert.True((await engine.EnqueueCommandAsync(new ConfigureRuntimeCommand(compiled.Configuration!, project.Id))).IsAccepted);
            Assert.True((await engine.EnqueueCommandAsync(new StartSequenceCommand("wafer-efem-cycle"))).IsAccepted);
            for (int tick = 0; tick < 4000 && engine.CurrentSnapshot.Sequences.Single().Status != SequenceExecutionStatus.Completed; tick++)
            {
                Assert.True((await engine.EnqueueCommandAsync(new StepCommand())).IsAccepted);
                Assert.DoesNotContain(engine.CurrentSnapshot.Sequences, sequence => sequence.Status == SequenceExecutionStatus.Faulted);
            }
            var completed = engine.CurrentSnapshot;
            Assert.Equal(SequenceExecutionStatus.Completed, completed.Sequences.Single().Status);
            Assert.Equal(VirtualCameraState.Idle, completed.Cameras.Single(camera => camera.Id == "camera-1").State);
            var inputs = completed.Cameras.Where(camera => camera.Result?.NumericalInput is not null).ToArray();
            Assert.Equal(new[] { "camera-2", "camera-4" }, inputs.Select(camera => camera.Id));
            Assert.All(inputs, camera => Assert.Equal("wafer-workpiece", camera.Result!.WorkpieceComponentId));
            Assert.Equal(inputs[0].Result!.WorkpieceInstanceId, inputs[1].Result!.WorkpieceInstanceId);
            Assert.False(string.IsNullOrWhiteSpace(inputs[0].Result!.WorkpieceInstanceId));
            Assert.Equal(PlaceholderInspectionDecision.Pass, inputs[0].Result!.Decision);
            Assert.Equal(PlaceholderInspectionDecision.Fail, inputs[1].Result!.Decision);
            var exported = new List<object>();
            foreach (var camera in inputs)
            {
                var input = camera.Result!.NumericalInput!;
                var bytes = input.CopyBytes();
                Assert.Equal(input.Frame.ContentSha256, Convert.ToHexString(SHA256.HashData(bytes)));
                if (!string.IsNullOrWhiteSpace(fixturePath))
                {
                    string directory = Path.GetDirectoryName(fixturePath)!;
                    File.WriteAllBytes(Path.Combine(directory, $"{camera.Id}-input.bin"), bytes);
                    File.WriteAllBytes(Path.Combine(directory, $"{camera.Id}-preview.pgm"), "P5\n128 128\n255\n"u8.ToArray().Concat(input.CopyPreview()).ToArray());
                }
                exported.Add(new { camera.Id, camera.CurrentAcquisitionId, camera.CurrentRecipeId, camera.AcquisitionOrdinal, camera.Result.WorkpieceComponentId,
                    camera.Result.WorkpieceInstanceId, decision = camera.Result.Decision.ToString(), input.Frame.FrameId, input.Frame.PixelFormat, input.Frame.ContentSha256, byte_count = bytes.Length });
            }
            Assert.Equal(2, CameraCommissioningPresentation.CreateExecutionInputs(project, completed.Cameras).Sum(item => item.InputCount));
            Assert.True((await engine.EnqueueCommandAsync(new ResetCommand())).IsAccepted);
            Assert.DoesNotContain(engine.CurrentSnapshot.Cameras, camera => camera.Result is not null);
            Assert.Equal(0, CameraCommissioningPresentation.CreateExecutionInputs(project, engine.CurrentSnapshot.Cameras).Sum(item => item.InputCount));
            Assert.True(engine.CurrentSnapshot.RuntimeGeneration > completed.RuntimeGeneration);
            if (!string.IsNullOrWhiteSpace(fixturePath))
                File.WriteAllText(Path.Combine(Path.GetDirectoryName(fixturePath)!, "native-fixture-verification.json"), JsonSerializer.Serialize(new
                {
                    status = "pass", project = fixturePath, project_sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(fixturePath))),
                    scope = "Existing project compiler and in-process engine; actual input bytes and hash; reset. No native EXE/UI launched.",
                    tick = completed.TickIndex, generation = completed.RuntimeGeneration, reset_generation = engine.CurrentSnapshot.RuntimeGeneration, inputs = exported
                }, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally { await engine.StopAsync(); }
    }

    [Fact]
    public void AvailableInputsUseStableCameraIdsAndSelectionDoesNotRunOrMutateProject()
    {
        OpenVisionLanguageService.Load();
        using var fixture = new CameraExecutionInputFixture();
        var before = new ProjectDocumentStore().Serialize(fixture.Project);
        Assert.Equal(new[] { "camera-2", "camera-4" }, fixture.Camera.CurrentCameraInputs.Select(item => item.Id));
        Assert.All(fixture.Camera.CurrentCameraInputs, item => Assert.Equal(1, item.InputCount));
        Assert.Equal(fixture.Camera.CurrentCameraInputs[0].Name, fixture.Camera.CurrentCameraInputs[1].Name);
        Assert.Equal("camera-1", fixture.Camera.SelectedCameraId);
        fixture.Camera.RefreshProjection();
        Assert.Equal("camera-1", fixture.Camera.SelectedCameraId);
        Assert.False(fixture.Camera.HasSelectedCameraInput);
        foreach (string cameraId in new[] { "camera-2", "camera-4" })
        {
            fixture.Camera.SelectCameraCommand.Execute(cameraId);
            Assert.Equal(cameraId, fixture.Camera.SelectedCameraId);
            Assert.True(fixture.Camera.HasSelectedCameraInput);
            Assert.Contains(cameraId == "camera-2" ? "run-1/WP-2" : "run-1/WP-4", fixture.Camera.SelectedInputContextText);
            Assert.Contains(cameraId == "camera-2" ? "recipe-2" : "recipe-4", fixture.Camera.SelectedInputContextText);
        }
        fixture.Camera.SelectCameraCommand.Execute("missing-camera");
        Assert.Equal("camera-4", fixture.Camera.SelectedCameraId);
        Assert.Equal(before, new ProjectDocumentStore().Serialize(fixture.Project));
        Assert.Equal(0, fixture.SideEffects);
    }

    [Theory]
    [InlineData(VirtualCameraState.Idle)]
    [InlineData(VirtualCameraState.Exposing)]
    [InlineData(VirtualCameraState.Transferring)]
    [InlineData(VirtualCameraState.Faulted)]
    public void UnavailableCaptureClearsInputAndImageThenValidCaptureRecovers(VirtualCameraState state)
    {
        OpenVisionLanguageService.Load();
        using var fixture = new CameraExecutionInputFixture();
        fixture.Camera.SelectCameraCommand.Execute("camera-2");
        Assert.NotNull(fixture.Camera.CurrentInputImagePath);
        var original = fixture.Snapshot.Cameras.Single(camera => camera.Id == "camera-2");
        var changed = original with { State = state, Result = null, FrameEvidence = state is VirtualCameraState.Exposing or VirtualCameraState.Transferring ? original.FrameEvidence : null };
        fixture.Snapshot = fixture.CreateSnapshot(fixture.Snapshot.Cameras.Select(camera => camera.Id == changed.Id ? changed : camera));
        fixture.Camera.RefreshProjection();
        Assert.DoesNotContain(fixture.Camera.CurrentCameraInputs, item => item.Id == changed.Id);
        Assert.False(fixture.Camera.HasSelectedCameraInput);
        Assert.Null(fixture.Camera.CurrentInputImagePath);
        Assert.Null(fixture.Camera.CurrentCameraImagePath);
        Assert.Null(fixture.Camera.CurrentNumericalInput);
        string key = state switch { VirtualCameraState.Faulted => "Camera.InputFaulted", VirtualCameraState.Idle => "Camera.InputNotAcquired", _ => "Camera.InputAcquiring" };
        Assert.Equal(OpenVisionLanguageService.T(key), fixture.Camera.SelectedInputStateText);
        fixture.Snapshot = fixture.CreateSnapshot(fixture.Snapshot.Cameras.Select(camera => camera.Id == changed.Id ? original : camera));
        fixture.Camera.RefreshProjection();
        Assert.True(fixture.Camera.HasSelectedCameraInput);
        Assert.NotNull(fixture.Camera.CurrentInputImagePath);
        Assert.Equal(0, fixture.SideEffects);
    }

    [Fact]
    public void AwaitingExternalResultHasOneInputAndPendingDecisionWithoutInspecting()
    {
        OpenVisionLanguageService.Load();
        using var fixture = new CameraExecutionInputFixture();
        fixture.Camera.SelectCameraCommand.Execute("camera-2");
        var original = fixture.Snapshot.Cameras.Single(camera => camera.Id == "camera-2");
        fixture.Snapshot = fixture.CreateSnapshot(fixture.Snapshot.Cameras.Select(camera => camera.Id == original.Id ? camera with { State = VirtualCameraState.AwaitingExternalResult, Result = null } : camera));
        fixture.Camera.RefreshProjection();
        Assert.Equal(1, fixture.Camera.CurrentCameraInputs.Single(item => item.Id == original.Id).InputCount);
        Assert.Equal(OpenVisionLanguageService.T("Camera.InputAwaitingResult"), fixture.Camera.SelectedInputStateText);
        Assert.Equal(OpenVisionLanguageService.T("Shell.ResultPending"), fixture.Camera.CurrentCameraResultText);
        Assert.NotNull(fixture.Camera.CurrentInputImagePath);
        Assert.Equal(0, fixture.SideEffects);
    }

    [Fact]
    public void TriggerOrdinalAndMockDecisionWithoutFrameDoNotBecomeSuccessfulInputCount()
    {
        OpenVisionLanguageService.Load();
        using var fixture = new CameraExecutionInputFixture();
        fixture.Camera.SelectCameraCommand.Execute("camera-2");
        var original = fixture.Snapshot.Cameras.Single(camera => camera.Id == "camera-2");
        var mock = original with { AcquisitionOrdinal = 41, FrameEvidence = null, Result = original.Result! with { AcquisitionOrdinal = 41, FrameEvidence = null, NumericalInput = null } };
        fixture.Snapshot = fixture.CreateSnapshot(fixture.Snapshot.Cameras.Select(camera => camera.Id == mock.Id ? mock : camera));
        fixture.Camera.RefreshProjection();
        Assert.Single(fixture.Camera.CurrentCameraInputs);
        Assert.False(fixture.Camera.HasSelectedCameraInput);
        Assert.Null(fixture.Camera.CurrentInputImagePath);
        Assert.Contains("41", fixture.Camera.SelectedInputAcquisitionText);
        Assert.Equal(string.Format(System.Globalization.CultureInfo.CurrentCulture, OpenVisionLanguageService.T("Camera.InputOnlyMockResult"), OpenVisionLanguageService.T("Shell.ResultPass")), fixture.Camera.SelectedInputStateText);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StaleProjectionCannotShowPreviousInputAfterRestartOrProjectSwitch(bool otherProject)
    {
        OpenVisionLanguageService.Load();
        using var fixture = new CameraExecutionInputFixture();
        fixture.Camera.SelectCameraCommand.Execute("camera-4");
        fixture.ProjectionOverride = fixture.Projection(fixture.Snapshot.Cameras.Single(camera => camera.Id == "camera-4"));
        Assert.NotNull(fixture.Camera.CurrentNumericalInput);
        var nextCameras = otherProject ? fixture.Snapshot.Cameras : fixture.Snapshot.Cameras.Select(camera => CameraExecutionInputFixture.Idle(camera.Id, camera.Name));
        fixture.Snapshot = fixture.CreateSnapshot(nextCameras, generation: 2, projectId: otherProject ? "other-project" : fixture.Project.Id);
        fixture.Camera.RefreshProjection();
        Assert.Empty(fixture.Camera.CurrentCameraInputs);
        Assert.Null(fixture.Camera.CurrentNumericalInput);
        Assert.Null(fixture.Camera.CurrentInputImagePath);
        Assert.Equal("—", fixture.Camera.CurrentCameraResultText);
        Assert.Equal("camera-4", fixture.Camera.SelectedCameraId);
        if (otherProject) Assert.Equal(OpenVisionLanguageService.T("Camera.InputForeignRuntime"), fixture.Camera.CurrentInputSummaryText);
        else Assert.Equal(string.Format(System.Globalization.CultureInfo.CurrentCulture, OpenVisionLanguageService.T("Camera.CurrentInputsSummary"), 2, 0, 4), fixture.Camera.CurrentInputSummaryText);
        Assert.Equal(0, fixture.SideEffects);
    }

    [Fact]
    public void LinkedStepsShowAllConfiguredMatchesAndDoNotClaimRecordedTrigger()
    {
        OpenVisionLanguageService.Load();
        using var fixture = new CameraExecutionInputFixture();
        fixture.Project.Sequences[0].Steps.Add(new SequenceStepDefinition { Id = "alternate-capture", Name = "다른 촬영", Action = SequenceStepAction.TriggerCamera, TargetId = "camera-2", Parameter = "recipe-2" });
        fixture.Camera.SelectCameraCommand.Execute("camera-2");
        Assert.StartsWith(OpenVisionLanguageService.T("Camera.InputLinkedSteps").Split('{')[0], fixture.Camera.SelectedInputStepContextText);
        Assert.Contains("capture-2", fixture.Camera.SelectedInputStepContextText);
        Assert.Contains("alternate-capture", fixture.Camera.SelectedInputStepContextText);
        Assert.DoesNotContain(OpenVisionLanguageService.T("Camera.InputRecordedStep").Split('{')[0], fixture.Camera.SelectedInputStepContextText);
        fixture.Project.Devices.Single(device => device.Id == "camera-2").Name = "새 카메라 이름";
        fixture.Project.Sequences[0].Steps[0].Name = "새 촬영 단계";
        fixture.Camera.RefreshProjection();
        Assert.Equal("새 카메라 이름", fixture.Camera.CurrentCameraInputs.Single(item => item.Id == "camera-2").Name);
        Assert.Contains("새 촬영 단계", fixture.Camera.SelectedInputStepContextText);
        Assert.Equal("camera-2", fixture.Camera.SelectedCameraId);
    }

    [Fact]
    public void DisposedCameraDoesNotRefreshInventoryOrAcceptSelection()
    {
        OpenVisionLanguageService.Load();
        using var fixture = new CameraExecutionInputFixture();
        var before = fixture.Camera.CurrentCameraInputs.ToArray();
        fixture.Camera.Dispose();
        fixture.Snapshot = fixture.CreateSnapshot(Array.Empty<VirtualCameraSnapshot>(), generation: 2);
        fixture.Camera.RefreshProjection();
        fixture.Camera.SelectCameraCommand.Execute("camera-2");
        Assert.Equal(before, fixture.Camera.CurrentCameraInputs);
        Assert.Equal("camera-1", fixture.Camera.SelectedCameraId);
        Assert.Equal(0, fixture.SideEffects);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RecordedExternalStepRequiresMatchingProjectCameraAndAcquisition(bool otherAcquisition)
    {
        OpenVisionLanguageService.Load();
        using var fixture = new CameraExecutionInputFixture();
        var original = fixture.Snapshot.Cameras.Single(camera => camera.Id == "camera-2");
        var correlation = new ExternalInspectionCorrelationIdentity(fixture.Project.Id, fixture.Project.Schema, "inspection", "capture-2", original.Id,
            otherAcquisition ? "previous-acquisition" : original.CurrentAcquisitionId!, original.FrameEvidence!.FrameId, "unit", "Mono8", "image",
            new string('A', 64), new string('B', 64), new ExternalInspectionConsumerIdentity("VisionConsumer", "2.1.0", "commit", "clean"));
        var evidence = new VirtualCameraExternalResultEvidence(Guid.Empty, Guid.Empty, Guid.Empty, Guid.Empty, new string('C', 64), correlation,
            ExternalInspectionResultStatus.Completed, ExternalInspectionOutcome.Pass, "external-run-1", PlaceholderInspectionDecision.Pass);
        fixture.Snapshot = fixture.CreateSnapshot(fixture.Snapshot.Cameras.Select(camera => camera.Id == original.Id ? camera with { ExternalResultEvidence = evidence } : camera));
        fixture.Camera.SelectCameraCommand.Execute("camera-2");
        string key = otherAcquisition ? "Camera.InputLinkedSteps" : "Camera.InputRecordedStep";
        Assert.StartsWith(OpenVisionLanguageService.T(key).Split('{')[0], fixture.Camera.SelectedInputStepContextText);
        Assert.Contains("capture-2", fixture.Camera.SelectedInputStepContextText);
        Assert.Equal(0, fixture.SideEffects);
    }
}
