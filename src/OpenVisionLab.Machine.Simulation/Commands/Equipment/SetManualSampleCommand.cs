namespace OpenVisionLab.Machine.Simulation.Commands;

public sealed class SetManualSampleCommand(double x, double y, bool clear = false) : SimulationCommand
{
    public double X { get; } = x;
    public double Y { get; } = y;
    public bool Clear { get; } = clear;
}
