using System.ComponentModel;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.TextFormatting;
using System.Windows.Threading;

namespace OpenVisionLab.MachineStudio.View.Sequence;

public partial class SequenceEditorView : UserControl
{
    public static readonly DependencyProperty StepViewIndexProperty = DependencyProperty.Register(nameof(StepViewIndex), typeof(int), typeof(SequenceEditorView), new PropertyMetadata(0));

    public int StepViewIndex
    {
        get => (int)GetValue(StepViewIndexProperty);
        set => SetValue(StepViewIndexProperty, value);
    }

    public static readonly DependencyProperty ShowInlineEditorProperty = DependencyProperty.Register(nameof(ShowInlineEditor), typeof(bool), typeof(SequenceEditorView),
        new PropertyMetadata(true, (owner, args) =>
        {
            var view = (SequenceEditorView)owner;
            if (view.InlineEditorColumn is null) return;
            view.InlineEditorColumn.MinWidth = (bool)args.NewValue ? 280 : 0;
            view.InlineEditorColumn.Width = (bool)args.NewValue ? new GridLength(0.55, GridUnitType.Star) : new GridLength(0);
        }));

    public bool ShowInlineEditor { get => (bool)GetValue(ShowInlineEditorProperty); set => SetValue(ShowInlineEditorProperty, value); }

    public static readonly DependencyProperty ShowUnitSettingsProperty = DependencyProperty.Register(nameof(ShowUnitSettings), typeof(bool), typeof(SequenceEditorView), new PropertyMetadata(true));

    public bool ShowUnitSettings { get => (bool)GetValue(ShowUnitSettingsProperty); set => SetValue(ShowUnitSettingsProperty, value); }

    private bool _isTemplateDialogOpen;
    private (DataGridLength Width, double MinWidth)[]? _legacyColumnLayout;
    private readonly (double Minimum, double Preferred)[] _contentColumnWidths = new (double, double)[5];
    private (double Minimum, double Preferred) _orderColumnWidths;
    private ScrollViewer? _stepListScrollViewer;
    private bool _columnRefreshQueued;
    private bool _columnTextChanged = true;
    private double _columnViewportWidth;

    public static readonly DependencyProperty IsR24DockedProperty = DependencyProperty.Register(nameof(IsR24Docked), typeof(bool), typeof(SequenceEditorView),
        new PropertyMetadata(false, (owner, args) =>
        {
            var view = (SequenceEditorView)owner;
            if (view.StepListDataGrid is null) return;
            view._legacyColumnLayout ??= view.StepListDataGrid.Columns.Select(column => (column.Width, column.MinWidth)).ToArray();
            for (int index = 0; index < view.StepListDataGrid.Columns.Count; index++)
            {
                var column = view.StepListDataGrid.Columns[index];
                column.MinWidth = (bool)args.NewValue ? 0 : view._legacyColumnLayout[index].MinWidth;
                if (!(bool)args.NewValue) column.Width = view._legacyColumnLayout[index].Width;
                else if (index == 0) column.Width = new DataGridLength(30);
                else if (index == 6) column.Width = new DataGridLength(170);
            }
            view._columnViewportWidth = 0;
            foreach (var text in VisualChildren(view.StepListDataGrid).OfType<TextBlock>()) text.GetBindingExpression(TextBlock.TextProperty)?.UpdateTarget();
            view.QueueColumnRefresh(measureText: true);
        }));
    public bool IsR24Docked { get => (bool)GetValue(IsR24DockedProperty); set => SetValue(IsR24DockedProperty, value); }

    private void OnStepListLoaded(object sender, RoutedEventArgs args)
    {
        _stepListScrollViewer = StepListDataGrid.Template.FindName("DG_ScrollViewer", StepListDataGrid) as ScrollViewer ?? VisualChildren(StepListDataGrid).OfType<ScrollViewer>().FirstOrDefault();
        QueueColumnRefresh(measureText: true);
    }

    private void OnStepListUnloaded(object sender, RoutedEventArgs args)
    {
        _stepListScrollViewer = null;
        _columnViewportWidth = 0;
        _columnTextChanged = true;
    }

    private void OnStepListTargetUpdated(object sender, RoutedEventArgs args)
    {
        if (sender == StepListDataGrid && args is DataTransferEventArgs { Property: var targetProperty } && targetProperty == Selector.SelectedItemProperty)
        {
            Dispatcher.InvokeAsync(() =>
            {
                if (IsLoaded && StepListDataGrid.SelectedItem is { } selected) StepListDataGrid.ScrollIntoView(selected);
            }, DispatcherPriority.Loaded);
            return;
        }
        if (IsR24Docked && sender == StepListDataGrid && args is DataTransferEventArgs { TargetObject: TextBlock text, Property: var property } && property == TextBlock.TextProperty)
        {
            string display = AddBrowserHyphenBreaks(text.Text);
            if (display != text.Text)
            {
                text.SetCurrentValue(TextBlock.TextProperty, display);
            }
        }
        QueueColumnRefresh(measureText: true);
    }

