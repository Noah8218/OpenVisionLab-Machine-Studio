using System.Collections.ObjectModel;
using OpenVisionLab.Machine.Simulation.Engine;
using OpenVisionLab.Machine.Simulation.Events;
using OpenVisionLab.Machine.Simulation.Snapshots;
using OpenVisionLab.MachineStudio.Models.Simulation;

namespace OpenVisionLab.MachineStudio.ViewModel;

/// <summary>
/// Maps runtime lifecycle and event data to the existing observability owners.
/// It deliberately owns presentation policy only; runtime state and shutdown
/// sequencing remain with the application shell.
/// </summary>
internal sealed class RuntimeObservabilityPresenter
{
    private readonly RuntimeObservabilityJournal _journal;
    private readonly VisionExecutionEvidenceViewModel _visionExecutionEvidence;
    private readonly RuntimeDebuggerViewModel _runtimeDebugger;

    internal RuntimeObservabilityPresenter(
        RuntimeObservabilityJournal journal,
        VisionExecutionEvidenceViewModel visionExecutionEvidence,
        RuntimeDebuggerViewModel runtimeDebugger)
    {
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        _visionExecutionEvidence = visionExecutionEvidence
            ?? throw new ArgumentNullException(nameof(visionExecutionEvidence));
        _runtimeDebugger = runtimeDebugger
            ?? throw new ArgumentNullException(nameof(runtimeDebugger));
    }

    internal ReadOnlyObservableCollection<string> LogMessages => _journal.LogMessages;

    internal IReadOnlyList<SimulationOperationalDiagnostic> OperationalDiagnostics =>
        _journal.OperationalDiagnostics;

    internal void RecordRuntimeEvent(
        SimulationEvent runtimeEvent,
        SimulationSnapshot currentSnapshot)
    {
        ArgumentNullException.ThrowIfNull(runtimeEvent);
        ArgumentNullException.ThrowIfNull(currentSnapshot);

        RecordCanonicalRuntimeEvent(runtimeEvent, currentSnapshot);
        RecordRuntimeEventPresentation(runtimeEvent);
    }

    internal void RecordCanonicalRuntimeEvent(
        SimulationEvent runtimeEvent,
        SimulationSnapshot currentSnapshot)
    {
        ArgumentNullException.ThrowIfNull(runtimeEvent);
        ArgumentNullException.ThrowIfNull(currentSnapshot);
        _visionExecutionEvidence.RecordEvent(runtimeEvent, currentSnapshot);
    }

    internal void RecordRuntimeEventPresentation(SimulationEvent runtimeEvent)
    {
        ArgumentNullException.ThrowIfNull(runtimeEvent);
        _runtimeDebugger.ApplyEvent(runtimeEvent);
        _journal.Append(
            runtimeEvent.SimulationTime,
            runtimeEvent.Category,
            runtimeEvent.Message,
            runtimeEvent.TickIndex,
            new SimulationOperationalDiagnostic(
                DateTimeOffset.UtcNow,
                SimulationOperationalDiagnosticKind.RuntimeEvent,
                RuntimeObservabilityJournal.SeverityForCategory(runtimeEvent.Category),
                "SimulationEngine",
                runtimeEvent.Code,
                runtimeEvent.Message,
                runtimeEvent.TickIndex,
                runtimeEvent.SimulationTime,
                runtimeEvent.Category,
                runtimeEvent.CommandId));
    }

    internal void RecordCanonicalEventJournalCompleted(
        SimulationEventJournalSnapshot journal)
    {
        ArgumentNullException.ThrowIfNull(journal);
        var message = journal.IsComplete
            ? $"Canonical runtime event journal completed: {journal.TotalEventCount} events " +
                $"({journal.FirstEventIndex}-{journal.LastEventIndex})."
            : $"Canonical runtime event journal incomplete: budget {journal.Capacity}, " +
                $"stored {journal.StoredEventCount} of {journal.TotalEventCount}; " +
                $"first missing index {journal.FirstMissingEventIndex}.";
        _journal.Record(
            new SimulationOperationalDiagnostic(
                DateTimeOffset.UtcNow,
                SimulationOperationalDiagnosticKind.RuntimeEvent,
                journal.IsComplete ? SimulationLogSeverity.Info : SimulationLogSeverity.Alarm,
                "SimulationEngine",
                "CanonicalEventJournalCompleted",
                message,
                0,
                TimeSpan.Zero,
                "Lifecycle"),
            writeToLogger: !journal.IsComplete);
    }

    internal void RecordEngineTermination(SimulationEngineTerminationResult termination)
    {
        ArgumentNullException.ThrowIfNull(termination);
        var exception = termination.Exception;
        var message = exception is null
            ? $"Simulation engine terminated with outcome {termination.Outcome}."
            : $"Simulation engine faulted with {exception.GetType().Name}: {exception.Message}";
        _journal.Record(
            new SimulationOperationalDiagnostic(
                DateTimeOffset.UtcNow,
                SimulationOperationalDiagnosticKind.EngineTermination,
                termination.IsFaulted
                    ? SimulationLogSeverity.Alarm
                    : SimulationLogSeverity.Info,
                "SimulationEngine",
                "EngineTerminated",
                message,
                termination.TickIndex,
                termination.SimulationTime,
                "Lifecycle",
                termination.CurrentCommandId,
                termination.Operation,
                termination.Outcome,
                ExceptionType: exception?.GetType().FullName,
                ExceptionMessage: exception?.Message),
            writeToLogger: true);
    }

    internal void RecordShutdownDiagnostic(
        SimulationRuntimeShutdownDiagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);
        var coordinates = _journal.CurrentCoordinates;
        RecordShutdownDiagnostic(
            diagnostic.Kind,
            diagnostic.Severity,
            diagnostic.Message,
            diagnostic.Stage,
            coordinates.TickIndex,
            coordinates.SimulationTime,
            diagnostic.Termination,
            diagnostic.Exception);
    }

    internal void RecordShutdownDiagnostic(
        SimulationOperationalDiagnosticKind kind,
        SimulationLogSeverity severity,
        string message,
        string stage,
        long currentTickIndex,
        TimeSpan currentSimulationTime,
        SimulationEngineTerminationResult? termination = null,
        Exception? exception = null)
    {
        _journal.Record(
            new SimulationOperationalDiagnostic(
                DateTimeOffset.UtcNow,
                kind,
                severity,
                "MachineStudio",
                kind.ToString(),
                message,
                termination?.TickIndex ?? currentTickIndex,
                termination?.SimulationTime ?? currentSimulationTime,
                "Lifecycle",
                termination?.CurrentCommandId,
                termination?.Operation,
                termination?.Outcome,
                ExceptionType: exception?.GetType().FullName,
                ExceptionMessage: exception?.Message,
                ShutdownStage: stage),
            writeToLogger: true);
    }
}
