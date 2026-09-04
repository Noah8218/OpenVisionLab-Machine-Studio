using System.Globalization;
using System.IO;
using System.Net;
using System.ComponentModel;
using System.Windows.Input;
using OpenVisionLab;
using OpenVisionLab.Integration.Contracts;
using OpenVisionLab.Machine.Infrastructure.Integration;
using OpenVisionLab.MachineStudio.View.Dialogs;

namespace OpenVisionLab.MachineStudio.ViewModel;

/// <summary>
/// Owns the explicit Machine Studio integration setup and its read-only
/// transaction projection. It never acknowledges a handoff or starts a
/// consumer inspection.
/// </summary>
public sealed class MachineIntegrationViewModel : ViewModelBase, IDisposable
{
    private readonly Func<string, IntegrationApplicationIdentity, MachineInspectionHandoffRequest?> _requestFactory;
    private readonly Func<string, IntegrationApplicationIdentity, bool> _canBuildRequest;
    private readonly Func<string?> _projectIdProvider;
    private readonly Func<string, string?> _selectExchangeRoot;
    private readonly Func<string, string?> _selectInspectionRecipe;
    private readonly MachineIntegrationSetupStore _setupStore;
    private readonly MachineIntegrationTcpControlViewModel _tcpControl;
    private string _exchangeRoot = string.Empty;
    private string _inspectionRecipePath = string.Empty;
    private string _twoDConsumerVersion = string.Empty;
    private string _twoDConsumerCommit = string.Empty;
    private string _tcpListenAddress = "127.0.0.1";
    private string _tcpListenPortText = "45101";
    private string _tcpPeerHost = "127.0.0.1";
    private string _tcpPeerPortText = "45102";
    private bool _isBusy;
    private readonly MachineIntegrationResultObservationWorkflow _resultObservation;
    private string _statusText = string.Empty;
    private bool _disposed;

    public MachineIntegrationViewModel(
        Func<string, IntegrationApplicationIdentity, MachineInspectionHandoffRequest?> requestFactory,
        Func<string, IntegrationApplicationIdentity, bool> canBuildRequest,
        Func<string?> projectIdProvider,
        string? settingsPath = null)
        : this(
            requestFactory,
            canBuildRequest,
            projectIdProvider,
            settingsPath,
            MachineIntegrationFileDialogHost.SelectExchangeRoot,
            MachineIntegrationFileDialogHost.SelectInspectionRecipe)
    {
    }

