using System.Globalization;
using System.IO;
using System.Windows.Input;
using OpenVisionLab;
using OpenVisionLab.Machine.Core.Diagnostics;

namespace OpenVisionLab.MachineStudio.ViewModel;

/// <summary>
/// Presents the current redacted support-diagnostic snapshot and owns only the
/// explicit Save-file action. The caller supplies observations from existing
/// owners; no simulation, network, acquisition, or project-save action occurs.
/// </summary>
public sealed class SupportDiagnosticBundleViewModel : ViewModelBase, IDisposable
{
    private readonly SupportDiagnosticBundleBuilder _builder;
    private readonly Func<SupportDiagnosticBundleRequest> _requestFactory;
    private readonly Func<string?> _selectExportPath;
    private readonly Action<string> _setStatus;
    private readonly Action<string> _log;
    private readonly RelayCommand _exportCommand;
    private string _statusText;
    private bool _disposed;

    internal SupportDiagnosticBundleViewModel(
        SupportDiagnosticBundleBuilder builder,
        Func<SupportDiagnosticBundleRequest> requestFactory,
        Func<string?> selectExportPath,
        Action<string> setStatus,
        Action<string> log)
    {
        _builder = builder ?? throw new ArgumentNullException(nameof(builder));
        _requestFactory = requestFactory ?? throw new ArgumentNullException(nameof(requestFactory));
        _selectExportPath = selectExportPath ?? throw new ArgumentNullException(nameof(selectExportPath));
        _setStatus = setStatus ?? throw new ArgumentNullException(nameof(setStatus));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _statusText = L(
            "PreviewReady",
            "지원 진단 항목을 확인한 뒤 내보내기를 선택하세요.",
            "Review the support diagnostics before choosing export.");
        _exportCommand = new RelayCommand(
            Export,
            _ => CanExport,
            useCommandManagerRequery: false);
    }

    public bool CanExport => !_disposed;

    public string StatusText => _disposed
        ? L("Unavailable", "지원 진단 내보내기를 사용할 수 없습니다.", "Support diagnostic export is unavailable.")
        : _statusText;

    public string PreviewText
    {
        get
        {
            if (_disposed)
            {
                return StatusText;
            }

            try
            {
                return FormatPreview(_builder.Build(_requestFactory()));
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
            {
                return string.Format(
                    CultureInfo.CurrentCulture,
                    "{0}\n{1}",
                    L("PreviewFailed", "지원 진단 미리보기 실패", "Support diagnostic preview failed."),
                    exception.Message);
            }
        }
    }

    public ICommand ExportCommand => _exportCommand;

    public void Refresh()
    {
        if (_disposed)
        {
            return;
        }

        OnPropertyChanged(nameof(PreviewText));
        OnPropertyChanged(nameof(StatusText));
        _exportCommand.RaiseCanExecuteChanged();
    }

    internal SupportDiagnosticBundle CreateCurrentBundle() =>
        _builder.Build(_requestFactory());

    internal bool TryExport(string path)
    {
        if (!CanExport || string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            _builder.WriteJson(path, _requestFactory());
            _statusText = L(
                "Exported",
                "민감정보를 제외한 지원 진단 사본을 내보냈습니다.",
                "Exported the redacted support diagnostic copy.");
            _setStatus(StatusText);
            _log($"Support diagnostic bundle exported · {Path.GetFileName(path)}");
            Refresh();
            return true;
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or ArgumentException
                or InvalidOperationException)
        {
            _statusText = L(
                "ExportFailed",
                "지원 진단 사본을 내보내지 못했습니다.",
                "The support diagnostic copy could not be exported.");
            _setStatus(StatusText);
            _log($"Support diagnostic bundle export failed · {exception.Message}");
            Refresh();
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _exportCommand.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(CanExport));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(PreviewText));
    }

    private void Export(object? parameter)
    {
        if (!CanExport)
        {
            return;
        }

        var path = parameter is string candidate && !string.IsNullOrWhiteSpace(candidate)
            ? candidate
            : _selectExportPath();
        if (path is not null)
        {
            TryExport(path);
        }
    }

    private static string FormatPreview(SupportDiagnosticBundle bundle)
    {
        var lines = new List<string>
        {
            $"{L("Version", "버전", "Version")}: {bundle.Identity.ApplicationVersion}",
            $"{L("Source", "소스", "Source")}: {bundle.Identity.SourceCommit ?? "—"} ({bundle.Identity.SourceState ?? "—"})",
            $"{L("Project", "프로젝트", "Project")}: {bundle.Identity.ProjectId ?? "—"}",
            $"{L("Run", "실행 ID", "Run ID")}: {bundle.Run.RunId ?? "—"}",
            string.Empty,
            $"{L("Errors", "오류", "Errors")}:"
        };
        lines.AddRange(bundle.Errors.Count == 0
            ? ["  —"]
            : bundle.Errors.Select(error => $"  - {error.Code}: {error.Message}"));
        lines.Add($"{L("Queue", "큐/시간 제한", "Queue/timeout")}:" );
        lines.AddRange(bundle.Queue.Count == 0
            ? ["  —"]
            : bundle.Queue.Select(item =>
                $"  - {item.Name}: {item.State}" +
                (item.TimedOut
                    ? $" ({item.TimeoutMilliseconds?.ToString(CultureInfo.InvariantCulture) ?? "?"} ms timeout)"
                    : string.Empty)));
        lines.Add($"{L("Artifacts", "상대 경로 산출물", "Relative artifacts")}:" );
        lines.AddRange(bundle.Artifacts.Count == 0
            ? ["  —"]
            : bundle.Artifacts.Select(artifact => $"  - {artifact.RelativePath} [{artifact.Kind}]"));
        lines.Add($"{L("Excluded", "제외 항목", "Excluded")}:" );
        lines.AddRange(bundle.Excluded.Count == 0
            ? ["  —"]
            : bundle.Excluded.Select(item => $"  - {item.Category}: {item.Reason}"));
        lines.Add($"{L("Replay", "재생", "Replay")}: {(bundle.Replay.IsReplayable ? "available" : "unavailable")}");
        lines.Add($"  {bundle.Replay.Limitation}");
        return string.Join(Environment.NewLine, lines);
    }

    private static string L(string key, string korean, string english) =>
        OpenVisionLanguageService.T($"SupportDiagnostics.{key}", korean, english);
}
