using System.Globalization;
using System.IO;
using OpenVisionLab;
using OpenVisionLab.Machine.Simulation.Scenarios;

namespace OpenVisionLab.MachineStudio.ViewModel;

internal readonly record struct UnifiedCommissioningEvidenceContext(
    string ProjectId,
    string ProjectJson,
    TimeSpan FixedStep,
    DeterministicConditionScenarioProfile Profile,
    string BuildIdentity,
    string ProjectPath);

/// <summary>
/// Owns the lifecycle of one portable unified commissioning evidence artifact.
/// Package inputs, project mutation, runtime access, and WPF dialogs remain
/// explicit callbacks owned by the shell and the existing child ViewModels.
/// </summary>
internal sealed class UnifiedCommissioningEvidenceViewModel : ViewModelBase
{
    private enum ArtifactState
    {
        None,
        Exported,
        Imported,
        Failed
    }

    private readonly Func<bool> _canExport;
    private readonly Func<bool> _canImport;
    private readonly Func<DeterministicSimulationEvidenceExchangePackage?> _createSimulationEvidence;
    private readonly Func<DeterministicSimulationCommandTracePackage?> _createCommandTrace;
    private readonly Func<DeterministicVisionExecutionEvidencePackage?> _getCurrentVisionEvidence;
    private readonly Func<UnifiedCommissioningEvidenceContext?> _getContext;
    private readonly Action<
        DeterministicSimulationBatchResultPackage,
        DeterministicSimulationRunResultPackage?,
        DeterministicVisionExecutionEvidencePackage?> _applyImportedArtifacts;
    private readonly Action<string> _setStatus;
    private readonly Action<string> _log;
    private readonly Action _notifyPresentationChanged;
    private DeterministicUnifiedCommissioningEvidencePackage? _latestEvidence;
    private ArtifactState _artifactState;

    internal UnifiedCommissioningEvidenceViewModel(
        Func<bool> canExport,
        Func<bool> canImport,
        Func<DeterministicSimulationEvidenceExchangePackage?> createSimulationEvidence,
        Func<DeterministicSimulationCommandTracePackage?> createCommandTrace,
        Func<DeterministicVisionExecutionEvidencePackage?> getCurrentVisionEvidence,
        Func<UnifiedCommissioningEvidenceContext?> getContext,
        Action<
            DeterministicSimulationBatchResultPackage,
            DeterministicSimulationRunResultPackage?,
            DeterministicVisionExecutionEvidencePackage?> applyImportedArtifacts,
        Action<string> setStatus,
        Action<string> log,
        Action notifyPresentationChanged)
    {
        _canExport = canExport ?? throw new ArgumentNullException(nameof(canExport));
        _canImport = canImport ?? throw new ArgumentNullException(nameof(canImport));
        _createSimulationEvidence = createSimulationEvidence
            ?? throw new ArgumentNullException(nameof(createSimulationEvidence));
        _createCommandTrace = createCommandTrace
            ?? throw new ArgumentNullException(nameof(createCommandTrace));
        _getCurrentVisionEvidence = getCurrentVisionEvidence
            ?? throw new ArgumentNullException(nameof(getCurrentVisionEvidence));
        _getContext = getContext ?? throw new ArgumentNullException(nameof(getContext));
        _applyImportedArtifacts = applyImportedArtifacts
            ?? throw new ArgumentNullException(nameof(applyImportedArtifacts));
        _setStatus = setStatus ?? throw new ArgumentNullException(nameof(setStatus));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _notifyPresentationChanged = notifyPresentationChanged
            ?? throw new ArgumentNullException(nameof(notifyPresentationChanged));
    }

    internal DeterministicUnifiedCommissioningEvidencePackage? LatestEvidence =>
        _latestEvidence;

    internal bool CanExport => _canExport();

    internal bool CanImport => _canImport();

    internal string StatusText
    {
        get
        {
            if (_latestEvidence is { } package && IsForCurrentContext(package))
            {
                var visionState = package.ContainsNonReplayableVisionEvidence
                    ? OpenVisionLanguageService.T("Simulation.UnifiedEvidenceVisionIncluded")
                    : OpenVisionLanguageService.T("Simulation.UnifiedEvidenceVisionOmitted");
                var statusKey = _artifactState == ArtifactState.Imported
                    ? "Simulation.UnifiedEvidenceImported"
                    : "Simulation.UnifiedEvidenceExported";
                return string.Format(
                    CultureInfo.CurrentCulture,
                    OpenVisionLanguageService.T(statusKey),
                    ShortHash(package.EvidenceHash),
                    visionState);
            }

            return CanExport
                ? string.Format(
                    CultureInfo.CurrentCulture,
                    OpenVisionLanguageService.T("Simulation.UnifiedEvidenceReady"),
                    CurrentVisionStateText())
                : OpenVisionLanguageService.T("Simulation.UnifiedEvidenceNotReady");
        }
    }

