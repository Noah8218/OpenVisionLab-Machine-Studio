using System.Globalization;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Collections.ObjectModel;
using OpenVisionLab;
using OpenVisionLab.Integration.Contracts;
using OpenVisionLab.Machine.Infrastructure.Integration;
using OpenVisionLab.Machine.Infrastructure.Vision;

namespace OpenVisionLab.MachineStudio.ViewModel;

public sealed class MachineIntegrationRecipeItemViewModel : ViewModelBase
{
    public required string Path { get; init; }

    public string DisplayName => System.IO.Path.GetFileNameWithoutExtension(Path);

    public bool IsAvailable => File.Exists(Path);

    public bool IsActive { get; private set; }

    public string AvailabilityText => OpenVisionLanguageService.T(
        IsAvailable ? "Integration.MmiRecipeAvailable" : "Integration.MmiRecipeMissing");

    public string ActiveText => IsActive
        ? OpenVisionLanguageService.T("Integration.MmiRecipeActive")
        : string.Empty;

    internal void SetActive(bool isActive)
    {
        if (IsActive == isActive)
        {
            return;
        }

        IsActive = isActive;
        OnPropertyChanged(nameof(IsActive));
        OnPropertyChanged(nameof(ActiveText));
    }

    internal void OnPropertyChangedForLocalization()
    {
        OnPropertyChanged(nameof(AvailabilityText));
        OnPropertyChanged(nameof(ActiveText));
    }
}

/// <summary>
/// Owns the editable Machine Studio integration setup form. It validates and
/// persists paths, consumer identity and TCP endpoints, but does not publish a
/// handoff, observe results or start a network operation.
/// </summary>
public sealed class MachineIntegrationSetupViewModel : ViewModelBase, IDisposable
{
    private readonly MachineIntegrationPathReadinessPolicy _pathReadiness;
    private readonly MachineIntegrationSetupStore _setupStore;
    private readonly Func<string, string?> _selectExchangeRoot;
    private readonly Func<string, string?> _selectInspectionRecipe;
    private readonly Func<string, string?> _selectHeightMapSource;
    private readonly Func<bool> _canEditTcpSetup;
    private readonly Action<string> _setStatus;
    private readonly Action _clearSessionSharedKey;
    private readonly ObservableCollection<MachineIntegrationRecipeItemViewModel> _recipeCatalogItems = new();
    private string _exchangeRoot = string.Empty;
    private string _inspectionRecipePath = string.Empty;
    private string _twoDConsumerVersion = string.Empty;
    private string _twoDConsumerCommit = string.Empty;
    private bool _waitForExternalResult;
    private bool _useThreeDHeightMap;
    private string _threeDHeightMapSourcePath = string.Empty;
    private string _threeDHeightMapSourceSha256 = string.Empty;
    private long _threeDHeightMapSourceLength;
    private string _threeDHeightMapWidthText = string.Empty;
    private string _threeDHeightMapHeightText = string.Empty;
    private string _threeDHeightMapPixelFormat = string.Empty;
    private string _threeDHeightMapUnit = "mm";
    private string _threeDInspectionRecipePath = string.Empty;
    private string _threeDInspectionRecipeSha256 = string.Empty;
    private long _threeDInspectionRecipeLength;
    private string _threeDConsumerVersion = string.Empty;
    private string _threeDConsumerCommit = string.Empty;
    private string _threeDSequenceId = string.Empty;
    private string _threeDStepId = string.Empty;
    private string _threeDDeviceId = string.Empty;
    private string _tcpListenAddress = "127.0.0.1";
    private string _tcpListenPortText = "45101";
    private string _tcpPeerHost = "127.0.0.1";
    private string _tcpPeerPortText = "45102";
    private int _resetVersion;
    private MachineIntegrationRecipeItemViewModel? _selectedRecipe;
    private bool _isApplyingSettings;
    private bool _hasUnsavedChanges;
    private bool _disposed;

