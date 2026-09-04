using System.Windows;
using System.Windows.Controls;
using OpenVisionLab.MachineStudio.ViewModel;

namespace OpenVisionLab.MachineStudio.View.Inspector;

public partial class RightToolRegionView : UserControl
{
    public RightToolRegionView()
    {
        InitializeComponent();
    }

    private void OnIntegrationTcpSharedKeyPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox passwordBox
            && DataContext is MainViewModel viewModel)
        {
            viewModel.Integration.SetSessionSharedKey(passwordBox.Password);
        }
    }

    private void OnResetIntegrationSetupClicked(object sender, RoutedEventArgs e) =>
        IntegrationTcpSharedKeyBox.Clear();

    public StackPanel AxisCommissioningPanel =>
        AxisCommissioningContent.AxisCommissioningPanelControl;

    public TextBlock AxisDriveTuningText => AxisCommissioningContent.AxisDriveTuningTextControl;
    public TextBlock AxisInterlockStatusText => AxisCommissioningContent.AxisInterlockStatusTextControl;
    public TextBlock AxisFollowingErrorText => AxisCommissioningContent.AxisFollowingErrorTextControl;
    public TextBlock AxisDriveAlarmStatusText => AxisCommissioningContent.AxisDriveAlarmStatusTextControl;
    public TextBox AxisTargetPositionTextBox => AxisCommissioningContent.AxisTargetPositionTextBoxControl;
    public Button MoveAxisAbsoluteButton => AxisCommissioningContent.MoveAxisAbsoluteButtonControl;
    public TextBlock AxisTargetValidationText => AxisCommissioningContent.AxisTargetValidationTextControl;
    public TextBox AxisRelativeDistanceTextBox => AxisCommissioningContent.AxisRelativeDistanceTextBoxControl;
    public Button MoveAxisRelativeButton => AxisCommissioningContent.MoveAxisRelativeButtonControl;
    public TextBlock AxisRelativeDistanceValidationText => AxisCommissioningContent.AxisRelativeDistanceValidationTextControl;
    public TextBox AxisCommandVelocityTextBox => AxisCommissioningContent.AxisCommandVelocityTextBoxControl;
    public Button MoveAxisVelocityButton => AxisCommissioningContent.MoveAxisVelocityButtonControl;
    public TextBlock AxisCommandVelocityValidationText => AxisCommissioningContent.AxisCommandVelocityValidationTextControl;
    public Button JogNegativeButton => AxisCommissioningContent.JogNegativeButtonControl;
    public Button HomeAxisButton => AxisCommissioningContent.HomeAxisButtonControl;
    public Button StopAxisMotionButton => AxisCommissioningContent.StopAxisMotionButtonControl;
    public Button JogPositiveButton => AxisCommissioningContent.JogPositiveButtonControl;

    public TextBox IntegrationTcpListenAddressTextBoxControl => IntegrationTcpListenAddressTextBox;
    public TextBox IntegrationTcpListenPortTextBoxControl => IntegrationTcpListenPortTextBox;
    public TextBox IntegrationTcpPeerHostTextBoxControl => IntegrationTcpPeerHostTextBox;
    public TextBox IntegrationTcpPeerPortTextBoxControl => IntegrationTcpPeerPortTextBox;
    public PasswordBox IntegrationTcpSharedKeyBoxControl => IntegrationTcpSharedKeyBox;
    public Button StartIntegrationTcpListenerButtonControl => StartIntegrationTcpListenerButton;
    public Button StopIntegrationTcpListenerButtonControl => StopIntegrationTcpListenerButton;
    public Button PingIntegrationTcpPeerButtonControl => PingIntegrationTcpPeerButton;
    public Button PushIntegrationTcpTransactionButtonControl => PushIntegrationTcpTransactionButton;
    public Button PullIntegrationTcpTransactionButtonControl => PullIntegrationTcpTransactionButton;
    public TextBlock IntegrationTcpListenerStatusTextBlockControl => IntegrationTcpListenerStatusTextBlock;
    public TextBlock IntegrationTcpTransferStatusTextBlockControl => IntegrationTcpTransferStatusTextBlock;
}
