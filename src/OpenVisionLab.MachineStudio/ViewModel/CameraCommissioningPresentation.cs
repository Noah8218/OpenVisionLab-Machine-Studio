using System.Globalization;
using OpenVisionLab;
using OpenVisionLab.Machine.Core.Devices;
using OpenVisionLab.Machine.Sequence.Runtime;
using OpenVisionLab.Machine.Simulation.Camera;
using OpenVisionLab.Machine.Simulation.Engine;

namespace OpenVisionLab.MachineStudio.ViewModel;

internal sealed record CameraCommissioningProjection(
    VirtualCameraSnapshot? Snapshot,
    bool HasCameraDefinition,
    string? FallbackCameraName,
    VirtualSingleImageSourceDefinition? ImageSource,
    string? ProjectPath,
    string? SelectedCameraRecipe,
    SimulationRunMode RuntimeRunMode,
    bool IsRunMode,
    bool IsApplyingProject,
    bool IsValidationBusy,
    bool IsRuntimeDefinitionDirty,
    bool IsRunning,
    SimulationControlOwner ControlOwner,
    bool IsAutomaticRunActive,
    SequenceExecutionStatus? ActiveSequenceStatus);

/// <summary>
/// Projects the selected virtual-camera snapshot into the existing
/// Machine Studio presentation and manual-command availability contract.
/// Main retains camera selection, acquisition, evidence, and command dispatch.
/// </summary>
internal sealed class CameraCommissioningPresentation
{
    private CameraCommissioningProjection? _projection;

    internal void ApplyProjection(CameraCommissioningProjection projection)
    {
        ArgumentNullException.ThrowIfNull(projection);
        _projection = projection;
    }

    internal string CurrentCameraName => Projection.Snapshot?.Name
        ?? Projection.FallbackCameraName
        ?? OpenVisionLanguageService.T("Shell.NoCamera");

    internal string CurrentCameraStateText => Projection.Snapshot is null
        ? OpenVisionLanguageService.T("Shell.Unavailable")
        : OpenVisionLanguageService.T(
            $"Equipment.State.{Projection.Snapshot.State}",
            Projection.Snapshot.State.ToString(),
            Projection.Snapshot.State.ToString());

    internal string CurrentCameraResultText => Projection.Snapshot?.Result?.Decision switch
    {
        PlaceholderInspectionDecision.Pass => OpenVisionLanguageService.T("Shell.ResultPass"),
        PlaceholderInspectionDecision.Fail => OpenVisionLanguageService.T("Shell.ResultFail"),
        _ when Projection.Snapshot?.State is
            VirtualCameraState.Exposing or VirtualCameraState.Transferring
            => OpenVisionLanguageService.T("Shell.ResultPending"),
        _ => "—"
    };

    internal string CurrentCameraFrameText => Projection.Snapshot?.CurrentAcquisitionId ?? "—";

    internal string CurrentCameraExposureTicksText => (Projection.Snapshot?.ExposureTicksRemaining ?? 0)
        .ToString(CultureInfo.InvariantCulture);

    internal string CurrentCameraTransferTicksText => (Projection.Snapshot?.TransferTicksRemaining ?? 0)
        .ToString(CultureInfo.InvariantCulture);

    internal string CurrentCameraSourceText => Projection.ImageSource?.SourceRelativePath ?? "—";

    internal string CurrentCameraFrameHashText => Projection.Snapshot?.FrameEvidence?.ContentSha256
        ?? "—";

    internal string CurrentCameraInspectionIdText => Projection.Snapshot?.Result?.InspectionEvidence?
        .InspectionId ?? "—";

    internal string CurrentCameraInspectionMessageText => Projection.Snapshot?.Result?
        .InspectionEvidence?.Message ?? "—";

    internal string CurrentCameraInspectionMetricsText =>
        Projection.Snapshot?.Result?.InspectionEvidence?.Metrics is { Count: > 0 } metrics
            ? string.Join(
                " · ",
                metrics.OrderBy(metric => metric.Key, StringComparer.Ordinal)
                    .Select(metric =>
                        $"{metric.Key}={metric.Value.ToString("G17", CultureInfo.InvariantCulture)}"))
            : "—";

    internal bool HasUsableCameraImageSource => !string.IsNullOrWhiteSpace(Projection.ProjectPath)
        && !string.IsNullOrWhiteSpace(Projection.SelectedCameraRecipe)
        && Projection.ImageSource is
        {
            SourceRelativePath.Length: > 0,
            Width: > 0,
            Height: > 0,
            PixelFormat.Length: > 0
        };

    internal string CameraCommissioningHintText => !Projection.HasCameraDefinition
        ? OpenVisionLanguageService.T("Camera.NoCameraHint")
        : !HasUsableCameraImageSource
            ? OpenVisionLanguageService.T("Camera.ConfigureSourceHint")
            : Projection.ControlOwner == SimulationControlOwner.Manual
                ? Projection.IsRunning
                    ? OpenVisionLanguageService.T("Camera.PauseBeforeTriggerHint")
                    : Projection.Snapshot?.State is
                        VirtualCameraState.Exposing or VirtualCameraState.Transferring
                        ? OpenVisionLanguageService.T("Camera.StepAcquisitionHint")
                        : OpenVisionLanguageService.T("Camera.TriggerReadyHint")
                : Projection.IsRunning || Projection.IsAutomaticRunActive ||
                  Projection.ActiveSequenceStatus == SequenceExecutionStatus.Running
                    ? OpenVisionLanguageService.T("Camera.ResetForManualHint")
                    : OpenVisionLanguageService.T("Camera.StartManualHint");

    internal bool CanStartManualCameraControl => Projection.IsRunMode
        && !Projection.IsApplyingProject
        && !Projection.IsValidationBusy
        && !Projection.IsRuntimeDefinitionDirty
        && !Projection.IsRunning
        && Projection.ControlOwner != SimulationControlOwner.Manual
        && !Projection.IsAutomaticRunActive
        && Projection.ActiveSequenceStatus != SequenceExecutionStatus.Running
        && Projection.Snapshot is not null;

    internal bool CanTriggerCamera => Projection.IsRunMode
        && !Projection.IsApplyingProject
        && !Projection.IsValidationBusy
        && !Projection.IsRuntimeDefinitionDirty
        && !Projection.IsRunning
        && Projection.RuntimeRunMode == SimulationRunMode.Paused
        && Projection.ControlOwner == SimulationControlOwner.Manual
        && Projection.Snapshot?.State is VirtualCameraState.Idle or VirtualCameraState.FrameReady
        && HasUsableCameraImageSource;

    private CameraCommissioningProjection Projection => _projection
        ?? throw new InvalidOperationException("Camera commissioning projection has not been initialized.");
}
