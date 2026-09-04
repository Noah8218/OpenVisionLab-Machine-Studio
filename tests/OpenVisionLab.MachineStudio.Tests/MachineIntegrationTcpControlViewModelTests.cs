using System.Net;
using System.Security.Cryptography;
using System.Text;
using OpenVisionLab.Machine.Infrastructure.Integration;
using OpenVisionLab.MachineStudio.ViewModel;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

public sealed class MachineIntegrationTcpControlViewModelTests
{
    [Fact]
    public async Task ListenerLifecycleRemainsOwnedByTcpControlViewModel()
    {
        using var fixture = new TestRoot();
        using var viewModel = CreateViewModel(fixture);

        viewModel.SetSessionSharedKey(CreateEncodedKey("tcp-control-lifecycle"));

        await viewModel.StartTcpListenerAsync();

        Assert.True(viewModel.IsTcpListening);
        Assert.False(viewModel.CanEditTcpSetup);

        await viewModel.StopTcpListenerAsync();

        Assert.False(viewModel.IsTcpListening);
        Assert.True(viewModel.CanEditTcpSetup);
    }

    [Fact]
    public async Task InvalidSettingsArePresentedWithoutStartingTransport()
    {
        var statuses = new List<string>();
        using var viewModel = new MachineIntegrationTcpControlViewModel(
            () => throw new InvalidOperationException("settings rejected"),
            () => null,
            () => Task.CompletedTask,
            statuses.Add);

        await viewModel.StartTcpListenerAsync();

        Assert.False(viewModel.IsTcpListening);
        Assert.False(viewModel.IsTcpBusy);
        Assert.Contains("settings rejected", statuses);
    }

    [Fact]
    public async Task ConcurrentTcpOperationsUseOneActiveOperation()
    {
        using var fixture = new TestRoot();
        var settingsStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowSettings = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var settingsCalls = 0;
        using var viewModel = new MachineIntegrationTcpControlViewModel(
            () =>
            {
                Interlocked.Increment(ref settingsCalls);
                settingsStarted.TrySetResult();
                allowSettings.Task.GetAwaiter().GetResult();
                return new MachineIntegrationTcpSettings(
                    fixture.ExchangeRoot,
                    IPAddress.Loopback,
                    0,
                    IPAddress.Loopback.ToString(),
                    45101);
            },
            () => null,
            () => Task.CompletedTask,
            _ => { });

        viewModel.SetSessionSharedKey(CreateEncodedKey("tcp-control-concurrency"));
        var firstOperation = Task.Run(() => viewModel.StartTcpListenerAsync());
        await settingsStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var secondOperation = viewModel.StartTcpListenerAsync();
        allowSettings.TrySetResult();
        await Task.WhenAll(firstOperation, secondOperation);

        Assert.Equal(1, settingsCalls);
        Assert.True(viewModel.IsTcpListening);

        await viewModel.StopTcpListenerAsync();
    }

    private static MachineIntegrationTcpControlViewModel CreateViewModel(TestRoot fixture) =>
        new(
            () => new MachineIntegrationTcpSettings(
                fixture.ExchangeRoot,
                IPAddress.Loopback,
                0,
                IPAddress.Loopback.ToString(),
                45101),
            () => null,
            () => Task.CompletedTask,
            _ => { });

    private static string CreateEncodedKey(string value) =>
        Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private sealed class TestRoot : IDisposable
    {
        internal TestRoot()
        {
            Root = Path.Combine(
                "D:\\OpenVisionLab-TestData\\OpenVisionLab-Machine-Studio",
                "machine-integration-tcp-control-tests",
                Guid.NewGuid().ToString("N"));
            ExchangeRoot = Path.Combine(Root, "exchange");
            Directory.CreateDirectory(ExchangeRoot);
        }

        private string Root { get; }
        internal string ExchangeRoot { get; }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
