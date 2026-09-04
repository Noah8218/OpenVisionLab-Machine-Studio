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
    string? RecipeId);

/// <summary>
/// Owns the lifecycle of one project-linked deterministic Vision execution
/// artifact. Camera acquisition and engine command dispatch remain outside this
/// ViewModel; this type only records, validates, persists, and presents the
/// resulting evidence through explicit callbacks.
/// </summary>
internal sealed class VisionExecutionEvidenceViewModel : ViewModelBase
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
        _activeRecorder = recorder ?? throw new ArgumentNullException(nameof(recorder));
        RaiseChanged(invalidateCommands: false);
    }

    internal void RecordEvent(SimulationEvent runtimeEvent, SimulationSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(runtimeEvent);
        ArgumentNullException.ThrowIfNull(snapshot);
        if (_activeRecorder is null)
        {
            return;
        }

        _activeRecorder.RecordEvent(runtimeEvent);
        TryComplete(snapshot);
    }

    internal bool TryComplete(SimulationSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
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
            _artifactState = package.IsForContext(
                context.ProjectId,
                context.ProjectJson,
                context.BuildIdentity,
                context.CameraId,
                context.RecipeId)
                ? ArtifactState.Restored
                : ArtifactState.StaleRejected;
        }

        _log(
            _artifactState == ArtifactState.Restored
                ? "Saved execution evidence restored"
                : _artifactState == ArtifactState.StaleRejected
                    ? "Saved execution evidence rejected because project, build, camera, or recipe context changed"
                    : "No saved execution evidence found");
        RaiseChanged(invalidateCommands: false);
    }

    internal void Persist()
    {
        if (_latestEvidence is null)
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
        if (_activeRecorder is not null)
        {
            RaiseChanged(invalidateCommands: false);
            return;
        }

        var context = _getContext();
        _artifactState = _latestEvidence switch
        {
            null when _artifactState == ArtifactState.StaleRejected =>
                ArtifactState.StaleRejected,
            null => ArtifactState.None,
            { } package when package.IsForContext(
                context.ProjectId,
                context.ProjectJson,
                context.BuildIdentity,
                context.CameraId,
                context.RecipeId) => _artifactState switch
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
        RelinkProjectPath(projectPath);
        RefreshContext();
        Persist();
    }

    internal void SetImportedEvidence(
        DeterministicVisionExecutionEvidencePackage? evidence)
    {
        _latestEvidence = evidence;
        _comparison = null;
        _artifactState = evidence is null
            ? ArtifactState.None
            : ArtifactState.Imported;
        RaiseChanged(invalidateCommands: false);
    }

    internal void Clear()
    {
        _activeRecorder = null;
        _latestEvidence = null;
        _comparison = null;
        _artifactState = ArtifactState.None;
        RaiseChanged(invalidateCommands: false);
    }

    internal void CancelCapture()
    {
        if (_activeRecorder is null)
        {
            return;
        }

        _activeRecorder = null;
        RefreshContext();
    }

    internal DeterministicVisionExecutionEvidencePackage? GetCurrentEvidence()
    {
        var evidence = _latestEvidence;
        if (evidence is null)
        {
            return null;
        }

        var context = _getContext();
        return evidence.IsForContext(
            context.ProjectId,
            context.ProjectJson,
            context.BuildIdentity,
            context.CameraId,
            context.RecipeId)
            ? evidence
            : null;
    }

    internal void RefreshLocalization() => RaiseChanged(invalidateCommands: false);

    private void PersistCore(VisionEvidenceContext context)
    {
        if (_latestEvidence is null
            || string.IsNullOrWhiteSpace(context.ProjectPath))
        {
            return;
        }

        if (!_latestEvidence.IsForContext(
                context.ProjectId,
                context.ProjectJson,
                context.BuildIdentity,
                context.CameraId,
                context.RecipeId))
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
        OnPropertyChanged(nameof(IsCapturing));
        OnPropertyChanged(nameof(EvidenceHashText));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(ComparisonText));
        _notifyParentPresentationChanged(invalidateCommands);
    }

    private static string ArtifactPath(string projectPath) =>
        $"{Path.GetFullPath(projectPath)}.vision-result.json";
}
