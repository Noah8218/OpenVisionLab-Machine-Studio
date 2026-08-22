using System.Text.Json;
using System.Text.Json.Serialization;
using System.IO;
using System.Linq;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Simulation.Analysis;
using OpenVisionLab.Machine.Simulation.Commands;
using OpenVisionLab.Machine.Simulation.Compilation;
using OpenVisionLab.Machine.Simulation.Engine;

namespace OpenVisionLab.Machine.Simulation.FaultScenarios;

public sealed record DeterministicFaultScenarioHeadlessRunReport(
    string ProjectPath,
    string ScenarioPath,
    bool IsSuccess,
    DateTimeOffset CompletedAtUtc,
    IReadOnlyList<string> CompilationErrors,
    string? FailureReason,
    DeterministicFaultScenarioReplayResult? ReplayResult,
    IReadOnlyList<SignalTimeline>? SignalTimelines)
{
    public static DeterministicFaultScenarioHeadlessRunReport Failure(
        string projectPath,
        string scenarioPath,
        string failureReason,
        IReadOnlyList<string>? compilationErrors = null) =>
        new(
            projectPath,
            scenarioPath,
            false,
            DateTimeOffset.UtcNow,
            compilationErrors ?? Array.Empty<string>(),
            failureReason,
            null,
            Array.Empty<SignalTimeline>());

    public static DeterministicFaultScenarioHeadlessRunReport Success(
        string projectPath,
        string scenarioPath,
        DeterministicFaultScenarioReplayResult replayResult,
        IReadOnlyList<string>? compilationErrors = null) =>
        new(
            projectPath,
            scenarioPath,
            replayResult.IsSuccess,
            DateTimeOffset.UtcNow,
            compilationErrors ?? Array.Empty<string>(),
            replayResult.FailureReason,
            replayResult,
            AnalyzeTimelines(replayResult));

    private static IReadOnlyList<SignalTimeline> AnalyzeTimelines(
        DeterministicFaultScenarioReplayResult replayResult) =>
        replayResult is null
            ? Array.Empty<SignalTimeline>()
            : SimulationSignalTimelineAnalyzer.AnalyzeSignals(replayResult.SnapshotHistory);

    public static readonly JsonSerializerOptions ReportJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters =
        {
            new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)
        }
    };
}

public sealed class DeterministicFaultScenarioHeadlessRunner
{
    private readonly DeterministicFaultScenarioRunner _scenarioRunner = new();

    public async Task<DeterministicFaultScenarioHeadlessRunReport> RunAsync(
        string projectPath,
        string scenarioPath,
        string? reportPath = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(projectPath))
        {
            return DeterministicFaultScenarioHeadlessRunReport.Failure(
                projectPath,
                scenarioPath,
                "Project path is required.");
        }

        if (string.IsNullOrWhiteSpace(scenarioPath))
        {
            return DeterministicFaultScenarioHeadlessRunReport.Failure(
                projectPath,
                scenarioPath,
                "Scenario path is required.");
        }

        if (!File.Exists(projectPath))
        {
            return DeterministicFaultScenarioHeadlessRunReport.Failure(
                projectPath,
                scenarioPath,
                $"Project file not found: {projectPath}");
        }

        if (!File.Exists(scenarioPath))
        {
            return DeterministicFaultScenarioHeadlessRunReport.Failure(
                projectPath,
                scenarioPath,
                $"Scenario file not found: {scenarioPath}");
        }

        var scenario = DeterministicFaultScenarioProfile.LoadFromJson(scenarioPath);
        if (scenario is null)
        {
            return DeterministicFaultScenarioHeadlessRunReport.Failure(
                projectPath,
                scenarioPath,
                "Failed to load scenario JSON.");
        }

        var compilationLoadFailure = string.Empty;
        var compilation = CompileRuntime(projectPath, ref compilationLoadFailure);
        if (compilation is null)
        {
            return DeterministicFaultScenarioHeadlessRunReport.Failure(
                projectPath,
                scenarioPath,
                $"Project compile failed: {compilationLoadFailure}");
        }

        if (!compilation.Value.compilationResult.IsSuccess)
        {
            return DeterministicFaultScenarioHeadlessRunReport.Failure(
                projectPath,
                scenarioPath,
                "Project runtime compilation failed.",
                RuntimeCompilationErrors(compilation.Value.compilationResult));
        }

