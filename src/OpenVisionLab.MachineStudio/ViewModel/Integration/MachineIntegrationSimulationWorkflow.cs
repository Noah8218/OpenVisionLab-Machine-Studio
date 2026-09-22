using OpenVisionLab.Integration.Contracts;
using OpenVisionLab.Machine.Infrastructure.Integration;
using OpenVisionLab.Machine.Simulation.Camera;
using OpenVisionLab.Machine.Simulation.Commands;
using OpenVisionLab.Machine.Simulation.Engine;
using OpenVisionLab.Machine.Simulation.Snapshots;

namespace OpenVisionLab.MachineStudio.ViewModel;

internal sealed record MachineIntegrationPublishRuntimeContext(
    SimulationRuntimeIdentity RuntimeIdentity);

internal sealed record MachineIntegrationPublishedSimulationContext(
    IntegrationHandoffV2 Handoff,
    SimulationRuntimeIdentity RuntimeIdentity,
    bool IsClosed = false);

/// <summary>
/// Owns the current-process association between one published Handoff and the
/// simulation runtime that produced it, then maps one validated Result into an
/// explicit simulation command. It never observes files or applies a result
/// automatically.
/// </summary>
internal sealed class MachineIntegrationSimulationWorkflow
{
    private readonly Func<SimulationSnapshot> _snapshotProvider;
    private readonly Func<SimulationCommand, Task<SimulationCommandResult>> _dispatchCommandAsync;
    private MachineIntegrationPublishedSimulationContext? _publishedContext;

    internal MachineIntegrationSimulationWorkflow(
        Func<SimulationSnapshot> snapshotProvider,
        Func<SimulationCommand, Task<SimulationCommandResult>> dispatchCommandAsync)
    {
        _snapshotProvider = snapshotProvider ?? throw new ArgumentNullException(nameof(snapshotProvider));
        _dispatchCommandAsync = dispatchCommandAsync ?? throw new ArgumentNullException(nameof(dispatchCommandAsync));
    }

    internal MachineIntegrationPublishRuntimeContext? CapturePublishContext()
    {
        var snapshot = _snapshotProvider();
        return string.IsNullOrWhiteSpace(snapshot.ProjectId)
            ? null
            : new(new SimulationRuntimeIdentity(snapshot.ProjectId, snapshot.RuntimeGeneration));
    }

    internal bool TryRecordPublishedHandoff(
        IntegrationHandoffV2 handoff,
        MachineIntegrationPublishRuntimeContext? publishContext)
    {
        ArgumentNullException.ThrowIfNull(handoff);
        var snapshot = _snapshotProvider();
        if (publishContext is null
            || !MatchesRuntime(snapshot, publishContext.RuntimeIdentity)
            || !MatchesPendingCamera(snapshot, handoff.Context))
        {
            _publishedContext = null;
            return false;
        }

        _publishedContext = new(handoff, publishContext.RuntimeIdentity);
        return true;
    }

    internal bool CanApply(MachineIntegrationValidatedResult? validatedResult) =>
        TryCreateCommand(validatedResult, out _);

    internal Guid? PublishedTransactionId => _publishedContext?.Handoff.TransactionId;

    internal bool HasClosedPublishedContext => _publishedContext?.IsClosed == true;

    internal bool CanQuarantineLateResult(MachineIntegrationValidatedResult? validatedResult) =>
        _publishedContext?.IsClosed == true
        && TryCreateCommand(validatedResult, allowClosedContext: true, out _);

    internal async Task<SimulationCommandResult> ApplyAsync(
        MachineIntegrationValidatedResult validatedResult)
    {
        if (!TryCreateCommand(validatedResult, out var command))
        {
            throw new InvalidOperationException(
                "The validated external Result no longer matches the current pending simulation acquisition.");
        }

        var result = await _dispatchCommandAsync(command).ConfigureAwait(true);
        if (result.IsAccepted)
        {
            _publishedContext = null;
        }

        return result;
    }

    internal async Task<SimulationCommandResult> QuarantineLateResultAsync(
        MachineIntegrationValidatedResult validatedResult)
    {
        if (!TryCreateCommand(validatedResult, allowClosedContext: true, out var command)
            || _publishedContext?.IsClosed != true)
        {
            throw new InvalidOperationException(
                "The external Result no longer matches a closed automatic inspection transaction.");
        }

        var result = await _dispatchCommandAsync(command).ConfigureAwait(true);
        if (!result.IsAccepted && result.ErrorCode == SimulationCommandErrorCode.ExternalInspectionNotPending)
        {
            _publishedContext = null;
        }

        return result;
    }

