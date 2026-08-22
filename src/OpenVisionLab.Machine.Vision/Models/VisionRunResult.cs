using System.Collections.ObjectModel;

namespace OpenVisionLab.Machine.Vision.Models;

public sealed class VisionRunResult
{
    public string InspectionId { get; }
    public string AcquisitionId { get; }
    public string CameraId { get; }
    public string RecipeId { get; }
    public string FrameId { get; }
    public VisionJudgment Judgment { get; }
    public string Message { get; }
    public IReadOnlyDictionary<string, double> Metrics { get; }

    public VisionRunResult(
        string inspectionId,
        string acquisitionId,
        string cameraId,
        string recipeId,
        string frameId,
        VisionJudgment judgment,
        string message,
        IReadOnlyDictionary<string, double>? metrics = null)
    {
        InspectionId = VisionContractValidation.RequiredIdentifier(inspectionId, nameof(inspectionId));
        AcquisitionId = VisionContractValidation.RequiredIdentifier(acquisitionId, nameof(acquisitionId));
        CameraId = VisionContractValidation.RequiredIdentifier(cameraId, nameof(cameraId));
        RecipeId = VisionContractValidation.RequiredIdentifier(recipeId, nameof(recipeId));
        FrameId = VisionContractValidation.RequiredIdentifier(frameId, nameof(frameId));

        if (!Enum.IsDefined(judgment) || judgment == VisionJudgment.None)
        {
            throw new ArgumentOutOfRangeException(nameof(judgment), judgment, "A defined non-None judgment is required.");
        }

        Judgment = judgment;
        Message = VisionContractValidation.RequiredText(message, nameof(message));

        var copiedMetrics = new SortedDictionary<string, double>(StringComparer.Ordinal);
        if (metrics is not null)
        {
            foreach (var (metricName, metricValue) in metrics)
            {
                var validatedMetricName = VisionContractValidation.RequiredIdentifier(metricName, nameof(metrics));
                if (!double.IsFinite(metricValue))
                {
                    throw new ArgumentOutOfRangeException(nameof(metrics), metricValue, $"Metric '{validatedMetricName}' must be finite.");
                }

                if (!copiedMetrics.TryAdd(validatedMetricName, metricValue))
                {
                    throw new ArgumentException($"Metric name '{validatedMetricName}' is duplicated.", nameof(metrics));
                }
            }
        }

        Metrics = new ReadOnlyDictionary<string, double>(copiedMetrics);
    }
}

public enum VisionJudgment
{
    None,
    OK,
    NG,
    Error
}
