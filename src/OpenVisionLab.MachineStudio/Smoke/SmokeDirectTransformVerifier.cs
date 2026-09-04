using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using OpenVisionLab.MachineStudio.Model;
using OpenVisionLab.MachineStudio.View.Inspector;
using OpenVisionLab.MachineStudio.View.Scene;
using OpenVisionLab.MachineStudio.View.Shell;
using OpenVisionLab.MachineStudio.ViewModel;
using static OpenVisionLab.MachineStudio.SmokeRoundTripScenario;

namespace OpenVisionLab.MachineStudio;

internal sealed class SmokeDirectTransformReport
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

internal static class SmokeDirectTransformVerifier
{
    public static async Task<SmokeDirectTransformReport> VerifyAsync(
        ShellWindow window,
        MainViewModel viewModel,
        string reportPath,
        Func<DependencyObject, MachineSceneViewport?> findViewport,
        Func<DependencyObject, RightToolRegionView?> findInspector)
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

        static void Execute(ICommand command)
        {
            if (!command.CanExecute(null))
            {
                throw new InvalidOperationException("Expected layout history command was disabled.");
            }
            command.Execute(null);
        }

        static (double X, double Y) Corner(LayoutItem item, double signX, double signY)
        {
            var radians = item.CurrentRotationDegrees * Math.PI / 180d;
            var axisXX = Math.Cos(radians);
            var axisXY = Math.Sin(radians);
            var axisYX = -axisXY;
            var axisYY = axisXX;
            return (
                item.CurrentX + (signX * item.CurrentWidth * axisXX / 2d) +
                    (signY * item.CurrentHeight * axisYX / 2d),
                item.CurrentY + (signX * item.CurrentWidth * axisXY / 2d) +
                    (signY * item.CurrentHeight * axisYY / 2d));
        }

        var viewport = findViewport(window)
            ?? throw new InvalidOperationException("Machine scene viewport was not available.");
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        viewModel.Layout.Select(RoundTripCylinderId);
        viewport.FitToLayout();

        var initial = viewModel.Layout.Items.Single(item => item.Id == RoundTripCylinderId);
        var initialWidth = initial.CurrentWidth;
        var initialHeight = initial.CurrentHeight;
        var initialRotation = initial.CurrentRotationDegrees;
        var initialBinding = initial.BehaviorBindingId;
        var fixedCornerBefore = Corner(initial, -1d, -1d);
        var itemCenter = viewport.GetItemCenter(RoundTripCylinderId)
            ?? throw new InvalidOperationException("Cylinder center was unavailable.");
        viewport.ZoomAt(itemCenter, 120);
        viewport.PanBy(new Vector(38, -22));

        var resizeHandle = viewport.GetTransformHandleCenter(
            RoundTripCylinderId,
            LayoutTransformHandle.BottomRight);
        Check("singleSelectionShowsResizeHandle", resizeHandle is not null);
        var rotationHandle = viewport.GetTransformHandleCenter(
            RoundTripCylinderId,
            LayoutTransformHandle.Rotation);
        Check("singleSelectionShowsRotationHandle", rotationHandle is not null);
        if (resizeHandle is null || rotationHandle is null)
        {
            throw new InvalidOperationException("Transform handles were unavailable.");
        }

