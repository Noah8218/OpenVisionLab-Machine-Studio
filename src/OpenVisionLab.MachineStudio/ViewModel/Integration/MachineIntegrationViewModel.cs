using System.Globalization;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32;
using OpenVisionLab;
using OpenVisionLab.Integration.Contracts;
using OpenVisionLab.Integration.Transport.Tcp;
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

public sealed class MachineIntegrationViewModel : ViewModelBase, IDisposable
{
    internal const string SharedKeyEnvironmentVariable = "OPENVISIONLAB_TCP_SHARED_KEY";

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
    private string tcpListenAddress = "127.0.0.1";
    private string tcpListenPortText = "45101";
    private string tcpPeerHost = "127.0.0.1";
    private string tcpPeerPortText = "45102";
    private bool isTcpBusy;
    private bool isTcpListening;
    private string tcpListenerStatusText = string.Empty;
    private string sharedKeyStatusText = string.Empty;
    private string lastTcpTransferText = string.Empty;
    private byte[]? sessionSharedKey;
    private bool hasSessionSharedKeyInput;
    private MachineIntegrationTcpExchange? tcpListener;
    private CancellationTokenSource? tcpOperationCancellation;
    private bool disposed;

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
        BrowseExchangeRootCommand = new RelayCommand(
            _ => BrowseExchangeRoot(),
            _ => CanEditTcpSetup,
            useCommandManagerRequery: false);
        BrowseInspectionSourceCommand = new RelayCommand(
            _ => BrowseInspectionSource(),
            _ => CanEditTcpSetup,
            useCommandManagerRequery: false);
        SaveSetupCommand = new RelayCommand(
            _ => SaveSetup(),
            _ => CanEditTcpSetup,
            useCommandManagerRequery: false);
        ResetSetupCommand = new RelayCommand(
            _ => ResetSetup(),
            _ => CanEditTcpSetup,
            useCommandManagerRequery: false);
        ExportHandoffCommand = new RelayCommand(_ => ExportHandoff());
        RefreshResultCommand = new RelayCommand(_ => RefreshResult());
        StartTcpListenerCommand = new RelayCommand(
            async _ => await StartTcpListenerAsync(),
            _ => !IsTcpBusy && !IsTcpListening,
            useCommandManagerRequery: false);
        StopTcpListenerCommand = new RelayCommand(
            async _ => await StopTcpListenerAsync(),
            _ => !IsTcpBusy && IsTcpListening,
            useCommandManagerRequery: false);
        PingTcpPeerCommand = new RelayCommand(
            async _ => await PingTcpPeerAsync(),
            _ => !IsTcpBusy,
            useCommandManagerRequery: false);
        PushLatestTransactionCommand = new RelayCommand(
            async _ => await PushLatestTransactionAsync(),
            _ => CanPushLatestTransaction,
            useCommandManagerRequery: false);
        PullLatestTransactionCommand = new RelayCommand(
            async _ => await PullLatestTransactionAsync(),
            _ => CanPullLatestTransaction,
            useCommandManagerRequery: false);
        SyncProjectContext();
    }

    public RelayCommand BrowseExchangeRootCommand { get; }
    public RelayCommand BrowseInspectionSourceCommand { get; }
    public RelayCommand SaveSetupCommand { get; }
    public RelayCommand ResetSetupCommand { get; }
    public RelayCommand ExportHandoffCommand { get; }
    public RelayCommand RefreshResultCommand { get; }
    public RelayCommand StartTcpListenerCommand { get; }
    public RelayCommand StopTcpListenerCommand { get; }
    public RelayCommand PingTcpPeerCommand { get; }
    public RelayCommand PushLatestTransactionCommand { get; }
    public RelayCommand PullLatestTransactionCommand { get; }

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

    public string TcpListenAddress
    {
        get => tcpListenAddress;
        set
        {
            if (SetProperty(ref tcpListenAddress, value ?? string.Empty))
            {
                RefreshCommandState();
            }
        }
    }

    public string TcpListenPortText
    {
        get => tcpListenPortText;
        set
        {
            if (SetProperty(ref tcpListenPortText, value ?? string.Empty))
            {
                RefreshCommandState();
            }
        }
    }

    public string TcpPeerHost
    {
        get => tcpPeerHost;
        set
        {
            if (SetProperty(ref tcpPeerHost, value ?? string.Empty))
            {
                RefreshCommandState();
            }
        }
    }

    public string TcpPeerPortText
    {
        get => tcpPeerPortText;
        set
        {
            if (SetProperty(ref tcpPeerPortText, value ?? string.Empty))
            {
                RefreshCommandState();
            }
        }
    }

    public bool IsTcpBusy
    {
        get => isTcpBusy;
        private set
        {
            if (SetProperty(ref isTcpBusy, value))
            {
                OnPropertyChanged(nameof(CanEditTcpSetup));
                RefreshCommandState();
            }
        }
    }

    public bool IsTcpListening
    {
        get => isTcpListening;
        private set
        {
            if (SetProperty(ref isTcpListening, value))
            {
                OnPropertyChanged(nameof(CanEditTcpSetup));
                RefreshCommandState();
            }
        }
    }

    public bool CanEditTcpSetup => !IsTcpBusy && !IsTcpListening;

    public string TcpListenerStatusText
    {
        get => tcpListenerStatusText;
        private set => SetProperty(ref tcpListenerStatusText, value);
    }

    public string SharedKeyStatusText
    {
        get => sharedKeyStatusText;
        private set => SetProperty(ref sharedKeyStatusText, value);
    }

    public string LastTcpTransferText
    {
        get => lastTcpTransferText;
        private set => SetProperty(ref lastTcpTransferText, value);
    }

    public bool CanPushLatestTransaction => !IsTcpBusy && currentTransactionId is not null;

    public bool CanPullLatestTransaction => !IsTcpBusy && currentTransactionId is not null;

    public void SyncProjectContext()
    {
        var context = contextProvider();
        ProjectSummary = string.IsNullOrWhiteSpace(context.ProjectPath)
            ? $"{context.Project.Name} | {L("Integration.Project.SaveBeforeExport", "내보내기 전에 프로젝트를 저장하세요.", "Save the project before export.")}"
            : $"{context.Project.Name} | {context.SequenceId} / {context.StepId} | {context.CameraId}";
        ExchangeRoot = settings.ExchangeRoot;
        TcpListenAddress = settings.TcpListenAddress;
        TcpListenPortText = settings.TcpListenPort.ToString(CultureInfo.InvariantCulture);
        TcpPeerHost = settings.TcpPeerHost;
        TcpPeerPortText = settings.TcpPeerPort.ToString(CultureInfo.InvariantCulture);
        TcpListenerStatusText = L(
            "Integration.Tcp.Stopped",
            "TCP 수신 중지됨",
            "TCP listener stopped");
        SharedKeyStatusText = DescribeSharedKeyStatus();
        LastTcpTransferText = L(
            "Integration.Tcp.NoTransfer",
            "TCP 전송 기록이 없습니다.",
            "No TCP transfer has run.");
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
        RefreshCommandState();
    }

    public void RefreshLocalization()
    {
        var context = contextProvider();
        ProjectSummary = string.IsNullOrWhiteSpace(context.ProjectPath)
            ? $"{context.Project.Name} | {L("Integration.Project.SaveBeforeExport", "내보내기 전에 프로젝트를 저장하세요.", "Save the project before export.")}"
            : $"{context.Project.Name} | {context.SequenceId} / {context.StepId} | {context.CameraId}";
        TransactionSummary = transactionSummaryProvider();
        StatusText = statusTextProvider();
        SharedKeyStatusText = DescribeSharedKeyStatus();
        OnPropertyChanged(nameof(CanEditTcpSetup));
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
            if (!CanEditTcpSetup)
            {
                throw new InvalidOperationException(L(
                    "Integration.Error.TcpSetupBusy",
                    "TCP 작업 중에는 연동 설정을 변경할 수 없습니다.",
                    "Integration setup cannot change while a TCP action is running."));
            }
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

            var tcp = ResolveCurrentTcpSettings(root);

            Directory.CreateDirectory(root);
            settings.ExchangeRoot = root;
            settings.TcpListenAddress = tcp.ListenAddress.ToString();
            settings.TcpListenPort = tcp.ListenPort;
            settings.TcpPeerHost = tcp.PeerHost;
            settings.TcpPeerPort = tcp.PeerPort;
            settings.Projects[projectKey] = new ProjectExchangeSettings(source, currentTransactionId);
            settings.Save(settingsPath);
            ExchangeRoot = root;
            InspectionSourcePath = source;
            TcpListenAddress = settings.TcpListenAddress;
            TcpListenPortText = settings.TcpListenPort.ToString(CultureInfo.InvariantCulture);
            TcpPeerHost = settings.TcpPeerHost;
            TcpPeerPortText = settings.TcpPeerPort.ToString(CultureInfo.InvariantCulture);
            SetStatus(
                "Integration.Status.Saved",
                "설정과 TCP 주소를 저장했습니다. 공유 키와 네트워크 작업은 실행하지 않았습니다.",
                "Setup and TCP endpoints saved. The shared key and network actions were not run.");
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or InvalidOperationException
            or InvalidDataException)
        {
            SetErrorStatus(exception.Message);
        }
    }

    private void ResetSetup()
    {
        if (!CanEditTcpSetup)
        {
            SetErrorStatus(L(
                "Integration.Error.TcpSetupBusy",
                "TCP 작업 중에는 연동 설정을 초기화할 수 없습니다.",
                "Integration setup cannot reset while a TCP action is running."));
            return;
        }

        settings = new IntegrationExchangeSettings();
        settings.Save(settingsPath);
        ExchangeRoot = string.Empty;
        InspectionSourcePath = string.Empty;
        TcpListenAddress = settings.TcpListenAddress;
        TcpListenPortText = settings.TcpListenPort.ToString(CultureInfo.InvariantCulture);
        TcpPeerHost = settings.TcpPeerHost;
        TcpPeerPortText = settings.TcpPeerPort.ToString(CultureInfo.InvariantCulture);
        SetSessionSharedKey(null);
        TcpListenerStatusText = L(
            "Integration.Tcp.Stopped",
            "TCP 수신 중지됨",
            "TCP listener stopped");
        LastTcpTransferText = L(
            "Integration.Tcp.NoTransfer",
            "TCP 전송 기록이 없습니다.",
            "No TCP transfer has run.");
        currentTransactionId = null;
        RefreshCommandState();
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

    private Task StartTcpListenerAsync() => RunTcpOperationAsync(
        L(
            "Integration.Tcp.Starting",
            "TCP 수신을 시작하는 중입니다.",
            "Starting TCP listener."),
        async cancellationToken =>
        {
            if (tcpListener is not null)
            {
                throw new InvalidOperationException(L(
                    "Integration.Tcp.AlreadyStarted",
                    "TCP 수신기가 이미 실행 중입니다.",
                    "The TCP listener is already running."));
            }

            var tcp = RequireSavedTcpSettings();
            var key = AcquireSharedKey();
            MachineIntegrationTcpExchange? listener = null;
            try
            {
                listener = new MachineIntegrationTcpExchange(tcp.ExchangeRoot, key);
                var endpoint = await listener.StartListeningAsync(
                        tcp.ListenAddress,
                        tcp.ListenPort,
                        cancellationToken)
                    .ConfigureAwait(false);
                tcpListener = listener;
                listener = null;
                IsTcpListening = true;
                TcpListenerStatusText = string.Format(
                    CultureInfo.CurrentCulture,
                    L(
                        "Integration.Tcp.Listening",
                        "TCP 수신 중: {0}",
                        "TCP listening: {0}"),
                    endpoint);
                SetStatus(
                    "Integration.Tcp.Started",
                    "TCP 수신을 시작했습니다. 파일 수신만으로 ACK, 검사, Run 또는 Result를 실행하지 않습니다.",
                    "TCP listening started. Receiving files never ACKs, inspects, runs, or creates a Result.");
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
                if (listener is not null)
                {
                    await listener.DisposeAsync().ConfigureAwait(false);
                }
            }
        });

    private Task StopTcpListenerAsync() => RunTcpOperationAsync(
        L(
            "Integration.Tcp.Stopping",
            "TCP 수신을 중지하는 중입니다.",
            "Stopping TCP listener."),
        async cancellationToken =>
        {
            var listener = tcpListener;
            tcpListener = null;
            try
            {
                if (listener is not null)
                {
                    await listener.StopListeningAsync(cancellationToken).ConfigureAwait(false);
                    await listener.DisposeAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                IsTcpListening = false;
                TcpListenerStatusText = L(
                    "Integration.Tcp.Stopped",
                    "TCP 수신 중지됨",
                    "TCP listener stopped");
            }

            SetStatus(
                "Integration.Tcp.StoppedStatus",
                "TCP 수신을 중지했습니다.",
                "TCP listening stopped.");
        });

    private Task PingTcpPeerAsync() => RunTcpTransferAsync(
        L(
            "Integration.Tcp.Pinging",
            "TCP 상대를 확인하는 중입니다.",
            "Pinging TCP peer."),
        (exchange, endpoint, _, cancellationToken) =>
            exchange.PingAsync(endpoint, cancellationToken));

    private Task PushLatestTransactionAsync()
    {
        if (currentTransactionId is not { } transactionId)
        {
            SetErrorStatus(L(
                "Integration.Tcp.TransactionRequired",
                "먼저 Handoff를 내보내고 거래를 선택하세요.",
                "Export a Handoff before pushing the latest transaction."));
            return Task.CompletedTask;
        }

        return RunTcpTransferAsync(
            L(
                "Integration.Tcp.Pushing",
                "최근 거래를 보내는 중입니다.",
                "Pushing the latest transaction."),
            (exchange, endpoint, _, cancellationToken) =>
                exchange.PushTransactionAsync(endpoint, transactionId, cancellationToken));
    }

    private Task PullLatestTransactionAsync()
    {
        if (currentTransactionId is not { } transactionId)
        {
            SetErrorStatus(L(
                "Integration.Tcp.TransactionRequired",
                "먼저 Handoff를 내보내고 거래를 선택하세요.",
                "Export a Handoff before pulling the latest transaction."));
            return Task.CompletedTask;
        }

        return RunTcpTransferAsync(
            L(
                "Integration.Tcp.Pulling",
                "최근 ACK/Result를 받는 중입니다.",
                "Pulling the latest ACK/Result."),
            (exchange, endpoint, _, cancellationToken) =>
                exchange.PullTransactionAsync(endpoint, transactionId, cancellationToken),
            refreshAfterTransfer: true);
    }

    private Task RunTcpTransferAsync(
        string busyStatus,
        Func<
            MachineIntegrationTcpExchange,
            TcpIntegrationEndpoint,
            Guid,
            CancellationToken,
            Task<TcpIntegrationTransferReceipt>> operation,
        bool refreshAfterTransfer = false) =>
        RunTcpOperationAsync(
            busyStatus,
            async cancellationToken =>
            {
                var tcp = RequireSavedTcpSettings();
                var key = AcquireSharedKey();
                try
                {
                    await using var exchange = new MachineIntegrationTcpExchange(
                        tcp.ExchangeRoot,
                        key);
                    var receipt = await operation(
                            exchange,
                            new TcpIntegrationEndpoint(tcp.PeerHost, tcp.PeerPort),
                            currentTransactionId ?? Guid.Empty,
                            cancellationToken)
                        .ConfigureAwait(false);
                    LastTcpTransferText = string.Format(
                        CultureInfo.CurrentCulture,
                        L(
                            "Integration.Tcp.Transfer",
                            "{0} 완료 · 상대 {1} · 거래 {2} · 파일 {3} · 바이트 {4:N0} · 멱등 {5}",
                            "{0} complete · peer {1} · transaction {2} · files {3} · bytes {4:N0} · idempotent {5}"),
                        receipt.Operation,
                        receipt.PeerApplicationId,
                        receipt.TransactionId?.ToString("D") ?? "-",
                        receipt.FilesTransferred,
                        receipt.BytesTransferred,
                        receipt.Idempotent);
                    if (refreshAfterTransfer)
                    {
                        RefreshResult();
                    }

                    StatusText = LastTcpTransferText;
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(key);
                }
            });

    private async Task RunTcpOperationAsync(
        string busyStatus,
        Func<CancellationToken, Task> operation)
    {
        if (disposed || IsTcpBusy)
        {
            return;
        }

        IsTcpBusy = true;
        StatusText = busyStatus;
        using var cancellation = new CancellationTokenSource();
        tcpOperationCancellation = cancellation;
        try
        {
            await operation(cancellation.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            SetStatus(
                "Integration.Tcp.Cancelled",
                "TCP 작업을 취소했습니다.",
                "TCP action cancelled.");
        }
        catch (Exception exception)
        {
            SetErrorStatus(exception.Message);
        }
        finally
        {
            if (ReferenceEquals(tcpOperationCancellation, cancellation))
            {
                tcpOperationCancellation = null;
            }

            IsTcpBusy = false;
        }
    }

    private ResolvedTcpSettings RequireSavedTcpSettings()
    {
        var root = Path.GetFullPath(Require(
            ExchangeRoot,
            L(
                "Integration.Error.ChooseAndSaveRoot",
                "교환 폴더를 선택하고 설정을 저장하세요.",
                "Choose and save an exchange folder.")));
        var current = ResolveCurrentTcpSettings(root);
        if (!string.Equals(settings.ExchangeRoot, root, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(settings.TcpListenAddress, current.ListenAddress.ToString(), StringComparison.OrdinalIgnoreCase)
            || settings.TcpListenPort != current.ListenPort
            || !string.Equals(settings.TcpPeerHost, current.PeerHost, StringComparison.OrdinalIgnoreCase)
            || settings.TcpPeerPort != current.PeerPort)
        {
            throw new InvalidOperationException(L(
                "Integration.Error.SaveCurrentTcpSetup",
                "TCP 작업 전에 현재 교환 폴더와 주소를 설정 저장하세요.",
                "Save the current exchange folder and TCP endpoints before a TCP action."));
        }

        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException(L(
                "Integration.Error.RootUnavailable",
                "저장한 교환 폴더를 사용할 수 없습니다. 폴더를 다시 선택하거나 만든 뒤 설정을 저장하세요.",
                "The saved exchange folder is unavailable. Choose or recreate it, then save setup again."));
        }

        return current;
    }

    private ResolvedTcpSettings ResolveCurrentTcpSettings(string root)
    {
        var listenText = Require(
            TcpListenAddress,
            L(
                "Integration.Error.TcpListenAddressRequired",
                "TCP 수신 주소를 입력하세요.",
                "Enter a TCP listen address."));
        if (!IPAddress.TryParse(listenText, out var listenAddress))
        {
            throw new ArgumentException(L(
                "Integration.Error.TcpListenAddressInvalid",
                "TCP 수신 주소는 올바른 IP 주소여야 합니다.",
                "The TCP listen address must be a valid IP address."));
        }

        return new(
            Path.GetFullPath(root),
            listenAddress,
            ParsePort(TcpListenPortText, L("Integration.Error.TcpListenPort", "수신 포트", "listen port")),
            Require(
                TcpPeerHost,
                L(
                    "Integration.Error.TcpPeerRequired",
                    "TCP 상대 주소를 입력하세요.",
                    "Enter a TCP peer host.")),
            ParsePort(TcpPeerPortText, L("Integration.Error.TcpPeerPort", "상대 포트", "peer port")));
    }

    public void SetSessionSharedKey(string? encodedKey)
    {
        if (sessionSharedKey is not null)
        {
            CryptographicOperations.ZeroMemory(sessionSharedKey);
        }

        sessionSharedKey = null;
        hasSessionSharedKeyInput = !string.IsNullOrWhiteSpace(encodedKey);
        if (!hasSessionSharedKeyInput)
        {
            SharedKeyStatusText = DescribeSharedKeyStatus();
            return;
        }

        try
        {
            var parsed = Convert.FromBase64String(encodedKey!.Trim());
            if (parsed.Length < 32)
            {
                CryptographicOperations.ZeroMemory(parsed);
                SharedKeyStatusText = L(
                    "Integration.Tcp.KeyTooShort",
                    "세션 공유 키는 Base64로 인코딩한 32바이트 이상이어야 합니다.",
                    "The session shared key must be Base64-encoded and contain at least 32 bytes.");
                return;
            }

            sessionSharedKey = parsed;
            SharedKeyStatusText = L(
                "Integration.Tcp.SessionKeyReady",
                "세션 공유 키 준비됨(저장되지 않음)",
                "Session shared key ready (not saved)");
        }
        catch (FormatException)
        {
            SharedKeyStatusText = L(
                "Integration.Tcp.KeyInvalidBase64",
                "세션 공유 키가 올바른 Base64가 아닙니다.",
                "The session shared key is not valid Base64.");
        }
    }

    private byte[] AcquireSharedKey()
    {
        if (hasSessionSharedKeyInput)
        {
            if (sessionSharedKey is null)
            {
                throw new InvalidOperationException(SharedKeyStatusText);
            }

            return sessionSharedKey.ToArray();
        }

        var encoded = Environment.GetEnvironmentVariable(SharedKeyEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(encoded))
        {
            throw new InvalidOperationException(string.Format(
                CultureInfo.CurrentCulture,
                L(
                    "Integration.Tcp.KeyRequired",
                    "세션 공유 키를 입력하거나 환경 변수 {0}에 Base64 키를 설정하세요.",
                    "Enter a session shared key or set environment variable {0} to a Base64 key."),
                SharedKeyEnvironmentVariable));
        }

        try
        {
            var key = Convert.FromBase64String(encoded.Trim());
            if (key.Length >= 32)
            {
                return key;
            }

            CryptographicOperations.ZeroMemory(key);
        }
        catch (FormatException)
        {
            // Use the actionable message below for malformed and short values.
        }

        throw new InvalidOperationException(string.Format(
            CultureInfo.CurrentCulture,
            L(
                "Integration.Tcp.EnvironmentKeyInvalid",
                "환경 변수 {0}에는 Base64로 인코딩한 32바이트 이상의 키가 필요합니다.",
                "Environment variable {0} must contain a Base64 key of at least 32 bytes."),
            SharedKeyEnvironmentVariable));
    }

    private string DescribeSharedKeyStatus()
    {
        var encoded = Environment.GetEnvironmentVariable(SharedKeyEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(encoded))
        {
            return string.Format(
                CultureInfo.CurrentCulture,
                L(
                    "Integration.Tcp.EnvironmentKeyMissing",
                    "공유 키 없음: 세션 입력 또는 환경 변수 {0} 필요",
                    "No shared key: session input or environment variable {0} required"),
                SharedKeyEnvironmentVariable);
        }

        try
        {
            var key = Convert.FromBase64String(encoded.Trim());
            var valid = key.Length >= 32;
            CryptographicOperations.ZeroMemory(key);
            return valid
                ? string.Format(
                    CultureInfo.CurrentCulture,
                    L(
                        "Integration.Tcp.EnvironmentKeyReady",
                        "환경 변수 {0}의 공유 키 준비됨",
                        "Shared key ready from environment variable {0}"),
                    SharedKeyEnvironmentVariable)
                : string.Format(
                    CultureInfo.CurrentCulture,
                    L(
                        "Integration.Tcp.EnvironmentKeyShort",
                        "환경 변수 {0}의 공유 키가 32바이트보다 짧습니다.",
                        "Shared key in environment variable {0} is shorter than 32 bytes."),
                    SharedKeyEnvironmentVariable);
        }
        catch (FormatException)
        {
            return string.Format(
                CultureInfo.CurrentCulture,
                L(
                    "Integration.Tcp.EnvironmentKeyMalformed",
                    "환경 변수 {0}의 공유 키가 올바른 Base64가 아닙니다.",
                    "Shared key in environment variable {0} is not valid Base64."),
                SharedKeyEnvironmentVariable);
        }
    }

    private static int ParsePort(string value, string name)
    {
        if (!int.TryParse(
                value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var port)
            || port is < 1 or > IPEndPoint.MaxPort)
        {
            throw new ArgumentException($"{name} must be between 1 and {IPEndPoint.MaxPort}.");
        }

        return port;
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

    private void RefreshCommandState()
    {
        OnPropertyChanged(nameof(CanEditTcpSetup));
        OnPropertyChanged(nameof(CanPushLatestTransaction));
        OnPropertyChanged(nameof(CanPullLatestTransaction));
        BrowseExchangeRootCommand.RaiseCanExecuteChanged();
        BrowseInspectionSourceCommand.RaiseCanExecuteChanged();
        SaveSetupCommand.RaiseCanExecuteChanged();
        ResetSetupCommand.RaiseCanExecuteChanged();
        ExportHandoffCommand.RaiseCanExecuteChanged();
        RefreshResultCommand.RaiseCanExecuteChanged();
        StartTcpListenerCommand.RaiseCanExecuteChanged();
        StopTcpListenerCommand.RaiseCanExecuteChanged();
        PingTcpPeerCommand.RaiseCanExecuteChanged();
        PushLatestTransactionCommand.RaiseCanExecuteChanged();
        PullLatestTransactionCommand.RaiseCanExecuteChanged();
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

    private sealed record ResolvedTcpSettings(
        string ExchangeRoot,
        IPAddress ListenAddress,
        int ListenPort,
        string PeerHost,
        int PeerPort);

    private sealed class IntegrationExchangeSettings
    {
        public string ExchangeRoot { get; set; } = string.Empty;
        public string TcpListenAddress { get; set; } = "127.0.0.1";
        public int TcpListenPort { get; set; } = 45101;
        public string TcpPeerHost { get; set; } = "127.0.0.1";
        public int TcpPeerPort { get; set; } = 45102;
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

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        tcpOperationCancellation?.Cancel();
        var listener = tcpListener;
        tcpListener = null;
        IsTcpListening = false;
        TcpListenerStatusText = L(
            "Integration.Tcp.Stopped",
            "TCP 수신 중지됨",
            "TCP listener stopped");
        if (sessionSharedKey is not null)
        {
            CryptographicOperations.ZeroMemory(sessionSharedKey);
            sessionSharedKey = null;
        }

        hasSessionSharedKeyInput = false;
        listener?.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    private sealed record ProjectExchangeSettings(
        string InspectionSourcePath,
        Guid? TransactionId);
}
