using System.Net;
using System.Security.Cryptography;
using System.Text;
using OpenVisionLab.Integration.Contracts;
using OpenVisionLab.Integration.Transport.Tcp;
using OpenVisionLab.Machine.Infrastructure.Integration;
using OpenVisionLab.TestSupport;
using Xunit;

namespace OpenVisionLab.Machine.Infrastructure.Tests;

public sealed class Mch037TransactionRecoveryTests
{
    [Fact]
    public async Task Observe_DistinguishesPendingFailureCancellationAndCompletion()
    {
        using var fixture = new Fixture();
        await using var exchange = fixture.CreateExchange(fixture.LocalRoot);
        var recovery = new MachineIntegrationTransactionRecovery(exchange);

        var waitingHandoff = await fixture.PublishAsync(fixture.LocalRoot, "waiting");
        var waiting = recovery.Observe(waitingHandoff.TransactionId);
        Assert.Equal(MachineIntegrationRecoveryState.WaitingForAcknowledgement, waiting.State);
        Assert.True(waiting.CanRequestCancellation);
        Assert.False(waiting.IsExecutionFailure);

        var intent = MachineIntegrationTransactionRecovery.CreateCancellationIntent(
            waiting,
            "operator stopped waiting",
            DateTimeOffset.UtcNow);
        Assert.Equal(waiting.TransactionId, intent.TransactionId);
        Assert.True(intent.RequiresConsumerResultPublication);
        Assert.False(File.Exists(fixture.MessagePath(
            fixture.LocalRoot,
            waiting.TransactionId,
            IntegrationTransactionLayout.ResultFileName)));

        var awaitingHandoff = await fixture.PublishAsync(fixture.LocalRoot, "awaiting");
        var awaitingAcknowledgement = fixture.WriteAcknowledgement(
            fixture.LocalRoot,
            awaitingHandoff,
            IntegrationAcknowledgementStatus.Accepted);
        var awaiting = recovery.Observe(awaitingHandoff.TransactionId);
        Assert.Equal(MachineIntegrationRecoveryState.AwaitingResult, awaiting.State);
        Assert.True(awaiting.CanRequestCancellation);
        Assert.False(awaiting.CanRetry);

        var failedHandoff = await fixture.PublishAsync(fixture.LocalRoot, "failed");
        var failedAcknowledgement = fixture.WriteAcknowledgement(
            fixture.LocalRoot,
            failedHandoff,
            IntegrationAcknowledgementStatus.Accepted);
        var failedResult = fixture.WriteResult(
            fixture.LocalRoot,
            failedHandoff,
            failedAcknowledgement,
            IntegrationResultStatus.Failed,
            IntegrationInspectionOutcome.ExecutionError);
        var failed = recovery.Observe(failedHandoff.TransactionId);
        Assert.Equal(MachineIntegrationRecoveryState.Failed, failed.State);
        Assert.True(failed.IsExecutionFailure);
        Assert.True(failed.CanRetry);
        Assert.Equal(failedResult.MessageId, failed.Result!.MessageId);

        var cancelledHandoff = await fixture.PublishAsync(fixture.LocalRoot, "cancelled");
        var cancelledAcknowledgement = fixture.WriteAcknowledgement(
            fixture.LocalRoot,
            cancelledHandoff,
            IntegrationAcknowledgementStatus.Accepted);
        fixture.WriteResult(
            fixture.LocalRoot,
            cancelledHandoff,
            cancelledAcknowledgement,
            IntegrationResultStatus.Cancelled,
            IntegrationInspectionOutcome.Indeterminate);
        var cancelled = recovery.Observe(cancelledHandoff.TransactionId);
        Assert.Equal(MachineIntegrationRecoveryState.Cancelled, cancelled.State);
        Assert.True(cancelled.CanRetry);
        Assert.False(cancelled.IsExecutionFailure);

        var completedHandoff = await fixture.PublishAsync(fixture.LocalRoot, "completed");
        var completedAcknowledgement = fixture.WriteAcknowledgement(
            fixture.LocalRoot,
            completedHandoff,
            IntegrationAcknowledgementStatus.Accepted);
        fixture.WriteResult(
            fixture.LocalRoot,
            completedHandoff,
            completedAcknowledgement,
            IntegrationResultStatus.Completed,
            IntegrationInspectionOutcome.Pass);
        var completed = recovery.Observe(completedHandoff.TransactionId);
        Assert.Equal(MachineIntegrationRecoveryState.Completed, completed.State);
        Assert.True(completed.IsTerminal);
        Assert.False(completed.CanRetry);
        Assert.False(completed.CanRequestCancellation);
    }

