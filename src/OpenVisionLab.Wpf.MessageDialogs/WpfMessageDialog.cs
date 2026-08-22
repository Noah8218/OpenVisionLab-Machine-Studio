using System.Windows;

namespace OpenVisionLab.Wpf.MessageDialogs;

public enum WpfMessageDialogResult
{
    None,
    OK,
    Cancel,
    Yes,
    No
}

public enum WpfMessageDialogKind
{
    Info,
    Warning,
    Question
}

public sealed class WpfMessageDialogOptions
{
    public string Title { get; set; } = string.Empty;

    public string Message { get; set; } = string.Empty;

    public WpfMessageDialogKind Kind { get; set; } = WpfMessageDialogKind.Info;

    public WpfMessageDialogResult DefaultResult { get; set; } = WpfMessageDialogResult.OK;

    public string PrimaryButtonText { get; set; } = string.Empty;

    public string SecondaryButtonText { get; set; } = string.Empty;

    public string TertiaryButtonText { get; set; } = string.Empty;
}

public static class WpfMessageDialog
{
    public static WpfMessageDialogResult Show(Window? owner, WpfMessageDialogOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var window = new WpfMessageDialogWindow(options);
        if (owner is { IsVisible: true })
        {
            window.Owner = owner;
            window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }

        return window.ShowDialog() == true
            ? window.Result
            : WpfMessageDialogResult.None;
    }
}
