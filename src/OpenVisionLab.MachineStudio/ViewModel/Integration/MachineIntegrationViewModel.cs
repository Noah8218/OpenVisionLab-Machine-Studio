using System.ComponentModel;
using OpenVisionLab;
using OpenVisionLab.Integration.Contracts;
using OpenVisionLab.Machine.Infrastructure.Integration;
using OpenVisionLab.MachineStudio.View.Dialogs;

namespace OpenVisionLab.MachineStudio.ViewModel;

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
    private readonly MachineIntegrationParticipant _integrationParticipant;
    private readonly MachineIntegrationTcpControlViewModel _tcpControl;
    private readonly MachineIntegrationResultObservationWorkflow _resultObservation;
    private string _statusText = string.Empty;
    private readonly object _lifetimeGate = new();
    private bool _disposed;

    // Main supplies facts; this owner prepares requests and tracks its own refresh identity.
    internal MachineIntegrationViewModel(
        Func<MachineIntegrationRequestContext> requestContextProvider,
        Func<IntegrationApplicationIdentity> producerIdentityProvider,
        Func<string?> projectIdProvider,
        string? settingsPath = null,
        Func<Func<Task>, Task>? invokeOnUiThreadAsync = null)
        : this(
            new MachineIntegrationRequestWorkflow(),
            requestContextProvider,
            producerIdentityProvider,
            projectIdProvider,
            settingsPath,
            invokeOnUiThreadAsync)
    {
    }

    private MachineIntegrationViewModel(
        MachineIntegrationRequestWorkflow workflow,
        Func<MachineIntegrationRequestContext> requestContextProvider,
        Func<IntegrationApplicationIdentity> producerIdentityProvider,
        Func<string?> projectIdProvider,
        string? settingsPath,
        Func<Func<Task>, Task>? invokeOnUiThreadAsync)
        : this(
            (path, consumer) => workflow.TryCreate(requestContextProvider(), path, producerIdentityProvider(), consumer),
            (path, consumer) => workflow.TryCreate(requestContextProvider(), path, producerIdentityProvider(), consumer) is not null,
            projectIdProvider,
            settingsPath,
            MachineIntegrationFileDialogHost.SelectExchangeRoot,
            MachineIntegrationFileDialogHost.SelectInspectionRecipe,
            invokeOnUiThreadAsync)
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
        Func<Func<Task>, Task>? invokeOnUiThreadAsync = null)
    {
        _requestFactory = requestFactory ?? throw new ArgumentNullException(nameof(requestFactory));
        _canBuildRequest = canBuildRequest ?? throw new ArgumentNullException(nameof(canBuildRequest));
        _projectIdProvider = projectIdProvider ?? throw new ArgumentNullException(nameof(projectIdProvider));
        ArgumentNullException.ThrowIfNull(selectExchangeRoot);
        ArgumentNullException.ThrowIfNull(selectInspectionRecipe);
        _invokeOnUiThreadAsync = invokeOnUiThreadAsync ?? ExecuteImmediatelyAsync;
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
            () => tcpControl?.SetSessionSharedKey(null));
        _resultObservation = new(
            () => Setup.ExchangeRoot,
            _projectIdProvider,
            () => CanRefreshResults,
            () => IsBusy,
            RefreshResultsAsync,
            _invokeOnUiThreadAsync,
            exception => TryPublish(() => StatusText = exception.Message),
            pathReadiness);
        _tcpControl = new(
            Setup.RequireSavedTcpSettings,
            () => _resultObservation.LatestTransaction,
            RefreshResultsAsync,
            status => TryPublish(() => StatusText = status),
            _invokeOnUiThreadAsync);
        tcpControl = _tcpControl;
        Setup.PropertyChanged += OnSetupPropertyChanged;
        Setup.ResetCompleted += OnSetupResetCompleted;
        _tcpControl.PropertyChanged += OnTcpControlPropertyChanged;

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
        Setup.RefreshCommandState();
        _resultObservation.ConfigureWatcher();
    }

    public MachineIntegrationSetupViewModel Setup { get; }
    public AsyncRelayCommand PublishTwoDImageHandoffCommand { get; }
    public AsyncRelayCommand RefreshResultsCommand { get; }
    public RelayCommand StartTcpListenerCommand => _tcpControl.StartTcpListenerCommand;
    public RelayCommand StopTcpListenerCommand => _tcpControl.StopTcpListenerCommand;
    public RelayCommand PingTcpPeerCommand => _tcpControl.PingTcpPeerCommand;
    public RelayCommand PushLatestTransactionCommand => _tcpControl.PushLatestTransactionCommand;
    public RelayCommand PullLatestTransactionCommand => _tcpControl.PullLatestTransactionCommand;

    public bool IsBusy
    {
        get => _integrationParticipant.IsBusy;
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
            if (IsDisposed)
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

            return _canBuildRequest(paths.InspectionRecipePath, consumer);
        }
    }

    public bool CanRefreshResults =>
        !IsDisposed
        && _integrationParticipant.CanStart
        && !string.IsNullOrWhiteSpace(_projectIdProvider())
        && Setup.Readiness.CanRefreshResults;

    public bool CanPushLatestTransaction => _tcpControl.CanPushLatestTransaction;

    public bool CanPullLatestTransaction => _tcpControl.CanPullLatestTransaction;

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

    internal void RefreshSourceContext()
    {
        if (_requestContextProvider is not { } getContext)
        {
            return;
        }

        var contextKey = MachineIntegrationRequestWorkflow.CreateRefreshKey(getContext());
        if (string.Equals(_requestContextKey, contextKey, StringComparison.Ordinal))
        {
            return;
        }

        _requestContextKey = contextKey;
        RefreshContext();
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
        Setup.RefreshLocalization();
        _tcpControl.RefreshLocalization();
        RaiseProjectionChanged();
        StatusText = L(
            "Ready",
            "통합 상태를 갱신했습니다. Publish와 Refresh Result는 별도 명령입니다.",
            "Integration text refreshed. Publish and Refresh Result remain separate commands.");
    }

    public void SetSessionSharedKey(string? encodedKey) => _tcpControl.SetSessionSharedKey(encodedKey);

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
            () => PublishTwoDImageHandoffCoreAsync(consumer));
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

    private async Task<MachineIntegrationOperationObservation> PublishTwoDImageHandoffCoreAsync(
        IntegrationApplicationIdentity consumer)
    {
        try
        {
            var request = _requestFactory(Setup.NormalizePath(Setup.InspectionRecipePath), consumer)
                ?? throw new InvalidOperationException(
                    L(
                        "ContextNotReady",
                        "저장된 프로젝트에서 완료된 가상 카메라 프레임을 먼저 준비하세요.",
                        "Prepare a completed virtual-camera frame from a saved project first."));
            var handoff = await MachineIntegrationHandoffPublisher.PublishAsync(
                    Setup.NormalizePath(Setup.ExchangeRoot),
                    request)
                .ConfigureAwait(true);
            TryPublish(() =>
            {
                _resultObservation.RecordPublishedHandoff(handoff);
                RaiseProjectionChanged();
                StatusText = string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    L("HandoffPublished", "2D/Image Handoff 게시 완료: {0:D}", "2D/Image Handoff published: {0:D}"),
                    handoff.TransactionId);
            });
            return new(
                MachineIntegrationOperationKind.PublishHandoff,
                MachineIntegrationParticipantOutcome.Completed);
        }
        catch (Exception exception)
        {
            TryPublish(() => StatusText = exception.Message);
            return new(
                MachineIntegrationOperationKind.PublishHandoff,
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
            return new(
                MachineIntegrationOperationKind.RefreshResults,
                MachineIntegrationParticipantOutcome.Completed);
        }
        catch (Exception exception)
        {
            TryPublish(() => StatusText = exception.Message);
            return new(
                MachineIntegrationOperationKind.RefreshResults,
                MachineIntegrationParticipantOutcome.Failed,
                exception);
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
        Setup.RefreshCommandState();
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
            TryPublish(() => OnPropertyChanged(propertyName));
        }
    }

    private void OnSetupPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(MachineIntegrationSetupViewModel.ExchangeRoot))
        {
            _resultObservation.ConfigureWatcher();
        }

        TryPublish(RefreshCommandState);
    }

    private void OnSetupResetCompleted(object? sender, EventArgs args)
    {
        _resultObservation.Reset();
        RaiseProjectionChanged();
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

    private void HandleCommandException(Exception exception) => TryPublish(() => StatusText = exception.Message);

    private static string L(string key, string korean, string english) =>
        OpenVisionLanguageService.CurrentLanguage == OpenVisionLanguage.English
            ? english
            : korean;

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
    }
}
