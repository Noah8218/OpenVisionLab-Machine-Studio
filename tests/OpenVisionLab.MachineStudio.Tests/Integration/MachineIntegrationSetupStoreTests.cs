using System.Net;
using OpenVisionLab.MachineStudio.ViewModel;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

public sealed class MachineIntegrationSetupStoreTests
{
    [Fact]
    public void SaveAndLoadPreserveSetupWithoutViewModel()
    {
        using var fixture = new TestRoot();
        var store = new MachineIntegrationSetupStore(fixture.SettingsPath);
        var setup = new MachineIntegrationSetup
        {
            ExchangeRoot = fixture.ExchangeRoot,
            InspectionRecipePath = Path.Combine(fixture.Root, "recipe.json"),
            TwoDConsumerVersion = "2.1.0",
            TwoDConsumerCommit = new string('2', 40),
            TcpListenAddress = IPAddress.Loopback.ToString(),
            TcpListenPort = 45111,
            TcpPeerHost = IPAddress.Loopback.ToString(),
            TcpPeerPort = 45112
        };

        store.Save(setup);
        var loaded = store.Load();

        Assert.Equal(MachineIntegrationSetupLoadWarning.None, loaded.Warning);
        Assert.Null(loaded.ErrorMessage);
        Assert.Equal(setup, loaded.Settings);
    }

    [Fact]
    public void InvalidSavedSetupReturnsDefaultsAndWarning()
    {
        using var fixture = new TestRoot();
        Directory.CreateDirectory(fixture.Root);
        File.WriteAllText(
            fixture.SettingsPath,
            "{\"TcpListenAddress\":\"not-an-ip\",\"TcpListenPort\":0}");
        var store = new MachineIntegrationSetupStore(fixture.SettingsPath);

        var loaded = store.Load();

        Assert.Equal(MachineIntegrationSetupLoadWarning.MissingOrInvalid, loaded.Warning);
        Assert.Equal(new MachineIntegrationSetup(), loaded.Settings);
    }

    [Fact]
    public void ResetRestoresDefaults()
    {
        using var fixture = new TestRoot();
        var store = new MachineIntegrationSetupStore(fixture.SettingsPath);
        store.Save(new MachineIntegrationSetup { ExchangeRoot = fixture.ExchangeRoot });

        store.Reset();

        Assert.Equal(new MachineIntegrationSetup(), store.Load().Settings);
    }

    [Fact]
    public void SavedTcpSettingsMatchCurrentSnapshotAndRejectChanges()
    {
        using var fixture = new TestRoot();
        var store = new MachineIntegrationSetupStore(fixture.SettingsPath);
        store.Save(new MachineIntegrationSetup
        {
            ExchangeRoot = fixture.ExchangeRoot,
            TcpListenAddress = IPAddress.Loopback.ToString(),
            TcpListenPort = 45111,
            TcpPeerHost = "LOCALHOST",
            TcpPeerPort = 45112
        });

        var current = new MachineIntegrationTcpSettings(
            fixture.ExchangeRoot,
            IPAddress.Loopback,
            45111,
            "localhost",
            45112);

        Assert.True(store.MatchesSavedTcpSettings(current));
        Assert.False(store.MatchesSavedTcpSettings(current with { PeerPort = 45113 }));
    }

    private sealed class TestRoot : IDisposable
    {
        public TestRoot()
        {
            Root = Path.Combine(
                "D:\\OpenVisionLab-TestData\\OpenVisionLab-Machine-Studio",
                "machine-integration-setup-store-tests",
                Guid.NewGuid().ToString("N"));
            ExchangeRoot = Path.Combine(Root, "exchange");
            SettingsPath = Path.Combine(Root, "integration-exchange.json");
        }

        public string Root { get; }
        public string ExchangeRoot { get; }
        public string SettingsPath { get; }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
