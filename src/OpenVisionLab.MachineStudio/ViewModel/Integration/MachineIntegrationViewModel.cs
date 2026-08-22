using System.IO;
using System.Text.Json;
using Microsoft.Win32;
using OpenVisionLab;
using OpenVisionLab.Integration.Contracts;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Infrastructure.Integration;

namespace OpenVisionLab.MachineStudio.ViewModel.Integration;

public sealed record MachineIntegrationProjectContext(
    MachineProjectDocument Project,
    string? ProjectPath,
    string SequenceId,
    string StepId,
    string CameraId,
    bool HasUnsavedChanges);

public sealed class MachineIntegrationViewModel : ViewModelBase
{
    private readonly Func<MachineIntegrationProjectContext> contextProvider;
    private readonly string settingsPath;
    private readonly Func<IntegrationApplicationIdentity> producerIdentityProvider;
    private IntegrationExchangeSettings settings;
    private string exchangeRoot = string.Empty;
    private string inspectionSourcePath = string.Empty;
    private string projectSummary = string.Empty;
    private Func<string> transactionSummaryProvider = () => L(
        "Integration.Transaction.None",
        "이 프로젝트에서 내보낸 Handoff가 없습니다.",
        "No handoff has been exported for this project.");
    private Func<string> statusTextProvider = () => L(
        "Integration.Status.SetupRequired",
        "설정을 한 번 저장한 다음 명시적으로 내보내세요.",
        "Save the setup once, then export explicitly.");
    private string transactionSummary = string.Empty;
    private string statusText = string.Empty;
    private Guid? currentTransactionId;