    internal MachineIntegrationSetupViewModel(
        string? settingsPath,
        MachineIntegrationPathReadinessPolicy pathReadiness,
        Func<string, string?> selectExchangeRoot,
        Func<string, string?> selectInspectionRecipe,
        Func<bool> canEditTcpSetup,
        Action<string> setStatus,
        Action clearSessionSharedKey,
        Func<string, string?>? selectHeightMapSource = null)
    {
        _pathReadiness = pathReadiness ?? throw new ArgumentNullException(nameof(pathReadiness));
        _setupStore = new MachineIntegrationSetupStore(settingsPath);
        _selectExchangeRoot = selectExchangeRoot ?? throw new ArgumentNullException(nameof(selectExchangeRoot));
        _selectInspectionRecipe = selectInspectionRecipe ?? throw new ArgumentNullException(nameof(selectInspectionRecipe));
        _selectHeightMapSource = selectHeightMapSource ?? selectInspectionRecipe;
        _canEditTcpSetup = canEditTcpSetup ?? throw new ArgumentNullException(nameof(canEditTcpSetup));
        _setStatus = setStatus ?? throw new ArgumentNullException(nameof(setStatus));
        _clearSessionSharedKey = clearSessionSharedKey ?? throw new ArgumentNullException(nameof(clearSessionSharedKey));
        RecipeCatalogItems = new ReadOnlyObservableCollection<MachineIntegrationRecipeItemViewModel>(_recipeCatalogItems);

        BrowseExchangeRootCommand = new RelayCommand(
            _ => BrowseExchangeRoot(),
            _ => !IsDisposed,
            useCommandManagerRequery: false);
        BrowseRecipeCommand = new RelayCommand(
            _ => BrowseRecipe(),
            _ => !IsDisposed,
            useCommandManagerRequery: false);
        AddRecipeCommand = new RelayCommand(
            _ => BrowseRecipe(),
            _ => !IsDisposed,
            useCommandManagerRequery: false);
        BrowseHeightMapSourceCommand = new RelayCommand(
            _ => BrowseHeightMapSource(),
            _ => !IsDisposed,
            useCommandManagerRequery: false);
        RemoveRecipeCommand = new RelayCommand(
            parameter => RemoveRecipe(parameter as MachineIntegrationRecipeItemViewModel),
            parameter => !IsDisposed
                && CanEditTcpSetup
                && parameter is MachineIntegrationRecipeItemViewModel,
            useCommandManagerRequery: false);
        SaveSetupCommand = new RelayCommand(
            _ => SaveSetup(),
            _ => !IsDisposed && CanEditTcpSetup,
            useCommandManagerRequery: false);
        ResetSetupCommand = new RelayCommand(
            _ => ResetSetup(),
            _ => !IsDisposed && CanEditTcpSetup,
            useCommandManagerRequery: false);

        var settingsLoad = _setupStore.Load();
        ApplySettings(settingsLoad.Settings);
        _setStatus(CreateLoadStatus(settingsLoad));
    }

    public RelayCommand BrowseExchangeRootCommand { get; }
    public RelayCommand BrowseRecipeCommand { get; }
    public RelayCommand AddRecipeCommand { get; }
    public RelayCommand BrowseHeightMapSourceCommand { get; }
    public RelayCommand RemoveRecipeCommand { get; }
    public RelayCommand SaveSetupCommand { get; }
    public RelayCommand ResetSetupCommand { get; }

    public ReadOnlyObservableCollection<MachineIntegrationRecipeItemViewModel> RecipeCatalogItems { get; }

    public MachineIntegrationRecipeItemViewModel? SelectedRecipe
    {
        get => _selectedRecipe;
        set
        {
            if (!SetProperty(ref _selectedRecipe, value))
            {
                return;
            }

            RemoveRecipeCommand.RaiseCanExecuteChanged();
            if (!_isApplyingSettings && value is not null)
            {
                InspectionRecipePath = value.Path;
                _setStatus(string.Format(
                    CultureInfo.CurrentCulture,
                    L(
                        "MmiRecipeSelected",
                        "검사 레시피를 선택했습니다: {0}. 설정 저장 후 다음 검사 요청부터 사용합니다.",
                        "Inspection recipe selected: {0}. Save setup before the next inspection request."),
                    value.DisplayName));
            }
        }
    }

    public bool HasRecipeCatalogItems => RecipeCatalogItems.Count > 0;

    public bool HasUnsavedChanges => _hasUnsavedChanges;

    public string SetupSaveStateText => HasUnsavedChanges
        ? L(
            "MmiSetupUnsaved",
            "변경 사항이 저장되지 않았습니다. 다음 요청 전에 설정 저장을 누르세요.",
            "Unsaved changes. Save setup before creating the next request.")
        : L(
            "MmiSetupSaved",
            "저장된 통합 설정을 사용합니다.",
            "Using the saved integration setup.");

    public string RecipeCatalogStatusText
    {
        get
        {
            if (SelectedRecipe is null)
            {
                return L(
                    "MmiRecipeCatalogEmpty",
                    "등록된 2D 레시피가 없습니다. 레시피를 추가하세요.",
                    "No 2D recipes are registered. Add a recipe.");
            }

            return string.Format(
                CultureInfo.CurrentCulture,
                L(
                    "MmiRecipeCatalogStatus",
                    "{0}개 레시피 · 활성: {1}",
                    "{0} recipe(s) · Active: {1}"),
                RecipeCatalogItems.Count,
                SelectedRecipe.DisplayName);
        }
    }

    public int ResetVersion => _resetVersion;

    public string ExchangeRoot
    {
        get => _exchangeRoot;
        set
        {
            if (SetProperty(ref _exchangeRoot, value ?? string.Empty))
            {
                MarkUnsavedChanges();
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
                MarkUnsavedChanges();
                RefreshCommandState();
                OnPropertyChanged(nameof(InspectionRecipeStatusText));
                OnPropertyChanged(nameof(Readiness));
                UpdateRecipeSelectionState();
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
                MarkUnsavedChanges();
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
                MarkUnsavedChanges();
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
                MarkUnsavedChanges();
                RefreshCommandState();
            }
        }
    }

    public bool WaitForExternalResult
    {
        get => _waitForExternalResult;
        set
        {
            if (SetProperty(ref _waitForExternalResult, value))
            {
                MarkUnsavedChanges();
                RefreshCommandState();
            }
        }
    }

