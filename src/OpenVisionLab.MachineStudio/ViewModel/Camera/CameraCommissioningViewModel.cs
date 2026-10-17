using System.Windows.Input;
using OpenVisionLab;
using OpenVisionLab.Machine.Core.Devices;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Core.Sequences;
using OpenVisionLab.Machine.Simulation.Commands;
using OpenVisionLab.Machine.Simulation.Engine;
using OpenVisionLab.Machine.Simulation.Camera;
using OpenVisionLab.Machine.Simulation.Scenarios;
using OpenVisionLab.Machine.Simulation.Snapshots;
using OpenVisionLab.Machine.Vision.Models;

namespace OpenVisionLab.MachineStudio.ViewModel;

/// <summary>
/// Owns the camera commissioning workspace state and command lifetime. Project
/// and Runtime data enter through explicit snapshots and callbacks; the
/// workspace does not reach into the visual tree or construct modal UI.
/// </summary>
public sealed class CameraCommissioningViewModel : ViewModelBase, IDisposable
{
    private static readonly string[] PresentationPropertyNames =
    [
        nameof(CameraCountText), nameof(HasVirtualCamera), nameof(VirtualCameras),
        nameof(CurrentCameraInputs), nameof(SelectedExecutionInput), nameof(HasCurrentCameraInputs), nameof(CurrentInputSummaryText),
        nameof(SelectedInputStateText), nameof(SelectedInputContextText), nameof(SelectedInputStepContextText),
        nameof(SelectedInputAcquisitionText), nameof(HasSelectedCameraInput), nameof(CurrentInputImagePath),
        nameof(SelectedVirtualCamera), nameof(SelectedCameraId), nameof(CurrentCameraRecipes),
        nameof(SelectedCameraRecipe), nameof(CurrentCameraName), nameof(CurrentCameraStateText),
        nameof(CurrentCameraResultText), nameof(CurrentCameraFrameText),
        nameof(CurrentCameraExposureTicksText), nameof(CurrentCameraTransferTicksText),
        nameof(CurrentCameraSourceText), nameof(CurrentCameraSourceModeText), nameof(CurrentCameraInputKindText),
        nameof(CurrentCameraImagePath), nameof(HasCurrentCameraImage), nameof(HasCurrentNumericalInput),
        nameof(CurrentCameraResultSourceText), nameof(CurrentCameraVerificationLevelText),
        nameof(CurrentCameraInputHashText), nameof(CurrentCameraModelKindText),
        nameof(CurrentCameraClockModeText),
        nameof(CurrentCameraFrameHashText), nameof(CurrentCameraInspectionIdText),
        nameof(CurrentAcquisitionSteps),
        nameof(CurrentCameraInspectionMessageText), nameof(CurrentCameraInspectionMetricsText), nameof(CurrentNumericalInput),
        nameof(CurrentVisionEvidenceHashText), nameof(VisionEvidenceStatusText),
        nameof(VisionEvidenceComparisonText), nameof(CurrentCameraEvidenceDetailsText),
        nameof(CameraCommissioningHintText), nameof(CanStartManualCameraControl),
        nameof(CanTriggerCamera), nameof(CurrentCaptureOutputText), nameof(CanShowHeightData), nameof(IsHeightDataView), nameof(IsImageDataView), nameof(CurrentHeightRangeText)
    ];

    private static readonly string[] ModePresentationPropertyNames =
    [
        nameof(CanStartManualCameraControl), nameof(CanTriggerCamera)
    ];

