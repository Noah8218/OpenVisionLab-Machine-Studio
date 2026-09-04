using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenVisionLab.Integration.Contracts;
using OpenVisionLab.Integration.Transport.Tcp;
using OpenVisionLab.Machine.Infrastructure.Integration;
using Xunit;

namespace OpenVisionLab.Machine.Infrastructure.Tests;

public sealed class MachineIntegrationTcpFailureRecoveryTests
{
    [Fact]
    public async Task WrongSharedKey_FailsClosedWithoutCompletedReceiptOrTransaction()
    {
        using var fixture = new TcpFixture();
        await using var receiver = new MachineIntegrationTcpExchange(
            fixture.RemoteRoot,
            fixture.SharedKey);
        var completed = 0;
        receiver.RequestCompleted += _ => Interlocked.Increment(ref completed);
        var endpoint = await receiver.StartListeningAsync(IPAddress.Loopback, 0);
        using var client = new TcpIntegrationClient(
            IntegrationApplicationIds.MachineStudio,
            ToEndpoint(endpoint),
            fixture.WrongKey,
            Q6Options());

        var exception = await Record.ExceptionAsync(() => client.PingAsync());

        Assert.NotNull(exception);
        Assert.True(
            exception is IOException or SocketException or TimeoutException,
            exception.ToString());
        AssertNoPublishedTransaction(fixture.RemoteRoot);
        Assert.Equal(0, completed);
    }

    [Fact]
    public async Task WrongEndpoint_FailsWithoutCreatingLocalTransferState()
    {
        using var fixture = new TcpFixture();
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var endpoint = (IPEndPoint)listener.LocalEndpoint;
        listener.Stop();
        using var client = new TcpIntegrationClient(
            IntegrationApplicationIds.MachineStudio,
            ToEndpoint(endpoint),
            fixture.SharedKey,
            Q6Options(connectTimeout: TimeSpan.FromMilliseconds(250)));

        var exception = await Record.ExceptionAsync(() => client.PingAsync());

        Assert.NotNull(exception);
        Assert.True(
            exception is SocketException or TimeoutException or TcpIntegrationTransportException,
            exception.ToString());
        AssertNoPublishedTransaction(fixture.RemoteRoot);
    }

    [Fact]
    public async Task TamperedPayload_FailsClosedAndDeletesStaging()
    {
        using var fixture = new TcpFixture();
        var transactionId = fixture.CreatePublishedTransaction();
        await using var receiver = new MachineIntegrationTcpExchange(
            fixture.RemoteRoot,
            fixture.SharedKey);
        var completed = 0;
        receiver.RequestCompleted += _ => Interlocked.Increment(ref completed);
        var serverEndpoint = await receiver.StartListeningAsync(IPAddress.Loopback, 0);
        await using var proxy = TamperingProxy.Start(serverEndpoint);
        using var client = new TcpIntegrationClient(
            IntegrationApplicationIds.MachineStudio,
            ToEndpoint(proxy.Endpoint),
            fixture.SharedKey,
            Q6Options());

        var exception = await Record.ExceptionAsync(() =>
            client.PushTransactionAsync(fixture.SourceRoot, transactionId));

        var transportException = Assert.IsType<TcpIntegrationTransportException>(exception);
        Assert.True(
            transportException.Code is "artifactHashMismatch" or "connectionClosed",
            transportException.ToString());
        await proxy.Completion;
        AssertNoPublishedTransaction(fixture.RemoteRoot);
        Assert.Equal(0, completed);
    }

    [Fact]
    public async Task DuplicateTransaction_IsIdempotentWithoutAcknowledgementOrResult()
    {
        using var fixture = new TcpFixture();
        var transactionId = fixture.CreatePublishedTransaction();
        await using var receiver = new MachineIntegrationTcpExchange(
            fixture.RemoteRoot,
            fixture.SharedKey);
        var endpoint = await receiver.StartListeningAsync(IPAddress.Loopback, 0);
        await using var sender = new MachineIntegrationTcpExchange(
            fixture.SourceRoot,
            fixture.SharedKey);

        var first = await sender.PushTransactionAsync(
            ToEndpoint(endpoint),
            transactionId);
        var repeated = await sender.PushTransactionAsync(
            ToEndpoint(endpoint),
            transactionId);

        Assert.False(first.Idempotent);
        Assert.True(repeated.Idempotent);
        var transaction = Assert.Single(receiver.DiscoverTransactions());
        Assert.Equal(transactionId, transaction.Handoff.TransactionId);
        Assert.False(transaction.HasAcknowledgement);
        Assert.False(transaction.HasResult);
    }

