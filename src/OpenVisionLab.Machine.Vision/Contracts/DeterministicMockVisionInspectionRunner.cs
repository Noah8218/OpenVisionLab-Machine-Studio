using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using OpenVisionLab.Machine.Vision.Models;

namespace OpenVisionLab.Machine.Vision.Contracts;

public sealed class DeterministicMockVisionInspectionRunner : IVisionInspectionRunner
{
    private const string CanonicalContract = "OpenVisionLab.Machine.Vision.DeterministicMock/v1";

    public DeterministicMockVisionInspectionRunner(IReadOnlyDictionary<string, VisionJudgment> recipeJudgments)
    {
        ArgumentNullException.ThrowIfNull(recipeJudgments);

        var copiedJudgments = new SortedDictionary<string, VisionJudgment>(StringComparer.Ordinal);
        foreach (var (recipeId, judgment) in recipeJudgments)
        {
            if (string.IsNullOrWhiteSpace(recipeId) || !string.Equals(recipeId, recipeId.Trim(), StringComparison.Ordinal))
            {
                throw new ArgumentException("Recipe identifiers must be non-empty and cannot start or end with whitespace.", nameof(recipeJudgments));
            }

            if (!Enum.IsDefined(judgment) || judgment == VisionJudgment.None)
            {
                throw new ArgumentException($"Recipe '{recipeId}' must have an explicit defined non-None judgment.", nameof(recipeJudgments));
            }

            if (!copiedJudgments.TryAdd(recipeId, judgment))
            {
                throw new ArgumentException($"Recipe identifier '{recipeId}' is duplicated.", nameof(recipeJudgments));
            }
        }

        RecipeJudgments = new ReadOnlyDictionary<string, VisionJudgment>(copiedJudgments);
    }

    public IReadOnlyDictionary<string, VisionJudgment> RecipeJudgments { get; }

    public Task<VisionRunResult> RunAsync(
        VisionRecipeReference recipe,
        VirtualFrameDescriptor frame,
        CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<VisionRunResult>(cancellationToken);
        }

        ArgumentNullException.ThrowIfNull(recipe);
        ArgumentNullException.ThrowIfNull(frame);

        if (!string.Equals(recipe.Id, frame.Context.RecipeId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Recipe '{recipe.Id}' does not match acquisition recipe '{frame.Context.RecipeId}'.");
        }

        if (!RecipeJudgments.TryGetValue(recipe.Id, out var judgment))
        {
            throw new KeyNotFoundException($"Recipe '{recipe.Id}' has no configured deterministic judgment.");
        }

        var metrics = new SortedDictionary<string, double>(StringComparer.Ordinal)
        {
            ["ContentLengthBytes"] = frame.ContentLength,
            ["PixelCount"] = (double)frame.Width * frame.Height,
            ["SimulationTick"] = frame.Context.SimulationTick
        };

        return Task.FromResult(new VisionRunResult(
            CreateInspectionId(recipe, frame),
            frame.AcquisitionId,
            frame.CameraId,
            frame.RecipeId,
            frame.FrameId,
            judgment,
            $"Deterministic mock inspection completed with {judgment}.",
            metrics));
    }

    private static string CreateInspectionId(VisionRecipeReference recipe, VirtualFrameDescriptor frame)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        AppendString(hash, CanonicalContract);
        AppendString(hash, recipe.Id);
        AppendString(hash, recipe.RelativePath);
        AppendString(hash, frame.Context.AcquisitionId);
        AppendString(hash, frame.Context.CameraId);
        AppendString(hash, frame.Context.RecipeId);
        AppendInt64(hash, frame.Context.SimulationTick);
        AppendInt64(hash, frame.Context.SimulationTime.Ticks);
        AppendInt32(hash, frame.Context.Seed);
        AppendInt32(hash, frame.Context.AxisPositions.Count);
        foreach (var (axisId, position) in frame.Context.AxisPositions)
        {
            AppendString(hash, axisId);
            AppendInt64(hash, BitConverter.DoubleToInt64Bits(position));
        }

        AppendString(hash, frame.FrameId);
        AppendString(hash, frame.SourceRelativePath);
        AppendString(hash, frame.ContentSha256);
        AppendInt64(hash, frame.ContentLength);
        AppendInt32(hash, frame.Width);
        AppendInt32(hash, frame.Height);
        AppendString(hash, frame.PixelFormat);

        return $"inspection/sha256/{Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant()}";
    }

    private static void AppendString(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        AppendInt32(hash, bytes.Length);
        hash.AppendData(bytes);
    }

    private static void AppendInt32(IncrementalHash hash, int value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        hash.AppendData(bytes);
    }

    private static void AppendInt64(IncrementalHash hash, long value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64BigEndian(bytes, value);
        hash.AppendData(bytes);
    }
}