    private readonly Func<MachineProjectDocument> _projectAccessor;
    private readonly Func<CameraCommissioningProjection> _projectionAccessor;
    private readonly Func<SimulationSnapshot> _currentSnapshotAccessor;
    private readonly Func<ManualCameraTriggerRequestInput> _triggerRequestInputAccessor;
    private readonly Func<ManualCameraSessionIdentity> _sessionIdentityAccessor;
    private readonly Func<CancellationToken> _runtimeCancellationTokenAccessor;
    private readonly Func<Task> _startManualCameraControl;
    private readonly Action _notifyIntegrationContext;
    private readonly Action<string> _setStatus;
    private readonly Action<string, string> _log;
    private readonly Action<Exception> _handleCommandException;
    private readonly CameraCommissioningPresentation _presentation = new();
    private readonly ManualCameraAcquisitionParticipant _acquisitionParticipant = new();
    private readonly ManualCameraTriggerRequestFactory _requestFactory = new();
    private readonly VisionExecutionEvidenceViewModel _visionExecutionEvidence;
    private readonly ManualCameraTriggerWorkflow _manualCameraTriggerWorkflow;
    private readonly CameraImageSourceApplicationWorkflow _imageSourceApplicationWorkflow;
    private CameraCommissioningProjection? _lastProjection;
    private IReadOnlyList<CameraExecutionInputItem> _executionInputs = Array.Empty<CameraExecutionInputItem>();
    private (DeviceDefinition Device, string Id, string Name, NumericalCameraInputKind? InputKind)[] _virtualCameraRows = [];
    private IReadOnlyList<DeviceDefinition> _virtualCameras = Array.Empty<DeviceDefinition>();
    private IReadOnlyList<CameraSequenceStepReference> _acquisitionStepReferences = Array.Empty<CameraSequenceStepReference>();
    private SimulationSnapshot? _executionSnapshot;
    private bool _sessionCloseRequested;
    private bool _disposed;
    private bool _isHeightDataView;

    internal CameraCommissioningViewModel(
        Func<MachineProjectDocument> projectAccessor,
        Func<CameraCommissioningProjection> projectionAccessor,
        Func<VisionEvidenceContext> evidenceContextAccessor,
        Func<ManualCameraSessionIdentity> sessionIdentityAccessor,
        Func<ManualCameraTriggerRequestInput> triggerRequestInputAccessor,
        Func<SimulationSnapshot> currentSnapshotAccessor,
        Func<CancellationToken> runtimeCancellationTokenAccessor,
        Func<SimulationCommand, string, Task<SimulationCommandResult>> dispatchCameraCommand,
        Action<SimulationSnapshot> applyMonitorSnapshot,
        Action applyCurrentSnapshot,
        Func<Task> startManualCameraControl,
        Action markProjectChanged,
        Action<string> setStatus,
        Action<string, string> log,
        Action notifyIntegrationContext,
        Func<string, string> localize,
        Action<Exception> handleCommandException,
        Func<bool>? canEditCapture = null)
    {
        _projectAccessor = projectAccessor ?? throw new ArgumentNullException(nameof(projectAccessor));
        _projectionAccessor = projectionAccessor ?? throw new ArgumentNullException(nameof(projectionAccessor));
        _currentSnapshotAccessor = currentSnapshotAccessor ?? throw new ArgumentNullException(nameof(currentSnapshotAccessor));
        _sessionIdentityAccessor = sessionIdentityAccessor
            ?? throw new ArgumentNullException(nameof(sessionIdentityAccessor));
        _triggerRequestInputAccessor = triggerRequestInputAccessor
            ?? throw new ArgumentNullException(nameof(triggerRequestInputAccessor));
        _runtimeCancellationTokenAccessor = runtimeCancellationTokenAccessor
            ?? throw new ArgumentNullException(nameof(runtimeCancellationTokenAccessor));
        _startManualCameraControl = startManualCameraControl
            ?? throw new ArgumentNullException(nameof(startManualCameraControl));
        _notifyIntegrationContext = notifyIntegrationContext
            ?? throw new ArgumentNullException(nameof(notifyIntegrationContext));
        _setStatus = setStatus ?? throw new ArgumentNullException(nameof(setStatus));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _handleCommandException = handleCommandException
            ?? throw new ArgumentNullException(nameof(handleCommandException));

        _visionExecutionEvidence = new(
            evidenceContextAccessor ?? throw new ArgumentNullException(nameof(evidenceContextAccessor)),
            message => _log("Vision", message),
            NotifyPresentationChanged);
        _imageSourceApplicationWorkflow = new(
            markProjectChanged ?? throw new ArgumentNullException(nameof(markProjectChanged)),
            _setStatus,
            _log,
            _visionExecutionEvidence.RefreshContext,
            () => NotifyPresentationChanged(),
            localize ?? throw new ArgumentNullException(nameof(localize)));
        ImageSourceEditor = new CameraImageSourceEditorViewModel(
            _imageSourceApplicationWorkflow.Apply);
        CaptureEditor = new CameraCaptureEditorViewModel(canEditCapture ?? (() => false), _imageSourceApplicationWorkflow.Apply);
        Selection = new(
            _projectAccessor,
            cameraId => { ImageSourceEditor.SelectCamera(cameraId); CaptureEditor.SelectCamera(cameraId); },
            _visionExecutionEvidence.RefreshContext,
            applyCurrentSnapshot ?? throw new ArgumentNullException(nameof(applyCurrentSnapshot)),
            name =>
            {
                OnPropertyChanged(name);
                if (name == nameof(SelectedCameraId)) OnPropertyChanged(nameof(CurrentAcquisitionSteps));
            },
            () => NotifyPresentationChanged());
        _manualCameraTriggerWorkflow = new(
            _currentSnapshotAccessor,
            dispatchCameraCommand ?? throw new ArgumentNullException(nameof(dispatchCameraCommand)),
            _visionExecutionEvidence,
            applyMonitorSnapshot ?? throw new ArgumentNullException(nameof(applyMonitorSnapshot)),
            _sessionIdentityAccessor);
        StartManualCameraControlCommand = new AsyncRelayCommand(
            async _ => await _startManualCameraControl(),
            _ => CanStartManualCameraControl,
            _handleCommandException,
            useCommandManagerRequery: false);
        TriggerCameraCommand = new AsyncRelayCommand(
            _ => TriggerSelectedCameraAsync(),
            _ => CanTriggerCamera,
            _handleCommandException,
            useCommandManagerRequery: false);
        SelectCameraCommand = new RelayCommand(value => SelectedCameraId = value as string, _ => !_disposed);
        ShowInputOutputCommand = new RelayCommand(value => IsHeightDataView = value as string == "height", value => !_disposed && (value as string != "height" || CanShowHeightData));
    }

