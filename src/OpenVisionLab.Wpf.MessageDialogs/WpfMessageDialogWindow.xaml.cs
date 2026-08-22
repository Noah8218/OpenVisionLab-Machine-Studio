using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Media;
using OpenVisionLab;

namespace OpenVisionLab.Wpf.MessageDialogs;

public partial class WpfMessageDialogWindow : Window
{
    private readonly WpfMessageDialogResult _defaultResult;

    public WpfMessageDialogWindow(WpfMessageDialogOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        InitializeComponent();

        Title = string.IsNullOrWhiteSpace(options.Title)
            ? OpenVisionLanguageService.T("MessageBox.Message", "메시지", "Message")
            : options.Title;
        TitleText.Text = Title;
        MessageText.Text = options.Message;
        _defaultResult = options.DefaultResult;

        ConfigureKind(options.Kind);
        ConfigureButton(
            PrimaryButton,
            options.PrimaryButtonText,
            OpenVisionLanguageService.T("MessageBox.OK", "확인", "OK"),
            options.DefaultResult is WpfMessageDialogResult.OK or WpfMessageDialogResult.Yes,
            isOptional: false);
        ConfigureButton(
            SecondaryButton,
            options.SecondaryButtonText,
            OpenVisionLanguageService.T("MessageBox.Cancel", "취소", "Cancel"),
            options.DefaultResult == WpfMessageDialogResult.No,
            isOptional: true);
        ConfigureButton(
            TertiaryButton,
            options.TertiaryButtonText,
            OpenVisionLanguageService.T("MessageBox.Cancel", "취소", "Cancel"),
            options.DefaultResult == WpfMessageDialogResult.Cancel,
            isOptional: true);
    }

    public WpfMessageDialogResult Result { get; private set; }

    private static void ConfigureButton(
        System.Windows.Controls.Button button,
        string configuredText,
        string fallbackText,
        bool isDefault,
        bool isOptional)
    {
        button.Content = string.IsNullOrWhiteSpace(configuredText) ? fallbackText : configuredText;
        button.Visibility = isOptional && string.IsNullOrWhiteSpace(configuredText)
            ? Visibility.Collapsed
            : Visibility.Visible;
        button.IsDefault = isDefault;
        AutomationProperties.SetName(button, button.Content.ToString() ?? fallbackText);
    }

    private void ConfigureKind(WpfMessageDialogKind kind)
    {
        (string glyph, Color color) = kind switch
        {
            WpfMessageDialogKind.Warning => ("!", Color.FromRgb(245, 158, 11)),
            WpfMessageDialogKind.Question => ("?", Color.FromRgb(139, 92, 246)),
            _ => ("i", Color.FromRgb(59, 130, 246))
        };
        var brush = new SolidColorBrush(color);
        KindGlyph.Text = glyph;
        KindBadge.BorderBrush = brush;
        AccentBar.Background = brush;
    }

    private void OnPrimaryClicked(object sender, RoutedEventArgs e) =>
        CloseWithResult(_defaultResult == WpfMessageDialogResult.OK
            ? WpfMessageDialogResult.OK
            : WpfMessageDialogResult.Yes);

    private void OnSecondaryClicked(object sender, RoutedEventArgs e) =>
        CloseWithResult(WpfMessageDialogResult.No);

    private void OnTertiaryClicked(object sender, RoutedEventArgs e) =>
        CloseWithResult(WpfMessageDialogResult.Cancel);

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CloseWithResult(WpfMessageDialogResult.Cancel);
        }
    }

    private void CloseWithResult(WpfMessageDialogResult result)
    {
        Result = result;
        DialogResult = true;
        Close();
    }
}