    internal MachineIntegrationViewModel(
        Func<string, IntegrationApplicationIdentity, MachineInspectionHandoffRequest?> requestFactory,
        Func<string, IntegrationApplicationIdentity, bool> canBuildRequest,
        Func<string?> projectIdProvider,
        string? settingsPath,
        Func<string, string?> selectExchangeRoot,
        Func<string, string?> selectInspectionRecipe)
    {
        _requestFactory = requestFactory ?? throw new ArgumentNullException(nameof(requestFactory));
        _canBuildRequest = canBuildRequest ?? throw new ArgumentNullException(nameof(canBuildRequest));
        _projectIdProvider = projectIdProvider ?? throw new ArgumentNullException(nameof(projectIdProvider));
        _selectExchangeRoot = selectExchangeRoot ?? throw new ArgumentNullException(nameof(selectExchangeRoot));
        _selectInspectionRecipe = selectInspectionRecipe ?? throw new ArgumentNullException(nameof(selectInspectionRecipe));
        _setupStore = new(settingsPath);

        var settingsLoad = _setupStore.Load();
        var settings = settingsLoad.Settings;
        _exchangeRoot = settings.ExchangeRoot;
        _inspectionRecipePath = settings.InspectionRecipePath;
        _twoDConsumerVersion = settings.TwoDConsumerVersion;
        _twoDConsumerCommit = settings.TwoDConsumerCommit;
        _tcpListenAddress = settings.TcpListenAddress;
        _tcpListenPortText = settings.TcpListenPort.ToString(CultureInfo.InvariantCulture);
        _tcpPeerHost = settings.TcpPeerHost;
        _tcpPeerPortText = settings.TcpPeerPort.ToString(CultureInfo.InvariantCulture);
        _resultObservation = new(
            () => ExchangeRoot,
            _projectIdProvider,
            () => CanRefreshResults,
            () => IsBusy,
            RefreshResultsAsync,
            InvokeOnUiThreadAsync,
            exception =>
            {
                if (!_disposed)
                {
                    StatusText = exception.Message;
                }
            });
        _statusText = settingsLoad.Warning switch
        {
            MachineIntegrationSetupLoadWarning.MissingOrInvalid => L(
                "SettingsIncompatible",
                "저장된 TCP 설정이 없거나 올바르지 않아 기본값을 복원했습니다. 수신은 시작되지 않았습니다.",
                "Saved TCP settings were missing or invalid; defaults were restored. Listening was not started."),
            MachineIntegrationSetupLoadWarning.ReadFailed => string.Format(
                CultureInfo.CurrentCulture,
                L(
                    "SettingsReadFailed",
                    "저장된 TCP 설정을 읽지 못해 기본값을 복원했습니다: {0}",
                    "Saved TCP settings could not be read; defaults were restored: {0}"),
                settingsLoad.ErrorMessage ?? string.Empty),
            _ => L(
                "Ready",
                "설정을 확인했습니다. 폴더를 스캔하거나 검사를 실행하지 않았습니다.",
                "Setup loaded. No folder was scanned and no inspection was run.")
        };
        _tcpControl = new(
            RequireSavedTcpSettings,
            () => _resultObservation.LatestTransaction,
            RefreshResultsAsync,
            status => StatusText = status);
        _tcpControl.PropertyChanged += OnTcpControlPropertyChanged;

        BrowseExchangeRootCommand = new RelayCommand(_ => BrowseExchangeRoot(), useCommandManagerRequery: false);
        BrowseRecipeCommand = new RelayCommand(_ => BrowseRecipe(), useCommandManagerRequery: false);
        SaveSetupCommand = new RelayCommand(
            _ => SaveSetup(),
            _ => CanEditTcpSetup,
            useCommandManagerRequery: false);
        ResetSetupCommand = new RelayCommand(
            _ => ResetSetup(),
            _ => CanEditTcpSetup,
            useCommandManagerRequery: false);
        PublishTwoDImageHandoffCommand = new AsyncRelayCommand(
            _ => PublishTwoDImageHandoffAsync(),
            _ => CanPublishTwoDImageHandoff,
            HandleCommandException,
            useCommandManagerRequery: false);
        RefreshResultsCommand = new AsyncRelayCommand(
            _ => RefreshResultsAsync(),
            _ => CanRefreshResults,
            HandleCommandException,
            useCommandManagerRequery: false);
        _resultObservation.ConfigureWatcher();
    }

    public RelayCommand BrowseExchangeRootCommand { get; }
    public RelayCommand BrowseRecipeCommand { get; }
    public RelayCommand SaveSetupCommand { get; }
    public RelayCommand ResetSetupCommand { get; }
    public AsyncRelayCommand PublishTwoDImageHandoffCommand { get; }
    public AsyncRelayCommand RefreshResultsCommand { get; }
    public RelayCommand StartTcpListenerCommand => _tcpControl.StartTcpListenerCommand;
    public RelayCommand StopTcpListenerCommand => _tcpControl.StopTcpListenerCommand;
    public RelayCommand PingTcpPeerCommand => _tcpControl.PingTcpPeerCommand;
    public RelayCommand PushLatestTransactionCommand => _tcpControl.PushLatestTransactionCommand;
    public RelayCommand PullLatestTransactionCommand => _tcpControl.PullLatestTransactionCommand;

    public string ExchangeRoot
    {
        get => _exchangeRoot;
        set
        {
            if (SetProperty(ref _exchangeRoot, value ?? string.Empty))
            {
                _resultObservation.ConfigureWatcher();
                RefreshCommandState();
                OnPropertyChanged(nameof(ExchangeRootStatusText));
            }
        }
    }