    public CameraImageSourceEditorViewModel ImageSourceEditor { get; }
    public CameraCaptureEditorViewModel CaptureEditor { get; }

    public ICommand StartManualCameraControlCommand { get; }

    public ICommand TriggerCameraCommand { get; }

    public ICommand SelectCameraCommand { get; }

    public ICommand ShowInputOutputCommand { get; }

    internal CameraSelectionWorkflow Selection { get; }

    internal VisionExecutionEvidenceViewModel VisionEvidence => _visionExecutionEvidence;

    internal bool IsDisposed => _disposed;

    public string CameraCountText => _projectAccessor().Devices
        .Count(device => device.Kind == DeviceKind.Camera)
        .ToString(System.Globalization.CultureInfo.InvariantCulture);

    public bool HasVirtualCamera => _projectAccessor().Devices.Any(device => device.Kind == DeviceKind.Camera);

    public IReadOnlyList<DeviceDefinition> VirtualCameras
    {
        get
        {
            var rows = _projectAccessor().Devices.Where(device => device.Kind == DeviceKind.Camera)
                .Select(device => (Device: device, device.Id, device.Name, InputKind: device.Camera?.NumericalInput?.InputKind)).ToArray();
            if (!rows.SequenceEqual(_virtualCameraRows))
            {
                _virtualCameraRows = rows;
                _virtualCameras = rows.Select(row => row.Device).ToArray();
            }
            return _virtualCameras;
        }
    }

    public DeviceDefinition? SelectedVirtualCamera => Selection.SelectedVirtualCamera;

    public IReadOnlyList<CameraSequenceStepReference> CurrentAcquisitionSteps
    {
        get
        {
            var project = _projectAccessor();
            var rows = SelectedVirtualCamera is null ? Array.Empty<CameraSequenceStepReference>() : project.Sequences
                .SelectMany(sequence => sequence.Steps.Select((step, index) => (Sequence: sequence, Step: step, Order: index + 1)))
                .Where(item => item.Step.Action == SequenceStepAction.TriggerCamera && item.Step.TargetId == SelectedCameraId)
                .Select(item => new CameraSequenceStepReference(project.Id, SelectedCameraId!, item.Sequence.Id, item.Step.Id, item.Sequence.Name,
                    OpenVisionLanguageService.TUserText("sequence", $"{item.Sequence.Id}.step.{item.Step.Id}.name", item.Step.Name), item.Order)).ToArray();
            if (!rows.SequenceEqual(_acquisitionStepReferences)) _acquisitionStepReferences = rows;
            return _acquisitionStepReferences;
        }
    }

    public IReadOnlyList<CameraExecutionInputItem> CurrentCameraInputs => _executionInputs.Where(item => item.InputCount > 0).ToArray();

    public bool HasCurrentCameraInputs => _executionInputs.Any(item => item.InputCount > 0);

    public CameraExecutionInputItem? SelectedExecutionInput => _executionInputs.FirstOrDefault(item => item.Id == SelectedCameraId);

