namespace OpenVisionLab.Machine.Core.Events;

public interface IMachineEvent
{
    global::OpenVisionLab.Machine.Core.Time.SimulationTime SimulationTime { get; }
}
