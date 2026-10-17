using System.Buffers.Binary;
using System.Security.Cryptography;
using OpenVisionLab.Machine.Core.Devices;

namespace OpenVisionLab.Machine.Simulation.Camera;

/// <summary>Immutable generated samples and evidence, consumed by the local numerical mock inspector.</summary>
public sealed class NumericalCameraInput
{
    private readonly byte[] _bytes;
    private readonly byte[] _preview;
    private readonly byte[] _inspectionBytes;
    private NumericalCameraInput(byte[] bytes, byte[] preview, double[] samples, VirtualCameraFrameEvidence frame, VirtualCameraInspectionEvidence inspection, byte[] inspectionBytes, bool externalInspection)
    {
        _bytes = bytes;
        _preview = preview;
        _inspectionBytes = inspectionBytes;
        IsExternalInspection = externalInspection;
        Samples = Array.AsReadOnly(samples);
        Frame = frame;
        Inspection = inspection;
    }

    public const int Width = 128;
    public const int Height = 128;
    public IReadOnlyList<double> Samples { get; }
    public VirtualCameraFrameEvidence Frame { get; }
    public VirtualCameraInspectionEvidence Inspection { get; }
    public bool IsExternalInspection { get; }
    public byte[] CopyInspectionBytes() => (byte[])_inspectionBytes.Clone();
    public byte[] CopyBytes() => (byte[])_bytes.Clone();
    public byte[] CopyPreview() => (byte[])_preview.Clone();

    public static NumericalCameraInput Generate(NumericalCameraSettings settings, string cameraId, string acquisitionId, long ordinal, double axisOffset = 0, bool externalInspection = false)
    {
        if (settings.Validate() is { } error) throw new ArgumentException(error, nameof(settings));
        if (!double.IsFinite(axisOffset)) throw new ArgumentOutOfRangeException(nameof(axisOffset));
        int width = settings.ResolutionWidth ?? Width, height = settings.ResolutionHeight ?? Height;
        bool heightMap = settings.InputKind == NumericalCameraInputKind.HeightMap3D;
        var samples = new double[width * height];
        var preview = new byte[samples.Length];
        var bytes = new byte[samples.Length * (heightMap ? 4 : 1)];
        var random = new Random(unchecked(settings.Seed + (int)ordinal));
        double dx = settings.OffsetX + settings.CameraOffsetX + axisOffset + (random.NextDouble() * 2 - 1) * settings.Shake;
        double dy = settings.OffsetY + settings.CameraCenterY + (random.NextDouble() * 2 - 1) * settings.Shake;
        double yaw = settings.Yaw * Math.PI / 180;
        double tilt = Math.Tan(settings.Tilt * Math.PI / 180);
        int objectPixels = 0, visiblePixels = 0, darkPixels = 0;
        double peak = double.NegativeInfinity;
        for (int row = 0; row < height; row++)
        for (int column = 0; column < width; column++)
        {
            int index = row * width + column;
            double x = ((column + .5) / width - .5) * settings.FieldOfView - dx;
            double y = ((row + .5) / height - .5) * (settings.FieldOfViewHeight ?? settings.FieldOfView) - dy;
            double u = x * Math.Cos(yaw) + y * Math.Sin(yaw);
            double v = -x * Math.Sin(yaw) + y * Math.Cos(yaw);
            bool inside = Math.Abs(u) <= 20 && Math.Abs(v) <= 12;
            bool flaw = inside && settings.Defect && u > 0 && v > 0;
            double value = heightMap ? tilt * x + (inside ? flaw ? 8 : 5 : 0) + (random.NextDouble() * 2 - 1) * .02 / Math.Max(.1, settings.Light)
                : Math.Clamp((inside ? flaw ? 35 : 190 : 25) * settings.Light + random.NextDouble() * 2, 0, 255);
            samples[index] = heightMap ? (float)value : Math.Round(value);
            preview[index] = heightMap ? (byte)Math.Clamp((value + 2) * 25, 0, 255) : (byte)samples[index];
            if (heightMap) BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(index * 4, 4), (float)value);
            else bytes[index] = preview[index];
            if (!inside) continue;
            objectPixels++;
            if (value >= 128) visiblePixels++;
            else darkPixels++;
            peak = Math.Max(peak, samples[index]);
        }
        double coverage = objectPixels / (double)samples.Length;
        bool passed = objectPixels > 0 && settings.Light > .1 && coverage is >= .01 and <= .9 && (heightMap ? peak is >= 4 and <= 6 : visiblePixels > 0 && darkPixels / (double)objectPixels < .1);
        var inspectionBytes = externalInspection ? EncodeInspectionSource(bytes, heightMap, width, height) : bytes;
        var evidenceBytes = externalInspection ? inspectionBytes : bytes;
        string hash = Convert.ToHexString(SHA256.HashData(evidenceBytes));
        string sourcePath = externalInspection ? "assets/generated-capture/" + hash + (heightMap ? ".c3d" : ".pgm") : "simulation-input/" + acquisitionId;
        var frame = new VirtualCameraFrameEvidence(acquisitionId, sourcePath, hash, evidenceBytes.Length, width, height,
            heightMap ? "HeightMapFloat32LE" : "Gray8");
        var metrics = new Dictionary<string, double>
        {
            ["coverage"] = coverage, ["peak"] = objectPixels == 0 ? 0 : peak,
            ["dx"] = dx, ["dy"] = dy, ["yaw"] = settings.Yaw, ["tilt"] = settings.Tilt, ["axisOffset"] = axisOffset,
            ["light"] = settings.Light, ["seed"] = settings.Seed, ["recipeVersion"] = settings.Version,
            ["confirmedVersion"] = settings.ConfirmedVersion, ["sampleCount"] = samples.Length
        };
        var inspection = new VirtualCameraInspectionEvidence("numerical-mock/" + acquisitionId, acquisitionId, cameraId, settings.RecipeName, acquisitionId,
            passed ? PlaceholderInspectionDecision.Pass : PlaceholderInspectionDecision.Fail,
            heightMap ? "수치 모의 검사 · 실제 Float32 높이 입력 · 외부 AI/교정 검증 아님" : "수치 모의 검사 · 실제 Gray8 입력 · 외부 AI/교정 검증 아님", metrics);
        return new NumericalCameraInput(bytes, preview, samples, frame, inspection, inspectionBytes, externalInspection);
    }
    private static byte[] EncodeInspectionSource(byte[] samples, bool heightMap, int width, int height)
    {
        // C3D dimensions + Float32LE, or portable Gray8 PGM for the 2D decoder.
        byte[] header = heightMap ? new byte[8] : System.Text.Encoding.ASCII.GetBytes($"P5\n{width} {height}\n255\n");
        if (heightMap)
        {
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(0, 4), width);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4, 4), height);
        }
        var encoded = new byte[header.Length + samples.Length];
        header.CopyTo(encoded, 0);
        samples.CopyTo(encoded, header.Length);
        return encoded;
    }
}
