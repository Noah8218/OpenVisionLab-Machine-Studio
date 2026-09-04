using OpenVisionLab.Machine.Simulation.Commands;
using OpenVisionLab.Machine.Simulation.Engine;
using OpenVisionLab.Machine.Simulation.Scenarios;

namespace OpenVisionLab.MachineStudio.ViewModel;

internal sealed record SimulationScenarioBatchRepetitionRequest(
    string ProjectId,
    string ProjectName,
    SimulationRuntimeConfiguration Runtime,
    DeterministicConditionScenarioProfile Profile,
    string ProjectPath,
    string ProjectJson,
    TimeSpan FixedStep);

/// <summary>
/// Runs one deterministic scenario-batch repetition in an isolated engine.
/// The owner guarantees that the temporary engine is stopped after replay.
/// </summary>
internal sealed class SimulationScenarioBatchRepetitionRunner
{
    internal async Task<DeterministicSimulationRunResultPackage> RunAsync(
        SimulationScenarioBatchRepetitionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Runtime);
        ArgumentNullException.ThrowIfNull(request.Profile);
        if (request.FixedStep <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Fixed step must be positive.");
        }

        using var engine = new FixedStepSimulationEngine(
            new SimulationSettings { FixedStep = request.FixedStep });
        await engine.StartAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var configured = await engine.EnqueueCommandAsync(
                new ConfigureRuntimeCommand(request.Runtime),
                cancellationToken).ConfigureAwait(false);
            if (!configured.IsAccepted)
            {
                throw new InvalidOperationException(
                    $"Batch runtime configuration was rejected: {configured.ErrorCode}: {configured.Detail}");
            }

            if (request.Runtime.AutomaticRun is not null)
            {
                var automaticStarted = await engine.EnqueueCommandAsync(
                    new StartAutomaticRunCommand(beginRealTime: false),
                    cancellationToken).ConfigureAwait(false);
                if (!automaticStarted.IsAccepted)
                {
                    throw new InvalidOperationException(
                        $"Batch automatic run was rejected: {automaticStarted.ErrorCode}: {automaticStarted.Detail}");
                }
            }
            else if (request.Profile.FaultRecovery?.RestartSequenceId is { } recoverySequenceId)
            {
                var sequenceStarted = await engine.EnqueueCommandAsync(
                    new StartSequenceCommand(recoverySequenceId),
                    cancellationToken).ConfigureAwait(false);
                if (!sequenceStarted.IsAccepted)
                {
                    throw new InvalidOperationException(
                        $"Batch recovery sequence was rejected: {sequenceStarted.ErrorCode}: {sequenceStarted.Detail}");
                }
            }

            var replay = await new DeterministicConditionScenarioRunner().ReplayAsync(
                engine,
                request.Profile,
                cancellationToken).ConfigureAwait(false);
            return DeterministicSimulationRunResultPackage.FromReplay(
                request.ProjectId,
                request.ProjectName,
                request.ProjectPath,
                request.ProjectJson,
                request.FixedStep,
                request.Profile,
                replay);
        }
        finally
        {
            await engine.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }
}
