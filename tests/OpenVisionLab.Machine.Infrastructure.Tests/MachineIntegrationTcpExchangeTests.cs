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
    public async Task PushPullAreAuthenticatedAndReceiptDoesNotCreateDomainMessages()
    {
        using var fixture = new TcpFixture();
        var key = SHA256.HashData(Encoding.UTF8.GetBytes("machine-tcp-test-key"));
        await using var receiver = new MachineIntegrationTcpExchange(fixture.ReceiverRoot, key);
        var endpoint = await receiver.StartListeningAsync(IPAddress.Loopback, 0);
        await using var sender = new MachineIntegrationTcpExchange(fixture.SenderRoot, key);

        var handoff = await MachineIntegrationHandoffPublisher.PublishAsync(
            fixture.SenderRoot,
            fixture.CreateRequest());
        var peer = new TcpIntegrationEndpoint(IPAddress.Loopback.ToString(), endpoint.Port);

        var push = await sender.PushTransactionAsync(peer, handoff.TransactionId);

        Assert.Equal("push", push.Operation);
        Assert.Equal(handoff.TransactionId, push.TransactionId);
        Assert.Equal(IntegrationApplicationIds.MachineStudio, push.PeerApplicationId);
        var received = Assert.Single(receiver.DiscoverTransactions());
        Assert.Equal(handoff.TransactionId, received.Handoff.TransactionId);
        Assert.False(received.HasAcknowledgement);
        Assert.False(received.HasResult);
        Assert.Equal(
            IntegrationContractJson.SerializeCanonical(handoff),
            IntegrationContractJson.SerializeCanonical(
                receiver.ReadHandoff(handoff.TransactionId)));

        var pull = await sender.PullTransactionAsync(peer, handoff.TransactionId);
        Assert.Equal("pull", pull.Operation);
        Assert.Equal(handoff.TransactionId, pull.TransactionId);
        Assert.Equal(
            IntegrationContractJson.SerializeCanonical(handoff),
            IntegrationContractJson.SerializeCanonical(
                sender.ReadHandoff(handoff.TransactionId)));
    }

    [Fact]
    public async Task WrongKeyIsRejectedAndListenerCanRestartWithoutImplicitActions()
    {
        using var fixture = new TcpFixture();
        var key = SHA256.HashData(Encoding.UTF8.GetBytes("machine-tcp-test-key"));
        var wrongKey = SHA256.HashData(Encoding.UTF8.GetBytes("wrong-machine-tcp-key"));
        await using var receiver = new MachineIntegrationTcpExchange(fixture.ReceiverRoot, key);
        var endpoint = await receiver.StartListeningAsync(IPAddress.Loopback, 0);
        await using var wrongClient = new MachineIntegrationTcpExchange(fixture.SenderRoot, wrongKey);

        await Assert.ThrowsAsync<TcpIntegrationTransportException>(() =>
            wrongClient.PingAsync(
                new TcpIntegrationEndpoint(IPAddress.Loopback.ToString(), endpoint.Port)));

        await receiver.StopListeningAsync();
        Assert.Null(receiver.LocalEndpoint);
        var restarted = await receiver.StartListeningAsync(IPAddress.Loopback, 0);
        Assert.NotEqual(0, restarted.Port);
        Assert.Empty(receiver.DiscoverTransactions());
    }

    private sealed class TcpFixture : IDisposable
    {
        public TcpFixture()
        {
            Root = Path.Combine(
                "D:\\OpenVisionLab-TestData\\OpenVisionLab-Machine-Studio",
                "machine-integration-tcp-tests",
                Guid.NewGuid().ToString("N"));
            SenderRoot = Path.Combine(Root, "sender");
            ReceiverRoot = Path.Combine(Root, "receiver");
            SourceRoot = Path.Combine(Root, "source");
            Directory.CreateDirectory(SourceRoot);
            Directory.CreateDirectory(SenderRoot);
            Directory.CreateDirectory(ReceiverRoot);
            ProjectPath = Path.Combine(SourceRoot, "machine.ovmachine");
            SourcePath = Path.Combine(SourceRoot, "inspection-source.png");
            RecipePath = Path.Combine(SourceRoot, "inspection-recipe.json");
            File.WriteAllText(ProjectPath, "{\"schema\":\"machine-project/1.0\"}", new UTF8Encoding(false));
            File.WriteAllBytes(SourcePath, [0x89, 0x50, 0x4E, 0x47, 0x00]);
            File.WriteAllText(RecipePath, "{\"tool\":\"local\"}", new UTF8Encoding(false));
        }

        private string Root { get; }
        public string SenderRoot { get; }
        public string ReceiverRoot { get; }
        private string SourceRoot { get; }
        private string ProjectPath { get; }
        private string SourcePath { get; }
        private string RecipePath { get; }

        public MachineInspectionHandoffRequest CreateRequest() => new(
            "machine-tcp-project",
            "machine-project/1.0",
            "sequence-001",
            "inspect-image",
            "camera-virtual",
            "acquisition-1",
            "frame-1",
            "mm",
            ProjectPath,
            SourcePath,
            RecipePath,
            IntegrationInspectionModality.TwoD,
            IntegrationInspectionInputKind.Image,
            new IntegrationApplicationIdentity(
                IntegrationApplicationIds.MachineStudio,
                "0.1.0-test",
                new string('1', 40),
                IntegrationSourceState.Clean),
            new IntegrationApplicationIdentity(
                IntegrationApplicationIds.TwoDStudio,
                "2.1.0-test",
                new string('2', 40),
                IntegrationSourceState.Clean));

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
