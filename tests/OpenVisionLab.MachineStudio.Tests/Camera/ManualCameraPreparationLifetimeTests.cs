using OpenVisionLab.MachineStudio.ViewModel;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

public sealed class ManualCameraPreparationLifetimeTests
{
    [Fact]
    public void InvalidateCancelsPreviousTokenAndCreatesFreshToken()
    {
        using var lifetime = new ManualCameraPreparationLifetime();
        var previous = lifetime.Token;

        lifetime.Invalidate();

        var current = lifetime.Token;
        Assert.True(previous.IsCancellationRequested);
        Assert.False(current.IsCancellationRequested);
        Assert.NotEqual(previous, current);
    }

    [Fact]
    public void CancelCancelsCurrentTokenWithoutReplacingIt()
    {
        using var lifetime = new ManualCameraPreparationLifetime();
        var current = lifetime.Token;

        lifetime.Cancel();

        Assert.True(current.IsCancellationRequested);
        Assert.Equal(current, lifetime.Token);
    }

    [Fact]
    public void DisposeCancelsCurrentTokenAndRejectsFuturePreparation()
    {
        var lifetime = new ManualCameraPreparationLifetime();
        var current = lifetime.Token;

        lifetime.Dispose();

        Assert.True(current.IsCancellationRequested);
        Assert.Throws<ObjectDisposedException>(() => _ = lifetime.Token);
        lifetime.Cancel();
        lifetime.Invalidate();
    }
}
