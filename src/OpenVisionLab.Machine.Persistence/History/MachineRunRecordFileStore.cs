using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenVisionLab.Machine.Core.History;
using OpenVisionLab.Machine.Core.Projects;

namespace OpenVisionLab.Machine.Persistence.History;

public sealed record RunRecordReadResult(IReadOnlyList<MachineRunRecord> Records, IReadOnlyList<string> Errors);

/// <summary>One immutable JSON file per run, including captured bytes; no automatic retention deletion.</summary>
public sealed class MachineRunRecordFileStore
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
    private readonly string _root;

    public MachineRunRecordFileStore(string root) => _root = Path.GetFullPath(root);

    public async Task SaveAsync(MachineRunRecord record, CancellationToken cancellationToken = default)
    {
        Validate(record, record.RecipeId);
        var path = RecordPath(record.RecipeId, record.Id);
        var json = JsonSerializer.Serialize(record, Options);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path))
        {
            if (await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false) == json) return;
            throw new IOException("A different record already has this run ID.");
        }
        var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporaryPath, json, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, path);
        }
        finally { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
    }

    public async Task<RunRecordReadResult> ReadAsync(string recipeId, CancellationToken cancellationToken = default)
    {
        var directory = RecipeDirectory(recipeId);
        if (!Directory.Exists(directory)) return new([], []);
        var records = new List<MachineRunRecord>();
        var errors = new List<string>();
        foreach (var path in Directory.EnumerateFiles(directory, "*.json").Order(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var record = JsonSerializer.Deserialize<MachineRunRecord>(await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false), Options)
                    ?? throw new InvalidDataException("Empty run record.");
                Validate(record, recipeId);
                if (Path.GetFileNameWithoutExtension(path) != record.Id) throw new InvalidDataException("Run ID does not match its file.");
                records.Add(record);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException or ProjectDocumentLoadException)
            { errors.Add(Path.GetFileName(path) + ": " + exception.Message); }
        }
        return new(records.OrderByDescending(record => record.StartedAtUtc).ToArray(), errors);
    }

    public void Delete(string recipeId, string runId) => File.Delete(RecordPath(recipeId, runId));

    public async Task<MachineRunRecord> PreserveSourceImagesAsync(MachineRunRecord record, CancellationToken cancellationToken = default)
    {
        var captures = new List<RunCaptureRecord>();
        foreach (var capture in record.Captures)
        {
            if (capture.Pixels is not null || capture.SourceRelativePath is null) { captures.Add(capture); continue; }
            try
            {
                if (record.ProjectPath is null) throw new IOException("Source project path was not recorded.");
                var directory = Path.GetDirectoryName(Path.GetFullPath(record.ProjectPath))!;
                var source = Path.GetFullPath(Path.Combine(directory, capture.SourceRelativePath));
                if (!source.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("The frame source is outside the recipe directory.");
                var bytes = await File.ReadAllBytesAsync(source, cancellationToken).ConfigureAwait(false);
                if (capture.ContentSha256 is null || !Convert.ToHexString(SHA256.HashData(bytes)).Equals(capture.ContentSha256, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("The source no longer matches the captured frame hash.");
                captures.Add(capture with { Pixels = bytes });
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
            { captures.Add(capture with { ImageError = exception.Message }); }
        }
        return record with { Captures = captures.ToArray() };
    }

    private string RecipeDirectory(string recipeId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recipeId);
        return Path.Combine(_root, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(recipeId))));
    }

    private string RecordPath(string recipeId, string runId)
    {
        if (!Guid.TryParseExact(runId, "N", out _)) throw new ArgumentException("Run ID must be a GUID in N format.", nameof(runId));
        return Path.Combine(RecipeDirectory(recipeId), runId + ".json");
    }

    private static void Validate(MachineRunRecord record, string recipeId)
    {
        if (record.SchemaVersion != 1 || record.RecipeId != recipeId || !Guid.TryParseExact(record.Id, "N", out _)
            || record.EndedAtUtc < record.StartedAtUtc || record.Outcome is not ("Completed" or "Stopped" or "Faulted")
            || record.Products is null || record.Captures is null || record.Inspections is null || record.Faults is null)
            throw new InvalidDataException("Invalid or unsupported run record.");
        var project = new ProjectDocumentStore().Load(record.AppliedRecipeJson);
        if (project.Id != recipeId) throw new InvalidDataException("Applied recipe ID does not match the record.");
        foreach (var capture in record.Captures)
            if (capture.Pixels is not null && capture.ContentSha256 is not null
                && !Convert.ToHexString(SHA256.HashData(capture.Pixels)).Equals(capture.ContentSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Captured image hash does not match.");
    }
}
