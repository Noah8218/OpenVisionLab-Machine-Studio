using OpenVisionLab.Machine.Simulation.Camera;

namespace OpenVisionLab.Machine.Simulation.Commands;

public sealed record ExternalInspectionMessageChain
{
    public ExternalInspectionMessageChain(
        Guid handoffTransactionId,
        Guid handoffMessageId,
        Guid acknowledgementTransactionId,
        Guid acknowledgementHandoffMessageId,
        Guid acknowledgementMessageId,
        Guid resultTransactionId,
        Guid resultHandoffMessageId,
        Guid resultAcknowledgementMessageId,
        Guid resultMessageId,
        string resultDocumentSha256)
    {
        if (handoffTransactionId == Guid.Empty
            || handoffMessageId == Guid.Empty
            || acknowledgementTransactionId == Guid.Empty
            || acknowledgementHandoffMessageId == Guid.Empty
            || acknowledgementMessageId == Guid.Empty
            || resultTransactionId == Guid.Empty
            || resultHandoffMessageId == Guid.Empty
            || resultAcknowledgementMessageId == Guid.Empty
            || resultMessageId == Guid.Empty)
        {
            throw new ArgumentException("External inspection message identities cannot be empty.");
        }
        if (string.IsNullOrWhiteSpace(resultDocumentSha256)
            || resultDocumentSha256.Length != 64
            || resultDocumentSha256.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException(
                "Result document SHA-256 must contain exactly 64 hexadecimal characters.",
                nameof(resultDocumentSha256));
        }

        HandoffTransactionId = handoffTransactionId;
        HandoffMessageId = handoffMessageId;
        AcknowledgementTransactionId = acknowledgementTransactionId;
        AcknowledgementHandoffMessageId = acknowledgementHandoffMessageId;
        AcknowledgementMessageId = acknowledgementMessageId;
        ResultTransactionId = resultTransactionId;
        ResultHandoffMessageId = resultHandoffMessageId;
        ResultAcknowledgementMessageId = resultAcknowledgementMessageId;
        ResultMessageId = resultMessageId;
        ResultDocumentSha256 = resultDocumentSha256.ToUpperInvariant();
    }

    public Guid HandoffTransactionId { get; }
    public Guid HandoffMessageId { get; }
    public Guid AcknowledgementTransactionId { get; }
    public Guid AcknowledgementHandoffMessageId { get; }
    public Guid AcknowledgementMessageId { get; }
    public Guid ResultTransactionId { get; }
    public Guid ResultHandoffMessageId { get; }
    public Guid ResultAcknowledgementMessageId { get; }
    public Guid ResultMessageId { get; }
    public string ResultDocumentSha256 { get; }

    public bool IsExactlyCorrelated =>
        HandoffTransactionId == AcknowledgementTransactionId
        && HandoffTransactionId == ResultTransactionId
        && HandoffMessageId == AcknowledgementHandoffMessageId
        && HandoffMessageId == ResultHandoffMessageId
        && AcknowledgementMessageId == ResultAcknowledgementMessageId;
}

public sealed class ApplyExternalInspectionResultCommand : SimulationCommand
{
    public ApplyExternalInspectionResultCommand(
        SimulationRuntimeIdentity expectedRuntime,
        ExternalInspectionMessageChain messageChain,
        ExternalInspectionCorrelationIdentity expectedCorrelation,
        ExternalInspectionCorrelationIdentity resultCorrelation,
        ExternalInspectionConsumerIdentity acknowledgementProducer,
        ExternalInspectionConsumerIdentity resultProducer,
        bool acknowledgementAccepted,
        ExternalInspectionResultStatus status,
        ExternalInspectionOutcome outcome,
        string? runId)
    {
        if (string.IsNullOrWhiteSpace(expectedRuntime.ProjectId))
        {
            throw new ArgumentException(
                "External inspection admission requires a project-bound runtime.",
                nameof(expectedRuntime));
        }

        ExpectedRuntime = expectedRuntime;
        MessageChain = messageChain ?? throw new ArgumentNullException(nameof(messageChain));
        ExpectedCorrelation = expectedCorrelation ?? throw new ArgumentNullException(nameof(expectedCorrelation));
        ResultCorrelation = resultCorrelation ?? throw new ArgumentNullException(nameof(resultCorrelation));
        AcknowledgementProducer = acknowledgementProducer ?? throw new ArgumentNullException(nameof(acknowledgementProducer));
        ResultProducer = resultProducer ?? throw new ArgumentNullException(nameof(resultProducer));
        AcknowledgementAccepted = acknowledgementAccepted;
        Status = status;
        Outcome = outcome;
        RunId = string.IsNullOrWhiteSpace(runId) ? null : runId;
    }

    public ExternalInspectionMessageChain MessageChain { get; }
    public ExternalInspectionCorrelationIdentity ExpectedCorrelation { get; }
    public ExternalInspectionCorrelationIdentity ResultCorrelation { get; }
    public ExternalInspectionConsumerIdentity AcknowledgementProducer { get; }
    public ExternalInspectionConsumerIdentity ResultProducer { get; }
    public bool AcknowledgementAccepted { get; }
    public ExternalInspectionResultStatus Status { get; }
    public ExternalInspectionOutcome Outcome { get; }
    public string? RunId { get; }
}
