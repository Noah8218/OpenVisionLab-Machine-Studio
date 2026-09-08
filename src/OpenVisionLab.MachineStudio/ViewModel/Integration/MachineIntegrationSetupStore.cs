using System.IO;
using System.Net;
using System.Text.Json;

namespace OpenVisionLab.MachineStudio.ViewModel;

internal enum MachineIntegrationSetupLoadWarning
{
    None,
    MissingOrInvalid,
    ReadFailed
}

internal sealed record MachineIntegrationSetupLoadResult(
    MachineIntegrationSetup Settings,
    MachineIntegrationSetupLoadWarning Warning,
    string? ErrorMessage);

internal sealed record MachineIntegrationSetup
{
    public string ExchangeRoot { get; init; } = string.Empty;
    public string InspectionRecipePath { get; init; } = string.Empty;
    public string TwoDConsumerVersion { get; init; } = string.Empty;
    public string TwoDConsumerCommit { get; init; } = string.Empty;
    public string TcpListenAddress { get; init; } = "127.0.0.1";
    public int TcpListenPort { get; init; } = 45101;
    public string TcpPeerHost { get; init; } = "127.0.0.1";
    public int TcpPeerPort { get; init; } = 45102;
}

internal sealed record MachineIntegrationTcpSettings(
    string ExchangeRoot,
    IPAddress ListenAddress,
    int ListenPort,
    string PeerHost,
    int PeerPort);

/// <summary>
/// Owns the file-backed Machine Studio integration setup format and its
/// atomic persistence. It has no WPF or ViewModel dependency.
/// </summary>
internal sealed class MachineIntegrationSetupStore
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
                    TwoDConsumerVersion = settings.TwoDConsumerVersion,
                    TwoDConsumerCommit = settings.TwoDConsumerCommit,
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
                        TwoDConsumerVersion = settings.TwoDConsumerVersion,
                        TwoDConsumerCommit = settings.TwoDConsumerCommit,
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
        public string TwoDConsumerVersion { get; set; } = string.Empty;
        public string TwoDConsumerCommit { get; set; } = string.Empty;
        public string TcpListenAddress { get; set; } = "127.0.0.1";
        public int TcpListenPort { get; set; } = 45101;
        public string TcpPeerHost { get; set; } = "127.0.0.1";
        public int TcpPeerPort { get; set; } = 45102;
    }
}
