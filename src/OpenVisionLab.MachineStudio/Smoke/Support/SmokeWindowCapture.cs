using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using OpenVisionLab.Machine.Simulation.Snapshots;
using OpenVisionLab.MachineStudio.View.Shell;

namespace OpenVisionLab.MachineStudio;

/// <summary>
/// Owns one Direct EXE smoke run's Window/Popup rendering and PNG persistence.
/// </summary>
internal sealed class SmokeWindowCapture
{
    private FrameworkElement? _popupContent;

    internal void SetPopupContent(FrameworkElement? popup) => _popupContent = popup;

    internal async Task CaptureFramesAsync(Window window, string directory, int seconds, Func<SimulationSnapshot?> snapshotAccessor)
    {
        var root = Path.GetFullPath(directory);
        if (!root.StartsWith("D:\\", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Frame recording must be stored on the D: test-data drive.", nameof(directory));
        if (seconds is < 1 or > 120)
            throw new ArgumentOutOfRangeException(nameof(seconds));
        if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any())
            throw new IOException("Frame recording directory must be empty to preserve prior evidence.");

        Directory.CreateDirectory(root);
        var frames = new List<object>();
        var startedAtUtc = DateTimeOffset.UtcNow;
        var clock = Stopwatch.StartNew();
        var options = new JsonSerializerOptions { WriteIndented = true };
        try
        {
            while (clock.Elapsed.TotalSeconds < seconds)
            {
                var elapsedMs = clock.Elapsed.TotalMilliseconds;
                var file = $"frame-{frames.Count:D5}.png";
                var snapshot = snapshotAccessor();
                Capture(window, Path.Combine(root, file));
                frames.Add(new
                {
                    File = file, ElapsedMs = elapsedMs, SimulationTime = snapshot?.SimulationTime,
                    TickIndex = snapshot?.TickIndex, RunMode = snapshot?.RunMode,
                    Sequences = snapshot?.Sequences, Slots = snapshot?.UnitMaterialSlots,
                    Exits = snapshot?.UnitMaterialExits,
                    Cameras = snapshot?.Cameras.Select(camera => new
                    {
                        camera.Id, camera.State, camera.AcquisitionOrdinal,
                        camera.CurrentAcquisitionId, camera.Result?.Decision,
                        camera.Result?.WorkpieceInstanceId, camera.Result?.FrameEvidence
                    }).ToArray()
                });
                await Task.Delay(Math.Max(1, (int)(100 - (clock.Elapsed.TotalMilliseconds - elapsedMs))));
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
            }
        }
        finally
        {
            var report = new
            {
                Method = "Actual EXE WPF RenderTargetBitmap; explicit RunCommand; no native pointer/keyboard authoring",
                StartedAtUtc = startedAtUtc, WallDurationMs = clock.Elapsed.TotalMilliseconds,
                RequestedSeconds = seconds, TargetIntervalMs = 100,
                Monitor = SmokeDpiTestHook.CaptureMonitorEvidence(window), BuildIdentity = BuildIdentity.Current,
                Frames = frames, FinalSnapshot = snapshotAccessor()
            };
            File.WriteAllText(Path.Combine(root, "recording.json"), JsonSerializer.Serialize(report, options));
        }
    }

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
