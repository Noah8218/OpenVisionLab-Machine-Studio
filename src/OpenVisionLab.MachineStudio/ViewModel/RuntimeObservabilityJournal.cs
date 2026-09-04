using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using OpenVisionLab;
using OpenVisionLab.Logging;
using OpenVisionLab.MachineStudio.Models.Simulation;

namespace OpenVisionLab.MachineStudio.ViewModel;

internal sealed class RuntimeObservabilityJournal
{
    private readonly int _logMessageRetentionLimit;
    private readonly int _diagnosticRetentionLimit;
    private readonly ILogger _logger;
    private readonly ObservableCollection<string> _logMessages = new();
    private readonly ConcurrentQueue<SimulationOperationalDiagnostic> _operationalDiagnostics = new();

    internal RuntimeObservabilityJournal(
        int logMessageRetentionLimit,
        int diagnosticRetentionLimit,
        ILogger? logger = null)
    {
        if (logMessageRetentionLimit <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(logMessageRetentionLimit));
        }

        if (diagnosticRetentionLimit <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(diagnosticRetentionLimit));
        }

        _logMessageRetentionLimit = logMessageRetentionLimit;
        _diagnosticRetentionLimit = diagnosticRetentionLimit;
        _logger = logger ?? new ConsoleLogger();
        LogMessages = new ReadOnlyObservableCollection<string>(_logMessages);
    }

    internal ReadOnlyObservableCollection<string> LogMessages { get; }

    internal IReadOnlyList<SimulationOperationalDiagnostic> OperationalDiagnostics =>
        _operationalDiagnostics.ToArray();

    internal void Append(
        TimeSpan time,
        string category,
        string message,
        long tickIndex,
        SimulationOperationalDiagnostic? diagnostic = null)
    {
        Record(
            diagnostic ?? new SimulationOperationalDiagnostic(
                DateTimeOffset.UtcNow,
                SimulationOperationalDiagnosticKind.RuntimeMessage,
                SeverityForCategory(category),
                "MachineStudio",
                category,
                message,
                tickIndex,
                time,
                category));

        var localizedCategory = OpenVisionLanguageService.T(
            $"Runtime.Category.{category}",
            category,
            category);
        var line = $"[{time:hh\\:mm\\:ss\\.fff}] {localizedCategory} · {SimulationLogEntry.LocalizeMessage(message)}";
        _logMessages.Add(line);
        while (_logMessages.Count > _logMessageRetentionLimit)
        {
            _logMessages.RemoveAt(0);
        }

        _logger.Log(line);
    }

    internal void Record(
        SimulationOperationalDiagnostic diagnostic,
        bool writeToLogger = false)
    {
        _operationalDiagnostics.Enqueue(diagnostic);
        while (_operationalDiagnostics.Count > _diagnosticRetentionLimit)
        {
            _operationalDiagnostics.TryDequeue(out _);
        }

        if (writeToLogger)
        {
            _logger.Log(diagnostic.ToDiagnosticLine());
        }
    }

    internal static SimulationLogSeverity SeverityForCategory(string category) =>
        category switch
        {
            "Error" or "Fault" => SimulationLogSeverity.Alarm,
            "Warning" => SimulationLogSeverity.Warning,
            "Recovery" => SimulationLogSeverity.Recovery,
            _ => SimulationLogSeverity.Info
        };
}
