using OpenVisionLab.Machine.Simulation.Commands;
using OpenVisionLab.Machine.Simulation.Engine;

namespace OpenVisionLab.MachineStudio.Models.Simulation;

public enum SimulationOperationalDiagnosticKind
{
    RuntimeMessage,
    RuntimeEvent,
    EngineTermination,
    ShutdownRequested,
    ShutdownCompleted,
    ShutdownFaulted,
    ShutdownTimedOut
}

public sealed record SimulationOperationalDiagnostic(
    DateTimeOffset TimestampUtc,
    SimulationOperationalDiagnosticKind Kind,
    SimulationLogSeverity Severity,
    string Component,
    string EventName,
    string Message,
    long TickIndex,
    TimeSpan SimulationTime,
    string? Category = null,
    string? CommandId = null,
    string? Operation = null,
    SimulationEngineTerminationOutcome? TerminationOutcome = null,
    SimulationCommandErrorCode? CommandErrorCode = null,
    string? ExceptionType = null,
    string? ExceptionMessage = null,
    string? ShutdownStage = null)
{
    public string ToDiagnosticLine()
    {
        var exception = ExceptionType is null
            ? string.Empty
            : $" exception={ExceptionType}:{ExceptionMessage}";
        var command = CommandId is null ? string.Empty : $" command={CommandId}";
        var operation = Operation is null ? string.Empty : $" operation={Operation}";
        var outcome = TerminationOutcome is null ? string.Empty : $" outcome={TerminationOutcome}";
        var stage = ShutdownStage is null ? string.Empty : $" shutdownStage={ShutdownStage}";
        return $"diagnostic kind={Kind} severity={Severity} component={Component} " +
            $"event={EventName} tick={TickIndex} simulationTime={SimulationTime}{outcome}" +
            $"{command}{operation}{stage}{exception} message={Message}";
    }
}
