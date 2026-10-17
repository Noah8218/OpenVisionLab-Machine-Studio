using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Net;
using OpenVisionLab;
using OpenVisionLab.Integration.Contracts;
using OpenVisionLab.Machine.Infrastructure.Integration;
using OpenVisionLab.Machine.Simulation.Commands;
using OpenVisionLab.Machine.Simulation.Camera;
using OpenVisionLab.Machine.Simulation.Engine;
using OpenVisionLab.Machine.Simulation.Snapshots;
using OpenVisionLab.MachineStudio.View.Dialogs;
using OpenVisionLab.Machine.Core.History;
using System.Text.Json;

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
    public string BottomStatusText { get; init; } = StatusText;
    public string StatusTone { get; init; } = "warn";
    public bool HasAcknowledgement { get; init; }
    public bool HasValidatedResult { get; init; }
    public bool HasReceivedAcknowledgement { get; init; }
    public bool HasReceivedResult { get; init; }
    public bool? IsAutomaticRequest { get; init; }
    public bool IsResultApplied { get; init; }
    public bool IsLateResult { get; init; }
    public string LateResultText { get; init; } = string.Empty;
    public bool HasKnownPurpose => IsAutomaticRequest.HasValue;
    public bool HasApplicationStatus => IsResultApplied || HasKnownPurpose && HasValidatedResult && !IsLateResult;
    public string PurposeText => IsAutomaticRequest switch
    {
        true => L("RequestPurposeBranch", "결과로 분기", "Branch on result"),
        false => L("RequestPurposeManual", "수동 재검사", "Manual reinspection"),
        _ => string.Empty
    };
    public string ApplicationText => !HasApplicationStatus ? string.Empty : IsResultApplied
        ? L("RequestApplied", "현재 운전에 반영됨", "Applied to current run")
        : L("RequestDisplayOnly", "표시만 (미반영)", "Display only (not applied)");
    public bool InspectionCompleted { get; init; }
    public bool HasTerminalError => StatusTone == "bad";
    public bool ShowTransferStage => true;
    public bool ShowAcknowledgementStage => HasReceivedAcknowledgement;
    public bool ShowResultStage => HasReceivedResult;
    public bool IsPending { get; init; }
    public string ErrorText { get; init; } = string.Empty;
    public string MetricsText { get; init; } = string.Empty;
    public string HistoryText { get; init; } = string.Empty;
    public string ConsumerText => Handoff.Context.ConsumerBuild.ApplicationId switch
    {
        IntegrationApplicationIds.TwoDStudio => "2D Studio",
        IntegrationApplicationIds.ThreeDStudio => "3D Studio",
        _ => Handoff.Context.ConsumerBuild.ApplicationId
    };
    public string ContextText => $"{Handoff.Context.ProjectId} · {Handoff.Context.SequenceId} · {Handoff.Context.CameraId} · {Handoff.Context.StepId} · {L("RequestImage", "이미지", "Image")} {Handoff.Context.AcquisitionId} · {Handoff.Context.FrameId} · {Handoff.Context.Unit}";
    public string PublishedCameraName { get; init; } = string.Empty;
    public string PublishedStepName { get; init; } = string.Empty;
    public bool HasPublishedSourceNames => !string.IsNullOrWhiteSpace(PublishedCameraName) || !string.IsNullOrWhiteSpace(PublishedStepName);
    public string SourceIdentityText => $"{Handoff.Context.ProjectId} · {Handoff.Context.SequenceId} · {(string.IsNullOrWhiteSpace(PublishedCameraName) ? Handoff.Context.CameraId : PublishedCameraName)} · {(string.IsNullOrWhiteSpace(PublishedStepName) ? Handoff.Context.StepId : PublishedStepName)}";
    public string InspectionSourcePath => Handoff.Context.Artifacts.FirstOrDefault(artifact => artifact.Role == IntegrationArtifactRoles.InspectionSource)?.RelativePath ?? string.Empty;
    public string InspectionRecipePath => Handoff.Context.Artifacts.FirstOrDefault(artifact => artifact.Role == IntegrationArtifactRoles.InspectionRecipe)?.RelativePath ?? string.Empty;
    public string ImageCaption => L("RequestImage", "이미지", "Image");
    public string HistoryDetailText => string.IsNullOrEmpty(HistoryText) ? ContextText : HistoryText + Environment.NewLine + ContextText;
    public string SentCaption => L("RequestSent", "전송됨", "Sent");
    public string AcknowledgementCaption => L("RequestAcknowledgement", "수신 확인", "Acknowledged");
    public string WaitingCaption => L("RequestWaitingConsumer", "검사 실행 대기", "Waiting for inspection");
    public string RunningCaption => L("RequestRunning", "검사 중", "Inspecting");
    public string ExecutionStatusHint => L("RequestExecutionStatusHint", "검사 실행 상태는 외부 검사 프로그램에서 확인하세요.", "Check inspection execution status in the external inspection program.");
    public string ResultCaption => L("RequestResultReceived", "결과 수신", "Result received");
    public string CancelCaption => L("RequestCancel", "요청 취소", "Cancel request");
    public string HistoryCaption => L("RequestHistory", "이력", "History");
    private static string L(string key, string korean, string english) => OpenVisionLanguageService.T($"R24.Bottom.{key}", korean, english);
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
    private readonly MachineIntegrationGeneratedSourceStore _generatedSourceStore = new();
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
    private readonly Dictionary<(Guid TransactionId, Guid HandoffMessageId), (bool Automatic, bool Applied, IntegrationInspectionOutcome? LateOutcome)> _requestDisplayEvidence = [];
    private readonly Dictionary<(Guid TransactionId, Guid HandoffMessageId), (string CameraName, string StepName)> _requestSourceNames = [];
    private readonly Dictionary<(Guid TransactionId, Guid HandoffMessageId), (bool Acknowledgement, bool Result)> _observedRequestStages = [];
    private volatile int _requestDisplayGeneration;

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
        Func<Task>? abortAutomaticExternalInspectionAsync = null,
        Func<bool>? useGeneratedHeightMap = null)
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
            abortAutomaticExternalInspectionAsync,
            useGeneratedHeightMap)
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
        Func<Task>? abortAutomaticExternalInspectionAsync,
        Func<bool>? useGeneratedHeightMap)
        : this(
            (path, consumer) => workflow.TryCreate(requestContextProvider(), path, producerIdentityProvider(), consumer),
            (path, consumer) => workflow.TryCreate(requestContextProvider(), path, producerIdentityProvider(), consumer) is not null
                || workflow.CanPrepareGenerated(requestContextProvider(), path, producerIdentityProvider(), consumer),
            projectIdProvider,
            settingsPath,
            MachineIntegrationFileDialogHost.SelectExchangeRoot,
            MachineIntegrationFileDialogHost.SelectInspectionRecipe,
            invokeOnUiThreadAsync,
            handleException,
            simulationSnapshotProvider,
            dispatchSimulationCommandAsync,
            abortAutomaticExternalInspectionAsync,
            MachineIntegrationFileDialogHost.SelectHeightMapSource,
            useGeneratedHeightMap)
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
        Func<string, string?>? selectHeightMapSource = null,
        Func<bool>? useGeneratedHeightMap = null)
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
            selectHeightMapSource,
            useGeneratedHeightMap);
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
        PublishInspectionHandoffCommand = new AsyncRelayCommand(
            _ => PublishInspectionHandoffAsync(),
            _ => CanPublishInspectionHandoff,
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
    public AsyncRelayCommand PublishInspectionHandoffCommand { get; }
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
                return OpenVisionLanguageService.T(
                    "Integration.SetupStepExchange",
                    "1/6 교환 폴더를 선택하고 설정을 저장하세요.",
                    "1/6 Choose the exchange folder and save setup.");
            }

            if (Setup.UseThreeDHeightMap)
            {
                if (!Setup.IsThreeDAutomaticSetupReady)
                {
                    return OpenVisionLanguageService.T(
                        "Integration.SetupStepThreeD",
                        "2/6 3D 높이 원본, c3d 레시피, 검사 프로그램 버전·source commit과 시퀀스·장치 연결을 저장하세요.",
                        "2/6 Save the 3D HeightMap source, c3d recipe, consumer identity, and Sequence/device binding.");
                }
            }

            if (!Setup.UseThreeDHeightMap
                && (!readiness.IsInspectionRecipeAvailable || !readiness.IsInspectionRecipeSupportedByTwoD))
            {
                return OpenVisionLanguageService.T(
                    "Integration.SetupStepRecipe",
                    "2/6 사용할 2D 검사 레시피를 선택하세요.",
                    "2/6 Choose the 2D inspection recipe to use.");
            }

            if (!Setup.UseThreeDHeightMap && Setup.TwoDConsumerIdentity is null)
            {
                return OpenVisionLanguageService.T(
                    "Integration.SetupStepConsumer",
                    "3/6 2D 검사 프로그램 버전과 40자리 source commit을 입력하세요.",
                    "3/6 Enter the 2D consumer version and 40-character source commit.");
            }

            if (!HasValidTcpEndpoints())
            {
                return OpenVisionLanguageService.T(
                    "Integration.SetupStepTcp",
                    "4/6 TCP 수신·상대 주소와 포트를 확인하세요.",
                    "4/6 Check the TCP listen/peer addresses and ports.");
            }

            if (!IsSharedKeyReady)
            {
                return OpenVisionLanguageService.T(
                    "Integration.SetupStepKey",
                    "5/6 세션 공유 키를 입력하거나 허용된 환경 변수를 준비하세요.",
                    "5/6 Enter a session shared key or prepare the approved environment variable.");
            }

            if (Setup.HasUnsavedChanges)
            {
                return OpenVisionLanguageService.T(
                    "Integration.SetupStepSave",
                    "6/6 변경 사항을 저장한 뒤 명시적 연결 또는 검사 요청을 실행하세요.",
                    "6/6 Save the changes before an explicit connection or Handoff.");
            }

            return OpenVisionLanguageService.T(
                "Integration.SetupReady",
                "설정 준비 완료. 수신 시작, 상대 Ping 또는 명시적 검사 요청 중 하나를 선택하세요.",
                "Setup ready. Choose Start listening, Ping peer, or an explicit Handoff.")
                + (string.IsNullOrWhiteSpace(HandoffReadinessText) ? string.Empty : "\n" + HandoffReadinessText);
        }
    }

    public string HandoffReadinessText
    {
        get
        {
            var context = _requestContextProvider?.Invoke();
            if (context is null) return string.Empty;
            if (!context.IsExactCommit)
                return L("HandoffBuildRequired", "검사 전송 대기: 소스 커밋과 일치하는 clean 빌드가 필요합니다.", "Handoff pending: a clean build matching its source commit is required.");
            if (string.IsNullOrWhiteSpace(context.ProjectPath))
                return L("HandoffSaveProject", "검사 전송 대기: 레시피를 프로젝트 파일로 저장하세요.", "Handoff pending: save the recipe as a project file.");
            if (context.CurrentCamera?.NumericalInput is { IsExternalInspection: false })
                return L("HandoffGeneratedRun", "내부 수치 모의 판정입니다. 외부 결과 대기를 켠 뒤 새로 촬영하거나 자동 시퀀스를 실행하세요.", "This is a local numerical mock decision. Enable external-result waiting, then capture again or run the automatic sequence.");
            if (context.CurrentCamera?.FrameEvidence is null && context.CurrentCamera?.Result?.FrameEvidence is null)
                return L("HandoffCaptureRequired", "검사 전송 대기: 촬영 단계를 실행해 현재 원본을 생성하세요.", "Handoff pending: execute the capture step to generate the current input.");
            if (context.CurrentCamera.State is not (VirtualCameraState.FrameReady or VirtualCameraState.AwaitingExternalResult))
                return L("HandoffCapturePending", "검사 전송 대기: 현재 촬영이 끝나야 합니다.", "Handoff pending: the current capture must finish.");
            try
            {
                var recipe = Setup.UseThreeDHeightMap ? Setup.ThreeDInspectionRecipePath : Setup.InspectionRecipePath;
                var consumer = Setup.ActiveConsumerIdentity;
                if (consumer is null || !_canBuildRequest(recipe, consumer))
                    return L("HandoffSourceMismatch", "검사 전송 대기: 현재 원본 파일·해시, 촬영 레시피, 검사 종류와 시퀀스 연결을 확인하세요.", "Handoff pending: check the current source file/hash, capture recipe, inspection mode, and sequence binding.");
            }
            catch (IntegrationContractException)
            {
                return L("HandoffBuildRequired", "검사 전송 대기: 소스 커밋과 일치하는 clean 빌드가 필요합니다.", "Handoff pending: a clean build matching its source commit is required.");
            }
            return L("HandoffInputReady", "현재 원본과 검사 연결이 준비되었습니다. 전송과 검사는 명시적으로 실행하세요.", "Current input and inspection binding are ready. Explicitly send the handoff and run inspection.");
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

    public string PublishInspectionHandoffCaption => Setup.UseThreeDHeightMap
        ? L("CreateThreeDRequest", "3D 검사 요청 만들기", "Create 3D inspection request")
        : OpenVisionLanguageService.T("Integration.MmiCreateTwoDRequest");

    public string PublishInspectionHandoffTooltip => Setup.UseThreeDHeightMap
        ? L("CreateThreeDRequestTooltip", "현재 높이 원본을 3D 검사 요청으로 저장합니다. 실제 검사는 3D Studio에서 별도로 실행합니다.", "Save the current height input as a 3D inspection request. Run inspection separately in 3D Studio.")
        : OpenVisionLanguageService.T("Integration.MmiCreateTwoDRequestTooltip");

    public bool CanPublishInspectionHandoff
    {
        get
        {
            if (!Setup.UseThreeDHeightMap) return CanPublishTwoDImageHandoff;
            if (IsDisposed || !_integrationParticipant.CanStart || !Setup.Readiness.IsExchangeRootAvailable
                || !Setup.IsThreeDAutomaticSetupReady || Setup.ThreeDConsumerIdentity is not { } consumer) return false;
            try { return _canBuildRequest(Setup.NormalizePath(Setup.ActiveInspectionRecipePath), consumer); }
            catch (IntegrationContractException) { return false; }
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
        && _requestContextProvider?.Invoke().IsExactCommit != false
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

    internal IReadOnlyList<RunInspectionRecord> CaptureRunInspections(MachineRunRecord record)
    {
        var acquisitions = record.Captures.Select(capture => capture.AcquisitionId).ToHashSet(StringComparer.Ordinal);
        var inspections = record.Captures.Where(capture => capture.ResultJson is not null)
            .Select(capture => new RunInspectionRecord(capture.AcquisitionId, capture.FrameId, null, capture.ResultJson)).ToList();
        foreach (var transaction in _resultObservation.CurrentTransactions)
        {
            if (transaction.Handoff.Context.ProjectId != record.RecipeId || !acquisitions.Contains(transaction.Handoff.Context.AcquisitionId)
                || transaction.Handoff.CreatedAtUtc < record.StartedAtUtc || transaction.Handoff.CreatedAtUtc > record.EndedAtUtc) continue;
            _resultObservation.ValidatedResultsByTransaction.TryGetValue(transaction.Handoff.TransactionId, out var result);
            inspections.Add(new(transaction.Handoff.TransactionId.ToString("D"), transaction.Handoff.Context.AcquisitionId,
                JsonSerializer.Serialize(transaction.Handoff), result is null ? null : JsonSerializer.Serialize(result.Result)));
        }
        return inspections;
    }

    public int PendingInspectionRequestCount => _resultObservation.CurrentTransactions.Count(transaction =>
        GetTransactionHistoryState(transaction) is MachineIntegrationTransactionHistoryState.PendingReview or MachineIntegrationTransactionHistoryState.Reviewed);

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
        ? OpenVisionLanguageService.T("Integration.RequestNone", "현재 프로젝트의 검사 요청이 없습니다.", "No Handoff exists for the current project.")
        : string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            OpenVisionLanguageService.T(
                "Integration.RequestStatusFormat",
                "최근 검사 요청: {0}/{1} · {2}",
                "Latest Handoff: {0}/{1} · {2}"),
            _resultObservation.LatestTransaction.Handoff.Context.Modality,
            _resultObservation.LatestTransaction.Handoff.Context.InputKind,
            GetTransactionState(_resultObservation.LatestTransaction));

    public string AcknowledgementStatusText => _resultObservation.LatestAcknowledgement is not null
        ? string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            OpenVisionLanguageService.T(
                "Integration.ReceiptStatusFormat",
                "최근 수신 확인: {0}",
                "Latest Acknowledgement: {0}"),
            GetReceiptStatusCaption(_resultObservation.LatestAcknowledgement.Status))
        : _resultObservation.AcknowledgementReadError is not null
            ? string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                OpenVisionLanguageService.T(
                    "Integration.ReceiptReadFailed",
                    "수신 확인 읽기 실패: {0}",
                    "Acknowledgement read failed: {0}"),
                _resultObservation.AcknowledgementReadError)
            : _resultObservation.LatestAcknowledgementTransaction is null
                ? OpenVisionLanguageService.T(
                    "Integration.ReceiptNone",
                    "현재 프로젝트의 수신 확인이 없습니다.",
                    "No Acknowledgement exists for the current project.")
                : OpenVisionLanguageService.T(
                    "Integration.ReceiptPendingValidation",
                    "수신 확인을 아직 검증하지 못했습니다.",
                    "Acknowledgement has not been validated yet.");

    public string ResultStatusText => _resultObservation.LatestResult is not null
        ? string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            OpenVisionLanguageService.T(
                "Integration.ResultStatusFormat",
                "최근 검사 결과: {0} · {1} · 운전 {2}",
                "Latest Result: {0} · {1} · Run {2}"),
            GetResultOutcomeCaption(_resultObservation.LatestResult.Outcome),
            GetResultStatusCaption(_resultObservation.LatestResult.Status),
            _resultObservation.LatestResult.RunId)
        : _resultObservation.ResultReadError is not null
            ? string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                OpenVisionLanguageService.T("Integration.ResultReadFailed", "검사 결과 읽기 실패: {0}", "Result read failed: {0}"),
                _resultObservation.ResultReadError)
            : _resultObservation.LatestResultTransaction is null
                ? OpenVisionLanguageService.T("Integration.ResultNone", "아직 검증된 검사 결과가 없습니다.", "No validated Result is available yet.")
                : OpenVisionLanguageService.T("Integration.ResultPendingValidation", "결과 파일이 있지만 요청·수신 확인·결과의 순서를 아직 검증하지 못했습니다.", "A Result file exists, but the complete sequence is not validated yet.");

    /// <summary>
    /// States which decision source Machine Studio is configured to use. This is
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
            TryPublish(() =>
            {
                _observedRequestStages.Clear();
                _requestDisplayGeneration++;
            });
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

    internal Task<MachineIntegrationOperationObservation> PublishInspectionHandoffAsync()
    {
        if (!CanPublishInspectionHandoff || Setup.ActiveConsumerIdentity is not { } consumer || !_integrationParticipant.CanStart)
            return Task.FromResult(new MachineIntegrationOperationObservation(MachineIntegrationOperationKind.PublishHandoff, MachineIntegrationParticipantOutcome.Idle));
        return _integrationParticipant.TrackAsync(MachineIntegrationOperationKind.PublishHandoff,
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
        var displayGeneration = _requestDisplayGeneration;
        try
        {
            var publishRuntimeContext = _simulationWorkflow?.CapturePublishContext();
            var captureContext = _requestContextProvider?.Invoke();
            if (captureContext?.CurrentCamera is { NumericalInput: { IsExternalInspection: true } generated } camera)
            {
                if (!captureContext.IsExactCommit || !_canBuildRequest(Setup.ActiveInspectionRecipePath, consumer)
                    || captureContext.ProjectPath is null || generated.Frame.FrameId != camera.CurrentAcquisitionId)
                    throw new InvalidOperationException("The generated capture is not eligible for this inspection handoff.");
                var contextKey = MachineIntegrationRequestWorkflow.CreateRefreshKey(captureContext);
                var frame = generated.Frame;
                await _generatedSourceStore.WriteAsync(captureContext.ProjectPath,
                    new(frame.FrameId, frame.SourceRelativePath, frame.ContentSha256, frame.ContentLength),
                    generated.CopyInspectionBytes()).ConfigureAwait(true);
                if (IsDisposed || _requestContextProvider is null
                    || contextKey != MachineIntegrationRequestWorkflow.CreateRefreshKey(_requestContextProvider()))
                    throw new InvalidOperationException("The recipe or capture changed while preparing the generated input.");
            }
            var request = _requestFactory(Setup.NormalizePath(Setup.ActiveInspectionRecipePath), consumer)
                ?? throw new InvalidOperationException(
                    L(
                        "ContextNotReady",
                        "저장된 프로젝트에서 완료된 가상 카메라 프레임을 먼저 준비하세요.",
                        "Prepare a completed virtual-camera frame from a saved project first."));
            var sourceNames = CapturePublishedSourceNames(captureContext, request);
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
                if (displayGeneration == _requestDisplayGeneration)
                {
                    var key = (handoff.TransactionId, handoff.MessageId);
                    _requestDisplayEvidence[key] = (automatic, false, null);
                    if (sourceNames is { } names && string.Equals(_projectIdProvider(), handoff.Context.ProjectId, StringComparison.Ordinal))
                        _requestSourceNames[key] = names;
                }
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
        var displayGeneration = _requestDisplayGeneration;
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
                var key = (validatedResult.Handoff.TransactionId, validatedResult.Handoff.MessageId);
                if (displayGeneration == _requestDisplayGeneration && _requestDisplayEvidence.TryGetValue(key, out var display))
                    _requestDisplayEvidence[key] = (display.Automatic, true, null);
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
        var displayGeneration = _requestDisplayGeneration;
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
                if (displayGeneration == _requestDisplayGeneration)
                {
                    foreach (var transaction in _resultObservation.CurrentTransactions)
                    {
                        if (!string.Equals(transaction.Handoff.Context.ProjectId, _projectIdProvider(), StringComparison.Ordinal)) continue;
                        var key = (transaction.Handoff.TransactionId, transaction.Handoff.MessageId);
                        _observedRequestStages.TryGetValue(key, out var observed);
                        _resultObservation.ValidatedResultsByTransaction.TryGetValue(key.TransactionId, out var validated);
                        var acknowledgement = validated?.Acknowledgement
                            ?? (_resultObservation.LatestAcknowledgement?.TransactionId == key.TransactionId ? _resultObservation.LatestAcknowledgement : null);
                        _observedRequestStages[key] = (observed.Acknowledgement || acknowledgement is not null, observed.Result || validated is not null);
                    }
                }
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
                            var key = (automaticResult.Handoff.TransactionId, automaticResult.Handoff.MessageId);
                            if (displayGeneration == _requestDisplayGeneration && _requestDisplayEvidence.TryGetValue(key, out var display))
                                _requestDisplayEvidence[key] = (display.Automatic, false, automaticResult.Result.Outcome);
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

    private static (string CameraName, string StepName)? CapturePublishedSourceNames(
        MachineIntegrationRequestContext? context, MachineInspectionHandoffRequest request)
    {
        var camera = context?.CurrentCamera;
        var frame = camera?.Result?.FrameEvidence ?? camera?.FrameEvidence;
        if (context is null || camera is null || frame is null
            || !string.Equals(context.ProjectId, request.ProjectId, StringComparison.Ordinal)
            || !string.Equals(context.CameraId, request.CameraId, StringComparison.Ordinal)
            || !string.Equals(camera.Id, request.CameraId, StringComparison.Ordinal)
            || !string.Equals(camera.CurrentAcquisitionId, request.AcquisitionId, StringComparison.Ordinal)
            || !string.Equals(frame.FrameId, request.FrameId, StringComparison.Ordinal))
            return null;

        var step = context.Sequences.FirstOrDefault(sequence => sequence.Id == request.SequenceId)?.Steps.FirstOrDefault(item => item.Id == request.StepId);
        return step is null ? null : (camera.Name, step.Name);
    }

    private MachineIntegrationTransactionHistoryItem CreateTransactionHistoryRow(MachineIntegrationTransactionSummary transaction)
    {
        var id = transaction.Handoff.TransactionId;
        (bool Automatic, bool Applied, IntegrationInspectionOutcome? LateOutcome) display;
        bool hasDisplayEvidence;
        (string CameraName, string StepName) sourceNames;
        (bool Acknowledgement, bool Result) observedStages;
        lock (_lifetimeGate)
        {
            hasDisplayEvidence = _requestDisplayEvidence.TryGetValue((id, transaction.Handoff.MessageId), out display);
            _requestSourceNames.TryGetValue((id, transaction.Handoff.MessageId), out sourceNames);
            _observedRequestStages.TryGetValue((id, transaction.Handoff.MessageId), out observedStages);
        }
        _resultObservation.ValidatedResultsByTransaction.TryGetValue(id, out var validated);
        var result = validated?.Result;
        var acknowledgement = validated?.Acknowledgement
            ?? (_resultObservation.LatestAcknowledgement?.TransactionId == id ? _resultObservation.LatestAcknowledgement : null);
        var state = GetTransactionHistoryState(transaction);
        var readError = (_resultObservation.LatestResultTransaction?.Handoff.TransactionId == id ? _resultObservation.ResultReadError : null)
            ?? (_resultObservation.LatestAcknowledgementTransaction?.Handoff.TransactionId == id ? _resultObservation.AcknowledgementReadError : null);
        var hasReplyReadError = !string.IsNullOrWhiteSpace(readError) || transaction.HasResult && result is null;
        var hasAcceptedAcknowledgement = acknowledgement?.Status == IntegrationAcknowledgementStatus.Accepted;
        var error = result?.Error?.Message ?? acknowledgement?.Error?.Message ?? readError;
        if (hasReplyReadError && string.IsNullOrWhiteSpace(error))
            error = L("RequestResultVerificationError", "검사 결과를 검증하지 못했습니다.", "The inspection result could not be validated.");
        var bottomStatus = hasReplyReadError
            ? L("RequestProcessingError", "처리오류", "Processing error")
            : result is not null
                ? (result.Status switch
                {
                    IntegrationResultStatus.Completed => L("RequestResultReceived", "결과 수신", "Result received"),
                    IntegrationResultStatus.Failed => L("RequestProcessingError", "처리오류", "Processing error"),
                    _ => GetResultStatusCaption(result.Status)
                }) + " · " + GetResultOutcomeCaption(result.Outcome)
                : acknowledgement?.Status == IntegrationAcknowledgementStatus.Rejected
                    ? GetReceiptStatusCaption(IntegrationAcknowledgementStatus.Rejected)
                    : hasAcceptedAcknowledgement
                        ? L("RequestAcknowledgement", "수신 확인", "Acknowledged")
                        : L("RequestSent", "전송됨", "Sent");
        var tone = hasReplyReadError || state == MachineIntegrationTransactionHistoryState.Rejected
            || result?.Status is IntegrationResultStatus.Failed or IntegrationResultStatus.Cancelled
            ? "bad"
            : result?.Status == IntegrationResultStatus.Completed ? "ok" : "warn";
        var history = new List<string> { $"{transaction.Handoff.CreatedAtUtc.ToLocalTime():HH:mm:ss} {L("RequestSent", "전송됨", "Sent")}" };
        if (acknowledgement is not null)
        {
            var acknowledgementCaption = hasAcceptedAcknowledgement
                ? L("RequestAcknowledgement", "수신 확인", "Acknowledged")
                : GetReceiptStatusCaption(acknowledgement.Status);
            history.Add($"{acknowledgement.CreatedAtUtc.ToLocalTime():HH:mm:ss} {acknowledgementCaption}");
        }
        if (result is not null) history.Add($"{result.CreatedAtUtc.ToLocalTime():HH:mm:ss} {bottomStatus}");
        return new(transaction.Handoff, GetTransactionState(transaction))
        {
            BottomStatusText = bottomStatus,
            PublishedCameraName = sourceNames.CameraName ?? string.Empty,
            PublishedStepName = sourceNames.StepName ?? string.Empty,
            StatusTone = tone,
            HasAcknowledgement = hasAcceptedAcknowledgement,
            HasValidatedResult = validated is not null,
            HasReceivedAcknowledgement = observedStages.Acknowledgement,
            HasReceivedResult = observedStages.Result,
            IsAutomaticRequest = hasDisplayEvidence ? display.Automatic : null,
            IsResultApplied = hasDisplayEvidence && display.Applied,
            IsLateResult = hasDisplayEvidence && display.LateOutcome is not null,
            LateResultText = hasDisplayEvidence && display.LateOutcome is { } lateOutcome
                ? string.Format(CultureInfo.CurrentCulture, L("RequestLateResult", "지연 수신 {0} · 미반영", "Late result {0} · not applied"), GetResultOutcomeCaption(lateOutcome)) : string.Empty,
            InspectionCompleted = result?.Status == IntegrationResultStatus.Completed,
            IsPending = state is MachineIntegrationTransactionHistoryState.PendingReview or MachineIntegrationTransactionHistoryState.Reviewed,
            ErrorText = error ?? string.Empty,
            MetricsText = result is null ? string.Empty : string.Join(" · ", result.Metrics.Select(metric =>
                string.Join(" ", new[] { metric.Name, metric.Unit, metric.Value.ToString(CultureInfo.InvariantCulture) }
                    .Where(value => !string.IsNullOrWhiteSpace(value))))),
            HistoryText = string.Join(Environment.NewLine, history)
        };
    }

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

    private static string GetReceiptStatusCaption(IntegrationAcknowledgementStatus value) => value switch
    {
        IntegrationAcknowledgementStatus.Accepted => OpenVisionLanguageService.T("Integration.ReceiptAccepted", "수신 확인됨", "Accepted"),
        IntegrationAcknowledgementStatus.Rejected => OpenVisionLanguageService.T("Integration.ReceiptRejected", "거부됨", "Rejected"),
        _ => value.ToString()
    };

    private static string GetResultOutcomeCaption(IntegrationInspectionOutcome value) => value switch
    {
        IntegrationInspectionOutcome.Pass => OpenVisionLanguageService.T("Integration.OutcomePass", "OK", "Pass"),
        IntegrationInspectionOutcome.Ng => OpenVisionLanguageService.T("Integration.OutcomeNg", "NG", "Ng"),
        IntegrationInspectionOutcome.NotMeasured => OpenVisionLanguageService.T("Integration.OutcomeNotMeasured", "측정 안 됨", "NotMeasured"),
        IntegrationInspectionOutcome.Indeterminate => OpenVisionLanguageService.T("Integration.OutcomeIndeterminate", "판정불가", "Indeterminate"),
        IntegrationInspectionOutcome.CorrelationMismatch => OpenVisionLanguageService.T("Integration.OutcomeCorrelationMismatch", "요청-응답 불일치", "CorrelationMismatch"),
        IntegrationInspectionOutcome.Tampered => OpenVisionLanguageService.T("Integration.OutcomeTampered", "데이터 무결성 오류", "Tampered"),
        IntegrationInspectionOutcome.ExecutionError => OpenVisionLanguageService.T("Integration.OutcomeExecutionError", "처리 오류", "ExecutionError"),
        _ => value.ToString()
    };

    private static string GetResultStatusCaption(IntegrationResultStatus value) => value switch
    {
        IntegrationResultStatus.Completed => OpenVisionLanguageService.T("Integration.ResultCompleted", "완료", "Completed"),
        IntegrationResultStatus.Failed => OpenVisionLanguageService.T("Integration.ResultFailed", "처리 오류", "Failed"),
        IntegrationResultStatus.Cancelled => OpenVisionLanguageService.T("Integration.ResultCancelled", "취소됨", "Cancelled"),
        _ => value.ToString()
    };

    private string GetTransactionState(MachineIntegrationTransactionSummary transaction) =>
        GetTransactionHistoryState(transaction) switch
        {
            MachineIntegrationTransactionHistoryState.ResultReadFailed =>
                OpenVisionLanguageService.T("Integration.TransactionHistoryFilterResultReadFailed", "결과 읽기 실패", "Result read failed"),
            MachineIntegrationTransactionHistoryState.ResultPublished =>
                OpenVisionLanguageService.T("Integration.TransactionHistoryFilterPublished", "결과 수신", "Result published"),
            MachineIntegrationTransactionHistoryState.Rejected =>
                OpenVisionLanguageService.T("Integration.TransactionHistoryFilterRejected", "거부됨", "Rejected"),
            MachineIntegrationTransactionHistoryState.Reviewed =>
                OpenVisionLanguageService.T("Integration.TransactionHistoryFilterReviewed", "수신 확인됨", "Reviewed"),
            _ => OpenVisionLanguageService.T("Integration.TransactionHistoryFilterPendingReview", "수신 확인 대기", "Pending review")
        };

    private void RefreshCommandState()
    {
        OnPropertyChanged(nameof(CanPublishTwoDImageHandoff));
        OnPropertyChanged(nameof(CanPublishInspectionHandoff));
        OnPropertyChanged(nameof(PublishInspectionHandoffCaption));
        OnPropertyChanged(nameof(PublishInspectionHandoffTooltip));
        OnPropertyChanged(nameof(HandoffReadinessText));
        OnPropertyChanged(nameof(SetupWorkflowStatusText));
        OnPropertyChanged(nameof(CanRefreshResults));
        OnPropertyChanged(nameof(CanApplyResultToSimulation));
        PublishTwoDImageHandoffCommand.RaiseCanExecuteChanged();
        PublishInspectionHandoffCommand.RaiseCanExecuteChanged();
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
        OnPropertyChanged(nameof(PendingInspectionRequestCount));
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
            new(null, OpenVisionLanguageService.T("Integration.TransactionHistoryFilterAll", "전체 상태", "All states")),
            new(MachineIntegrationTransactionHistoryState.PendingReview,
                OpenVisionLanguageService.T("Integration.TransactionHistoryFilterPendingReview", "수신 확인 대기", "Pending review")),
            new(MachineIntegrationTransactionHistoryState.Reviewed,
                OpenVisionLanguageService.T("Integration.TransactionHistoryFilterReviewed", "수신 확인됨", "Reviewed")),
            new(MachineIntegrationTransactionHistoryState.Rejected,
                OpenVisionLanguageService.T("Integration.TransactionHistoryFilterRejected", "거부됨", "Rejected")),
            new(MachineIntegrationTransactionHistoryState.ResultPublished,
                OpenVisionLanguageService.T("Integration.TransactionHistoryFilterPublished", "결과 수신", "Result published")),
            new(MachineIntegrationTransactionHistoryState.ResultReadFailed,
                OpenVisionLanguageService.T("Integration.TransactionHistoryFilterResultReadFailed", "결과 읽기 실패", "Result read failed"))
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
            if (args.PropertyName == nameof(MachineIntegrationSetupViewModel.ExchangeRoot))
            {
                _requestDisplayEvidence.Clear();
                _requestSourceNames.Clear();
                _observedRequestStages.Clear();
                _requestDisplayGeneration++;
                RaiseProjectionChanged();
            }
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
            _requestDisplayEvidence.Clear();
            _requestSourceNames.Clear();
            _observedRequestStages.Clear();
            _requestDisplayGeneration++;
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

            _requestDisplayEvidence.Clear();
            _requestSourceNames.Clear();
            _observedRequestStages.Clear();
            _requestDisplayGeneration++;
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
        PublishInspectionHandoffCommand.RaiseCanExecuteChanged();
        RefreshResultsCommand.RaiseCanExecuteChanged();
        ApplyResultToSimulationCommand.RaiseCanExecuteChanged();
        SetSessionSharedKeyCommand.RaiseCanExecuteChanged();
    }
}
