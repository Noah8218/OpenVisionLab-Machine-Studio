using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace OpenVisionLab.MachineStudio.View.Scene;

public partial class SceneDocumentView : UserControl
{
    private IInputElement? _stationUnitReturnFocus;

    public SceneDocumentView()
    {
        InitializeComponent();
    }

    // Reserve the device ID before trimming a long display name.
    private void OnEquipmentSelectionLayoutChanged(object sender, RoutedEventArgs e)
    {
        if (!EquipmentSelectionIdentity.IsVisible || EquipmentSelectionActions.MaxWidth <= 0) return;
        FormattedText Measure(TextBlock text, string? value = null) => new(value ?? text.Text, CultureInfo.CurrentUICulture, text.FlowDirection,
            new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch), text.FontSize, text.Foreground,
            VisualTreeHelper.GetDpi(text).PixelsPerDip);
        var name = Measure(EquipmentSelectionName);
        var detail = Measure(EquipmentSelectionDetail);
        var naturalStripWidth = name.WidthIncludingTrailingWhitespace + detail.WidthIncludingTrailingWhitespace + 12
            + EquipmentSelectionCommands.DesiredSize.Width + EquipmentSelectionActions.Padding.Left + EquipmentSelectionActions.Padding.Right
            + EquipmentSelectionActions.BorderThickness.Left + EquipmentSelectionActions.BorderThickness.Right;
        var stripWidth = naturalStripWidth > EquipmentSelectionActions.MaxWidth ? EquipmentSelectionActions.MaxWidth : double.NaN;
        if (double.IsNaN(stripWidth) != double.IsNaN(EquipmentSelectionActions.Width)
            || !double.IsNaN(stripWidth) && Math.Abs(stripWidth - EquipmentSelectionActions.Width) > 0.01) EquipmentSelectionActions.Width = stripWidth;
        var naturalName = name.WidthIncludingTrailingWhitespace;
        var naturalDetail = detail.WidthIncludingTrailingWhitespace;
        var available = Math.Max(0, Math.Min(naturalName + naturalDetail, EquipmentSelectionActions.MaxWidth
            - EquipmentSelectionActions.Padding.Left - EquipmentSelectionActions.Padding.Right
            - EquipmentSelectionActions.BorderThickness.Left - EquipmentSelectionActions.BorderThickness.Right
            - EquipmentSelectionCommands.DesiredSize.Width - EquipmentSelectionIdentity.Margin.Right - EquipmentSelectionDetail.Margin.Left));
        var detailWidth = Math.Min(naturalDetail, Math.Max(0, available - Math.Min(48, naturalName)));
        detailWidth = Math.Ceiling(detailWidth) + EquipmentSelectionDetail.Margin.Left;
        var column = EquipmentSelectionIdentity.ColumnDefinitions[1];
        if (column.Width.IsAuto || Math.Abs(column.Width.Value - detailWidth) > 0.01) column.Width = new GridLength(detailWidth);
    }

    private void StationUnitEditor_OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        var opening = e.NewValue is true;
        if (opening) _stationUnitReturnFocus = Keyboard.FocusedElement;
        var target = opening ? StationUnitNameTextBox : _stationUnitReturnFocus;
        if (!opening) _stationUnitReturnFocus = null;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (IsVisible && StationUnitEditorOverlay.IsVisible == opening
                && target is UIElement { IsVisible: true, IsEnabled: true } element)
            {
                element.Focus();
            }
        }));
    }

    private void LargeOverviewScrollViewer_OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true && sender is ScrollViewer scrollViewer)
        {
            scrollViewer.ScrollToTop();
        }
    }
}