    [Fact]
    public async Task StopAndStart_RestartsMachineListenerWithoutImplicitTransactionAction()
    {
        using var fixture = new TcpFixture();
        await using var receiver = new MachineIntegrationTcpExchange(
            fixture.RemoteRoot,
            fixture.SharedKey);
        await using var sender = new MachineIntegrationTcpExchange(
            fixture.SourceRoot,
            fixture.SharedKey);

        var first = await receiver.StartListeningAsync(IPAddress.Loopback, 0);
        await sender.PingAsync(ToEndpoint(first));
        await receiver.StopListeningAsync();
        Assert.Null(receiver.LocalEndpoint);

        var second = await receiver.StartListeningAsync(IPAddress.Loopback, 0);
        await sender.PingAsync(ToEndpoint(second));

        AssertNoPublishedTransaction(fixture.RemoteRoot);
    }

    [Fact]
    public async Task Cancellation_StopsAnInFlightPingWithoutPublishing()
    {
        using var fixture = new TcpFixture();
        await using var holding = HoldingListener.Start();
        using var client = new TcpIntegrationClient(
            IntegrationApplicationIds.MachineStudio,
            ToEndpoint(holding.Endpoint),
            fixture.SharedKey,
            Q6Options());
        using var cancellation = new CancellationTokenSource();
        var operation = client.PingAsync(cancellation.Token);
        await holding.WaitForConnectionAsync();

        cancellation.Cancel();

        var exception = await Record.ExceptionAsync(() => operation);

        Assert.IsAssignableFrom<OperationCanceledException>(exception);
        AssertNoPublishedTransaction(fixture.RemoteRoot);
    }

    [Fact]
    public async Task IdleTimeout_StopsAnUnresponsivePeerWithBoundedFailure()
    {
        using var fixture = new TcpFixture();
        await using var holding = HoldingListener.Start();
        using var client = new TcpIntegrationClient(
            IntegrationApplicationIds.MachineStudio,
            ToEndpoint(holding.Endpoint),
            fixture.SharedKey,
            Q6Options(idleTimeout: TimeSpan.FromMilliseconds(150)));
        var operation = client.PingAsync();
        await holding.WaitForConnectionAsync();

        var exception = await Record.ExceptionAsync(() => operation);

        Assert.IsType<TimeoutException>(exception);
        AssertNoPublishedTransaction(fixture.RemoteRoot);
    }

    private static TcpIntegrationEndpoint ToEndpoint(IPEndPoint endpoint) =>
        new(endpoint.Address.ToString(), endpoint.Port);

    private static TcpIntegrationOptions Q6Options(
        TimeSpan? connectTimeout = null,
        TimeSpan? idleTimeout = null) =>
        new()
        {
            MaxAttempts = 1,
            ConnectTimeout = connectTimeout ?? TimeSpan.FromSeconds(1),
            IdleTimeout = idleTimeout ?? TimeSpan.FromSeconds(1)
        };

    private static void AssertNoPublishedTransaction(string exchangeRoot)
    {
        var transactionsRoot = Path.Combine(
            exchangeRoot,
            IntegrationTransactionLayout.TransactionsDirectoryName);
        if (!Directory.Exists(transactionsRoot))
        {
            return;
        }

        var published = Directory.EnumerateDirectories(transactionsRoot)
            .Where(path => Guid.TryParse(Path.GetFileName(path), out _))
            .ToArray();
        Assert.Empty(published);
        Assert.Empty(Directory.EnumerateDirectories(transactionsRoot, "*.tcp-staging"));
    }

    private sealed class TcpFixture : IDisposable
    {
        public TcpFixture()
        {
            Root = Path.Combine(
                "D:\\OpenVisionLab-TestData\\OpenVisionLab-Machine-Studio",
                "q6-failure-recovery",
                Guid.NewGuid().ToString("N"));
            SourceRoot = Path.Combine(Root, "source");
            RemoteRoot = Path.Combine(Root, "remote");
            Directory.CreateDirectory(SourceRoot);
            Directory.CreateDirectory(RemoteRoot);
            SharedKey = SHA256.HashData(Encoding.UTF8.GetBytes("q6-shared-key"));
            WrongKey = SHA256.HashData(Encoding.UTF8.GetBytes("q6-wrong-key"));
        }

