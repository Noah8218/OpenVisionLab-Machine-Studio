using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using OpenVisionLab.MachineStudio.Model;
using OpenVisionLab.MachineStudio.View.Scene;
using OpenVisionLab.MachineStudio.View.Shell;
using OpenVisionLab.MachineStudio.ViewModel;
using static OpenVisionLab.MachineStudio.SmokeRoundTripScenario;

namespace OpenVisionLab.MachineStudio;

internal sealed class SmokeMultiSelectionTransformReport
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

internal static class SmokeMultiSelectionTransformVerifier
{
    public static async Task<SmokeMultiSelectionTransformReport> VerifyAsync(
        ShellWindow window,
        MainViewModel viewModel,
        string reportPath,
        Func<DependencyObject, MachineSceneViewport?> findViewport)
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

        static IReadOnlyDictionary<string, (
            double X,
            double Y,
            double Width,
            double Height,
            double Rotation,
            string? BindingId)> States(
                MachineLayoutViewModel layout,
                IEnumerable<string> ids) => ids.ToDictionary(
                    id => id,
                    id =>
                    {
                        var item = layout.Items.Single(candidate => candidate.Id == id);
                        return (
                            item.CurrentX,
                            item.CurrentY,
                            item.CurrentWidth,
                            item.CurrentHeight,
                            item.CurrentRotationDegrees,
                            item.BehaviorBindingId);
                    },
                    StringComparer.Ordinal);

        static bool Same(
            MachineLayoutViewModel layout,
            IReadOnlyDictionary<string, (
                double X,
                double Y,
                double Width,
                double Height,
                double Rotation,
                string? BindingId)> expected) => expected.All(entry =>
            {
                var item = layout.Items.Single(candidate => candidate.Id == entry.Key);
                return item.CurrentX == entry.Value.X &&
                    item.CurrentY == entry.Value.Y &&
                    item.CurrentWidth == entry.Value.Width &&
                    item.CurrentHeight == entry.Value.Height &&
                    item.CurrentRotationDegrees == entry.Value.Rotation &&
                    string.Equals(item.BehaviorBindingId, entry.Value.BindingId, StringComparison.Ordinal);
            });

        static (double MinimumX, double MinimumY, double MaximumX, double MaximumY) Bounds(
            IReadOnlyDictionary<string, (
                double X,
                double Y,
                double Width,
                double Height,
                double Rotation,
                string? BindingId)> states)
        {
            var minimumX = double.PositiveInfinity;
            var minimumY = double.PositiveInfinity;
            var maximumX = double.NegativeInfinity;
            var maximumY = double.NegativeInfinity;
            foreach (var state in states.Values)
            {
                var radians = state.Rotation * Math.PI / 180d;
                var cosine = Math.Abs(Math.Cos(radians));
                var sine = Math.Abs(Math.Sin(radians));
                var halfWidth = ((state.Width * cosine) + (state.Height * sine)) / 2d;
                var halfHeight = ((state.Width * sine) + (state.Height * cosine)) / 2d;
                minimumX = Math.Min(minimumX, state.X - halfWidth);
                minimumY = Math.Min(minimumY, state.Y - halfHeight);
                maximumX = Math.Max(maximumX, state.X + halfWidth);
                maximumY = Math.Max(maximumY, state.Y + halfHeight);
            }
            return (minimumX, minimumY, maximumX, maximumY);
        }

