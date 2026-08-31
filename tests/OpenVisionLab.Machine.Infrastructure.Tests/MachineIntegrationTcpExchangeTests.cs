using System.Net;
using System.Security.Cryptography;
using System.Text;
using OpenVisionLab.Integration.Contracts;
using OpenVisionLab.Integration.Transport.Tcp;
using OpenVisionLab.Machine.Infrastructure.Integration;
using Xunit;

namespace OpenVisionLab.Machine.Infrastructure.Tests;

public sealed class MachineIntegrationTcpExchangeTests
{
    [Fact]
    public async Task PushAndPullTransferTheExistingTransactionWithoutCreatingResultMessages()
    {
        using var fixture = new TcpFixture();
        var key = SHA256.HashData(Encoding.UTF8.GetBytes("machine-tcp-original-test-key"));
        await using var receiver = new MachineIntegrationTcpExchange(fixture.ReceiverRoot, key);
        var endpoint = await receiver.StartListeningAsync(IPAddress.Loopback, 0);
        await using var sender = new MachineIntegrationTcpExchange(fixture.SenderRoot, key);
        var handoff = MachineIntegrationExchange.PublishHandoff(fixture.CreateRequest());
        var peer = new TcpIntegrationEndpoint(IPAddress.Loopback.ToString(), endpoint.Port);

        var push = await sender.PushTransactionAsync(peer, handoff.TransactionId);

        Assert.Equal("push", push.Operation);
        Assert.Equal(handoff.TransactionId, push.TransactionId);
        Assert.Equal(IntegrationApplicationIds.MachineStudio, push.PeerApplicationId);
        var receiverTransaction = fixture.TransactionDirectory(fixture.ReceiverRoot, handoff.TransactionId);
        Assert.True(File.Exists(Path.Combine(receiverTransaction, IntegrationTransactionLayout.HandoffFileName)));
        Assert.True(File.Exists(Path.Combine(receiverTransaction, "artifacts", "project.ovmachine")));
        Assert.False(File.Exists(Path.Combine(receiverTransaction, IntegrationTransactionLayout.AcknowledgementFileName)));
        Assert.False(File.Exists(Path.Combine(receiverTransaction, IntegrationTransactionLayout.ResultFileName)));

        var pullerRoot = fixture.CreatePeerRoot("puller");
        await using var puller = new MachineIntegrationTcpExchange(pullerRoot, key);
        var pull = await puller.PullTransactionAsync(peer, handoff.TransactionId);

        Assert.Equal("pull", pull.Operation);
        Assert.Equal(handoff.TransactionId, pull.TransactionId);
        var pulledTransaction = fixture.TransactionDirectory(pullerRoot, handoff.TransactionId);
        Assert.Equal(
            File.ReadAllBytes(Path.Combine(receiverTransaction, IntegrationTransactionLayout.HandoffFileName)),
            File.ReadAllBytes(Path.Combine(pulledTransaction, IntegrationTransactionLayout.HandoffFileName)));
        Assert.False(File.Exists(Path.Combine(pulledTransaction, IntegrationTransactionLayout.AcknowledgementFileName)));
        Assert.False(File.Exists(Path.Combine(pulledTransaction, IntegrationTransactionLayout.ResultFileName)));
    }

    [Fact]
    public async Task WrongKeyIsRejectedAndTheListenerCanRestart()
    {
        using var fixture = new TcpFixture();
        var key = SHA256.HashData(Encoding.UTF8.GetBytes("machine-tcp-original-test-key"));
        var wrongKey = SHA256.HashData(Encoding.UTF8.GetBytes("wrong-machine-tcp-original-key"));
        await using var receiver = new MachineIntegrationTcpExchange(fixture.ReceiverRoot, key);
        var endpoint = await receiver.StartListeningAsync(IPAddress.Loopback, 0);
        await using var wrongClient = new MachineIntegrationTcpExchange(fixture.SenderRoot, wrongKey);

        await Assert.ThrowsAsync<TcpIntegrationTransportException>(() =>
            wrongClient.PingAsync(new TcpIntegrationEndpoint(IPAddress.Loopback.ToString(), endpoint.Port)));

        await receiver.StopListeningAsync();
        Assert.Null(receiver.LocalEndpoint);
        var restarted = await receiver.StartListeningAsync(IPAddress.Loopback, 0);
        Assert.NotEqual(0, restarted.Port);
    }

    private sealed class TcpFixture : IDisposable
    {
        private readonly string root = Path.Combine(
            "D:\\OpenVisionLab-TestData\\OpenVisionLab-Machine-Studio",
            "original-machine-integration-tcp-tests",
            Guid.NewGuid().ToString("N"));

        public TcpFixture()
        {
            SenderRoot = CreatePeerRoot("sender");
            ReceiverRoot = CreatePeerRoot("receiver");
            SourceRoot = Path.Combine(root, "source");
            Directory.CreateDirectory(SourceRoot);
            ProjectPath = Path.Combine(SourceRoot, "project.ovmachine");
            SourcePath = Path.Combine(SourceRoot, "frame.c3d");
            File.WriteAllText(ProjectPath, "{}", new UTF8Encoding(false));
            File.WriteAllBytes(SourcePath, [1, 2, 3, 4]);
        }

        public string SenderRoot { get; }
        public string ReceiverRoot { get; }
        private string SourceRoot { get; }
        private string ProjectPath { get; }
        private string SourcePath { get; }

        public string CreatePeerRoot(string name)
        {
            var path = Path.Combine(root, name);
            Directory.CreateDirectory(path);
            return path;
        }

        public string TransactionDirectory(string exchangeRoot, Guid transactionId) =>
            Path.Combine(
                exchangeRoot,
                IntegrationTransactionLayout.TransactionsDirectoryName,
                transactionId.ToString("D"));

        public MachineHandoffRequest CreateRequest() => new(
            SenderRoot,
            new IntegrationApplicationIdentity(
                IntegrationApplicationIds.MachineStudio,
                "0.2.0-dev",
                new string('1', 40),
                IntegrationSourceState.Clean),
            "project-1",
            "machine-project/1.0",
            "sequence-1",
            "step-1",
            "camera-1",
            "mm",
            "camera-frame",
            [
                new(
                    IntegrationArtifactRoles.MachineProject,
                    "project-1",
                    ProjectPath,
                    "project.ovmachine"),
                new(
                    IntegrationArtifactRoles.InspectionSource,
                    "frame-1",
                    SourcePath,
                    "frame.c3d")
            ]);

        public void Dispose()
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