        public string Root { get; }

        public string SourceRoot { get; }

        public string RemoteRoot { get; }

        public byte[] SharedKey { get; }

        public byte[] WrongKey { get; }

        public Guid CreatePublishedTransaction()
        {
            var projectPath = Write(
                "machine.ovmachine",
                Encoding.UTF8.GetBytes("{\"schema\":\"machine-project/1.0\"}"));
            var imagePath = Write("inspection.png", [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A]);
            var recipePath = Write(
                "inspection-recipe.json",
                Encoding.UTF8.GetBytes("{\"tool\":\"q6\"}"));
            var consumer = new IntegrationApplicationIdentity(
                IntegrationApplicationIds.TwoDStudio,
                "2.1.0",
                new string('2', 40),
                IntegrationSourceState.Clean);
            var artifacts = new[]
            {
                Artifact(
                    IntegrationArtifactRoles.MachineProject,
                    "machine-project",
                    projectPath,
                    "artifacts/machine.ovmachine"),
                Artifact(
                    IntegrationArtifactRoles.InspectionSource,
                    "inspection-source",
                    imagePath,
                    "artifacts/inspection.png"),
                Artifact(
                    IntegrationArtifactRoles.InspectionRecipe,
                    "inspection-recipe",
                    recipePath,
                    "artifacts/inspection-recipe.json")
            };
            var handoff = new IntegrationHandoffV2(
                IntegrationContractSchema.V2,
                IntegrationMessageKind.Handoff,
                Guid.NewGuid(),
                Guid.NewGuid(),
                DateTimeOffset.UtcNow,
                new IntegrationApplicationIdentity(
                    IntegrationApplicationIds.MachineStudio,
                    "2.1.0",
                    new string('1', 40),
                    IntegrationSourceState.Clean),
                new IntegrationInspectionContextV2(
                    "project-1",
                    "machine-project/1.0",
                    "sequence-1",
                    "inspect-step",
                    "camera-virtual",
                    "acquisition-1",
                    "frame-1",
                    "px",
                    IntegrationInspectionModality.TwoD,
                    IntegrationInspectionInputKind.Image,
                    Hash(imagePath),
                    Hash(recipePath),
                    consumer,
                    artifacts));

            MachineIntegrationExchange.PublishHandoff(
                SourceRoot,
                handoff,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["machine-project"] = projectPath,
                    ["inspection-source"] = imagePath,
                    ["inspection-recipe"] = recipePath
                });
            return handoff.TransactionId;
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }

