using System.Globalization;

namespace OpenVisionLab.Machine.Simulation.Camera;

/// <summary>
/// Advances one virtual acquisition by exact fixed-step tick counts.
/// </summary>
public sealed class DeterministicVirtualCamera
{
    private readonly VirtualCameraConfiguration _configuration;
    private VirtualCameraState _state = VirtualCameraState.Idle;
    private long _acquisitionOrdinal;
    private string? _currentAcquisitionId;
    private string? _currentRecipeId;
    private int _exposureTicksRemaining;
    private int _transferTicksRemaining;
    private VirtualCameraAcquisitionResult? _result;
    private VirtualCameraFrameEvidence? _frameEvidence;
    private VirtualCameraInspectionEvidence? _inspectionEvidence;

    public DeterministicVirtualCamera(VirtualCameraConfiguration configuration)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
    }

    public string Id => _configuration.Id;
    public string Name => _configuration.Name;
    public VirtualCameraState State => _state;

    /// <summary>
    /// Starts an acquisition from Idle or FrameReady. Busy and faulted cameras
    /// reject the request without changing their active acquisition.
    /// </summary>
    public VirtualCameraTriggerResult Trigger(
        string? recipeId,
        VirtualCameraFrameEvidence? frameEvidence = null,
        VirtualCameraInspectionEvidence? inspectionEvidence = null)
    {
        if (string.IsNullOrWhiteSpace(recipeId))
        {
            return VirtualCameraTriggerResult.Rejected(
                VirtualCameraTriggerErrorCode.RecipeIdRequired,
                _acquisitionOrdinal);
        }

        if (_state is VirtualCameraState.Exposing or VirtualCameraState.Transferring)
        {
            return VirtualCameraTriggerResult.Rejected(
                VirtualCameraTriggerErrorCode.CameraBusy,
                _acquisitionOrdinal);
        }

        if (_state == VirtualCameraState.Faulted)
        {
            return VirtualCameraTriggerResult.Rejected(
                VirtualCameraTriggerErrorCode.CameraFaulted,
                _acquisitionOrdinal);
        }

        var nextOrdinal = _acquisitionOrdinal + 1;
        var nextAcquisitionId = string.Concat(
            Id,
            "/frame/",
            nextOrdinal.ToString("D8", CultureInfo.InvariantCulture));
        if (frameEvidence is not null && !string.Equals(
                frameEvidence.FrameId,
                nextAcquisitionId,
                StringComparison.Ordinal))
        {
            return VirtualCameraTriggerResult.Rejected(
                VirtualCameraTriggerErrorCode.FrameEvidenceInvalid,
                _acquisitionOrdinal);
        }
        if (inspectionEvidence is not null
            && (frameEvidence is null
                || inspectionEvidence.AcquisitionId != nextAcquisitionId
                || inspectionEvidence.CameraId != Id
                || inspectionEvidence.RecipeId != recipeId
                || inspectionEvidence.FrameId != frameEvidence.FrameId))
        {
            return VirtualCameraTriggerResult.Rejected(
                VirtualCameraTriggerErrorCode.InspectionEvidenceInvalid,
                _acquisitionOrdinal);
        }

        _acquisitionOrdinal = nextOrdinal;
        _currentAcquisitionId = nextAcquisitionId;
        _currentRecipeId = recipeId;
        _exposureTicksRemaining = _configuration.ExposureTicks;
        _transferTicksRemaining = 0;
        _result = null;
        _frameEvidence = frameEvidence;
        _inspectionEvidence = inspectionEvidence;
        _state = VirtualCameraState.Exposing;

        return VirtualCameraTriggerResult.Accepted(
            _currentAcquisitionId,
            _acquisitionOrdinal);
    }

    /// <summary>
    /// Advances the active acquisition by exactly one fixed simulation tick.
    /// Exposure and transfer never consume the same tick.
    /// </summary>
    public VirtualCameraTickResult Tick()
    {
        VirtualCameraTickTransition transition = VirtualCameraTickTransition.None;
        VirtualCameraAcquisitionResult? completedAcquisition = null;

        if (_state == VirtualCameraState.Exposing)
        {
            _exposureTicksRemaining--;

            if (_exposureTicksRemaining == 0)
            {
                _transferTicksRemaining = _configuration.TransferTicks;
                _state = VirtualCameraState.Transferring;
                transition = VirtualCameraTickTransition.ExposureCompleted;
            }
        }
        else if (_state == VirtualCameraState.Transferring)
        {
            _transferTicksRemaining--;

            if (_transferTicksRemaining == 0)
            {
                _result = new VirtualCameraAcquisitionResult(
                    _currentAcquisitionId!,
                    Id,
                    _currentRecipeId!,
                    _acquisitionOrdinal,
                    _inspectionEvidence?.Decision ?? _configuration.PlaceholderDecision,
                    _frameEvidence,
                    _inspectionEvidence);
                _state = VirtualCameraState.FrameReady;
                transition = VirtualCameraTickTransition.FrameReady;
                completedAcquisition = _result;
            }
        }

        return new VirtualCameraTickResult(
            CaptureSnapshot(),
            transition,
            completedAcquisition);
    }

    /// <summary>
    /// Enters the explicit fault state for deterministic future fault injection.
    /// Reset is the only recovery path.
    /// </summary>
    public VirtualCameraSnapshot Fault()
    {
        _state = VirtualCameraState.Faulted;
        _exposureTicksRemaining = 0;
        _transferTicksRemaining = 0;
        _result = null;
        _frameEvidence = null;
        _inspectionEvidence = null;
        return CaptureSnapshot();
    }

    public void Reset()
    {
        _state = VirtualCameraState.Idle;
        _acquisitionOrdinal = 0;
        _currentAcquisitionId = null;
        _currentRecipeId = null;
        _exposureTicksRemaining = 0;
        _transferTicksRemaining = 0;
        _result = null;
        _frameEvidence = null;
        _inspectionEvidence = null;
    }

    public VirtualCameraSnapshot CaptureSnapshot() =>
        new(
            Id,
            Name,
            _state,
            _acquisitionOrdinal,
            _currentAcquisitionId,
            _currentRecipeId,
            _exposureTicksRemaining,
            _transferTicksRemaining,
            _result,
            _frameEvidence);
}
