using System.Text.Json;
using System.Text.RegularExpressions;

namespace OpenVisionLab.Machine.Core.Diagnostics;

public sealed record SupportDiagnosticError(
    string Code,
    string Message,
    string? Detail = null);

public sealed record SupportDiagnosticQueueObservation(
    string Name,
    string State,
    bool TimedOut = false,
    long? TimeoutMilliseconds = null,
    string? Detail = null);

public sealed record SupportDiagnosticArtifact(
    string RelativePath,
    string Kind,
    long? ByteLength = null,
    string? Sha256 = null);

public sealed record SupportDiagnosticBundleRequest(
    string ApplicationVersion,
    string? SourceCommit = null,
    string? SourceState = null,
    string? ProjectId = null,
    string? RunId = null,
    IReadOnlyList<SupportDiagnosticError>? Errors = null,
    IReadOnlyList<SupportDiagnosticQueueObservation>? Queue = null,
    IReadOnlyList<SupportDiagnosticArtifact>? Artifacts = null,
    bool Replayable = false,
    string? ReplayLimitation = null,
    IReadOnlyList<string>? SensitiveValues = null,
    IReadOnlyList<string>? SensitivePaths = null);

public sealed record SupportDiagnosticIdentity(
    string ApplicationVersion,
    string? SourceCommit,
    string? SourceState,
    string? ProjectId);

public sealed record SupportDiagnosticRun(string? RunId);

public sealed record SupportDiagnosticReplay(
    bool IsReplayable,
    string Limitation);

public sealed record SupportDiagnosticExclusion(
    string Category,
    string Reason);

public sealed record SupportDiagnosticBundle(
    string Schema,
    DateTimeOffset ExportedAtUtc,
    SupportDiagnosticIdentity Identity,
    SupportDiagnosticRun Run,
    IReadOnlyList<SupportDiagnosticError> Errors,
    IReadOnlyList<SupportDiagnosticQueueObservation> Queue,
    IReadOnlyList<SupportDiagnosticArtifact> Artifacts,
    SupportDiagnosticReplay Replay,
    IReadOnlyList<SupportDiagnosticExclusion> Excluded);

