using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using OpenVisionLab.Machine.IO.Channels;
using OpenVisionLab.Machine.Core.Layouts;
using OpenVisionLab.Machine.Sequence.Runtime;
using OpenVisionLab.Machine.Simulation.Axis;
using OpenVisionLab.Machine.Simulation.Camera;
using OpenVisionLab.Machine.Simulation.Engine;
using OpenVisionLab.Machine.Simulation.Layout;
using OpenVisionLab.Machine.Simulation.Snapshots;
using OpenVisionLab.MachineStudio.Model;
using OpenVisionLab.MachineStudio.View.Scene;
using OpenVisionLab.MachineStudio.ViewModel;
using OpenVisionLab.TestSupport;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

[Collection(StudioUiTestCollection.Name)]
public sealed class SceneViewportRenderingTests
{
    private readonly StudioUiTestHost _ui;

    public SceneViewportRenderingTests(StudioUiTestHost ui) => _ui = ui;

    [Theory]
    [InlineData(false, 640)]
    [InlineData(true, 640)]
    [InlineData(false, 1000)]
    [InlineData(true, 1000)]
    public async Task SelectedLabelAvoidsActualOverlayAndRecoversAfterResizeAndCollapse(bool oblique, double width)
    {
        await _ui.InvokeAsync(() =>
        {
            var frame = CreateItem("frame", LayoutComponentKind.MachineFrame, 0, 0, 400, 250);
            var camera = CreateItem("camera-right", LayoutComponentKind.Camera, 530, 120, 24, 20);
            camera.IsSelected = true;
            var viewport = new MachineSceneViewport { IsObliqueView = oblique, ItemsSource = new[] { frame, camera }, SelectedItem = camera };
            var overlay = new Border
            {
                Width = 260, Height = 400, HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 60, 16, 0), Background = Brushes.Black
            };
            var root = new Grid();
            root.Children.Add(viewport);
            root.Children.Add(overlay);
            MachineSceneViewport.SetLabelOverlayElement(root, overlay);
            var window = new Window { Width = width, Height = 480, Content = root, WindowStyle = WindowStyle.None, ShowActivated = false, ShowInTaskbar = false };
            try
            {
                window.Show();
                window.UpdateLayout();
                PumpRenderDispatcher();
                Assert.Same(overlay, MachineSceneViewport.GetLabelOverlayElement(viewport));
                var panelBounds = overlay.TransformToVisual(viewport).TransformBounds(new Rect(overlay.RenderSize));
                var label = Assert.IsType<Rect>(viewport.LastRenderedSelectedLabelBounds);
                Assert.False(label.IntersectsWith(panelBounds), $"Label {label}; panel {panelBounds}");
                Assert.True(new Rect(viewport.RenderSize).Contains(label));
                var before = Capture(viewport);
                camera.CurrentName = string.Concat(Enumerable.Repeat("검사 위치의 긴 카메라 이름 ", 8));
                var longName = Capture(viewport);
                label = Assert.IsType<Rect>(viewport.LastRenderedSelectedLabelBounds);
                Assert.False(label.IntersectsWith(panelBounds));
                Assert.True(new Rect(viewport.RenderSize).Contains(label));
                Assert.True(label.Height > 20, "The full name and ID must wrap rather than disappear under the panel.");
                Assert.True(CountDifferentPixelsInRegion(before, longName, 0, (int)panelBounds.Left) > 10);
                var accent = (viewport.TryFindResource("Accent.Primary") as SolidColorBrush)?.Color ?? Color.FromRgb(0x3A, 0x8D, 0xFF);
                var visibleTextPixels = 0;
                for (var y = (int)label.Top + 2; y < Math.Min(label.Bottom, label.Top + 14); y++)
                for (var x = (int)(label.Left + label.Width / 4); x < label.Left + label.Width * 3 / 4; x++)
                {
                    var index = (y * longName.Width + x) * 4;
                    if (Math.Abs(longName.Pixels[index] - accent.B) < 8 && Math.Abs(longName.Pixels[index + 1] - accent.G) < 8 && Math.Abs(longName.Pixels[index + 2] - accent.R) < 8) visibleTextPixels++;
                }
                Assert.True(visibleTextPixels > 3, "The center of the label must remain visible above scene geometry.");
                Save(longName.Bitmap, $"label-overlay-{oblique}-{width}.png", Path.Combine(TestStorage.RootPath, "label-render"));

                camera.CurrentName = "Camera";
                window.Width += 120;
                window.UpdateLayout();
                PumpRenderDispatcher();
                panelBounds = overlay.TransformToVisual(viewport).TransformBounds(new Rect(overlay.RenderSize));
                label = Assert.IsType<Rect>(viewport.LastRenderedSelectedLabelBounds);
                Assert.False(label.IntersectsWith(panelBounds));
                var protectedLabel = label;
                overlay.Visibility = Visibility.Collapsed;
                window.UpdateLayout();
                PumpRenderDispatcher();
                label = Assert.IsType<Rect>(viewport.LastRenderedSelectedLabelBounds);
                Assert.NotEqual(protectedLabel, label);
                Assert.True(new Rect(viewport.RenderSize).Contains(label));
                overlay.Visibility = Visibility.Visible;
                window.UpdateLayout();
                PumpRenderDispatcher();
                Assert.Equal(protectedLabel, viewport.LastRenderedSelectedLabelBounds);
                return true;
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SelectedComponentNameIsRenderedInBothProjections(bool oblique)
    {
        await _ui.InvokeAsync(() =>
        {
            var item = CreateItem("AX-002", LayoutComponentKind.LinearStage, 180, 120, 100, 28);
            item.IsSelected = true;
            var viewport = new MachineSceneViewport { Width = 640, Height = 420, IsObliqueView = oblique, ItemsSource = new[] { item } };
            var window = new Window { Content = viewport, Width = 640, Height = 420, ShowInTaskbar = false, ShowActivated = false, WindowStyle = WindowStyle.None };
            try
            {
                window.Show();
                window.UpdateLayout();
                var before = Capture(viewport);
                item.CurrentName = "검사 위치 정렬 축";
                window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(window.UpdateLayout));
                var after = Capture(viewport);
                Assert.True(CountDifferentPixels(before.Pixels, after.Pixels) > 10, "The selected name must be visible in the scene, not only the outline.");
                Save(after.Bitmap, $"selected-label-{(oblique ? "oblique" : "plan")}.png", Path.Combine(TestStorage.RootPath, "r19-batch-1-4-20260930", "inprocess"));
                return true;
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public async Task EveryLayoutComponentKindRendersInPlanAndObliqueViews()
    {
        var result = await _ui.InvokeAsync(() => RenderEquipmentViews());

        Assert.Equal(640, result.Width);
        Assert.Equal(420, result.Height);
        Assert.True(result.ScenePixelDifferenceCount > 100);
        Assert.True(result.ProjectionPixelDifferenceCount > 100);
        Assert.Equal(Enum.GetValues<LayoutComponentKind>(), result.KindPixelDifferences.Select(item => item.Kind));
        Assert.All(result.KindPixelDifferences, item => Assert.True(item.Plan > 10, $"{item.Kind} plan pixels: {item.Plan}"));
        Assert.All(result.KindPixelDifferences, item => Assert.True(item.Oblique > 10, $"{item.Kind} oblique pixels: {item.Oblique}"));
    }

    [Fact]
    public async Task RuntimeSnapshotsRenderIndependentConveyorStatesInPlanAndObliqueViews()
    {
        var result = await _ui.InvokeAsync(() => RenderConveyorStates());

        AssertStateDifferences(result.Plan);
        AssertStateDifferences(result.Oblique);
    }

    private static RenderResult RenderEquipmentViews()
    {
        var items = new[]
        {
            CreateItem("frame-1", LayoutComponentKind.MachineFrame, 40, 30, 120, 70),
            CreateItem("linear-stage-1", LayoutComponentKind.LinearStage, 190, 30, 100, 28),
            CreateItem("rotary-stage-1", LayoutComponentKind.RotaryStage, 330, 30, 64, 64),
            CreateItem("sensor-1", LayoutComponentKind.DigitalSensor, 40, 145, 32, 32),
            CreateItem("cylinder-1", LayoutComponentKind.PneumaticCylinder, 115, 145, 74, 32),
            CreateItem("conveyor-1", LayoutComponentKind.Conveyor, 220, 145, 140, 36),
            CreateItem("workpiece-1", LayoutComponentKind.Workpiece, 410, 145, 40, 40),
            CreateItem("camera-1", LayoutComponentKind.Camera, 500, 30, 44, 36)
        };
        var viewport = new MachineSceneViewport
        {
            Height = 420,
            ItemsSource = items,
            IsObliqueView = false,
            Width = 640
        };
        var window = new Window
        {
            Content = viewport,
            Height = 448,
            Left = -2000,
            ShowActivated = false,
            ShowInTaskbar = false,
            Top = -2000,
            Width = 654,
            WindowStyle = WindowStyle.None
        };

        try
        {
            window.Show();
            window.UpdateLayout();
            viewport.UpdateLayout();

            var planScene = Capture(viewport);
            viewport.ItemsSource = Array.Empty<LayoutItem>();
            viewport.UpdateLayout();
            var planBackground = Capture(viewport);

            var planKindDifferences = items.Select(item =>
            {
                viewport.ItemsSource = [item];
                viewport.UpdateLayout();
                return CountDifferentPixels(planBackground.Pixels, Capture(viewport).Pixels);
            }).ToArray();

            viewport.ItemsSource = items;
            viewport.IsObliqueView = true;
            window.UpdateLayout();
            viewport.UpdateLayout();
            var obliqueScene = Capture(viewport);
            viewport.ItemsSource = Array.Empty<LayoutItem>();
            viewport.UpdateLayout();
            var obliqueBackground = Capture(viewport);
            var obliqueKindDifferences = items.Select(item =>
            {
                viewport.ItemsSource = [item];
                viewport.UpdateLayout();
                return CountDifferentPixels(obliqueBackground.Pixels, Capture(viewport).Pixels);
            }).ToArray();

            Save(planScene.Bitmap, "equipment-all-kinds-plan-viewport-640x420.png");
            Save(obliqueScene.Bitmap, "equipment-all-kinds-oblique-viewport-640x420.png");

            return new RenderResult(
                planScene.Width,
                planScene.Height,
                CountDifferentPixels(planScene.Pixels, planBackground.Pixels),
                CountDifferentPixels(planScene.Pixels, obliqueScene.Pixels),
                items.Select((item, index) => new KindPixelDifference(
                    item.Component!.Kind,
                    planKindDifferences[index],
                    obliqueKindDifferences[index])).ToArray());
        }
        finally
        {
            window.Close();
        }
    }

    private static ConveyorProjectionResult RenderConveyorStates()
    {
        var items = new[]
        {
            CreateItem("conveyor-left", LayoutComponentKind.Conveyor, 70, 145, 150, 36),
            CreateItem("workpiece-left", LayoutComponentKind.Workpiece, 120, 158, 28, 28),
            CreateItem("conveyor-right", LayoutComponentKind.Conveyor, 470, 145, 150, 36),
            CreateItem("workpiece-right", LayoutComponentKind.Workpiece, 520, 158, 28, 28)
        };
        var states = new[]
        {
            new ConveyorState("both-running", true, true),
            new ConveyorState("left-stopped", false, true),
            new ConveyorState("right-stopped", true, false),
            new ConveyorState("both-stopped", false, false)
        };

        var plan = RenderConveyorProjection(items, states, isOblique: false);
        var oblique = RenderConveyorProjection(items, states, isOblique: true);
        return new ConveyorProjectionResult(plan, oblique);
    }

    private static IReadOnlyDictionary<string, CapturedBitmap> RenderConveyorProjection(
        IReadOnlyList<LayoutItem> items,
        IReadOnlyList<ConveyorState> states,
        bool isOblique)
    {
        var frames = new Dictionary<string, CapturedBitmap>(StringComparer.Ordinal);
        var store = new SceneSnapshotStore();
        store.Publish(CreateConveyorSnapshot(states[0]));
        var viewport = new MachineSceneViewport
        {
            Height = 460,
            IsDesignMode = false,
            IsObliqueView = isOblique,
            ItemsSource = items,
            SnapshotSource = store,
            Width = 800
        };
        var window = new Window
        {
            Content = viewport,
            Height = 488,
            Left = -2000,
            ShowActivated = false,
            ShowInTaskbar = false,
            Top = -2000,
            Width = 814,
            WindowStyle = WindowStyle.None
        };

        try
        {
            window.Show();
            window.UpdateLayout();
            viewport.UpdateLayout();
            foreach (var (state, index) in states.Select((state, index) => (state, index)))
            {
                if (index > 0)
                {
                    store.Publish(CreateConveyorSnapshot(state));
                    PumpRenderDispatcher();
                }

                frames.Add(state.Name, Capture(viewport));
            }
        }
        finally
        {
            window.Close();
        }

        var outputDirectory = Path.Combine(TestStorage.RootPath, "r19-priority-24-conveyor-runtime-render-20260929");
        foreach (var frame in frames)
        {
            Save(frame.Value.Bitmap, $"{frame.Key}-{(isOblique ? "oblique" : "plan")}-viewport-800x460.png", outputDirectory);
        }

        return frames;
    }

    private static void PumpRenderDispatcher()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(
            DispatcherPriority.ApplicationIdle,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static SimulationSnapshot CreateConveyorSnapshot(ConveyorState state) => new(
        TimeSpan.Zero,
        0,
        SimulationRunMode.Paused,
        SimulationControlOwner.Manual,
        1,
        Array.Empty<AxisSnapshot>(),
        0,
        Array.Empty<DigitalSignalSnapshot>(),
        Array.Empty<SequenceExecutionSnapshot>(),
        Array.Empty<VirtualCameraSnapshot>(),
        AutomaticRunSnapshot.NotConfigured,
        [
            new LayoutComponentSnapshot("conveyor-left", "Left conveyor", LayoutComponentKind.Conveyor, 70, 145, 0, 150, 36, null, null, ConveyorRunning: state.LeftRunning, ConveyorDirection: ConveyorDirection.Forward),
            new LayoutComponentSnapshot("workpiece-left", "Left workpiece", LayoutComponentKind.Workpiece, 120, 158, 0, 28, 28, null, null, ConveyorRunning: null, WorkpieceType: "sample", IsWorkpiecePresent: true),
            new LayoutComponentSnapshot("conveyor-right", "Right conveyor", LayoutComponentKind.Conveyor, 470, 145, 0, 150, 36, null, null, ConveyorRunning: state.RightRunning, ConveyorDirection: ConveyorDirection.Reverse),
            new LayoutComponentSnapshot("workpiece-right", "Right workpiece", LayoutComponentKind.Workpiece, 520, 158, 0, 28, 28, null, null, ConveyorRunning: null, WorkpieceType: "sample", IsWorkpiecePresent: true)
        ]);

    private static void AssertStateDifferences(IReadOnlyDictionary<string, CapturedBitmap> frames)
    {
        var bothRunning = frames["both-running"];
        var leftStopped = frames["left-stopped"];
        var rightStopped = frames["right-stopped"];
        var bothStopped = frames["both-stopped"];
        Assert.Equal((800, 460), (bothRunning.Width, bothRunning.Height));
        Assert.True(CountDifferentPixelsInRegion(bothRunning, leftStopped, 0, 400) > 10, "Stopping the left conveyor must update its rendered runtime state.");
        Assert.Equal(0, CountDifferentPixelsInRegion(bothRunning, leftStopped, 400, 800));
        Assert.Equal(0, CountDifferentPixelsInRegion(bothRunning, rightStopped, 0, 400));
        Assert.True(CountDifferentPixelsInRegion(bothRunning, rightStopped, 400, 800) > 10, "Stopping the right conveyor must update its rendered runtime state.");
        Assert.True(CountDifferentPixelsInRegion(leftStopped, rightStopped, 0, 400) > 10);
        Assert.True(CountDifferentPixelsInRegion(leftStopped, rightStopped, 400, 800) > 10);
        Assert.True(CountDifferentPixelsInRegion(bothRunning, bothStopped, 0, 400) > 10);
        Assert.True(CountDifferentPixelsInRegion(bothRunning, bothStopped, 400, 800) > 10);
    }

    private static LayoutItem CreateItem(
        string id,
        LayoutComponentKind kind,
        double x,
        double y,
        double width,
        double height) =>
        new(new LayoutComponentDefinition
        {
            Id = id,
            Name = id,
            Kind = kind,
            Transform = new Transform2D { X = x, Y = y },
            Size = new Size2D { Width = width, Height = height }
        }, gridSize: 10, snapToGrid: true);

    private static CapturedBitmap Capture(MachineSceneViewport viewport)
    {
        var width = (int)viewport.ActualWidth;
        var height = (int)viewport.ActualHeight;
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(viewport);
        var pixels = new byte[width * height * 4];
        bitmap.CopyPixels(pixels, width * 4, 0);
        return new CapturedBitmap(width, height, bitmap, pixels);
    }

    private static int CountDifferentPixels(byte[] first, byte[] second)
    {
        var count = 0;
        for (var index = 0; index < first.Length; index += 4)
        {
            if (first[index] != second[index]
                || first[index + 1] != second[index + 1]
                || first[index + 2] != second[index + 2]
                || first[index + 3] != second[index + 3])
            {
                count++;
            }
        }

        return count;
    }

    private static int CountDifferentPixelsInRegion(CapturedBitmap first, CapturedBitmap second, int left, int right)
    {
        var count = 0;
        for (var y = 0; y < first.Height; y++)
        {
            for (var x = left; x < right; x++)
            {
                var index = (y * first.Width + x) * 4;
                if (first.Pixels[index] != second.Pixels[index]
                    || first.Pixels[index + 1] != second.Pixels[index + 1]
                    || first.Pixels[index + 2] != second.Pixels[index + 2]
                    || first.Pixels[index + 3] != second.Pixels[index + 3])
                {
                    count++;
                }
            }
        }

        return count;
    }

    private static void Save(BitmapSource bitmap, string fileName)
    {
        Save(bitmap, fileName, Path.Combine(TestStorage.RootPath, "r19-equipment-inprocess-render-all-kinds-20260929"));
    }

    private static void Save(BitmapSource bitmap, string fileName, string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(outputDirectory, fileName));
        encoder.Save(stream);
    }

    private sealed record CapturedBitmap(int Width, int Height, BitmapSource Bitmap, byte[] Pixels);

    private sealed record ConveyorState(string Name, bool LeftRunning, bool RightRunning);

    private sealed record ConveyorProjectionResult(
        IReadOnlyDictionary<string, CapturedBitmap> Plan,
        IReadOnlyDictionary<string, CapturedBitmap> Oblique);

    private sealed record KindPixelDifference(LayoutComponentKind Kind, int Plan, int Oblique);

    private sealed record RenderResult(
        int Width,
        int Height,
        int ScenePixelDifferenceCount,
        int ProjectionPixelDifferenceCount,
        IReadOnlyList<KindPixelDifference> KindPixelDifferences);
}