    internal bool TryExport(string path)
    {
        if (!CanExport || string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            var simulationEvidence = _createSimulationEvidence()
                ?? throw new InvalidOperationException("Simulation evidence was unavailable.");
            var commandTrace = _createCommandTrace()
                ?? throw new InvalidOperationException("Command trace was unavailable.");
            var package = DeterministicUnifiedCommissioningEvidencePackage.Create(
                simulationEvidence,
                commandTrace,
                _getCurrentVisionEvidence());
            DeterministicUnifiedCommissioningEvidencePackage.SaveToJson(package, path);
            _latestEvidence = package;
            _artifactState = ArtifactState.Exported;
            var visionState = package.ContainsNonReplayableVisionEvidence
                ? OpenVisionLanguageService.T("Simulation.UnifiedEvidenceVisionIncluded")
                : OpenVisionLanguageService.T("Simulation.UnifiedEvidenceVisionOmitted");
            _setStatus(string.Format(
                CultureInfo.CurrentCulture,
                OpenVisionLanguageService.T("Simulation.UnifiedEvidenceExported"),
                ShortHash(package.EvidenceHash),
                visionState));
            _log(
                $"Unified commissioning evidence exported · {ShortHash(package.EvidenceHash)} · " +
                $"trace {ShortHash(commandTrace.TraceHash)} · Vision {visionState}");
            RaiseChanged();
            return true;
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or IOException
                or UnauthorizedAccessException
                or InvalidOperationException)
        {
            _artifactState = ArtifactState.Failed;
            _setStatus(OpenVisionLanguageService.T(
                "Simulation.UnifiedEvidenceExportFailed",
                "통합 커미셔닝 증거를 내보내지 못했습니다.",
                "Unified commissioning evidence could not be exported."));
            _log($"Unified commissioning evidence export failed · {exception.Message}");
            RaiseChanged();
            return false;
        }
    }

    internal bool TryImport(string path)
    {
        if (!CanImport || string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            var package = DeterministicUnifiedCommissioningEvidencePackage.LoadFromJson(path);
            var context = _getContext();
            if (package is null || context is null)
            {
                RejectImport("file could not be loaded");
                return false;
            }

            if (!package.IsForContext(
                    context.Value.ProjectId,
                    context.Value.ProjectJson,
                    context.Value.FixedStep,
                    context.Value.Profile,
                    context.Value.BuildIdentity))
            {
                RejectImport("context mismatch");
                return false;
            }

            if (!package.TryGetArtifacts(
                    context.Value.ProjectPath,
                    out var batchResult,
                    out var acceptedBaseline,
                    out var importedCommandTrace,
                    out var visionEvidence))
            {
                RejectImport("package validation failed");
                return false;
            }

            _applyImportedArtifacts(batchResult, acceptedBaseline, visionEvidence);
            _latestEvidence = package;
            _artifactState = ArtifactState.Imported;
            var visionState = package.ContainsNonReplayableVisionEvidence
                ? OpenVisionLanguageService.T("Simulation.UnifiedEvidenceVisionIncluded")
                : OpenVisionLanguageService.T("Simulation.UnifiedEvidenceVisionOmitted");
            _setStatus(string.Format(
                CultureInfo.CurrentCulture,
                OpenVisionLanguageService.T("Simulation.UnifiedEvidenceImported"),
                ShortHash(package.EvidenceHash),
                visionState));
            _log(
                $"Unified commissioning evidence imported without execution · " +
                $"{ShortHash(package.EvidenceHash)} · trace {ShortHash(importedCommandTrace.TraceHash)} · " +
                $"Vision {visionState}");
            RaiseChanged();
            return true;
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or InvalidOperationException
                or System.Text.Json.JsonException)
        {
            RejectImport(exception.Message);
            return false;
        }
    }

    internal void Reset()
    {
        _latestEvidence = null;
        _artifactState = ArtifactState.None;
    }

    private bool IsForCurrentContext(DeterministicUnifiedCommissioningEvidencePackage package)
    {
        try
        {
            var context = _getContext();
            return context is { } current
                && package.IsForContext(
                    current.ProjectId,
                    current.ProjectJson,
                    current.FixedStep,
                    current.Profile,
                    current.BuildIdentity);
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private string CurrentVisionStateText() =>
        _getCurrentVisionEvidence() is null
            ? OpenVisionLanguageService.T("Simulation.UnifiedEvidenceVisionOmitted")
            : OpenVisionLanguageService.T("Simulation.UnifiedEvidenceVisionIncluded");

    private void RejectImport(string detail)
    {
        _setStatus(OpenVisionLanguageService.T(
            "Simulation.UnifiedEvidenceImportRejected",
            "현재 프로젝트 또는 시나리오와 일치하지 않아 통합 증거를 가져오지 않았습니다.",
            "Unified commissioning evidence was not imported because it is invalid for the current context."));
        _log($"Unified commissioning evidence import rejected · {detail}");
        RaiseChanged();
    }

    private void RaiseChanged()
    {
        OnPropertyChanged(nameof(LatestEvidence));
        OnPropertyChanged(nameof(CanExport));
        OnPropertyChanged(nameof(CanImport));
        OnPropertyChanged(nameof(StatusText));
        _notifyPresentationChanged();
    }

    private static string ShortHash(string hash) =>
        hash.Length <= 12 ? hash : hash[..12];
}
