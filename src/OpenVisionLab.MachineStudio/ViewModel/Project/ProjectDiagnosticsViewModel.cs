using System.IO;
using System.Globalization;
using System.Text.Json;
using System.Windows.Input;
using OpenVisionLab;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Persistence.Projects;

namespace OpenVisionLab.MachineStudio.ViewModel;

public sealed record ProjectDiagnosticItemPresentation(
    ProjectDocumentDiagnosticCode Code,
    ProjectDocumentDiagnosticSeverity Severity,
    string SeverityText,
    string MessageText,
    string DetailText,
    bool IsRepairable);

/// <summary>
/// Owns the project health report and the explicit Preview/Apply recovery
/// interaction. File inspection and replacement remain in Persistence and
/// project application remains in ProjectLifecycleCoordinator.
/// </summary>
public sealed class ProjectDiagnosticsViewModel : ViewModelBase, IDisposable
{
    private readonly ProjectDocumentDiagnostics _diagnostics;
    private readonly Func<MachineProjectDocument> _getProject;
    private readonly Func<string?> _getProjectPath;
    private readonly Func<bool> _hasUnsavedChanges;
    private readonly Func<bool> _isBusy;
    private readonly Func<ProjectDocumentRecoveryPreview, Task<bool>> _applyRecovery;
    private readonly Action<string> _setStatus;
    private readonly List<ProjectDiagnosticItemPresentation> _items = new();
    private ProjectDocumentDiagnosticReport? _report;
    private ProjectDocumentRecoveryPreview? _recoveryPreview;
    private ProjectDocumentLoadResult? _lastLoadResult;
    private ICommand? _refreshCommand;
    private ICommand? _previewRecoveryCommand;
    private ICommand? _applyRecoveryCommand;
    private ICommand? _cancelRecoveryPreviewCommand;
    private bool _isApplying;
    private bool _disposed;

    internal ProjectDiagnosticsViewModel(
        ProjectDocumentDiagnostics diagnostics,
        Func<MachineProjectDocument> getProject,
        Func<string?> getProjectPath,
        Func<bool> hasUnsavedChanges,
        Func<bool> isBusy,
        Func<ProjectDocumentRecoveryPreview, Task<bool>> applyRecovery,
        Action<string> setStatus)
    {
        _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
        _getProject = getProject ?? throw new ArgumentNullException(nameof(getProject));
        _getProjectPath = getProjectPath ?? throw new ArgumentNullException(nameof(getProjectPath));
        _hasUnsavedChanges = hasUnsavedChanges ?? throw new ArgumentNullException(nameof(hasUnsavedChanges));
        _isBusy = isBusy ?? throw new ArgumentNullException(nameof(isBusy));
        _applyRecovery = applyRecovery ?? throw new ArgumentNullException(nameof(applyRecovery));
        _setStatus = setStatus ?? throw new ArgumentNullException(nameof(setStatus));
        Refresh();
    }

    public ProjectDocumentDiagnosticReport? Report => _report;

    public IReadOnlyList<ProjectDiagnosticItemPresentation> Items => _items;

    public bool HasReport => _report is not null;

    public bool HasItems => _items.Count > 0;

    public bool HasRecoveryCandidate => _report?.CanPreviewRecovery == true;

    public bool HasRecoveryPreview => _recoveryPreview is not null;

    public bool CanPreviewRecovery => !_disposed
        && !_isApplying
        && !_isBusy()
        && _report?.CanPreviewRecovery == true
        && _recoveryPreview is null;

    public bool CanApplyRecovery => !_disposed
        && !_isApplying
        && !_isBusy()
        && !_hasUnsavedChanges()
        && _recoveryPreview is not null;

    public string PathText => _report?.ProjectPath
        ?? OpenVisionLanguageService.T(
            "ProjectDiagnostics.NoPath",
            "저장된 프로젝트 경로 없음",
            "No saved project path");

