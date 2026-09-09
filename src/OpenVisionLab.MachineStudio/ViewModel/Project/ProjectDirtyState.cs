namespace OpenVisionLab.MachineStudio.ViewModel;

/// <summary>
/// Owns the saved-project evidence baseline and the resulting dirty state.
/// Evidence capture is supplied by the project owner so this type stays
/// independent from WPF, persistence, and runtime objects.
/// </summary>
internal sealed class ProjectDirtyState
{
    private readonly Func<string> _captureEvidence;
    private string _savedEvidence = string.Empty;

    internal ProjectDirtyState(Func<string> captureEvidence)
    {
        _captureEvidence = captureEvidence
            ?? throw new ArgumentNullException(nameof(captureEvidence));
    }

    internal bool HasUnsavedChanges { get; private set; }

    internal bool Refresh()
    {
        var currentEvidence = _captureEvidence();
        return SetDirty(!string.Equals(
            _savedEvidence,
            currentEvidence,
            StringComparison.Ordinal));
    }

    internal bool AcceptAsSaved()
    {
        _savedEvidence = _captureEvidence();
        return SetDirty(false);
    }

    private bool SetDirty(bool value)
    {
        if (HasUnsavedChanges == value)
        {
            return false;
        }

        HasUnsavedChanges = value;
        return true;
    }
}
