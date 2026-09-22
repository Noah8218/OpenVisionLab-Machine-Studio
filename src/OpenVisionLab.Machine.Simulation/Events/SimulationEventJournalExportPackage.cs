using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenVisionLab.Machine.Simulation.Scenarios;

namespace OpenVisionLab.Machine.Simulation.Events;

/// <summary>
/// Deterministic, file-backed representation of one canonical event journal.
/// Incomplete journals can be inspected after loading, but they cannot be
/// created or saved as valid evidence.
/// </summary>
public sealed record SimulationEventJournalExportPackage(
    int SchemaVersion,
    SimulationEventJournalSnapshot Journal,
    ImmutableArray<SimulationEvent> Events,
    string JournalHash)
{
    public const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public bool CanExport => TryValidateSnapshotAndEvents(
        Journal,
        Events,
        requireCompleted: true,
        requireComplete: true,
        out _);

    public bool HasValidJournalHash()
    {
        if (SchemaVersion != CurrentSchemaVersion)
        {
            return false;
        }

        var events = Events.IsDefault
            ? ImmutableArray<SimulationEvent>.Empty
            : Events;
        return TryValidateSnapshotAndEvents(
                Journal,
                events,
                requireCompleted: false,
                requireComplete: false,
                out _)
            && string.Equals(
                JournalHash,
                ComputeJournalHash(SchemaVersion, Journal, events),
                StringComparison.Ordinal);
    }

    public static SimulationEventJournalExportPackage Create(
        SimulationEventJournalSnapshot journal,
        IEnumerable<SimulationEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        var materialized = events.ToImmutableArray();
        if (!TryValidateSnapshotAndEvents(
                journal,
                materialized,
                requireCompleted: true,
                requireComplete: true,
                out var error))
        {
            throw new InvalidOperationException(error);
        }

        return new(
            CurrentSchemaVersion,
            journal,
            materialized,
            ComputeJournalHash(CurrentSchemaVersion, journal, materialized));
    }

    public static async Task<SimulationEventJournalExportPackage> CaptureAsync(
        ISimulationEventJournalSource source,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        var events = new List<SimulationEvent>();
        await foreach (var runtimeEvent in source
            .ReadCanonicalEventsAsync(cancellationToken)
            .ConfigureAwait(false))
        {
            events.Add(runtimeEvent);
        }

        return Create(source.EventJournal, events);
    }

    public static string SaveToJson(SimulationEventJournalExportPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        if (!package.HasValidJournalHash() || !package.CanExport)
        {
            throw new InvalidOperationException(
                "Only complete, integrity-checked canonical event journals can be exported.");
        }

        return JsonSerializer.Serialize(package, JsonOptions);
    }

    public static void SaveToJson(
        SimulationEventJournalExportPackage package,
        string path)
    {
        SaveToJson(package, path, new SimulationEventJournalExportOptions());
    }

    public static void SaveToJson(
        SimulationEventJournalExportPackage package,
        string path,
        SimulationEventJournalExportOptions options)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(options);

        var json = SaveToJson(package);
        var fullPath = Path.GetFullPath(path);
        options.Validate(json, fullPath);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        AtomicEvidenceFile.Write(fullPath, temporaryPath => File.WriteAllText(temporaryPath, json, Encoding.UTF8));
    }

    public static SimulationEventJournalExportPackage? LoadFromJson(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var package = JsonSerializer.Deserialize<SimulationEventJournalExportPackage>(
                File.ReadAllText(path),
                JsonOptions);
            return package is not null && package.HasValidJournalHash()
                ? package
                : null;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static bool TryValidateSnapshotAndEvents(
        SimulationEventJournalSnapshot journal,
        IReadOnlyList<SimulationEvent> events,
        bool requireCompleted,
        bool requireComplete,
        out string error)
    {
        if (journal.Capacity <= 0)
        {
            error = "Journal capacity must be positive.";
            return false;
        }

        if (journal.StoredEventCount < 0
            || journal.StoredEventCount > journal.Capacity
            || journal.TotalEventCount < journal.StoredEventCount)
        {
            error = "Journal event counts are inconsistent.";
            return false;
        }

        if (journal.StoredEventCount != events.Count)
        {
            error = "Journal stored count does not match the exported records.";
            return false;
        }

        long lastStoredEventIndex = 0;
        if (journal.StoredEventCount == 0)
        {
            if (journal.FirstEventIndex != 0 || journal.LastEventIndex != 0)
            {
                error = "An empty journal must have zero first and last indexes.";
                return false;
            }
        }
        else if (journal.FirstEventIndex <= 0
            || journal.FirstEventIndex > long.MaxValue - journal.StoredEventCount + 1)
        {
            error = "Journal first and stored indexes are inconsistent.";
            return false;
        }
        else
        {
            lastStoredEventIndex = journal.FirstEventIndex + journal.StoredEventCount - 1;
            if (journal.LastEventIndex < lastStoredEventIndex
                || journal.LastEventIndex != journal.TotalEventCount)
            {
                error = "Journal last and total indexes are inconsistent.";
                return false;
            }
        }

        if (journal.IsComplete != (journal.IsCompleted && journal.FirstMissingEventIndex is null))
        {
            error = "Journal completion flags are inconsistent.";
            return false;
        }

        if (journal.FirstMissingEventIndex is { } firstMissingEventIndex)
        {
            if (journal.StoredEventCount != journal.Capacity
                || journal.StoredEventCount == 0
                || lastStoredEventIndex == long.MaxValue
                || firstMissingEventIndex != lastStoredEventIndex + 1
                || firstMissingEventIndex > journal.TotalEventCount)
            {
                error = "Journal overflow watermark is inconsistent.";
                return false;
            }
        }
        else if (journal.TotalEventCount != journal.StoredEventCount)
        {
            error = "A journal without an overflow watermark cannot hide records.";
            return false;
        }

        if (requireCompleted && !journal.IsCompleted)
        {
            error = "The canonical event journal must be completed before export.";
            return false;
        }

        if (requireComplete && !journal.IsComplete)
        {
            error = "Incomplete canonical event journal evidence cannot be exported.";
            return false;
        }

        for (var index = 0; index < events.Count; index++)
        {
            var runtimeEvent = events[index];
            var expectedEventIndex = journal.FirstEventIndex + index;
            if (runtimeEvent.EventIndex != expectedEventIndex)
            {
                error = "Canonical event indexes contain a gap or duplicate.";
                return false;
            }
        }

        error = string.Empty;
        return true;
    }

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

/// <summary>
/// Explicit resource guards for one canonical journal export. A zero value
/// leaves that guard disabled; no deletion or age policy is implied.
/// </summary>
public sealed record SimulationEventJournalExportOptions(
    long MaximumFileBytes = 0,
    long MinimumFreeBytes = 0)
{
    internal void Validate(string json, string exportPath)
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentException.ThrowIfNullOrWhiteSpace(exportPath);
        if (MaximumFileBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumFileBytes));
        }
        if (MinimumFreeBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MinimumFreeBytes));
        }

        var fileBytes = Encoding.UTF8.GetByteCount(json);
        if (MaximumFileBytes > 0 && fileBytes > MaximumFileBytes)
        {
            throw new InvalidOperationException(
                $"Canonical event journal export is {fileBytes} bytes, exceeding the configured "
                + $"maximum of {MaximumFileBytes} bytes.");
        }

        if (MinimumFreeBytes > 0)
        {
            var root = Path.GetPathRoot(exportPath);
            if (string.IsNullOrWhiteSpace(root))
            {
                throw new IOException("The export path has no resolvable drive root.");
            }

            var availableFreeBytes = new DriveInfo(root).AvailableFreeSpace;
            if (MinimumFreeBytes > long.MaxValue - fileBytes)
            {
                throw new IOException("The configured minimum free-space guard is too large.");
            }

            var requiredFreeBytes = fileBytes + MinimumFreeBytes;
            if (availableFreeBytes < requiredFreeBytes)
            {
                throw new IOException(
                    $"Canonical event journal export requires {requiredFreeBytes} "
                    + $"free bytes, but only {availableFreeBytes} are available.");
            }
        }
    }
}