    public string CurrentStateText => _hasUnsavedChanges()
        ? OpenVisionLanguageService.T(
            "ProjectDiagnostics.CurrentUnsaved",
            "현재 문서에 저장하지 않은 변경이 있습니다. 복구 적용 전 저장하거나 변경을 취소하세요.",
            "The current document has unsaved changes. Save or discard them before applying recovery.")
        : string.IsNullOrWhiteSpace(_getProjectPath())
            ? OpenVisionLanguageService.T(
                "ProjectDiagnostics.CurrentNoPath",
                "아직 저장되지 않은 프로젝트입니다.",
                "This project has not been saved yet.")
            : OpenVisionLanguageService.T(
                "ProjectDiagnostics.CurrentClean",
                "현재 문서는 저장 기준과 일치합니다.",
                "The current document matches its saved baseline.");

    public bool HasLoadSourceNotice => _lastLoadResult?.IsRecoveredFromBackup == true;

    public string LoadSourceNoticeText => HasLoadSourceNotice
        ? OpenVisionLanguageService.T(
            "ProjectDiagnostics.LoadedFromBackupNotice",
            "백업 파일(.bak)에서 문서를 열었습니다. 기본 파일은 변경되지 않았습니다. 다른 이름으로 저장(Ctrl+Shift+S)하여 복구 사본을 만드세요.",
            "The document was opened from its .bak backup. The primary file was not changed. Use Save As (Ctrl+Shift+S) to create a recovery copy.")
        : string.Empty;

    public string SummaryText => _report is null
        ? OpenVisionLanguageService.T(
            "ProjectDiagnostics.RunHint",
            "진단을 실행하세요.",
            "Run diagnostics.")
        : _report.HasErrors
            ? string.Format(
                CultureInfo.CurrentCulture,
                OpenVisionLanguageService.T(
                    "ProjectDiagnostics.ErrorsSummary",
                    "진단에서 오류 {0}개를 찾았습니다.",
                    "Diagnostics found {0} error(s)."),
                _report.Items.Count(item => item.Severity == ProjectDocumentDiagnosticSeverity.Error))
            : _report.HasWarnings
                ? string.Format(
                    CultureInfo.CurrentCulture,
                    OpenVisionLanguageService.T(
                        "ProjectDiagnostics.WarningsSummary",
                        "진단에서 확인이 필요한 항목 {0}개를 찾았습니다.",
                        "Diagnostics found {0} item(s) that need attention."),
                    _report.Items.Count(item => item.Severity == ProjectDocumentDiagnosticSeverity.Warning))
                : _report.Items.Any(item =>
                    item.Code == ProjectDocumentDiagnosticCode.NoSavedPath)
                    ? OpenVisionLanguageService.T(
                        "ProjectDiagnostics.NoPathSummary",
                        "저장된 프로젝트가 없어 파일 진단을 실행할 수 없습니다.",
                        "No saved project path is available for file diagnostics.")
                : OpenVisionLanguageService.T(
                    "ProjectDiagnostics.HealthySummary",
                    "프로젝트 상태가 정상입니다.",
                    "Project health is good.");

    public string RecoveryPreviewText => _recoveryPreview is null
        ? string.Empty
        : string.Format(
            CultureInfo.CurrentCulture,
            OpenVisionLanguageService.T(
                "ProjectDiagnostics.RecoveryPreview",
                "백업 '{0}'을(를) '{1}'에 적용합니다. 프로젝트 '{2}', 스키마 {3}, 원본 해시 {4}",
                "Apply backup '{0}' to '{1}'. Project '{2}', schema {3}, source hash {4}"),
            _recoveryPreview.SourcePath,
            _recoveryPreview.ProjectPath,
            _recoveryPreview.ProjectName,
            _recoveryPreview.ProjectSchema,
            _recoveryPreview.SourceHash);

