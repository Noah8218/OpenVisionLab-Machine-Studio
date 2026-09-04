using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Windows.Input;
using OpenVisionLab;
using OpenVisionLab.Integration.Contracts;
using OpenVisionLab.Integration.Transport.Tcp;
using OpenVisionLab.Machine.Infrastructure.Integration;

namespace OpenVisionLab.MachineStudio.ViewModel;

/// <summary>
/// Owns the Machine Studio TCP control lifecycle: listener/client commands,
/// transient shared-key handling, operation cancellation, and transport
/// disposal. Setup form values and result observation remain parent-owned
/// through explicit callbacks.
/// </summary>
internal sealed class MachineIntegrationTcpControlViewModel : ViewModelBase, IDisposable
{
    private readonly Func<MachineIntegrationTcpSettings> _settingsProvider;
    private readonly Func<MachineIntegrationTransactionSummary?> _latestTransactionProvider;
    private readonly Func<Task> _refreshResults;
    private readonly Action<string> _setStatus;
    private readonly MachineIntegrationSharedKeyStore _sharedKeyStore = new();
    private readonly MachineIntegrationTcpWorkflow _tcpWorkflow = new();
    private string _tcpListenerStatusText;
    private string _sharedKeyStatusText;
    private string _lastTcpTransferText;
    private bool _isTcpBusy;
    private bool _isTcpListening;
    private CancellationTokenSource? _tcpOperationCancellation;
    private int _tcpOperationActive;
    private bool _disposed;

    internal MachineIntegrationTcpControlViewModel(
        Func<MachineIntegrationTcpSettings> settingsProvider,
        Func<MachineIntegrationTransactionSummary?> latestTransactionProvider,
        Func<Task> refreshResults,
        Action<string> setStatus)
    {
        _settingsProvider = settingsProvider ?? throw new ArgumentNullException(nameof(settingsProvider));
        _latestTransactionProvider = latestTransactionProvider
            ?? throw new ArgumentNullException(nameof(latestTransactionProvider));
        _refreshResults = refreshResults ?? throw new ArgumentNullException(nameof(refreshResults));
        _setStatus = setStatus ?? throw new ArgumentNullException(nameof(setStatus));
        _tcpListenerStatusText = L(
            "TcpStopped",
            "TCP 수신 중지됨",
            "TCP listener stopped");
        _sharedKeyStatusText = DescribeSharedKeyStatus();
        _lastTcpTransferText = L(
            "NoTcpTransfer",
            "TCP 전송 기록이 없습니다.",
            "No TCP transfer has run.");

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
    }

    internal RelayCommand StartTcpListenerCommand { get; }
    internal RelayCommand StopTcpListenerCommand { get; }
    internal RelayCommand PingTcpPeerCommand { get; }
    internal RelayCommand PushLatestTransactionCommand { get; }
    internal RelayCommand PullLatestTransactionCommand { get; }

    internal bool IsTcpListening
    {
        get => _isTcpListening;
        private set
        {
            if (SetProperty(ref _isTcpListening, value))
            {
                RefreshCommandState();
                OnPropertyChanged(nameof(CanEditTcpSetup));
            }
        }
    }

    internal bool IsTcpBusy
    {
        get => _isTcpBusy;
        private set
        {
            if (SetProperty(ref _isTcpBusy, value))
            {
                RefreshCommandState();
                OnPropertyChanged(nameof(CanEditTcpSetup));
            }
        }
    }

    internal bool CanEditTcpSetup => !IsTcpBusy && !IsTcpListening;

    internal string TcpListenerStatusText
    {
        get => _tcpListenerStatusText;
        private set => SetProperty(ref _tcpListenerStatusText, value);
    }

    internal string SharedKeyStatusText
    {
        get => _sharedKeyStatusText;
        private set => SetProperty(ref _sharedKeyStatusText, value);
    }

    internal string LastTcpTransferText
    {
        get => _lastTcpTransferText;
        private set => SetProperty(ref _lastTcpTransferText, value);
    }

