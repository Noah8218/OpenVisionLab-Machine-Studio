using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace OpenVisionLab.MachineStudio.View.Inspector;

public partial class RightToolRegionView : UserControl
{
    public static readonly DependencyProperty IntegrationSetupOnlyProperty = DependencyProperty.Register(
        nameof(IntegrationSetupOnly), typeof(bool), typeof(RightToolRegionView), new PropertyMetadata(false));

    public RightToolRegionView()
    {
        InitializeComponent();
    }

    // Keep short device IDs intact when the display name needs trimming.
    private void OnEquipmentInspectorHeaderLayoutChanged(object sender, RoutedEventArgs e)
    {
        if (!EquipmentInspectorIdentity.IsVisible || EquipmentInspectorIdentity.ActualWidth <= 0) return;
        double TextWidth(TextBlock text, string value) => new FormattedText(value, CultureInfo.CurrentUICulture, text.FlowDirection,
            new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch), text.FontSize, text.Foreground,
            VisualTreeHelper.GetDpi(text).PixelsPerDip).WidthIncludingTrailingWhitespace;
        var idWidth = 0d;
        if (EquipmentInspectorId.Visibility == Visibility.Visible)
        {
            var titleWidth = TextWidth(EquipmentInspectorTitle, EquipmentInspectorTitle.Text);
            var naturalIdWidth = TextWidth(EquipmentInspectorId, EquipmentInspectorId.Text);
            var availableWidth = EquipmentInspectorIdentity.Parent is Grid header ? header.ColumnDefinitions[1].ActualWidth : EquipmentInspectorIdentity.ActualWidth;
            var available = Math.Max(0, availableWidth - EquipmentInspectorId.Margin.Left);
            idWidth = Math.Min(naturalIdWidth, Math.Max(0, available - Math.Min(48, titleWidth)));
            idWidth = Math.Ceiling(idWidth) + EquipmentInspectorId.Margin.Left;
        }
        var column = EquipmentInspectorIdentity.ColumnDefinitions[1];
        if (column.Width.IsAuto || Math.Abs(column.Width.Value - idWidth) > 0.01) column.Width = new GridLength(idWidth);
    }

    // Presentation-only navigation: focus the first visible field already marked by its binding.
    private void OnInspectorErrorClick(object sender, RoutedEventArgs e)
    {
        FocusError(this);
        static bool FocusError(DependencyObject parent)
        {
            if (parent is Control { IsVisible: true, Tag: string { Length: > 0 } } field && field is TextBox or ComboBox)
            {
                field.BringIntoView();
                field.Focus();
                return true;
            }
            for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
                if (FocusError(VisualTreeHelper.GetChild(parent, index))) return true;
            return false;
        }
    }

    public bool IntegrationSetupOnly
    {
        get => (bool)GetValue(IntegrationSetupOnlyProperty);
        set => SetValue(IntegrationSetupOnlyProperty, value);
    }

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
    public CheckBox IntegrationWaitForExternalResultCheckBoxControl => IntegrationWaitForExternalResultCheckBox;
    public Expander MmiRecipeManagerExpanderControl => MmiRecipeManagerExpander;
    public ListBox MmiRecipeListControl => MmiRecipeList;
    public Button AddMmiRecipeButtonControl => AddMmiRecipeButton;
    public Button RemoveMmiRecipeButtonControl => RemoveMmiRecipeButton;
}
