using System.Collections.ObjectModel;
using OpenVisionLab.Machine.Simulation.Camera;

namespace OpenVisionLab.Machine.Simulation.Commands;

/// <summary>
/// Binds immutable, preflighted frame sources to one automatic Sequence.
/// The command is the explicit boundary between asynchronous source I/O and
/// the deterministic fixed-step runtime.
/// </summary>
public sealed class ArmAutomaticExternalInspectionCommand : SimulationCommand
{
    public ArmAutomaticExternalInspectionCommand(
        SimulationRuntimeIdentity expectedRuntime,
        string sequenceId,
        IReadOnlyDictionary<string, VirtualCameraExternalSource> sources)
    {
        if (string.IsNullOrWhiteSpace(expectedRuntime.ProjectId))
        {
            throw new ArgumentException(
                "Automatic external inspection requires a project-bound runtime.",
                nameof(expectedRuntime));
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(sequenceId);
        ArgumentNullException.ThrowIfNull(sources);
        if (sources.Count == 0)
        {
            throw new ArgumentException(
                "At least one automatic external camera source is required.",
                nameof(sources));
        }

        var copied = new SortedDictionary<string, VirtualCameraExternalSource>(StringComparer.Ordinal);
        foreach (var (cameraId, source) in sources)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(cameraId);
            ArgumentNullException.ThrowIfNull(source);
            copied.Add(cameraId, source);
        }

        ExpectedRuntime = expectedRuntime;
        SequenceId = sequenceId;
        Sources = new ReadOnlyDictionary<string, VirtualCameraExternalSource>(copied);
    }

    public string SequenceId { get; }

    public IReadOnlyDictionary<string, VirtualCameraExternalSource> Sources { get; }
}
