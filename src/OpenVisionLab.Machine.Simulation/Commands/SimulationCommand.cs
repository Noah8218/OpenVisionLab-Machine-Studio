namespace OpenVisionLab.Machine.Simulation.Commands;

/// <summary>Identifies the runtime that an explicitly bound command may affect.</summary>
public readonly record struct SimulationRuntimeIdentity(string? ProjectId, long RuntimeGeneration);

public abstract class SimulationCommand
{
    private readonly TaskCompletionSource<SimulationCommandResult> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public string CommandId { get; } = Guid.NewGuid().ToString("n");
    public DateTimeOffset IssuedAt { get; } = DateTimeOffset.UtcNow;

    // Null preserves existing unbound callers. Once created, a queued command's
    // expectation cannot be changed by its caller.
    public SimulationRuntimeIdentity? ExpectedRuntime { get; init; }

    internal Task<SimulationCommandResult> Completion => _completion.Task;

    internal bool TryComplete(SimulationCommandResult result) =>
        _completion.TrySetResult(result);
}
