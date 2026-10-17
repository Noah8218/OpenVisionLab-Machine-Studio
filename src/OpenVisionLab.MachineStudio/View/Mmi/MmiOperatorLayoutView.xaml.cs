using System.Windows.Controls;

namespace OpenVisionLab.MachineStudio.View.Mmi;

public partial class MmiOperatorLayoutView : UserControl
{
    public MmiOperatorLayoutView() => InitializeComponent();

    public Expander IntegrationExchangeDetailsExpanderControl => IntegrationExchangeDetailsExpander;
    public TextBlock IntegrationWorkspaceStatusTextBlockControl => IntegrationWorkspaceStatusText;
    public TextBlock IntegrationHandoffStatusTextBlockControl => IntegrationHandoffStatusText;
    public TextBlock IntegrationAcknowledgementStatusTextBlockControl => IntegrationAcknowledgementStatusText;
    public TextBlock IntegrationResultStatusTextBlockControl => IntegrationResultStatusText;
    public TextBlock IntegrationProjectionStatusTextBlockControl => IntegrationProjectionStatusText;
    public TextBlock IntegrationLastTransactionTextBlockControl => IntegrationLastTransactionText;
    public Button RefreshIntegrationResultsButtonControl => MmiRefreshButton;
    public Button ApplyIntegrationResultToSimulationButtonControl => MmiApplyResultButton;
    public TextBlock IntegrationSetupWorkflowStatusTextBlockControl => IntegrationSetupStatusTextBlock;
    public StackPanel IntegrationTransactionHistorySectionControl => IntegrationTransactionHistorySection;
    public ComboBox IntegrationTransactionHistoryFilterControl => IntegrationTransactionHistoryFilter;
    public TextBlock IntegrationTransactionHistoryEmptyTextBlockControl => IntegrationTransactionHistoryEmptyText;
    public ListBox IntegrationTransactionHistoryListControl => IntegrationTransactionHistoryList;
    public StackPanel IntegrationTransactionDiagnosticsSectionControl => IntegrationTransactionDiagnosticsSection;
    public ComboBox IntegrationTransactionDiagnosticsFilterControl => IntegrationTransactionDiagnosticsFilter;
    public TextBlock IntegrationTransactionDiagnosticsReadErrorTextBlockControl => IntegrationTransactionDiagnosticsReadErrorText;
    public TextBlock IntegrationTransactionDiagnosticsEmptyTextBlockControl => IntegrationTransactionDiagnosticsEmptyText;
    public ListBox IntegrationTransactionDiagnosticsListControl => IntegrationTransactionDiagnosticsList;
}