/// <summary>
/// Builds and writes the smallest support diagnostic snapshot. It accepts
/// observations from existing owners but never reads or modifies their files.
/// Original images, secret material, absolute paths, and traversal paths are
/// excluded before serialization.
/// </summary>
public sealed class SupportDiagnosticBundleBuilder
{
    private const string Schema = "openvisionlab-machine-support-diagnostics/1";
    private const string DefaultReplayLimitation =
        "Replay is unavailable because original image and secret material are excluded; included diagnostics are not an integrity-valid package.";
    private static readonly Regex WindowsAbsolutePath = new(
        @"^[A-Za-z]:[\\/]",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex SecretAssignment = new(
        @"(?ix)\b(token|secret|password|api[-_]?key|access[-_]?key)\s*[:=]\s*[^\s,;]+",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex UnixAbsolutePath = new(
        @"(?:^|[\s(])/(?:[^\s/]+/)+[^\s,;)]*",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly string[] ImageExtensions =
    [
        ".bmp", ".gif", ".jpeg", ".jpg", ".pgm", ".png", ".tif", ".tiff", ".webp"
    ];

    public SupportDiagnosticBundle Build(
        SupportDiagnosticBundleRequest request,
        DateTimeOffset? exportedAtUtc = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ApplicationVersion);

        var excluded = new List<SupportDiagnosticExclusion>();
        var sensitiveValues = request.SensitiveValues?
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray()
            ?? [];
        var sensitivePaths = request.SensitivePaths?
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path.Trim())
            .ToArray()
            ?? [];

        foreach (var _ in sensitiveValues)
        {
            excluded.Add(new("secret-value", "Secret values are never exported."));
        }

        foreach (var path in sensitivePaths)
        {
            excluded.Add(new(DescribePathCategory(path), "Sensitive path is never exported."));
        }

        var sanitizedArtifacts = new List<SupportDiagnosticArtifact>();
        foreach (var artifact in request.Artifacts ?? [])
        {
            if (!TryNormalizeArtifact(
                    artifact,
                    sensitiveValues,
                    sensitivePaths,
                    out var normalized,
                    out var category,
                    out var reason))
            {
                excluded.Add(new(category, reason));
                continue;
            }

            sanitizedArtifacts.Add(normalized);
        }

        var hasSensitiveExclusion = excluded.Any(item =>
            item.Category is "secret-value" or "secret-path" or "selected-image" or "original-image"
                or "absolute-path" or "user-path");
        var isReplayable = request.Replayable && !hasSensitiveExclusion;
        var replayLimitation = isReplayable
            ? Redact(request.ReplayLimitation ?? string.Empty, sensitiveValues)
            : Redact(
                string.IsNullOrWhiteSpace(request.ReplayLimitation)
                    ? DefaultReplayLimitation
                    : request.ReplayLimitation,
                sensitiveValues);

        return new(
            Schema,
            exportedAtUtc ?? DateTimeOffset.UtcNow,
            new(
                Redact(request.ApplicationVersion, sensitiveValues),
                RedactOptional(request.SourceCommit, sensitiveValues),
                RedactOptional(request.SourceState, sensitiveValues),
                RedactOptional(request.ProjectId, sensitiveValues)),
            new(RedactOptional(request.RunId, sensitiveValues)),
            (request.Errors ?? [])
                .Select(error => new SupportDiagnosticError(
                    SanitizeIdentifier(error.Code, "error"),
                    Redact(error.Message, sensitiveValues),
                    RedactOptional(error.Detail, sensitiveValues)))
                .ToArray(),
            (request.Queue ?? [])
                .Select(observation => new SupportDiagnosticQueueObservation(
                    SanitizeIdentifier(observation.Name, "queue"),
                    SanitizeIdentifier(observation.State, "unknown"),
                    observation.TimedOut,
                    observation.TimeoutMilliseconds is < 0
                        ? null
                        : observation.TimeoutMilliseconds,
                    RedactOptional(observation.Detail, sensitiveValues)))
                .ToArray(),
            sanitizedArtifacts
                .OrderBy(artifact => artifact.RelativePath, StringComparer.Ordinal)
                .ToArray(),
            new(isReplayable, replayLimitation),
            excluded
                .Distinct()
                .ToArray());
    }

    public string Serialize(
        SupportDiagnosticBundleRequest request,
        DateTimeOffset? exportedAtUtc = null)
    {
        var bundle = Build(request, exportedAtUtc);
        return JsonSerializer.Serialize(
            bundle,
            new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });
    }

    public void WriteJson(
        string path,
        SupportDiagnosticBundleRequest request,
        DateTimeOffset? exportedAtUtc = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(fullPath, Serialize(request, exportedAtUtc));
    }

    private static bool TryNormalizeArtifact(
        SupportDiagnosticArtifact artifact,
        IReadOnlyList<string> sensitiveValues,
        IReadOnlyList<string> sensitivePaths,
        out SupportDiagnosticArtifact normalized,
        out string category,
        out string reason)
    {
        var rawPath = artifact.RelativePath?.Trim() ?? string.Empty;
        var path = rawPath.Replace('\\', '/');
        category = "artifact";
        reason = "Artifact was excluded.";
        normalized = artifact;

        if (string.IsNullOrWhiteSpace(path))
        {
            reason = "Artifact path is empty.";
            return false;
        }

        if (Path.IsPathRooted(rawPath) || WindowsAbsolutePath.IsMatch(rawPath) || path.StartsWith("/", StringComparison.Ordinal))
        {
            category = "absolute-path";
            reason = "Absolute paths are never exported.";
            return false;
        }

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(segment => segment is "." or ".."))
        {
            category = "traversal-path";
            reason = "Traversal paths are never exported.";
            return false;
        }

        if (ContainsSensitive(path, sensitiveValues, sensitivePaths))
        {
            category = DescribePathCategory(path);
            reason = "Sensitive paths are never exported.";
            return false;
        }