    internal bool CanPushLatestTransaction =>
        !IsTcpBusy && _latestTransactionProvider() is not null;

    internal bool CanPullLatestTransaction =>
        !IsTcpBusy && _latestTransactionProvider() is not null;

    internal void RefreshLocalization()
    {
        SharedKeyStatusText = DescribeSharedKeyStatus();
        OnPropertyChanged(nameof(CanEditTcpSetup));
    }

    internal void SetSessionSharedKey(string? encodedKey) =>
        SharedKeyStatusText = DescribeSharedKeyStatus(_sharedKeyStore.SetSessionKey(encodedKey));

    internal Task StartTcpListenerAsync() => RunTcpOperationAsync(
        L("TcpStarting", "TCP 수신을 시작하는 중입니다.", "Starting TCP listener."),
        async cancellationToken =>
        {
            if (_tcpWorkflow.IsListening)
            {
                throw new InvalidOperationException(L(
                    "TcpAlreadyStarted",
                    "TCP 수신기가 이미 실행 중입니다.",
                    "The TCP listener is already running."));
            }

            var settings = _settingsProvider();
            var key = AcquireSharedKey();
            try
            {
                var endpoint = await _tcpWorkflow.StartListeningAsync(
                        settings.ExchangeRoot,
                        settings.ListenAddress,
                        settings.ListenPort,
                        key,
                        cancellationToken)
                    .ConfigureAwait(false);
                IsTcpListening = true;
                TcpListenerStatusText = string.Format(
                    CultureInfo.CurrentCulture,
                    L("TcpListeningFormat", "TCP 수신 중: {0}", "TCP listening: {0}"),
                    endpoint);
                _setStatus(L(
                    "TcpStarted",
                    "TCP 수신을 시작했습니다. 수신만으로 ACK, 검사, Run 또는 Result를 실행하지 않습니다.",
                    "TCP listening started. Receipt alone never ACKs, inspects, runs, or creates a Result."));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
            }
        });

    internal Task StopTcpListenerAsync() => RunTcpOperationAsync(
        L("TcpStopping", "TCP 수신을 중지하는 중입니다.", "Stopping TCP listener."),
        async _ =>
        {
            await _tcpWorkflow.StopListeningAsync().ConfigureAwait(false);
            IsTcpListening = false;
            TcpListenerStatusText = L(
                "TcpStopped",
                "TCP 수신 중지됨",
                "TCP listener stopped");
            _setStatus(L(
                "TcpStoppedStatus",
                "TCP 수신을 중지했습니다.",
                "TCP listening stopped."));
        });

    internal Task PingTcpPeerAsync() => RunTcpTransferAsync(
        L("TcpPinging", "TCP 상대를 확인하는 중입니다.", "Pinging TCP peer."),
        (settings, key, cancellationToken) =>
            _tcpWorkflow.PingAsync(
                settings.ExchangeRoot,
                key,
                new TcpIntegrationEndpoint(settings.PeerHost, settings.PeerPort),
                cancellationToken),
        refreshAfterTransfer: false);

    internal Task PushLatestTransactionAsync()
    {
        var transaction = _latestTransactionProvider();
        if (transaction is null)
        {
            _setStatus(L(
                "ChooseTransferTransaction",
                "먼저 Handoff를 게시하거나 Result를 새로고침하세요.",
                "Publish a Handoff or refresh a Result before pushing a transaction."));
            return Task.CompletedTask;
        }

        return RunTcpTransferAsync(
            L("TcpPushing", "최근 거래를 보내는 중입니다.", "Pushing the latest transaction."),
            (settings, key, cancellationToken) =>
                _tcpWorkflow.PushTransactionAsync(
                    settings.ExchangeRoot,
                    key,
                    new TcpIntegrationEndpoint(settings.PeerHost, settings.PeerPort),
                    transaction.Handoff.TransactionId,
                    cancellationToken),
            refreshAfterTransfer: false);
    }