    public string InspectionRecipePath
    {
        get => _inspectionRecipePath;
        set
        {
            if (SetProperty(ref _inspectionRecipePath, value ?? string.Empty))
            {
                RefreshCommandState();
                OnPropertyChanged(nameof(InspectionRecipeStatusText));
            }
        }
    }

    public string TwoDConsumerVersion
    {
        get => _twoDConsumerVersion;
        set
        {
            if (SetProperty(ref _twoDConsumerVersion, value ?? string.Empty))
            {
                RefreshCommandState();
                OnPropertyChanged(nameof(TwoDConsumerIdentityStatusText));
            }
        }
    }

    public string TwoDConsumerCommit
    {
        get => _twoDConsumerCommit;
        set
        {
            if (SetProperty(ref _twoDConsumerCommit, value ?? string.Empty))
            {
                RefreshCommandState();
                OnPropertyChanged(nameof(TwoDConsumerIdentityStatusText));
            }
        }
    }

    public string TcpListenAddress
    {
        get => _tcpListenAddress;
        set
        {
            if (SetProperty(ref _tcpListenAddress, value ?? string.Empty))
            {
                RefreshCommandState();
            }
        }
    }

    public string TcpListenPortText
    {
        get => _tcpListenPortText;
        set
        {
            if (SetProperty(ref _tcpListenPortText, value ?? string.Empty))
            {
                RefreshCommandState();
            }
        }
    }

    public string TcpPeerHost
    {
        get => _tcpPeerHost;
        set
        {
            if (SetProperty(ref _tcpPeerHost, value ?? string.Empty))
            {
                RefreshCommandState();
            }
        }
    }