    public bool HasSelectedCameraInput => SelectedExecutionInput?.InputCount > 0;

    public string CurrentInputSummaryText => _executionSnapshot?.ProjectId is null || _lastProjection?.IsApplyingProject == true
        ? OpenVisionLanguageService.T("Camera.InputOriginUnavailable")
        : _executionSnapshot.ProjectId != _projectAccessor().Id
            ? OpenVisionLanguageService.T("Camera.InputForeignRuntime")
            : string.Format(System.Globalization.CultureInfo.CurrentCulture, OpenVisionLanguageService.T("Camera.CurrentInputsSummary"),
                _executionSnapshot.RuntimeGeneration, _executionInputs.Sum(item => item.InputCount), _executionInputs.Count);

    public string SelectedInputStateText => (SelectedExecutionInput?.StateText ?? OpenVisionLanguageService.T("Camera.InputNotAcquired"))
        + (HasSelectedCameraInput && _lastProjection?.Snapshot?.Result is not null ? $" · {CurrentCameraResultSourceText}" : string.Empty);
    public string SelectedInputContextText => SelectedExecutionInput?.ContextText ?? "—";
    public string SelectedInputStepContextText => SelectedExecutionInput?.StepContextText ?? "—";
    public string SelectedInputAcquisitionText => SelectedExecutionInput?.AcquisitionText ?? "—";
    public string? CurrentInputImagePath => HasSelectedCameraInput ? CurrentCameraImagePath : null;

    public string? SelectedCameraId
    {
        get => Selection.SelectedCameraId;
        set
        {
            if (_disposed)
            {
                return;
            }

            Selection.SelectVirtualCamera(value);
        }
    }

    public IReadOnlyList<string> CurrentCameraRecipes => Selection.CurrentCameraRecipes;

    public string? SelectedCameraRecipe
    {
        get => Selection.SelectedCameraRecipe;
        set
        {
            if (_disposed)
            {
                return;
            }

            Selection.SelectCameraRecipe(value);
        }
    }

