using System.Windows.Controls;

namespace OpenVisionLab.MachineStudio.View.Simulation;

public partial class SimulationIntegrationWorkspaceView : UserControl
{
    public SimulationIntegrationWorkspaceView()
    {
        InitializeComponent();
    }

    public TextBlock IntegrationWorkspaceStatusTextBlockControl => IntegrationWorkspaceStatusText;
    public TextBlock IntegrationHandoffStatusTextBlockControl => IntegrationHandoffStatusText;
    public TextBlock IntegrationAcknowledgementStatusTextBlockControl => IntegrationAcknowledgementStatusText;
    public TextBlock IntegrationResultStatusTextBlockControl => IntegrationResultStatusText;
    public TextBlock IntegrationProjectionStatusTextBlockControl => IntegrationProjectionStatusText;
    public TextBlock IntegrationLastTransactionTextBlockControl => IntegrationLastTransactionText;
    public Button RefreshIntegrationResultsButtonControl => RefreshIntegrationResultsButton;
    public Button ApplyIntegrationResultToSimulationButtonControl => ApplyIntegrationResultToSimulationButton;
    public TextBlock IntegrationSetupWorkflowStatusTextBlockControl => IntegrationSetupWorkflowStatusText;
    public Expander IntegrationTransactionHistoryExpanderControl => IntegrationTransactionHistoryExpander;
    public ComboBox IntegrationTransactionHistoryFilterControl => IntegrationTransactionHistoryFilter;
    public TextBlock IntegrationTransactionHistoryEmptyTextBlockControl => IntegrationTransactionHistoryEmptyText;
    public ListBox IntegrationTransactionHistoryListControl => IntegrationTransactionHistoryList;
    public Expander IntegrationTransactionDiagnosticsExpanderControl => IntegrationTransactionDiagnosticsExpander;
    public ComboBox IntegrationTransactionDiagnosticsFilterControl => IntegrationTransactionDiagnosticsFilter;
    public TextBlock IntegrationTransactionDiagnosticsReadErrorTextBlockControl => IntegrationTransactionDiagnosticsReadErrorText;
    public TextBlock IntegrationTransactionDiagnosticsEmptyTextBlockControl => IntegrationTransactionDiagnosticsEmptyText;
    public ListBox IntegrationTransactionDiagnosticsListControl => IntegrationTransactionDiagnosticsList;
}