    private void OnProcessTableSelectionUpdated(object sender, DataTransferEventArgs args)
    {
        if (args.Property == TagProperty && sender is Button { Tag: true } row) RevealProcessTableRow(row);
    }

    private void OnProcessTableRowLoaded(object sender, RoutedEventArgs args)
    {
        if (sender is Button { Tag: true } row) RevealProcessTableRow(row);
    }

    private void RevealProcessTableRow(FrameworkElement row)
    {
        Dispatcher.InvokeAsync(() =>
        {
            if (!row.IsLoaded || row.Tag is not true || !AllUnitsTableViewport.IsVisible) return;
            AllUnitsTableViewport.UpdateLayout();
            Rect bounds = row.TransformToAncestor(AllUnitsTableViewport).TransformBounds(new Rect(row.RenderSize));
            double offset = AllUnitsTableViewport.VerticalOffset;
            if (bounds.Top < 0 || bounds.Height > AllUnitsTableViewport.ViewportHeight) offset += bounds.Top;
            else if (bounds.Bottom > AllUnitsTableViewport.ViewportHeight) offset += bounds.Bottom - AllUnitsTableViewport.ViewportHeight;
            AllUnitsTableViewport.ScrollToVerticalOffset(Math.Clamp(offset, 0, AllUnitsTableViewport.ScrollableHeight));
        }, DispatcherPriority.ContextIdle);
    }

    private static string AddBrowserHyphenBreaks(string text) => Regex.Replace(text, @"(?<=[\p{L}\p{N}])-(?=[\p{L}\p{N}])", "-\u200B");

    private void OnStepListLayoutUpdated(object? sender, EventArgs args)
    {
        if (IsR24Docked && Math.Abs((_stepListScrollViewer?.ViewportWidth ?? StepListDataGrid.ActualWidth) - _columnViewportWidth) > 0.1) QueueColumnRefresh(measureText: false);
    }