    public ICommand RefreshCommand => _refreshCommand ??= new RelayCommand(
        _ => Refresh(),
        _ => CanRefresh,
        useCommandManagerRequery: false);

    public ICommand PreviewRecoveryCommand => _previewRecoveryCommand ??= new RelayCommand(
        _ => PreviewRecovery(),
        _ => CanPreviewRecovery,
        useCommandManagerRequery: false);

    public ICommand ApplyRecoveryCommand => _applyRecoveryCommand ??= new AsyncRelayCommand(
        _ => ApplyRecoveryAsync(),
        _ => CanApplyRecovery,
        HandleApplyException,
        useCommandManagerRequery: false);

    public ICommand CancelRecoveryPreviewCommand => _cancelRecoveryPreviewCommand ??= new RelayCommand(
        _ => CancelRecoveryPreview(),
        _ => !_disposed && !_isApplying && _recoveryPreview is not null,
        useCommandManagerRequery: false);

    private bool CanRefresh => !_disposed && !_isApplying && !_isBusy();

    public void Refresh()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            _report = _diagnostics.Inspect(
                _getProjectPath(),
                _getProject(),
                _hasUnsavedChanges());
        }
        catch (Exception exception) when (exception is IOException
                                               or UnauthorizedAccessException
                                               or ArgumentException
                                               or InvalidOperationException)
        {
            _report = new(
                _getProjectPath(),
                [new ProjectDocumentDiagnosticItem(
                    ProjectDocumentDiagnosticCode.InspectionFailed,
                    ProjectDocumentDiagnosticSeverity.Error,
                    exception.GetType().Name)],
                null);
            _setStatus(OpenVisionLanguageService.T(
                "ProjectDiagnostics.InspectionFailed",
                "프로젝트 진단에 실패했습니다.",
                "Project diagnostics failed."));
        }

        _recoveryPreview = null;
        RebuildPresentation();
        NotifyPresentationChanged();
        InvalidateCommands();
    }

    internal void NotifyCurrentProjectChanged()
    {
        if (_disposed)
        {
            return;
        }

        OnPropertyChanged(nameof(CurrentStateText));
        OnPropertyChanged(nameof(SummaryText));
        OnPropertyChanged(nameof(CanPreviewRecovery));
        OnPropertyChanged(nameof(CanApplyRecovery));
        InvalidateCommands();
    }

    internal void NotifyProjectLoaded(ProjectDocumentLoadResult? loadResult)
    {
        if (_disposed)
        {
            return;
        }

        _lastLoadResult = loadResult;
        OnPropertyChanged(nameof(HasLoadSourceNotice));
        OnPropertyChanged(nameof(LoadSourceNoticeText));
    }

    internal void RefreshLocalization()
    {
        if (_disposed)
        {
            return;
        }

        RebuildPresentation();
        NotifyPresentationChanged();
    }

    internal void InvalidateCommands()
    {
        OnPropertyChanged(nameof(CanPreviewRecovery));
        OnPropertyChanged(nameof(CanApplyRecovery));
        (_refreshCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (_previewRecoveryCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (_applyRecoveryCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (_cancelRecoveryPreviewCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    private void PreviewRecovery()
    {
        if (!CanPreviewRecovery || _report?.ProjectPath is not { } projectPath)
        {
            return;
        }

        try
        {
            _recoveryPreview = _diagnostics.PreviewRecovery(projectPath);
            if (_recoveryPreview is null)
            {
                _setStatus(OpenVisionLanguageService.T(
                    "ProjectDiagnostics.RecoveryUnavailable",
                    "복구 가능한 백업을 찾지 못했습니다. 진단을 다시 실행하세요.",
                    "No recoverable backup was found. Run diagnostics again."));
            }
        }
        catch (Exception exception) when (exception is IOException
                                               or UnauthorizedAccessException
                                               or JsonException
                                               or ProjectDocumentLoadException)
        {
            _recoveryPreview = null;
            _setStatus(string.Format(
                CultureInfo.CurrentCulture,
                OpenVisionLanguageService.T(
                    "ProjectDiagnostics.PreviewFailed",
                    "백업 Preview에 실패했습니다: {0}",
                    "Backup preview failed: {0}"),
                exception.GetType().Name));
        }

        OnPropertyChanged(nameof(HasRecoveryPreview));
        OnPropertyChanged(nameof(RecoveryPreviewText));
        InvalidateCommands();
    }

    private async Task ApplyRecoveryAsync()
    {
        if (!CanApplyRecovery || _recoveryPreview is not { } preview)
        {
            return;
        }

        _isApplying = true;
        InvalidateCommands();
        try
        {
            if (await _applyRecovery(preview))
            {
                _setStatus(OpenVisionLanguageService.T(
                    "ProjectDiagnostics.RecoveryApplied",
                    "백업 복구를 적용하고 프로젝트를 다시 열었습니다.",
                    "The backup was applied and the project was reopened."));
                Refresh();
            }
            else
            {
                _setStatus(OpenVisionLanguageService.T(
                    "ProjectDiagnostics.RecoveryRejected",
                    "복구 적용이 거부되었습니다. 프로젝트 상태와 Preview를 다시 확인하세요.",
                    "Recovery was rejected. Check the project state and preview again."));
            }
        }
        finally
        {
            _isApplying = false;
            InvalidateCommands();
        }
    }

    private void HandleApplyException(Exception exception)
    {
        _setStatus(string.Format(
            CultureInfo.CurrentCulture,
            OpenVisionLanguageService.T(
                "ProjectDiagnostics.ApplyFailed",
                "복구 적용에 실패했습니다: {0}",
                "Recovery apply failed: {0}"),
            exception.GetType().Name));
    }

    private void CancelRecoveryPreview()
    {
        if (_disposed)
        {
            return;
        }

        _recoveryPreview = null;
        OnPropertyChanged(nameof(HasRecoveryPreview));
        OnPropertyChanged(nameof(RecoveryPreviewText));
        InvalidateCommands();
    }

    private void RebuildPresentation()
    {
        _items.Clear();
        if (_report is null)
        {
            return;
        }

        _items.AddRange(_report.Items.Select(CreatePresentation));
    }

    private static ProjectDiagnosticItemPresentation CreatePresentation(
        ProjectDocumentDiagnosticItem item)
    {
        var severityText = item.Severity switch
        {
            ProjectDocumentDiagnosticSeverity.Warning => OpenVisionLanguageService.T(
                "ProjectDiagnostics.SeverityWarning",
                "주의",
                "Warning"),
            ProjectDocumentDiagnosticSeverity.Error => OpenVisionLanguageService.T(
                "ProjectDiagnostics.SeverityError",
                "오류",
                "Error"),
            _ => OpenVisionLanguageService.T(
                "ProjectDiagnostics.SeverityInformation",
                "정보",
                "Info")
        };
        var messageText = item.Code switch
        {
            ProjectDocumentDiagnosticCode.NoSavedPath => OpenVisionLanguageService.T(
                "ProjectDiagnostics.NoSavedPath",
                "저장된 프로젝트 경로가 없습니다.",
                "No saved project path is available."),
            ProjectDocumentDiagnosticCode.CurrentDocumentHasUnsavedChanges => OpenVisionLanguageService.T(
                "ProjectDiagnostics.UnsavedChanges",
                "현재 문서에 저장하지 않은 변경이 있습니다.",
                "The current document has unsaved changes."),
            ProjectDocumentDiagnosticCode.PrimaryFileHealthy => string.Format(
                CultureInfo.CurrentCulture,
                OpenVisionLanguageService.T(
                    "ProjectDiagnostics.PrimaryHealthy",
                    "기본 프로젝트 파일을 읽을 수 있습니다. 스키마 {0}",
                    "The primary project file is readable. Schema {0}"),
                item.Detail),
            ProjectDocumentDiagnosticCode.PrimaryFileDiffersFromCurrent => OpenVisionLanguageService.T(
                "ProjectDiagnostics.PrimaryDiffers",
                "디스크의 프로젝트 내용이 현재 문서와 다릅니다.",
                "The disk project differs from the current document."),
            ProjectDocumentDiagnosticCode.PrimaryFileMissing => OpenVisionLanguageService.T(
                "ProjectDiagnostics.PrimaryMissing",
                "기본 프로젝트 파일을 찾을 수 없습니다.",
                "The primary project file is missing."),
            ProjectDocumentDiagnosticCode.PrimaryFileUnreadable => OpenVisionLanguageService.T(
                "ProjectDiagnostics.PrimaryUnreadable",
                "기본 프로젝트 파일을 읽을 수 없습니다.",
                "The primary project file cannot be read."),
            ProjectDocumentDiagnosticCode.PrimaryFileInvalid => OpenVisionLanguageService.T(
                "ProjectDiagnostics.PrimaryInvalid",
                "기본 프로젝트 파일 형식이 올바르지 않습니다.",
                "The primary project file is invalid."),
            ProjectDocumentDiagnosticCode.PrimaryFileUnsupportedSchema => string.Format(
                CultureInfo.CurrentCulture,
                OpenVisionLanguageService.T(
                    "ProjectDiagnostics.UnsupportedSchema",
                    "기본 파일 스키마 {0}은(는) 지원되지 않습니다.",
                    "The primary file schema {0} is unsupported."),
                item.Detail),
            ProjectDocumentDiagnosticCode.RecoveryAvailable => OpenVisionLanguageService.T(
                "ProjectDiagnostics.RecoveryAvailable",
                "검증된 .bak 백업으로 복구할 수 있습니다. 먼저 Preview를 확인하세요.",
                "A validated .bak backup is available. Review the preview first."),
            ProjectDocumentDiagnosticCode.RecoveryUnavailable => OpenVisionLanguageService.T(
                "ProjectDiagnostics.RecoveryUnavailableItem",
                "복구 가능한 백업이 없습니다.",
                "No recoverable backup is available."),
            ProjectDocumentDiagnosticCode.BackupFileInvalid => OpenVisionLanguageService.T(
                "ProjectDiagnostics.BackupInvalid",
                "백업 파일 형식이 올바르지 않습니다.",
                "The backup file is invalid."),
            ProjectDocumentDiagnosticCode.BackupFileUnreadable => OpenVisionLanguageService.T(
                "ProjectDiagnostics.BackupUnreadable",
                "백업 파일을 읽을 수 없습니다.",
                "The backup file cannot be read."),
            _ => OpenVisionLanguageService.T(
                "ProjectDiagnostics.InspectionFailedItem",
                "프로젝트 진단에 실패했습니다.",
                "Project diagnostics failed.")
        };

        return new(
            item.Code,
            item.Severity,
            severityText,
            messageText,
            item.RelatedPath ?? item.Detail,
            item.IsRepairable);
    }

    private void NotifyPresentationChanged()
    {
        OnPropertyChanged(nameof(Report));
        OnPropertyChanged(nameof(Items));
        OnPropertyChanged(nameof(HasReport));
        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(HasRecoveryCandidate));
        OnPropertyChanged(nameof(HasRecoveryPreview));
        OnPropertyChanged(nameof(PathText));
        OnPropertyChanged(nameof(CurrentStateText));
        OnPropertyChanged(nameof(SummaryText));
        OnPropertyChanged(nameof(LoadSourceNoticeText));
        OnPropertyChanged(nameof(RecoveryPreviewText));
        OnPropertyChanged(nameof(CanPreviewRecovery));
        OnPropertyChanged(nameof(CanApplyRecovery));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _recoveryPreview = null;
        _items.Clear();
        InvalidateCommands();
    }
}
