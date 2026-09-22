using OpenVisionLab;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Simulation.Commands;
using OpenVisionLab.Machine.Simulation.Scenarios;
using OpenVisionLab.MachineStudio.ViewModel.Simulation;

namespace OpenVisionLab.MachineStudio.ViewModel;

/// <summary>
/// Owns the shell-facing Test Scenario execution state and outcome presentation.
/// Target/profile authoring remains in <see cref="SimulationWorkspaceViewModel"/>;
/// engine command orchestration remains in <see cref="SimulationScenarioWorkflow"/>.
/// </summary>
internal sealed class SimulationScenarioExecutionCoordinator : IDisposable
{
    private readonly SimulationScenarioWorkflow _workflow;
    private readonly SimulationWorkspaceViewModel _workspace;
    private readonly Func<MachineProjectDocument> _getProject;
    private readonly Func<Task<bool>> _ensureRuntimeDefinitionApplied;
    private readonly Action<bool> _setDesignMode;
    private readonly Action<bool> _setRunning;
    private readonly Action<string> _setStatus;
    private readonly Action<string, string> _log;
    private int _operationGeneration;
    private int _operationActive;
    private int _disposed;
    private bool _ownsRun;

    internal SimulationScenarioExecutionCoordinator(
        SimulationScenarioWorkflow workflow,
        SimulationWorkspaceViewModel workspace,
        Func<MachineProjectDocument> getProject,
        Func<Task<bool>> ensureRuntimeDefinitionApplied,
        Action<bool> setDesignMode,
        Action<bool> setRunning,
        Action<string> setStatus,
        Action<string, string> log)
    {
        _workflow = workflow ?? throw new ArgumentNullException(nameof(workflow));
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _getProject = getProject ?? throw new ArgumentNullException(nameof(getProject));
        _ensureRuntimeDefinitionApplied = ensureRuntimeDefinitionApplied
            ?? throw new ArgumentNullException(nameof(ensureRuntimeDefinitionApplied));
        _setDesignMode = setDesignMode ?? throw new ArgumentNullException(nameof(setDesignMode));
        _setRunning = setRunning ?? throw new ArgumentNullException(nameof(setRunning));
        _setStatus = setStatus ?? throw new ArgumentNullException(nameof(setStatus));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    internal bool OwnsRun => !IsDisposed && _ownsRun;

    internal Task StartAsync() => ExecuteExclusiveAsync(StartCoreAsync);

    private async Task StartCoreAsync()
    {
        if (IsDisposed)
        {
            return;
        }

        var profile = await PrepareProfileAsync(exitDesignMode: true);
        if (profile is null || IsDisposed)
        {
            return;
        }

        var operationGeneration = Volatile.Read(ref _operationGeneration);
        var result = await _workflow.StartAsync(
            profile,
            _getProject().Simulation.AutomaticRun is not null);
        if (IsDisposed
            || operationGeneration != Volatile.Read(ref _operationGeneration))
        {
            return;
        }

        if (!result.IsAccepted)
        {
            HandleFailure(result);
            return;
        }

        ApplySuccess(result, replay: false);
    }

    internal Task StopAsync() => ExecuteExclusiveAsync(StopCoreAsync);

    private async Task StopCoreAsync()
    {
        if (IsDisposed)
        {
            return;
        }

        var wasOwned = _ownsRun;
        var operationGeneration = Volatile.Read(ref _operationGeneration);
        var result = await _workflow.StopAsync(wasOwned);
        if (IsDisposed
            || operationGeneration != Volatile.Read(ref _operationGeneration))
        {
            return;
        }

        if (!result.IsAccepted)
        {
            var failureMessage = result.PauseResult is { } pauseResult
                ? $"Pause after stop rejected: {pauseResult.ErrorCode}: {pauseResult.Detail}"
                : $"Stop rejected · {result.StopResult.ErrorCode}: {result.StopResult.Detail}";
            _setStatus(OpenVisionLanguageService.T("Simulation.ScenarioStopRejected"));
            _log("Scenario", failureMessage);
            return;
        }

        _ownsRun = result.OwnsRunAfterOperation;
        if (wasOwned && !_ownsRun)
        {
            _setRunning(false);
        }

        _setStatus(OpenVisionLanguageService.T("Simulation.ScenarioStopped"));
        _log("Scenario", $"Test scenario stopped · {ShortCommandId(result.StopResult.CommandId)}");
    }

    internal Task ReplayAsync() => ExecuteExclusiveAsync(ReplayCoreAsync);

    private async Task ReplayCoreAsync()
    {
        if (IsDisposed)
        {
            return;
        }

        var profile = await PrepareProfileAsync(exitDesignMode: false);
        if (profile is null || IsDisposed)
        {
            return;
        }

        var operationGeneration = Volatile.Read(ref _operationGeneration);
        var result = await _workflow.ReplayAsync(
            profile,
            _getProject().Simulation.AutomaticRun is not null);
        if (IsDisposed
            || operationGeneration != Volatile.Read(ref _operationGeneration))
        {
            return;
        }

        if (!result.IsAccepted)
        {
            HandleFailure(result);
            return;
        }

        ApplySuccess(result, replay: true);
    }

    private async Task<DeterministicConditionScenarioProfile?> PrepareProfileAsync(
        bool exitDesignMode)
    {
        if (IsDisposed || !await _ensureRuntimeDefinitionApplied() || IsDisposed)
        {
            return null;
        }

        if (exitDesignMode)
        {
            _setDesignMode(false);
        }

        var targetId = _workspace.ScenarioTargetId;
        if (string.IsNullOrWhiteSpace(targetId))
        {
            _setStatus(OpenVisionLanguageService.T("Simulation.ScenarioTargetRequired"));
            return null;
        }

        return _workspace.BuildEngineProfile(targetId);
    }

    internal void InvalidatePendingExecution()
    {
        if (!IsDisposed)
        {
            Interlocked.Increment(ref _operationGeneration);
        }
    }

    private async Task ExecuteExclusiveAsync(Func<Task> operation)
    {
        if (IsDisposed)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _operationActive, 1, 0) != 0)
        {
            _log("Scenario", "Scenario operation ignored because another operation is already in progress.");
            return;
        }

