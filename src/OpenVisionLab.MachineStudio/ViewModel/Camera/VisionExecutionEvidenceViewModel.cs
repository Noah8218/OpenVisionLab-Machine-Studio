using System.Globalization;
using System.IO;
using OpenVisionLab;
using OpenVisionLab.Machine.Simulation.Events;
using OpenVisionLab.Machine.Simulation.Scenarios;
using OpenVisionLab.Machine.Simulation.Snapshots;

namespace OpenVisionLab.MachineStudio.ViewModel;

internal readonly record struct VisionEvidenceContext(
    string ProjectId,
    string ProjectJson,
    string BuildIdentity,
    string? ProjectPath,
    string? CameraId,
    string? RecipeId,
    Func<string, bool>? ValidateFrameSource = null);

/// <summary>
/// Owns the lifecycle of one project-linked deterministic Vision execution
/// artifact. Camera acquisition and engine command dispatch remain outside this
/// ViewModel; this type only records, validates, persists, and presents the
/// resulting evidence through explicit callbacks.
/// </summary>
internal sealed class VisionExecutionEvidenceViewModel : ViewModelBase, IDisposable
{
    private enum ArtifactState
    {
        None,
        MemoryOnly,
        Saved,
        Restored,
        Imported,
        StaleRejected,
        SaveFailed
    }

    private readonly Func<VisionEvidenceContext> _getContext;
    private readonly Action<string> _log;
    private readonly Action<bool> _notifyParentPresentationChanged;
    private DeterministicVisionExecutionRecorder? _activeRecorder;
    private DeterministicVisionExecutionEvidencePackage? _latestEvidence;
    private DeterministicVisionExecutionComparison? _comparison;
    private ArtifactState _artifactState;
    private int _disposed;

    internal VisionExecutionEvidenceViewModel(
        Func<VisionEvidenceContext> getContext,
        Action<string> log,
        Action<bool> notifyParentPresentationChanged)
    {
        _getContext = getContext ?? throw new ArgumentNullException(nameof(getContext));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _notifyParentPresentationChanged = notifyParentPresentationChanged
            ?? throw new ArgumentNullException(nameof(notifyParentPresentationChanged));
    }

    internal bool IsCapturing => _activeRecorder is not null;

    internal DeterministicVisionExecutionEvidencePackage? LatestEvidence => _latestEvidence;

    internal DeterministicVisionExecutionComparison? Comparison => _comparison;

    internal string EvidenceHashText => _latestEvidence?.ShortEvidenceHash ?? "—";

    internal string StatusText => _activeRecorder is not null
        ? OpenVisionLanguageService.T("Camera.EvidenceCapturing")
        : OpenVisionLanguageService.T(_artifactState switch
        {
            ArtifactState.Saved => "Camera.EvidenceSaved",
            ArtifactState.Restored => "Camera.EvidenceRestored",
            ArtifactState.Imported => "Camera.EvidenceImported",
            ArtifactState.StaleRejected => "Camera.EvidenceStale",
            ArtifactState.SaveFailed => "Camera.EvidenceSaveFailed",
            _ => "Camera.EvidenceNone"
        });

    internal string ComparisonText => _comparison switch
    {
        null => OpenVisionLanguageService.T("Camera.EvidenceNoComparison"),
        { IsMatch: true } => OpenVisionLanguageService.T("Camera.EvidenceMatch"),
        { MismatchCode: { } mismatchCode } => string.Format(
            CultureInfo.CurrentCulture,
            OpenVisionLanguageService.T("Camera.EvidenceMismatch"),
            mismatchCode),
        _ => OpenVisionLanguageService.T("Camera.EvidenceNoComparison")
    };

    internal void BeginCapture(DeterministicVisionExecutionRecorder recorder)
    {
        ArgumentNullException.ThrowIfNull(recorder);
        if (IsDisposed)
        {
            return;
        }

        _activeRecorder = recorder;
        RaiseChanged(invalidateCommands: false);
    }

    internal void RecordEvent(SimulationEvent runtimeEvent, SimulationSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(runtimeEvent);
        ArgumentNullException.ThrowIfNull(snapshot);
        if (IsDisposed || _activeRecorder is null)
        {
            return;
        }

        _activeRecorder.RecordEvent(runtimeEvent);
        TryComplete(snapshot);
    }

    internal bool TryComplete(SimulationSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (IsDisposed)
        {
            return false;
        }

        var recorder = _activeRecorder;
        if (recorder is null || !recorder.CanComplete(snapshot))
        {
            return false;
        }

        try
        {
            var package = recorder.Complete(snapshot);
            _comparison = _latestEvidence?.CompareTo(package);
            _latestEvidence = package;
            _activeRecorder = null;
            _artifactState = ArtifactState.MemoryOnly;
            PersistCore(_getContext());
            _log(
                $"Execution evidence completed · {package.ShortEvidenceHash}" +
                (_comparison is null
                    ? string.Empty
                    : _comparison.IsMatch
                        ? " · repeat match"
                        : $" · {_comparison.MismatchCode}"));
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or InvalidOperationException
                or ArgumentException)
        {
            _activeRecorder = null;
            _artifactState = ArtifactState.SaveFailed;
            _log($"Execution evidence failed · {exception.Message}");
        }

        RaiseChanged(invalidateCommands: false);
        return true;
    }