    [Fact]
    public async Task ReconnectAfterPeerRestart_RecoversSameTransactionResultWithoutRerun()
    {
        using var fixture = new Fixture();
        await using var receiver = fixture.CreateExchange(fixture.RemoteRoot);
        await using var sender = fixture.CreateExchange(fixture.LocalRoot);
        var recovery = new MachineIntegrationTransactionRecovery(sender);
        var firstEndpoint = await receiver.StartListeningAsync(IPAddress.Loopback, 0);
        var handoff = await fixture.PublishAsync(fixture.LocalRoot, "peer-restart");
        await sender.PushTransactionAsync(fixture.ToEndpoint(firstEndpoint), handoff.TransactionId);

        await receiver.StopListeningAsync();
        var unavailable = await recovery.ReconnectAndRecoverAsync(
            fixture.ToEndpoint(firstEndpoint),
            handoff.TransactionId);
        Assert.Equal(MachineIntegrationRecoveryState.Unknown, unavailable.State);
        Assert.False(unavailable.IsExecutionFailure);
        Assert.NotNull(unavailable.Detail);

        var secondEndpoint = await receiver.StartListeningAsync(IPAddress.Loopback, 0);
        var remoteHandoff = receiver.ReadHandoff(handoff.TransactionId);
        var acknowledgement = fixture.WriteAcknowledgement(
            fixture.RemoteRoot,
            remoteHandoff,
            IntegrationAcknowledgementStatus.Accepted);

        var awaiting = await recovery.ReconnectAndRecoverAsync(
            fixture.ToEndpoint(secondEndpoint),
            handoff.TransactionId);
        Assert.Equal(MachineIntegrationRecoveryState.AwaitingResult, awaiting.State);
        Assert.False(awaiting.IsExecutionFailure);

        await receiver.StopListeningAsync();
        fixture.WriteResult(
            fixture.RemoteRoot,
            remoteHandoff,
            acknowledgement,
            IntegrationResultStatus.Completed,
            IntegrationInspectionOutcome.Pass);

        var resultUnavailable = await recovery.ReconnectAndRecoverAsync(
            fixture.ToEndpoint(secondEndpoint),
            handoff.TransactionId);
        Assert.Equal(MachineIntegrationRecoveryState.Unknown, resultUnavailable.State);
        Assert.False(resultUnavailable.IsExecutionFailure);

        var thirdEndpoint = await receiver.StartListeningAsync(IPAddress.Loopback, 0);
        var recovered = await recovery.ReconnectAndRecoverAsync(
            fixture.ToEndpoint(thirdEndpoint),
            handoff.TransactionId);
        Assert.Equal(MachineIntegrationRecoveryState.Completed, recovered.State);
        Assert.Equal(handoff.TransactionId, recovered.TransactionId);
        Assert.Equal("pull", recovered.TransferReceipt!.Operation);
        Assert.Single(sender.DiscoverTransactions());
    }