    public string TcpPeerPortText
    {
        get => _tcpPeerPortText;
        set
        {
            if (SetProperty(ref _tcpPeerPortText, value ?? string.Empty))
            {
                RefreshCommandState();
            }
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                RefreshCommandState();
            }
        }
    }

    public bool IsTcpListening => _tcpControl.IsTcpListening;

    public bool IsTcpBusy => _tcpControl.IsTcpBusy;

    public bool CanEditTcpSetup => _tcpControl.CanEditTcpSetup;

    public string TcpListenerStatusText => _tcpControl.TcpListenerStatusText;

    public string SharedKeyStatusText => _tcpControl.SharedKeyStatusText;

    public string LastTcpTransferText => _tcpControl.LastTcpTransferText;

    public bool CanPublishTwoDImageHandoff
    {
        get
        {
            var consumer = TwoDConsumerIdentity;
            if (IsBusy
                || consumer is null
                || !Directory.Exists(ExchangeRoot.Trim())
                || !File.Exists(InspectionRecipePath.Trim()))
            {
                return false;
            }

            return _canBuildRequest(InspectionRecipePath.Trim(), consumer);
        }
    }

    public bool CanRefreshResults =>
        !IsBusy
        && !string.IsNullOrWhiteSpace(_projectIdProvider())
        && Directory.Exists(ExchangeRoot.Trim());

    public bool CanPushLatestTransaction => _tcpControl.CanPushLatestTransaction;

    public bool CanPullLatestTransaction => _tcpControl.CanPullLatestTransaction;

    public string ExchangeRootStatusText => Directory.Exists(ExchangeRoot.Trim())
        ? L("Available", "교환 폴더를 사용할 수 있습니다.", "Exchange folder is available.")
        : L("Unavailable", "교환 폴더를 선택하고 설정 저장을 누르세요.", "Choose an exchange folder and save setup.");

    public string InspectionRecipeStatusText => File.Exists(InspectionRecipePath.Trim())
        ? L("RecipeAvailable", "2D 검사 레시피 파일을 사용할 수 있습니다.", "The 2D inspection recipe file is available.")
        : L("RecipeRequired", "2D 소비자가 읽을 레시피 파일을 선택하세요.", "Choose the recipe file that the 2D consumer will read.");

    public string TwoDConsumerIdentityStatusText => TwoDConsumerIdentity is not null
        ? L("ConsumerIdentityValid", "2D 소비자 clean build identity가 유효합니다.", "The 2D consumer clean-build identity is valid.")
        : L("ConsumerIdentityInvalid", "2D 소비자 버전과 40자리 source commit을 입력하세요.", "Enter the 2D consumer version and its 40-character source commit.");

    public string HandoffStatusText => _resultObservation.LatestTransaction is null
        ? L("NoHandoff", "현재 프로젝트의 Handoff가 없습니다.", "No Handoff exists for the current project.")
        : string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            L(
                "HandoffStatus",
                "최근 Handoff: {0}/{1} · {2}",
                "Latest Handoff: {0}/{1} · {2}"),
            _resultObservation.LatestTransaction.Handoff.Context.Modality,
            _resultObservation.LatestTransaction.Handoff.Context.InputKind,
            GetTransactionState(_resultObservation.LatestTransaction));

    public string AcknowledgementStatusText => _resultObservation.LatestAcknowledgement is not null
        ? string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            L(
                "AcknowledgementStatus",
                "최근 Acknowledgement: {0}",
                "Latest Acknowledgement: {0}"),
            _resultObservation.LatestAcknowledgement.Status)
        : _resultObservation.AcknowledgementReadError is not null
            ? string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                L(
                    "AcknowledgementReadFailed",
                    "Acknowledgement 읽기 실패: {0}",
                    "Acknowledgement read failed: {0}"),
                _resultObservation.AcknowledgementReadError)
            : _resultObservation.LatestAcknowledgementTransaction is null
                ? L(
                    "NoAcknowledgement",
                    "현재 프로젝트의 Acknowledgement가 없습니다.",
                    "No Acknowledgement exists for the current project.")
                : L(
                    "AcknowledgementPendingValidation",
                    "Acknowledgement를 아직 검증하지 못했습니다.",
                    "Acknowledgement has not been validated yet.");

    public string ResultStatusText => _resultObservation.LatestResult is not null
        ? string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            L(
                "ResultStatus",
                "최근 Result: {0} · {1} · Run {2}",
                "Latest Result: {0} · {1} · Run {2}"),
            _resultObservation.LatestResult.Outcome,
            _resultObservation.LatestResult.Status,
            _resultObservation.LatestResult.RunId)
        : _resultObservation.ResultReadError is not null
            ? string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                L("ResultReadFailed", "Result 읽기 실패: {0}", "Result read failed: {0}"),
                _resultObservation.ResultReadError)
            : _resultObservation.LatestResultTransaction is null
                ? L("NoResult", "아직 검증된 Result가 없습니다.", "No validated Result is available yet.")
                : L("ResultPending", "Result 파일이 있지만 전체 순서를 아직 검증하지 못했습니다.", "A Result file exists, but the complete sequence is not validated yet.");

    public string ProjectionStatusText => _resultObservation.LatestProjectionResult is not null
        ? string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            L(
                "ProjectionStatus",
                "좌표 투영: 2D→3D {0}점 · 3D→2D {1}점",
                "Coordinate projection: 2D→3D {0} point(s) · 3D→2D {1} point(s)"),
            _resultObservation.LatestProjectionResult.TwoDToThreeD.Count,
            _resultObservation.LatestProjectionResult.ThreeDToTwoD.Count)
        : _resultObservation.ProjectionReadError is not null
            ? string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                L(
                    "ProjectionReadFailed",
                    "좌표 투영 읽기 실패: {0}",
                    "Coordinate projection read failed: {0}"),
                _resultObservation.ProjectionReadError)
            : _resultObservation.LatestResult is null
                ? L(
                    "NoProjection",
                    "검증된 좌표 투영 결과가 없습니다.",
                    "No validated coordinate projection is available.")
                : L(
                    "ProjectionNotPublished",
                    "현재 Result에는 좌표 투영 증거가 없습니다.",
                    "The current Result has no coordinate projection evidence.");

    public string LastTransactionText => _resultObservation.LatestTransaction is null
        ? "—"
        : string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            "{0:D} · {1:yyyy-MM-dd HH:mm:ss}",
            _resultObservation.LatestTransaction.Handoff.TransactionId,
            _resultObservation.LatestTransaction.Handoff.CreatedAtUtc.ToLocalTime());

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public IntegrationApplicationIdentity? TwoDConsumerIdentity
    {
        get
        {
            var commit = TwoDConsumerCommit.Trim();
            if (string.IsNullOrWhiteSpace(TwoDConsumerVersion)
                || commit.Length != 40
                || commit.Any(character => !Uri.IsHexDigit(character)))
            {
                return null;
            }

            return new(
                IntegrationApplicationIds.TwoDStudio,
                TwoDConsumerVersion.Trim(),
                commit,
                IntegrationSourceState.Clean);
        }
    }

    public void RefreshContext()
    {
        if (_resultObservation.RefreshContext())
        {
            RaiseProjectionChanged();
        }

        RefreshCommandState();
    }

    public void RefreshLocalization()
    {
        OnPropertyChanged(nameof(ExchangeRootStatusText));
        OnPropertyChanged(nameof(InspectionRecipeStatusText));
        OnPropertyChanged(nameof(TwoDConsumerIdentityStatusText));
        _tcpControl.RefreshLocalization();
        RaiseProjectionChanged();
        StatusText = L(
            "Ready",
            "통합 상태를 갱신했습니다. Publish와 Refresh Result는 별도 명령입니다.",
            "Integration text refreshed. Publish and Refresh Result remain separate commands.");
    }

    public void SetSessionSharedKey(string? encodedKey) => _tcpControl.SetSessionSharedKey(encodedKey);

    internal Task StartTcpListenerAsync() => _tcpControl.StartTcpListenerAsync();

    internal Task StopTcpListenerAsync() => _tcpControl.StopTcpListenerAsync();

    internal Task PingTcpPeerAsync() => _tcpControl.PingTcpPeerAsync();

    internal Task PushLatestTransactionAsync() => _tcpControl.PushLatestTransactionAsync();

    internal Task PullLatestTransactionAsync() => _tcpControl.PullLatestTransactionAsync();

    private void BrowseExchangeRoot()
    {
        if (_selectExchangeRoot(ExchangeRoot) is { } selectedPath)
        {
            ExchangeRoot = selectedPath;
            StatusText = L(
                "FolderSelected",
                "교환 폴더를 선택했습니다. 설정 저장을 눌러 기억하세요.",
                "Exchange folder selected. Choose Save setup to remember it.");
        }
    }

    private void BrowseRecipe()
    {
        if (_selectInspectionRecipe(InspectionRecipePath) is { } selectedPath)
        {
            InspectionRecipePath = selectedPath;
            StatusText = L(
                "RecipeSelected",
                "2D 레시피를 선택했습니다. 설정 저장 후 Publish를 실행하세요.",
                "2D recipe selected. Save setup before publishing.");
        }
    }

    private void SaveSetup()
    {
        try
        {
            var root = RequireText(ExchangeRoot, L("RootRequired", "교환 폴더를 선택하세요.", "Choose an exchange folder."));
            var tcp = ResolveCurrentTcpSettings(root);
            Directory.CreateDirectory(tcp.ExchangeRoot);
            var settings = new MachineIntegrationSetup
            {
                ExchangeRoot = tcp.ExchangeRoot,
                InspectionRecipePath = string.IsNullOrWhiteSpace(InspectionRecipePath)
                    ? string.Empty
                    : Path.GetFullPath(InspectionRecipePath.Trim()),
                TwoDConsumerVersion = TwoDConsumerVersion.Trim(),
                TwoDConsumerCommit = TwoDConsumerCommit.Trim(),
                TcpListenAddress = tcp.ListenAddress.ToString(),
                TcpListenPort = tcp.ListenPort,
                TcpPeerHost = tcp.PeerHost,
                TcpPeerPort = tcp.PeerPort
            };
            _setupStore.Save(settings);
            ExchangeRoot = settings.ExchangeRoot;
            InspectionRecipePath = settings.InspectionRecipePath;
            TwoDConsumerVersion = settings.TwoDConsumerVersion;
            TwoDConsumerCommit = settings.TwoDConsumerCommit;
            TcpListenAddress = settings.TcpListenAddress;
            TcpListenPortText = settings.TcpListenPort.ToString(CultureInfo.InvariantCulture);
            TcpPeerHost = settings.TcpPeerHost;
            TcpPeerPortText = settings.TcpPeerPort.ToString(CultureInfo.InvariantCulture);
            StatusText = L(
                "SetupSaved",
                "교환 폴더와 TCP 주소를 저장했습니다. 공유 키는 저장하지 않으며 네트워크/Publish/Refresh는 실행하지 않았습니다.",
                "Exchange folder and TCP endpoints saved. The shared key is not saved; network, Publish, and Refresh were not run.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            StatusText = exception.Message;
        }
    }

    private void ResetSetup()
    {
        try
        {
            _setupStore.Reset();
            ExchangeRoot = string.Empty;
            InspectionRecipePath = string.Empty;
            TwoDConsumerVersion = string.Empty;
            TwoDConsumerCommit = string.Empty;
            TcpListenAddress = "127.0.0.1";
            TcpListenPortText = "45101";
            TcpPeerHost = "127.0.0.1";
            TcpPeerPortText = "45102";
            SetSessionSharedKey(null);
            _resultObservation.Reset();
            RaiseProjectionChanged();
            StatusText = L(
                "SetupReset",
                "통합 설정을 초기화했습니다. Handoff 작업은 실행하지 않았습니다.",
                "Integration setup reset. No Handoff action was run.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            StatusText = exception.Message;
        }
    }

    private async Task PublishTwoDImageHandoffAsync()
    {
        if (!CanPublishTwoDImageHandoff || TwoDConsumerIdentity is not { } consumer)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var request = _requestFactory(InspectionRecipePath.Trim(), consumer)
                ?? throw new InvalidOperationException(
                    L(
                        "ContextNotReady",
                        "저장된 프로젝트에서 완료된 가상 카메라 프레임을 먼저 준비하세요.",
                        "Prepare a completed virtual-camera frame from a saved project first."));
            var handoff = await MachineIntegrationHandoffPublisher.PublishAsync(
                    ExchangeRoot.Trim(),
                    request)
                .ConfigureAwait(true);
            _resultObservation.RecordPublishedHandoff(handoff);
            RaiseProjectionChanged();
            StatusText = string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                L("HandoffPublished", "2D/Image Handoff 게시 완료: {0:D}", "2D/Image Handoff published: {0:D}"),
                handoff.TransactionId);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or InvalidOperationException
            or IntegrationContractException)
        {
            StatusText = exception.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RefreshResultsAsync()
    {
        if (_disposed || !CanRefreshResults)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var transactionCount = await _resultObservation.RefreshAsync().ConfigureAwait(true);
            if (transactionCount is null)
            {
                return;
            }

            RaiseProjectionChanged();
            StatusText = string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                L("ResultsRefreshed", "현재 프로젝트 거래 {0}개를 확인했습니다. 실행은 하지 않았습니다.", "Found {0} current-project transaction(s). No inspection was run."),
                transactionCount.Value);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or InvalidOperationException
            or IntegrationContractException)
        {
            StatusText = exception.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private string GetTransactionState(MachineIntegrationTransactionSummary transaction) =>
        transaction.HasResult
            ? L("ResultPublished", "결과 게시됨", "Result published")
            : _resultObservation.LatestAcknowledgement?.TransactionId == transaction.Handoff.TransactionId
                ? _resultObservation.LatestAcknowledgement.Status == IntegrationAcknowledgementStatus.Rejected
                    ? L("Rejected", "거절됨", "Rejected")
                    : L("Reviewed", "검토됨", "Reviewed")
            : transaction.HasAcknowledgement
                ? L("Reviewed", "검토됨", "Reviewed")
                : L("PendingReview", "검토 대기", "Pending review");

    private void RefreshCommandState()
    {
        OnPropertyChanged(nameof(CanPublishTwoDImageHandoff));
        OnPropertyChanged(nameof(CanRefreshResults));
        PublishTwoDImageHandoffCommand.RaiseCanExecuteChanged();
        RefreshResultsCommand.RaiseCanExecuteChanged();
        SaveSetupCommand.RaiseCanExecuteChanged();
        ResetSetupCommand.RaiseCanExecuteChanged();
        _tcpControl.RefreshCommandState();
    }

    private void RaiseProjectionChanged()
    {
        OnPropertyChanged(nameof(HandoffStatusText));
        OnPropertyChanged(nameof(AcknowledgementStatusText));
        OnPropertyChanged(nameof(ResultStatusText));
        OnPropertyChanged(nameof(ProjectionStatusText));
        OnPropertyChanged(nameof(LastTransactionText));
    }

    private void OnTcpControlPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is { } propertyName)
        {
            OnPropertyChanged(propertyName);
        }
    }

    private static Task InvokeOnUiThreadAsync(Func<Task> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        return dispatcher is not null && !dispatcher.CheckAccess()
            ? dispatcher.InvokeAsync(operation).Task.Unwrap()
            : operation();
    }

    private void HandleCommandException(Exception exception) => StatusText = exception.Message;

    private MachineIntegrationTcpSettings RequireSavedTcpSettings()
    {
        var root = Path.GetFullPath(RequireText(
            ExchangeRoot,
            L(
                "ChooseAndSaveFolder",
                "교환 폴더를 선택하고 저장하세요.",
                "Choose and save an exchange folder.")));
        var current = ResolveCurrentTcpSettings(root);
        if (!_setupStore.MatchesSavedTcpSettings(current))
        {
            throw new InvalidOperationException(L(
                "SaveCurrentTcpSetup",
                "TCP 작업 전에 현재 교환 폴더와 주소를 설정 저장하세요.",
                "Save the current exchange folder and TCP endpoints before a TCP action."));
        }

        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException(L(
                "SavedFolderUnavailable",
                "저장한 교환 폴더를 사용할 수 없습니다. 폴더를 다시 선택하거나 만든 뒤 설정을 저장하세요.",
                "The saved exchange folder is unavailable. Choose or recreate it, then save setup again."));
        }

        return current;
    }

    private MachineIntegrationTcpSettings ResolveCurrentTcpSettings(string exchangeRoot)
    {
        var listenAddressText = RequireText(
            TcpListenAddress,
            L(
                "TcpListenAddressRequired",
                "TCP 수신 주소를 입력하세요.",
                "Enter a TCP listen address."));
        if (!IPAddress.TryParse(listenAddressText, out var listenAddress))
        {
            throw new ArgumentException(L(
                "TcpListenAddressInvalid",
                "TCP 수신 주소는 이 PC의 올바른 IP 주소여야 합니다.",
                "The TCP listen address must be a valid IP address on this PC."));
        }

        return new(
            Path.GetFullPath(exchangeRoot),
            listenAddress,
            ParsePort(
                TcpListenPortText,
                L("TcpListenPort", "수신 포트", "listen port")),
            RequireText(
                TcpPeerHost,
                L("TcpPeerRequired", "TCP 상대 주소를 입력하세요.", "Enter a TCP peer host.")),
            ParsePort(
                TcpPeerPortText,
                L("TcpPeerPort", "상대 포트", "peer port")));
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
            throw new ArgumentException(
                $"{name} must be between 1 and {IPEndPoint.MaxPort}.");
        }

        return port;
    }

    private static string RequireText(string value, string message) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException(message)
            : value.Trim();

    private static string L(string key, string korean, string english) =>
        OpenVisionLanguageService.CurrentLanguage == OpenVisionLanguage.English
            ? english
            : korean;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _tcpControl.PropertyChanged -= OnTcpControlPropertyChanged;
        _resultObservation.Dispose();
        _tcpControl.Dispose();
    }
}
