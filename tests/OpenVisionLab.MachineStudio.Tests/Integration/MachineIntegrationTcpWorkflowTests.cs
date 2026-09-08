using System.Net;
using System.Security.Cryptography;
using System.Text;
using OpenVisionLab.MachineStudio.ViewModel;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

public sealed class MachineIntegrationTcpWorkflowTests
{
    [Fact]
    public async Task ListenerLifecycleIsOwnedByWorkflow()
    {
        using var fixture = new TestRoot();
        await using var workflow = new MachineIntegrationTcpWorkflow();
        var key = SHA256.HashData(Encoding.UTF8.GetBytes("tcp-workflow-key"));

        var endpoint = await workflow.StartListeningAsync(
            fixture.ExchangeRoot,
            IPAddress.Loopback,
            0,
            key);

        Assert.True(workflow.IsListening);
        Assert.NotEqual(0, endpoint.Port);

        await workflow.StopListeningAsync();

        Assert.False(workflow.IsListening);
    }

    [Fact]
    public async Task DisposeReleasesListenerOwnedByWorkflow()
    {
        using var fixture = new TestRoot();
        var workflow = new MachineIntegrationTcpWorkflow();
        var key = SHA256.HashData(Encoding.UTF8.GetBytes("tcp-workflow-dispose-key"));
        await workflow.StartListeningAsync(fixture.ExchangeRoot, IPAddress.Loopback, 0, key);

        await workflow.DisposeAsync();

        Assert.False(workflow.IsListening);
        await workflow.DisposeAsync();
    }

    [Fact]
    public async Task ConcurrentStartsReserveOneListener()
    {
        using var fixture = new TestRoot();
        await using var workflow = new MachineIntegrationTcpWorkflow();
        var key = SHA256.HashData(Encoding.UTF8.GetBytes("tcp-workflow-concurrency-key"));

        var first = workflow.StartListeningAsync(
            fixture.ExchangeRoot,
            IPAddress.Loopback,
            0,
            key);
        var second = workflow.StartListeningAsync(
            fixture.ExchangeRoot,
            IPAddress.Loopback,
            0,
            key);

        var firstException = await Record.ExceptionAsync(() => first);
        var secondException = await Record.ExceptionAsync(() => second);

        Assert.Equal(1, (firstException is null ? 1 : 0) + (secondException is null ? 1 : 0));
        Assert.IsType<InvalidOperationException>(firstException ?? secondException);
        Assert.True(workflow.IsListening);

        await workflow.StopListeningAsync();
    }

    [Fact]
    public async Task DisposedWorkflowRejectsNewListenerStart()
    {
        using var fixture = new TestRoot();
        await using var workflow = new MachineIntegrationTcpWorkflow();
        var key = SHA256.HashData(Encoding.UTF8.GetBytes("tcp-workflow-post-dispose-key"));

        await workflow.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            workflow.StartListeningAsync(
                fixture.ExchangeRoot,
                IPAddress.Loopback,
                0,
                key));
        Assert.False(workflow.IsListening);
    }

    private sealed class TestRoot : IDisposable
    {
        public TestRoot()
        {
            Root = Path.Combine(
                "D:\\OpenVisionLab-TestData\\OpenVisionLab-Machine-Studio",
                "machine-integration-tcp-workflow-tests",
                Guid.NewGuid().ToString("N"));
            ExchangeRoot = Path.Combine(Root, "exchange");
            Directory.CreateDirectory(ExchangeRoot);
        }

        private string Root { get; }
        public string ExchangeRoot { get; }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
