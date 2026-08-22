namespace OpenVisionLab.Machine.Simulation.Commands;

public abstract class SimulationCommand
{
    private readonly TaskCompletionSource<SimulationCommandResult> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public string CommandId { get; } = Guid.NewGuid().ToString("n");
    public DateTimeOffset IssuedAt { get; } = DateTimeOffset.UtcNow;

    internal Task<SimulationCommandResult> Completion => _completion.Task;

    internal bool TryComplete(SimulationCommandResult result) =>
        _completion.TrySetResult(result);
}