        DeterministicFaultScenarioHeadlessRunReport? result = null;
        try
        {
            result = await RunReplayAsync(
                projectPath,
                scenarioPath,
                scenario,
                compilation.Value.compilationResult,
                compilation.Value.fixedStep,
                cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            return DeterministicFaultScenarioHeadlessRunReport.Failure(
                projectPath,
                scenarioPath,
                $"Replay execution failed: {exception.Message}");
        }

        if (result is null)
        {
            return DeterministicFaultScenarioHeadlessRunReport.Failure(
                projectPath,
                scenarioPath,
                "Replay execution returned no result.");
        }

        if (reportPath is not null)
        {
            var writeResult = await TryWriteReportAsync(result, reportPath, cancellationToken)
                .ConfigureAwait(false);
            if (writeResult is not null)
            {
                return writeResult;
            }
        }

        return result;
    }

    public async Task WriteReportAsync(
        DeterministicFaultScenarioHeadlessRunReport report,
        string reportPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (string.IsNullOrWhiteSpace(reportPath))
        {
            throw new ArgumentException("Report path is required.", nameof(reportPath));
        }

        var fullPath = Path.GetFullPath(reportPath);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var payload = JsonSerializer.Serialize(report, DeterministicFaultScenarioHeadlessRunReport.ReportJsonOptions);
        await File.WriteAllTextAsync(fullPath, payload, cancellationToken).ConfigureAwait(false);
    }

    private static string[] RuntimeCompilationErrors(
        MachineProjectRuntimeCompilationResult compilationResult)
    {
        return compilationResult.Errors.Select(error => $"{error.Code} [{error.TargetId}]: {error.Message}").ToArray();
    }

    private static IReadOnlyList<SignalTimeline> AnalyzeTimelines(
        DeterministicFaultScenarioReplayResult replayResult) =>
        replayResult is null
            ? Array.Empty<SignalTimeline>()
            : SimulationSignalTimelineAnalyzer.AnalyzeSignals(replayResult.SnapshotHistory);

    private async Task<DeterministicFaultScenarioHeadlessRunReport> RunReplayAsync(
        string projectPath,
        string scenarioPath,
        DeterministicFaultScenarioProfile scenario,
        MachineProjectRuntimeCompilationResult compilation,
        TimeSpan fixedStep,
        CancellationToken cancellationToken)
    {
        using var engine = new FixedStepSimulationEngine(new SimulationSettings { FixedStep = fixedStep });
        try
        {
            await engine.StartAsync(cancellationToken).ConfigureAwait(false);
            var configure = await engine.EnqueueCommandAsync(
                    new ConfigureRuntimeCommand(compilation.Configuration!),
                    cancellationToken)
                .ConfigureAwait(false);

            if (!configure.IsAccepted)
            {
                return DeterministicFaultScenarioHeadlessRunReport.Failure(
                    projectPath,
                    scenarioPath,
                    $"Runtime configuration command rejected: {configure.Detail}");
            }

            var replayResult = await _scenarioRunner.ReplayAsync(engine, scenario, cancellationToken)
                .ConfigureAwait(false);

            return DeterministicFaultScenarioHeadlessRunReport.Success(
                projectPath,
                scenarioPath,
                replayResult);
        }
        finally
        {
            await engine.StopAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static (MachineProjectRuntimeCompilationResult compilationResult, TimeSpan fixedStep)?
        CompileRuntime(string projectPath, ref string error)
    {
        try
        {
            var projectJson = File.ReadAllText(projectPath);
            var project = new ProjectDocumentStore().Load(projectJson);
            var fixedStepMilliseconds = project.Simulation?.FixedStepMilliseconds ?? 5;
            if (fixedStepMilliseconds <= 0)
            {
                error = "Project simulation fixed step must be a positive millisecond value.";
                return null;
            }

            var fixedStep = TimeSpan.FromMilliseconds(fixedStepMilliseconds);
            var result = new MachineProjectRuntimeCompiler(fixedStep).Compile(project);
            return (result, fixedStep);
        }
        catch (Exception exception)
        {
            error = exception.Message;
            return null;
        }
    }

    private async Task<DeterministicFaultScenarioHeadlessRunReport?> TryWriteReportAsync(
        DeterministicFaultScenarioHeadlessRunReport result,
        string reportPath,
        CancellationToken cancellationToken)
    {
        try
        {
            await WriteReportAsync(result, reportPath, cancellationToken).ConfigureAwait(false);
            return null;
        }
        catch (Exception exception)
        {
            return DeterministicFaultScenarioHeadlessRunReport.Failure(
                result.ProjectPath,
                result.ScenarioPath,
                $"Failed to write report: {exception.Message}");
        }
    }
}
