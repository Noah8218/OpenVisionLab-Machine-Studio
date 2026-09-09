namespace OpenVisionLab.Machine.Simulation.Commands;

public enum AxisJogDirection
{
    Negative,
    Positive
}

public sealed class JogAxisCommand : SimulationCommand
{
    public JogAxisCommand(string axisId, AxisJogDirection direction)
    {
        AxisId = axisId;
        Direction = direction;
    }

    public string AxisId { get; }
    public AxisJogDirection Direction { get; }
}
