using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace OpenVisionLab.MachineStudio.Model;

internal sealed record RecipePackCompatibilityResult(
    string FileName,
    string DisplayName,
    string ProjectSchema,
    string BuildIdentity,
    string SourceCommit,
    string SourceState,
    bool IsExactCommit,
    string Outcome,
    string StepId,
    string Detail);

internal sealed record RecipePackCompatibilityReport(
    string Schema,
    DateTimeOffset CapturedAtUtc,
    string CurrentProjectSchema,
    IReadOnlyList<RecipePackCompatibilityResult> Results)
{
    public const string CurrentSchema = "1.0";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public static RecipePackCompatibilityReport Load(string path)
    {
        var report = JsonSerializer.Deserialize<RecipePackCompatibilityReport>(
            File.ReadAllText(Path.GetFullPath(path)),
            JsonOptions)
            ?? throw new InvalidDataException("The compatibility report is empty.");
        report.Validate();
        return report;
    }

    public void Save(string path)
    {
        Validate();
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(
            fullPath,
            JsonSerializer.Serialize(this, JsonOptions));
    }

    public RecipePackCompatibilityComparison CompareTo(RecipePackCompatibilityReport current)
    {
        ArgumentNullException.ThrowIfNull(current);
        Validate();
        current.Validate();

        var baselineByFile = Results.ToDictionary(result => result.FileName, StringComparer.OrdinalIgnoreCase);
        var currentByFile = current.Results.ToDictionary(result => result.FileName, StringComparer.OrdinalIgnoreCase);
        var orderedFileNames = Results.Select(result => result.FileName)
            .Concat(current.Results
                .Select(result => result.FileName)
                .Where(fileName => !baselineByFile.ContainsKey(fileName)));

        return new RecipePackCompatibilityComparison(
            this,
            current,
            orderedFileNames.Select(fileName =>
            {
                baselineByFile.TryGetValue(fileName, out var baselineResult);
                currentByFile.TryGetValue(fileName, out var currentResult);
                var projectSchemaChanged = !string.Equals(
                    baselineResult?.ProjectSchema,
                    currentResult?.ProjectSchema,
                    StringComparison.Ordinal);
                var buildChanged = baselineResult is null
                    || currentResult is null
                    || !string.Equals(baselineResult.BuildIdentity, currentResult.BuildIdentity, StringComparison.Ordinal)
                    || !string.Equals(baselineResult.SourceCommit, currentResult.SourceCommit, StringComparison.Ordinal)
                    || !string.Equals(baselineResult.SourceState, currentResult.SourceState, StringComparison.Ordinal)
                    || baselineResult.IsExactCommit != currentResult.IsExactCommit;
                var kind = GetChangeKind(baselineResult, currentResult, projectSchemaChanged, buildChanged);

                return new RecipePackCompatibilityComparisonItem(
                    fileName,
                    currentResult?.DisplayName ?? baselineResult!.DisplayName,
                    baselineResult,
                    currentResult,
                    kind,
                    projectSchemaChanged,
                    buildChanged);
            }).ToArray());
    }

    private void Validate()
    {
        if (!string.Equals(Schema, CurrentSchema, StringComparison.Ordinal))
        {
            throw new NotSupportedException($"Unsupported compatibility report schema '{Schema}'.");
        }

        if (CapturedAtUtc == default
            || string.IsNullOrWhiteSpace(CurrentProjectSchema)
            || Results is null
            || Results.Count == 0)
        {
            throw new InvalidDataException("The compatibility report is incomplete.");
        }

        var fileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var result in Results)
        {
            if (result is null
                || string.IsNullOrWhiteSpace(result.FileName)
                || string.IsNullOrWhiteSpace(result.DisplayName)
                || string.IsNullOrWhiteSpace(result.ProjectSchema)
                || string.IsNullOrWhiteSpace(result.BuildIdentity)
                || string.IsNullOrWhiteSpace(result.SourceCommit)
                || result.SourceState is not ("clean" or "dirty" or "unknown")
                || result.Outcome is not ("passed" or "failed")
                || !fileNames.Add(result.FileName))
            {
                throw new InvalidDataException("The compatibility report contains an invalid recipe result.");
            }
        }
    }

    private static RecipePackCompatibilityChangeKind GetChangeKind(
        RecipePackCompatibilityResult? baseline,
        RecipePackCompatibilityResult? current,
        bool projectSchemaChanged,
        bool buildChanged)
    {
        if (baseline is null)
        {
            return RecipePackCompatibilityChangeKind.Added;
        }

        if (current is null)
        {
            return RecipePackCompatibilityChangeKind.Removed;
        }

        if (baseline.Outcome == "passed" && current.Outcome == "failed")
        {
            return RecipePackCompatibilityChangeKind.NewlyFailed;
        }

        if (baseline.Outcome == "failed" && current.Outcome == "passed")
        {
            return RecipePackCompatibilityChangeKind.Recovered;
        }

        return projectSchemaChanged || buildChanged
            ? RecipePackCompatibilityChangeKind.MetadataChanged
            : RecipePackCompatibilityChangeKind.Unchanged;
    }
}

internal enum RecipePackCompatibilityChangeKind
{
    Unchanged,
    MetadataChanged,
    NewlyFailed,
    Recovered,
    Added,
    Removed
}

internal sealed record RecipePackCompatibilityComparisonItem(
    string FileName,
    string DisplayName,
    RecipePackCompatibilityResult? Baseline,
    RecipePackCompatibilityResult? Current,
    RecipePackCompatibilityChangeKind ChangeKind,
    bool ProjectSchemaChanged,
    bool BuildChanged);

internal sealed record RecipePackCompatibilityComparison(
    RecipePackCompatibilityReport Baseline,
    RecipePackCompatibilityReport Current,
    IReadOnlyList<RecipePackCompatibilityComparisonItem> Items)
{
    public bool ProjectSchemaChanged => !string.Equals(
        Baseline.CurrentProjectSchema,
        Current.CurrentProjectSchema,
        StringComparison.Ordinal);
}