            CryptographicOperations.ZeroMemory(SharedKey);
            CryptographicOperations.ZeroMemory(WrongKey);
        }

        private string Write(string name, byte[] bytes)
        {
            var path = Path.Combine(SourceRoot, name);
            File.WriteAllBytes(path, bytes);
            return path;
        }

        private static IntegrationArtifactReference Artifact(
            string role,
            string id,
            string sourcePath,
            string relativePath)
        {
            var info = new FileInfo(sourcePath);
            return new(
                role,
                id,
                relativePath,
                info.Length,
                Hash(sourcePath));
        }

        private static string Hash(string path) =>
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    }

    private sealed class HoldingListener : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly Task<TcpClient> _accepted;
        private TcpClient? _connection;

        private HoldingListener(TcpListener listener)
        {
            _listener = listener;
            _accepted = listener.AcceptTcpClientAsync();
        }

        public IPEndPoint Endpoint => (IPEndPoint)_listener.LocalEndpoint;

        public static HoldingListener Start()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return new(listener);
        }

        public async Task WaitForConnectionAsync()
        {
            _connection = await _accepted.WaitAsync(TimeSpan.FromSeconds(2));
        }

        public async ValueTask DisposeAsync()
        {
            _listener.Stop();
            _connection?.Dispose();
            try
            {
                var connection = await _accepted.ConfigureAwait(false);
                if (!ReferenceEquals(connection, _connection))
                {
                    connection.Dispose();
                }
            }
            catch (Exception exception) when (
                exception is SocketException or ObjectDisposedException)
            {
            }
        }
    }

    private sealed class TamperingProxy : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly IPEndPoint _target;
        private readonly CancellationTokenSource _shutdown = new();
        private readonly Task _completion;

        private TamperingProxy(TcpListener listener, IPEndPoint target)
        {
            _listener = listener;
            _target = target;
            _completion = RunAsync();
        }

        public IPEndPoint Endpoint => (IPEndPoint)_listener.LocalEndpoint;

        public Task Completion => _completion;

        public static TamperingProxy Start(IPEndPoint target)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return new(listener, target);
        }

        public async ValueTask DisposeAsync()
        {
            _shutdown.Cancel();
            _listener.Stop();
            await _completion.ConfigureAwait(false);
            _shutdown.Dispose();
        }

        private async Task RunAsync()
        {
            try
            {
                using var client = await _listener.AcceptTcpClientAsync(_shutdown.Token);
                using var target = new TcpClient();
                await target.ConnectAsync(
                        _target.Address,
                        _target.Port,
                        _shutdown.Token)
                    .ConfigureAwait(false);
                await using var clientStream = client.GetStream();
                await using var targetStream = target.GetStream();
                var request = await ReadControlFrameAsync(
                        clientStream,
                        _shutdown.Token)
                    .ConfigureAwait(false);
                var payloadBytes = ReadPayloadBytes(request.Json);
                await targetStream.WriteAsync(
                        request.Frame.AsMemory(),
                        _shutdown.Token)
                    .ConfigureAwait(false);
                await targetStream.FlushAsync(_shutdown.Token).ConfigureAwait(false);
                var payload = new byte[checked((int)payloadBytes)];
                await clientStream.ReadExactlyAsync(
                        payload.AsMemory(),
                        _shutdown.Token)
                    .ConfigureAwait(false);
                if (payload.Length == 0)
                {
                    throw new InvalidDataException("The tamper test requires a payload.");
                }

                payload[0] ^= 0x01;
                await targetStream.WriteAsync(
                        payload.AsMemory(),
                        _shutdown.Token)
                    .ConfigureAwait(false);
                await targetStream.FlushAsync(_shutdown.Token).ConfigureAwait(false);
                var response = await ReadControlFrameAsync(
                        targetStream,
                        _shutdown.Token)
                    .ConfigureAwait(false);
                await clientStream.WriteAsync(
                        response.Frame.AsMemory(),
                        _shutdown.Token)
                    .ConfigureAwait(false);
                await clientStream.FlushAsync(_shutdown.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
            {
            }
            catch (IOException)
            {
                // The peer may reset the connection immediately after the error frame.
                // The client-side assertion and remote residue checks remain authoritative.
            }
            catch (SocketException)
            {
                // The peer may reset the connection immediately after the error frame.
                // The client-side assertion and remote residue checks remain authoritative.
            }
            catch (ObjectDisposedException)
            {
                // Listener shutdown can race with the proxy completion task.
            }
        }

        private static long ReadPayloadBytes(byte[] json)
        {
            using var document = JsonDocument.Parse(json);
            var total = 0L;
            foreach (var file in document.RootElement
                         .GetProperty("files")
                         .EnumerateArray())
            {
                total = checked(total + file.GetProperty("byteLength").GetInt64());
            }

            return total;
        }

        private static async Task<(byte[] Frame, byte[] Json)> ReadControlFrameAsync(
            Stream stream,
            CancellationToken cancellationToken)
        {
            var magic = new byte[8];
            await stream.ReadExactlyAsync(magic.AsMemory(), cancellationToken);
            if (!magic.AsSpan().SequenceEqual("OVLTCP01"u8))
            {
                throw new InvalidDataException("The TCP magic header was invalid.");
            }

            var lengthBytes = new byte[sizeof(int)];
            await stream.ReadExactlyAsync(lengthBytes.AsMemory(), cancellationToken);
            var length = BinaryPrimitives.ReadInt32BigEndian(lengthBytes);
            if (length <= 0)
            {
                throw new InvalidDataException("The TCP control frame length was invalid.");
            }

            var json = new byte[length];
            var tag = new byte[32];
            await stream.ReadExactlyAsync(json.AsMemory(), cancellationToken);
            await stream.ReadExactlyAsync(tag.AsMemory(), cancellationToken);
            var frame = new byte[magic.Length + lengthBytes.Length + length + tag.Length];
            magic.CopyTo(frame, 0);
            lengthBytes.CopyTo(frame, magic.Length);
            json.CopyTo(frame, magic.Length + lengthBytes.Length);
            tag.CopyTo(frame, magic.Length + lengthBytes.Length + length);
            return (frame, json);
        }
    }
}
