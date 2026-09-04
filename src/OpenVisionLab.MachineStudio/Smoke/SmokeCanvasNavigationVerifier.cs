using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using OpenVisionLab.MachineStudio.View.Scene;
using OpenVisionLab.MachineStudio.View.Shell;
using OpenVisionLab.MachineStudio.ViewModel;
using static OpenVisionLab.MachineStudio.SmokeRoundTripScenario;

namespace OpenVisionLab.MachineStudio;

internal sealed class SmokeCanvasNavigationReport
{
    public string Schema { get; init; } = "1.0";
    public DateTimeOffset CapturedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public required IReadOnlyDictionary<string, bool> Checks { get; init; }
    public required IReadOnlyList<string> Failures { get; init; }
    public SmokeMonitorEvidence? Monitor { get; init; }
    public bool IsValid => Failures.Count == 0 && Checks.Values.All(value => value);

    public void Save(string path)
    {
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(
            fullPath,
            JsonSerializer.Serialize(this, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = true
            }));
    }
}

internal static class SmokeCanvasNavigationVerifier
{
    private const uint MouseEventMiddleDown = 0x0020;
    private const uint MouseEventMiddleUp = 0x0040;

    public static async Task<SmokeCanvasNavigationReport> VerifyAsync(
        ShellWindow window,
        MainViewModel viewModel,
        Func<DependencyObject, MachineSceneViewport?> findViewport,
        Func<DependencyObject, SceneDocumentView?> findDocument)
    {
        var checks = new Dictionary<string, bool>(StringComparer.Ordinal);
        var failures = new List<string>();
        void Check(string name, bool passed)
        {
            checks[name] = passed;
            if (!passed)
            {
                failures.Add(name);
            }
        }

        var viewport = findViewport(window)
            ?? throw new InvalidOperationException("Machine scene viewport was not available.");
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);

        viewport.FitToLayout();
        var initialCenter = viewport.GetItemCenter(RoundTripCylinderId)
            ?? throw new InvalidOperationException("Cylinder center was unavailable.");
        var initialBounds = viewport.GetItemScreenBounds(RoundTripCylinderId)
            ?? throw new InvalidOperationException("Cylinder bounds were unavailable.");