    public string CurrentCameraName => _presentation.CurrentCameraName;
    public string CurrentCameraStateText => _presentation.CurrentCameraStateText;
    public string CurrentCameraResultText => _presentation.CurrentCameraResultText;
    public string CurrentCameraFrameText => _presentation.CurrentCameraFrameText;
    public string CurrentCameraExposureTicksText => _presentation.CurrentCameraExposureTicksText;
    public string CurrentCameraTransferTicksText => _presentation.CurrentCameraTransferTicksText;
    public string CurrentCameraSourceText => _presentation.CurrentCameraSourceText;
    public string CurrentCameraSourceModeText => _presentation.CurrentCameraSourceModeText;
    public string CurrentCameraInputKindText => SelectedVirtualCamera?.Camera?.NumericalInput is { } settings
        ? string.Format(OpenVisionLanguageService.T("Camera.AppliedNumericalInput"),
            settings.InputKind switch { NumericalCameraInputKind.Image2D => OpenVisionLanguageService.T("Camera.TwoDImage"), NumericalCameraInputKind.HeightMap3D => OpenVisionLanguageService.T("Camera.HeightMap3D"), _ => settings.InputKind.ToString() },
            settings.RecipeName, settings.Version, settings.ConfirmedVersion)
        : _presentation.CurrentCameraInputKindText;
    public NumericalCameraInput? CurrentNumericalInput
    {
        get
        {
            var snapshot = _lastProjection?.Snapshot;
            if (snapshot is { State: VirtualCameraState.FrameReady, Result: { } result }
                && result.AcquisitionId == snapshot.CurrentAcquisitionId)
                return result.NumericalInput ?? snapshot.NumericalInput;
            return snapshot is { State: VirtualCameraState.AwaitingExternalResult, NumericalInput: { } pending }
                && pending.Frame.FrameId == snapshot.CurrentAcquisitionId ? pending : null;
        }
    }
    public bool HasCurrentNumericalInput => CurrentNumericalInput is not null;
    public bool CanShowHeightData => CurrentNumericalInput?.Frame.PixelFormat == "HeightMapFloat32LE";
    public bool IsHeightDataView
    {
        get => _isHeightDataView && CanShowHeightData;
        set
        {
            if (SetProperty(ref _isHeightDataView, value && CanShowHeightData))
            {
                OnPropertyChanged(nameof(IsImageDataView));
                OnPropertyChanged(nameof(CurrentHeightRangeText));
            }
        }
    }
    public bool IsImageDataView => !IsHeightDataView;
    public string CurrentCaptureOutputText => OpenVisionLanguageService.T(CurrentNumericalInput is { } input
        ? input.Frame.PixelFormat == "HeightMapFloat32LE" ? "Camera.HeightData" : "Camera.TwoDImage"
        : SelectedVirtualCamera?.Camera?.NumericalInput?.InputKind == NumericalCameraInputKind.HeightMap3D ? "Camera.HeightData" : "Camera.TwoDImage");
    public string CurrentHeightRangeText => IsHeightDataView && CurrentNumericalInput is { } input
        ? $"{input.Frame.Width} × {input.Frame.Height} · {input.Samples.Min():F3}–{input.Samples.Max():F3} LU · {OpenVisionLanguageService.T("Camera.NumericalHeightLegend")}" : string.Empty;
    public string? CurrentCameraImagePath => _presentation.CurrentCameraImagePath;
    public bool HasCurrentCameraImage => HasCurrentNumericalInput || _presentation.HasCurrentCameraImage;
    public string CurrentCameraResultSourceText => _presentation.CurrentCameraResultSourceText;
    public string CurrentCameraVerificationLevelText => _presentation.CurrentCameraVerificationLevelText;
    public string CurrentCameraInputHashText => _presentation.CurrentCameraInputHashText;
    public string CurrentCameraModelKindText => _presentation.CurrentCameraModelKindText;
    public string CurrentCameraClockModeText => _presentation.CurrentCameraClockModeText;
    public string CurrentCameraFrameHashText => _presentation.CurrentCameraFrameHashText;
    public string CurrentCameraInspectionIdText => _presentation.CurrentCameraInspectionIdText;
    public string CurrentCameraInspectionMessageText => _presentation.CurrentCameraInspectionMessageText;
    public string CurrentCameraInspectionMetricsText => _presentation.CurrentCameraInspectionMetricsText;
    public string CurrentVisionEvidenceHashText => _visionExecutionEvidence.EvidenceHashText;
    public string VisionEvidenceStatusText => _visionExecutionEvidence.StatusText;
    public string VisionEvidenceComparisonText => _visionExecutionEvidence.ComparisonText;
    public string CurrentCameraEvidenceDetailsText => string.Join(
        Environment.NewLine,
        $"{OpenVisionLanguageService.T("Camera.ResultSource")}: {CurrentCameraResultSourceText}",
        $"{OpenVisionLanguageService.T("Camera.WorkpieceAssociation")}: {_presentation.CurrentCameraWorkpieceComponentIdText}",
        $"{OpenVisionLanguageService.T("Camera.WorkpieceInstanceId")}: {_presentation.CurrentCameraWorkpieceInstanceIdText}",
        $"{OpenVisionLanguageService.T("Camera.VerificationLevel")}: {CurrentCameraVerificationLevelText}",
        $"{OpenVisionLanguageService.T("Camera.ClockMode")}: {CurrentCameraClockModeText}",
        $"{OpenVisionLanguageService.T("Camera.InputHash")}: {CurrentCameraInputHashText}",
        $"{OpenVisionLanguageService.T("Camera.ModelKind")}: {CurrentCameraModelKindText}",
        $"{OpenVisionLanguageService.T("Camera.InspectionId")}: {CurrentCameraInspectionIdText}",
        $"{OpenVisionLanguageService.T("Camera.InspectionMessage")}: {CurrentCameraInspectionMessageText}",
        $"{OpenVisionLanguageService.T("Camera.InspectionMetrics")}: {CurrentCameraInspectionMetricsText}",
        $"{OpenVisionLanguageService.T("Camera.ExecutionEvidence")}: {CurrentVisionEvidenceHashText}",
        VisionEvidenceStatusText,
        VisionEvidenceComparisonText);
    public string CameraCommissioningHintText => _presentation.CameraCommissioningHintText;
    public bool CanStartManualCameraControl => !_disposed
        && !_sessionCloseRequested
        && _presentation.CanStartManualCameraControl;
    public bool CanTriggerCamera => !_disposed
        && !_sessionCloseRequested
        && _presentation.CanTriggerCamera;

    internal DeterministicVisionExecutionEvidencePackage? LatestVisionEvidence =>
        _visionExecutionEvidence.LatestEvidence;

