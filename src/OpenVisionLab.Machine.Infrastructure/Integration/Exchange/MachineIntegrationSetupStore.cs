using System.IO;
using System.Net;
using System.Text.Json;

namespace OpenVisionLab.Machine.Infrastructure.Integration;

public enum MachineIntegrationSetupLoadWarning
{
    None,
    MissingOrInvalid,
    ReadFailed
}

public sealed record MachineIntegrationSetupLoadResult(
    MachineIntegrationSetup Settings,
    MachineIntegrationSetupLoadWarning Warning,
    string? ErrorMessage);

public sealed record MachineIntegrationSetup
{
    public string ExchangeRoot { get; init; } = string.Empty;
    public string InspectionRecipePath { get; init; } = string.Empty;
    public IReadOnlyList<string> RecipeCatalogPaths { get; init; } = Array.Empty<string>();
    public string TwoDConsumerVersion { get; init; } = string.Empty;
    public string TwoDConsumerCommit { get; init; } = string.Empty;
    public bool WaitForExternalResult { get; init; }
    public bool UseThreeDHeightMap { get; init; }
    public string ThreeDHeightMapSourcePath { get; init; } = string.Empty;
    public string ThreeDHeightMapSourceSha256 { get; init; } = string.Empty;
    public long ThreeDHeightMapSourceLength { get; init; }
    public int ThreeDHeightMapWidth { get; init; }
    public int ThreeDHeightMapHeight { get; init; }
    public string ThreeDHeightMapPixelFormat { get; init; } = string.Empty;
    public string ThreeDHeightMapUnit { get; init; } = "mm";
    public string ThreeDInspectionRecipePath { get; init; } = string.Empty;
    public string ThreeDInspectionRecipeSha256 { get; init; } = string.Empty;
    public long ThreeDInspectionRecipeLength { get; init; }
    public string ThreeDConsumerVersion { get; init; } = string.Empty;
    public string ThreeDConsumerCommit { get; init; } = string.Empty;
    public string ThreeDSequenceId { get; init; } = string.Empty;
    public string ThreeDStepId { get; init; } = string.Empty;
    public string ThreeDDeviceId { get; init; } = string.Empty;
    public string TcpListenAddress { get; init; } = "127.0.0.1";
    public int TcpListenPort { get; init; } = 45101;
    public string TcpPeerHost { get; init; } = "127.0.0.1";
    public int TcpPeerPort { get; init; } = 45102;
}

public sealed record MachineIntegrationTcpSettings(
    string ExchangeRoot,
    IPAddress ListenAddress,
    int ListenPort,
    string PeerHost,
    int PeerPort);

/// <summary>
/// Owns the file-backed Machine Studio integration setup format and its
/// atomic persistence. It has no WPF or ViewModel dependency.
/// </summary>
public sealed class MachineIntegrationSetupStore
{
    private readonly string _path;

    public MachineIntegrationSetupStore(string? path = null) =>
        _path = path ?? DefaultSettingsPath();

