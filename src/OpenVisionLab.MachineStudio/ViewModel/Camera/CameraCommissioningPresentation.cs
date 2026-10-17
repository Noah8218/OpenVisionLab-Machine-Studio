using System.Globalization;
using System.IO;
using OpenVisionLab;
using OpenVisionLab.Machine.Core.Devices;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Core.Sequences;
using OpenVisionLab.Machine.Sequence.Runtime;
using OpenVisionLab.Machine.Simulation.Camera;
using OpenVisionLab.Machine.Simulation.Engine;

namespace OpenVisionLab.MachineStudio.ViewModel;

public sealed record CameraExecutionInputItem(
    string Id,
    string Name,
    int InputCount,
    string StateText,
    string ContextText,
    string StepContextText,
    string AcquisitionText,
    string? ProjectId = null,
    long RuntimeGeneration = 0);

internal sealed record CameraCommissioningProjection(
    VirtualCameraSnapshot? Snapshot,
    bool HasCameraDefinition,
    string? FallbackCameraName,
    VirtualSingleImageSourceDefinition? ImageSource,
    string? ProjectPath,
    string? SelectedCameraRecipe,
    TimeSpan SimulationFixedStep,
    SimulationRunMode RuntimeRunMode,
    bool IsRunMode,
    bool IsApplyingProject,
    bool IsValidationBusy,
    bool IsRuntimeDefinitionDirty,
    bool IsRunning,
    SimulationControlOwner ControlOwner,
    bool IsAutomaticRunActive,
    SequenceExecutionStatus? ActiveSequenceStatus,
    NumericalCameraSettings? NumericalInput = null);

/// <summary>
/// Projects the selected virtual-camera snapshot into the existing
/// Machine Studio presentation and manual-command availability contract.
/// CameraCommissioningViewModel owns the surrounding workspace lifecycle.
/// </summary>
internal sealed class CameraCommissioningPresentation
{
    internal static IReadOnlyList<CameraExecutionInputItem> CreateExecutionInputs(MachineProjectDocument project, IEnumerable<VirtualCameraSnapshot> cameras, long runtimeGeneration = 0)
    {
        var snapshots = cameras.ToDictionary(camera => camera.Id, StringComparer.Ordinal);
        return project.Devices.Where(device => device.Kind == DeviceKind.Camera).Select(device =>
        {
            snapshots.TryGetValue(device.Id, out var snapshot);
            var result = snapshot?.Result;
            bool hasInput = snapshot?.CurrentAcquisitionId is not null
                && (snapshot.State == VirtualCameraState.AwaitingExternalResult && snapshot.FrameEvidence is not null
                    || snapshot.State == VirtualCameraState.FrameReady && result?.AcquisitionId == snapshot.CurrentAcquisitionId
                        && (snapshot.FrameEvidence is not null || result?.FrameEvidence is not null || result?.NumericalInput is not null));
            string decision = result?.Decision switch
            {
                PlaceholderInspectionDecision.Pass => OpenVisionLanguageService.T("Shell.ResultPass"),
                PlaceholderInspectionDecision.Fail => OpenVisionLanguageService.T("Shell.ResultFail"),
                _ => "—"
            };
            string state = snapshot?.State switch
            {
                VirtualCameraState.Faulted => OpenVisionLanguageService.T("Camera.InputFaulted"),
                VirtualCameraState.Exposing or VirtualCameraState.Transferring => OpenVisionLanguageService.T("Camera.InputAcquiring"),
                VirtualCameraState.AwaitingExternalResult when hasInput => OpenVisionLanguageService.T("Camera.InputAwaitingResult"),
                VirtualCameraState.AwaitingExternalResult => OpenVisionLanguageService.T("Camera.InputAcquiring"),
                VirtualCameraState.FrameReady when hasInput => Format("Camera.InputReady", decision),
                VirtualCameraState.FrameReady when result is not null => Format("Camera.InputOnlyMockResult", decision),
                _ => OpenVisionLanguageService.T("Camera.InputNotAcquired")
            };
            string? workpieceId = result?.WorkpieceComponentId;
            string? workpieceName = project.Layouts.SelectMany(layout => layout.Components).FirstOrDefault(component => component.Id == workpieceId)?.Name
                ?? project.Stations.SelectMany(station => station.Units).FirstOrDefault(unit => unit.Id == workpieceId)?.Name;
            string workpiece = DisplayName(workpieceName, workpieceId);
            if (result?.WorkpieceInstanceId is { } instanceId) workpiece += $" · {instanceId}";
            var correlation = (snapshot?.ExternalResultEvidence ?? result?.ExternalResultEvidence)?.Correlation;
            string stepContext;
            if (correlation is not null && correlation.ProjectId == project.Id && correlation.CameraId == device.Id
                && correlation.AcquisitionId == snapshot?.CurrentAcquisitionId)
            {
                var sequence = project.Sequences.FirstOrDefault(candidate => candidate.Id == correlation.SequenceId);
                var step = sequence?.Steps.FirstOrDefault(candidate => candidate.Id == correlation.StepId);
                stepContext = Format("Camera.InputRecordedStep", $"{DisplayName(sequence?.Name, correlation.SequenceId)} / {DisplayName(step?.Name, correlation.StepId)}");
            }
            else
            {
                var linkedSteps = project.Sequences.SelectMany(sequence => sequence.Steps
                    .Where(step => step.Action == SequenceStepAction.TriggerCamera && step.TargetId == device.Id && step.Parameter == snapshot?.CurrentRecipeId)
                    .Select(step => $"{DisplayName(sequence.Name, sequence.Id)} / {DisplayName(step.Name, step.Id)}")).ToArray();
                stepContext = linkedSteps.Length == 0 ? OpenVisionLanguageService.T("Camera.InputUnrecordedStep") : Format("Camera.InputLinkedSteps", string.Join(" · ", linkedSteps));
            }
            return new CameraExecutionInputItem(device.Id, device.Name, hasInput ? 1 : 0, state,
                Format("Camera.InputContext", workpiece, snapshot?.CurrentRecipeId ?? "—"), stepContext,
                Format("Camera.InputAcquisition", snapshot?.CurrentAcquisitionId ?? "—", snapshot?.AcquisitionOrdinal ?? 0), project.Id, runtimeGeneration);
        }).ToArray();
    }

