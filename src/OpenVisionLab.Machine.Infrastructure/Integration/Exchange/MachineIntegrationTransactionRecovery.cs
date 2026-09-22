using System.Text.Json;
using OpenVisionLab.Integration.Contracts;
using OpenVisionLab.Integration.Transport.Tcp;

namespace OpenVisionLab.Machine.Infrastructure.Integration;

public enum MachineIntegrationRecoveryState
{
    Unknown,
    WaitingForAcknowledgement,
    AwaitingResult,
    Rejected,
    Completed,
    Failed,
    Cancelled,
    Invalid,
    Conflict
}

/// <summary>
/// A read-only snapshot of one transaction after local observation or an
/// explicit reconnect/pull. Unknown means that the current durable state could
/// not be established; it is never treated as an execution failure.
/// </summary>
public sealed record MachineIntegrationTransactionRecoverySnapshot(
    Guid TransactionId,
    MachineIntegrationRecoveryState State,
    IntegrationHandoffV2? Handoff = null,
    IntegrationAcknowledgementV2? Acknowledgement = null,
    IntegrationResultV2? Result = null,
    string? ResultDocumentSha256 = null,
    TcpIntegrationTransferReceipt? TransferReceipt = null,
    string? TransportErrorCode = null,
    string? Detail = null)
{
    public bool IsExecutionFailure =>
        State == MachineIntegrationRecoveryState.Failed
        && Result?.Outcome == IntegrationInspectionOutcome.ExecutionError;

    public bool CanRetry => State is
        MachineIntegrationRecoveryState.Rejected
        or MachineIntegrationRecoveryState.Failed
        or MachineIntegrationRecoveryState.Cancelled;

    public bool CanRequestCancellation => State is
        MachineIntegrationRecoveryState.WaitingForAcknowledgement
        or MachineIntegrationRecoveryState.AwaitingResult;

    public bool IsTerminal => State is
        MachineIntegrationRecoveryState.Rejected
        or MachineIntegrationRecoveryState.Completed
        or MachineIntegrationRecoveryState.Failed
        or MachineIntegrationRecoveryState.Cancelled;
}

/// <summary>
/// An in-memory cancellation intent. V2 has no cancellation-request message;
/// therefore this record never writes a synthetic Result or sends a network
/// command. The consumer must publish the correlated V2 Cancelled Result.
/// </summary>
public sealed record MachineIntegrationCancellationIntent(
    Guid TransactionId,
    DateTimeOffset RequestedAtUtc,
    string? Reason,
    bool RequiresConsumerResultPublication);

/// <summary>
/// Provides the smallest MCH-037 recovery adapter over the existing local
/// transaction inspector, TCP Pull, and handoff publisher. Reconnect is an
/// explicit pull followed by observation; it never starts inspection or Apply.
/// </summary>
public sealed class MachineIntegrationTransactionRecovery
{
    private readonly MachineIntegrationTcpExchange _exchange;

    public MachineIntegrationTransactionRecovery(
        MachineIntegrationTcpExchange exchange)
    {
        _exchange = exchange ?? throw new ArgumentNullException(nameof(exchange));
    }