        try
        {
            await operation();
        }
        finally
        {
            Volatile.Write(ref _operationActive, 0);
        }
    }

    private void ApplySuccess(SimulationScenarioResult result, bool replay)
    {
        if (IsDisposed)
        {
            return;
        }

        if (result.StartCommand is not { } startCommand)
        {
            throw new InvalidOperationException(
                "Simulation scenario workflow did not return its start command.");
        }

        _ownsRun = result.OwnsRun;
        if (result.OwnsRun)
        {
            _setRunning(true);
        }

        _setDesignMode(false);
        _setStatus(OpenVisionLanguageService.T(
            replay ? "Simulation.ScenarioReplayed" : "Simulation.ScenarioStarted"));
        var message = result.OwnsRun
            ? "Scheduled fault scenario started"
            : replay
                ? "Test scenario replayed"
                : "Test scenario started";
        _log("Scenario", $"{message} · {ShortCommandId(startCommand)}");
    }

    private void HandleFailure(SimulationScenarioResult result)
    {
        if (IsDisposed)
        {
            return;
        }

        if (result.ScheduledFaultResult is { } scheduledResult)
        {
            if (scheduledResult.FailureStage is not { } failureStage
                || scheduledResult.FailureResult is not { } failureResult)
            {
                throw new InvalidOperationException(
                    "Scheduled fault scenario returned an invalid failure result.");
            }

            _setStatus(OpenVisionLanguageService.T("Simulation.ScenarioStartRejected"));
            _log(
                "Scenario",
                $"{ScheduledFaultScenarioFailureLabel(failureStage)} rejected: " +
                $"{failureResult.ErrorCode}: {failureResult.Detail}");
            return;
        }

        if (result.FailureResult is not { } ordinaryResult)
        {
            throw new InvalidOperationException(
                "Simulation scenario returned an invalid failure result.");
        }

        var operation = result.FailureStage switch
        {
            SimulationScenarioFailureStage.Start => "Start",
            SimulationScenarioFailureStage.ReplayReset => "Replay reset",
            SimulationScenarioFailureStage.ReplayStart => "Replay start",
            _ => throw new ArgumentOutOfRangeException(nameof(result.FailureStage))
        };
        if (result.FailureStage == SimulationScenarioFailureStage.Start)
        {
            _setStatus(OpenVisionLanguageService.T("Simulation.ScenarioStartRejected"));
        }

        _log("Scenario", $"{operation} rejected · {ordinaryResult.ErrorCode}: {ordinaryResult.Detail}");
    }

    private static string ScheduledFaultScenarioFailureLabel(
        ScheduledFaultScenarioFailureStage stage) => stage switch
    {
        ScheduledFaultScenarioFailureStage.Reset => "Reset",
        ScheduledFaultScenarioFailureStage.StartConditionScenario => "Start",
        ScheduledFaultScenarioFailureStage.StartAutomaticRun => "Automatic run",
        ScheduledFaultScenarioFailureStage.StartRecoverySequence => "Recovery sequence",
        ScheduledFaultScenarioFailureStage.Play => "Play",
        _ => throw new ArgumentOutOfRangeException(nameof(stage), stage, null)
    };

    private static string ShortCommandId(string commandId) =>
        $"CMD-{commandId[..8].ToUpperInvariant()}";

    private static string ShortCommandId(SimulationCommand command) =>
        ShortCommandId(command.CommandId);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Interlocked.Increment(ref _operationGeneration);
        _ownsRun = false;
    }

    private bool IsDisposed => Volatile.Read(ref _disposed) != 0;
}
