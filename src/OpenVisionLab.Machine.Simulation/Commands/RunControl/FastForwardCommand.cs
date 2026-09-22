namespace OpenVisionLab.Machine.Simulation.Commands;

/// <summary>
/// Runs a finite number of deterministic fixed ticks without wall-clock
/// pacing. External inspection waits are not accelerated by this command.
/// </summary>
public sealed class FastForwardCommand : SimulationCommand
{
    public FastForwardCommand(int tickBudget)
    {
        if (tickBudget <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(tickBudget), "Tick budget must be positive.");
        }

        TickBudget = tickBudget;
    }

    public int TickBudget { get; }
}
