namespace OpenVisionLab.Machine.Simulation.Commands;

public sealed class SetSimulationTimeScaleCommand(double timeScale) : SimulationCommand
{
    public double TimeScale { get; } = timeScale;
}
