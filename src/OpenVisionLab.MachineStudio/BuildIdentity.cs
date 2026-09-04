using System.IO;
using System.Reflection;
using System.Text.Json;
using OpenVisionLab.Integration.Contracts;

namespace OpenVisionLab.MachineStudio;

internal static class BuildIdentity
{
    public static string Current { get; } = Resolve();
    public static string SourceCommit { get; } = ResolveMetadata("OpenVisionSourceCommit");
    public static string SourceState { get; } = ResolveMetadata("OpenVisionSourceState");
    public static string Compact { get; } = ResolveCompact();
    public static IntegrationApplicationIdentity IntegrationIdentity =>
        LoadQualifiedIntegrationIdentity(typeof(BuildIdentity).Assembly);
    public static bool IsExactCommit =>
        string.Equals(SourceState, "clean", StringComparison.Ordinal)
        && SourceCommit.Length == 40
        && SourceCommit.All(Uri.IsHexDigit);

    internal static IntegrationApplicationIdentity LoadQualifiedIntegrationIdentity(
        Assembly applicationAssembly,
        string? manifestPath = null) =>
        IntegrationRuntimeBuildVerifier.LoadQualifiedIdentity(
            applicationAssembly,
            IntegrationApplicationIds.MachineStudio,
            manifestPath);

    public static void SaveReport(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var report = new
        {
            schema = "1.0",
            capturedAtUtc = DateTimeOffset.UtcNow,
            identity = Current,
            sourceCommit = SourceCommit,
            sourceState = SourceState,
            isExactCommit = IsExactCommit
        };
        File.WriteAllText(
            fullPath,
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string Resolve()
    {
        var assembly = typeof(BuildIdentity).Assembly;
        return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "development";
    }

    private static string ResolveMetadata(string key) =>
        typeof(BuildIdentity).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => string.Equals(attribute.Key, key, StringComparison.Ordinal))?
            .Value
        ?? "unknown";

    private static string ResolveCompact()
    {
        return SourceCommit.Length >= 8 ? $"g{SourceCommit[..8]}" : SourceCommit;
    }
}
