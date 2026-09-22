using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenVisionLab.Machine.Simulation.Scenarios;

/// <summary>
/// The reviewed expected outcome for one deterministic vision sample. Unknown
/// is explicit and is never eligible for scoring, even after approval.
/// </summary>
public enum DeterministicVisionExpectedJudgment
{
    Pass,
    Fail,
    Unknown
}

/// <summary>
/// Review lifecycle for an expected-judgment entry. Pending entries preserve
/// the sample for later review but cannot be treated as approved ground truth.
/// </summary>
public enum DeterministicVisionLabelApprovalState
{
    Pending,
    Approved,
    Superseded
}

public sealed record DeterministicVisionExpectedJudgmentEntry(
    string SampleId,
    string ImageSha256,
    string RecipeId,
    string RecipeVersion,
    string LabelVersion,
    DeterministicVisionExpectedJudgment ExpectedJudgment,
    string Reason,
    string ReviewerRole,
    DeterministicVisionLabelApprovalState ApprovalState)
{
    public bool IsScorable => ApprovalState == DeterministicVisionLabelApprovalState.Approved
        && ExpectedJudgment != DeterministicVisionExpectedJudgment.Unknown;
}

/// <summary>
/// Versioned, hash-bound expected-label sidecar for the deterministic Vision
/// evidence path. It records review state without inventing customer ground
/// truth; values remain Pending until an authorized reviewer approves them.
/// </summary>
public sealed record DeterministicVisionExpectedJudgmentManifest(
    int SchemaVersion,
    string ManifestVersion,
    ImmutableArray<DeterministicVisionExpectedJudgmentEntry> Entries,
    string ManifestHash)
{
    public const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static DeterministicVisionExpectedJudgmentManifest Create(
        string manifestVersion,
        IEnumerable<DeterministicVisionExpectedJudgmentEntry> entries)
    {
        var normalizedVersion = NormalizeRequired(manifestVersion, nameof(manifestVersion));
        ArgumentNullException.ThrowIfNull(entries);
        var normalizedEntries = entries
            .Select(NormalizeEntry)
            .OrderBy(entry => entry.SampleId, StringComparer.Ordinal)
            .ToImmutableArray();
        ValidateEntries(normalizedEntries);

        var manifestHash = Hash(SerializeHashPayload(
            CurrentSchemaVersion,
            normalizedVersion,
            normalizedEntries));
        return new(CurrentSchemaVersion, normalizedVersion, normalizedEntries, manifestHash);
    }

    public bool HasValidManifestHash()
    {
        if (SchemaVersion != CurrentSchemaVersion
            || string.IsNullOrWhiteSpace(ManifestVersion)
            || Entries.IsDefaultOrEmpty
            || !IsSha256(ManifestHash))
        {
            return false;
        }

        try
        {
            ValidateEntries(Entries, requireSorted: true);
        }
        catch (ArgumentException)
        {
            return false;
        }

        var expectedHash = Hash(SerializeHashPayload(
            SchemaVersion,
            ManifestVersion,
            Entries));
        return string.Equals(ManifestHash, expectedHash, StringComparison.Ordinal);
    }

    public static string SaveToJson(DeterministicVisionExpectedJudgmentManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (!manifest.HasValidManifestHash())
        {
            throw new InvalidOperationException("Invalid expected-judgment manifest cannot be saved.");
        }

        return JsonSerializer.Serialize(manifest, JsonOptions);
    }

    public static void SaveToJson(
        DeterministicVisionExpectedJudgmentManifest manifest,
        string path)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        AtomicEvidenceFile.Write(
            fullPath,
            temporaryPath => File.WriteAllText(temporaryPath, SaveToJson(manifest)));
    }

    public static DeterministicVisionExpectedJudgmentManifest? LoadFromJson(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<DeterministicVisionExpectedJudgmentManifest>(
                File.ReadAllText(path),
                JsonOptions);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static DeterministicVisionExpectedJudgmentEntry NormalizeEntry(
        DeterministicVisionExpectedJudgmentEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return entry with
        {
            SampleId = NormalizeRequired(entry.SampleId, nameof(entry.SampleId)),
            ImageSha256 = NormalizeRequired(entry.ImageSha256, nameof(entry.ImageSha256)).ToUpperInvariant(),
            RecipeId = NormalizeRequired(entry.RecipeId, nameof(entry.RecipeId)),
            RecipeVersion = NormalizeRequired(entry.RecipeVersion, nameof(entry.RecipeVersion)),
            LabelVersion = NormalizeRequired(entry.LabelVersion, nameof(entry.LabelVersion)),
            Reason = NormalizeRequired(entry.Reason, nameof(entry.Reason)),
            ReviewerRole = NormalizeRequired(entry.ReviewerRole, nameof(entry.ReviewerRole))
        };
    }

    private static void ValidateEntries(
        ImmutableArray<DeterministicVisionExpectedJudgmentEntry> entries,
        bool requireSorted = false)
    {
        if (entries.IsDefaultOrEmpty)
        {
            throw new ArgumentException("At least one expected-judgment entry is required.", nameof(entries));
        }

        string? previousSampleId = null;
        var sampleIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            _ = NormalizeEntry(entry);
            if (!IsSha256(entry.ImageSha256))
            {
                throw new ArgumentException(
                    $"Entry '{entry.SampleId}' must contain a 64-character SHA-256 image hash.",
                    nameof(entries));
            }

            if (!Enum.IsDefined(entry.ExpectedJudgment)
                || !Enum.IsDefined(entry.ApprovalState))
            {
                throw new ArgumentException(
                    $"Entry '{entry.SampleId}' contains an unknown judgment or approval state.",
                    nameof(entries));
            }

            if (!sampleIds.Add(entry.SampleId))
            {
                throw new ArgumentException(
                    $"Sample id '{entry.SampleId}' is duplicated.",
                    nameof(entries));
            }

            if (requireSorted
                && previousSampleId is not null
                && string.CompareOrdinal(previousSampleId, entry.SampleId) >= 0)
            {
                throw new ArgumentException("Expected-judgment entries must be sorted by sample id.", nameof(entries));
            }

            previousSampleId = entry.SampleId;
        }
    }

    private static string SerializeHashPayload(
        int schemaVersion,
        string manifestVersion,
        ImmutableArray<DeterministicVisionExpectedJudgmentEntry> entries) =>
        JsonSerializer.Serialize(
            new HashPayload(schemaVersion, manifestVersion, entries),
            JsonOptions);

    private static string NormalizeRequired(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        return value.Trim();
    }

    private static bool IsSha256(string? value) =>
        value is { Length: 64 }
        && value.All(Uri.IsHexDigit);

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private sealed record HashPayload(
        int SchemaVersion,
        string ManifestVersion,
        ImmutableArray<DeterministicVisionExpectedJudgmentEntry> Entries);
}
