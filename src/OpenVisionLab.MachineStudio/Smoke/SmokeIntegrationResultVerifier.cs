using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using OpenVisionLab.MachineStudio.View.Inspector;
using OpenVisionLab.MachineStudio.View.Shell;
using OpenVisionLab.MachineStudio.ViewModel;

namespace OpenVisionLab.MachineStudio;

internal sealed class SmokeIntegrationResultReport
{
    public string Schema { get; init; } = "1.0";
    public DateTimeOffset CapturedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public required string State { get; init; }
    public required string ExchangeRoot { get; init; }
    public required string AcknowledgementStatusText { get; init; }
    public required string ResultStatusText { get; init; }
    public required string StatusText { get; init; }
    public required IReadOnlyDictionary<string, bool> Checks { get; init; }
    public required IReadOnlyList<string> Failures { get; init; }
    public required SmokeMonitorEvidence Monitor { get; init; }
    public bool IsValid => Failures.Count == 0 && Checks.Values.All(value => value);

    public void Save(string path)
    {
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(
            fullPath,
            JsonSerializer.Serialize(this, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = true
            }));
    }
}

internal static class SmokeIntegrationResultVerifier
{
    public static async Task<SmokeIntegrationResultReport> VerifyAsync(
        ShellWindow window,
        MainViewModel viewModel,
        string state,
        string? exchangeRoot,
        Func<DependencyObject, RightToolRegionView?> findInspector)
    {
        if (!viewModel.IsRunMode)
        {
            throw new ArgumentException(
                "--smoke-integration-panel-state requires --smoke-run-layout.");
        }

        state = state.ToLowerInvariant();
        if (state is not ("visible" or "status" or "result" or "rejected" or "tcp" or "tcp-bottom"))
        {
            throw new ArgumentException(
                $"Unsupported --smoke-integration-panel-state '{state}'. " +
                "Expected visible, status, result, rejected, tcp, or tcp-bottom.");
        }

        var checks = new Dictionary<string, bool>(StringComparer.Ordinal);
        var failures = new List<string>();
        void Check(string name, bool passed)
        {
            checks[name] = passed;
            if (!passed)
            {
                failures.Add(name);
            }
        }

        if (!string.IsNullOrWhiteSpace(exchangeRoot))
        {
            var fullExchangeRoot = Path.GetFullPath(exchangeRoot);
            if (!Directory.Exists(fullExchangeRoot))
            {
                throw new DirectoryNotFoundException(
                    $"The integration exchange root was not found: {fullExchangeRoot}");
            }

            viewModel.Integration.ExchangeRoot = fullExchangeRoot;
        }

        if (state is "result" or "rejected")
        {
            if (string.IsNullOrWhiteSpace(exchangeRoot))
            {
                throw new ArgumentException(
                    "The result state requires --smoke-integration-exchange-root.");
            }

            if (!viewModel.Integration.RefreshResultsCommand.CanExecute(null))
            {
                throw new InvalidOperationException(
                    "Refresh Result was unavailable for the supplied exchange root.");
            }

            viewModel.Integration.RefreshResultsCommand.Execute(null);
            for (var attempt = 0; attempt < 120; attempt++)
            {
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                if (!viewModel.Integration.IsBusy
                    && viewModel.Integration.ResultStatusText.Contains("Run ", StringComparison.Ordinal))
                {
                    break;
                }

                await Task.Delay(50);
            }
        }

        var inspector = findInspector(window)
            ?? throw new InvalidOperationException("Run inspector was unavailable.");
        FrameworkElement target = state switch
        {
            "result" or "rejected" => inspector.MachineIntegrationResultStatusTextBlock,
            "status" => inspector.MachineIntegrationStatusTextBlock,
            "tcp" => inspector.IntegrationTcpListenAddressTextBoxControl,
            "tcp-bottom" => inspector.IntegrationTcpTransferStatusTextBlockControl,
            _ => inspector.IntegrationExchangeRootTextBox
        };
        target.BringIntoView();
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        await Task.Delay(100);

        var monitor = SmokeDpiTestHook.CaptureMonitorEvidence(window);
        Check("target-visible", target.IsVisible && target.ActualWidth > 0 && target.ActualHeight > 0);
        Check("window-intersects-selected-monitor", monitor.WindowIntersectsMonitor);
        Check("exchange-root-observed", string.IsNullOrWhiteSpace(exchangeRoot)
            || string.Equals(
                viewModel.Integration.ExchangeRoot,
                Path.GetFullPath(exchangeRoot),
                StringComparison.OrdinalIgnoreCase));
        Check("tcp-listen-address-rendered",
            inspector.IntegrationTcpListenAddressTextBoxControl.IsVisible
            && inspector.IntegrationTcpListenAddressTextBoxControl.ActualWidth > 0
            && inspector.IntegrationTcpListenAddressTextBoxControl.ActualHeight > 0);
        Check("tcp-listen-port-rendered",
            inspector.IntegrationTcpListenPortTextBoxControl.IsVisible
            && inspector.IntegrationTcpListenPortTextBoxControl.ActualWidth > 0
            && inspector.IntegrationTcpListenPortTextBoxControl.ActualHeight > 0);
        Check("tcp-peer-host-rendered",
            inspector.IntegrationTcpPeerHostTextBoxControl.IsVisible
            && inspector.IntegrationTcpPeerHostTextBoxControl.ActualWidth > 0
            && inspector.IntegrationTcpPeerHostTextBoxControl.ActualHeight > 0);
        Check("tcp-peer-port-rendered",
            inspector.IntegrationTcpPeerPortTextBoxControl.IsVisible
            && inspector.IntegrationTcpPeerPortTextBoxControl.ActualWidth > 0
            && inspector.IntegrationTcpPeerPortTextBoxControl.ActualHeight > 0);
        Check("tcp-shared-key-rendered",
            inspector.IntegrationTcpSharedKeyBoxControl.IsVisible
            && inspector.IntegrationTcpSharedKeyBoxControl.ActualWidth > 0
            && inspector.IntegrationTcpSharedKeyBoxControl.ActualHeight > 0);
        Check("tcp-transfer-command-state-bound",
            inspector.PushIntegrationTcpTransactionButtonControl.IsEnabled
                == viewModel.Integration.CanPushLatestTransaction
            && inspector.PullIntegrationTcpTransactionButtonControl.IsEnabled
                == viewModel.Integration.CanPullLatestTransaction);
        Check("tcp-listener-status-bound",
            string.Equals(
                inspector.IntegrationTcpListenerStatusTextBlockControl.Text,
                viewModel.Integration.TcpListenerStatusText,
                StringComparison.Ordinal));
        Check("tcp-transfer-status-bound",
            string.Equals(
                inspector.IntegrationTcpTransferStatusTextBlockControl.Text,
                viewModel.Integration.LastTcpTransferText,
                StringComparison.Ordinal));

        if (state == "result")
        {
            var resultText = viewModel.Integration.ResultStatusText;
            Check("result-row-binding-current",
                string.Equals(
                    inspector.MachineIntegrationResultStatusTextBlock.Text,
                    resultText,
                    StringComparison.Ordinal));
            Check("result-outcome-pass", resultText.Contains("Pass", StringComparison.Ordinal));
            Check("result-status-completed", resultText.Contains("Completed", StringComparison.Ordinal));
            Check("result-run-id-visible", resultText.Contains("Run ", StringComparison.Ordinal));
            Check("acknowledgement-accepted",
                viewModel.Integration.AcknowledgementStatusText.Contains(
                    "Accepted",
                    StringComparison.Ordinal));
            Check("refresh-remained-read-only",
                viewModel.Integration.StatusText.Contains(
                    "No inspection was run.",
                    StringComparison.Ordinal)
                || viewModel.Integration.StatusText.Contains(
                    "검사를 실행하지 않았습니다.",
                    StringComparison.Ordinal));
        }
        else if (state == "rejected")
        {
            var acknowledgementText = viewModel.Integration.AcknowledgementStatusText;
            var handoffText = viewModel.Integration.HandoffStatusText;
            var resultText = viewModel.Integration.ResultStatusText;
            Check(
                "acknowledgement-rejected",
                acknowledgementText.Contains("Rejected", StringComparison.OrdinalIgnoreCase)
                || acknowledgementText.Contains("거절", StringComparison.Ordinal));
            Check(
                "handoff-rejected",
                handoffText.Contains("Rejected", StringComparison.OrdinalIgnoreCase)
                || handoffText.Contains("거절", StringComparison.Ordinal));
            Check(
                "result-absent",
                resultText.Contains("No validated Result", StringComparison.Ordinal)
                || resultText.Contains("검증된 Result가 없습니다", StringComparison.Ordinal));
            Check(
                "refresh-remained-read-only",
                viewModel.Integration.StatusText.Contains("No inspection was run", StringComparison.Ordinal)
                || viewModel.Integration.StatusText.Contains("검사를 실행하지 않았습니다", StringComparison.Ordinal));
        }

        return new SmokeIntegrationResultReport
        {
            State = state,
            ExchangeRoot = viewModel.Integration.ExchangeRoot,
            AcknowledgementStatusText = viewModel.Integration.AcknowledgementStatusText,
            ResultStatusText = viewModel.Integration.ResultStatusText,
            StatusText = viewModel.Integration.StatusText,
            Checks = checks,
            Failures = failures,
            Monitor = monitor
        };
    }
}
