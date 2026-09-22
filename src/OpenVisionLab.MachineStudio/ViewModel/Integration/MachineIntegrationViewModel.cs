using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Net;
using OpenVisionLab;
using OpenVisionLab.Integration.Contracts;
using OpenVisionLab.Machine.Infrastructure.Integration;
using OpenVisionLab.Machine.Simulation.Commands;
using OpenVisionLab.Machine.Simulation.Engine;
using OpenVisionLab.Machine.Simulation.Snapshots;
using OpenVisionLab.MachineStudio.View.Dialogs;

namespace OpenVisionLab.MachineStudio.ViewModel;

public sealed record MachineIntegrationDiagnosticFilterItem(
    MachineIntegrationTransactionState? State,
    string DisplayText);

public enum MachineIntegrationTransactionHistoryState
{
    PendingReview,
    Reviewed,
    Rejected,
    ResultPublished,
    ResultReadFailed
}

public sealed record MachineIntegrationTransactionHistoryFilterItem(
    MachineIntegrationTransactionHistoryState? State,
    string DisplayText);

public sealed record MachineIntegrationTransactionHistoryItem(
    IntegrationHandoffV2 Handoff,
    string StatusText)
{
    public string ShortTransactionId => $"TX-{Handoff.TransactionId.ToString("N")[..8].ToUpperInvariant()}";

    public string FullTransactionId => Handoff.TransactionId.ToString("D");
}

/// <summary>
/// Composes the explicit Machine Studio integration setup with its read-only
/// transaction projection; setup editing belongs to <see cref="MachineIntegrationSetupViewModel"/>.
/// This owner never acknowledges a handoff or starts a consumer inspection.
/// </summary>
public sealed class MachineIntegrationViewModel : ViewModelBase, IDisposable
{
    private readonly Func<string, IntegrationApplicationIdentity, MachineInspectionHandoffRequest?> _requestFactory;
    private readonly Func<string, IntegrationApplicationIdentity, bool> _canBuildRequest;
    private readonly Func<string?> _projectIdProvider;
    private readonly Func<MachineIntegrationRequestContext>? _requestContextProvider;
    private string? _requestContextKey;
    private readonly Func<Func<Task>, Task> _invokeOnUiThreadAsync;
    private readonly Action<Exception>? _handleException;
    private readonly MachineIntegrationParticipant _integrationParticipant;
    private readonly MachineIntegrationTcpControlViewModel _tcpControl;
    private readonly MachineIntegrationResultObservationWorkflow _resultObservation;
    private readonly MachineIntegrationSimulationWorkflow? _simulationWorkflow;
    private readonly Func<Task>? _abortAutomaticExternalInspectionAsync;
    private string _statusText = string.Empty;
    private readonly object _lifetimeGate = new();
    private bool _disposed;
    private IReadOnlyList<MachineIntegrationDiagnosticFilterItem> _transactionDiagnosticFilters = [];
    private MachineIntegrationTransactionState? _selectedTransactionDiagnosticState;
    private IReadOnlyList<MachineIntegrationTransactionHistoryFilterItem> _transactionHistoryFilters = [];
    private MachineIntegrationTransactionHistoryState? _selectedTransactionHistoryState;
    private bool _automaticExternalRunActive;
    private Guid? _automaticExternalTransactionId;

    // Main supplies facts; this owner prepares requests and tracks its own refresh identity.
    internal MachineIntegrationViewModel(
        Func<MachineIntegrationRequestContext> requestContextProvider,
        Func<IntegrationApplicationIdentity> producerIdentityProvider,
        Func<string?> projectIdProvider,
        string? settingsPath = null,
        Func<Func<Task>, Task>? invokeOnUiThreadAsync = null,
        Action<Exception>? handleException = null,
        Func<SimulationSnapshot>? simulationSnapshotProvider = null,
        Func<SimulationCommand, Task<SimulationCommandResult>>? dispatchSimulationCommandAsync = null,
        Func<Task>? abortAutomaticExternalInspectionAsync = null)
        : this(
            new MachineIntegrationRequestWorkflow(),
            requestContextProvider,
            producerIdentityProvider,
            projectIdProvider,
            settingsPath,
            invokeOnUiThreadAsync,
            handleException,
            simulationSnapshotProvider,
            dispatchSimulationCommandAsync,
            abortAutomaticExternalInspectionAsync)
    {
    }

    private MachineIntegrationViewModel(
        MachineIntegrationRequestWorkflow workflow,
        Func<MachineIntegrationRequestContext> requestContextProvider,
        Func<IntegrationApplicationIdentity> producerIdentityProvider,
        Func<string?> projectIdProvider,
        string? settingsPath,
        Func<Func<Task>, Task>? invokeOnUiThreadAsync,
        Action<Exception>? handleException,
        Func<SimulationSnapshot>? simulationSnapshotProvider,
        Func<SimulationCommand, Task<SimulationCommandResult>>? dispatchSimulationCommandAsync,
        Func<Task>? abortAutomaticExternalInspectionAsync)
        : this(
            (path, consumer) => workflow.TryCreate(requestContextProvider(), path, producerIdentityProvider(), consumer),
            (path, consumer) => workflow.TryCreate(requestContextProvider(), path, producerIdentityProvider(), consumer) is not null,
            projectIdProvider,
            settingsPath,
            MachineIntegrationFileDialogHost.SelectExchangeRoot,
            MachineIntegrationFileDialogHost.SelectInspectionRecipe,
            invokeOnUiThreadAsync,
            handleException,
            simulationSnapshotProvider,
            dispatchSimulationCommandAsync,
            abortAutomaticExternalInspectionAsync,
            MachineIntegrationFileDialogHost.SelectHeightMapSource)
    {
        _requestContextProvider = requestContextProvider;
    }

