using System.Collections.ObjectModel;

namespace OpenVisionLab.Machine.Vision.Models;

public sealed class VirtualAcquisitionContext
{
    public VirtualAcquisitionContext(
        string acquisitionId,
        string cameraId,
        string recipeId,
        long simulationTick,
        TimeSpan simulationTime,
        int seed,
        IReadOnlyDictionary<string, double>? axisPositions = null)
    {
        AcquisitionId = VisionContractValidation.RequiredIdentifier(acquisitionId, nameof(acquisitionId));
        CameraId = VisionContractValidation.RequiredIdentifier(cameraId, nameof(cameraId));
        RecipeId = VisionContractValidation.RequiredIdentifier(recipeId, nameof(recipeId));

        if (simulationTick < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(simulationTick), simulationTick, "Simulation tick cannot be negative.");
        }

        if (simulationTime < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(simulationTime), simulationTime, "Simulation time cannot be negative.");
        }

        var copiedPositions = new SortedDictionary<string, double>(StringComparer.Ordinal);
        if (axisPositions is not null)
        {
            foreach (var (axisId, position) in axisPositions)
            {
                var validatedAxisId = VisionContractValidation.RequiredIdentifier(axisId, nameof(axisPositions));
                if (!double.IsFinite(position))
                {
                    throw new ArgumentOutOfRangeException(nameof(axisPositions), position, $"Axis '{validatedAxisId}' position must be finite.");
                }

                if (!copiedPositions.TryAdd(validatedAxisId, position))
                {
                    throw new ArgumentException($"Axis identifier '{validatedAxisId}' is duplicated.", nameof(axisPositions));
                }
            }
        }

        SimulationTick = simulationTick;
        SimulationTime = simulationTime;
        Seed = seed;
        AxisPositions = new ReadOnlyDictionary<string, double>(copiedPositions);
    }

    public string AcquisitionId { get; }

    public string CameraId { get; }

    public string RecipeId { get; }

    public long SimulationTick { get; }

    public TimeSpan SimulationTime { get; }

    public int Seed { get; }

    public IReadOnlyDictionary<string, double> AxisPositions { get; }
}