    internal Task PullLatestTransactionAsync()
    {
        var transaction = _latestTransactionProvider();
        if (transaction is null)
        {
            _setStatus(L(
                "ChooseTransferTransaction",
                "먼저 Handoff를 게시하거나 거래를 새로고침하세요.",
                "Publish a Handoff or refresh a transaction before pulling it."));
            return Task.CompletedTask;
        }

        return RunTcpTransferAsync(
            L("TcpPulling", "최근 ACK/Result를 받는 중입니다.", "Pulling the latest ACK/Result."),
            (settings, key, cancellationToken) =>
                _tcpWorkflow.PullTransactionAsync(
                    settings.ExchangeRoot,
                    key,
                    new TcpIntegrationEndpoint(settings.PeerHost, settings.PeerPort),
                    transaction.Handoff.TransactionId,
                    cancellationToken),
            refreshAfterTransfer: true);
    }

    internal void RefreshCommandState()
    {
        OnPropertyChanged(nameof(CanPushLatestTransaction));
        OnPropertyChanged(nameof(CanPullLatestTransaction));
        OnPropertyChanged(nameof(CanEditTcpSetup));
        StartTcpListenerCommand.RaiseCanExecuteChanged();
        StopTcpListenerCommand.RaiseCanExecuteChanged();
        PingTcpPeerCommand.RaiseCanExecuteChanged();
        PushLatestTransactionCommand.RaiseCanExecuteChanged();
        PullLatestTransactionCommand.RaiseCanExecuteChanged();
    }

