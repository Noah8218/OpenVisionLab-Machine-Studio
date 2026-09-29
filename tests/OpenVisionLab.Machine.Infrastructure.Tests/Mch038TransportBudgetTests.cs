using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenVisionLab.Integration.Transport.Tcp;
using OpenVisionLab.Machine.Infrastructure.Integration;
using OpenVisionLab.TestSupport;
using Xunit;

namespace OpenVisionLab.Machine.Infrastructure.Tests;

public sealed class Mch038TransportBudgetTests
{
    [Fact]
    public async Task PushRejectsFileCountBeforeNetworkPublication()
    {
        using var fixture = new TcpBudgetFixture();
        var transactionId = fixture.CreateSourceTransaction(
            ("handoff.json", [0x01]),
            ("artifacts/a.bin", [0x02]),
            ("artifacts/b.bin", [0x03]));
        var receiverOptions = new TcpIntegrationOptions { MaxAttempts = 1 };
        var senderOptions = new TcpIntegrationOptions
        {
            MaxFiles = 2,
            MaxFileBytes = 1_024,
            MaxTransactionBytes = 2_048,
            MaxAttempts = 1
        };
        await using var receiver = new MachineIntegrationTcpExchange(
            fixture.ReceiverRoot,
            fixture.SharedKey,
            receiverOptions);
        var endpoint = await receiver.StartListeningAsync(IPAddress.Loopback, 0);
        await using var sender = new MachineIntegrationTcpExchange(
            fixture.SenderRoot,
            fixture.SharedKey,
            senderOptions);

        var exception = await Assert.ThrowsAsync<TcpIntegrationTransportException>(() =>
            sender.PushTransactionAsync(
                new TcpIntegrationEndpoint(IPAddress.Loopback.ToString(), endpoint.Port),
                transactionId));

        Assert.Equal("fileCountExceeded", exception.Code);
        Assert.Empty(receiver.DiscoverTransactions());
        Assert.Empty(EnumerateStagingDirectories(fixture.ReceiverRoot));
    }

    [Fact]
    public async Task PushRejectsTransactionBytesBeforeNetworkPublication()
    {
        using var fixture = new TcpBudgetFixture();
        var transactionId = fixture.CreateSourceTransaction(
            ("handoff.json", [0x01]),
            ("artifacts/a.bin", [0x02, 0x03, 0x04, 0x05, 0x06]),
            ("artifacts/b.bin", [0x07, 0x08, 0x09, 0x0A, 0x0B]));
        var receiverOptions = new TcpIntegrationOptions { MaxAttempts = 1 };
        var senderOptions = new TcpIntegrationOptions
        {
            MaxFiles = 10,
            MaxFileBytes = 8,
            MaxTransactionBytes = 10,
            MaxAttempts = 1
        };
        await using var receiver = new MachineIntegrationTcpExchange(
            fixture.ReceiverRoot,
            fixture.SharedKey,
            receiverOptions);
        var endpoint = await receiver.StartListeningAsync(IPAddress.Loopback, 0);
        await using var sender = new MachineIntegrationTcpExchange(
            fixture.SenderRoot,
            fixture.SharedKey,
            senderOptions);

        var exception = await Assert.ThrowsAsync<TcpIntegrationTransportException>(() =>
            sender.PushTransactionAsync(
                new TcpIntegrationEndpoint(IPAddress.Loopback.ToString(), endpoint.Port),
                transactionId));

        Assert.Equal("transactionSizeExceeded", exception.Code);
        Assert.Empty(receiver.DiscoverTransactions());
        Assert.Empty(EnumerateStagingDirectories(fixture.ReceiverRoot));
    }

    [Fact]
    public async Task ReceiverRejectsTraversalManifestBeforeCreatingStaging()
    {
        using var fixture = new TcpBudgetFixture();
        var options = new TcpIntegrationOptions
        {
            MaxAttempts = 1,
            IdleTimeout = TimeSpan.FromSeconds(2)
        };
        await using var receiver = new MachineIntegrationTcpExchange(
            fixture.ReceiverRoot,
            fixture.SharedKey,
            options);
        var endpoint = await receiver.StartListeningAsync(IPAddress.Loopback, 0);
        var transactionId = Guid.NewGuid();
        var response = await SendFrameAndReadResponseAsync(
            endpoint,
            fixture.SharedKey,
            transactionId,
            "../outside.bin",
            0,
            new string('0', 64));

        Assert.Equal("error", response.GetProperty("kind").GetString());
        Assert.Equal("unsafePath", response.GetProperty("errorCode").GetString());
        Assert.Empty(receiver.DiscoverTransactions());
        Assert.Empty(EnumerateStagingDirectories(fixture.ReceiverRoot));
    }