    private void QueueColumnRefresh(bool measureText)
    {
        _columnTextChanged |= measureText;
        if (!IsR24Docked || !IsLoaded || _columnRefreshQueued) return;
        _columnRefreshQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            _columnRefreshQueued = false;
            if (!IsR24Docked || !IsLoaded) return;
            UpdateContentColumnWidths();
        }));
    }

    private void UpdateContentColumnWidths()
    {
        _columnViewportWidth = _stepListScrollViewer?.ViewportWidth ?? StepListDataGrid.ActualWidth;
        if (_columnViewportWidth <= 0) return;
        if (_columnTextChanged)
        {
            _columnTextChanged = false;
            string[] paths = ["DisplayName", "StepListActionText", "StepListTargetText", "StepListParameterText", "StepListNextText"];
            var headers = VisualChildren(StepListDataGrid).OfType<DataGridColumnHeader>().Where(header => header.Column is not null).ToDictionary(header => header.Column.DisplayIndex);
            using var formatter = TextFormatter.Create();
            double pixelsPerDip = VisualTreeHelper.GetDpi(StepListDataGrid).PixelsPerDip;
            for (int index = 0; index < paths.Length; index++)
            {
                var header = headers.GetValueOrDefault(index + 1);
                string caption = header is null ? StepListDataGrid.Columns[index + 1].Header?.ToString() ?? "" : string.Concat(VisualChildren(header).OfType<TextBlock>().Where(text => text.IsVisible).Select(text => text.Text));
                var headingProperties = new ColumnRunProperties(new Typeface(StepListDataGrid.FontFamily, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal), 12);
                var bodyProperties = new ColumnRunProperties(new Typeface(StepListDataGrid.FontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), index == 4 ? 11.5 : 12);
                var widths = MeasureColumnText(caption, headingProperties);
                if (index == 0 && headers.TryGetValue(0, out var orderHeader))
                {
                    string orderCaption = string.Concat(VisualChildren(orderHeader).OfType<TextBlock>().Where(text => text.IsVisible).Select(text => text.Text));
                    var orderWidths = MeasureColumnText(orderCaption, headingProperties);
                    _orderColumnWidths = (orderWidths.Minimum + 16, orderWidths.Preferred + 16);
                }
                foreach (var item in StepListDataGrid.Items)
                {
                    string value = TypeDescriptor.GetProperties(item)[paths[index]]?.GetValue(item)?.ToString() ?? "";
                    var measured = MeasureColumnText(value, bodyProperties);
                    widths = (Math.Max(widths.Minimum, measured.Minimum), Math.Max(widths.Preferred, measured.Preferred));
                }
                _contentColumnWidths[index] = (widths.Minimum + 16, widths.Preferred + 16);

                (double Minimum, double Preferred) MeasureColumnText(string value, ColumnRunProperties properties)
                {
                    double minimum = 0, preferred = 0;
                    foreach (string line in value.Replace("\r", "").Split('\n'))
                    {
                        var measured = formatter.FormatMinMaxParagraphWidth(new ColumnTextSource(line, properties) { PixelsPerDip = pixelsPerDip }, 0, new ColumnParagraphProperties(properties, StepListDataGrid.FlowDirection));
                        // Browser normal wrapping permits a break after the hyphen in IDs such as AX-01. Keep display/binding text intact.
                        string minimumText = AddBrowserHyphenBreaks(line);
                        var minimumWidth = minimumText != line ? formatter.FormatMinMaxParagraphWidth(new ColumnTextSource(minimumText, properties) { PixelsPerDip = pixelsPerDip }, 0, new ColumnParagraphProperties(properties, StepListDataGrid.FlowDirection)).MinWidth : measured.MinWidth;
                        minimum = Math.Max(minimum, minimumWidth);
                        preferred = Math.Max(preferred, measured.MaxWidth);
                    }
                    return (minimum, preferred);
                }
            }
        }
        bool empty = !StepListDataGrid.HasItems;
        double available = Math.Max(0, _columnViewportWidth - (empty ? 170 : 200));
        double minimumTotal = _contentColumnWidths.Sum(width => width.Minimum) + (empty ? _orderColumnWidths.Minimum : 0);
        double preferredTotal = _contentColumnWidths.Sum(width => width.Preferred) + (empty ? _orderColumnWidths.Preferred : 0);
        StepListDataGrid.Columns[0].MinWidth = empty ? _orderColumnWidths.Minimum : 0;
        StepListDataGrid.Columns[0].Width = new DataGridLength(empty ? ContentWidth(_orderColumnWidths) : 30);
        for (int index = 0; index < _contentColumnWidths.Length; index++)
        {
            var content = _contentColumnWidths[index];
            StepListDataGrid.Columns[index + 1].MinWidth = content.Minimum;
            StepListDataGrid.Columns[index + 1].Width = new DataGridLength(ContentWidth(content));
        }

        double ContentWidth((double Minimum, double Preferred) content) => available >= preferredTotal ? content.Preferred * available / preferredTotal
            : content.Minimum + (content.Preferred - content.Minimum) * Math.Max(0, available - minimumTotal) / Math.Max(1, preferredTotal - minimumTotal);
    }

    private static IEnumerable<DependencyObject> VisualChildren(DependencyObject root)
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            yield return child;
            foreach (var descendant in VisualChildren(child)) yield return descendant;
        }
    }

    // TextFormatter requires these native property adapters to measure Unicode wrapping without a custom word-break policy.
    private sealed class ColumnRunProperties(Typeface typeface, double fontSize) : TextRunProperties
    {
        public override Typeface Typeface => typeface;
        public override double FontRenderingEmSize => fontSize;
        public override double FontHintingEmSize => fontSize;
        public override TextDecorationCollection TextDecorations => null!;
        public override Brush ForegroundBrush => Brushes.Black;
        public override Brush BackgroundBrush => null!;
        public override CultureInfo CultureInfo => CultureInfo.CurrentCulture;
        public override TextEffectCollection TextEffects => null!;
    }

    private sealed class ColumnParagraphProperties(ColumnRunProperties properties, FlowDirection flowDirection) : TextParagraphProperties
    {
        public override FlowDirection FlowDirection => flowDirection;
        public override TextAlignment TextAlignment => TextAlignment.Left;
        public override double LineHeight => 0;
        public override bool FirstLineInParagraph => true;
        public override TextRunProperties DefaultTextRunProperties => properties;
        public override TextWrapping TextWrapping => TextWrapping.Wrap;
        public override TextMarkerProperties TextMarkerProperties => null!;
        public override double Indent => 0;
    }

    private sealed class ColumnTextSource(string text, ColumnRunProperties properties) : TextSource
    {
        public override TextRun GetTextRun(int index) => index < text.Length ? new TextCharacters(text, index, text.Length - index, properties) : new TextEndOfParagraph(1);
        public override TextSpan<CultureSpecificCharacterBufferRange> GetPrecedingText(int limit) => new(limit, new(properties.CultureInfo, new CharacterBufferRange(text, 0, limit)));
        public override int GetTextEffectCharacterIndexFromTextSourceCharacterIndex(int index) => index;
    }

    private void ShowStepTemplateDialog(object sender, RoutedEventArgs args)
    {
        if (_isTemplateDialogOpen) return;
        _isTemplateDialogOpen = true;
        try
        {
            var owner = Window.GetWindow(this);
            var dialog = new SequenceStepTemplateDialog { Owner = owner, DataContext = DataContext };
            if (owner is not null) { dialog.MaxHeight = owner.ActualHeight - 40; dialog.Width = Math.Min(900, owner.ActualWidth - 40); }
            dialog.ShowDialog();
        }
        finally { _isTemplateDialogOpen = false; }
    }

    public SequenceEditorView()
    {
        InitializeComponent();
    }
}
