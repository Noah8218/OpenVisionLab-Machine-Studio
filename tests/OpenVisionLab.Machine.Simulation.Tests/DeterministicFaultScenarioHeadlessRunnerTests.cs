using System.Text.Json;
using System.Linq;
using OpenVisionLab.Machine.Simulation.FaultScenarios;
using Xunit;

namespace OpenVisionLab.Machine.Simulation.Tests;

public sealed class DeterministicFaultScenarioHeadlessRunnerTests
{
    private static readonly string ProjectPath = Path.Combine(
        AppContext.BaseDirectory,
        "AutomaticTransferCell.ovmachine");

    [Fact]
    public async Task RunAsync_WithValidProjectAndScenario_WritesReport()
    {
        var outputRoot = PrepareTestDataDirectory();
        var scenarioPath = Path.Combine(outputRoot, $"valid-scenario-{Guid.NewGuid():N}.json");
        var reportPath = Path.Combine(outputRoot, $"valid-report-{Guid.NewGuid():N}.json");
        var scenario = new DeterministicFaultScenarioProfile(
            SchemaVersion: 1,
            ScenarioId: "headless-fixture",
            Name: "Headless fixture",
            Description: "Integration fixture for headless runner smoke validation.",
            DurationTicks: 12,
            Actions:
            [
                new(
                    Tick: 1,
                    Action: DeterministicFaultScenarioActionKind.InjectFault,
                    FaultKind: DeterministicFaultScenarioFaultKind.StuckDigitalInput,
                    TargetId: "di.station-present",
                    ForcedValue: false),
                new(
                    Tick: 3,
                    Action: DeterministicFaultScenarioActionKind.ClearFault,
                    FaultKind: DeterministicFaultScenarioFaultKind.StuckDigitalInput,
                    TargetId: "di.station-present"),
                new(
                    Tick: 5,
                    Action: DeterministicFaultScenarioActionKind.InjectFault,
                    FaultKind: DeterministicFaultScenarioFaultKind.CylinderTravelBlocked,
                    TargetId: "cylinder-1"),
                new(
                    Tick: 7,
                    Action: DeterministicFaultScenarioActionKind.ClearFault,
                    FaultKind: DeterministicFaultScenarioFaultKind.CylinderTravelBlocked,
                    TargetId: "cylinder-1")
            ]);

        await File.WriteAllTextAsync(scenarioPath, DeterministicFaultScenarioProfile.SaveToJson(scenario));
        try
        {
            var runner = new DeterministicFaultScenarioHeadlessRunner();
            var result = await runner.RunAsync(ProjectPath, scenarioPath, reportPath);

            Assert.True(result.IsSuccess);
            Assert.NotNull(result.ReplayResult);
            Assert.True(result.ReplayResult!.IsSuccess, result.ReplayResult.FailureReason);
            Assert.Empty(result.CompilationErrors);
            Assert.Equal("headless-fixture", result.ReplayResult.ScenarioId);
            Assert.True(File.Exists(reportPath));

            using var report = LoadReportDocument(reportPath);
            var root = report.RootElement;
            Assert.True(root.GetProperty("isSuccess").GetBoolean());
            Assert.Equal(ProjectPath, root.GetProperty("projectPath").GetString());
            Assert.Equal(scenarioPath, root.GetProperty("scenarioPath").GetString());
            var replay = root.GetProperty("replayResult");
            Assert.Equal(result.ReplayResult.PlannedTicks, replay.GetProperty("plannedTicks").GetInt64());
            Assert.Equal(result.ReplayResult.ExecutedTicks, replay.GetProperty("executedTicks").GetInt64());
            Assert.Equal(result.ReplayResult.CommandResults.Count, replay.GetProperty("commandResults").GetArrayLength());
            Assert.NotNull(result.SignalTimelines);
            Assert.NotEmpty(result.SignalTimelines);
            var stationTimelineSamples = Assert.Single(
                result.SignalTimelines,
                timeline => timeline.SignalId == "di.station-present").Samples
                .Select(sample => sample.Value)
                .ToArray();
            Assert.NotEmpty(stationTimelineSamples);
            Assert.Contains(stationTimelineSamples, value => value == false);
        }
        finally
        {
            if (File.Exists(scenarioPath))
            {
                File.Delete(scenarioPath);
            }
            if (File.Exists(reportPath))
            {
                File.Delete(reportPath);
            }
        }
    }

    [Fact]
    public async Task RunAsync_WithMissingScenario_ReturnsFailure()
    {
        var runner = new DeterministicFaultScenarioHeadlessRunner();
        var result = await runner.RunAsync(ProjectPath, "missing-fault-scenario.json");

        Assert.False(result.IsSuccess);
        Assert.Null(result.ReplayResult);
        Assert.NotNull(result.FailureReason);
        Assert.Empty(result.CompilationErrors);
        Assert.Contains("Scenario file not found", result.FailureReason ?? "Scenario file not found");
    }

    [Fact]
    public async Task RunAsync_WithInvalidScenarioJson_ReturnsFailure()
    {
        var outputRoot = PrepareTestDataDirectory();
        var invalidScenarioPath = Path.Combine(outputRoot, $"invalid-scenario-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(
            invalidScenarioPath,
            """
            {
              "schemaVersion": 999,
              "scenarioId": "invalid-schema",
              "name": "Invalid schema",
              "description": "Out-of-band version",
              "durationTicks": 1,
              "actions": [
                {
                  "tick": 0,
                  "action": "InjectFault",
                  "faultKind": "UnknownFault",
                  "targetId": "di.station-present",
                  "forcedValue": false
                }
              ]
            }
            """);

        try
        {
            var runner = new DeterministicFaultScenarioHeadlessRunner();
            var result = await runner.RunAsync(ProjectPath, invalidScenarioPath);

            Assert.False(result.IsSuccess);
            Assert.NotEmpty(result.FailureReason ?? string.Empty);
            Assert.Contains("Failed to load scenario JSON.", result.FailureReason);
        }
        finally
        {
            if (File.Exists(invalidScenarioPath))
            {
                File.Delete(invalidScenarioPath);
            }
        }
    }

    private static string PrepareTestDataDirectory()
    {
        var preferred = Path.Combine(
            "D:\\",
            "OpenVisionLab-TestData",
            "OpenVisionLab-Machine-Studio",
            "Simulation",
            "HeadlessRunnerTests");
        try
        {
            Directory.CreateDirectory(preferred);
            return preferred;
        }
        catch
        {
            var fallback = Path.Combine(
                Path.GetTempPath(),
                "OpenVisionLab-Machine-Studio",
                "Simulation",
                "HeadlessRunnerTests");
            Directory.CreateDirectory(fallback);
            return fallback;
        }
    }

    private static JsonDocument LoadReportDocument(string reportPath)
    {
        var text = File.ReadAllText(reportPath);
        return JsonDocument.Parse(text);
    }
}
