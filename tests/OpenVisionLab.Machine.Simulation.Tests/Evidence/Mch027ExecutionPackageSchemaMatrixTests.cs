using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenVisionLab.Machine.Simulation.Commissioning;
using OpenVisionLab.Machine.Simulation.Events;
using OpenVisionLab.Machine.Simulation.FaultScenarios;
using OpenVisionLab.Machine.Simulation.Scenarios;
using Xunit;

namespace OpenVisionLab.Machine.Simulation.Tests;

public sealed class Mch027ExecutionPackageSchemaMatrixTests
{
    private const string TestRoot = @"D:\OpenVisionLab-TestData\Machine\mch-027-schema-read-20260916\execution";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    [Fact]
    public void FutureSchemaFixtures_AreNeverAcceptedAsValidEvidence()
    {
        var futureJson = """
            {
              "schemaVersion": 999,
              "scenarioId": "future",
              "name": "Future",
              "description": "Future schema fixture",
              "targetId": "target",
              "durationTicks": 1,
              "actions": [],
              "assertions": [],
              "entries": [],
              "runs": []
            }
            """;

        var readers = new (string Name, Func<string, bool> IsAccepted)[]
        {
            ("condition-profile", path =>
            {
                var profile = DeterministicConditionScenarioProfile.LoadFromJson(path);
                return profile is not null
                    && DeterministicConditionScenarioProfile.Validate(profile).Count == 0;
            }),
            ("fault-profile", path =>
            {
                var profile = DeterministicFaultScenarioProfile.LoadFromJson(path);
                return profile is not null
                    && DeterministicFaultScenarioProfile.Validate(profile).Count == 0;
            }),
            ("run-result", path =>
                DeterministicSimulationRunResultPackage.LoadFromJson(path)?.HasValidEvidenceHash() == true),
            ("batch-result", path =>
                DeterministicSimulationBatchResultPackage.LoadFromJson(path)?.HasValidEvidenceHash() == true),
            ("command-trace", path =>
                DeterministicSimulationCommandTracePackage.LoadFromJson(path)?.HasValidTraceHash() == true),
            ("exchange", path =>
                DeterministicSimulationEvidenceExchangePackage.LoadFromJson(path)?.HasValidEvidenceHash() == true),
            ("vision", path =>
                DeterministicVisionExecutionEvidencePackage.LoadFromJson(path)?.HasValidEvidenceHash() == true),
            ("unified", path =>
                DeterministicUnifiedCommissioningEvidencePackage.LoadFromJson(path)?.HasValidEvidenceHash() == true),
            ("multi-axis-result", path =>
                DeterministicMultiAxisCommissioningResultPackage.LoadFromJson(path)?.HasValidEvidenceHash() == true),
            ("commissioning-baseline", path =>
                DeterministicMultiAxisCommissioningBaseline.LoadFromJson(path)?.HasValidEvidenceHash() == true),
            ("commissioning-history", path =>
                DeterministicMultiAxisCommissioningResultHistory.LoadFromJson(path)?.HasValidEvidenceHash() == true)
        };

        Directory.CreateDirectory(TestRoot);
        foreach (var reader in readers)
        {
            var path = Path.Combine(TestRoot, $"future-{reader.Name}.json");
            File.WriteAllText(path, futureJson);
            var beforeHash = HashFile(path);

            Assert.False(reader.IsAccepted(path), reader.Name);
            Assert.Equal(beforeHash, HashFile(path));
        }
    }

    [Fact]
    public void EventJournal_FutureSchemaWithMatchingFutureHashIsRejected()
    {
        var journal = new SimulationEventJournalSnapshot(
            Capacity: 1,
            StoredEventCount: 0,
            TotalEventCount: 0,
            FirstEventIndex: 0,
            LastEventIndex: 0,
            IsCompleted: true,
            IsComplete: true,
            FirstMissingEventIndex: null);
        var current = SimulationEventJournalExportPackage.Create(journal, []);
        var futureSchema = current with
        {
            SchemaVersion = SimulationEventJournalExportPackage.CurrentSchemaVersion + 1,
            JournalHash = ComputeJournalHash(
                SimulationEventJournalExportPackage.CurrentSchemaVersion + 1,
                journal,
                [])
        };
        var path = Path.Combine(TestRoot, "future-event-journal.json");
        Directory.CreateDirectory(TestRoot);
        File.WriteAllText(path, JsonSerializer.Serialize(futureSchema, JsonOptions));
        var beforeHash = HashFile(path);

        Assert.False(futureSchema.HasValidJournalHash());
        Assert.Null(SimulationEventJournalExportPackage.LoadFromJson(path));
        Assert.Equal(beforeHash, HashFile(path));
    }

    private static string HashFile(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private static string ComputeJournalHash(
        int schemaVersion,
        SimulationEventJournalSnapshot journal,
        IEnumerable<SimulationEvent> events)
    {
        var builder = new StringBuilder()
            .Append(schemaVersion).Append('|')
            .Append(journal.Capacity).Append('|')
            .Append(journal.StoredEventCount).Append('|')
            .Append(journal.TotalEventCount).Append('|')
            .Append(journal.FirstEventIndex).Append('|')
            .Append(journal.LastEventIndex).Append('|')
            .Append(journal.IsCompleted).Append('|')
            .Append(journal.IsComplete).Append('|')
            .Append(journal.FirstMissingEventIndex).Append('\n');
        foreach (var runtimeEvent in events)
        {
            builder.Append(JsonSerializer.Serialize(runtimeEvent, JsonOptions)).Append('\n');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }
}
