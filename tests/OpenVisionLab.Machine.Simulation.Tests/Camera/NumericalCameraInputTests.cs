using System.Buffers.Binary;
using System.Security.Cryptography;
using OpenVisionLab.Machine.Core.Devices;
using OpenVisionLab.Machine.Simulation.Camera;
using Xunit;

namespace OpenVisionLab.Machine.Simulation.Tests;

public sealed class NumericalCameraInputTests
{
    [Theory]
    [InlineData(NumericalCameraInputKind.Image2D)]
    [InlineData(NumericalCameraInputKind.HeightMap3D)]
    public void SamplesDetermineTheHashAndVerdictAndDefectChangesTheInput(NumericalCameraInputKind kind)
    {
        var settings = new NumericalCameraSettings { InputKind = kind };
        var input = NumericalCameraInput.Generate(settings, "camera", "camera/frame/1", 1);
        Assert.Equal(PlaceholderInspectionDecision.Pass, input.Inspection.Decision);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(input.CopyBytes())), input.Frame.ContentSha256);
        Assert.Equal(input.Frame.ContentSha256, NumericalCameraInput.Generate(settings, "camera", "camera/frame/1", 1).Frame.ContentSha256);
        var defect = NumericalCameraInput.Generate(settings with { Defect = true }, "camera", "camera/frame/2", 2);
        Assert.Equal(PlaceholderInspectionDecision.Fail, defect.Inspection.Decision);
        Assert.NotEqual(input.Frame.ContentSha256, defect.Frame.ContentSha256);
        var bytes = input.CopyBytes();
        if (kind == NumericalCameraInputKind.HeightMap3D)
        {
            Assert.Equal("HeightMapFloat32LE", input.Frame.PixelFormat);
            Assert.Equal(input.Samples[100], BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(400, 4)));
        }
        else Assert.Equal(input.Samples[100], bytes[100]);
        bytes[0] ^= 255;
        Assert.Equal(input.Frame.ContentSha256, Convert.ToHexString(SHA256.HashData(input.CopyBytes())));
    }

    [Theory]
    [InlineData(NumericalCameraInputKind.Image2D)]
    [InlineData(NumericalCameraInputKind.HeightMap3D)]
    public void ExternalCaptureHasDecodablePayloadAndNoLocalVerdictWhileWaiting(NumericalCameraInputKind kind)
    {
        var settings = new NumericalCameraSettings { InputKind = kind };
        var camera = new DeterministicVirtualCamera(new("c", "c", 1, 1, PlaceholderInspectionDecision.Pass, settings));
        Assert.True(camera.Trigger(settings.RecipeName, waitForExternalResult: true, axisOffset: 12).IsAccepted);
        camera.Tick();
        var pending = camera.Tick().Snapshot;
        Assert.Equal(VirtualCameraState.AwaitingExternalResult, pending.State);
        Assert.Null(pending.Result);
        var input = Assert.IsType<NumericalCameraInput>(pending.NumericalInput);
        Assert.True(input.IsExternalInspection);
        Assert.Equal(12, input.Inspection.Metrics.Single(metric => metric.Key == "axisOffset").Value);
        var payload = input.CopyInspectionBytes();
        Assert.Equal(payload.LongLength, input.Frame.ContentLength);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(payload)), input.Frame.ContentSha256);
        if (kind == NumericalCameraInputKind.Image2D)
        {
            var header = System.Text.Encoding.ASCII.GetBytes("P5\n128 128\n255\n");
            Assert.Equal(header, payload[..header.Length]);
            Assert.Equal(input.CopyBytes(), payload[header.Length..]);
        }
        else
        {
            Assert.Equal(128, BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(0, 4)));
            Assert.Equal(128, BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(4, 4)));
            Assert.Equal(input.CopyBytes(), payload[8..]);
        }
        payload[0] ^= 255;
        Assert.Equal(input.Frame.ContentSha256, Convert.ToHexString(SHA256.HashData(input.CopyInspectionBytes())));
        Assert.NotEqual(input.Frame.ContentSha256,
            NumericalCameraInput.Generate(settings, "c", input.Frame.FrameId, 1, axisOffset: 0, externalInspection: true).Frame.ContentSha256);
        Assert.Equal(VirtualCameraTriggerErrorCode.CameraBusy, camera.Trigger(settings.RecipeName).ErrorCode);
        camera.Reset();
        Assert.Null(camera.CaptureSnapshot().NumericalInput);
        Assert.True(camera.Trigger(settings.RecipeName, waitForExternalResult: true, axisOffset: 12).IsAccepted);
        camera.Tick();
        Assert.Equal(input.Frame.ContentSha256, camera.Tick().Snapshot.FrameEvidence!.ContentSha256);
        camera.Fault();
        Assert.Null(camera.CaptureSnapshot().NumericalInput);
    }

    [Fact]
    public void VariationsChangeTheActualSamplesAndCroppingOrDarknessFail()
    {
        var settings = new NumericalCameraSettings();
        var baseline = NumericalCameraInput.Generate(settings, "c", "c/1", 1);
        foreach (var changed in new[] { settings with { OffsetX = 10 }, settings with { OffsetY = 5 }, settings with { Yaw = 30 }, settings with { FieldOfView = 100 },
            settings with { Shake = 2 }, settings with { Light = .5 }, settings with { Seed = 43 } })
            Assert.NotEqual(baseline.Frame.ContentSha256, NumericalCameraInput.Generate(changed, "c", "c/1", 1).Frame.ContentSha256);
        Assert.Equal(PlaceholderInspectionDecision.Fail, NumericalCameraInput.Generate(settings with { OffsetX = 1000 }, "c", "c/1", 1).Inspection.Decision);
        Assert.Equal(PlaceholderInspectionDecision.Fail, NumericalCameraInput.Generate(settings with { Light = 0 }, "c", "c/1", 1).Inspection.Decision);
        var height = settings with { InputKind = NumericalCameraInputKind.HeightMap3D };
        Assert.NotEqual(NumericalCameraInput.Generate(height, "c", "c/1", 1).Frame.ContentSha256,
            NumericalCameraInput.Generate(height with { Tilt = 5 }, "c", "c/1", 1).Frame.ContentSha256);
    }

    [Fact]
    public void UnconfirmedVersionDoesNotStartCaptureAndResetAllowsDeterministicRecovery()
    {
        var settings = new NumericalCameraSettings { Version = 2, ConfirmedVersion = 1 };
        var unconfirmed = new DeterministicVirtualCamera(new("c", "c", 1, 1, PlaceholderInspectionDecision.Pass, settings));
        Assert.Equal(VirtualCameraTriggerErrorCode.RecipeVersionUnconfirmed, unconfirmed.Trigger(settings.RecipeName).ErrorCode);
        Assert.Equal(VirtualCameraState.Idle, unconfirmed.State);
        var camera = new DeterministicVirtualCamera(new("c", "c", 1, 1, PlaceholderInspectionDecision.Fail, settings with { ConfirmedVersion = 2 }));
        Assert.Equal(VirtualCameraTriggerErrorCode.RecipeMismatch, camera.Trigger("other").ErrorCode);
        Assert.True(camera.Trigger(settings.RecipeName).IsAccepted);
        camera.Tick();
        var result = camera.Tick().CompletedAcquisition!;
        Assert.NotNull(result.NumericalInput);
        Assert.Equal(PlaceholderInspectionDecision.Pass, result.Decision);
        camera.Reset();
        Assert.True(camera.Trigger(settings.RecipeName).IsAccepted);
        camera.Tick();
        Assert.Equal(result.FrameEvidence!.ContentSha256, camera.Tick().CompletedAcquisition!.FrameEvidence!.ContentSha256);
        Assert.Throws<ArgumentException>(() => new VirtualCameraConfiguration("c", "c", 1, 1, PlaceholderInspectionDecision.Pass, settings with { FieldOfView = double.NaN }));
    }
    [Theory]
    [InlineData(NumericalCameraInputKind.Image2D)]
    [InlineData(NumericalCameraInputKind.HeightMap3D)]
    public void InspectorGeometryProducesOddNonSquareFramesWithMatchingPayloadHeaders(NumericalCameraInputKind kind)
    {
        var settings = new NumericalCameraSettings { InputKind = kind, FieldOfView = 160, FieldOfViewHeight = 80, ResolutionWidth = 31, ResolutionHeight = 17, CameraOffsetX = 5, CameraCenterY = 2 };
        var input = NumericalCameraInput.Generate(settings, "camera", "frame", 1, externalInspection: true);
        Assert.Equal(31, input.Frame.Width);
        Assert.Equal(17, input.Frame.Height);
        Assert.Equal(31 * 17, input.Samples.Count);
        var payload = input.CopyInspectionBytes();
        Assert.Equal(Convert.ToHexString(SHA256.HashData(payload)), input.Frame.ContentSha256);
        if (kind == NumericalCameraInputKind.Image2D)
        {
            var header = System.Text.Encoding.ASCII.GetBytes("P5\n31 17\n255\n");
            Assert.Equal(header, payload[..header.Length]);
            Assert.Equal(31 * 17, payload.Length - header.Length);
        }
        else
        {
            Assert.Equal(31, BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(0, 4)));
            Assert.Equal(17, BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(4, 4)));
            Assert.Equal(31 * 17 * 4, payload.Length - 8);
        }
        Assert.NotEqual(input.Frame.ContentSha256, NumericalCameraInput.Generate(settings with { CameraOffsetX = 0 }, "camera", "frame", 1, externalInspection: true).Frame.ContentSha256);
        Assert.NotNull((settings with { ResolutionWidth = 0 }).Validate());
        Assert.NotNull((settings with { FieldOfViewHeight = double.NaN }).Validate());
    }

}