    public bool UseThreeDHeightMap
    {
        get => _useThreeDHeightMap;
        set
        {
            if (SetProperty(ref _useThreeDHeightMap, value))
            {
                MarkUnsavedChanges();
                RefreshCommandState();
                OnPropertyChanged(nameof(ActiveInspectionRecipePath));
                OnPropertyChanged(nameof(ActiveConsumerIdentity));
                OnPropertyChanged(nameof(ThreeDSetupStatusText));
            }
        }
    }

    public string ThreeDHeightMapSourcePath
    {
        get => _threeDHeightMapSourcePath;
        set
        {
            if (SetProperty(ref _threeDHeightMapSourcePath, value ?? string.Empty))
            {
                MarkUnsavedChanges();
                OnPropertyChanged(nameof(ThreeDHeightMapSourceStatusText));
                OnPropertyChanged(nameof(ThreeDSetupStatusText));
            }
        }
    }

    public string ThreeDHeightMapWidthText
    {
        get => _threeDHeightMapWidthText;
        set
        {
            if (SetProperty(ref _threeDHeightMapWidthText, value ?? string.Empty))
            {
                MarkUnsavedChanges();
                OnPropertyChanged(nameof(ThreeDSetupStatusText));
            }
        }
    }

    public string ThreeDHeightMapHeightText
    {
        get => _threeDHeightMapHeightText;
        set
        {
            if (SetProperty(ref _threeDHeightMapHeightText, value ?? string.Empty))
            {
                MarkUnsavedChanges();
                OnPropertyChanged(nameof(ThreeDSetupStatusText));
            }
        }
    }

    public string ThreeDHeightMapPixelFormat
    {
        get => _threeDHeightMapPixelFormat;
        set
        {
            if (SetProperty(ref _threeDHeightMapPixelFormat, value ?? string.Empty))
            {
                MarkUnsavedChanges();
                OnPropertyChanged(nameof(ThreeDSetupStatusText));
            }
        }
    }

    public string ThreeDHeightMapUnit
    {
        get => _threeDHeightMapUnit;
        set
        {
            if (SetProperty(ref _threeDHeightMapUnit, value ?? string.Empty))
            {
                MarkUnsavedChanges();
                OnPropertyChanged(nameof(ThreeDSetupStatusText));
            }
        }
    }

    public string ThreeDInspectionRecipePath
    {
        get => _threeDInspectionRecipePath;
        set
        {
            if (SetProperty(ref _threeDInspectionRecipePath, value ?? string.Empty))
            {
                MarkUnsavedChanges();
                OnPropertyChanged(nameof(ThreeDInspectionRecipeStatusText));
                OnPropertyChanged(nameof(ThreeDSetupStatusText));
                OnPropertyChanged(nameof(ActiveInspectionRecipePath));
            }
        }
    }

    public string ThreeDConsumerVersion
    {
        get => _threeDConsumerVersion;
        set
        {
            if (SetProperty(ref _threeDConsumerVersion, value ?? string.Empty))
            {
                MarkUnsavedChanges();
                RefreshCommandState();
                OnPropertyChanged(nameof(ThreeDConsumerIdentityStatusText));
                OnPropertyChanged(nameof(ThreeDConsumerIdentity));
                OnPropertyChanged(nameof(ActiveConsumerIdentity));
            }
        }
    }

    public string ThreeDConsumerCommit
    {
        get => _threeDConsumerCommit;
        set
        {
            if (SetProperty(ref _threeDConsumerCommit, value ?? string.Empty))
            {
                MarkUnsavedChanges();
                RefreshCommandState();
                OnPropertyChanged(nameof(ThreeDConsumerIdentityStatusText));
                OnPropertyChanged(nameof(ThreeDConsumerIdentity));
                OnPropertyChanged(nameof(ActiveConsumerIdentity));
            }
        }
    }

    public string ThreeDSequenceId
    {
        get => _threeDSequenceId;
        set
        {
            if (SetProperty(ref _threeDSequenceId, value ?? string.Empty))
            {
                MarkUnsavedChanges();
                OnPropertyChanged(nameof(ThreeDSetupStatusText));
            }
        }
    }

    public string ThreeDStepId
    {
        get => _threeDStepId;
        set
        {
            if (SetProperty(ref _threeDStepId, value ?? string.Empty))
            {
                MarkUnsavedChanges();
                OnPropertyChanged(nameof(ThreeDSetupStatusText));
            }
        }
    }

    public string ThreeDDeviceId
    {
        get => _threeDDeviceId;
        set
        {
            if (SetProperty(ref _threeDDeviceId, value ?? string.Empty))
            {
                MarkUnsavedChanges();
                OnPropertyChanged(nameof(ThreeDSetupStatusText));
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
                MarkUnsavedChanges();
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
                MarkUnsavedChanges();
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
                MarkUnsavedChanges();
                RefreshCommandState();
            }
        }
    }

    public bool CanEditTcpSetup => _canEditTcpSetup();

    public string ExchangeRootStatusText => Readiness.IsExchangeRootAvailable
        ? L("Available", "교환 폴더를 사용할 수 있습니다.", "Exchange folder is available.")
        : L("Unavailable", "교환 폴더를 선택하고 설정 저장을 누르세요.", "Choose an exchange folder and save setup.");