        var viewport = findViewport(window)
            ?? throw new InvalidOperationException("Machine scene viewport was not available.");
        var ids = new[] { RoundTripStageId, RoundTripCylinderId };
        viewModel.Layout.SelectMany(ids, RoundTripCylinderId);
        viewport.FitToLayout();
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);

        var initial = States(viewModel.Layout, ids);
        var initialBounds = Bounds(initial);
        var initialBindings = initial.ToDictionary(
            entry => entry.Key,
            entry => entry.Value.BindingId,
            StringComparer.Ordinal);
        var bottomRight = viewport.GetSelectionTransformHandleCenter(
            LayoutTransformHandle.BottomRight);
        var rotationHandle = viewport.GetSelectionTransformHandleCenter(
            LayoutTransformHandle.Rotation);
        Check("groupShowsResizeHandle", bottomRight is not null);
        Check("groupShowsRotationHandle", rotationHandle is not null);
        Check("groupHidesSingleItemHandles", viewport.GetTransformHandleCenter(
            RoundTripCylinderId,
            LayoutTransformHandle.BottomRight) is null);
        if (bottomRight is null || rotationHandle is null)
        {
            throw new InvalidOperationException("Multi-selection transform handles were unavailable.");
        }

        var center = viewport.GetItemCenter(RoundTripCylinderId)
            ?? throw new InvalidOperationException("Cylinder center was unavailable.");
        viewport.ZoomAt(center, 120);
        viewport.PanBy(new Vector(34, -20));
        bottomRight = viewport.GetSelectionTransformHandleCenter(LayoutTransformHandle.BottomRight)
            ?? throw new InvalidOperationException("Group resize handle was unavailable after navigation.");
        var cursorPoint = viewport.PointToScreen(bottomRight.Value);
        SetCursorPos((int)Math.Round(cursorPoint.X), (int)Math.Round(cursorPoint.Y));
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        await Task.Delay(50);
        viewport.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount)
        {
            RoutedEvent = Mouse.MouseMoveEvent
        });
        Check("groupResizeCursor", viewport.Cursor == Cursors.SizeNWSE);
        Check("navigationDoesNotCreateHistory", !viewModel.UndoLayoutEditCommand.CanExecute(null));

        Check("groupResizeRequested", viewport.RequestSelectionTransform(
            LayoutTransformHandle.BottomRight,
            bottomRight.Value + new Vector(72, 44)));
        var resized = States(viewModel.Layout, ids);
        var resizedBounds = Bounds(resized);
        Check("groupResizeApplied", ids.All(id =>
            resized[id].Width > initial[id].Width &&
            resized[id].Height > initial[id].Height));
        Check("groupResizeKeepsOppositeCorner",
            Math.Abs(resizedBounds.MinimumX - initialBounds.MinimumX) < 0.000001d &&
            Math.Abs(resizedBounds.MinimumY - initialBounds.MinimumY) < 0.000001d);
        Check("groupResizeUsesCommonScale",
            Math.Abs(
                (resized[ids[0]].Width / initial[ids[0]].Width) -
                (resized[ids[1]].Width / initial[ids[1]].Width)) < 0.000001d &&
            Math.Abs(
                (resized[ids[0]].Height / initial[ids[0]].Height) -
                (resized[ids[1]].Height / initial[ids[1]].Height)) < 0.000001d);
        Check("groupResizePreservesBindings", ids.All(id => string.Equals(
            resized[id].BindingId,
            initialBindings[id],
            StringComparison.Ordinal)));
        Check("groupResizeCreatesHistory", viewModel.UndoLayoutEditCommand.CanExecute(null));
        viewModel.UndoLayoutEditCommand.Execute(null);
        Check("groupResizeUndo", Same(viewModel.Layout, initial));
        Check("oneUndoEntryPerGroupResize", !viewModel.UndoLayoutEditCommand.CanExecute(null));
        viewModel.RedoLayoutEditCommand.Execute(null);
        Check("groupResizeRedo", Same(viewModel.Layout, resized));
        viewModel.UndoLayoutEditCommand.Execute(null);

        viewModel.Layout.SelectMany(ids, RoundTripCylinderId);
        var aspectHandle = viewport.GetSelectionTransformHandleCenter(
            LayoutTransformHandle.BottomRight)
            ?? throw new InvalidOperationException("Group aspect-ratio resize handle was unavailable.");
        Check("groupAspectRatioResizeRequested", viewport.RequestSelectionTransform(
            LayoutTransformHandle.BottomRight,
            aspectHandle + new Vector(96, 24),
            ModifierKeys.Shift));
        var aspectResized = States(viewModel.Layout, ids);
        var aspectBounds = Bounds(aspectResized);
        var groupAspectWidthScale =
            (aspectBounds.MaximumX - aspectBounds.MinimumX) /
            (initialBounds.MaximumX - initialBounds.MinimumX);
        var groupAspectHeightScale =
            (aspectBounds.MaximumY - aspectBounds.MinimumY) /
            (initialBounds.MaximumY - initialBounds.MinimumY);
        Check("groupAspectRatioResizeApplied", ids.All(id =>
            aspectResized[id].Width > initial[id].Width &&
            aspectResized[id].Height > initial[id].Height));
        Check("groupAspectRatioPreserved",
            Math.Abs(groupAspectWidthScale - groupAspectHeightScale) < 0.000001d);
        Check("groupAspectRatioUsesUniformItemScale", ids.All(id =>
            Math.Abs(
                (aspectResized[id].Width / initial[id].Width) -
                (aspectResized[id].Height / initial[id].Height)) < 0.000001d &&
            Math.Abs(
                (aspectResized[id].Width / initial[id].Width) -
                groupAspectWidthScale) < 0.000001d));
        Check("groupAspectRatioKeepsOppositeCorner",
            Math.Abs(aspectBounds.MinimumX - initialBounds.MinimumX) < 0.000001d &&
            Math.Abs(aspectBounds.MinimumY - initialBounds.MinimumY) < 0.000001d);
        Check("groupAspectRatioPreservesRotationsAndBindings", ids.All(id =>
            aspectResized[id].Rotation == initial[id].Rotation &&
            string.Equals(aspectResized[id].BindingId, initialBindings[id], StringComparison.Ordinal)));
        Check("groupAspectRatioResizeCreatesHistory", viewModel.UndoLayoutEditCommand.CanExecute(null));
        viewModel.UndoLayoutEditCommand.Execute(null);
        Check("groupAspectRatioResizeUndo", Same(viewModel.Layout, initial));
        Check("oneUndoEntryPerGroupAspectRatioResize", !viewModel.UndoLayoutEditCommand.CanExecute(null));
        viewModel.RedoLayoutEditCommand.Execute(null);
        Check("groupAspectRatioResizeRedo", Same(viewModel.Layout, aspectResized));
        viewModel.UndoLayoutEditCommand.Execute(null);

        viewModel.Layout.SelectMany(ids, RoundTripCylinderId);
        var topLeft = viewport.GetSelectionTransformHandleCenter(LayoutTransformHandle.TopLeft)
            ?? throw new InvalidOperationException("Group top-left handle was unavailable.");
        var groupBottomRight = viewport.GetSelectionTransformHandleCenter(LayoutTransformHandle.BottomRight)
            ?? throw new InvalidOperationException("Group bottom-right handle was unavailable.");
        var groupCenter = new Point(
            (topLeft.X + groupBottomRight.X) / 2d,
            (topLeft.Y + groupBottomRight.Y) / 2d);
        Check("groupRotationRequested", viewport.RequestSelectionTransform(
            LayoutTransformHandle.Rotation,
            groupCenter + new Vector(80, 0)));
        var rotated = States(viewModel.Layout, ids);
        var initialCenterX = (initialBounds.MinimumX + initialBounds.MaximumX) / 2d;
        var initialCenterY = (initialBounds.MinimumY + initialBounds.MaximumY) / 2d;
        Check("groupRotationApplied", ids.All(id =>
            Math.Abs(rotated[id].Rotation - 90d) < 0.000001d));
        Check("groupCentersRotateTogether", ids.All(id =>
            Math.Abs(rotated[id].X - (initialCenterX - (initial[id].Y - initialCenterY))) < 0.000001d &&
            Math.Abs(rotated[id].Y - (initialCenterY + (initial[id].X - initialCenterX))) < 0.000001d));
        Check("groupRotationPreservesSize", ids.All(id =>
            rotated[id].Width == initial[id].Width &&
            rotated[id].Height == initial[id].Height));
        Check("groupRotationPreservesBindings", ids.All(id => string.Equals(
            rotated[id].BindingId,
            initialBindings[id],
            StringComparison.Ordinal)));
        Check("groupRotationCreatesHistory", viewModel.UndoLayoutEditCommand.CanExecute(null));
        viewModel.UndoLayoutEditCommand.Execute(null);
        Check("groupRotationUndo", Same(viewModel.Layout, initial));
        Check("oneUndoEntryPerGroupRotation", !viewModel.UndoLayoutEditCommand.CanExecute(null));

        viewModel.Layout.SelectMany(ids, RoundTripCylinderId);
        Check("groupCancelBegins", viewModel.Layout.BeginSelectionTransform(
            LayoutTransformHandle.BottomRight));
        viewModel.Layout.UpdateSelectionTransform(
            initialBounds.MaximumX + 100,
            initialBounds.MaximumY + 80,
            preserveAspectRatio: true);
        viewModel.Layout.CancelSelectionTransform();
        Check("groupCancelRestores", Same(viewModel.Layout, initial));
        Check("groupCancelDoesNotCreateHistory", !viewModel.UndoLayoutEditCommand.CanExecute(null));

        viewModel.IsRunMode = true;
        Check("runModeHidesGroupHandles", viewport.GetSelectionTransformHandleCenter(
            LayoutTransformHandle.BottomRight) is null);
        Check("runModeBlocksGroupTransform", !viewModel.Layout.BeginSelectionTransform(
            LayoutTransformHandle.BottomRight));
        viewModel.IsRunMode = false;

        viewModel.Layout.Select(RoundTripCylinderId);
        Check("singleSelectionStillShowsHandles", viewport.GetTransformHandleCenter(
            RoundTripCylinderId,
            LayoutTransformHandle.BottomRight) is not null);
        Check("singleSelectionHidesGroupHandles", viewport.GetSelectionTransformHandleCenter(
            LayoutTransformHandle.BottomRight) is null);

        viewModel.Layout.SelectMany(ids, RoundTripCylinderId);
        topLeft = viewport.GetSelectionTransformHandleCenter(LayoutTransformHandle.TopLeft)
            ?? throw new InvalidOperationException("Final group top-left handle was unavailable.");
        groupBottomRight = viewport.GetSelectionTransformHandleCenter(LayoutTransformHandle.BottomRight)
            ?? throw new InvalidOperationException("Final group bottom-right handle was unavailable.");
        groupCenter = new Point(
            (topLeft.X + groupBottomRight.X) / 2d,
            (topLeft.Y + groupBottomRight.Y) / 2d);
        viewport.RequestSelectionTransform(
            LayoutTransformHandle.Rotation,
            groupCenter + new Vector(80, 0));
        var persisted = States(viewModel.Layout, ids);
        var reportDirectory = Path.GetDirectoryName(Path.GetFullPath(reportPath))!;
        Directory.CreateDirectory(reportDirectory);
        var projectPath = Path.Combine(reportDirectory, "multi-selection-transform-roundtrip.ovmachine");
        await viewModel.SaveProjectAsync(projectPath);
        Check("roundTripOpen", await viewModel.OpenProjectAsync(projectPath));
        Check("groupTransformPersists", Same(viewModel.Layout, persisted));
        Check("reopenDoesNotRun", viewModel.IsDesignMode && !viewModel.IsRunning &&
            !viewModel.UndoLayoutEditCommand.CanExecute(null));

        return new SmokeMultiSelectionTransformReport
        {
            Checks = checks,
            Failures = failures
        };
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCursorPos(int x, int y);
}
