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