    public string InspectionRecipeStatusText => !Readiness.IsInspectionRecipeAvailable
        ? L("RecipeRequired", "2D 소비자가 읽을 레시피 파일을 선택하세요.", "Choose the recipe file that the 2D consumer will read.")
        : !Readiness.IsInspectionRecipeSupportedByTwoD
            ? L(
                "RecipeWrongTarget",
                "3D 레시피가 선택되었습니다. 2D 검사 레시피를 선택하세요.",
                "A 3D recipe is selected. Choose a 2D inspection recipe.")
            : L("RecipeAvailable", "2D 검사 레시피 파일을 사용할 수 있습니다.", "The 2D inspection recipe file is available.");

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

    public string ThreeDHeightMapSourceStatusText =>
        IsThreeDSourcePathAvailable()
            ? L(
                "ThreeDSourceAvailable",
                "3D HeightMap 원본 파일을 사용할 수 있습니다.",
                "The 3D HeightMap source file is available.")
            : L(
                "ThreeDSourceRequired",
                "3D HeightMap 원본 C3D 파일을 선택하고 설정을 저장하세요.",
                "Choose the 3D HeightMap C3D source and save setup.");

    public string ThreeDInspectionRecipeStatusText =>
        !IsThreeDInspectionRecipeAvailable()
            ? L(
                "ThreeDRecipeRequired",
                "3D c3d 레시피 파일을 선택하고 설정을 저장하세요.",
                "Choose a 3D c3d recipe file and save setup.")
            : !_pathReadiness.IsInspectionRecipeSupportedByThreeD(ThreeDInspectionRecipePath)
                ? L(
                    "ThreeDRecipeWrongTarget",
                    "선택한 레시피는 3D c3d 레시피가 아닙니다.",
                    "The selected recipe is not a 3D c3d recipe.")
                : L(
                    "ThreeDRecipeAvailable",
                    "3D HeightMap 레시피를 사용할 수 있습니다.",
                    "The 3D HeightMap recipe is available.");

    public string ThreeDConsumerIdentityStatusText => ThreeDConsumerIdentity is not null
        ? L("ThreeDConsumerIdentityValid", "3D 소비자 clean build identity가 유효합니다.", "The 3D consumer clean-build identity is valid.")
        : L("ThreeDConsumerIdentityInvalid", "3D 소비자 버전과 40자리 source commit을 입력하세요.", "Enter the 3D consumer version and its 40-character source commit.");

    public string ThreeDSetupStatusText => !UseThreeDHeightMap
        ? L("TwoDSetupActive", "2D/Image 자동 외부 검사 설정을 사용합니다.", "2D/Image automatic external inspection setup is active.")
        : IsThreeDAutomaticSetupReady
            ? L("ThreeDSetupReady", "3D/HeightMap 자동 외부 검사 설정이 준비되었습니다.", "3D/HeightMap automatic external inspection setup is ready.")
            : L("ThreeDSetupIncomplete", "3D/HeightMap source, recipe, identity, metadata, sequence/device binding을 저장하세요.", "Save the 3D/HeightMap source, recipe, identity, metadata, and sequence/device binding.");

    public IntegrationApplicationIdentity? ThreeDConsumerIdentity
    {
        get
        {
            var commit = ThreeDConsumerCommit.Trim();
            if (string.IsNullOrWhiteSpace(ThreeDConsumerVersion)
                || commit.Length != 40
                || commit.Any(character => !Uri.IsHexDigit(character)))
            {
                return null;
            }

            return new(
                IntegrationApplicationIds.ThreeDStudio,
                ThreeDConsumerVersion.Trim(),
                commit,
                IntegrationSourceState.Clean);
        }
    }

    internal string ActiveInspectionRecipePath => UseThreeDHeightMap
        ? ThreeDInspectionRecipePath
        : InspectionRecipePath;

    internal IntegrationApplicationIdentity? ActiveConsumerIdentity => UseThreeDHeightMap
        ? ThreeDConsumerIdentity
        : TwoDConsumerIdentity;

    internal bool IsThreeDAutomaticSetupReady =>
        UseThreeDHeightMap
        && !HasUnsavedChanges
        && IsThreeDSourcePathAvailable()
        && IsThreeDInspectionRecipeAvailable()
        && _pathReadiness.IsInspectionRecipeSupportedByThreeD(ThreeDInspectionRecipePath)
        && ThreeDConsumerIdentity is not null
        && TryParsePositiveInt(ThreeDHeightMapWidthText, out _)
        && TryParsePositiveInt(ThreeDHeightMapHeightText, out _)
        && !string.IsNullOrWhiteSpace(ThreeDHeightMapPixelFormat)
        && !string.IsNullOrWhiteSpace(ThreeDHeightMapUnit)
        && TryGetThreeDRecipeFrameId(out _)
        && !string.IsNullOrWhiteSpace(ThreeDSequenceId)
        && !string.IsNullOrWhiteSpace(ThreeDStepId)
        && !string.IsNullOrWhiteSpace(ThreeDDeviceId)
        && HasStoredEvidence(_threeDHeightMapSourceSha256, _threeDHeightMapSourceLength)
        && HasStoredEvidence(_threeDInspectionRecipeSha256, _threeDInspectionRecipeLength);

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