    public MachineIntegrationSetupLoadResult Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return new(new MachineIntegrationSetup(), MachineIntegrationSetupLoadWarning.None, null);
            }

            var settings = JsonSerializer.Deserialize<PersistedSettings>(File.ReadAllText(_path));
            if (settings is null || !IsValid(settings))
            {
                return new(
                    new MachineIntegrationSetup(),
                    MachineIntegrationSetupLoadWarning.MissingOrInvalid,
                    null);
            }

            return new(
                new MachineIntegrationSetup
                {
                    ExchangeRoot = settings.ExchangeRoot,
                    InspectionRecipePath = settings.InspectionRecipePath,
                    RecipeCatalogPaths = settings.RecipeCatalogPaths is { Length: > 0 }
                        ? settings.RecipeCatalogPaths.ToArray()
                        : Array.Empty<string>(),
                    TwoDConsumerVersion = settings.TwoDConsumerVersion,
                    TwoDConsumerCommit = settings.TwoDConsumerCommit,
                    WaitForExternalResult = settings.WaitForExternalResult,
                    UseThreeDHeightMap = settings.UseThreeDHeightMap,
                    ThreeDHeightMapSourcePath = settings.ThreeDHeightMapSourcePath,
                    ThreeDHeightMapSourceSha256 = settings.ThreeDHeightMapSourceSha256,
                    ThreeDHeightMapSourceLength = settings.ThreeDHeightMapSourceLength,
                    ThreeDHeightMapWidth = settings.ThreeDHeightMapWidth,
                    ThreeDHeightMapHeight = settings.ThreeDHeightMapHeight,
                    ThreeDHeightMapPixelFormat = settings.ThreeDHeightMapPixelFormat,
                    ThreeDHeightMapUnit = settings.ThreeDHeightMapUnit,
                    ThreeDInspectionRecipePath = settings.ThreeDInspectionRecipePath,
                    ThreeDInspectionRecipeSha256 = settings.ThreeDInspectionRecipeSha256,
                    ThreeDInspectionRecipeLength = settings.ThreeDInspectionRecipeLength,
                    ThreeDConsumerVersion = settings.ThreeDConsumerVersion,
                    ThreeDConsumerCommit = settings.ThreeDConsumerCommit,
                    ThreeDSequenceId = settings.ThreeDSequenceId,
                    ThreeDStepId = settings.ThreeDStepId,
                    ThreeDDeviceId = settings.ThreeDDeviceId,
                    TcpListenAddress = settings.TcpListenAddress,
                    TcpListenPort = settings.TcpListenPort,
                    TcpPeerHost = settings.TcpPeerHost,
                    TcpPeerPort = settings.TcpPeerPort
                },
                MachineIntegrationSetupLoadWarning.None,
                null);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return new(
                new MachineIntegrationSetup(),
                MachineIntegrationSetupLoadWarning.ReadFailed,
                exception.Message);
        }
    }

    public void Save(MachineIntegrationSetup settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!string.IsNullOrWhiteSpace(settings.ExchangeRoot))
        {
            Directory.CreateDirectory(Path.GetFullPath(settings.ExchangeRoot));
        }

        var fullPath = Path.GetFullPath(_path);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new ArgumentException("The settings path must include a directory.", nameof(_path));
        Directory.CreateDirectory(directory);
        var temporaryPath = $"{fullPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(
                temporaryPath,
                JsonSerializer.Serialize(
                    new PersistedSettings
                    {
                        ExchangeRoot = settings.ExchangeRoot,
                        InspectionRecipePath = settings.InspectionRecipePath,
                        RecipeCatalogPaths = settings.RecipeCatalogPaths is { Count: > 0 }
                            ? settings.RecipeCatalogPaths.Where(path => !string.IsNullOrWhiteSpace(path)).ToArray()
                            : Array.Empty<string>(),
                        TwoDConsumerVersion = settings.TwoDConsumerVersion,
                        TwoDConsumerCommit = settings.TwoDConsumerCommit,
                        WaitForExternalResult = settings.WaitForExternalResult,
                        UseThreeDHeightMap = settings.UseThreeDHeightMap,
                        ThreeDHeightMapSourcePath = settings.ThreeDHeightMapSourcePath,
                        ThreeDHeightMapSourceSha256 = settings.ThreeDHeightMapSourceSha256,
                        ThreeDHeightMapSourceLength = settings.ThreeDHeightMapSourceLength,
                        ThreeDHeightMapWidth = settings.ThreeDHeightMapWidth,
                        ThreeDHeightMapHeight = settings.ThreeDHeightMapHeight,
                        ThreeDHeightMapPixelFormat = settings.ThreeDHeightMapPixelFormat,
                        ThreeDHeightMapUnit = settings.ThreeDHeightMapUnit,
                        ThreeDInspectionRecipePath = settings.ThreeDInspectionRecipePath,
                        ThreeDInspectionRecipeSha256 = settings.ThreeDInspectionRecipeSha256,
                        ThreeDInspectionRecipeLength = settings.ThreeDInspectionRecipeLength,
                        ThreeDConsumerVersion = settings.ThreeDConsumerVersion,
                        ThreeDConsumerCommit = settings.ThreeDConsumerCommit,
                        ThreeDSequenceId = settings.ThreeDSequenceId,
                        ThreeDStepId = settings.ThreeDStepId,
                        ThreeDDeviceId = settings.ThreeDDeviceId,
                        TcpListenAddress = settings.TcpListenAddress,
                        TcpListenPort = settings.TcpListenPort,
                        TcpPeerHost = settings.TcpPeerHost,
                        TcpPeerPort = settings.TcpPeerPort
                    },
                    new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public void Reset() => Save(new MachineIntegrationSetup());

    public bool MatchesSavedTcpSettings(MachineIntegrationTcpSettings current)
    {
        ArgumentNullException.ThrowIfNull(current);
        var saved = Load().Settings;
        return string.Equals(saved.ExchangeRoot, current.ExchangeRoot, StringComparison.OrdinalIgnoreCase)
            && string.Equals(
                saved.TcpListenAddress,
                current.ListenAddress.ToString(),
                StringComparison.OrdinalIgnoreCase)
            && saved.TcpListenPort == current.ListenPort
            && string.Equals(saved.TcpPeerHost, current.PeerHost, StringComparison.OrdinalIgnoreCase)
            && saved.TcpPeerPort == current.PeerPort;
    }

    private static string DefaultSettingsPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OpenVisionLab",
        "MachineStudio",
        "CONFIG",
        "integration-exchange.json");

    private static bool IsValid(PersistedSettings settings) =>
        IPAddress.TryParse(settings.TcpListenAddress, out _)
        && settings.TcpListenPort is >= 1 and <= IPEndPoint.MaxPort
        && !string.IsNullOrWhiteSpace(settings.TcpPeerHost)
        && settings.TcpPeerPort is >= 1 and <= IPEndPoint.MaxPort;

    private sealed class PersistedSettings
    {
        public string ExchangeRoot { get; set; } = string.Empty;
        public string InspectionRecipePath { get; set; } = string.Empty;
        public string[] RecipeCatalogPaths { get; set; } = Array.Empty<string>();
        public string TwoDConsumerVersion { get; set; } = string.Empty;
        public string TwoDConsumerCommit { get; set; } = string.Empty;
        public bool WaitForExternalResult { get; set; }
        public bool UseThreeDHeightMap { get; set; }
        public string ThreeDHeightMapSourcePath { get; set; } = string.Empty;
        public string ThreeDHeightMapSourceSha256 { get; set; } = string.Empty;
        public long ThreeDHeightMapSourceLength { get; set; }
        public int ThreeDHeightMapWidth { get; set; }
        public int ThreeDHeightMapHeight { get; set; }
        public string ThreeDHeightMapPixelFormat { get; set; } = string.Empty;
        public string ThreeDHeightMapUnit { get; set; } = "mm";
        public string ThreeDInspectionRecipePath { get; set; } = string.Empty;
        public string ThreeDInspectionRecipeSha256 { get; set; } = string.Empty;
        public long ThreeDInspectionRecipeLength { get; set; }
        public string ThreeDConsumerVersion { get; set; } = string.Empty;
        public string ThreeDConsumerCommit { get; set; } = string.Empty;
        public string ThreeDSequenceId { get; set; } = string.Empty;
        public string ThreeDStepId { get; set; } = string.Empty;
        public string ThreeDDeviceId { get; set; } = string.Empty;
        public string TcpListenAddress { get; set; } = "127.0.0.1";
        public int TcpListenPort { get; set; } = 45101;
        public string TcpPeerHost { get; set; } = "127.0.0.1";
        public int TcpPeerPort { get; set; } = 45102;
    }
}
