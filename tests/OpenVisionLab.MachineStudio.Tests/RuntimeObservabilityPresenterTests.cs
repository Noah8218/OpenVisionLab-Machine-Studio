using OpenVisionLab;
using OpenVisionLab.Machine.Simulation.Commands;
using OpenVisionLab.Machine.Simulation.Engine;
using OpenVisionLab.Machine.Simulation.Events;
using OpenVisionLab.Machine.Simulation.Snapshots;
using OpenVisionLab.MachineStudio.Models.Simulation;
using OpenVisionLab.MachineStudio.ViewModel;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

public sealed class RuntimeObservabilityPresenterTests
{
    [Fact]
    public void RecordRuntimeEvent_PresentsToDebuggerAndStructuredJournal()
    {
        OpenVisionLanguageService.Load();
        var evidence = CreateEvidenceViewModel();
        var debugger = CreateDebuggerViewModel();
        var presenter = new RuntimeObservabilityPresenter(4, 4, evidence, debugger);
        var runtimeEvent = new SimulationEvent(
            EventIndex: 7,
            TickIndex: 9,
            SimulationTime: TimeSpan.FromMilliseconds(45),
            Category: "Warning",
            Code: "AxisStalled",
            Message: "Axis stalled",
            CommandId: "command-7");

        presenter.RecordRuntimeEvent(runtimeEvent, CreateSnapshot());

        var timelineItem = Assert.Single(debugger.Timeline);
        Assert.Equal(runtimeEvent.Code, timelineItem.Code);
        Assert.Contains(
            presenter.LogMessages,
            line => line.Contains(runtimeEvent.Message, StringComparison.Ordinal));
        var diagnostic = Assert.Single(presenter.OperationalDiagnostics);
        Assert.Equal(SimulationOperationalDiagnosticKind.RuntimeEvent, diagnostic.Kind);
        Assert.Equal(SimulationLogSeverity.Warning, diagnostic.Severity);
        Assert.Equal(runtimeEvent.Code, diagnostic.EventName);
        Assert.Equal(runtimeEvent.CommandId, diagnostic.CommandId);
        Assert.Equal(runtimeEvent.TickIndex, diagnostic.TickIndex);
    }

    [Fact]
    public void RecordEngineTermination_PreservesFailureContext()
    {
        OpenVisionLanguageService.Load();
        var presenter = CreatePresenter();
        var exception = new InvalidOperationException("engine failure");
        var termination = new SimulationEngineTerminationResult(
            SimulationEngineTerminationOutcome.Faulted,
            TickIndex: 42,
            SimulationTime: TimeSpan.FromMilliseconds(210),
            Exception: exception,
            CurrentCommandId: "command-42",
            Operation: "StopAsync");

        presenter.RecordEngineTermination(termination);

        var diagnostic = Assert.Single(presenter.OperationalDiagnostics);
        Assert.Equal(SimulationOperationalDiagnosticKind.EngineTermination, diagnostic.Kind);
        Assert.Equal(SimulationLogSeverity.Alarm, diagnostic.Severity);
        Assert.Equal(termination.TickIndex, diagnostic.TickIndex);
        Assert.Equal(termination.SimulationTime, diagnostic.SimulationTime);
        Assert.Equal(termination.CurrentCommandId, diagnostic.CommandId);
        Assert.Equal(termination.Operation, diagnostic.Operation);
        Assert.Equal(termination.Outcome, diagnostic.TerminationOutcome);
        Assert.Equal(exception.GetType().FullName, diagnostic.ExceptionType);
        Assert.Equal(exception.Message, diagnostic.ExceptionMessage);
    }

    [Fact]
    public void RecordShutdownDiagnostic_UsesTerminationCoordinatesWhenAvailable()
    {
        OpenVisionLanguageService.Load();
        var presenter = CreatePresenter();
        var termination = new SimulationEngineTerminationResult(
            SimulationEngineTerminationOutcome.Stopped,
            TickIndex: 42,
            SimulationTime: TimeSpan.FromMilliseconds(210),
            CurrentCommandId: "command-42",
            Operation: "StopAsync");

        presenter.RecordShutdownDiagnostic(
            SimulationOperationalDiagnosticKind.ShutdownCompleted,
            SimulationLogSeverity.Info,
            "shutdown completed",
            "ResourceDispose",
            currentTickIndex: 99,
            currentSimulationTime: TimeSpan.FromSeconds(9),
            termination);

        var diagnostic = Assert.Single(presenter.OperationalDiagnostics);
        Assert.Equal(SimulationOperationalDiagnosticKind.ShutdownCompleted, diagnostic.Kind);
        Assert.Equal("MachineStudio", diagnostic.Component);
        Assert.Equal("Lifecycle", diagnostic.Category);
        Assert.Equal("ResourceDispose", diagnostic.ShutdownStage);
        Assert.Equal(termination.TickIndex, diagnostic.TickIndex);
        Assert.Equal(termination.SimulationTime, diagnostic.SimulationTime);
        Assert.Equal(termination.Outcome, diagnostic.TerminationOutcome);
    }

    private static RuntimeObservabilityPresenter CreatePresenter() =>
        new(4, 4, CreateEvidenceViewModel(), CreateDebuggerViewModel());

    private static VisionExecutionEvidenceViewModel CreateEvidenceViewModel() =>
        new(
            () => new VisionEvidenceContext(
                "project",
                "{}",
                "build",
                null,
                null,
                null),
            _ => { },
            _ => { });

    private static RuntimeDebuggerViewModel CreateDebuggerViewModel() =>
        new(_ => Task.FromResult(
            new SimulationCommandResult(
                "test",
                true,
                0,
                TimeSpan.Zero,
                SimulationCommandErrorCode.None,
                null)));

    private static SimulationSnapshot CreateSnapshot() =>
        new(
            TimeSpan.Zero,
            0,
            SimulationRunMode.Paused,
            SimulationControlOwner.Definition,
            1,
            [],
            0,
            [],
            []);
}
