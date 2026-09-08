using OpenVisionLab.Machine.Simulation.Commissioning;

namespace OpenVisionLab.MachineStudio.ViewModel;

internal enum MultiAxisCommissioningParticipantOutcome
{
    Idle,
    Completed,
    Cancelled,
    Failed,
    TimedOut
}

internal sealed record MultiAxisCommissioningParticipantResult(
    MultiAxisCommissioningParticipantOutcome Outcome,
    DeterministicMultiAxisCommissioningResultPackage? Validation = null,
    Exception? Exception = null)
{
    internal bool IsTimedOut => Outcome == MultiAxisCommissioningParticipantOutcome.TimedOut;
}

/// <summary>
/// Owns the multi-axis commissioning validation task lifetime and exposes a
/// typed, bounded observation boundary to session close.
/// </summary>
internal sealed class MultiAxisCommissioningParticipant : IDisposable
{
    private readonly AsyncOperationParticipant<MultiAxisCommissioningParticipantResult> _participant = new(
        static () => new(MultiAxisCommissioningParticipantOutcome.Idle),
        static exception => new(MultiAxisCommissioningParticipantOutcome.Cancelled, Exception: exception),
        static exception => new(MultiAxisCommissioningParticipantOutcome.TimedOut, Exception: exception),
        static exception => new(MultiAxisCommissioningParticipantOutcome.Failed, Exception: exception));

    internal Task? CurrentTask => _participant.CurrentTask;

    internal Task<MultiAxisCommissioningParticipantResult> TrackAsync(
        Func<CancellationToken, Task<MultiAxisCommissioningParticipantResult>> operation)
        => _participant.TrackAsync(operation);

    internal void Cancel() => _participant.Cancel();

    internal Task<MultiAxisCommissioningParticipantResult> ObserveAsync(TimeSpan timeout)
        => _participant.ObserveAsync(timeout);

    public void Dispose() => _participant.Dispose();
}
