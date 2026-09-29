using System.Net;
using OpenVisionLab.Machine.Infrastructure.Integration;
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
            WaitForExternalResult = true,
            UseThreeDHeightMap = true,
            ThreeDHeightMapSourcePath = Path.Combine(fixture.Root, "assets", "input.c3d"),
            ThreeDHeightMapSourceSha256 = new string('3', 64),
            ThreeDHeightMapSourceLength = 4096,
            ThreeDHeightMapWidth = 64,
            ThreeDHeightMapHeight = 32,
            ThreeDHeightMapPixelFormat = "C3D-HeightMap",
            ThreeDHeightMapUnit = "mm",
            ThreeDInspectionRecipePath = Path.Combine(fixture.Root, "recipes", "heightmap.json"),
            ThreeDInspectionRecipeSha256 = new string('4', 64),
            ThreeDInspectionRecipeLength = 512,
            ThreeDConsumerVersion = "0.2.0-dev",
            ThreeDConsumerCommit = new string('5', 40),
            ThreeDSequenceId = "sequence-001",
            ThreeDStepId = "inspect-1",
            ThreeDDeviceId = "camera-virtual",
            TcpListenAddress = IPAddress.Loopback.ToString(),
            TcpListenPort = 45111,
            TcpPeerHost = IPAddress.Loopback.ToString(),
            TcpPeerPort = 45112
        };

        store.Save(setup);
        var loaded = store.Load();

        Assert.True(Directory.Exists(fixture.ExchangeRoot));
        Assert.Equal(MachineIntegrationSetupLoadWarning.None, loaded.Warning);
        Assert.Null(loaded.ErrorMessage);
        Assert.Equal(setup, loaded.Settings);
    }

    [Fact]
    public void SaveAndLoadPreserveMmiRecipeCatalogOrder()
    {
        using var fixture = new TestRoot();
        var firstRecipe = Path.Combine(fixture.Root, "recipes", "first.json");
        var secondRecipe = Path.Combine(fixture.Root, "recipes", "second.json");
        var store = new MachineIntegrationSetupStore(fixture.SettingsPath);
        var setup = new MachineIntegrationSetup
        {
            ExchangeRoot = fixture.ExchangeRoot,
            InspectionRecipePath = secondRecipe,
            RecipeCatalogPaths = [firstRecipe, secondRecipe],
            TcpListenAddress = IPAddress.Loopback.ToString(),
            TcpListenPort = 45111,
            TcpPeerHost = IPAddress.Loopback.ToString(),
            TcpPeerPort = 45112
        };

        store.Save(setup);
        var loaded = store.Load();

        Assert.Equal(
            new[] { firstRecipe, secondRecipe },
            loaded.Settings.RecipeCatalogPaths);
        Assert.Equal(secondRecipe, loaded.Settings.InspectionRecipePath);
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