    internal DeterministicVisionExecutionComparison? VisionEvidenceComparison =>
        _visionExecutionEvidence.Comparison;

    internal DeviceDefinition? GetSelectedDefinition(string? fallbackCameraId) =>
        Selection.GetSelectedDefinition(fallbackCameraId);

    internal void LoadProject(MachineProjectDocument project, string? projectPath)
    {
        ArgumentNullException.ThrowIfNull(project);
        Selection.EnsureSelectionFor(project);
        ImageSourceEditor.Load(project, projectPath, Selection.SelectedCameraId);
        CaptureEditor.Load(project, Selection.SelectedCameraId);
        OnPropertyChanged(nameof(SelectedVirtualCamera));
        OnPropertyChanged(nameof(SelectedCameraId));
        OnPropertyChanged(nameof(CurrentCameraRecipes));
        OnPropertyChanged(nameof(SelectedCameraRecipe));
        RefreshProjection(invalidateCommands: false);
    }

    internal void SetProjectPath(string? projectPath, bool isSaved)
    {
        ImageSourceEditor.SetProjectPath(projectPath, isSaved);
        RefreshProjection(invalidateCommands: false);
    }

    internal void ClearEvidence() => _visionExecutionEvidence.Clear();

    internal void RestoreEvidence() => _visionExecutionEvidence.Restore();

    internal void SetImportedEvidence(DeterministicVisionExecutionEvidencePackage? evidence) =>
        _visionExecutionEvidence.SetImportedEvidence(evidence);

    internal void RefreshLocalization()
    {
        if (_disposed)
        {
            return;
        }

        ImageSourceEditor.RefreshLocalization();
        CaptureEditor.RefreshLocalization();
        _visionExecutionEvidence.RefreshLocalization();
        if (_lastProjection is not null) RefreshExecutionInputs(_currentSnapshotAccessor(), _lastProjection);
        foreach (var propertyName in PresentationPropertyNames)
        {
            OnPropertyChanged(propertyName);
        }
    }

    internal DeterministicVisionExecutionEvidencePackage? GetCurrentEvidence() =>
        _visionExecutionEvidence.GetCurrentEvidence();

    internal ValueTask<VirtualFrameDescriptor> AcquireFrameAsync(
        VirtualCameraInspectionRequest request,
        CancellationToken cancellationToken = default) =>
        _manualCameraTriggerWorkflow.AcquireFrameAsync(request, cancellationToken);

    internal void PersistEvidenceForProjectPath(string projectPath) =>
        _visionExecutionEvidence.PersistForProjectPath(projectPath);

    internal void RefreshProjection(bool invalidateCommands = true)
    {
        if (_disposed) return;
        var projection = _projectionAccessor();
        var snapshot = _currentSnapshotAccessor();
        bool sameRuntime = _executionSnapshot?.ProjectId == snapshot.ProjectId && _executionSnapshot?.RuntimeGeneration == snapshot.RuntimeGeneration;
        var previousInputs = _executionInputs;
        if (snapshot.ProjectId is not null || projection.IsApplyingProject)
        {
            var selected = snapshot.ProjectId == _projectAccessor().Id && !projection.IsApplyingProject
                ? snapshot.Cameras.FirstOrDefault(camera => camera.Id == SelectedCameraId)
                : null;
            projection = projection with { Snapshot = selected };
        }
        RefreshExecutionInputs(snapshot, projection);
        var isModeOnlyChange = sameRuntime && ReferenceEquals(previousInputs, _executionInputs) && IsModeOnlyChange(_lastProjection, projection);
        _lastProjection = projection;
        _presentation.ApplyProjection(projection);
        if (!CanShowHeightData) _isHeightDataView = false;
        if (isModeOnlyChange)
        {
            RaiseModePresentationChanged();
        }
        else
        {
            RaisePresentationChanged();
            _notifyIntegrationContext();
        }
        if (invalidateCommands)
        {
            InvalidateCommands();
        }
    }

    internal void NotifyPresentationChanged(bool invalidateCommands = true) =>
        RefreshProjection(invalidateCommands);

    internal void InvalidatePreparation()
    {
        _acquisitionParticipant.Invalidate();
        RaisePresentationChanged();
    }

    internal void CancelPreparation()
    {
        InvalidatePreparation();
        _visionExecutionEvidence.CancelCapture();
        RaisePresentationChanged();
    }