    internal void Restore()
    {
        if (IsDisposed)
        {
            return;
        }

        _activeRecorder = null;
        _latestEvidence = null;
        _comparison = null;
        _artifactState = ArtifactState.None;
        var context = _getContext();
        if (string.IsNullOrWhiteSpace(context.ProjectPath))
        {
            RaiseChanged(invalidateCommands: false);
            return;
        }

        var path = ArtifactPath(context.ProjectPath);
        var package = DeterministicVisionExecutionEvidencePackage.LoadFromJson(path);
        if (package is null)
        {
            _artifactState = File.Exists(path)
                ? ArtifactState.StaleRejected
                : ArtifactState.None;
        }
        else
        {
            _latestEvidence = package;
            _artifactState = IsForContext(package, context)
                ? ArtifactState.Restored
                : ArtifactState.StaleRejected;
        }

        _log(
            _artifactState == ArtifactState.Restored
                ? "Saved execution evidence restored"
                : _artifactState == ArtifactState.StaleRejected
                    ? "Saved execution evidence rejected because project, build, camera, recipe, or frame source context changed"
                    : "No saved execution evidence found");
        RaiseChanged(invalidateCommands: false);
    }

    internal void Persist()
    {
        if (IsDisposed || _latestEvidence is null)
        {
            return;
        }

        var context = _getContext();
        if (string.IsNullOrWhiteSpace(context.ProjectPath))
        {
            return;
        }

        PersistCore(context);
        RaiseChanged(invalidateCommands: false);
    }

    internal void RelinkProjectPath(string projectPath)
    {
        if (IsDisposed)
        {
            return;
        }

        if (_latestEvidence is not null)
        {
            _latestEvidence = _latestEvidence with
            {
                ProjectPath = Path.GetFullPath(projectPath)
            };
        }
    }

    internal void RefreshContext()
    {
        if (IsDisposed)
        {
            return;
        }

        _activeRecorder = null;

        var context = _getContext();
        _artifactState = _latestEvidence switch
        {
            null when _artifactState == ArtifactState.StaleRejected =>
                ArtifactState.StaleRejected,
            null => ArtifactState.None,
            { } package when IsForContext(package, context) => _artifactState switch
                {
                    ArtifactState.Restored => ArtifactState.Restored,
                    ArtifactState.SaveFailed => ArtifactState.SaveFailed,
                    ArtifactState.MemoryOnly => ArtifactState.MemoryOnly,
                    ArtifactState.Imported => ArtifactState.Imported,
                    _ => ArtifactState.Saved
                },
            _ => ArtifactState.StaleRejected
        };
        RaiseChanged(invalidateCommands: false);
    }

    internal void PersistForProjectPath(string projectPath)
    {
        if (IsDisposed)
        {
            return;
        }

        RelinkProjectPath(projectPath);
        RefreshContext();
        Persist();
    }

    internal void SetImportedEvidence(
        DeterministicVisionExecutionEvidencePackage? evidence)
    {
        if (IsDisposed)
        {
            return;
        }

        _latestEvidence = evidence;
        _comparison = null;
        _artifactState = evidence switch
        {
            null => ArtifactState.None,
            { } package when IsForContext(package, _getContext()) => ArtifactState.Imported,
            _ => ArtifactState.StaleRejected
        };
        RaiseChanged(invalidateCommands: false);
    }

    internal void Clear()
    {
        if (IsDisposed)
        {
            return;
        }

        _activeRecorder = null;
        _latestEvidence = null;
        _comparison = null;
        _artifactState = ArtifactState.None;
        RaiseChanged(invalidateCommands: false);
    }

    internal void CancelCapture()
    {
        if (IsDisposed || _activeRecorder is null)
        {
            return;
        }

        _activeRecorder = null;
        RefreshContext();
    }

    internal DeterministicVisionExecutionEvidencePackage? GetCurrentEvidence()
    {
        var evidence = IsDisposed ? null : _latestEvidence;
        if (evidence is null)
        {
            return null;
        }

        var context = _getContext();
        return IsForContext(evidence, context)
            ? evidence
            : null;
    }

    internal void RefreshLocalization()
    {
        if (!IsDisposed)
        {
            RaiseChanged(invalidateCommands: false);
        }
    }

    private void PersistCore(VisionEvidenceContext context)
    {
        if (IsDisposed
            || _latestEvidence is null
            || string.IsNullOrWhiteSpace(context.ProjectPath))
        {
            return;
        }

        if (!IsForContext(_latestEvidence, context))
        {
            _artifactState = ArtifactState.StaleRejected;
            return;
        }

        try
        {
            DeterministicVisionExecutionEvidencePackage.SaveToJson(
                _latestEvidence,
                ArtifactPath(context.ProjectPath));
            _artifactState = ArtifactState.Saved;
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or InvalidOperationException)
        {
            _artifactState = ArtifactState.SaveFailed;
            _log($"Execution evidence save failed · {exception.Message}");
        }
    }

    private void RaiseChanged(bool invalidateCommands)
    {
        if (IsDisposed)
        {
            return;
        }

        OnPropertyChanged(nameof(IsCapturing));
        OnPropertyChanged(nameof(EvidenceHashText));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(ComparisonText));
        if (!IsDisposed)
        {
            _notifyParentPresentationChanged(invalidateCommands);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _activeRecorder = null;
        _latestEvidence = null;
        _comparison = null;
        _artifactState = ArtifactState.None;
    }

    private static string ArtifactPath(string projectPath) =>
        $"{Path.GetFullPath(projectPath)}.vision-result.json";

    private static bool IsForContext(
        DeterministicVisionExecutionEvidencePackage package,
        VisionEvidenceContext context)
    {
        if (!package.IsForContext(
                context.ProjectId,
                context.ProjectJson,
                context.BuildIdentity,
                context.CameraId,
                context.RecipeId))
        {
            return false;
        }

        try
        {
            return context.ValidateFrameSource?.Invoke(package.FrameHash) ?? true;
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or ArgumentException
                or InvalidOperationException)
        {
            return false;
        }
    }

    private bool IsDisposed => Volatile.Read(ref _disposed) != 0;
}
