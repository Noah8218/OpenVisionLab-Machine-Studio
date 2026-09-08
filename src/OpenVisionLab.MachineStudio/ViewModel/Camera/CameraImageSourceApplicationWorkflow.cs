namespace OpenVisionLab.MachineStudio.ViewModel;

internal enum CameraImageSourceApplicationOutcome
{
    Applied,
    Rejected
}

internal sealed record CameraImageSourceApplicationResult(
    CameraImageSourceApplicationOutcome Outcome,
    string? CameraId,
    string Detail)
{
    internal bool IsApplied => Outcome == CameraImageSourceApplicationOutcome.Applied;
}

/// <summary>
/// Owns the camera-workspace side effects of an image-source application result.
/// Draft state, path validation, and camera mutation remain in the editor.
/// </summary>
internal sealed class CameraImageSourceApplicationWorkflow
{
    private readonly Action _markProjectChanged;
    private readonly Action<string> _setStatus;
    private readonly Action<string, string> _log;
    private readonly Action _refreshVisionEvidence;
    private readonly Action _notifyCameraCommissioningChanged;
    private readonly Func<string, string> _localize;

    internal CameraImageSourceApplicationWorkflow(
        Action markProjectChanged,
        Action<string> setStatus,
        Action<string, string> log,
        Action refreshVisionEvidence,
        Action notifyCameraCommissioningChanged,
        Func<string, string> localize)
    {
        _markProjectChanged = markProjectChanged ?? throw new ArgumentNullException(nameof(markProjectChanged));
        _setStatus = setStatus ?? throw new ArgumentNullException(nameof(setStatus));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _refreshVisionEvidence = refreshVisionEvidence ?? throw new ArgumentNullException(nameof(refreshVisionEvidence));
        _notifyCameraCommissioningChanged = notifyCameraCommissioningChanged
            ?? throw new ArgumentNullException(nameof(notifyCameraCommissioningChanged));
        _localize = localize ?? throw new ArgumentNullException(nameof(localize));
    }

    internal void Apply(CameraImageSourceApplicationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (!result.IsApplied || string.IsNullOrWhiteSpace(result.CameraId))
        {
            _setStatus(_localize("Camera.SourceMustBeProjectOwned"));
            _log("Camera", $"Image source selection rejected · {result.Detail}");
            return;
        }

        _markProjectChanged();
        _setStatus(_localize("Camera.SourceAppliedSave"));
        _log("Camera", $"Image source applied · {result.CameraId} · {result.Detail}");
        _refreshVisionEvidence();
        _notifyCameraCommissioningChanged();
    }
}
