using System.Globalization;
using System.IO;
using System.Net;
using OpenVisionLab;
using OpenVisionLab.Integration.Contracts;
using OpenVisionLab.Machine.Infrastructure.Integration;

namespace OpenVisionLab.MachineStudio.ViewModel;

/// <summary>
/// Owns the editable Machine Studio integration setup form. It validates and
/// persists paths, consumer identity and TCP endpoints, but does not publish a
/// handoff, observe results or start a network operation.
/// </summary>
public sealed class MachineIntegrationSetupViewModel : ViewModelBase
{
    private readonly MachineIntegrationPathReadinessPolicy _pathReadiness;
    private readonly MachineIntegrationSetupStore _setupStore;
    private readonly Func<string, string?> _selectExchangeRoot;
    private readonly Func<string, string?> _selectInspectionRecipe;
    private readonly Func<bool> _canEditTcpSetup;
    private readonly Action<string> _setStatus;
    private readonly Action _clearSessionSharedKey;
    private string _exchangeRoot = string.Empty;
    private string _inspectionRecipePath = string.Empty;
    private string _twoDConsumerVersion = string.Empty;
    private string _twoDConsumerCommit = string.Empty;
    private string _tcpListenAddress = "127.0.0.1";
    private string _tcpListenPortText = "45101";
    private string _tcpPeerHost = "127.0.0.1";
    private string _tcpPeerPortText = "45102";

    internal MachineIntegrationSetupViewModel(
        string? settingsPath,
        MachineIntegrationPathReadinessPolicy pathReadiness,
        Func<string, string?> selectExchangeRoot,
        Func<string, string?> selectInspectionRecipe,
        Func<bool> canEditTcpSetup,
        Action<string> setStatus,
        Action clearSessionSharedKey)
    {
        _pathReadiness = pathReadiness ?? throw new ArgumentNullException(nameof(pathReadiness));
        _setupStore = new MachineIntegrationSetupStore(settingsPath);
        _selectExchangeRoot = selectExchangeRoot ?? throw new ArgumentNullException(nameof(selectExchangeRoot));
        _selectInspectionRecipe = selectInspectionRecipe ?? throw new ArgumentNullException(nameof(selectInspectionRecipe));
        _canEditTcpSetup = canEditTcpSetup ?? throw new ArgumentNullException(nameof(canEditTcpSetup));
        _setStatus = setStatus ?? throw new ArgumentNullException(nameof(setStatus));
        _clearSessionSharedKey = clearSessionSharedKey ?? throw new ArgumentNullException(nameof(clearSessionSharedKey));

        BrowseExchangeRootCommand = new RelayCommand(
            _ => BrowseExchangeRoot(),
            useCommandManagerRequery: false);
        BrowseRecipeCommand = new RelayCommand(
            _ => BrowseRecipe(),
            useCommandManagerRequery: false);
        SaveSetupCommand = new RelayCommand(
            _ => SaveSetup(),
            _ => CanEditTcpSetup,
            useCommandManagerRequery: false);
        ResetSetupCommand = new RelayCommand(
            _ => ResetSetup(),
            _ => CanEditTcpSetup,
            useCommandManagerRequery: false);

        var settingsLoad = _setupStore.Load();
        ApplySettings(settingsLoad.Settings);
        _setStatus(CreateLoadStatus(settingsLoad));
    }

    public RelayCommand BrowseExchangeRootCommand { get; }
    public RelayCommand BrowseRecipeCommand { get; }
    public RelayCommand SaveSetupCommand { get; }
    public RelayCommand ResetSetupCommand { get; }