    private Task RunTcpTransferAsync(
        string busyStatus,
        Func<MachineIntegrationTcpSettings, byte[], CancellationToken, Task<TcpIntegrationTransferReceipt>> operation,
        bool refreshAfterTransfer) =>
        RunTcpOperationAsync(
            busyStatus,
            async cancellationToken =>
            {
                var settings = _settingsProvider();
                var key = AcquireSharedKey();
                try
                {
                    var receipt = await operation(settings, key, cancellationToken)
                        .ConfigureAwait(false);
                    LastTcpTransferText = string.Format(
                        CultureInfo.CurrentCulture,
                        L(
                            "TcpTransferFormat",
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
                        await _refreshResults().ConfigureAwait(true);
                    }

                    _setStatus(LastTcpTransferText);
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
        if (_disposed)
        {
            _setStatus(L(
                "TcpDisposed",
                "종료 중인 연동 화면에서는 TCP 작업을 시작할 수 없습니다.",
                "A TCP action cannot start while the integration workspace is closing."));
            return;
        }

        if (Interlocked.CompareExchange(ref _tcpOperationActive, 1, 0) != 0)
        {
            return;
        }

        if (_disposed)
        {
            Interlocked.Exchange(ref _tcpOperationActive, 0);
            return;
        }

        IsTcpBusy = true;
        _setStatus(busyStatus);
        using var cancellation = new CancellationTokenSource();
        _tcpOperationCancellation = cancellation;
        try
        {
            await operation(cancellation.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            _setStatus(L("TcpCancelled", "TCP 작업을 취소했습니다.", "TCP action cancelled."));
        }
        catch (Exception exception)
        {
            _setStatus(exception.Message);
        }
        finally
        {
            if (ReferenceEquals(_tcpOperationCancellation, cancellation))
            {
                _tcpOperationCancellation = null;
            }

            IsTcpBusy = false;
            Interlocked.Exchange(ref _tcpOperationActive, 0);
        }
    }

    private byte[] AcquireSharedKey()
    {
        var key = _sharedKeyStore.TryAcquire();
        return key ?? throw new InvalidOperationException(DescribeSharedKeyAcquireError());
    }

    private string DescribeSharedKeyStatus() =>
        DescribeSharedKeyStatus(_sharedKeyStore.Status);

    private string DescribeSharedKeyStatus(MachineIntegrationSharedKeyStatus status) =>
        status switch
        {
            MachineIntegrationSharedKeyStatus.Missing => string.Format(
                CultureInfo.CurrentCulture,
                L(
                    "TcpEnvironmentKeyMissing",
                    "공유 키 없음: 세션 입력 또는 환경 변수 {0} 필요",
                    "No shared key: session input or environment variable {0} required"),
                MachineIntegrationSharedKeyStore.EnvironmentVariableName),
            MachineIntegrationSharedKeyStatus.SessionReady => L(
                "TcpSessionKeyReady",
                "세션 공유 키 준비됨(저장되지 않음)",
                "Session shared key ready (not saved)"),
            MachineIntegrationSharedKeyStatus.SessionTooShort => L(
                "TcpKeyTooShort",
                "세션 공유 키는 Base64로 인코딩한 32바이트 이상이어야 합니다.",
                "The session shared key must be Base64-encoded and contain at least 32 bytes."),
            MachineIntegrationSharedKeyStatus.SessionMalformed => L(
                "TcpKeyInvalidBase64",
                "세션 공유 키가 올바른 Base64가 아닙니다.",
                "The session shared key is not valid Base64."),
            MachineIntegrationSharedKeyStatus.EnvironmentReady => string.Format(
                CultureInfo.CurrentCulture,
                L(
                    "TcpEnvironmentKeyReady",
                    "환경 변수 {0}의 공유 키 준비됨",
                    "Shared key ready from environment variable {0}"),
                MachineIntegrationSharedKeyStore.EnvironmentVariableName),
            MachineIntegrationSharedKeyStatus.EnvironmentTooShort => string.Format(
                CultureInfo.CurrentCulture,
                L(
                    "TcpEnvironmentKeyShort",
                    "환경 변수 {0}의 공유 키가 32바이트보다 짧습니다.",
                    "Shared key in environment variable {0} is shorter than 32 bytes."),
                MachineIntegrationSharedKeyStore.EnvironmentVariableName),
            MachineIntegrationSharedKeyStatus.EnvironmentMalformed => string.Format(
                CultureInfo.CurrentCulture,
                L(
                    "TcpEnvironmentKeyMalformed",
                    "환경 변수 {0}의 공유 키가 올바른 Base64가 아닙니다.",
                    "Shared key in environment variable {0} is not valid Base64."),
                MachineIntegrationSharedKeyStore.EnvironmentVariableName),
            _ => throw new ArgumentOutOfRangeException(nameof(status), status, null)
        };

    private string DescribeSharedKeyAcquireError() =>
        _sharedKeyStore.Status switch
        {
            MachineIntegrationSharedKeyStatus.Missing => string.Format(
                CultureInfo.CurrentCulture,
                L(
                    "TcpKeyRequired",
                    "세션 공유 키를 입력하거나 환경 변수 {0}에 Base64 키를 설정하세요.",
                    "Enter a session shared key or set environment variable {0} to a Base64 key."),
                MachineIntegrationSharedKeyStore.EnvironmentVariableName),
            MachineIntegrationSharedKeyStatus.SessionTooShort => DescribeSharedKeyStatus(
                MachineIntegrationSharedKeyStatus.SessionTooShort),
            MachineIntegrationSharedKeyStatus.SessionMalformed => DescribeSharedKeyStatus(
                MachineIntegrationSharedKeyStatus.SessionMalformed),
            _ => string.Format(
                CultureInfo.CurrentCulture,
                L(
                    "TcpEnvironmentKeyInvalid",
                    "환경 변수 {0}에는 Base64로 인코딩한 32바이트 이상의 키가 필요합니다.",
                    "Environment variable {0} must contain a Base64 key of at least 32 bytes."),
                MachineIntegrationSharedKeyStore.EnvironmentVariableName)
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
        _tcpOperationCancellation?.Cancel();
        IsTcpListening = false;
        TcpListenerStatusText = L(
            "TcpStopped",
            "TCP 수신 중지됨",
            "TCP listener stopped");
        _sharedKeyStore.Dispose();
        _tcpWorkflow.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
