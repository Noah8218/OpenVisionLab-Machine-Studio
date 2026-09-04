using OpenVisionLab;
using OpenVisionLab.Logging;
using OpenVisionLab.MachineStudio.Models.Simulation;
using OpenVisionLab.MachineStudio.ViewModel;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

public sealed class RuntimeObservabilityJournalTests
{
    [Fact]
    public void Append_BoundsBothViewsAndPreservesExistingFormattingContract()
    {
        OpenVisionLanguageService.Load();
        var logger = new CapturingLogger();
        var journal = new RuntimeObservabilityJournal(
            logMessageRetentionLimit: 2,
            diagnosticRetentionLimit: 2,
            logger);

        journal.Append(TimeSpan.FromMilliseconds(1), "Runtime", "first", tickIndex: 1);
        journal.Append(TimeSpan.FromMilliseconds(2), "Warning", "second", tickIndex: 2);
        journal.Append(TimeSpan.FromMilliseconds(3), "Recovery", "third", tickIndex: 3);

        Assert.Equal(2, journal.LogMessages.Count);
        Assert.DoesNotContain(journal.LogMessages, line => line.Contains("first", StringComparison.Ordinal));
        Assert.Contains(journal.LogMessages, line => line.Contains("third", StringComparison.Ordinal));

        var diagnostics = journal.OperationalDiagnostics;
        Assert.Equal(2, diagnostics.Count);
        Assert.Equal("second", diagnostics[0].Message);
        Assert.Equal("third", diagnostics[1].Message);
        Assert.Equal(SimulationLogSeverity.Recovery, diagnostics[1].Severity);
        Assert.Equal(3, logger.Messages.Count);
        Assert.Contains("third", logger.Messages[2], StringComparison.Ordinal);
    }

    [Fact]
    public void Record_WithLoggerProjectionRetainsStructuredDiagnosticLine()
    {
        var logger = new CapturingLogger();
        var journal = new RuntimeObservabilityJournal(4, 4, logger);
        var diagnostic = new SimulationOperationalDiagnostic(
            DateTimeOffset.UtcNow,
            SimulationOperationalDiagnosticKind.ShutdownFaulted,
            SimulationLogSeverity.Alarm,
            "MachineStudio",
            "ShutdownFaulted",
            "shutdown failed",
            42,
            TimeSpan.FromMilliseconds(210),
            "Lifecycle",
            Operation: "StopAsync",
            ExceptionType: "System.InvalidOperationException",
            ExceptionMessage: "test failure",
            ShutdownStage: "EngineStop");

        journal.Record(diagnostic, writeToLogger: true);

        Assert.Single(journal.OperationalDiagnostics);
        Assert.Same(diagnostic, journal.OperationalDiagnostics[0]);
        Assert.Single(logger.Messages);
        Assert.Contains("kind=ShutdownFaulted", logger.Messages[0], StringComparison.Ordinal);
        Assert.Contains("shutdownStage=EngineStop", logger.Messages[0], StringComparison.Ordinal);
    }

    private sealed class CapturingLogger : ILogger
    {
        public List<string> Messages { get; } = [];

        public void Log(string message) => Messages.Add(message);
    }
}
