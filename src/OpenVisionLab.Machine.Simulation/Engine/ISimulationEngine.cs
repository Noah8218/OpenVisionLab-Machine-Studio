using System.Threading.Channels;
using OpenVisionLab.Machine.Simulation.Axis;
using OpenVisionLab.Machine.Simulation.Commands;
using OpenVisionLab.Machine.Simulation.Events;
using OpenVisionLab.Machine.Simulation.Snapshots;

namespace OpenVisionLab.Machine.Simulation.Engine;

public interface ISimulationEngine : IDisposable
{
    SimulationSnapshot CurrentSnapshot { get; }
    ChannelReader<SimulationSnapshot> SnapshotReader { get; }
    ChannelReader<SimulationEvent> EventReader { get; }
    Task StartAsync(CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
    Task<SimulationCommandResult> EnqueueCommandAsync(
        SimulationCommand command,
        CancellationToken cancellationToken = default);
    void AddAxis(ServoAxisComponent axis);
}
