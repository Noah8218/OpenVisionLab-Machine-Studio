using System.Windows.Controls;

namespace OpenVisionLab.MachineStudio.View.Inspector;

public partial class AxisCommissioningPanelView : UserControl
{
    public AxisCommissioningPanelView()
    {
        InitializeComponent();
    }

    public StackPanel AxisCommissioningPanelControl => AxisCommissioningPanel;
    public TextBlock AxisDriveTuningTextControl => AxisDriveTuningText;
    public TextBlock AxisInterlockStatusTextControl => AxisInterlockStatusText;
    public TextBlock AxisFollowingErrorTextControl => AxisFollowingErrorText;
    public TextBlock AxisDriveAlarmStatusTextControl => AxisDriveAlarmStatusText;
    public TextBox AxisTargetPositionTextBoxControl => AxisTargetPositionTextBox;
    public Button MoveAxisAbsoluteButtonControl => MoveAxisAbsoluteButton;
    public TextBlock AxisTargetValidationTextControl => AxisTargetValidationText;
    public TextBox AxisRelativeDistanceTextBoxControl => AxisRelativeDistanceTextBox;
    public Button MoveAxisRelativeButtonControl => MoveAxisRelativeButton;
    public TextBlock AxisRelativeDistanceValidationTextControl => AxisRelativeDistanceValidationText;
    public TextBox AxisCommandVelocityTextBoxControl => AxisCommandVelocityTextBox;
    public Button MoveAxisVelocityButtonControl => MoveAxisVelocityButton;
    public TextBlock AxisCommandVelocityValidationTextControl => AxisCommandVelocityValidationText;
    public Button JogNegativeButtonControl => JogNegativeButton;
    public Button HomeAxisButtonControl => HomeAxisButton;
    public Button StopAxisMotionButtonControl => StopAxisMotionButton;
    public Button JogPositiveButtonControl => JogPositiveButton;
}