        var cursorPoint = viewport.PointToScreen(resizeHandle.Value);
        SetCursorPos((int)Math.Round(cursorPoint.X), (int)Math.Round(cursorPoint.Y));
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        await Task.Delay(50);
        viewport.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount)
        {
            RoutedEvent = Mouse.MouseMoveEvent
        });
        Check("resizeHandleCursor", viewport.Cursor == Cursors.SizeNWSE);
        Check("navigationDoesNotCreateHistory", !viewModel.UndoLayoutEditCommand.CanExecute(null));

        Check("resizeRequested", viewport.RequestSelectionTransform(
            RoundTripCylinderId,
            LayoutTransformHandle.BottomRight,
            resizeHandle.Value + new Vector(48, 32)));
        var resized = viewModel.Layout.Items.Single(item => item.Id == RoundTripCylinderId);
        var fixedCornerAfter = Corner(resized, -1d, -1d);
        Check("resizeApplied", resized.CurrentWidth > initialWidth && resized.CurrentHeight > initialHeight);
        Check("resizeSnapped",
            Math.Abs(resized.CurrentWidth % viewModel.Layout.GridSize) < 0.000001d &&
            Math.Abs(resized.CurrentHeight % viewModel.Layout.GridSize) < 0.000001d);
        Check("resizeFixedOppositeCorner",
            Math.Abs(fixedCornerAfter.X - fixedCornerBefore.X) < 0.000001d &&
            Math.Abs(fixedCornerAfter.Y - fixedCornerBefore.Y) < 0.000001d);
        Check("resizeCreatesHistory", viewModel.UndoLayoutEditCommand.CanExecute(null));
        Execute(viewModel.UndoLayoutEditCommand);
        var resizeUndone = viewModel.Layout.Items.Single(item => item.Id == RoundTripCylinderId);
        Check("resizeUndo", resizeUndone.CurrentWidth == initialWidth &&
            resizeUndone.CurrentHeight == initialHeight);
        Check("oneUndoEntryPerResize", !viewModel.UndoLayoutEditCommand.CanExecute(null));
        Execute(viewModel.RedoLayoutEditCommand);
        Check("resizeRedo", viewModel.Layout.Items.Single(item => item.Id == RoundTripCylinderId).CurrentWidth ==
            resized.CurrentWidth);
        Execute(viewModel.UndoLayoutEditCommand);

        viewModel.Layout.Select(RoundTripCylinderId);
        var aspectHandle = viewport.GetTransformHandleCenter(
            RoundTripCylinderId,
            LayoutTransformHandle.BottomRight)
            ?? throw new InvalidOperationException("Aspect-ratio resize handle was unavailable.");
        Check("aspectRatioResizeRequested", viewport.RequestSelectionTransform(
            RoundTripCylinderId,
            LayoutTransformHandle.BottomRight,
            aspectHandle + new Vector(80, 10),
            ModifierKeys.Shift));
        var aspectResized = viewModel.Layout.Items.Single(item => item.Id == RoundTripCylinderId);
        var aspectFixedCorner = Corner(aspectResized, -1d, -1d);
        var aspectWidthScale = aspectResized.CurrentWidth / initialWidth;
        var aspectHeightScale = aspectResized.CurrentHeight / initialHeight;
        Check("aspectRatioResizeApplied", aspectResized.CurrentWidth > initialWidth &&
            aspectResized.CurrentHeight > initialHeight);
        Check("aspectRatioPreserved", Math.Abs(aspectWidthScale - aspectHeightScale) < 0.000001d);
        Check("aspectRatioKeepsOppositeCorner",
            Math.Abs(aspectFixedCorner.X - fixedCornerBefore.X) < 0.000001d &&
            Math.Abs(aspectFixedCorner.Y - fixedCornerBefore.Y) < 0.000001d);
        Check("aspectRatioPreservesRotationAndBinding",
            aspectResized.CurrentRotationDegrees == initialRotation &&
            string.Equals(aspectResized.BehaviorBindingId, initialBinding, StringComparison.Ordinal));
        Check("aspectRatioResizeCreatesHistory", viewModel.UndoLayoutEditCommand.CanExecute(null));
        Execute(viewModel.UndoLayoutEditCommand);
        Check("aspectRatioResizeUndo",
            viewModel.Layout.Items.Single(item => item.Id == RoundTripCylinderId).CurrentWidth == initialWidth &&
            viewModel.Layout.Items.Single(item => item.Id == RoundTripCylinderId).CurrentHeight == initialHeight);
        Check("oneUndoEntryPerAspectRatioResize", !viewModel.UndoLayoutEditCommand.CanExecute(null));
        Execute(viewModel.RedoLayoutEditCommand);
        Check("aspectRatioResizeRedo",
            viewModel.Layout.Items.Single(item => item.Id == RoundTripCylinderId).CurrentWidth ==
                aspectResized.CurrentWidth &&
            viewModel.Layout.Items.Single(item => item.Id == RoundTripCylinderId).CurrentHeight ==
                aspectResized.CurrentHeight);
        Execute(viewModel.UndoLayoutEditCommand);

        viewModel.Layout.Select(RoundTripCylinderId);
        var rotationCenter = viewport.GetItemCenter(RoundTripCylinderId)
            ?? throw new InvalidOperationException("Cylinder center was unavailable after resize Undo.");
        Check("rotationRequested", viewport.RequestSelectionTransform(
            RoundTripCylinderId,
            LayoutTransformHandle.Rotation,
            rotationCenter + new Vector(64, 0)));
        var rotated = viewModel.Layout.Items.Single(item => item.Id == RoundTripCylinderId);
        Check("rotationApplied", Math.Abs(rotated.CurrentRotationDegrees - 90d) < 0.000001d);
        Check("rotationCreatesHistory", viewModel.UndoLayoutEditCommand.CanExecute(null));
        Execute(viewModel.UndoLayoutEditCommand);
        Check("rotationUndo",
            viewModel.Layout.Items.Single(item => item.Id == RoundTripCylinderId).CurrentRotationDegrees ==
            initialRotation);
        Check("oneUndoEntryPerRotation", !viewModel.UndoLayoutEditCommand.CanExecute(null));

        viewModel.Layout.Select(RoundTripCylinderId);
        var cancelItem = viewModel.Layout.Items.Single(item => item.Id == RoundTripCylinderId);
        Check("cancelBegins", viewModel.Layout.BeginSelectionTransform(LayoutTransformHandle.TopLeft));
        viewModel.Layout.UpdateSelectionTransform(
            cancelItem.CurrentX - 40,
            cancelItem.CurrentY - 25,
            preserveAspectRatio: true);
        viewModel.Layout.CancelSelectionTransform();
        Check("cancelRestores", cancelItem.CurrentWidth == initialWidth &&
            cancelItem.CurrentHeight == initialHeight &&
            cancelItem.CurrentRotationDegrees == initialRotation);
        Check("cancelDoesNotCreateHistory", !viewModel.UndoLayoutEditCommand.CanExecute(null));

        viewModel.Layout.SelectMany(new[] { RoundTripStageId, RoundTripCylinderId }, RoundTripCylinderId);
        Check("multiSelectionHidesHandles", viewport.GetTransformHandleCenter(
            RoundTripCylinderId,
            LayoutTransformHandle.BottomRight) is null);
        viewModel.Layout.Select(RoundTripCylinderId);
        viewModel.IsRunMode = true;
        Check("runModeHidesHandles", viewport.GetTransformHandleCenter(
            RoundTripCylinderId,
            LayoutTransformHandle.BottomRight) is null);
        Check("runModeBlocksTransform",
            !viewModel.Layout.BeginSelectionTransform(LayoutTransformHandle.BottomRight));
        viewModel.IsRunMode = false;

        viewModel.Layout.Select(RoundTripCylinderId);
        var finalHandle = viewport.GetTransformHandleCenter(
            RoundTripCylinderId,
            LayoutTransformHandle.BottomRight)
            ?? throw new InvalidOperationException("Final resize handle was unavailable.");
        viewport.RequestSelectionTransform(
            RoundTripCylinderId,
            LayoutTransformHandle.BottomRight,
            finalHandle + new Vector(56, 18),
            ModifierKeys.Shift);
        var persisted = viewModel.Layout.Items.Single(item => item.Id == RoundTripCylinderId);
        var persistedSize = (persisted.CurrentWidth, persisted.CurrentHeight);
        Check("persistedAspectRatioPreserved",
            Math.Abs(
                (persisted.CurrentWidth / initialWidth) -
                (persisted.CurrentHeight / initialHeight)) < 0.000001d);
        var reportDirectory = Path.GetDirectoryName(Path.GetFullPath(reportPath))!;
        Directory.CreateDirectory(reportDirectory);
        var projectPath = Path.Combine(reportDirectory, "direct-transform-roundtrip.ovmachine");
        await viewModel.SaveProjectAsync(projectPath);
        Check("roundTripOpen", await viewModel.OpenProjectAsync(projectPath));
        var reopened = viewModel.Layout.Items.Single(item => item.Id == RoundTripCylinderId);
        Check("transformPersists", reopened.CurrentWidth == persistedSize.CurrentWidth &&
            reopened.CurrentHeight == persistedSize.CurrentHeight);
        viewModel.Layout.Select(RoundTripCylinderId);
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Check("aspectRatioHintVisible", findInspector(window)?
            .AspectRatioHintText is { IsVisible: true, Text.Length: > 0 });
        Check("reopenDoesNotRun", viewModel.IsDesignMode && !viewModel.IsRunning &&
            !viewModel.UndoLayoutEditCommand.CanExecute(null));

        return new SmokeDirectTransformReport
        {
            Checks = checks,
            Failures = failures
        };
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCursorPos(int x, int y);
}