    public MachineIntegrationViewModel(
        Func<MachineIntegrationProjectContext> contextProvider,
        string? settingsPath = null,
        Func<IntegrationApplicationIdentity>? producerIdentityProvider = null)
    {
        this.contextProvider = contextProvider ?? throw new ArgumentNullException(nameof(contextProvider));
        this.settingsPath = settingsPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OpenVisionLab",
            "MachineStudio",
            "integration-exchange.json");
        this.producerIdentityProvider = producerIdentityProvider ?? CreateProducerIdentity;
        settings = IntegrationExchangeSettings.Load(this.settingsPath);
        BrowseExchangeRootCommand = new RelayCommand(_ => BrowseExchangeRoot());
        BrowseInspectionSourceCommand = new RelayCommand(_ => BrowseInspectionSource());
        SaveSetupCommand = new RelayCommand(_ => SaveSetup());
        ResetSetupCommand = new RelayCommand(_ => ResetSetup());
        ExportHandoffCommand = new RelayCommand(_ => ExportHandoff());
        RefreshResultCommand = new RelayCommand(_ => RefreshResult());
        SyncProjectContext();
    }

    public RelayCommand BrowseExchangeRootCommand { get; }
    public RelayCommand BrowseInspectionSourceCommand { get; }
    public RelayCommand SaveSetupCommand { get; }
    public RelayCommand ResetSetupCommand { get; }
    public RelayCommand ExportHandoffCommand { get; }
    public RelayCommand RefreshResultCommand { get; }

    public string ExchangeRoot
    {
        get => exchangeRoot;
        set => SetProperty(ref exchangeRoot, value ?? string.Empty);
    }

    public string InspectionSourcePath
    {
        get => inspectionSourcePath;
        set => SetProperty(ref inspectionSourcePath, value ?? string.Empty);
    }

    public string ProjectSummary
    {
        get => projectSummary;
        private set => SetProperty(ref projectSummary, value);
    }

    public string TransactionSummary
    {
        get => transactionSummary;
        private set => SetProperty(ref transactionSummary, value);
    }

    public string StatusText
    {
        get => statusText;
        private set => SetProperty(ref statusText, value);
    }

    public void SyncProjectContext()
    {
        var context = contextProvider();
        ProjectSummary = string.IsNullOrWhiteSpace(context.ProjectPath)
            ? $"{context.Project.Name} | {L("Integration.Project.SaveBeforeExport", "내보내기 전에 프로젝트를 저장하세요.", "Save the project before export.")}"
            : $"{context.Project.Name} | {context.SequenceId} / {context.StepId} | {context.CameraId}";
        ExchangeRoot = settings.ExchangeRoot;
        var key = ProjectKey(context.ProjectPath);
        if (key is not null && settings.Projects.TryGetValue(key, out var project))
        {
            InspectionSourcePath = project.InspectionSourcePath;
            currentTransactionId = project.TransactionId;
        }
        else
        {
            InspectionSourcePath = string.Empty;
            currentTransactionId = null;
        }

        if (currentTransactionId is { } transactionId)
        {
            SetTransaction(
                "Integration.Transaction.Last",
                "마지막 거래: {0:D}",
                "Last transaction: {0:D}",
                transactionId);
        }
        else
        {
            SetTransaction(
                "Integration.Transaction.None",
                "이 프로젝트에서 내보낸 Handoff가 없습니다.",
                "No handoff has been exported for this project.");
        }
        SetStatus(
            "Integration.Status.Restored",
            "설정을 복원했습니다. 파일을 내보내거나 읽지 않았습니다.",
            "Setup restored. No file was exported or read.");
    }

    public void RefreshLocalization()
    {
        var context = contextProvider();
        ProjectSummary = string.IsNullOrWhiteSpace(context.ProjectPath)
            ? $"{context.Project.Name} | {L("Integration.Project.SaveBeforeExport", "내보내기 전에 프로젝트를 저장하세요.", "Save the project before export.")}"
            : $"{context.Project.Name} | {context.SequenceId} / {context.StepId} | {context.CameraId}";
        TransactionSummary = transactionSummaryProvider();
        StatusText = statusTextProvider();
    }

    private void BrowseExchangeRoot()
    {
        var dialog = new OpenFolderDialog
        {
            Title = L(
                "Integration.Dialog.ExchangeRoot",
                "Machine Studio와 3D Studio가 공유할 교환 폴더 선택",
                "Choose the shared Machine Studio / 3D Studio exchange folder"),
            InitialDirectory = Directory.Exists(ExchangeRoot) ? ExchangeRoot : null
        };
        if (dialog.ShowDialog() == true)
        {
            ExchangeRoot = dialog.FolderName;
            SetStatus(
                "Integration.Status.RootSelected",
                "교환 폴더를 선택했습니다. 설정 저장을 눌러 기억하세요.",
                "Exchange folder selected. Choose Save setup to remember it.");
        }
    }

    private void BrowseInspectionSource()
    {
        var dialog = new OpenFileDialog
        {
            Title = L(
                "Integration.Dialog.Source",
                "이 Machine 프로젝트의 C3D 검사 소스 선택",
                "Choose the C3D inspection source for this Machine project"),
            Filter = L(
                "Integration.Dialog.SourceFilter",
                "C3D 파일 (*.c3d)|*.c3d|모든 파일 (*.*)|*.*",
                "C3D files (*.c3d)|*.c3d|All files (*.*)|*.*"),
            CheckFileExists = true
        };
        if (dialog.ShowDialog() == true)
        {
            InspectionSourcePath = dialog.FileName;
            SetStatus(
                "Integration.Status.SourceSelected",
                "검사 소스를 선택했습니다. 설정 저장을 눌러 이 프로젝트에 기억하세요.",
                "Inspection source selected. Choose Save setup to remember it for this project.");
        }
    }

    private void SaveSetup()
    {
        try
        {
            var context = contextProvider();
            var projectKey = ProjectKey(context.ProjectPath)
                ?? throw new InvalidOperationException(L(
                    "Integration.Error.SaveProjectForSetup",
                    "연동 설정을 저장하기 전에 Machine 프로젝트를 저장하세요.",
                    "Save the Machine project before saving exchange setup."));
            var root = Path.GetFullPath(Require(
                ExchangeRoot,
                L("Integration.Error.ChooseRoot", "교환 폴더를 선택하세요.", "Choose an exchange folder.")));
            var source = Path.GetFullPath(Require(
                InspectionSourcePath,
                L("Integration.Error.ChooseSource", "C3D 검사 소스를 선택하세요.", "Choose a C3D inspection source.")));
            if (!File.Exists(source))
            {
                throw new FileNotFoundException(L(
                    "Integration.Error.SourceNotFound",
                    "선택한 C3D 검사 소스를 찾을 수 없습니다.",
                    "The selected C3D inspection source was not found."), source);
            }
            if (!string.Equals(Path.GetExtension(source), ".c3d", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(L(
                    "Integration.Error.SourceExtension",
                    "검사 소스는 .c3d 파일이어야 합니다.",
                    "The inspection source must be a .c3d file."));
            }

            Directory.CreateDirectory(root);
            settings.ExchangeRoot = root;
            settings.Projects[projectKey] = new ProjectExchangeSettings(source, currentTransactionId);
            settings.Save(settingsPath);
            ExchangeRoot = root;
            InspectionSourcePath = source;
            SetStatus(
                "Integration.Status.Saved",
                "설정을 저장했습니다. 내보내기는 별도의 명시적 작업입니다.",
                "Setup saved. Export remains a separate explicit action.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            SetErrorStatus(exception.Message);
        }
    }

    private void ResetSetup()
    {
        settings = new IntegrationExchangeSettings();
        settings.Save(settingsPath);
        ExchangeRoot = string.Empty;
        InspectionSourcePath = string.Empty;
        currentTransactionId = null;
        SetTransaction(
            "Integration.Transaction.None",
            "이 프로젝트에서 내보낸 Handoff가 없습니다.",
            "No handoff has been exported for this project.");
        SetStatus(
            "Integration.Status.Reset",
            "교환 설정을 초기화했습니다. 연동 작업은 실행하지 않았습니다.",
            "Exchange setup reset. No integration action was run.");
    }

    private void ExportHandoff()
    {
        try
        {
            var context = contextProvider();
            if (context.HasUnsavedChanges)
            {
                throw new InvalidOperationException(L(
                    "Integration.Error.SaveVisibleProject",
                    "화면의 프로젝트와 Handoff가 일치하도록 내보내기 전에 Machine 프로젝트를 저장하세요.",
                    "Save the Machine project before export so the handoff matches the visible project."));
            }
            var projectPath = ProjectKey(context.ProjectPath)
                ?? throw new InvalidOperationException(L(
                    "Integration.Error.SaveProjectForExport",
                    "내보내기 전에 Machine 프로젝트를 저장하세요.",
                    "Save the Machine project before export."));
            var root = RequireSavedSetup(projectPath);
            var producer = producerIdentityProvider();
            var handoff = MachineIntegrationExchange.PublishHandoff(new MachineHandoffRequest(
                root,
                producer,
                context.Project.Id,
                context.Project.Schema,
                context.SequenceId,
                context.StepId,
                context.CameraId,
                "mm",
                $"{context.CameraId}-frame",
                [
                    new(
                        IntegrationArtifactRoles.MachineProject,
                        context.Project.Id,
                        projectPath,
                        "machine-project.ovmachine"),
                    new(
                        IntegrationArtifactRoles.InspectionSource,
                        Path.GetFileNameWithoutExtension(InspectionSourcePath),
                        InspectionSourcePath,
                        "inspection-source.c3d")
                ]));
            currentTransactionId = handoff.TransactionId;
            settings.Projects[projectPath] = new ProjectExchangeSettings(
                InspectionSourcePath,
                currentTransactionId);
            settings.Save(settingsPath);
            SetTransaction(
                "Integration.Transaction.Exported",
                "내보냄: {0:D} | 3D Studio 검토 대기",
                "Exported: {0:D} | waiting for 3D Studio review",
                handoff.TransactionId);
            SetStatus(
                "Integration.Status.Exported",
                "Handoff를 내보냈습니다. 3D Studio에서 명시적으로 새로고침하고 검토해야 합니다.",
                "Handoff exported. 3D Studio must refresh and review it explicitly.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or IntegrationContractException)
        {
            SetErrorStatus(exception.Message);
        }
    }

    private void RefreshResult()
    {
        try
        {
            var projectPath = ProjectKey(contextProvider().ProjectPath)
                ?? throw new InvalidOperationException(L(
                    "Integration.Error.SaveProjectForRefresh",
                    "연동 상태를 새로고침하기 전에 Machine 프로젝트를 저장하세요.",
                    "Save the Machine project before refreshing integration status."));
            var root = RequireSavedSetup(projectPath);
            var transactionId = currentTransactionId
                ?? throw new InvalidOperationException(L(
                    "Integration.Error.ExportBeforeRefresh",
                    "결과를 새로고침하기 전에 Handoff를 내보내세요.",
                    "Export a handoff before refreshing its result."));
            var progress = MachineIntegrationExchange.ReadProgress(root, transactionId);
            if (progress.Result is { } result)
            {
                SetTransaction(
                    "Integration.Transaction.Completed",
                    "완료: {0:D} | {1} | Run {2}",
                    "Completed: {0:D} | {1} | Run {2}",
                    transactionId,
                    result.Disposition,
                    result.RunId);
                SetStatus(
                    "Integration.Status.ResultLoaded",
                    "검증된 3D 결과를 검토용으로 불러왔습니다. 시뮬레이션은 시작하지 않았습니다.",
                    "Validated 3D result loaded for review. Simulation was not started.");
            }
            else if (progress.Acknowledgement is { } acknowledgement)
            {
                if (acknowledgement.Status == IntegrationAcknowledgementStatus.Rejected)
                {
                    SetTransaction(
                        "Integration.Transaction.Rejected",
                        "거절됨: {0:D} | {1}",
                        "Rejected: {0:D} | {1}",
                        transactionId,
                        acknowledgement.Error?.Message ?? string.Empty);
                }
                else
                {
                    SetTransaction(
                        "Integration.Transaction.Accepted",
                        "승인됨: {0:D} | 명시적 3D 결과 대기",
                        "Accepted: {0:D} | waiting for an explicit 3D result",
                        transactionId);
                }
                SetStatus(
                    "Integration.Status.Refreshed",
                    "연동 상태를 새로고침했습니다. 시뮬레이션이나 프로젝트 작업은 실행하지 않았습니다.",
                    "Integration status refreshed. No simulation or project action was run.");
            }
            else
            {
                SetTransaction(
                    "Integration.Transaction.Exported",
                    "내보냄: {0:D} | 3D Studio 검토 대기",
                    "Exported: {0:D} | waiting for 3D Studio review",
                    transactionId);
                SetStatus(
                    "Integration.Status.NoAcknowledgement",
                    "연동 상태를 새로고침했습니다. 아직 승인 또는 거절 응답이 없습니다.",
                    "Integration status refreshed. No acknowledgement is available yet.");
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or IntegrationContractException)
        {
            SetErrorStatus(exception.Message);
        }
    }

    private string RequireSavedSetup(string projectPath)
    {
        if (!string.Equals(settings.ExchangeRoot, Path.GetFullPath(Require(
                ExchangeRoot,
                L("Integration.Error.ChooseAndSaveRoot", "교환 폴더를 선택하고 저장하세요.", "Choose and save an exchange folder."))), StringComparison.OrdinalIgnoreCase)
            || !settings.Projects.TryGetValue(projectPath, out var project)
            || !string.Equals(project.InspectionSourcePath, Path.GetFullPath(Require(
                InspectionSourcePath,
                L("Integration.Error.ChooseAndSaveSource", "C3D 소스를 선택하고 저장하세요.", "Choose and save a C3D source."))), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(L(
                "Integration.Error.SaveCurrentSetup",
                "연동 작업을 실행하기 전에 현재 교환 설정을 저장하세요.",
                "Save the current exchange setup before running an integration action."));
        }
        if (!Directory.Exists(settings.ExchangeRoot))
        {
            throw new DirectoryNotFoundException(L(
                "Integration.Error.RootUnavailable",
                "저장한 교환 폴더를 사용할 수 없습니다. 폴더를 다시 선택하거나 만든 뒤 설정을 저장하세요.",
                "The saved exchange folder is unavailable. Choose or recreate it, then save setup again."));
        }
        if (!File.Exists(project.InspectionSourcePath))
        {
            throw new FileNotFoundException(L(
                "Integration.Error.SourceUnavailable",
                "저장한 C3D 소스를 사용할 수 없습니다. 소스를 다시 선택한 뒤 설정을 저장하세요.",
                "The saved C3D source is unavailable. Choose it again, then save setup."), project.InspectionSourcePath);
        }
        return settings.ExchangeRoot;
    }

    private static IntegrationApplicationIdentity CreateProducerIdentity()
    {
        var version = BuildIdentity.Current.Split('+', 2)[0];
        var sourceState = BuildIdentity.SourceState.ToLowerInvariant() switch
        {
            "clean" => IntegrationSourceState.Clean,
            "dirty" => IntegrationSourceState.Dirty,
            _ => IntegrationSourceState.Unknown
        };
        return new(
            IntegrationApplicationIds.MachineStudio,
            version,
            BuildIdentity.SourceCommit,
            sourceState);
    }

    private static string Require(string value, string message) =>
        string.IsNullOrWhiteSpace(value) ? throw new ArgumentException(message) : value.Trim();

    private static string? ProjectKey(string? path) =>
        string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path);

    private void SetTransaction(string key, string korean, string english, params object?[] arguments)
    {
        transactionSummaryProvider = () => Format(key, korean, english, arguments);
        TransactionSummary = transactionSummaryProvider();
    }

    private void SetStatus(string key, string korean, string english, params object?[] arguments)
    {
        statusTextProvider = () => Format(key, korean, english, arguments);
        StatusText = statusTextProvider();
    }

    private void SetErrorStatus(string detail) => SetStatus(
        "Integration.Status.Error",
        "연동 작업을 완료할 수 없습니다: {0}",
        "The integration action could not be completed: {0}",
        detail);

    private static string L(string key, string korean, string english) =>
        OpenVisionLanguageService.T(key, korean, english);

    private static string Format(
        string key,
        string korean,
        string english,
        params object?[] arguments) =>
        string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            L(key, korean, english),
            arguments);

    private sealed class IntegrationExchangeSettings
    {
        public string ExchangeRoot { get; set; } = string.Empty;
        public Dictionary<string, ProjectExchangeSettings> Projects { get; set; } =
            new(StringComparer.OrdinalIgnoreCase);

        public static IntegrationExchangeSettings Load(string path)
        {
            try
            {
                if (!File.Exists(path))
                {
                    return new();
                }
                var loaded = JsonSerializer.Deserialize<IntegrationExchangeSettings>(File.ReadAllText(path)) ?? new();
                loaded.Projects = new Dictionary<string, ProjectExchangeSettings>(
                    loaded.Projects ?? [],
                    StringComparer.OrdinalIgnoreCase);
                return loaded;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
                return new();
            }
        }

        public void Save(string path)
        {
            var fullPath = Path.GetFullPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            var temporary = $"{fullPath}.{Guid.NewGuid():N}.tmp";
            try
            {
                File.WriteAllText(temporary, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
                File.Move(temporary, fullPath, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
        }
    }

    private sealed record ProjectExchangeSettings(
        string InspectionSourcePath,
        Guid? TransactionId);
}