    public MachineIntegrationTransactionRecoverySnapshot Observe(Guid transactionId)
    {
        EnsureTransactionId(transactionId);
        try
        {
            var handoff = MachineIntegrationTransactionInspector.ReadHandoff(
                _exchange.ExchangeRoot,
                transactionId);
            var transactionDirectory = MachineIntegrationTransactionFileSystem.GetTransactionDirectory(
                _exchange.ExchangeRoot,
                transactionId);
            var hasAcknowledgement = File.Exists(Path.Combine(
                transactionDirectory,
                IntegrationTransactionLayout.AcknowledgementFileName));
            var hasResult = File.Exists(Path.Combine(
                transactionDirectory,
                IntegrationTransactionLayout.ResultFileName));

            if (!hasAcknowledgement)
            {
                return hasResult
                    ? Invalid(
                        transactionId,
                        handoff,
                        detail: "A Result exists without an Acknowledgement.")
                    : Snapshot(
                        transactionId,
                        MachineIntegrationRecoveryState.WaitingForAcknowledgement,
                        handoff,
                        detail: "Handoff is present; no Acknowledgement has been published.");
            }

            IntegrationAcknowledgementV2 acknowledgement;
            try
            {
                acknowledgement = MachineIntegrationTransactionInspector.ReadAcknowledgement(
                    _exchange.ExchangeRoot,
                    transactionId);
            }
            catch (Exception exception) when (IsReadUnavailable(exception))
            {
                return Unknown(transactionId, handoff, exception: exception);
            }
            catch (Exception exception) when (IsInvalidObservation(exception))
            {
                return Invalid(transactionId, handoff, detail: Describe(exception));
            }

            if (acknowledgement.Status == IntegrationAcknowledgementStatus.Rejected)
            {
                return hasResult
                    ? Invalid(
                        transactionId,
                        handoff,
                        acknowledgement,
                        detail: "A Result exists after a rejected Acknowledgement.")
                    : Snapshot(
                        transactionId,
                        MachineIntegrationRecoveryState.Rejected,
                        handoff,
                        acknowledgement,
                        detail: acknowledgement.Error?.Message
                            ?? "The consumer rejected the Handoff.");
            }

            if (!hasResult)
            {
                return Snapshot(
                    transactionId,
                    MachineIntegrationRecoveryState.AwaitingResult,
                    handoff,
                    acknowledgement,
                    detail: "Accepted Acknowledgement is present; Result is pending.");
            }

            MachineIntegrationValidatedResult validated;
            try
            {
                validated = MachineIntegrationTransactionInspector.ReadValidatedResult(
                    _exchange.ExchangeRoot,
                    transactionId);
            }
            catch (Exception exception) when (IsReadUnavailable(exception))
            {
                return Unknown(transactionId, handoff, acknowledgement, exception);
            }
            catch (Exception exception) when (IsInvalidObservation(exception))
            {
                return Invalid(
                    transactionId,
                    handoff,
                    acknowledgement,
                    detail: Describe(exception));
            }

            var state = validated.Result.Status switch
            {
                IntegrationResultStatus.Completed => MachineIntegrationRecoveryState.Completed,
                IntegrationResultStatus.Failed => MachineIntegrationRecoveryState.Failed,
                IntegrationResultStatus.Cancelled => MachineIntegrationRecoveryState.Cancelled,
                _ => MachineIntegrationRecoveryState.Invalid
            };
            return Snapshot(
                transactionId,
                state,
                validated.Handoff,
                validated.Acknowledgement,
                validated.Result,
                validated.ResultDocumentSha256,
                detail: state == MachineIntegrationRecoveryState.Failed
                    ? validated.Result.Error?.Message ?? "The consumer reported execution failure."
                    : null);
        }
        catch (Exception exception) when (IsReadUnavailable(exception))
        {
            return Unknown(transactionId, exception: exception);
        }
        catch (Exception exception) when (IsInvalidObservation(exception))
        {
            return Invalid(transactionId, detail: Describe(exception));
        }
    }

    /// <summary>
    /// Pulls one immutable transaction after an explicit reconnect and then
    /// observes the same transaction identity. Transport failure is returned as
    /// Unknown, while an immutable-byte conflict is returned as Conflict.
    /// </summary>
    public async Task<MachineIntegrationTransactionRecoverySnapshot> ReconnectAndRecoverAsync(
        TcpIntegrationEndpoint peer,
        Guid transactionId,
        CancellationToken cancellationToken = default)
    {
        EnsureTransactionId(transactionId);
        ArgumentNullException.ThrowIfNull(peer);
        try
        {
            var receipt = await _exchange.PullTransactionAsync(
                    peer,
                    transactionId,
                    cancellationToken)
                .ConfigureAwait(false);
            return Observe(transactionId) with { TransferReceipt = receipt };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TcpIntegrationTransportException exception)
        {
            return exception.Code == "immutableConflict"
                ? Snapshot(
                    transactionId,
                    MachineIntegrationRecoveryState.Conflict,
                    transportErrorCode: exception.Code,
                    detail: exception.Message)
                : Unknown(
                    transactionId,
                    transportErrorCode: exception.Code,
                    detail: exception.Message);
        }
        catch (Exception exception) when (exception is IOException or TimeoutException)
        {
            return Unknown(transactionId, exception: exception);
        }
    }

    /// <summary>
    /// Publishes a new Handoff only after the caller explicitly supplies a
    /// retryable terminal snapshot. The publisher creates a new transaction and
    /// message identity; the prior transaction remains untouched.
    /// </summary>
    public Task<IntegrationHandoffV2> RetryAsync(
        MachineIntegrationTransactionRecoverySnapshot snapshot,
        MachineInspectionHandoffRequest request,
        IProgress<MachineIntegrationTransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(request);
        if (!snapshot.CanRetry || snapshot.Handoff is null)
        {
            throw new InvalidOperationException(
                "Explicit retry requires a Rejected, Failed, or Cancelled transaction with a known Handoff.");
        }

        EnsureRequestMatches(snapshot.Handoff.Context, request);
        return MachineIntegrationHandoffPublisher.PublishAsync(
            _exchange.ExchangeRoot,
            request,
            progress,
            cancellationToken);
    }