    internal bool RefreshRuntimeState()
    {
        if (_publishedContext is not { } published)
        {
            return false;
        }

        if (published.IsClosed)
        {
            return false;
        }

        var snapshot = _snapshotProvider();
        if (MatchesRuntime(snapshot, published.RuntimeIdentity)
            && MatchesPendingCamera(snapshot, published.Handoff.Context))
        {
            return false;
        }

        _publishedContext = MatchesRuntime(snapshot, published.RuntimeIdentity)
            || MatchesClosedCamera(snapshot, published.Handoff.Context)
            ? published with { IsClosed = true }
            : null;
        return true;
    }

    internal void Clear() => _publishedContext = null;

    private bool TryCreateCommand(
        MachineIntegrationValidatedResult? validatedResult,
        out ApplyExternalInspectionResultCommand command) =>
        TryCreateCommand(validatedResult, allowClosedContext: false, out command);

    private bool TryCreateCommand(
        MachineIntegrationValidatedResult? validatedResult,
        bool allowClosedContext,
        out ApplyExternalInspectionResultCommand command)
    {
        command = null!;
        if (_publishedContext is not { } published
            || validatedResult is not { } validated
            || validated.Handoff.TransactionId != published.Handoff.TransactionId
            || validated.Handoff.MessageId != published.Handoff.MessageId
            || validated.Acknowledgement.Status != IntegrationAcknowledgementStatus.Accepted)
        {
            return false;
        }

        var snapshot = _snapshotProvider();
        var runtimeMatches = MatchesRuntime(snapshot, published.RuntimeIdentity);
        var closedCameraMatches = allowClosedContext
            && published.IsClosed
            && MatchesClosedCamera(snapshot, published.Handoff.Context);
        if (snapshot.RunMode != SimulationRunMode.Paused
            || (!runtimeMatches && !closedCameraMatches)
            || (!allowClosedContext && published.IsClosed)
            || (!allowClosedContext || !published.IsClosed)
                && !MatchesPendingCamera(snapshot, published.Handoff.Context))
        {
            return false;
        }

        var resultCorrelation = validated.Result.Correlation;
        if (resultCorrelation is null)
        {
            return false;
        }

        var expectedCorrelation = MapCorrelation(published.Handoff.Context);
        if (expectedCorrelation != MapCorrelation(validated.Handoff.Context))
        {
            return false;
        }

        var messageChain = new ExternalInspectionMessageChain(
            validated.Handoff.TransactionId,
            validated.Handoff.MessageId,
            validated.Acknowledgement.TransactionId,
            validated.Acknowledgement.HandoffMessageId,
            validated.Acknowledgement.MessageId,
            validated.Result.TransactionId,
            validated.Result.HandoffMessageId,
            validated.Result.AcknowledgementMessageId,
            validated.Result.MessageId,
            validated.ResultDocumentSha256);
        if (!messageChain.IsExactlyCorrelated)
        {
            return false;
        }

        var commandRuntime = closedCameraMatches
            ? new SimulationRuntimeIdentity(snapshot.ProjectId!, snapshot.RuntimeGeneration)
            : published.RuntimeIdentity;
        command = new ApplyExternalInspectionResultCommand(
            commandRuntime,
            messageChain,
            expectedCorrelation,
            MapCorrelation(resultCorrelation),
            MapIdentity(validated.Acknowledgement.Producer),
            MapIdentity(validated.Result.Producer),
            acknowledgementAccepted: true,
            MapStatus(validated.Result.Status),
            MapOutcome(validated.Result.Outcome),
            validated.Result.RunId);
        return true;
    }

    private static bool MatchesRuntime(
        SimulationSnapshot snapshot,
        SimulationRuntimeIdentity expected) =>
        string.Equals(snapshot.ProjectId, expected.ProjectId, StringComparison.Ordinal)
        && snapshot.RuntimeGeneration == expected.RuntimeGeneration;

