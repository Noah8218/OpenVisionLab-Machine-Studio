using OpenVisionLab.Machine.Simulation.Scenarios;

namespace OpenVisionLab.MachineStudio.ViewModel;

internal enum SimulationScenarioBatchParticipantOutcome
{
    Idle,
    Completed,
    Cancelled,
    Failed,
    TimedOut
}

internal sealed record SimulationScenarioBatchParticipantResult(
    SimulationScenarioBatchParticipantOutcome Outcome,
    DeterministicSimulationBatchResultPackage? Batch = null,
    Exception? Exception = null)
{
    internal bool IsTimedOut => Outcome == SimulationScenarioBatchParticipantOutcome.TimedOut;
}

/// <summary>
/// Owns one scenario-batch task lifetime and exposes its bounded observation to
/// session close. Batch execution and artifact policy remain with the ViewModel.
/// </summary>
internal sealed class SimulationScenarioBatchParticipant : IDisposable
{
    private readonly AsyncOperationParticipant<SimulationScenarioBatchParticipantResult> _participant = new(
        static () => new(SimulationScenarioBatchParticipantOutcome.Idle),
        static exception => new(SimulationScenarioBatchParticipantOutcome.Cancelled, Exception: exception),
        static exception => new(SimulationScenarioBatchParticipantOutcome.TimedOut, Exception: exception),
        static exception => new(SimulationScenarioBatchParticipantOutcome.Failed, Exception: exception));

    internal Task? CurrentTask => _participant.CurrentTask;

    internal Task<SimulationScenarioBatchParticipantResult> TrackAsync(
        Func<CancellationToken, Task<SimulationScenarioBatchParticipantResult>> operation)
        => _participant.TrackAsync(operation);

    internal void Cancel() => _participant.Cancel();

    internal Task<SimulationScenarioBatchParticipantResult> ObserveAsync(TimeSpan timeout)
        => _participant.ObserveAsync(timeout);

    public void Dispose() => _participant.Dispose();
}
