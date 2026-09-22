using OpenVisionLab.Machine.Core.Devices;

namespace OpenVisionLab.Machine.Simulation.Camera;

public enum ExternalInspectionResultStatus
{
    Completed,
    Failed,
    Cancelled
}

public enum ExternalInspectionOutcome
{
    Pass,
    Ng,
    NotMeasured,
    Indeterminate,
    CorrelationMismatch,
    Tampered,
    ExecutionError
}

public sealed record ExternalInspectionConsumerIdentity
{
    public ExternalInspectionConsumerIdentity(
        string applicationId,
        string version,
        string sourceCommit,
        string sourceState)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceCommit);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceState);

        ApplicationId = applicationId;
        Version = version;
        SourceCommit = sourceCommit;
        SourceState = sourceState;
    }

    public string ApplicationId { get; }
    public string Version { get; }
    public string SourceCommit { get; }
    public string SourceState { get; }
}

public sealed record ExternalInspectionCorrelationIdentity
{
    public ExternalInspectionCorrelationIdentity(
        string projectId,
        string projectSchema,
        string sequenceId,
        string stepId,
        string cameraId,
        string acquisitionId,
        string frameId,
        string unit,
        string modality,
        string inputKind,
        string inputSha256,
        string recipeSha256,
        ExternalInspectionConsumerIdentity consumerBuild)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectSchema);
        ArgumentException.ThrowIfNullOrWhiteSpace(sequenceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(stepId);
        ArgumentException.ThrowIfNullOrWhiteSpace(cameraId);
        ArgumentException.ThrowIfNullOrWhiteSpace(acquisitionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(frameId);
        ArgumentException.ThrowIfNullOrWhiteSpace(unit);
        ArgumentException.ThrowIfNullOrWhiteSpace(modality);
        ArgumentException.ThrowIfNullOrWhiteSpace(inputKind);
        ValidateSha256(inputSha256, nameof(inputSha256));
        ValidateSha256(recipeSha256, nameof(recipeSha256));

        ProjectId = projectId;
        ProjectSchema = projectSchema;
        SequenceId = sequenceId;
        StepId = stepId;
        CameraId = cameraId;
        AcquisitionId = acquisitionId;
        FrameId = frameId;
        Unit = unit;
        Modality = modality;
        InputKind = inputKind;
        InputSha256 = inputSha256.ToUpperInvariant();
        RecipeSha256 = recipeSha256.ToUpperInvariant();
        ConsumerBuild = consumerBuild ?? throw new ArgumentNullException(nameof(consumerBuild));
    }

    public string ProjectId { get; }
    public string ProjectSchema { get; }
    public string SequenceId { get; }
    public string StepId { get; }
    public string CameraId { get; }
    public string AcquisitionId { get; }
    public string FrameId { get; }
    public string Unit { get; }
    public string Modality { get; }
    public string InputKind { get; }
    public string InputSha256 { get; }
    public string RecipeSha256 { get; }
    public ExternalInspectionConsumerIdentity ConsumerBuild { get; }

    private static void ValidateSha256(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length != 64
            || value.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException(
                "SHA-256 values must contain exactly 64 hexadecimal characters.",
                parameterName);
        }
    }
}

public sealed record VirtualCameraExternalResultEvidence(
    Guid TransactionId,
    Guid HandoffMessageId,
    Guid AcknowledgementMessageId,
    Guid ResultMessageId,
    string ResultDocumentSha256,
    ExternalInspectionCorrelationIdentity Correlation,
    ExternalInspectionResultStatus Status,
    ExternalInspectionOutcome Outcome,
    string? RunId,
    PlaceholderInspectionDecision? Decision);

public enum VirtualCameraExternalResultAdmissionErrorCode
{
    None,
    NotAwaitingExternalResult,
    AcquisitionMismatch,
    FrameMismatch,
    ConflictingDuplicate
}

public sealed record VirtualCameraExternalResultAdmissionResult(
    bool IsAccepted,
    bool IsIdempotent,
    bool IsTerminalFailure,
    VirtualCameraExternalResultAdmissionErrorCode ErrorCode)
{
    internal static VirtualCameraExternalResultAdmissionResult Applied(bool terminalFailure) =>
        new(true, false, terminalFailure, VirtualCameraExternalResultAdmissionErrorCode.None);

    internal static VirtualCameraExternalResultAdmissionResult Idempotent() =>
        new(true, true, false, VirtualCameraExternalResultAdmissionErrorCode.None);

    internal static VirtualCameraExternalResultAdmissionResult Rejected(
        VirtualCameraExternalResultAdmissionErrorCode errorCode) =>
        new(false, false, false, errorCode);
}