    [Fact]
    public async Task ReconnectReportsConflictAndMissingPeerAsNonExecutionFailures()
    {
        using var fixture = new Fixture();
        await using var receiver = fixture.CreateExchange(fixture.RemoteRoot);
        await using var sender = fixture.CreateExchange(fixture.LocalRoot);
        var recovery = new MachineIntegrationTransactionRecovery(sender);
        var endpoint = await receiver.StartListeningAsync(IPAddress.Loopback, 0);
        var handoff = await fixture.PublishAsync(fixture.LocalRoot, "conflict");
        await sender.PushTransactionAsync(fixture.ToEndpoint(endpoint), handoff.TransactionId);

        var remoteArtifact = fixture.MessagePath(
            fixture.RemoteRoot,
            handoff.TransactionId,
            "artifacts/inspection-source.png");
        var changed = await File.ReadAllBytesAsync(remoteArtifact);
        changed[^1] ^= 0x01;
        await File.WriteAllBytesAsync(remoteArtifact, changed);

        var conflict = await recovery.ReconnectAndRecoverAsync(
            fixture.ToEndpoint(endpoint),
            handoff.TransactionId);
        Assert.Equal(MachineIntegrationRecoveryState.Conflict, conflict.State);
        Assert.Equal("immutableConflict", conflict.TransportErrorCode);
        Assert.False(conflict.IsExecutionFailure);

        var missing = await recovery.ReconnectAndRecoverAsync(
            fixture.ToEndpoint(endpoint),
            Guid.NewGuid());
        Assert.Equal(MachineIntegrationRecoveryState.Unknown, missing.State);
        Assert.Equal("transactionNotFound", missing.TransportErrorCode);
        Assert.False(missing.IsExecutionFailure);
    }

