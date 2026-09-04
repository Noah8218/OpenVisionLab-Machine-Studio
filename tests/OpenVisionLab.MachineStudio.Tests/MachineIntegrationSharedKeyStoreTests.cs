using System.Security.Cryptography;
using System.Text;
using OpenVisionLab.MachineStudio.ViewModel;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

public sealed class MachineIntegrationSharedKeyStoreTests
{
    [Fact]
    public void SessionKeyIsCopiedAndNeverFallsBackWhenSessionInputIsInvalid()
    {
        var key = SHA256.HashData(Encoding.UTF8.GetBytes("shared-key-session"));
        using var store = new MachineIntegrationSharedKeyStore(() =>
            Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes("environment-key"))));

        Assert.Equal(
            MachineIntegrationSharedKeyStatus.SessionReady,
            store.SetSessionKey(Convert.ToBase64String(key)));
        var acquired = Assert.IsType<byte[]>(store.TryAcquire());
        Assert.Equal(key, acquired);
        CryptographicOperations.ZeroMemory(acquired);

        var secondAcquisition = Assert.IsType<byte[]>(store.TryAcquire());
        Assert.Equal(key, secondAcquisition);
        CryptographicOperations.ZeroMemory(secondAcquisition);

        Assert.Equal(
            MachineIntegrationSharedKeyStatus.SessionMalformed,
            store.SetSessionKey("not-base64"));
        Assert.Null(store.TryAcquire());
        Assert.Equal(MachineIntegrationSharedKeyStatus.SessionMalformed, store.Status);
    }

    [Fact]
    public void EmptySessionInputUsesEnvironmentKey()
    {
        var key = SHA256.HashData(Encoding.UTF8.GetBytes("shared-key-environment"));
        var encoded = Convert.ToBase64String(key);
        using var store = new MachineIntegrationSharedKeyStore(() => encoded);

        Assert.Equal(MachineIntegrationSharedKeyStatus.EnvironmentReady, store.Status);
        Assert.Equal(MachineIntegrationSharedKeyStatus.EnvironmentReady, store.SetSessionKey(null));
        var acquired = Assert.IsType<byte[]>(store.TryAcquire());

        Assert.Equal(key, acquired);
        CryptographicOperations.ZeroMemory(acquired);
    }

    [Fact]
    public void EnvironmentStatusDistinguishesMissingShortAndMalformedValues()
    {
        string? encoded = null;
        using var store = new MachineIntegrationSharedKeyStore(() => encoded);

        Assert.Equal(MachineIntegrationSharedKeyStatus.Missing, store.Status);
        encoded = Convert.ToBase64String(new byte[1]);
        Assert.Equal(MachineIntegrationSharedKeyStatus.EnvironmentTooShort, store.Status);
        encoded = "not-base64";
        Assert.Equal(MachineIntegrationSharedKeyStatus.EnvironmentMalformed, store.Status);
        Assert.Null(store.TryAcquire());
    }

    [Fact]
    public void DisposeRejectsFurtherKeyAccess()
    {
        var store = new MachineIntegrationSharedKeyStore(() => null);
        store.Dispose();

        Assert.Throws<ObjectDisposedException>(() => _ = store.Status);
        Assert.Throws<ObjectDisposedException>(() => store.TryAcquire());
        Assert.Throws<ObjectDisposedException>(() => store.SetSessionKey(null));
        store.Dispose();
    }
}