    internal Task<CameraAcquisitionParticipantResult> ObserveAsync(TimeSpan timeout) =>
        _acquisitionParticipant.ObserveAsync(timeout);

    internal void SetSessionCloseAdmission(bool isRequested)
    {
        if (_sessionCloseRequested == isRequested)
        {
            return;
        }

        _sessionCloseRequested = isRequested;
        if (isRequested)
        {
            CancelPreparation();
        }

        InvalidateCommands();
    }

    internal void InvalidateCommands()
    {
        if (StartManualCameraControlCommand is AsyncRelayCommand startCommand)
        {
            startCommand.RaiseCanExecuteChanged();
        }

        if (TriggerCameraCommand is AsyncRelayCommand triggerCommand)
        {
            triggerCommand.RaiseCanExecuteChanged();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ImageSourceEditor.Dispose();
        CancelPreparation();
        _acquisitionParticipant.Dispose();
        _visionExecutionEvidence.Dispose();
        InvalidateCommands();
    }

    internal Task TriggerFromInspectorAsync() => CanTriggerCamera ? TriggerSelectedCameraAsync() : Task.CompletedTask;

    private async Task TriggerSelectedCameraAsync()
    {
        var input = _triggerRequestInputAccessor();
        bool numerical = input.CameraDefinition?.NumericalInput is not null;
        var request = numerical ? null : _requestFactory.TryCreate(input);
        if (!numerical && request is null)
        {
            return;
        }

        var result = await _acquisitionParticipant.TrackAsync(async preparationToken =>
        {
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
                _runtimeCancellationTokenAccessor(),
                preparationToken);
            return numerical ? await _manualCameraTriggerWorkflow.ExecuteNumericalAsync(input, cancellation.Token)
                : await _manualCameraTriggerWorkflow.ExecuteAsync(request!, cancellation.Token);
        });
        PresentTriggerFailure(result);
    }

    private void PresentTriggerFailure(ManualCameraTriggerResult result)
    {
        switch (result.Outcome)
        {
            case ManualCameraTriggerOutcome.SourceRejected:
                _setStatus(OpenVisionLanguageService.T("Camera.StatusRejected"));
                _log("Camera", string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    OpenVisionLanguageService.T("Camera.SourceRejected"),
                    result.Detail ?? string.Empty));
                break;
            case ManualCameraTriggerOutcome.InspectionRejected:
                _setStatus(OpenVisionLanguageService.T("Camera.StatusRejected"));
                _log("Camera", string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    OpenVisionLanguageService.T("Camera.InspectionRejected"),
                    result.Detail ?? string.Empty));
                break;
            case ManualCameraTriggerOutcome.ContextChanged:
                _setStatus(OpenVisionLanguageService.T("Camera.StatusRejected"));
                _log("Camera", OpenVisionLanguageService.T("Camera.ContextChanged"));
                break;
        }
    }

    private void RaisePresentationChanged()
    {
        ((RelayCommand)ShowInputOutputCommand).RaiseCanExecuteChanged();
        foreach (var propertyName in PresentationPropertyNames)
        {
            OnPropertyChanged(propertyName);
        }
    }

    private void RaiseModePresentationChanged()
    {
        foreach (var propertyName in ModePresentationPropertyNames)
        {
            OnPropertyChanged(propertyName);
        }
    }


    private void RefreshExecutionInputs(SimulationSnapshot snapshot, CameraCommissioningProjection projection)
    {
        _executionSnapshot = snapshot;
        IEnumerable<VirtualCameraSnapshot> cameras = snapshot.Cameras;
        if (projection.IsApplyingProject || snapshot.ProjectId is not null && snapshot.ProjectId != _projectAccessor().Id)
            cameras = Array.Empty<VirtualCameraSnapshot>();
        else if (snapshot.ProjectId is null && snapshot.Cameras.Count == 0 && projection.Snapshot is not null)
            cameras = [projection.Snapshot];
        var inputs = CameraCommissioningPresentation.CreateExecutionInputs(_projectAccessor(), cameras, snapshot.RuntimeGeneration);
        if (!_executionInputs.SequenceEqual(inputs)) _executionInputs = inputs;
    }

    private static bool IsModeOnlyChange(
        CameraCommissioningProjection? previous,
        CameraCommissioningProjection current)
    {
        if (previous is null || previous.IsRunMode == current.IsRunMode)
        {
            return false;
        }

        return (previous with { IsRunMode = current.IsRunMode }) == current;
    }

}