        var kind = artifact.Kind?.Trim() ?? string.Empty;
        if (IsOriginalImage(path, kind))
        {
            category = kind.Contains("selected", StringComparison.OrdinalIgnoreCase)
                ? "selected-image"
                : "original-image";
            reason = "Original and selected images are excluded by default.";
            return false;
        }

        if (IsSecretPath(path, kind))
        {
            category = "secret-path";
            reason = "Secret and credential artifacts are never exported.";
            return false;
        }

        normalized = new(
            string.Join('/', segments),
            SanitizeIdentifier(kind, "artifact"),
            artifact.ByteLength is < 0 ? null : artifact.ByteLength,
            SanitizeHash(artifact.Sha256));
        return true;
    }

    private static bool ContainsSensitive(
        string value,
        IReadOnlyList<string> sensitiveValues,
        IReadOnlyList<string> sensitivePaths) =>
        sensitiveValues.Any(secret => value.Contains(secret, StringComparison.Ordinal))
        || sensitivePaths.Any(path =>
            value.Contains(path.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase));

    private static bool IsOriginalImage(string path, string kind) =>
        kind.Contains("image", StringComparison.OrdinalIgnoreCase)
        || kind.Contains("selected", StringComparison.OrdinalIgnoreCase)
        || kind.Contains("original", StringComparison.OrdinalIgnoreCase)
        || ImageExtensions.Any(extension =>
            path.EndsWith(extension, StringComparison.OrdinalIgnoreCase));

    private static bool IsSecretPath(string path, string kind)
    {
        var candidate = $"{path}/{kind}";
        return candidate.Contains("secret", StringComparison.OrdinalIgnoreCase)
            || candidate.Contains("token", StringComparison.OrdinalIgnoreCase)
            || candidate.Contains("password", StringComparison.OrdinalIgnoreCase)
            || candidate.Contains("credential", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".key", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".pem", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".pfx", StringComparison.OrdinalIgnoreCase);
    }

    private static string DescribePathCategory(string path)
    {
        var normalized = path.Replace('\\', '/');
        if (IsOriginalImage(normalized, string.Empty))
        {
            return "selected-image";
        }

        if (IsSecretPath(normalized, string.Empty))
        {
            return "secret-path";
        }

        return WindowsAbsolutePath.IsMatch(path) || path.StartsWith("/", StringComparison.Ordinal)
            ? "user-path"
            : "sensitive-path";
    }

    private static string Redact(string value, IReadOnlyList<string> sensitiveValues)
    {
        var result = value ?? string.Empty;
        foreach (var sensitiveValue in sensitiveValues)
        {
            result = result.Replace(sensitiveValue, "<redacted>", StringComparison.Ordinal);
        }

        result = SecretAssignment.Replace(result, "$1=<redacted>");
        result = UnixAbsolutePath.Replace(result, " <user-path>");
        result = Regex.Replace(
            result,
            @"\b[A-Za-z]:[\\/][^\s,;)]*",
            "<user-path>",
            RegexOptions.CultureInvariant);
        return result;
    }

    private static string? RedactOptional(string? value, IReadOnlyList<string> sensitiveValues) =>
        value is null ? null : Redact(value, sensitiveValues);

    private static string SanitizeIdentifier(string? value, string fallback)
    {
        var text = value?.Trim() ?? string.Empty;
        if (text.Length == 0)
        {
            return fallback;
        }

        var sanitized = new string(text
            .Where(character => char.IsLetterOrDigit(character) || character is '.' or '-' or '_' or ':')
            .ToArray());
        return sanitized.Length == 0 ? fallback : sanitized[..Math.Min(sanitized.Length, 120)];
    }

    private static string? SanitizeHash(string? value)
    {
        var text = value?.Trim();
        return string.IsNullOrWhiteSpace(text)
            ? null
            : text.Length <= 128 && text.All(Uri.IsHexDigit)
                ? text.ToUpperInvariant()
                : null;
    }
}