    private static bool MatchesPendingCamera(
        SimulationSnapshot snapshot,
        IntegrationInspectionContextV2 context)
    {
        var camera = snapshot.Cameras.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, context.CameraId, StringComparison.Ordinal));
        var frame = camera?.FrameEvidence;
        return camera is
            {
                State: VirtualCameraState.AwaitingExternalResult,
                CurrentAcquisitionId: { } acquisitionId
            }
            && string.Equals(acquisitionId, context.AcquisitionId, StringComparison.Ordinal)
            && frame is not null
            && string.Equals(
                frame.FrameId,
                GetSimulationFrameId(context),
                StringComparison.Ordinal)
            && string.Equals(frame.ContentSha256, context.InputSha256, StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesClosedCamera(
        SimulationSnapshot snapshot,
        IntegrationInspectionContextV2 context)
    {
        var camera = snapshot.Cameras.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, context.CameraId, StringComparison.Ordinal));
        return camera is
            {
                State: VirtualCameraState.Faulted,
                CurrentAcquisitionId: { } acquisitionId
            }
            && string.Equals(acquisitionId, context.AcquisitionId, StringComparison.Ordinal);
    }

    private static ExternalInspectionCorrelationIdentity MapCorrelation(
        IntegrationInspectionContextV2 context) =>
        new(
            context.ProjectId,
            context.ProjectSchema,
            context.SequenceId,
            context.StepId,
            context.CameraId,
            context.AcquisitionId,
            GetSimulationFrameId(context),
            context.Unit,
            context.Modality.ToString(),
            context.InputKind.ToString(),
            context.InputSha256,
            context.RecipeSha256,
            MapIdentity(context.ConsumerBuild));

    private static ExternalInspectionCorrelationIdentity MapCorrelation(
        IntegrationRunCorrelation context) =>
        new(
            context.ProjectId,
            context.ProjectSchema,
            context.SequenceId,
            context.StepId,
            context.CameraId,
            context.AcquisitionId,
            GetSimulationFrameId(context),
            context.Unit,
            context.Modality.ToString(),
            context.InputKind.ToString(),
            context.InputSha256,
            context.RecipeSha256,
            MapIdentity(context.ConsumerBuild));

    private static string GetSimulationFrameId(IntegrationInspectionContextV2 context) =>
        IsThreeDHeightMap(context.Modality, context.InputKind)
            ? context.AcquisitionId
            : context.FrameId;

    private static string GetSimulationFrameId(IntegrationRunCorrelation context) =>
        IsThreeDHeightMap(context.Modality, context.InputKind)
            ? context.AcquisitionId
            : context.FrameId;

    private static bool IsThreeDHeightMap(
        IntegrationInspectionModality modality,
        IntegrationInspectionInputKind inputKind) =>
        modality == IntegrationInspectionModality.ThreeD
        && inputKind == IntegrationInspectionInputKind.HeightMap;

    private static bool IsThreeDHeightMap(string modality, string inputKind) =>
        string.Equals(modality, nameof(IntegrationInspectionModality.ThreeD), StringComparison.Ordinal)
        && string.Equals(inputKind, nameof(IntegrationInspectionInputKind.HeightMap), StringComparison.Ordinal);

    private static ExternalInspectionConsumerIdentity MapIdentity(
        IntegrationApplicationIdentity identity) =>
        new(
            identity.ApplicationId,
            identity.ApplicationVersion,
            identity.SourceCommit,
            identity.SourceState.ToString());

    private static ExternalInspectionResultStatus MapStatus(IntegrationResultStatus status) =>
        status switch
        {
            IntegrationResultStatus.Completed => ExternalInspectionResultStatus.Completed,
            IntegrationResultStatus.Failed => ExternalInspectionResultStatus.Failed,
            IntegrationResultStatus.Cancelled => ExternalInspectionResultStatus.Cancelled,
            _ => throw new ArgumentOutOfRangeException(nameof(status), status, null)
        };

    private static ExternalInspectionOutcome MapOutcome(IntegrationInspectionOutcome outcome) =>
        outcome switch
        {
            IntegrationInspectionOutcome.Pass => ExternalInspectionOutcome.Pass,
            IntegrationInspectionOutcome.Ng => ExternalInspectionOutcome.Ng,
            IntegrationInspectionOutcome.NotMeasured => ExternalInspectionOutcome.NotMeasured,
            IntegrationInspectionOutcome.Indeterminate => ExternalInspectionOutcome.Indeterminate,
            IntegrationInspectionOutcome.CorrelationMismatch => ExternalInspectionOutcome.CorrelationMismatch,
            IntegrationInspectionOutcome.Tampered => ExternalInspectionOutcome.Tampered,
            IntegrationInspectionOutcome.ExecutionError => ExternalInspectionOutcome.ExecutionError,
            _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, null)
        };
}