    internal bool TryGetThreeDHeightMapSource(
        string projectPath,
        out MachineIntegrationHeightMapSourceDefinition source)
    {
        source = null!;
        if (!IsThreeDAutomaticSetupReady || string.IsNullOrWhiteSpace(projectPath))
        {
            return false;
        }

        try
        {
            var projectRoot = Path.GetDirectoryName(Path.GetFullPath(projectPath));
            if (string.IsNullOrWhiteSpace(projectRoot)
                || !TryParsePositiveInt(ThreeDHeightMapWidthText, out var width)
                || !TryParsePositiveInt(ThreeDHeightMapHeightText, out var height)
                || !TryGetThreeDRecipeFrameId(out var frameId))
            {
                return false;
            }

            var projectAssets = new ProjectAssetPathResolver(projectRoot);
            var relativePath = Path.GetRelativePath(
                projectRoot,
                _pathReadiness.NormalizeFullPath(ThreeDHeightMapSourcePath));
            var asset = projectAssets.ResolveExistingFile(relativePath);
            var evidence = ReadEvidence(asset.FullPath);
            if (!EvidenceMatches(evidence, _threeDHeightMapSourceSha256, _threeDHeightMapSourceLength))
            {
                return false;
            }

            source = new(
                asset.RelativePath,
                width,
                height,
                ThreeDHeightMapPixelFormat.Trim(),
                ThreeDHeightMapUnit.Trim(),
                frameId,
                evidence);
            return true;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or InvalidOperationException
            or InvalidDataException)
        {
            return false;
        }
    }

    internal bool TryGetThreeDInspectionRecipeEvidence(
        out MachineIntegrationArtifactEvidence evidence)
    {
        evidence = null!;
        if (!IsThreeDAutomaticSetupReady)
        {
            return false;
        }

        try
        {
            evidence = ReadEvidence(ThreeDInspectionRecipePath);
            return EvidenceMatches(
                evidence,
                _threeDInspectionRecipeSha256,
                _threeDInspectionRecipeLength);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or InvalidDataException)
        {
            return false;
        }
    }

    internal void RefreshLocalization()
    {
        OnPropertyChanged(nameof(ExchangeRootStatusText));
        OnPropertyChanged(nameof(InspectionRecipeStatusText));
        OnPropertyChanged(nameof(TwoDConsumerIdentityStatusText));
        OnPropertyChanged(nameof(ThreeDHeightMapSourceStatusText));
        OnPropertyChanged(nameof(ThreeDInspectionRecipeStatusText));
        OnPropertyChanged(nameof(ThreeDConsumerIdentityStatusText));
        OnPropertyChanged(nameof(ThreeDSetupStatusText));
        OnPropertyChanged(nameof(SetupSaveStateText));
        OnPropertyChanged(nameof(RecipeCatalogStatusText));
        foreach (var item in RecipeCatalogItems)
        {
            item.OnPropertyChangedForLocalization();
        }
    }

    internal void RefreshCommandState()
    {
        BrowseExchangeRootCommand.RaiseCanExecuteChanged();
        BrowseRecipeCommand.RaiseCanExecuteChanged();
        AddRecipeCommand.RaiseCanExecuteChanged();
        BrowseHeightMapSourceCommand.RaiseCanExecuteChanged();
        RemoveRecipeCommand.RaiseCanExecuteChanged();
        SaveSetupCommand.RaiseCanExecuteChanged();
        ResetSetupCommand.RaiseCanExecuteChanged();
    }

    private void BrowseExchangeRoot()
    {
        if (IsDisposed)
        {
            return;
        }

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
        if (IsDisposed)
        {
            return;
        }

        if (_selectInspectionRecipe(InspectionRecipePath) is { } selectedPath)
        {
            var normalizedPath = NormalizeRecipePath(selectedPath);
            InspectionRecipePath = normalizedPath;
            EnsureRecipeCatalogPath(normalizedPath);
            _setStatus(L(
                "RecipeSelected",
                "2D 레시피를 선택했습니다. 설정 저장 후 검사 요청을 만드세요.",
                "2D recipe selected. Save setup before creating an inspection request."));
        }
    }

    private void BrowseHeightMapSource()
    {
        if (IsDisposed)
        {
            return;
        }

        if (_selectHeightMapSource(ThreeDHeightMapSourcePath) is { } selectedPath)
        {
            ThreeDHeightMapSourcePath = NormalizeOptionalPath(selectedPath);
            _setStatus(L(
                "ThreeDSourceSelected",
                "3D HeightMap 원본을 선택했습니다. 설정 저장 전에는 자동 Handoff에 사용하지 않습니다.",
                "3D HeightMap source selected. It will not be used for automatic Handoff until setup is saved."));
        }
    }