    [Fact]
    public async Task ReceiverRejectsInterruptedPayloadAndRemovesPartialStaging()
    {
        using var fixture = new TcpBudgetFixture();
        var options = new TcpIntegrationOptions
        {
            MaxAttempts = 1,
            IdleTimeout = TimeSpan.FromSeconds(2)
        };
        await using var receiver = new MachineIntegrationTcpExchange(
            fixture.ReceiverRoot,
            fixture.SharedKey,
            options);
        var endpoint = await receiver.StartListeningAsync(IPAddress.Loopback, 0);
        var transactionId = Guid.NewGuid();

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, endpoint.Port);
        await using var stream = client.GetStream();
        await WriteFrameAsync(
            stream,
            fixture.SharedKey,
            transactionId,
            "artifacts/partial.bin",
            32,
            new string('0', 64));
        await stream.WriteAsync(new byte[] { 0x01, 0x02, 0x03, 0x04 });
        client.Client.Shutdown(SocketShutdown.Send);

        var response = await ReadFrameAsync(stream);

        Assert.Equal("error", response.GetProperty("kind").GetString());
        Assert.Equal("connectionClosed", response.GetProperty("errorCode").GetString());
        Assert.Empty(receiver.DiscoverTransactions());
        Assert.Empty(EnumerateStagingDirectories(fixture.ReceiverRoot));
    }

    private static IReadOnlyList<string> EnumerateStagingDirectories(string exchangeRoot)
    {
        var transactionsRoot = Path.Combine(
            exchangeRoot,
            "transactions");
        return Directory.Exists(transactionsRoot)
            ? Directory.EnumerateDirectories(transactionsRoot, "*.tcp-staging").ToArray()
            : [];
    }

    private static async Task<JsonElement> SendFrameAndReadResponseAsync(
        IPEndPoint endpoint,
        byte[] sharedKey,
        Guid transactionId,
        string relativePath,
        long byteLength,
        string sha256)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(endpoint.Address, endpoint.Port);
        await using var stream = client.GetStream();
        await WriteFrameAsync(
            stream,
            sharedKey,
            transactionId,
            relativePath,
            byteLength,
            sha256);
        return await ReadFrameAsync(stream);
    }

    private static async Task WriteFrameAsync(
        Stream stream,
        byte[] sharedKey,
        Guid transactionId,
        string relativePath,
        long byteLength,
        string sha256)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(
            new
            {
                ProtocolVersion = "1.0",
                Kind = "push",
                RequestId = Guid.NewGuid(),
                CreatedAtUtc = DateTimeOffset.UtcNow,
                Nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16)),
                ReplyToNonce = (string?)null,
                ApplicationId = "OpenVisionLab.2DStudio",
                TransactionId = transactionId,
                Files = new[]
                {
                    new
                    {
                        RelativePath = relativePath,
                        ByteLength = byteLength,
                        Sha256 = sha256
                    }
                },
                FilesTransferred = 0,
                BytesTransferred = 0L,
                Idempotent = false,
                ErrorCode = (string?)null,
                ErrorMessage = (string?)null,
                Retryable = false
            },
            JsonOptions);
        var length = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(length, json.Length);
        var frame = new byte[8 + length.Length + json.Length + 32];
        "OVLTCP01"u8.CopyTo(frame);
        length.CopyTo(frame, 8);
        json.CopyTo(frame, 8 + length.Length);
        HMACSHA256.HashData(sharedKey, json)
            .CopyTo(frame, 8 + length.Length + json.Length);
        await stream.WriteAsync(frame);
        await stream.FlushAsync();
    }

    private static async Task<JsonElement> ReadFrameAsync(Stream stream)
    {
        var magic = new byte[8];
        await stream.ReadExactlyAsync(magic);
        Assert.Equal("OVLTCP01"u8.ToArray(), magic);
        var lengthBytes = new byte[sizeof(int)];
        await stream.ReadExactlyAsync(lengthBytes);
        var length = BinaryPrimitives.ReadInt32BigEndian(lengthBytes);
        Assert.True(length > 0);
        var json = new byte[length];
        await stream.ReadExactlyAsync(json);
        var tag = new byte[32];
        await stream.ReadExactlyAsync(tag);
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Default,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    private sealed class TcpBudgetFixture : IDisposable
    {
        public TcpBudgetFixture()
        {
            Root = Path.Combine(
                TestStorage.RootPath,
                "mch-038-transport-budget-tests",
                Guid.NewGuid().ToString("N"));
            SenderRoot = Path.Combine(Root, "sender");
            ReceiverRoot = Path.Combine(Root, "receiver");
            Directory.CreateDirectory(SenderRoot);
            Directory.CreateDirectory(ReceiverRoot);
            SharedKey = SHA256.HashData(
                Encoding.UTF8.GetBytes("mch-038-transport-budget-test-key"));
        }

        public string Root { get; }

        public string SenderRoot { get; }

        public string ReceiverRoot { get; }

        public byte[] SharedKey { get; }

        public Guid CreateSourceTransaction(
            params (string RelativePath, byte[] Bytes)[] files)
        {
            var transactionId = Guid.NewGuid();
            var directory = Path.Combine(
                SenderRoot,
                "transactions",
                transactionId.ToString("D"));
            foreach (var file in files)
            {
                var path = Path.Combine(
                    directory,
                    file.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, file.Bytes);
            }

            return transactionId;
        }

        public void Dispose()
        {
            CryptographicOperations.ZeroMemory(SharedKey);
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
