using OpenVisionLab.Machine.Simulation.Engine;
using OpenVisionLab.MachineStudio.ViewModel.Simulation;

namespace OpenVisionLab.MachineStudio.ViewModel;

/// <summary>
/// Owns the concrete resources that share the Machine Studio runtime lifetime.
/// Shutdown ordering and diagnostics remain with MainViewModel.
/// </summary>
internal sealed class SimulationRuntimeResourceOwner
{
    private readonly object _gate = new();
    private readonly ISimulationEngine _engine;
    private readonly SimulationRuntimeLoop _runtimeLoop;
    private readonly SimulationWorkspaceViewModel _workspace;
    private readonly SimulationScenarioBatchViewModel? _scenarioBatch;
    private readonly MultiAxisCommissioningViewModel? _multiAxisCommissioning;
    private bool _disposed;

    internal SimulationRuntimeResourceOwner(
        ISimulationEngine engine,
        SimulationRuntimeLoop runtimeLoop,
        SimulationWorkspaceViewModel workspace,
        SimulationScenarioBatchViewModel? scenarioBatch = null,
        MultiAxisCommissioningViewModel? multiAxisCommissioning = null)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _runtimeLoop = runtimeLoop ?? throw new ArgumentNullException(nameof(runtimeLoop));
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _scenarioBatch = scenarioBatch;
        _multiAxisCommissioning = multiAxisCommissioning;
    }

    internal bool IsDisposed
    {
        get
        {
            lock (_gate)
            {
                return _disposed;
            }
        }
    }

    internal Task? ScenarioBatchTask => _scenarioBatch?.BatchTask;

    internal Task? CommissioningValidationTask => _multiAxisCommissioning?.ValidationTask;

    internal void RequestCancellation()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _runtimeLoop.Cancel();
            _scenarioBatch?.CancelBatch();
            _multiAxisCommissioning?.CancelValidation();
        }
    }

    internal bool TryDisposeIfSafe()
    {
        lock (_gate)
        {
            if (_disposed || !CanDisposeResources())
            {
                return false;
            }

            _disposed = true;
            _engine.Dispose();
            _workspace.Dispose();
            _runtimeLoop.Dispose();
            _multiAxisCommissioning?.Dispose();
            _scenarioBatch?.Dispose();
            return true;
        }
    }

    private bool CanDisposeResources() =>
        _engine.Termination.IsCompleted
        && _runtimeLoop.IsCompleted
        && (_scenarioBatch?.BatchTask is not { IsCompleted: false })
        && (_multiAxisCommissioning?.ValidationTask is not { IsCompleted: false });
}
