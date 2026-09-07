using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace OpenVisionLab.MachineStudio;

/// <summary>
/// Owns one Direct EXE smoke run's Window/Popup rendering and PNG persistence.
/// </summary>
internal sealed class SmokeWindowCapture
{
    private FrameworkElement? _popupContent;

    internal void SetPopupContent(FrameworkElement? popup) => _popupContent = popup;

    internal void Capture(Window window, string path)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var dpi = VisualTreeHelper.GetDpi(window);
        var width = checked((int)Math.Round(window.ActualWidth * dpi.DpiScaleX));
        var height = checked((int)Math.Round(window.ActualHeight * dpi.DpiScaleY));
        if (width < 1 || height < 1)
        {
            width = checked((int)Math.Round(window.Width * dpi.DpiScaleX));
            height = checked((int)Math.Round(window.Height * dpi.DpiScaleY));
        }

        var rendered = new RenderTargetBitmap(
            width,
            height,
            dpi.PixelsPerInchX,
            dpi.PixelsPerInchY,
            PixelFormats.Pbgra32);
        rendered.Render(window);

        BitmapSource bitmap = rendered;
        if (_popupContent is { IsVisible: true, ActualWidth: > 0, ActualHeight: > 0 } popup)
        {
            var windowOrigin = window.PointToScreen(new Point(0, 0));
            var popupOrigin = popup.PointToScreen(new Point(0, 0));
            var compositeVisual = new DrawingVisual();
            using (var drawing = compositeVisual.RenderOpen())
            {
                drawing.DrawImage(rendered, new Rect(0, 0, window.ActualWidth, window.ActualHeight));
                drawing.DrawRectangle(
                    new VisualBrush(popup),
                    null,
                    new Rect(
                        (popupOrigin.X - windowOrigin.X) / dpi.DpiScaleX,
                        (popupOrigin.Y - windowOrigin.Y) / dpi.DpiScaleY,
                        popup.ActualWidth,
                        popup.ActualHeight));
            }

            var composite = new RenderTargetBitmap(
                width,
                height,
                dpi.PixelsPerInchX,
                dpi.PixelsPerInchY,
                PixelFormats.Pbgra32);
            composite.Render(compositeVisual);
            bitmap = composite;
        }

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(fullPath);
        encoder.Save(stream);
    }
}