    private void RemoveRecipe(MachineIntegrationRecipeItemViewModel? recipe)
    {
        if (IsDisposed || recipe is null || !_recipeCatalogItems.Remove(recipe))
        {
            return;
        }

        var nextActivePath = string.Equals(
                InspectionRecipePath,
                recipe.Path,
                StringComparison.OrdinalIgnoreCase)
            ? _recipeCatalogItems.FirstOrDefault()?.Path ?? string.Empty
            : InspectionRecipePath;
        RebuildRecipeCatalogItems(_recipeCatalogItems.Select(item => item.Path));
        InspectionRecipePath = nextActivePath;
        SelectedRecipe = _recipeCatalogItems.FirstOrDefault(item =>
            string.Equals(item.Path, InspectionRecipePath, StringComparison.OrdinalIgnoreCase));
        _setStatus(L(
            "MmiRecipeRemoved",
            "검사 레시피 목록에서 제거했습니다. 파일은 삭제하지 않았습니다.",
            "Removed the recipe from the inspection list. The file was not deleted."));
    }

    private void SaveSetup()
    {
        if (IsDisposed)
        {
            return;
        }

        try
        {
            var root = RequireText(
                ExchangeRoot,
                L("RootRequired", "교환 폴더를 선택하세요.", "Choose an exchange folder."));
            var tcp = ResolveCurrentTcpSettings(root);
            EnsureRecipeCatalogPath(InspectionRecipePath);
            if (UseThreeDHeightMap
                && (ThreeDConsumerIdentity is null
                    || string.IsNullOrWhiteSpace(ThreeDSequenceId)
                    || string.IsNullOrWhiteSpace(ThreeDStepId)
                    || string.IsNullOrWhiteSpace(ThreeDDeviceId)))
            {
                throw new ArgumentException(L(
                    "ThreeDBindingRequired",
                    "3D consumer identity와 Sequence, Step, Device binding을 입력하세요.",
                    "Enter the 3D consumer identity and Sequence, Step, and Device binding."));
            }
            var threeDSourceEvidence = UseThreeDHeightMap
                ? CaptureRequiredThreeDSourceSettings()
                : CaptureOptionalThreeDSourceSettings();
            var threeDRecipe = UseThreeDHeightMap
                ? CaptureRequiredThreeDRecipeEvidence()
                : CaptureOptionalThreeDRecipeEvidence();
            var settings = new MachineIntegrationSetup
            {
                ExchangeRoot = tcp.ExchangeRoot,
                InspectionRecipePath = _pathReadiness.NormalizeFullPath(InspectionRecipePath),
                RecipeCatalogPaths = RecipeCatalogItems.Select(item => item.Path).ToArray(),
                TwoDConsumerVersion = TwoDConsumerVersion.Trim(),
                TwoDConsumerCommit = TwoDConsumerCommit.Trim(),
                WaitForExternalResult = WaitForExternalResult,
                UseThreeDHeightMap = UseThreeDHeightMap,
                ThreeDHeightMapSourcePath = NormalizeOptionalPath(ThreeDHeightMapSourcePath),
                ThreeDHeightMapSourceSha256 = threeDSourceEvidence?.ContentSha256 ?? string.Empty,
                ThreeDHeightMapSourceLength = threeDSourceEvidence?.ContentLength ?? 0,
                ThreeDHeightMapWidth = TryParsePositiveInt(ThreeDHeightMapWidthText, out var sourceWidth)
                    ? sourceWidth
                    : 0,
                ThreeDHeightMapHeight = TryParsePositiveInt(ThreeDHeightMapHeightText, out var sourceHeight)
                    ? sourceHeight
                    : 0,
                ThreeDHeightMapPixelFormat = ThreeDHeightMapPixelFormat.Trim(),
                ThreeDHeightMapUnit = ThreeDHeightMapUnit.Trim(),
                ThreeDInspectionRecipePath = NormalizeOptionalPath(ThreeDInspectionRecipePath),
                ThreeDInspectionRecipeSha256 = threeDRecipe?.ContentSha256 ?? string.Empty,
                ThreeDInspectionRecipeLength = threeDRecipe?.ContentLength ?? 0,
                ThreeDConsumerVersion = ThreeDConsumerVersion.Trim(),
                ThreeDConsumerCommit = ThreeDConsumerCommit.Trim(),
                ThreeDSequenceId = ThreeDSequenceId.Trim(),
                ThreeDStepId = ThreeDStepId.Trim(),
                ThreeDDeviceId = ThreeDDeviceId.Trim(),
                TcpListenAddress = tcp.ListenAddress.ToString(),
                TcpListenPort = tcp.ListenPort,
                TcpPeerHost = tcp.PeerHost,
                TcpPeerPort = tcp.PeerPort
            };

            _setupStore.Save(settings);
            ApplySettings(settings);
            SetUnsavedChanges(false);
            _setStatus(L(
                "SetupSaved",
                "검사 레시피 목록, 교환 폴더, 외부 결과 대기 옵션, TCP 주소를 저장했습니다. 공유 키는 저장하지 않으며 전송·결과 확인·적용은 실행하지 않았습니다.",
                "Inspection recipes, exchange folder, external result waiting option, and TCP endpoints saved. The shared key is not saved; transfer, result refresh, and apply were not run."));
        }
        catch (Exception exception) when (exception is IOException
            or InvalidDataException
            or UnauthorizedAccessException
            or ArgumentException)
        {
            _setStatus(exception.Message);
        }
    }