    // Compatibility entry point for callers that supply their own request callbacks.
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
        Func<string, string?> selectInspectionRecipe,
        Func<Func<Task>, Task>? invokeOnUiThreadAsync = null,
        Action<Exception>? handleException = null,
        Func<SimulationSnapshot>? simulationSnapshotProvider = null,
        Func<SimulationCommand, Task<SimulationCommandResult>>? dispatchSimulationCommandAsync = null,
        Func<Task>? abortAutomaticExternalInspectionAsync = null,
        Func<string, string?>? selectHeightMapSource = null)
    {
        _requestFactory = requestFactory ?? throw new ArgumentNullException(nameof(requestFactory));
        _canBuildRequest = canBuildRequest ?? throw new ArgumentNullException(nameof(canBuildRequest));
        _projectIdProvider = projectIdProvider ?? throw new ArgumentNullException(nameof(projectIdProvider));
        ArgumentNullException.ThrowIfNull(selectExchangeRoot);
        ArgumentNullException.ThrowIfNull(selectInspectionRecipe);
        _invokeOnUiThreadAsync = invokeOnUiThreadAsync ?? ExecuteImmediatelyAsync;
        _handleException = handleException;
        _abortAutomaticExternalInspectionAsync = abortAutomaticExternalInspectionAsync;
        if ((simulationSnapshotProvider is null) != (dispatchSimulationCommandAsync is null))
        {
            throw new ArgumentException(
                "Simulation snapshot and command callbacks must be supplied together.");
        }
        if (simulationSnapshotProvider is not null && dispatchSimulationCommandAsync is not null)
        {
            _simulationWorkflow = new(simulationSnapshotProvider, dispatchSimulationCommandAsync);
        }
        _integrationParticipant = new();
        _integrationParticipant.StateChanged += OnIntegrationParticipantStateChanged;

        MachineIntegrationTcpControlViewModel? tcpControl = null;
        var pathReadiness = new MachineIntegrationPathReadinessPolicy();
        Setup = new(
            settingsPath,
            pathReadiness,
            selectExchangeRoot,
            selectInspectionRecipe,
            () => !IsDisposed && (tcpControl?.CanEditTcpSetup ?? true),
            status => TryPublish(() => StatusText = status),
            () => tcpControl?.SetSessionSharedKey(null),
            selectHeightMapSource);
        _resultObservation = new(
            () => Setup.ExchangeRoot,
            _projectIdProvider,
            () => CanRefreshResults,
            () => IsBusy,
            RefreshResultsAsync,
            _invokeOnUiThreadAsync,
            ReportOperationException,
            pathReadiness);
        _tcpControl = new(
            Setup.RequireSavedTcpSettings,
            () => _resultObservation.LatestTransaction,
            RefreshResultsAsync,
            status => TryPublish(() => StatusText = status),
            _invokeOnUiThreadAsync,
            _handleException);
        tcpControl = _tcpControl;
        Setup.PropertyChanged += OnSetupPropertyChanged;
        Setup.ResetCompleted += OnSetupResetCompleted;
        _tcpControl.PropertyChanged += OnTcpControlPropertyChanged;
        RebuildTransactionDiagnosticFilters();
        RebuildTransactionHistoryFilters();

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
        ApplyResultToSimulationCommand = new AsyncRelayCommand(
            _ => ApplyResultToSimulationAsync(),
            _ => CanApplyResultToSimulation,
            HandleCommandException,
            useCommandManagerRequery: false);
        SetSessionSharedKeyCommand = new RelayCommand(
            parameter => SetSessionSharedKey(parameter as string),
            _ => !IsDisposed,
            useCommandManagerRequery: false);
        Setup.RefreshCommandState();
        _resultObservation.ConfigureWatcher();
    }

    public MachineIntegrationSetupViewModel Setup { get; }
    public AsyncRelayCommand PublishTwoDImageHandoffCommand { get; }
    public AsyncRelayCommand RefreshResultsCommand { get; }
    public AsyncRelayCommand ApplyResultToSimulationCommand { get; }
    public RelayCommand SetSessionSharedKeyCommand { get; }
    public RelayCommand StartTcpListenerCommand => _tcpControl.StartTcpListenerCommand;
    public RelayCommand StopTcpListenerCommand => _tcpControl.StopTcpListenerCommand;
    public RelayCommand PingTcpPeerCommand => _tcpControl.PingTcpPeerCommand;
    public RelayCommand PushLatestTransactionCommand => _tcpControl.PushLatestTransactionCommand;
    public RelayCommand PullLatestTransactionCommand => _tcpControl.PullLatestTransactionCommand;
    public RelayCommand CancelLatestTransactionCommand => _tcpControl.CancelLatestTransactionCommand;

    public bool IsBusy
    {
        get => _integrationParticipant.IsBusy;
    }

    public bool IsTcpListening => _tcpControl.IsTcpListening;

    public bool IsTcpBusy => _tcpControl.IsTcpBusy;

    public bool CanEditTcpSetup => _tcpControl.CanEditTcpSetup;

    public string TcpListenerStatusText => _tcpControl.TcpListenerStatusText;

    public string SharedKeyStatusText => _tcpControl.SharedKeyStatusText;

    public bool IsSharedKeyReady => _tcpControl.IsSharedKeyReady;

    public string SetupWorkflowStatusText
    {
        get
        {
            var readiness = Setup.Readiness;
            if (!readiness.IsExchangeRootAvailable)
            {
                return L(
                    "SetupStepExchange",
                    "1/6 교환 폴더를 선택하고 설정을 저장하세요.",
                    "1/6 Choose the exchange folder and save setup.");
            }

            if (Setup.UseThreeDHeightMap)
            {
                if (!Setup.IsThreeDAutomaticSetupReady)
                {
                    return L(
                        "SetupStepThreeD",
                        "2/6 3D HeightMap source, c3d recipe, consumer identity와 Sequence/device binding을 저장하세요.",
                        "2/6 Save the 3D HeightMap source, c3d recipe, consumer identity, and Sequence/device binding.");
                }
            }

            if (!Setup.UseThreeDHeightMap
                && (!readiness.IsInspectionRecipeAvailable || !readiness.IsInspectionRecipeSupportedByTwoD))
            {
                return L(
                    "SetupStepRecipe",
                    "2/6 사용할 2D 검사 레시피를 선택하세요.",
                    "2/6 Choose the 2D inspection recipe to use.");
            }

            if (!Setup.UseThreeDHeightMap && Setup.TwoDConsumerIdentity is null)
            {
                return L(
                    "SetupStepConsumer",
                    "3/6 2D 소비자 버전과 40자리 source commit을 입력하세요.",
                    "3/6 Enter the 2D consumer version and 40-character source commit.");
            }

            if (!HasValidTcpEndpoints())
            {
                return L(
                    "SetupStepTcp",
                    "4/6 TCP 수신·상대 주소와 포트를 확인하세요.",
                    "4/6 Check the TCP listen/peer addresses and ports.");
            }

            if (!IsSharedKeyReady)
            {
                return L(
                    "SetupStepKey",
                    "5/6 세션 공유 키를 입력하거나 허용된 환경 변수를 준비하세요.",
                    "5/6 Enter a session shared key or prepare the approved environment variable.");
            }

            if (Setup.HasUnsavedChanges)
            {
                return L(
                    "SetupStepSave",
                    "6/6 변경 사항을 저장한 뒤 명시적 연결 또는 Handoff를 실행하세요.",
                    "6/6 Save the changes before an explicit connection or Handoff.");
            }

            return L(
                "SetupReady",
                "설정 준비 완료. 수신 시작, 상대 Ping 또는 명시적 Handoff 중 하나를 선택하세요.",
                "Setup ready. Choose Start listening, Ping peer, or an explicit Handoff.");
        }
    }

    public string LastTcpTransferText => _tcpControl.LastTcpTransferText;

    public bool CanPublishTwoDImageHandoff
    {
        get
        {
            if (IsDisposed)
            {
                return false;
            }

            if (Setup.UseThreeDHeightMap)
            {
                return false;
            }

            var consumer = Setup.TwoDConsumerIdentity;
            var paths = Setup.Readiness;
            if (!_integrationParticipant.CanStart
                || consumer is null
                || !paths.CanPublishHandoff)
            {
                return false;
            }

            try
            {
                return _canBuildRequest(paths.InspectionRecipePath, consumer);
            }
            catch (IntegrationContractException)
            {
                // The integration contract is intentionally fail-closed for a dirty or
                // otherwise unqualified local build; command-state binding must not
                // terminate the WPF shell while it evaluates CanExecute.
                return false;
            }
        }
    }

    public bool CanRefreshResults =>
        !IsDisposed
        && _integrationParticipant.CanStart
        && !string.IsNullOrWhiteSpace(_projectIdProvider())
        && Setup.Readiness.CanRefreshResults;

    internal bool CanPrepareAutomaticExternalInspection =>
        !IsDisposed
        && Setup.WaitForExternalResult
        && _integrationParticipant.CanStart
        && Setup.ActiveConsumerIdentity is not null
        && Setup.Readiness.IsExchangeRootAvailable
        && (!Setup.UseThreeDHeightMap
            ? Setup.Readiness.CanPublishHandoff
            : Setup.IsThreeDAutomaticSetupReady);

    public bool CanApplyResultToSimulation =>
        !IsDisposed
        && _integrationParticipant.CanStart
        && _simulationWorkflow?.CanApply(_resultObservation.LatestValidatedResult) == true;

    public bool CanPushLatestTransaction => _tcpControl.CanPushLatestTransaction;

    public bool CanPullLatestTransaction => _tcpControl.CanPullLatestTransaction;

    public bool CanCancelLatestTransaction => _tcpControl.CanCancelLatestTransaction;

    public IReadOnlyList<MachineIntegrationTransactionSummary> TransactionHistory =>
        _resultObservation.CurrentTransactions;

    public IReadOnlyList<MachineIntegrationTransactionHistoryItem> TransactionHistoryRows =>
        _resultObservation.CurrentTransactions
            .Select(CreateTransactionHistoryRow)
            .ToArray();

    public IReadOnlyList<MachineIntegrationTransactionHistoryFilterItem> TransactionHistoryFilters =>
        _transactionHistoryFilters;

    public MachineIntegrationTransactionHistoryFilterItem? SelectedTransactionHistoryFilter
    {
        get => _transactionHistoryFilters.FirstOrDefault(item =>
            item.State == _selectedTransactionHistoryState);
        set
        {
            if (IsDisposed)
            {
                return;
            }

            var state = value?.State;
            if (value is not null && !_transactionHistoryFilters.Any(item => item.State == state))
            {
                return;
            }

            if (_selectedTransactionHistoryState == state)
            {
                return;
            }

            _selectedTransactionHistoryState = state;
            OnPropertyChanged(nameof(SelectedTransactionHistoryFilter));
            RaiseTransactionHistoryProjectionChanged();
        }
    }

    public IReadOnlyList<MachineIntegrationTransactionHistoryItem> VisibleTransactionHistoryRows =>
        _resultObservation.CurrentTransactions
            .Where(transaction => !_selectedTransactionHistoryState.HasValue
                || GetTransactionHistoryState(transaction) == _selectedTransactionHistoryState.Value)
            .Select(CreateTransactionHistoryRow)
            .ToArray();

    public bool HasVisibleTransactionHistoryRows => VisibleTransactionHistoryRows.Count > 0;

    public bool HasTransactionHistoryEmptyState => !HasVisibleTransactionHistoryRows;

    public string TransactionHistoryEmptyText => _resultObservation.CurrentTransactions.Count == 0
        ? L(
            "NoTransactionHistory",
            "현재 프로젝트에 거래 이력이 없습니다.",
            "No transaction history is available for the current project.")
        : L(
            "NoMatchingTransactionHistory",
            "선택한 상태의 거래 이력이 없습니다.",
            "No transaction history matches the selected state.");

    public IReadOnlyList<MachineIntegrationTransactionDiagnostic> TransactionDiagnostics =>
        _resultObservation.TransactionDiagnostics;

    public IReadOnlyList<MachineIntegrationDiagnosticFilterItem> TransactionDiagnosticFilters =>
        _transactionDiagnosticFilters;

    public MachineIntegrationDiagnosticFilterItem? SelectedTransactionDiagnosticFilter
    {
        get => _transactionDiagnosticFilters.FirstOrDefault(item =>
            item.State == _selectedTransactionDiagnosticState);
        set
        {
            if (IsDisposed)
            {
                return;
            }

            var state = value?.State;
            if (value is not null && !_transactionDiagnosticFilters.Any(item => item.State == state))
            {
                return;
            }

            if (_selectedTransactionDiagnosticState == state)
            {
                return;
            }

            _selectedTransactionDiagnosticState = state;
            OnPropertyChanged(nameof(SelectedTransactionDiagnosticFilter));
            RaiseDiagnosticProjectionChanged();
        }
    }

    public IReadOnlyList<MachineIntegrationTransactionDiagnostic> VisibleTransactionDiagnostics =>
        _resultObservation.TransactionDiagnostics
            .Where(diagnostic => !_selectedTransactionDiagnosticState.HasValue
                || diagnostic.State == _selectedTransactionDiagnosticState.Value)
            .ToArray();

    public bool HasVisibleTransactionDiagnostics => VisibleTransactionDiagnostics.Count > 0;

    public bool HasTransactionDiagnosticsEmptyState =>
        _resultObservation.DiagnosticReadError is null && !HasVisibleTransactionDiagnostics;

    public string TransactionDiagnosticsEmptyText => _resultObservation.TransactionDiagnostics.Count == 0
        ? L(
            "NoTransactionDiagnostics",
            "현재 교환 폴더에 진단 정보가 없습니다.",
            "No transaction diagnostics are available in the exchange folder.")
        : L(
            "NoMatchingTransactionDiagnostics",
            "선택한 진단 상태의 거래가 없습니다.",
            "No transactions match the selected diagnostic state.");

    public string? TransactionDiagnosticReadError => _resultObservation.DiagnosticReadError;

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

    /// <summary>
    /// States which decision source the MMI is configured to use. This is
    /// intentionally independent from the last observed Result so a stale
    /// result cannot make a new external-inspection cycle look like local
    /// simulation.
    /// </summary>
    public string InspectionExecutionModeText => Setup.WaitForExternalResult
        ? L(
            "MmiModeExternal",
            "외부 검사 운전: Result를 받을 때까지 시퀀스를 완료하지 않습니다.",
            "External inspection: the sequence does not complete until a Result is received.")
        : L(
            "MmiModeSimulation",
            "모의 판정 운전: 외부 검사 없이 로컬 시뮬레이션 결과로 진행합니다.",
            "Simulation decision: continue with the local simulation result without external inspection.");

    /// <summary>
    /// Resolves only image artifacts that were published by the external
    /// consumer transaction. Machine Studio does not manufacture a preview for
    /// an integration screen; the 2D/3D project owns the bytes that appear here.
    /// </summary>
    public System.Uri? LatestTwoDImageUri => ResolveLatestConsumerImageUri(IntegrationInspectionModality.TwoD);

    public System.Uri? LatestThreeDImageUri => ResolveLatestConsumerImageUri(IntegrationInspectionModality.ThreeD);

    public bool HasLatestTwoDImage => LatestTwoDImageUri is not null;

    public bool HasLatestThreeDImage => LatestThreeDImageUri is not null;

    public string LatestTwoDImageSourceText => DescribeLatestConsumerImage(
        IntegrationInspectionModality.TwoD,
        "2D 이미지 아티팩트가 아직 없습니다.",
        "No 2D image artifact is available yet.");

    public string LatestThreeDImageSourceText => DescribeLatestConsumerImage(
        IntegrationInspectionModality.ThreeD,
        "3D Studio가 게시한 이미지 미리보기가 아직 없습니다. HeightMap/C3D 거래만 있으면 원본 경로와 해시를 표시합니다.",
        "No image preview has been published by 3D Studio yet. When only a HeightMap/C3D transaction exists, its source path and hash are shown.");

    public string LatestThreeDImageStatusText => LatestThreeDImageUri is not null
        ? L("ThreeDImageReady", "3D Studio 이미지 미리보기 수신됨", "3D Studio image preview received")
        : FindLatestConsumerTransaction(IntegrationInspectionModality.ThreeD) is { } transaction
            ? string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                L(
                    "ThreeDHeightMapReady",
                    "3D Studio HeightMap/C3D 수신됨 · 이미지 미리보기 게시 대기 · {0}",
                    "3D Studio HeightMap/C3D received · waiting for an image preview artifact · {0}"),
                transaction.Handoff.Context.InputSha256[..Math.Min(12, transaction.Handoff.Context.InputSha256.Length)])
            : L("ThreeDImageWaiting", "3D Studio 이미지 대기", "Waiting for a 3D Studio image");

    public string LatestTwoDResultStatusText => DescribeLatestConsumerResultStatus(
        IntegrationInspectionModality.TwoD,
        "2D 검사 결과 대기",
        "Waiting for a 2D inspection result");

    public string LatestThreeDResultStatusText => DescribeLatestConsumerResultStatus(
        IntegrationInspectionModality.ThreeD,
        "3D 검사 결과 대기",
        "Waiting for a 3D inspection result");

    public string LatestTwoDImageStatusText => LatestTwoDImageUri is not null
        ? L("TwoDImageReady", "2D Studio 이미지 수신됨", "2D Studio image received")
        : L("TwoDImageWaiting", "2D Studio 이미지 대기", "Waiting for a 2D Studio image");

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

    internal string? LatestRunId => _resultObservation.LatestResult?.RunId;

    internal string? ResultReadError => _resultObservation.ResultReadError;

    internal string? AcknowledgementReadError => _resultObservation.AcknowledgementReadError;

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    internal void RefreshSourceContext()
    {
        if (IsDisposed || _requestContextProvider is not { } getContext)
        {
            return;
        }

        var contextKey = MachineIntegrationRequestWorkflow.CreateRefreshKey(getContext());
        if (string.Equals(_requestContextKey, contextKey, StringComparison.Ordinal))
        {
            return;
        }

        _requestContextKey = contextKey;
        _simulationWorkflow?.RefreshRuntimeState();
        var keepClosedTransaction = _simulationWorkflow?.HasClosedPublishedContext == true
            && _automaticExternalTransactionId == _simulationWorkflow.PublishedTransactionId;
        _automaticExternalRunActive = false;
        if (!keepClosedTransaction)
        {
            _automaticExternalTransactionId = null;
        }
        RefreshContext();
    }

    public void RefreshContext()
    {
        if (_resultObservation.RefreshContext())
        {
            _simulationWorkflow?.Clear();
            _automaticExternalRunActive = false;
            _automaticExternalTransactionId = null;
            ResetTransactionDiagnosticFilter();
            ResetTransactionHistoryFilter();
            RaiseProjectionChanged();
        }

        RefreshCommandState();
    }

    internal void RefreshRuntimeCommandState()
    {
        if (IsDisposed)
        {
            return;
        }

        RefreshSimulationIntegrationContext();
        RefreshCommandState();
    }

    public void RefreshLocalization()
    {
        if (IsDisposed)
        {
            return;
        }

        Setup.RefreshLocalization();
        _tcpControl.RefreshLocalization();
        RebuildTransactionDiagnosticFilters();
        RebuildTransactionHistoryFilters();
        OnPropertyChanged(nameof(TransactionDiagnosticFilters));
        OnPropertyChanged(nameof(SelectedTransactionDiagnosticFilter));
        OnPropertyChanged(nameof(TransactionHistoryFilters));
        OnPropertyChanged(nameof(SelectedTransactionHistoryFilter));
        RaiseProjectionChanged();
        StatusText = L(
            "Ready",
            "통합 상태를 갱신했습니다. Publish와 Refresh Result는 별도 명령입니다.",
            "Integration text refreshed. Publish and Refresh Result remain separate commands.");
    }

    public void SetSessionSharedKey(string? encodedKey) => TryPublish(() => _tcpControl.SetSessionSharedKey(encodedKey));

    internal void SetSessionCloseAdmission(bool isRequested)
    {
        _integrationParticipant.SetCloseAdmission(isRequested);
        _tcpControl.SetSessionCloseAdmission(isRequested);
        RefreshCommandState();
    }

    internal async Task<MachineIntegrationParticipantResult> ObserveAsync(TimeSpan timeout)
    {
        var fileTask = _integrationParticipant.ObserveAsync(timeout);
        var tcpTask = _tcpControl.ObserveAsync(timeout);
        await Task.WhenAll(fileTask, tcpTask).ConfigureAwait(true);
        var file = await fileTask.ConfigureAwait(true);
        var tcp = await tcpTask.ConfigureAwait(true);
        var outcome = CombineParticipantOutcomes(file.Outcome, tcp.Outcome);
        return new(outcome, file, tcp, file.Exception ?? tcp.Exception);
    }

    internal Task StartTcpListenerAsync() => _tcpControl.StartTcpListenerAsync();

    internal Task StopTcpListenerAsync() => _tcpControl.StopTcpListenerAsync();

    internal Task PingTcpPeerAsync() => _tcpControl.PingTcpPeerAsync();

    internal Task PushLatestTransactionAsync() => _tcpControl.PushLatestTransactionAsync();

    internal Task PullLatestTransactionAsync() => _tcpControl.PullLatestTransactionAsync();

    private Task<MachineIntegrationOperationObservation> PublishTwoDImageHandoffAsync()
    {
        if (!CanPublishTwoDImageHandoff
            || Setup.TwoDConsumerIdentity is not { } consumer
            || !_integrationParticipant.CanStart)
        {
            return Task.FromResult(new MachineIntegrationOperationObservation(
                MachineIntegrationOperationKind.PublishHandoff,
                MachineIntegrationParticipantOutcome.Idle));
        }

        return _integrationParticipant.TrackAsync(
            MachineIntegrationOperationKind.PublishHandoff,
            () => PublishTwoDImageHandoffCoreAsync(consumer, automatic: false));
    }

    internal Task<MachineIntegrationOperationObservation> PublishAutomaticExternalInspectionAsync()
    {
        if (!CanPrepareAutomaticExternalInspection
            || Setup.ActiveConsumerIdentity is not { } consumer
            || !_integrationParticipant.CanStart)
        {
            return Task.FromResult(new MachineIntegrationOperationObservation(
                MachineIntegrationOperationKind.PublishHandoff,
                MachineIntegrationParticipantOutcome.Idle));
        }

        return _integrationParticipant.TrackAsync(
            MachineIntegrationOperationKind.PublishHandoff,
            () => PublishTwoDImageHandoffCoreAsync(consumer, automatic: true));
    }

    private Task<MachineIntegrationOperationObservation> RefreshResultsAsync()
    {
        if (!CanRefreshResults || !_integrationParticipant.CanStart)
        {
            return Task.FromResult(new MachineIntegrationOperationObservation(
                MachineIntegrationOperationKind.RefreshResults,
                MachineIntegrationParticipantOutcome.Idle));
        }

        return _integrationParticipant.TrackAsync(
            MachineIntegrationOperationKind.RefreshResults,
            RefreshResultsCoreAsync);
    }

    private Task<MachineIntegrationOperationObservation> ApplyResultToSimulationAsync()
    {
        if (!CanApplyResultToSimulation
            || _simulationWorkflow is null
            || _resultObservation.LatestValidatedResult is null
            || !_integrationParticipant.CanStart)
        {
            return Task.FromResult(new MachineIntegrationOperationObservation(
                MachineIntegrationOperationKind.ApplyResultToSimulation,
                MachineIntegrationParticipantOutcome.Idle));
        }

        return _integrationParticipant.TrackAsync(
            MachineIntegrationOperationKind.ApplyResultToSimulation,
            () => ApplyResultToSimulationCoreAsync(_resultObservation.LatestValidatedResult));
    }

    private async Task<MachineIntegrationOperationObservation> PublishTwoDImageHandoffCoreAsync(
        IntegrationApplicationIdentity consumer,
        bool automatic)
    {
        try
        {
            var publishRuntimeContext = _simulationWorkflow?.CapturePublishContext();
            var request = _requestFactory(Setup.NormalizePath(Setup.ActiveInspectionRecipePath), consumer)
                ?? throw new InvalidOperationException(
                    L(
                        "ContextNotReady",
                        "저장된 프로젝트에서 완료된 가상 카메라 프레임을 먼저 준비하세요.",
                        "Prepare a completed virtual-camera frame from a saved project first."));
            var handoff = await MachineIntegrationHandoffPublisher.PublishAsync(
                    Setup.NormalizePath(Setup.ExchangeRoot),
                    request)
                .ConfigureAwait(true);
            var simulationBound = _simulationWorkflow?.TryRecordPublishedHandoff(
                handoff,
                publishRuntimeContext)
                ?? true;
            if (automatic && !simulationBound)
            {
                throw new InvalidOperationException(
                    "The automatic external Handoff no longer matches the pending simulation camera.");
            }

            TryPublish(() =>
            {
                _resultObservation.RecordPublishedHandoff(handoff);
                _automaticExternalRunActive = automatic;
                _automaticExternalTransactionId = automatic ? handoff.TransactionId : null;
                RaiseProjectionChanged();
                StatusText = string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    Setup.UseThreeDHeightMap
                        ? L("HeightMapHandoffPublished", "3D/HeightMap Handoff 게시 완료: {0:D}", "3D/HeightMap Handoff published: {0:D}")
                        : L("HandoffPublished", "2D/Image Handoff 게시 완료: {0:D}", "2D/Image Handoff published: {0:D}"),
                    handoff.TransactionId);
            });
            // When the producer listener is active, forward an automatic
            // Handoff through the configured TCP peer as part of the same
            // request-ready boundary. Shared-folder deployments keep the
            // existing file-only path when no listener is running.
            if (automatic && _tcpControl.IsTcpListening && _tcpControl.CanPushLatestTransaction)
            {
                await _tcpControl.PushLatestTransactionAsync().ConfigureAwait(true);
            }
            return new(
                MachineIntegrationOperationKind.PublishHandoff,
                MachineIntegrationParticipantOutcome.Completed);
        }
        catch (Exception exception)
        {
            ReportOperationException(exception);
            return new(
                MachineIntegrationOperationKind.PublishHandoff,
                MachineIntegrationParticipantOutcome.Failed,
                exception);
        }
    }

    private async Task<MachineIntegrationOperationObservation> ApplyResultToSimulationCoreAsync(
        MachineIntegrationValidatedResult validatedResult)
    {
        try
        {
            var result = await _simulationWorkflow!.ApplyAsync(validatedResult).ConfigureAwait(true);
            if (!result.IsAccepted)
            {
                var exception = new InvalidOperationException(result.Detail);
                ReportOperationException(exception);
                return new(
                    MachineIntegrationOperationKind.ApplyResultToSimulation,
                    MachineIntegrationParticipantOutcome.Failed,
                    exception);
            }

            TryPublish(() =>
            {
                _simulationWorkflow.RefreshRuntimeState();
                _automaticExternalRunActive = false;
                _automaticExternalTransactionId = null;
                RaiseProjectionChanged();
                StatusText = string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    L(
                        "ResultAppliedToSimulation",
                        "외부 Result를 시뮬레이션에 적용했습니다: {0:D}",
                        "External Result applied to Simulation: {0:D}"),
                    validatedResult.Result.MessageId);
            });
            return new(
                MachineIntegrationOperationKind.ApplyResultToSimulation,
                MachineIntegrationParticipantOutcome.Completed);
        }
        catch (Exception exception)
        {
            ReportOperationException(exception);
            return new(
                MachineIntegrationOperationKind.ApplyResultToSimulation,
                MachineIntegrationParticipantOutcome.Failed,
                exception);
        }
    }

    private async Task<MachineIntegrationOperationObservation> RefreshResultsCoreAsync()
    {
        try
        {
            var transactionCount = await _resultObservation.RefreshAsync().ConfigureAwait(true);
            if (transactionCount is null)
            {
                return new(
                    MachineIntegrationOperationKind.RefreshResults,
                    MachineIntegrationParticipantOutcome.Completed);
            }

            TryPublish(() =>
            {
                RaiseProjectionChanged();
                StatusText = string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    L("ResultsRefreshed", "현재 프로젝트 거래 {0}개를 확인했습니다. 실행은 하지 않았습니다.", "Found {0} current-project transaction(s). No inspection was run."),
                    transactionCount.Value);
            });

            RefreshSimulationIntegrationContext();

            if (_automaticExternalRunActive
                && IsAutomaticAcknowledgementRejected())
            {
                _automaticExternalRunActive = false;
                _automaticExternalTransactionId = null;
                if (_abortAutomaticExternalInspectionAsync is not null)
                {
                    await _abortAutomaticExternalInspectionAsync().ConfigureAwait(true);
                }
            }
            else if (_automaticExternalTransactionId is { } automaticTransactionId
                && _resultObservation.ValidatedResultsByTransaction.TryGetValue(
                    automaticTransactionId,
                    out var automaticResult))
            {
                if (_automaticExternalRunActive
                    && _simulationWorkflow?.CanApply(automaticResult) == true)
                {
                    await ApplyResultToSimulationCoreAsync(automaticResult).ConfigureAwait(true);
                }
                else if (_simulationWorkflow?.CanQuarantineLateResult(automaticResult) == true)
                {
                    var quarantine = await _simulationWorkflow.QuarantineLateResultAsync(automaticResult)
                        .ConfigureAwait(true);
                    if (!quarantine.IsAccepted
                        && quarantine.ErrorCode == SimulationCommandErrorCode.ExternalInspectionNotPending)
                    {
                        _automaticExternalRunActive = false;
                        _automaticExternalTransactionId = null;
                        TryPublish(() =>
                        {
                            RaiseProjectionChanged();
                            StatusText = string.Format(
                                System.Globalization.CultureInfo.CurrentCulture,
                                L(
                                    "LateResultQuarantined",
                                    "지연된 외부 Result를 격리했습니다: {0:D}",
                                    "Late external Result was quarantined: {0:D}"),
                                automaticResult.Result.MessageId);
                        });
                    }
                    else if (!quarantine.IsAccepted)
                    {
                        ReportOperationException(new InvalidOperationException(
                            quarantine.Detail ?? "The late external Result could not be quarantined."));
                    }
                }
            }

            return new(
                MachineIntegrationOperationKind.RefreshResults,
                MachineIntegrationParticipantOutcome.Completed);
        }
        catch (Exception exception)
        {
            ReportOperationException(exception);
            return new(
                MachineIntegrationOperationKind.RefreshResults,
                MachineIntegrationParticipantOutcome.Failed,
                exception);
        }
    }

    private bool IsAutomaticAcknowledgementRejected() =>
        _automaticExternalTransactionId is { } transactionId
        && _resultObservation.LatestAcknowledgement?.TransactionId == transactionId
        && _resultObservation.LatestAcknowledgement.Status == IntegrationAcknowledgementStatus.Rejected;

    private MachineIntegrationTransactionHistoryItem CreateTransactionHistoryRow(
        MachineIntegrationTransactionSummary transaction) =>
        new(transaction.Handoff, GetTransactionState(transaction));

    private MachineIntegrationTransactionHistoryState GetTransactionHistoryState(
        MachineIntegrationTransactionSummary transaction)
    {
        if (transaction.HasResult)
        {
            var isLatestResult = _resultObservation.LatestResultTransaction?.Handoff.TransactionId
                == transaction.Handoff.TransactionId;
            if (isLatestResult
                && _resultObservation.LatestResult is null
                && _resultObservation.ResultReadError is not null)
            {
                return MachineIntegrationTransactionHistoryState.ResultReadFailed;
            }

            return MachineIntegrationTransactionHistoryState.ResultPublished;
        }

        return _resultObservation.LatestAcknowledgement?.TransactionId == transaction.Handoff.TransactionId
            ? _resultObservation.LatestAcknowledgement.Status == IntegrationAcknowledgementStatus.Rejected
                ? MachineIntegrationTransactionHistoryState.Rejected
                : MachineIntegrationTransactionHistoryState.Reviewed
            : transaction.HasAcknowledgement
                ? MachineIntegrationTransactionHistoryState.Reviewed
                : MachineIntegrationTransactionHistoryState.PendingReview;
    }

    private string GetTransactionState(MachineIntegrationTransactionSummary transaction) =>
        GetTransactionHistoryState(transaction) switch
        {
            MachineIntegrationTransactionHistoryState.ResultReadFailed =>
                L("ResultReadFailedState", "결과 읽기 실패", "Result read failed"),
            MachineIntegrationTransactionHistoryState.ResultPublished =>
                L("ResultPublished", "결과 게시됨", "Result published"),
            MachineIntegrationTransactionHistoryState.Rejected =>
                L("Rejected", "거절됨", "Rejected"),
            MachineIntegrationTransactionHistoryState.Reviewed =>
                L("Reviewed", "검토됨", "Reviewed"),
            _ => L("PendingReview", "검토 대기", "Pending review")
        };

    private void RefreshCommandState()
    {
        OnPropertyChanged(nameof(CanPublishTwoDImageHandoff));
        OnPropertyChanged(nameof(CanRefreshResults));
        OnPropertyChanged(nameof(CanApplyResultToSimulation));
        PublishTwoDImageHandoffCommand.RaiseCanExecuteChanged();
        RefreshResultsCommand.RaiseCanExecuteChanged();
        ApplyResultToSimulationCommand.RaiseCanExecuteChanged();
        Setup.RefreshCommandState();
        _tcpControl.RefreshCommandState();
    }

    private void RefreshSimulationIntegrationContext()
    {
        if (_simulationWorkflow?.RefreshRuntimeState() != true)
        {
            return;
        }

        var keepClosedTransaction = _simulationWorkflow.HasClosedPublishedContext
            && _automaticExternalTransactionId == _simulationWorkflow.PublishedTransactionId;
        _automaticExternalRunActive = false;
        if (!keepClosedTransaction)
        {
            _automaticExternalTransactionId = null;
        }

        RaiseProjectionChanged();
    }

    private void RaiseProjectionChanged()
    {
        OnPropertyChanged(nameof(HandoffStatusText));
        OnPropertyChanged(nameof(AcknowledgementStatusText));
        OnPropertyChanged(nameof(ResultStatusText));
        OnPropertyChanged(nameof(InspectionExecutionModeText));
        OnPropertyChanged(nameof(LatestTwoDImageUri));
        OnPropertyChanged(nameof(LatestThreeDImageUri));
        OnPropertyChanged(nameof(HasLatestTwoDImage));
        OnPropertyChanged(nameof(HasLatestThreeDImage));
        OnPropertyChanged(nameof(LatestTwoDImageSourceText));
        OnPropertyChanged(nameof(LatestThreeDImageSourceText));
        OnPropertyChanged(nameof(LatestTwoDImageStatusText));
        OnPropertyChanged(nameof(LatestThreeDImageStatusText));
        OnPropertyChanged(nameof(LatestTwoDResultStatusText));
        OnPropertyChanged(nameof(LatestThreeDResultStatusText));
        OnPropertyChanged(nameof(CanApplyResultToSimulation));
        ApplyResultToSimulationCommand.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(ProjectionStatusText));
        OnPropertyChanged(nameof(LastTransactionText));
        OnPropertyChanged(nameof(TransactionHistory));
        OnPropertyChanged(nameof(TransactionHistoryRows));
        RaiseTransactionHistoryProjectionChanged();
        OnPropertyChanged(nameof(TransactionDiagnostics));
        OnPropertyChanged(nameof(TransactionDiagnosticReadError));
        RaiseDiagnosticProjectionChanged();
    }

    private void RaiseDiagnosticProjectionChanged()
    {
        OnPropertyChanged(nameof(VisibleTransactionDiagnostics));
        OnPropertyChanged(nameof(HasVisibleTransactionDiagnostics));
        OnPropertyChanged(nameof(HasTransactionDiagnosticsEmptyState));
        OnPropertyChanged(nameof(TransactionDiagnosticsEmptyText));
    }

    private void RaiseTransactionHistoryProjectionChanged()
    {
        OnPropertyChanged(nameof(VisibleTransactionHistoryRows));
        OnPropertyChanged(nameof(HasVisibleTransactionHistoryRows));
        OnPropertyChanged(nameof(HasTransactionHistoryEmptyState));
        OnPropertyChanged(nameof(TransactionHistoryEmptyText));
    }

    private void ResetTransactionDiagnosticFilter()
    {
        if (_selectedTransactionDiagnosticState is null)
        {
            return;
        }

        _selectedTransactionDiagnosticState = null;
        OnPropertyChanged(nameof(SelectedTransactionDiagnosticFilter));
    }

    private void ResetTransactionHistoryFilter()
    {
        if (_selectedTransactionHistoryState is null)
        {
            return;
        }

        _selectedTransactionHistoryState = null;
        OnPropertyChanged(nameof(SelectedTransactionHistoryFilter));
    }

    private void RebuildTransactionDiagnosticFilters()
    {
        _transactionDiagnosticFilters =
        [
            new(null, L("TransactionDiagnosticsFilterAll", "전체 상태", "All states")),
            new(MachineIntegrationTransactionState.Published, L("TransactionDiagnosticsFilterPublished", "게시됨", "Published")),
            new(MachineIntegrationTransactionState.Staging, L("TransactionDiagnosticsFilterStaging", "작성 중", "Staging")),
            new(MachineIntegrationTransactionState.Quarantined, L("TransactionDiagnosticsFilterQuarantined", "격리됨", "Quarantined")),
            new(MachineIntegrationTransactionState.Invalid, L("TransactionDiagnosticsFilterInvalid", "잘못됨", "Invalid"))
        ];
    }

    private void RebuildTransactionHistoryFilters()
    {
        _transactionHistoryFilters =
        [
            new(null, L("TransactionHistoryFilterAll", "전체 상태", "All states")),
            new(MachineIntegrationTransactionHistoryState.PendingReview,
                L("TransactionHistoryFilterPendingReview", "검토 대기", "Pending review")),
            new(MachineIntegrationTransactionHistoryState.Reviewed,
                L("TransactionHistoryFilterReviewed", "검토됨", "Reviewed")),
            new(MachineIntegrationTransactionHistoryState.Rejected,
                L("TransactionHistoryFilterRejected", "거절됨", "Rejected")),
            new(MachineIntegrationTransactionHistoryState.ResultPublished,
                L("TransactionHistoryFilterPublished", "결과 게시됨", "Result published")),
            new(MachineIntegrationTransactionHistoryState.ResultReadFailed,
                L("TransactionHistoryFilterResultReadFailed", "결과 읽기 실패", "Result read failed"))
        ];
    }

    private void OnTcpControlPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        TryPublish(() =>
        {
            if (args.PropertyName is { } propertyName)
            {
                OnPropertyChanged(propertyName);
            }

            OnPropertyChanged(nameof(SetupWorkflowStatusText));
        });
    }

    private void OnSetupPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(MachineIntegrationSetupViewModel.ExchangeRoot))
        {
            _resultObservation.ConfigureWatcher();
        }

        TryPublish(() =>
        {
            RefreshCommandState();
            OnPropertyChanged(nameof(SetupWorkflowStatusText));
            OnPropertyChanged(nameof(InspectionExecutionModeText));
        });
    }

    private bool HasValidTcpEndpoints() =>
        IPAddress.TryParse(Setup.TcpListenAddress, out _)
        && TryParsePort(Setup.TcpListenPortText)
        && !string.IsNullOrWhiteSpace(Setup.TcpPeerHost)
        && TryParsePort(Setup.TcpPeerPortText);

    private static bool TryParsePort(string value) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var port)
        && port is >= 1 and <= IPEndPoint.MaxPort;

    private void OnSetupResetCompleted(object? sender, EventArgs args)
    {
        TryPublish(() =>
        {
            _resultObservation.Reset();
            _simulationWorkflow?.Clear();
            _automaticExternalRunActive = false;
            _automaticExternalTransactionId = null;
            ResetTransactionDiagnosticFilter();
            ResetTransactionHistoryFilter();
            RaiseProjectionChanged();
        });
    }

    private void OnIntegrationParticipantStateChanged(object? sender, EventArgs args)
    {
        TryPublish(() =>
        {
            OnPropertyChanged(nameof(IsBusy));
            RefreshCommandState();
        });
    }

    private static MachineIntegrationParticipantOutcome CombineParticipantOutcomes(
        MachineIntegrationParticipantOutcome fileOutcome,
        MachineIntegrationParticipantOutcome tcpOutcome) =>
        fileOutcome == MachineIntegrationParticipantOutcome.TimedOut
            || tcpOutcome == MachineIntegrationParticipantOutcome.TimedOut
            ? MachineIntegrationParticipantOutcome.TimedOut
            : fileOutcome == MachineIntegrationParticipantOutcome.Failed
                || tcpOutcome == MachineIntegrationParticipantOutcome.Failed
                ? MachineIntegrationParticipantOutcome.Failed
                : fileOutcome == MachineIntegrationParticipantOutcome.Cancelled
                    || tcpOutcome == MachineIntegrationParticipantOutcome.Cancelled
                    ? MachineIntegrationParticipantOutcome.Cancelled
                    : fileOutcome == MachineIntegrationParticipantOutcome.Completed
                        || tcpOutcome == MachineIntegrationParticipantOutcome.Completed
                        ? MachineIntegrationParticipantOutcome.Completed
                        : MachineIntegrationParticipantOutcome.Idle;

    private static Task ExecuteImmediatelyAsync(Func<Task> operation) => operation();

    private void HandleCommandException(Exception exception) => ReportOperationException(exception);

    private void ReportOperationException(Exception exception)
    {
        TryPublish(() =>
        {
            StatusText = exception.Message;
            _handleException?.Invoke(exception);
        });
    }

    private static string L(string key, string korean, string english) =>
        OpenVisionLanguageService.CurrentLanguage == OpenVisionLanguage.English
            ? english
            : korean;

    private System.Uri? ResolveLatestConsumerImageUri(IntegrationInspectionModality modality)
    {
        var image = FindLatestConsumerImageArtifact(modality);
        if (image is null || string.IsNullOrWhiteSpace(Setup.ExchangeRoot))
        {
            return null;
        }

        try
        {
            var transactionRoot = Path.Combine(
                Path.GetFullPath(Setup.ExchangeRoot),
                IntegrationTransactionLayout.TransactionsDirectoryName,
                image.Value.TransactionId.ToString("D"));
            var candidatePath = Path.GetFullPath(Path.Combine(
                transactionRoot,
                image.Value.Artifact.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
            var rootPrefix = transactionRoot.TrimEnd(Path.DirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            if (!candidatePath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase)
                || !File.Exists(candidatePath))
            {
                return null;
            }

            return new System.Uri(candidatePath, System.UriKind.Absolute);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private string DescribeLatestConsumerImage(
        IntegrationInspectionModality modality,
        string emptyText,
        string emptyTextEnglish)
    {
        var transaction = FindLatestConsumerTransaction(modality);
        var image = FindLatestConsumerImageArtifact(modality);
        if (transaction is null)
        {
            return L($"{modality}ImageWaiting", emptyText, emptyTextEnglish);
        }

        var artifact = image?.Artifact
            ?? transaction.Handoff.Context.Artifacts.FirstOrDefault(candidate =>
                string.Equals(candidate.Role, IntegrationArtifactRoles.InspectionSource, StringComparison.Ordinal));
        if (artifact is null)
        {
            return L($"{modality}ImageWaiting", emptyText, emptyTextEnglish);
        }

        return string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            L(
                $"{modality}ImageSource",
                $"{modality} Studio 원본 · {artifact.RelativePath} · SHA-256 {artifact.Sha256[..Math.Min(12, artifact.Sha256.Length)]}",
            $"{modality} Studio source · {artifact.RelativePath} · SHA-256 {artifact.Sha256[..Math.Min(12, artifact.Sha256.Length)]}"));
    }

    private string DescribeLatestConsumerResultStatus(
        IntegrationInspectionModality modality,
        string emptyText,
        string emptyTextEnglish)
    {
        var transaction = FindLatestConsumerTransaction(modality);
        if (transaction is null)
        {
            return L($"{modality}ResultWaiting", emptyText, emptyTextEnglish);
        }

        if (!_resultObservation.ValidatedResultsByTransaction.TryGetValue(
            transaction.Handoff.TransactionId,
            out var validatedResult))
        {
            return L(
                $"{modality}ResultPending",
                $"{GetModalityLabel(modality)} 결과 대기 · 아직 검증되지 않음",
                $"{GetModalityLabel(modality)} result pending · not validated yet");
        }

        return string.Format(
            CultureInfo.CurrentCulture,
            L(
                $"{modality}ResultStatus",
                $"{GetModalityLabel(modality)} 결과 · {{0}} · {{1}} · Run {{2}}",
                $"{GetModalityLabel(modality)} result · {{0}} · {{1}} · Run {{2}}"),
            validatedResult.Result.Outcome,
            validatedResult.Result.Status,
            validatedResult.Result.RunId);
    }

    private static string GetModalityLabel(IntegrationInspectionModality modality) =>
        modality == IntegrationInspectionModality.ThreeD ? "3D" : "2D";

    private (Guid TransactionId, IntegrationArtifactReference Artifact)? FindLatestConsumerImageArtifact(
        IntegrationInspectionModality modality)
    {
        var transaction = FindLatestConsumerTransaction(modality);
        if (transaction is null)
        {
            return null;
        }

        if (_resultObservation.ValidatedResultsByTransaction.TryGetValue(
                transaction.Handoff.TransactionId,
                out var validatedResult)
            && validatedResult.Handoff.Context.Modality == modality)
        {
            var resultEvidence = validatedResult.Result.Evidence.FirstOrDefault(candidate =>
                string.Equals(candidate.Role, IntegrationArtifactRoles.ResultEvidence, StringComparison.Ordinal)
                && IsImagePath(candidate.RelativePath));
            if (resultEvidence is not null)
            {
                return (transaction.Handoff.TransactionId, resultEvidence);
            }
        }

        var sourceArtifact = transaction.Handoff.Context.Artifacts.FirstOrDefault(candidate =>
            string.Equals(candidate.Role, IntegrationArtifactRoles.InspectionSource, StringComparison.Ordinal)
            && IsImagePath(candidate.RelativePath));
        return sourceArtifact is null
            ? null
            : (transaction.Handoff.TransactionId, sourceArtifact);
    }

    private MachineIntegrationTransactionSummary? FindLatestConsumerTransaction(
        IntegrationInspectionModality modality) =>
        _resultObservation.CurrentTransactions
            .Where(transaction => transaction.Handoff.Context.Modality == modality)
            .OrderByDescending(transaction => transaction.Handoff.CreatedAtUtc)
            .FirstOrDefault();

    private static bool IsImagePath(string path) =>
        new[] { ".bmp", ".gif", ".jpeg", ".jpg", ".png", ".tif", ".tiff" }
            .Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    private bool IsDisposed
    {
        get
        {
            lock (_lifetimeGate)
            {
                return _disposed;
            }
        }
    }

    private bool TryPublish(Action action)
    {
        lock (_lifetimeGate)
        {
            if (_disposed)
            {
                return false;
            }

            action();
            return true;
        }
    }

    public void Dispose()
    {
        lock (_lifetimeGate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _integrationParticipant.StateChanged -= OnIntegrationParticipantStateChanged;
        _integrationParticipant.Dispose();
        Setup.PropertyChanged -= OnSetupPropertyChanged;
        Setup.ResetCompleted -= OnSetupResetCompleted;
        _tcpControl.PropertyChanged -= OnTcpControlPropertyChanged;
        _resultObservation.Dispose();
        _tcpControl.Dispose();
        Setup.Dispose();
        PublishTwoDImageHandoffCommand.RaiseCanExecuteChanged();
        RefreshResultsCommand.RaiseCanExecuteChanged();
        ApplyResultToSimulationCommand.RaiseCanExecuteChanged();
        SetSessionSharedKeyCommand.RaiseCanExecuteChanged();
    }
}
