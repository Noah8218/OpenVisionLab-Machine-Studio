namespace OpenVisionLab.Machine.Simulation.Commands;

public sealed class SetDigitalSensorForceCommand : SimulationCommand
{
    public SetDigitalSensorForceCommand(string sensorId, bool? forcedValue)
    {
        SensorId = sensorId;
        ForcedValue = forcedValue;
    }

    public string SensorId { get; }
    public bool? ForcedValue { get; }
}
