using System.Windows.Input;
using OpenVisionLab;
using OpenVisionLab.Machine.Core.Devices;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Simulation.Commands;
using OpenVisionLab.Machine.Simulation.Engine;
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
        nameof(SelectedVirtualCamera), nameof(SelectedCameraId), nameof(CurrentCameraRecipes),
        nameof(SelectedCameraRecipe), nameof(CurrentCameraName), nameof(CurrentCameraStateText),
        nameof(CurrentCameraResultText), nameof(CurrentCameraFrameText),
        nameof(CurrentCameraExposureTicksText), nameof(CurrentCameraTransferTicksText),
        nameof(CurrentCameraSourceText), nameof(CurrentCameraSourceModeText),
        nameof(CurrentCameraImagePath), nameof(HasCurrentCameraImage),
        nameof(CurrentCameraResultSourceText), nameof(CurrentCameraVerificationLevelText),
        nameof(CurrentCameraInputHashText), nameof(CurrentCameraModelKindText),
        nameof(CurrentCameraClockModeText),
        nameof(CurrentCameraFrameHashText), nameof(CurrentCameraInspectionIdText),
        nameof(CurrentCameraInspectionMessageText), nameof(CurrentCameraInspectionMetricsText),
        nameof(CurrentVisionEvidenceHashText), nameof(VisionEvidenceStatusText),
        nameof(VisionEvidenceComparisonText), nameof(CurrentCameraEvidenceDetailsText),
        nameof(CameraCommissioningHintText), nameof(CanStartManualCameraControl),
        nameof(CanTriggerCamera)
    ];

    private static readonly string[] ModePresentationPropertyNames =
    [
        nameof(CanStartManualCameraControl), nameof(CanTriggerCamera)
    ];

    private readonly Func<MachineProjectDocument> _projectAccessor;
    private readonly Func<CameraCommissioningProjection> _projectionAccessor;
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
    private bool _sessionCloseRequested;
    private bool _disposed;

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
        Action<Exception> handleCommandException)
    {
        _projectAccessor = projectAccessor ?? throw new ArgumentNullException(nameof(projectAccessor));
        _projectionAccessor = projectionAccessor ?? throw new ArgumentNullException(nameof(projectionAccessor));
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
        Selection = new(
            _projectAccessor,
            ImageSourceEditor.SelectCamera,
            _visionExecutionEvidence.RefreshContext,
            applyCurrentSnapshot ?? throw new ArgumentNullException(nameof(applyCurrentSnapshot)),
            OnPropertyChanged,
            () => NotifyPresentationChanged());
        _manualCameraTriggerWorkflow = new(
            currentSnapshotAccessor ?? throw new ArgumentNullException(nameof(currentSnapshotAccessor)),
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
    }

    public CameraImageSourceEditorViewModel ImageSourceEditor { get; }

    public ICommand StartManualCameraControlCommand { get; }

    public ICommand TriggerCameraCommand { get; }

    internal CameraSelectionWorkflow Selection { get; }

    internal VisionExecutionEvidenceViewModel VisionEvidence => _visionExecutionEvidence;

    internal bool IsDisposed => _disposed;

    public string CameraCountText => _projectAccessor().Devices
        .Count(device => device.Kind == DeviceKind.Camera)
        .ToString(System.Globalization.CultureInfo.InvariantCulture);

    public bool HasVirtualCamera => _projectAccessor().Devices.Any(device => device.Kind == DeviceKind.Camera);

    public IReadOnlyList<DeviceDefinition> VirtualCameras => _projectAccessor().Devices
        .Where(device => device.Kind == DeviceKind.Camera)
        .ToArray();

    public DeviceDefinition? SelectedVirtualCamera => Selection.SelectedVirtualCamera;

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
    public string? CurrentCameraImagePath => _presentation.CurrentCameraImagePath;
    public bool HasCurrentCameraImage => _presentation.HasCurrentCameraImage;
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
        _visionExecutionEvidence.RefreshLocalization();
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
        var projection = _projectionAccessor();
        var isModeOnlyChange = IsModeOnlyChange(_lastProjection, projection);
        _lastProjection = projection;
        _presentation.ApplyProjection(projection);
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

    private async Task TriggerSelectedCameraAsync()
    {
        var request = _requestFactory.TryCreate(_triggerRequestInputAccessor());
        if (request is null)
        {
            return;
        }

        var result = await _acquisitionParticipant.TrackAsync(async preparationToken =>
        {
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
                _runtimeCancellationTokenAccessor(),
                preparationToken);
            return await _manualCameraTriggerWorkflow.ExecuteAsync(request, cancellation.Token);
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