    /// <summary>
    /// Creates a local intent for a waiting transaction. No file is written and
    /// no consumer action is invoked until a separate V2 cancellation contract
    /// exists; the consumer must publish the terminal Cancelled Result.
    /// </summary>
    public static MachineIntegrationCancellationIntent CreateCancellationIntent(
        MachineIntegrationTransactionRecoverySnapshot snapshot,
        string? reason = null,
        DateTimeOffset? requestedAtUtc = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!snapshot.CanRequestCancellation)
        {
            throw new InvalidOperationException(
                "Cancellation intent requires a transaction waiting for Acknowledgement or Result.");
        }

        var requestedAt = requestedAtUtc?.ToUniversalTime() ?? DateTimeOffset.UtcNow;
        return new(
            snapshot.TransactionId,
            requestedAt,
            string.IsNullOrWhiteSpace(reason) ? null : reason.Trim(),
            RequiresConsumerResultPublication: true);
    }

    private static void EnsureRequestMatches(
        IntegrationInspectionContextV2 context,
        MachineInspectionHandoffRequest request)
    {
        if (!string.Equals(context.ProjectId, request.ProjectId, StringComparison.Ordinal)
            || !string.Equals(context.ProjectSchema, request.ProjectSchema, StringComparison.Ordinal)
            || !string.Equals(context.SequenceId, request.SequenceId, StringComparison.Ordinal)
            || !string.Equals(context.StepId, request.StepId, StringComparison.Ordinal)
            || !string.Equals(context.CameraId, request.CameraId, StringComparison.Ordinal)
            || !string.Equals(context.AcquisitionId, request.AcquisitionId, StringComparison.Ordinal)
            || !string.Equals(context.FrameId, request.FrameId, StringComparison.Ordinal)
            || !string.Equals(context.Unit, request.Unit, StringComparison.Ordinal)
            || context.Modality != request.Modality
            || context.InputKind != request.InputKind)
        {
            throw new ArgumentException(
                "Retry request does not match the original transaction context.",
                nameof(request));
        }
    }

    private static MachineIntegrationTransactionRecoverySnapshot Snapshot(
        Guid transactionId,
        MachineIntegrationRecoveryState state,
        IntegrationHandoffV2? handoff = null,
        IntegrationAcknowledgementV2? acknowledgement = null,
        IntegrationResultV2? result = null,
        string? resultDocumentSha256 = null,
        TcpIntegrationTransferReceipt? transferReceipt = null,
        string? transportErrorCode = null,
        string? detail = null) => new(
        transactionId,
        state,
        handoff,
        acknowledgement,
        result,
        resultDocumentSha256,
        transferReceipt,
        transportErrorCode,
        detail);

    private static MachineIntegrationTransactionRecoverySnapshot Unknown(
        Guid transactionId,
        IntegrationHandoffV2? handoff = null,
        IntegrationAcknowledgementV2? acknowledgement = null,
        Exception? exception = null,
        string? transportErrorCode = null,
        string? detail = null) => Snapshot(
        transactionId,
        MachineIntegrationRecoveryState.Unknown,
        handoff,
        acknowledgement,
        transportErrorCode: transportErrorCode,
        detail: detail ?? (exception is null ? null : Describe(exception)));

    private static MachineIntegrationTransactionRecoverySnapshot Invalid(
        Guid transactionId,
        IntegrationHandoffV2? handoff = null,
        IntegrationAcknowledgementV2? acknowledgement = null,
        string? detail = null) => Snapshot(
        transactionId,
        MachineIntegrationRecoveryState.Invalid,
        handoff,
        acknowledgement,
        detail: detail);

    private static bool IsReadUnavailable(Exception exception) => exception is
        FileNotFoundException
        or DirectoryNotFoundException
        or PathTooLongException
        or UnauthorizedAccessException
        or IOException;

    private static bool IsInvalidObservation(Exception exception) => exception is
        IntegrationContractException
        or InvalidDataException
        or JsonException;

    private static string Describe(Exception exception) =>
        string.IsNullOrWhiteSpace(exception.Message)
            ? exception.GetType().Name
            : $"{exception.GetType().Name}: {exception.Message}";

    private static void EnsureTransactionId(Guid transactionId)
    {
        if (transactionId == Guid.Empty)
        {
            throw new ArgumentException(
                "Transaction identity cannot be empty.",
                nameof(transactionId));
        }
    }
}