    [Fact]
    public async Task RetryRequiresExplicitTerminalStateAndUsesNewTransactionIdentity()
    {
        using var fixture = new Fixture();
        await using var exchange = fixture.CreateExchange(fixture.LocalRoot);
        var recovery = new MachineIntegrationTransactionRecovery(exchange);
        var failedHandoff = await fixture.PublishAsync(fixture.LocalRoot, "retry-source");
        var failedAcknowledgement = fixture.WriteAcknowledgement(
            fixture.LocalRoot,
            failedHandoff,
            IntegrationAcknowledgementStatus.Accepted);
        fixture.WriteResult(
            fixture.LocalRoot,
            failedHandoff,
            failedAcknowledgement,
            IntegrationResultStatus.Failed,
            IntegrationInspectionOutcome.ExecutionError);
        var failed = recovery.Observe(failedHandoff.TransactionId);

        var retried = await recovery.RetryAsync(
            failed,
            fixture.CreateRequest("retry-source"));
        Assert.NotEqual(failedHandoff.TransactionId, retried.TransactionId);
        Assert.NotEqual(failedHandoff.MessageId, retried.MessageId);
        Assert.Equal(
            MachineIntegrationRecoveryState.Failed,
            recovery.Observe(failedHandoff.TransactionId).State);
        Assert.Equal(2, exchange.DiscoverTransactions().Count);

        var awaitingHandoff = await fixture.PublishAsync(fixture.LocalRoot, "retry-blocked");
        var awaitingAcknowledgement = fixture.WriteAcknowledgement(
            fixture.LocalRoot,
            awaitingHandoff,
            IntegrationAcknowledgementStatus.Accepted);
        var awaiting = recovery.Observe(awaitingHandoff.TransactionId);
        Assert.Equal(MachineIntegrationRecoveryState.AwaitingResult, awaiting.State);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            recovery.RetryAsync(awaiting, fixture.CreateRequest("must-not-run")));
        Assert.Equal(
            awaitingAcknowledgement.MessageId,
            recovery.Observe(awaitingHandoff.TransactionId).Acknowledgement!.MessageId);
    }

    [Fact]
    public async Task InvalidResultBeforeAcknowledgementIsNotInterpretedAsUnknown()
    {
        using var fixture = new Fixture();
        await using var exchange = fixture.CreateExchange(fixture.LocalRoot);
        var recovery = new MachineIntegrationTransactionRecovery(exchange);
        var handoff = await fixture.PublishAsync(fixture.LocalRoot, "invalid-order");
        var acknowledgement = fixture.CreateAcknowledgement(
            handoff,
            IntegrationAcknowledgementStatus.Accepted);
        var result = fixture.CreateResult(
            handoff,
            acknowledgement,
            IntegrationResultStatus.Cancelled,
            IntegrationInspectionOutcome.Indeterminate);
        fixture.WriteMessage(
            fixture.LocalRoot,
            handoff.TransactionId,
            IntegrationTransactionLayout.ResultFileName,
            IntegrationContractJson.SerializeCanonical(result));

        var observed = recovery.Observe(handoff.TransactionId);
        Assert.Equal(MachineIntegrationRecoveryState.Invalid, observed.State);
        Assert.False(observed.IsExecutionFailure);
    }

    private sealed class Fixture : IDisposable
    {
        public Fixture()
        {
            Root = Path.Combine(
                TestStorage.RootPath,
                "mch-037-transaction-recovery",
                Guid.NewGuid().ToString("N"));
            LocalRoot = Path.Combine(Root, "local");
            RemoteRoot = Path.Combine(Root, "remote");
            SourceRoot = Path.Combine(Root, "source");
            Directory.CreateDirectory(LocalRoot);
            Directory.CreateDirectory(RemoteRoot);
            Directory.CreateDirectory(SourceRoot);
            MachineProjectPath = Write(
                "machine.ovmachine",
                "{\"schema\":\"machine-project/1.0\"}");
            SourcePath = WriteBytes("inspection-source.png", [0x89, 0x50, 0x4E, 0x47, 0x00]);
            RecipePath = Write("inspection-recipe.json", "{\"tool\":\"mch-037\"}");
            SharedKey = SHA256.HashData(Encoding.UTF8.GetBytes("mch-037-shared-key"));
        }

        private string Root { get; }

        public string LocalRoot { get; }

        public string RemoteRoot { get; }

        private string SourceRoot { get; }

        private string MachineProjectPath { get; }

        private string SourcePath { get; }

        private string RecipePath { get; }

        private byte[] SharedKey { get; }

        public MachineIntegrationTcpExchange CreateExchange(string root) =>
            new(root, SharedKey, new TcpIntegrationOptions
            {
                MaxAttempts = 1,
                ConnectTimeout = TimeSpan.FromMilliseconds(300),
                IdleTimeout = TimeSpan.FromSeconds(1)
            });

        public async Task<IntegrationHandoffV2> PublishAsync(
            string root,
            string suffix) =>
            await MachineIntegrationHandoffPublisher.PublishAsync(
                root,
                CreateRequest(suffix));

        public MachineInspectionHandoffRequest CreateRequest(string suffix) => new(
            "mch-037-project",
            "machine-project/1.0",
            "sequence-037",
            $"inspect-{suffix}",
            "camera-virtual",
            $"acquisition-{suffix}",
            $"frame-{suffix}",
            "px",
            MachineProjectPath,
            SourcePath,
            RecipePath,
            IntegrationInspectionModality.TwoD,
            IntegrationInspectionInputKind.Image,
            new IntegrationApplicationIdentity(
                IntegrationApplicationIds.MachineStudio,
                "0.2.0-test",
                new string('1', 40),
                IntegrationSourceState.Clean),
            new IntegrationApplicationIdentity(
                IntegrationApplicationIds.TwoDStudio,
                "2.1.0-test",
                new string('2', 40),
                IntegrationSourceState.Clean));

        public IntegrationAcknowledgementV2 WriteAcknowledgement(
            string root,
            IntegrationHandoffV2 handoff,
            IntegrationAcknowledgementStatus status)
        {
            var acknowledgement = CreateAcknowledgement(handoff, status);
            WriteMessage(
                root,
                handoff.TransactionId,
                IntegrationTransactionLayout.AcknowledgementFileName,
                IntegrationContractJson.SerializeCanonical(acknowledgement));
            return acknowledgement;
        }

        public IntegrationResultV2 WriteResult(
            string root,
            IntegrationHandoffV2 handoff,
            IntegrationAcknowledgementV2 acknowledgement,
            IntegrationResultStatus status,
            IntegrationInspectionOutcome outcome)
        {
            var result = CreateResult(handoff, acknowledgement, status, outcome);
            if (result.RunRecord is { } runRecord)
            {
                WriteMessage(
                    root,
                    handoff.TransactionId,
                    runRecord.RelativePath,
                    Encoding.UTF8.GetBytes("{\"run\":\"mch-037\"}"));
            }

            WriteMessage(
                root,
                handoff.TransactionId,
                IntegrationTransactionLayout.ResultFileName,
                IntegrationContractJson.SerializeCanonical(result));
            return result;
        }

        public IntegrationAcknowledgementV2 CreateAcknowledgement(
            IntegrationHandoffV2 handoff,
            IntegrationAcknowledgementStatus status) =>
            new(
                IntegrationContractSchema.V2,
                IntegrationMessageKind.Acknowledgement,
                Guid.NewGuid(),
                handoff.TransactionId,
                handoff.MessageId,
                handoff.CreatedAtUtc.AddMilliseconds(status == IntegrationAcknowledgementStatus.Accepted ? 1 : 2),
                handoff.Context.ConsumerBuild,
                status,
                status == IntegrationAcknowledgementStatus.Rejected
                    ? new IntegrationError(
                        IntegrationErrorCode.RequestRejected,
                        "The consumer rejected the request for this recovery fixture.",
                        false)
                    : null);

        public IntegrationResultV2 CreateResult(
            IntegrationHandoffV2 handoff,
            IntegrationAcknowledgementV2 acknowledgement,
            IntegrationResultStatus status,
            IntegrationInspectionOutcome outcome)
        {
            var runRecord = status == IntegrationResultStatus.Completed
                ? new IntegrationArtifactReference(
                    IntegrationArtifactRoles.RunRecord,
                    "run-record",
                    "artifacts/run-record.json",
                    Encoding.UTF8.GetByteCount("{\"run\":\"mch-037\"}"),
                    Convert.ToHexString(SHA256.HashData(
                        Encoding.UTF8.GetBytes("{\"run\":\"mch-037\"}"))))
                : null;
            var error = status switch
            {
                IntegrationResultStatus.Failed => new IntegrationError(
                    IntegrationErrorCode.ExecutionFailed,
                    "The consumer reported an execution failure.",
                    true),
                IntegrationResultStatus.Cancelled => new IntegrationError(
                    IntegrationErrorCode.Cancelled,
                    "The consumer cancelled the inspection.",
                    false),
                _ => null
            };
            return new(
                IntegrationContractSchema.V2,
                IntegrationMessageKind.Result,
                Guid.NewGuid(),
                handoff.TransactionId,
                handoff.MessageId,
                acknowledgement.MessageId,
                acknowledgement.CreatedAtUtc.AddMilliseconds(1),
                handoff.Context.ConsumerBuild,
                status,
                outcome,
                status == IntegrationResultStatus.Completed ? "run-mch-037" : null,
                runRecord,
                IntegrationRunCorrelation.FromContext(handoff.Context),
                [],
                [],
                error);
        }

        public string MessagePath(string root, Guid transactionId, string relativePath) =>
            Path.Combine(
                root,
                IntegrationTransactionLayout.TransactionsDirectoryName,
                transactionId.ToString("D"),
                relativePath.Replace('/', Path.DirectorySeparatorChar));

        public void WriteMessage(
            string root,
            Guid transactionId,
            string relativePath,
            byte[] bytes)
        {
            var path = MessagePath(root, transactionId, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, bytes);
        }

        public TcpIntegrationEndpoint ToEndpoint(IPEndPoint endpoint) =>
            new(endpoint.Address.ToString(), endpoint.Port);

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }

            CryptographicOperations.ZeroMemory(SharedKey);
        }

        private string Write(string name, string contents)
        {
            var path = Path.Combine(SourceRoot, name);
            File.WriteAllText(path, contents, new UTF8Encoding(false));
            return path;
        }

        private string WriteBytes(string name, byte[] contents)
        {
            var path = Path.Combine(SourceRoot, name);
            File.WriteAllBytes(path, contents);
            return path;
        }
    }
}
