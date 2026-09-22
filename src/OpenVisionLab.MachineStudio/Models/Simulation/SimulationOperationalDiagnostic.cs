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
    ShutdownTimedOut,
    ShutdownIncomplete
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
    string? ShutdownStage = null,
    string? ProjectId = null,
    string? SessionId = null,
    long? RuntimeGeneration = null,
    string? ExceptionDetail = null)
{
    public string ToDiagnosticLine()
    {
        var exception = ExceptionType is null
            ? string.Empty
            : $" exception={ExceptionType}:{ExceptionMessage}";
        var exceptionDetail = ExceptionDetail is null
            ? string.Empty
            : $" exceptionDetail={EscapeLine(ExceptionDetail)}";
        var command = CommandId is null ? string.Empty : $" command={CommandId}";
        var operation = Operation is null ? string.Empty : $" operation={Operation}";
        var outcome = TerminationOutcome is null ? string.Empty : $" outcome={TerminationOutcome}";
        var stage = ShutdownStage is null ? string.Empty : $" shutdownStage={ShutdownStage}";
        var project = ProjectId is null ? string.Empty : $" projectId={ProjectId}";
        var session = SessionId is null ? string.Empty : $" sessionId={SessionId}";
        var generation = RuntimeGeneration is null ? string.Empty : $" runtimeGeneration={RuntimeGeneration}";
        return $"diagnostic kind={Kind} severity={Severity} component={Component} " +
            $"event={EventName} tick={TickIndex} simulationTime={SimulationTime}" +
            $"{project}{session}{generation}{outcome}{command}{operation}{stage}{exception}{exceptionDetail} message={Message}";
    }

    private static string EscapeLine(string value) =>
        value.Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);
}