        var wheelScreenPoint = viewport.PointToScreen(initialCenter);
        SetCursorPos((int)Math.Round(wheelScreenPoint.X), (int)Math.Round(wheelScreenPoint.Y));
        var wheelAnchor = Mouse.GetPosition(viewport);
        viewport.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, 120)
        {
            RoutedEvent = Mouse.MouseWheelEvent
        });
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Check("wheelZoomRequested", viewport.ZoomFactor > 1d);
        var zoomedCenter = viewport.GetItemCenter(RoundTripCylinderId) ?? default;
        var zoomedBounds = viewport.GetItemScreenBounds(RoundTripCylinderId) ?? Rect.Empty;
        Check("wheelZoomApplied", viewport.ZoomFactor > 1d && zoomedBounds.Width > initialBounds.Width);
        var expectedZoomedCenter = wheelAnchor + ((initialCenter - wheelAnchor) * viewport.ZoomFactor);
        Check("wheelAnchorPreserved", (zoomedCenter - expectedZoomedCenter).Length < 0.001d);
        Check("zoomedHitTest", viewport.SelectItemAt(zoomedCenter) &&
            viewModel.Layout.SelectedItem?.Id == RoundTripCylinderId);

        var panDelta = new Vector(46, -28);
        var panStart = viewport.PointToScreen(zoomedCenter);
        var panEnd = viewport.PointToScreen(zoomedCenter + panDelta);
        SetCursorPos((int)Math.Round(panStart.X), (int)Math.Round(panStart.Y));
        var actualPanStart = Mouse.GetPosition(viewport);
        mouse_event(MouseEventMiddleDown, 0, 0, 0, UIntPtr.Zero);
        viewport.RaiseEvent(new MouseButtonEventArgs(
            Mouse.PrimaryDevice,
            Environment.TickCount,
            MouseButton.Middle)
        {
            RoutedEvent = Mouse.MouseDownEvent
        });
        await Task.Delay(50);
        SetCursorPos((int)Math.Round(panEnd.X), (int)Math.Round(panEnd.Y));
        var actualPanEnd = Mouse.GetPosition(viewport);
        viewport.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount)
        {
            RoutedEvent = Mouse.MouseMoveEvent
        });
        mouse_event(MouseEventMiddleUp, 0, 0, 0, UIntPtr.Zero);
        viewport.RaiseEvent(new MouseButtonEventArgs(
            Mouse.PrimaryDevice,
            Environment.TickCount,
            MouseButton.Middle)
        {
            RoutedEvent = Mouse.MouseUpEvent
        });
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        var pannedCenter = viewport.GetItemCenter(RoundTripCylinderId) ?? default;
        var expectedPanDelta = actualPanEnd - actualPanStart;
        Check("middlePanRequested", (pannedCenter - zoomedCenter).Length > 1d);
        Check("middlePanApplied", ((pannedCenter - zoomedCenter) - expectedPanDelta).Length < 0.001d);
        Check("pannedHitTest", viewport.SelectItemAt(pannedCenter) &&
            viewModel.Layout.SelectedItem?.Id == RoundTripCylinderId);
        Check("navigationDoesNotCreateHistoryBeforeDrag", !viewModel.UndoLayoutEditCommand.CanExecute(null));

        var cylinder = viewModel.Layout.Items.Single(item => item.Id == RoundTripCylinderId);
        var beforeDragX = cylinder.CurrentX;
        Check("dragAfterNavigationRequested", viewport.RequestSelectionDrag(
            RoundTripCylinderId,
            new Vector(24, 0)));
        Check("dragAfterNavigationApplied", cylinder.CurrentX != beforeDragX);
        Check("dragCreatesHistory", viewModel.UndoLayoutEditCommand.CanExecute(null));
        viewModel.UndoLayoutEditCommand.Execute(null);
        Check("dragAfterNavigationUndo",
            viewModel.Layout.Items.Single(item => item.Id == RoundTripCylinderId).CurrentX == beforeDragX &&
            !viewModel.UndoLayoutEditCommand.CanExecute(null));

        var pannedBounds = viewport.GetItemScreenBounds(RoundTripCylinderId) ?? Rect.Empty;
        pannedBounds.Inflate(2, 2);
        var marqueeIds = viewport.RequestMarqueeSelection(pannedBounds, ModifierKeys.None);
        Check("marqueeAfterNavigation", marqueeIds.Contains(RoundTripCylinderId, StringComparer.Ordinal));

        var document = findDocument(window)
            ?? throw new InvalidOperationException("Scene document view was not available.");
        var fitButton = document.FindName("FitLayoutButton") as Button
            ?? throw new InvalidOperationException("Fit layout button was not available.");
        fitButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        var fittedCenter = viewport.GetItemCenter(RoundTripCylinderId) ?? default;
        var fittedBounds = viewport.GetItemScreenBounds(RoundTripCylinderId) ?? Rect.Empty;
        Check("fitResetsZoom", Math.Abs(viewport.ZoomFactor - 1d) < 0.000001d);
        Check("fitButtonInvoked", fitButton.IsEnabled);
        Check("fitRestoresView", (fittedCenter - initialCenter).Length < 0.001d &&
            Math.Abs(fittedBounds.Width - initialBounds.Width) < 0.001d);
        Check("navigationDoesNotCreateHistory", !viewModel.UndoLayoutEditCommand.CanExecute(null));

        return new SmokeCanvasNavigationReport
        {
            Checks = checks,
            Failures = failures
        };
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern void mouse_event(
        uint dwFlags,
        uint dx,
        uint dy,
        uint dwData,
        UIntPtr dwExtraInfo);
}