    public string ExchangeRoot
    {
        get => _exchangeRoot;
        set
        {
            if (SetProperty(ref _exchangeRoot, value ?? string.Empty))
            {
                RefreshCommandState();
                OnPropertyChanged(nameof(ExchangeRootStatusText));
                OnPropertyChanged(nameof(Readiness));
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
                OnPropertyChanged(nameof(Readiness));
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
                OnPropertyChanged(nameof(TwoDConsumerIdentity));
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
                OnPropertyChanged(nameof(TwoDConsumerIdentity));
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

    public bool CanEditTcpSetup => _canEditTcpSetup();

    public string ExchangeRootStatusText => Readiness.IsExchangeRootAvailable
        ? L("Available", "교환 폴더를 사용할 수 있습니다.", "Exchange folder is available.")
        : L("Unavailable", "교환 폴더를 선택하고 설정 저장을 누르세요.", "Choose an exchange folder and save setup.");

    public string InspectionRecipeStatusText => Readiness.IsInspectionRecipeAvailable
        ? L("RecipeAvailable", "2D 검사 레시피 파일을 사용할 수 있습니다.", "The 2D inspection recipe file is available.")
        : L("RecipeRequired", "2D 소비자가 읽을 레시피 파일을 선택하세요.", "Choose the recipe file that the 2D consumer will read.");

    public string TwoDConsumerIdentityStatusText => TwoDConsumerIdentity is not null
        ? L("ConsumerIdentityValid", "2D 소비자 clean build identity가 유효합니다.", "The 2D consumer clean-build identity is valid.")
        : L("ConsumerIdentityInvalid", "2D 소비자 버전과 40자리 source commit을 입력하세요.", "Enter the 2D consumer version and its 40-character source commit.");

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

    public MachineIntegrationPathReadinessSnapshot Readiness =>
        _pathReadiness.Evaluate(ExchangeRoot, InspectionRecipePath);

    internal event EventHandler? ResetCompleted;

    internal MachineIntegrationTcpSettings RequireSavedTcpSettings()
    {
        var root = _pathReadiness.NormalizeFullPath(RequireText(
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

        if (!_pathReadiness.IsExchangeRootAvailable(root))
        {
            throw new DirectoryNotFoundException(L(
                "SavedFolderUnavailable",
                "저장한 교환 폴더를 사용할 수 없습니다. 폴더를 다시 선택하거나 만든 뒤 설정을 저장하세요.",
                "The saved exchange folder is unavailable. Choose or recreate it, then save setup again."));
        }

        return current;
    }

    internal string NormalizePath(string? path) => _pathReadiness.Normalize(path);

    internal void RefreshLocalization()
    {
        OnPropertyChanged(nameof(ExchangeRootStatusText));
        OnPropertyChanged(nameof(InspectionRecipeStatusText));
        OnPropertyChanged(nameof(TwoDConsumerIdentityStatusText));
    }

    internal void RefreshCommandState()
    {
        SaveSetupCommand.RaiseCanExecuteChanged();
        ResetSetupCommand.RaiseCanExecuteChanged();
    }

    private void BrowseExchangeRoot()
    {
        if (_selectExchangeRoot(ExchangeRoot) is { } selectedPath)
        {
            ExchangeRoot = selectedPath;
            _setStatus(L(
                "FolderSelected",
                "교환 폴더를 선택했습니다. 설정 저장을 눌러 기억하세요.",
                "Exchange folder selected. Choose Save setup to remember it."));
        }
    }

    private void BrowseRecipe()
    {
        if (_selectInspectionRecipe(InspectionRecipePath) is { } selectedPath)
        {
            InspectionRecipePath = selectedPath;
            _setStatus(L(
                "RecipeSelected",
                "2D 레시피를 선택했습니다. 설정 저장 후 Publish를 실행하세요.",
                "2D recipe selected. Save setup before publishing."));
        }
    }

    private void SaveSetup()
    {
        try
        {
            var root = RequireText(
                ExchangeRoot,
                L("RootRequired", "교환 폴더를 선택하세요.", "Choose an exchange folder."));
            var tcp = ResolveCurrentTcpSettings(root);
            var settings = new MachineIntegrationSetup
            {
                ExchangeRoot = tcp.ExchangeRoot,
                InspectionRecipePath = _pathReadiness.NormalizeFullPath(InspectionRecipePath),
                TwoDConsumerVersion = TwoDConsumerVersion.Trim(),
                TwoDConsumerCommit = TwoDConsumerCommit.Trim(),
                TcpListenAddress = tcp.ListenAddress.ToString(),
                TcpListenPort = tcp.ListenPort,
                TcpPeerHost = tcp.PeerHost,
                TcpPeerPort = tcp.PeerPort
            };

            _setupStore.Save(settings);
            ApplySettings(settings);
            _setStatus(L(
                "SetupSaved",
                "교환 폴더와 TCP 주소를 저장했습니다. 공유 키는 저장하지 않으며 네트워크/Publish/Refresh는 실행하지 않았습니다.",
                "Exchange folder and TCP endpoints saved. The shared key is not saved; network, Publish, and Refresh were not run."));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _setStatus(exception.Message);
        }
    }

    private void ResetSetup()
    {
        try
        {
            _setupStore.Reset();
            ApplySettings(new MachineIntegrationSetup());
            _clearSessionSharedKey();
            ResetCompleted?.Invoke(this, EventArgs.Empty);
            _setStatus(L(
                "SetupReset",
                "통합 설정을 초기화했습니다. Handoff 작업은 실행하지 않았습니다.",
                "Integration setup reset. No Handoff action was run."));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _setStatus(exception.Message);
        }
    }

    private void ApplySettings(MachineIntegrationSetup settings)
    {
        ExchangeRoot = settings.ExchangeRoot;
        InspectionRecipePath = settings.InspectionRecipePath;
        TwoDConsumerVersion = settings.TwoDConsumerVersion;
        TwoDConsumerCommit = settings.TwoDConsumerCommit;
        TcpListenAddress = settings.TcpListenAddress;
        TcpListenPortText = settings.TcpListenPort.ToString(CultureInfo.InvariantCulture);
        TcpPeerHost = settings.TcpPeerHost;
        TcpPeerPortText = settings.TcpPeerPort.ToString(CultureInfo.InvariantCulture);
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
            _pathReadiness.NormalizeFullPath(exchangeRoot),
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

    private static string CreateLoadStatus(MachineIntegrationSetupLoadResult settingsLoad) =>
        settingsLoad.Warning switch
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

    private static string L(string key, string korean, string english) =>
        OpenVisionLanguageService.CurrentLanguage == OpenVisionLanguage.English
            ? english
            : korean;
}