    private void ResetSetup()
    {
        if (IsDisposed)
        {
            return;
        }

        try
        {
            _setupStore.Reset();
            ApplySettings(new MachineIntegrationSetup());
            SetUnsavedChanges(false);
            _clearSessionSharedKey();
            _resetVersion++;
            OnPropertyChanged(nameof(ResetVersion));
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
        _isApplyingSettings = true;
        try
        {
            ExchangeRoot = settings.ExchangeRoot;
            InspectionRecipePath = settings.InspectionRecipePath;
            TwoDConsumerVersion = settings.TwoDConsumerVersion;
            TwoDConsumerCommit = settings.TwoDConsumerCommit;
            WaitForExternalResult = settings.WaitForExternalResult;
            UseThreeDHeightMap = settings.UseThreeDHeightMap;
            ThreeDHeightMapSourcePath = settings.ThreeDHeightMapSourcePath;
            _threeDHeightMapSourceSha256 = settings.ThreeDHeightMapSourceSha256 ?? string.Empty;
            _threeDHeightMapSourceLength = settings.ThreeDHeightMapSourceLength;
            ThreeDHeightMapWidthText = settings.ThreeDHeightMapWidth > 0
                ? settings.ThreeDHeightMapWidth.ToString(CultureInfo.InvariantCulture)
                : string.Empty;
            ThreeDHeightMapHeightText = settings.ThreeDHeightMapHeight > 0
                ? settings.ThreeDHeightMapHeight.ToString(CultureInfo.InvariantCulture)
                : string.Empty;
            ThreeDHeightMapPixelFormat = settings.ThreeDHeightMapPixelFormat;
            ThreeDHeightMapUnit = string.IsNullOrWhiteSpace(settings.ThreeDHeightMapUnit)
                ? "mm"
                : settings.ThreeDHeightMapUnit;
            ThreeDInspectionRecipePath = settings.ThreeDInspectionRecipePath;
            _threeDInspectionRecipeSha256 = settings.ThreeDInspectionRecipeSha256 ?? string.Empty;
            _threeDInspectionRecipeLength = settings.ThreeDInspectionRecipeLength;
            ThreeDConsumerVersion = settings.ThreeDConsumerVersion;
            ThreeDConsumerCommit = settings.ThreeDConsumerCommit;
            ThreeDSequenceId = settings.ThreeDSequenceId;
            ThreeDStepId = settings.ThreeDStepId;
            ThreeDDeviceId = settings.ThreeDDeviceId;
            TcpListenAddress = settings.TcpListenAddress;
            TcpListenPortText = settings.TcpListenPort.ToString(CultureInfo.InvariantCulture);
            TcpPeerHost = settings.TcpPeerHost;
            TcpPeerPortText = settings.TcpPeerPort.ToString(CultureInfo.InvariantCulture);
        }
        finally
        {
            _isApplyingSettings = false;
        }

        var paths = (settings.RecipeCatalogPaths ?? Array.Empty<string>())
            .Append(settings.InspectionRecipePath)
            .Select(NormalizeRecipePath)
            .Where(path => !string.IsNullOrWhiteSpace(path));
        RebuildRecipeCatalogItems(paths);
        SetUnsavedChanges(false);
    }

    private void MarkUnsavedChanges()
    {
        if (!_isApplyingSettings)
        {
            SetUnsavedChanges(true);
        }
    }

    private void SetUnsavedChanges(bool value)
    {
        if (_hasUnsavedChanges == value)
        {
            return;
        }

        _hasUnsavedChanges = value;
        OnPropertyChanged(nameof(HasUnsavedChanges));
        OnPropertyChanged(nameof(SetupSaveStateText));
    }

    private void EnsureRecipeCatalogPath(string? path)
    {
        var normalizedPath = NormalizeRecipePath(path);
        if (string.IsNullOrWhiteSpace(normalizedPath)
            || _recipeCatalogItems.Any(item => string.Equals(
                item.Path,
                normalizedPath,
                StringComparison.OrdinalIgnoreCase)))
        {
            UpdateRecipeSelectionState();
            return;
        }

        RebuildRecipeCatalogItems(_recipeCatalogItems.Select(item => item.Path).Append(normalizedPath));
    }

    private void RebuildRecipeCatalogItems(IEnumerable<string> paths)
    {
        var normalizedPaths = paths
            .Select(NormalizeRecipePath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        _recipeCatalogItems.Clear();
        foreach (var path in normalizedPaths)
        {
            _recipeCatalogItems.Add(new MachineIntegrationRecipeItemViewModel { Path = path });
        }

        UpdateRecipeSelectionState();
        OnPropertyChanged(nameof(HasRecipeCatalogItems));
        OnPropertyChanged(nameof(RecipeCatalogStatusText));
        RemoveRecipeCommand.RaiseCanExecuteChanged();
    }

    private void UpdateRecipeSelectionState()
    {
        var active = _recipeCatalogItems.FirstOrDefault(item => string.Equals(
            item.Path,
            NormalizeRecipePath(InspectionRecipePath),
            StringComparison.OrdinalIgnoreCase));
        foreach (var item in _recipeCatalogItems)
        {
            item.SetActive(ReferenceEquals(item, active));
        }

        if (!ReferenceEquals(_selectedRecipe, active))
        {
            _selectedRecipe = active;
            OnPropertyChanged(nameof(SelectedRecipe));
        }

        OnPropertyChanged(nameof(RecipeCatalogStatusText));
        RemoveRecipeCommand?.RaiseCanExecuteChanged();
    }

    private string NormalizeRecipePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        try
        {
            return _pathReadiness.NormalizeFullPath(path);
        }
        catch (ArgumentException)
        {
            return string.Empty;
        }
    }

    private bool IsThreeDSourcePathAvailable() =>
        !string.IsNullOrWhiteSpace(ThreeDHeightMapSourcePath)
        && File.Exists(_pathReadiness.Normalize(ThreeDHeightMapSourcePath));

    private bool IsThreeDInspectionRecipeAvailable() =>
        _pathReadiness.IsInspectionRecipeAvailable(ThreeDInspectionRecipePath);

    private MachineIntegrationArtifactEvidence CaptureRequiredThreeDSourceSettings()
    {
        var path = RequireText(
            ThreeDHeightMapSourcePath,
            L(
                "ThreeDSourceRequired",
                "3D HeightMap 원본 C3D 파일을 선택하세요.",
                "Choose the 3D HeightMap C3D source file."));
        if (!string.Equals(Path.GetExtension(path), ".c3d", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(L(
                "ThreeDSourceExtension",
                "3D HeightMap 원본은 .c3d 파일이어야 합니다.",
                "The 3D HeightMap source must be a .c3d file."));
        }

        if (!TryParsePositiveInt(ThreeDHeightMapWidthText, out _)
            || !TryParsePositiveInt(ThreeDHeightMapHeightText, out _)
            || string.IsNullOrWhiteSpace(ThreeDHeightMapPixelFormat)
            || string.IsNullOrWhiteSpace(ThreeDHeightMapUnit))
        {
            throw new ArgumentException(L(
                "ThreeDSourceMetadataRequired",
                "3D HeightMap width, height, pixel format, unit을 입력하세요.",
                "Enter the 3D HeightMap width, height, pixel format, and unit."));
        }

        return ReadEvidence(path);
    }

    private MachineIntegrationArtifactEvidence? CaptureOptionalThreeDSourceSettings()
    {
        if (string.IsNullOrWhiteSpace(ThreeDHeightMapSourcePath))
        {
            return null;
        }

        return IsThreeDSourcePathAvailable()
            ? ReadEvidence(ThreeDHeightMapSourcePath)
            : null;
    }

    private MachineIntegrationArtifactEvidence CaptureRequiredThreeDRecipeEvidence()
    {
        if (!IsThreeDInspectionRecipeAvailable()
            || !_pathReadiness.IsInspectionRecipeSupportedByThreeD(ThreeDInspectionRecipePath)
            || !TryGetThreeDRecipeFrameId(out _))
        {
            throw new ArgumentException(L(
                "ThreeDRecipeRequired",
                "사용할 수 있는 c3d 3D 레시피 파일을 선택하세요.",
                "Choose an available c3d 3D recipe file."));
        }

        return ReadEvidence(ThreeDInspectionRecipePath);
    }

    private bool TryGetThreeDRecipeFrameId(out string frameId)
    {
        frameId = string.Empty;
        if (!IsThreeDInspectionRecipeAvailable())
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(ThreeDInspectionRecipePath));
            if (!document.RootElement.TryGetProperty("step", out var step)
                || !step.TryGetProperty("frameId", out var frameProperty)
                || frameProperty.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            frameId = frameProperty.GetString()?.Trim() ?? string.Empty;
            return frameId.Length > 0;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or JsonException
            or InvalidOperationException)
        {
            return false;
        }
    }

    private MachineIntegrationArtifactEvidence? CaptureOptionalThreeDRecipeEvidence()
    {
        if (!IsThreeDInspectionRecipeAvailable())
        {
            return null;
        }

        return ReadEvidence(ThreeDInspectionRecipePath);
    }

    private static MachineIntegrationArtifactEvidence ReadEvidence(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var info = new FileInfo(fullPath);
        if (!info.Exists || info.Length <= 0)
        {
            throw new InvalidDataException($"Integration artifact is missing or empty: '{fullPath}'.");
        }

        return new(
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(fullPath))),
            info.Length);
    }

    private static bool EvidenceMatches(
        MachineIntegrationArtifactEvidence actual,
        string expectedSha256,
        long expectedLength) =>
        expectedLength > 0
        && string.Equals(actual.ContentSha256, expectedSha256, StringComparison.OrdinalIgnoreCase)
        && actual.ContentLength == expectedLength;

    private static bool HasStoredEvidence(string sha256, long length) =>
        length > 0
        && sha256.Length == 64
        && sha256.All(Uri.IsHexDigit);

    private string NormalizeOptionalPath(string? path) =>
        string.IsNullOrWhiteSpace(path)
            ? string.Empty
            : _pathReadiness.NormalizeFullPath(path);

    private static bool TryParsePositiveInt(string value, out int parsed) =>
        int.TryParse(
            value,
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out parsed)
        && parsed > 0;

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

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        RefreshCommandState();
    }

    private bool IsDisposed => _disposed;
}
