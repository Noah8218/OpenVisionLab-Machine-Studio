using OpenVisionLab.Machine.Simulation.Layout;

namespace OpenVisionLab.Machine.Simulation.Commands;

public sealed class SetConveyorCommand : SimulationCommand
{
    public SetConveyorCommand(
        string conveyorId,
        bool running,
        ConveyorDirection direction)
    {
        ConveyorId = conveyorId;
        Running = running;
        Direction = direction;
    }

    public string ConveyorId { get; }
    public bool Running { get; }
    public ConveyorDirection Direction { get; }
}
