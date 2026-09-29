using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using OpenVisionLab.MachineStudio.View.Inspector;
using OpenVisionLab.MachineStudio.View.Shell;
using OpenVisionLab.MachineStudio.View.Simulation;
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
        Func<DependencyObject, RightToolRegionView?> findInspector,
        Func<DependencyObject, SimulationIntegrationWorkspaceView?> findIntegrationWorkspace)
    {
        if (!viewModel.IsRunMode)
        {
            throw new ArgumentException(
                "--smoke-integration-panel-state requires --smoke-run-layout.");
        }

        state = state.ToLowerInvariant();
        if (state is not ("visible" or "status" or "result" or "rejected" or "invalid" or "history" or "tcp" or "tcp-bottom"))
        {
            throw new ArgumentException(
                $"Unsupported --smoke-integration-panel-state '{state}'. " +
                "Expected visible, status, result, rejected, invalid, history, tcp, or tcp-bottom.");
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

            viewModel.Integration.Setup.ExchangeRoot = fullExchangeRoot;
        }

        if (state is "result" or "rejected" or "invalid" or "history")
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
                    && (state == "history"
                        ? viewModel.Integration.TransactionHistory.Count > 0
                        && viewModel.Integration.TransactionDiagnostics.Count > 0
                        : state == "invalid"
                        ? viewModel.Integration.ResultStatusText.Contains(
                            "Result read failed",
                            StringComparison.Ordinal)
                        || viewModel.Integration.ResultStatusText.Contains(
                            "결과 읽기 실패",
                            StringComparison.Ordinal)
                        : viewModel.Integration.ResultStatusText.Contains("Run ", StringComparison.Ordinal)))
                {
                    break;
                }

                await Task.Delay(50);
            }
        }

        viewModel.Navigation.IsInspectionWorkspace = true;
        viewModel.Navigation.SelectedInspectionTabIndex = 1;
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        var inspector = findInspector(window)
            ?? throw new InvalidOperationException("Run inspector was unavailable.");
        var integrationWorkspace = findIntegrationWorkspace(window)
            ?? throw new InvalidOperationException("Integration workspace was unavailable.");
        FrameworkElement target = state switch
        {
            "result" or "rejected" or "invalid" => integrationWorkspace.IntegrationResultStatusTextBlockControl,
            "history" => integrationWorkspace.IntegrationTransactionHistoryExpanderControl,
            "status" => integrationWorkspace.IntegrationWorkspaceStatusTextBlockControl,
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
                viewModel.Integration.Setup.ExchangeRoot,
                Path.GetFullPath(exchangeRoot),
                StringComparison.OrdinalIgnoreCase));
        Check("central-workspace-status-rendered",
            integrationWorkspace.IntegrationWorkspaceStatusTextBlockControl.IsVisible
            && integrationWorkspace.IntegrationWorkspaceStatusTextBlockControl.ActualWidth > 0
            && integrationWorkspace.IntegrationWorkspaceStatusTextBlockControl.ActualHeight > 0);
        Check("central-workspace-status-bound",
            string.Equals(
                integrationWorkspace.IntegrationWorkspaceStatusTextBlockControl.Text,
                viewModel.Integration.StatusText,
                StringComparison.Ordinal));
        Check("central-handoff-status-bound",
            string.Equals(
                integrationWorkspace.IntegrationHandoffStatusTextBlockControl.Text,
                viewModel.Integration.HandoffStatusText,
                StringComparison.Ordinal));
        Check("central-acknowledgement-status-bound",
            string.Equals(
                integrationWorkspace.IntegrationAcknowledgementStatusTextBlockControl.Text,
                viewModel.Integration.AcknowledgementStatusText,
                StringComparison.Ordinal));
        Check("central-result-status-bound",
            string.Equals(
                integrationWorkspace.IntegrationResultStatusTextBlockControl.Text,
                viewModel.Integration.ResultStatusText,
                StringComparison.Ordinal));
        Check("central-projection-status-bound",
            string.Equals(
                integrationWorkspace.IntegrationProjectionStatusTextBlockControl.Text,
                viewModel.Integration.ProjectionStatusText,
                StringComparison.Ordinal));
        Check("central-last-transaction-bound",
            string.Equals(
                integrationWorkspace.IntegrationLastTransactionTextBlockControl.Text,
                viewModel.Integration.LastTransactionText,
                StringComparison.Ordinal));
        Check("setup-workflow-status-rendered",
            integrationWorkspace.IntegrationSetupWorkflowStatusTextBlockControl.IsVisible
            && integrationWorkspace.IntegrationSetupWorkflowStatusTextBlockControl.ActualWidth > 0
            && integrationWorkspace.IntegrationSetupWorkflowStatusTextBlockControl.ActualHeight > 0);
        Check("setup-workflow-status-bound",
            string.Equals(
                integrationWorkspace.IntegrationSetupWorkflowStatusTextBlockControl.Text,
                viewModel.Integration.SetupWorkflowStatusText,
                StringComparison.Ordinal));
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
        Check("external-result-wait-option-rendered",
            inspector.IntegrationWaitForExternalResultCheckBoxControl.IsVisible
            && inspector.IntegrationWaitForExternalResultCheckBoxControl.ActualWidth > 0
            && inspector.IntegrationWaitForExternalResultCheckBoxControl.ActualHeight > 0);
        Check("external-result-wait-option-bound",
            inspector.IntegrationWaitForExternalResultCheckBoxControl.IsChecked
                == viewModel.Integration.Setup.WaitForExternalResult);
        Check("apply-result-command-rendered",
            integrationWorkspace.ApplyIntegrationResultToSimulationButtonControl.IsVisible
            && integrationWorkspace.ApplyIntegrationResultToSimulationButtonControl.ActualWidth > 0
            && integrationWorkspace.ApplyIntegrationResultToSimulationButtonControl.ActualHeight > 0);
        Check("apply-result-command-state-bound",
            integrationWorkspace.ApplyIntegrationResultToSimulationButtonControl.IsEnabled
                == viewModel.Integration.CanApplyResultToSimulation);

        if (state == "result")
        {
            var resultText = viewModel.Integration.ResultStatusText;
            Check("result-apply-disabled-without-current-process-handoff",
                !viewModel.Integration.CanApplyResultToSimulation
                && !integrationWorkspace.ApplyIntegrationResultToSimulationButtonControl.IsEnabled);
            Check("result-row-binding-current",
                string.Equals(
                    integrationWorkspace.IntegrationResultStatusTextBlockControl.Text,
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
                IsReadOnlyRefreshStatus(viewModel.Integration.StatusText));
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
                IsReadOnlyRefreshStatus(viewModel.Integration.StatusText));
        }
        else if (state == "invalid")
        {
            var handoffText = viewModel.Integration.HandoffStatusText;
            var resultText = viewModel.Integration.ResultStatusText;
            Check(
                "result-read-failed",
                resultText.Contains("Result read failed", StringComparison.Ordinal)
                || resultText.Contains("결과 읽기 실패", StringComparison.Ordinal));
            Check(
                "handoff-read-failed",
                handoffText.Contains("Result read failed", StringComparison.Ordinal)
                || handoffText.Contains("결과 읽기 실패", StringComparison.Ordinal));
            Check(
                "published-wording-absent",
                !handoffText.Contains("Result published", StringComparison.Ordinal)
                && !handoffText.Contains("결과 게시됨", StringComparison.Ordinal));
            Check(
                "acknowledgement-accepted",
                viewModel.Integration.AcknowledgementStatusText.Contains(
                    "Accepted",
                    StringComparison.Ordinal)
                || viewModel.Integration.AcknowledgementStatusText.Contains(
                    "승인",
                    StringComparison.Ordinal));
            Check(
                "refresh-remained-read-only",
                IsReadOnlyRefreshStatus(viewModel.Integration.StatusText));
        }
        else if (state == "history")
        {
            integrationWorkspace.IntegrationTransactionHistoryExpanderControl.IsExpanded = true;
            integrationWorkspace.IntegrationTransactionDiagnosticsExpanderControl.IsExpanded = true;
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            integrationWorkspace.IntegrationTransactionHistoryListControl.BringIntoView();
            integrationWorkspace.IntegrationTransactionDiagnosticsListControl.BringIntoView();
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Check(
                "history-expander-expanded",
                integrationWorkspace.IntegrationTransactionHistoryExpanderControl.IsExpanded);
            Check(
                "diagnostics-expander-expanded",
                integrationWorkspace.IntegrationTransactionDiagnosticsExpanderControl.IsExpanded);
            var diagnosticsFilter = integrationWorkspace.IntegrationTransactionDiagnosticsFilterControl;
            Check(
                "diagnostics-filter-visible",
                diagnosticsFilter.IsVisible
                && diagnosticsFilter.ActualWidth > 0
                && diagnosticsFilter.ActualHeight > 0);
            Check(
                "diagnostics-filter-options",
                diagnosticsFilter.Items.Count == viewModel.Integration.TransactionDiagnosticFilters.Count
                && diagnosticsFilter.Items.Count == 5);
            var allDiagnosticsFilter = viewModel.Integration.TransactionDiagnosticFilters.Single(item => item.State is null);
            diagnosticsFilter.SelectedItem = allDiagnosticsFilter;
            diagnosticsFilter.Focus();
            diagnosticsFilter.IsDropDownOpen = true;
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Check(
                "diagnostics-filter-all-two-way",
                ReferenceEquals(viewModel.Integration.SelectedTransactionDiagnosticFilter, allDiagnosticsFilter));
            Check("diagnostics-filter-popup-open", diagnosticsFilter.IsDropDownOpen);
            Check("diagnostics-filter-keyboard-focus", diagnosticsFilter.IsKeyboardFocusWithin);
            diagnosticsFilter.IsDropDownOpen = false;
            var historyFilter = integrationWorkspace.IntegrationTransactionHistoryFilterControl;
            Check(
                "history-filter-visible",
                historyFilter.IsVisible
                && historyFilter.ActualWidth > 0
                && historyFilter.ActualHeight > 0);
            Check(
                "history-filter-options",
                historyFilter.Items.Count == viewModel.Integration.TransactionHistoryFilters.Count
                && historyFilter.Items.Count == 6);
            var allHistoryFilter = viewModel.Integration.TransactionHistoryFilters.Single(item => item.State is null);
            historyFilter.SelectedItem = allHistoryFilter;
            historyFilter.Focus();
            historyFilter.IsDropDownOpen = true;
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Check(
                "history-filter-all-two-way",
                ReferenceEquals(viewModel.Integration.SelectedTransactionHistoryFilter, allHistoryFilter));
            Check("history-filter-popup-open", historyFilter.IsDropDownOpen);
            Check("history-filter-keyboard-focus", historyFilter.IsKeyboardFocusWithin);
            historyFilter.IsDropDownOpen = false;
            Check(
                "history-items-present",
                integrationWorkspace.IntegrationTransactionHistoryListControl.Items.Count > 0
                && integrationWorkspace.IntegrationTransactionHistoryListControl.Items.Count
                    == viewModel.Integration.TransactionHistory.Count
                && integrationWorkspace.IntegrationTransactionHistoryListControl.Items.Count
                    == viewModel.Integration.TransactionHistoryRows.Count
                && integrationWorkspace.IntegrationTransactionHistoryListControl.Items.Count
                    == viewModel.Integration.VisibleTransactionHistoryRows.Count);
            Check(
                "history-status-projection",
                viewModel.Integration.TransactionHistoryRows.Count
                    == viewModel.Integration.TransactionHistory.Count
                && viewModel.Integration.TransactionHistoryRows.All(row =>
                    !string.IsNullOrWhiteSpace(row.StatusText)));
            MachineIntegrationTransactionHistoryFilterItem? populatedHistoryFilter = null;
            MachineIntegrationTransactionHistoryFilterItem? emptyHistoryFilter = null;
            foreach (var candidate in viewModel.Integration.TransactionHistoryFilters.Where(item => item.State is not null))
            {
                historyFilter.SelectedItem = candidate;
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                if (viewModel.Integration.VisibleTransactionHistoryRows.Count > 0
                    && populatedHistoryFilter is null)
                {
                    populatedHistoryFilter = candidate;
                }

                if (viewModel.Integration.VisibleTransactionHistoryRows.Count == 0
                    && emptyHistoryFilter is null)
                {
                    emptyHistoryFilter = candidate;
                }
            }

            if (populatedHistoryFilter is not null)
            {
                historyFilter.SelectedItem = populatedHistoryFilter;
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                Check(
                    "history-filter-state-applies",
                    viewModel.Integration.VisibleTransactionHistoryRows.Count > 0
                    && integrationWorkspace.IntegrationTransactionHistoryListControl.Items.Count
                        == viewModel.Integration.VisibleTransactionHistoryRows.Count
                    && ReferenceEquals(
                        viewModel.Integration.SelectedTransactionHistoryFilter,
                        populatedHistoryFilter));
            }
            else
            {
                Check("history-filter-state-applies", false);
            }

            if (emptyHistoryFilter is not null)
            {
                historyFilter.SelectedItem = emptyHistoryFilter;
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                Check(
                    "history-filter-empty-state",
                    viewModel.Integration.VisibleTransactionHistoryRows.Count == 0
                    && viewModel.Integration.HasTransactionHistoryEmptyState
                    && integrationWorkspace.IntegrationTransactionHistoryListControl.Items.Count == 0
                    && integrationWorkspace.IntegrationTransactionHistoryEmptyTextBlockControl.IsVisible);
            }
            else
            {
                Check("history-filter-empty-state", false);
            }

            historyFilter.SelectedItem = allHistoryFilter;
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Check(
                "diagnostics-items-present",
                integrationWorkspace.IntegrationTransactionDiagnosticsListControl.Items.Count > 0
                && integrationWorkspace.IntegrationTransactionDiagnosticsListControl.Items.Count
                    == viewModel.Integration.TransactionDiagnostics.Count);
            var availableDiagnosticStates = viewModel.Integration.TransactionDiagnostics
                .Select(diagnostic => diagnostic.State)
                .ToHashSet();
            var populatedDiagnosticsFilter = viewModel.Integration.TransactionDiagnosticFilters.FirstOrDefault(item =>
                item.State is { } state && availableDiagnosticStates.Contains(state));
            if (populatedDiagnosticsFilter is not null)
            {
                diagnosticsFilter.SelectedItem = populatedDiagnosticsFilter;
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                Check(
                    "diagnostics-filter-state-applies",
                    viewModel.Integration.VisibleTransactionDiagnostics.Count > 0
                    && viewModel.Integration.VisibleTransactionDiagnostics.All(diagnostic =>
                        diagnostic.State == populatedDiagnosticsFilter.State)
                    && integrationWorkspace.IntegrationTransactionDiagnosticsListControl.Items.Count
                        == viewModel.Integration.VisibleTransactionDiagnostics.Count);
            }
            else
            {
                Check("diagnostics-filter-state-applies", false);
            }
            var emptyDiagnosticsFilter = viewModel.Integration.TransactionDiagnosticFilters.FirstOrDefault(item =>
                item.State is { } state && !availableDiagnosticStates.Contains(state));
            if (emptyDiagnosticsFilter is not null)
            {
                diagnosticsFilter.SelectedItem = emptyDiagnosticsFilter;
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                Check(
                    "diagnostics-filter-empty-state",
                    viewModel.Integration.VisibleTransactionDiagnostics.Count == 0
                    && viewModel.Integration.HasTransactionDiagnosticsEmptyState
                    && integrationWorkspace.IntegrationTransactionDiagnosticsListControl.Items.Count == 0
                    && integrationWorkspace.IntegrationTransactionDiagnosticsEmptyTextBlockControl.IsVisible);
            }
            else
            {
                Check("diagnostics-filter-empty-state", false);
            }
            diagnosticsFilter.SelectedItem = allDiagnosticsFilter;
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Check(
                "history-item-visible",
                integrationWorkspace.IntegrationTransactionHistoryListControl.ItemContainerGenerator.ContainerFromIndex(0)
                    is FrameworkElement historyItem
                && historyItem.IsVisible);
            Check(
                "history-status-rendered",
                integrationWorkspace.IntegrationTransactionHistoryListControl.ItemContainerGenerator.ContainerFromIndex(0)
                    is FrameworkElement historyStatusItem
                && SmokeVisualTreeQuery.FindVisualDescendant<TextBlock>(
                    historyStatusItem,
                    textBlock => string.Equals(
                        textBlock.Text,
                        viewModel.Integration.TransactionHistoryRows[0].StatusText,
                        StringComparison.Ordinal)) is not null);
            var historyContainer = integrationWorkspace.IntegrationTransactionHistoryListControl
                .ItemContainerGenerator.ContainerFromIndex(0) as FrameworkElement;
            var historyRow = viewModel.Integration.TransactionHistoryRows.FirstOrDefault();
            var shortTransactionIdBlock = historyRow is { } row && historyContainer is not null
                ? SmokeVisualTreeQuery.FindVisualDescendant<TextBlock>(
                    historyContainer,
                    textBlock => string.Equals(
                        textBlock.Text,
                        row.ShortTransactionId,
                        StringComparison.Ordinal))
                : null;
            Check(
                "history-short-id-rendered",
                shortTransactionIdBlock is not null
                && shortTransactionIdBlock.Text.StartsWith("TX-", StringComparison.Ordinal)
                && shortTransactionIdBlock.Text.Length == 11);
            Check(
                "history-full-id-tooltip",
                shortTransactionIdBlock is not null
                && historyRow is { } fullIdRow
                && string.Equals(
                    shortTransactionIdBlock.ToolTip as string,
                    fullIdRow.FullTransactionId,
                    StringComparison.Ordinal)
                && string.Equals(
                    fullIdRow.FullTransactionId,
                    fullIdRow.Handoff.TransactionId.ToString("D"),
                    StringComparison.Ordinal));
            Check(
                "diagnostics-item-visible",
                integrationWorkspace.IntegrationTransactionDiagnosticsListControl.ItemContainerGenerator.ContainerFromIndex(0)
                    is FrameworkElement diagnosticsItem
                && diagnosticsItem.IsVisible);
            Check(
                "history-remained-read-only",
                IsReadOnlyRefreshStatus(viewModel.Integration.StatusText));
        }

        return new SmokeIntegrationResultReport
        {
            State = state,
            ExchangeRoot = viewModel.Integration.Setup.ExchangeRoot,
            AcknowledgementStatusText = viewModel.Integration.AcknowledgementStatusText,
            ResultStatusText = viewModel.Integration.ResultStatusText,
            StatusText = viewModel.Integration.StatusText,
            Checks = checks,
            Failures = failures,
            Monitor = monitor
        };
    }

    private static bool IsReadOnlyRefreshStatus(string statusText) =>
        statusText.Contains("No inspection was run", StringComparison.Ordinal)
        || statusText.Contains("검사를 실행하지 않았습니다", StringComparison.Ordinal)
        || statusText.Contains("실행은 하지 않았습니다", StringComparison.Ordinal);
}