    private static string DisplayName(string? name, string? id) => string.IsNullOrWhiteSpace(name) ? id ?? "—" : $"{name} ({id})";

    private static string Format(string key, params object[] arguments) => string.Format(CultureInfo.CurrentCulture, OpenVisionLanguageService.T(key), arguments);

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
            VirtualCameraState.Exposing
            or VirtualCameraState.Transferring
            or VirtualCameraState.AwaitingExternalResult
            => OpenVisionLanguageService.T("Shell.ResultPending"),
        _ => "—"
    };

    internal string CurrentCameraResultSourceText
    {
        get
        {
            if (ExternalResultEvidence is not null)
            {
                return HasVerifiedExternalResult
                    ? OpenVisionLanguageService.T("Camera.ResultSourceExternalVerified")
                    : OpenVisionLanguageService.T("Camera.ResultSourceExternalUnverified");
            }

            if (Projection.Snapshot?.Result is { } result
                && (result.InspectionEvidence is not null
                    || result.Decision is PlaceholderInspectionDecision.Pass or PlaceholderInspectionDecision.Fail))
            {
                return OpenVisionLanguageService.T("Camera.ResultSourceMock");
            }

            return Projection.Snapshot?.State is
                VirtualCameraState.Exposing
                or VirtualCameraState.Transferring
                or VirtualCameraState.AwaitingExternalResult
                ? OpenVisionLanguageService.T("Camera.ResultSourcePending")
                : OpenVisionLanguageService.T("Camera.ResultSourceNone");
        }
    }

    internal string CurrentCameraVerificationLevelText => HasVerifiedExternalResult
        ? OpenVisionLanguageService.T("Camera.VerificationExternal")
        : ExternalResultEvidence is not null
            ? OpenVisionLanguageService.T("Camera.VerificationNotVerified")
            : Projection.Snapshot?.Result?.InspectionEvidence is not null
                ? OpenVisionLanguageService.T("Camera.VerificationModelOnly")
                : OpenVisionLanguageService.T("Camera.VerificationNotVerified");

    internal string CurrentCameraInputHashText => ExternalResultEvidence?.Correlation.InputSha256
        ?? Projection.Snapshot?.FrameEvidence?.ContentSha256
        ?? "—";

    internal string CurrentCameraModelKindText
    {
        get
        {
            if (ExternalResultEvidence is { } external)
            {
                var consumer = external.Correlation.ConsumerBuild;
                return string.IsNullOrWhiteSpace(consumer.ApplicationId)
                    || string.IsNullOrWhiteSpace(consumer.Version)
                    ? OpenVisionLanguageService.T("Camera.ModelUnknownAdapter")
                    : string.Format(
                        CultureInfo.CurrentCulture,
                        OpenVisionLanguageService.T(
                            "Camera.ModelExternalAdapter",
                            "외부 어댑터 · {0} {1}",
                            "External adapter · {0} {1}"),
                        consumer.ApplicationId,
                        consumer.Version);
            }

            return Projection.Snapshot?.Result?.InspectionEvidence is not null
                ? OpenVisionLanguageService.T("Camera.ModelDeterministicMock")
                : "—";
        }
    }

    internal string CurrentCameraClockModeText
    {
        get
        {
            if (Projection.SimulationFixedStep <= TimeSpan.Zero)
            {
                return OpenVisionLanguageService.T("Camera.ClockUnknown");
            }

            var milliseconds = Projection.SimulationFixedStep.TotalMilliseconds
                .ToString("G", CultureInfo.InvariantCulture);
            return string.Format(
                CultureInfo.CurrentCulture,
                OpenVisionLanguageService.T(
                    "Camera.ClockFixedStep",
                    "고정 스텝 시뮬레이션 ({0} ms/tick)",
                    "Fixed-step simulation ({0} ms/tick)"),
                milliseconds);
        }
    }

    internal string CurrentCameraFrameText => Projection.Snapshot?.CurrentAcquisitionId ?? "—";

    internal string CurrentCameraExposureTicksText => (Projection.Snapshot?.ExposureTicksRemaining ?? 0)
        .ToString(CultureInfo.InvariantCulture);

    internal string CurrentCameraTransferTicksText => (Projection.Snapshot?.TransferTicksRemaining ?? 0)
        .ToString(CultureInfo.InvariantCulture);

    internal string CurrentCameraSourceText => Projection.ImageSource?.SourceRelativePath ?? "—";

    internal string CurrentCameraInputKindText => !Projection.HasCameraDefinition
        ? OpenVisionLanguageService.T("Camera.SourceModeUnavailable")
        : Projection.ImageSource is { SourceRelativePath.Length: > 0 } source
            ? string.Format(OpenVisionLanguageService.T("Camera.InputSingleImage"), source.SourceRelativePath, source.Width, source.Height, source.PixelFormat)
            : OpenVisionLanguageService.T("Camera.InputNotConfigured");

    /// <summary>
    /// Returns the project-owned image captured by the latest virtual-camera
    /// frame. The path is exposed only after a frame has been acquired so the
    /// view cannot mistake a configured source for a captured frame.
    /// </summary>
    internal string? CurrentCameraImagePath => ResolveCapturedImagePath();

    internal bool HasCurrentCameraImage => CurrentCameraImagePath is not null;

    internal string CurrentCameraSourceModeText
    {
        get
        {
            if (!Projection.HasCameraDefinition)
            {
                return OpenVisionLanguageService.T("Camera.SourceModeUnavailable");
            }

            if (Projection.ControlOwner == SimulationControlOwner.EmbeddedSequence
                || Projection.IsAutomaticRunActive
                || Projection.ActiveSequenceStatus == SequenceExecutionStatus.Running)
            {
                return OpenVisionLanguageService.T("Camera.SourceModeAutomatic");
            }

            if (Projection.ControlOwner == SimulationControlOwner.Manual)
            {
                return HasUsableCameraImageSource
                    ? OpenVisionLanguageService.T("Camera.SourceModeManual")
                    : OpenVisionLanguageService.T("Camera.SourceModeManualUnavailable");
            }

            return HasUsableCameraImageSource
                ? OpenVisionLanguageService.T("Camera.SourceModeManualReady")
                : OpenVisionLanguageService.T("Camera.SourceModeAutomaticOnly");
        }
    }

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

    internal string CurrentCameraWorkpieceComponentIdText =>
        Projection.Snapshot?.Result?.WorkpieceComponentId ?? "—";

    internal string CurrentCameraWorkpieceInstanceIdText =>
        Projection.Snapshot?.Result?.WorkpieceInstanceId ?? "—";

    internal bool HasUsableCameraImageSource => !string.IsNullOrWhiteSpace(Projection.ProjectPath)
        && !string.IsNullOrWhiteSpace(Projection.SelectedCameraRecipe)
        && (Projection.NumericalInput is { } settings && settings.Validate() is null && settings.ConfirmedVersion == settings.Version
            && settings.RecipeName == Projection.SelectedCameraRecipe
            || Projection.ImageSource is
        {
            SourceRelativePath.Length: > 0,
            Width: > 0,
            Height: > 0,
            PixelFormat.Length: > 0
        });

    internal string CameraCommissioningHintText => !Projection.HasCameraDefinition
        ? OpenVisionLanguageService.T("Camera.NoCameraHint")
        : !HasUsableCameraImageSource
            ? OpenVisionLanguageService.T(Projection.NumericalInput is not null ? "Camera.ConfigureNumericalSourceHint" : "Camera.ConfigureSourceHint")
            : Projection.ControlOwner == SimulationControlOwner.Manual
                ? Projection.IsRunning
                    ? OpenVisionLanguageService.T("Camera.PauseBeforeTriggerHint")
                    : Projection.Snapshot?.State is
                        VirtualCameraState.Exposing or VirtualCameraState.Transferring
                        ? OpenVisionLanguageService.T("Camera.StepAcquisitionHint")
                        : Projection.Snapshot?.State == VirtualCameraState.AwaitingExternalResult
                            ? OpenVisionLanguageService.T("Camera.ExternalResultPendingHint")
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

    private VirtualCameraExternalResultEvidence? ExternalResultEvidence =>
        Projection.Snapshot?.ExternalResultEvidence
        ?? Projection.Snapshot?.Result?.ExternalResultEvidence;

    private bool HasVerifiedExternalResult
    {
        get
        {
            var external = ExternalResultEvidence;
            return external is not null
                && external.Status == ExternalInspectionResultStatus.Completed
                && external.Outcome is ExternalInspectionOutcome.Pass or ExternalInspectionOutcome.Ng
                && IsSha256(external.Correlation.InputSha256)
                && !string.IsNullOrWhiteSpace(external.Correlation.ConsumerBuild.ApplicationId)
                && !string.IsNullOrWhiteSpace(external.Correlation.ConsumerBuild.Version);
        }
    }

    private static bool IsSha256(string? value) => !string.IsNullOrWhiteSpace(value)
        && value.Length == 64
        && value.All(Uri.IsHexDigit);

    private string? ResolveCapturedImagePath()
    {
        var snapshot = Projection.Snapshot;
        if (snapshot is null
            || snapshot.CurrentAcquisitionId is null
            || snapshot.State is not (VirtualCameraState.FrameReady or VirtualCameraState.AwaitingExternalResult)
            || snapshot.NumericalInput is not null
            || string.IsNullOrWhiteSpace(Projection.ProjectPath))
        {
            return null;
        }

        var relativePath = snapshot.FrameEvidence?.SourceRelativePath
            ?? Projection.ImageSource?.SourceRelativePath;
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return null;
        }

        var projectPath = Path.GetFullPath(Projection.ProjectPath);
        var projectDirectory = File.Exists(projectPath)
            ? Path.GetDirectoryName(projectPath)
            : Directory.Exists(projectPath)
                ? projectPath
                : null;
        if (string.IsNullOrWhiteSpace(projectDirectory))
        {
            return null;
        }

        var candidate = Path.GetFullPath(Path.Combine(projectDirectory, relativePath));
        var normalizedDirectory = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(projectDirectory));
        var directoryPrefix = normalizedDirectory + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(directoryPrefix, StringComparison.OrdinalIgnoreCase)
            || !File.Exists(candidate))
        {
            return null;
        }

        return candidate;
    }

    private CameraCommissioningProjection Projection => _projection
        ?? throw new InvalidOperationException("Camera commissioning projection has not been initialized.");
}
