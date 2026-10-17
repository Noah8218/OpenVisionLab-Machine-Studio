using System.Windows;
using OpenVisionLab.Machine.Core.Layouts;
using OpenVisionLab.MachineStudio.Model;
using OpenVisionLab.MachineStudio.View.Scene;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

public sealed class SceneViewportProjectionTests
{
    [Fact]
    public void NewUnitOriginsAlignHorizontallyInTheObliqueViewWithoutChangingLocalGeometry()
    {
        var projection = SceneViewportProjection.CreateEmpty(800, 600, isObliqueView: true);
        var origins = Enumerable.Range(0, 3).Select(LayoutComponentPlacementService.CreateUnitBaseOrigin).ToArray();
        var points = origins.Select(origin => projection.ToScreen(origin.X, origin.Y)).ToArray();
        Assert.All(points, point => Assert.Equal(points[0].Y, point.Y, precision: 8));
        Assert.True(points[1].X > points[0].X && points[2].X > points[1].X);
        var local = projection.ToScreen(45, -35, 70) - projection.ToScreen(0, 0);
        foreach (var origin in origins)
            Assert.InRange((projection.ToScreen(origin.X + 45, origin.Y - 35, 70) - projection.ToScreen(origin.X, origin.Y) - local).Length, 0, .000001);
    }

    [Fact]
    public void FitProjectionRoundTripsWorldAndScreenCoordinates()
    {
        var projection = SceneViewportProjection.Create(
            [
                new SceneViewportGeometry(40, 20, 80, 40),
                new SceneViewportGeometry(160, 90, 20, 30)
            ],
            viewportWidth: 640,
            viewportHeight: 420);

        var screenPoint = projection.ToScreen(100, 55);
        var worldPoint = projection.ToWorld(screenPoint);

        Assert.InRange(worldPoint.X, 99.999999, 100.000001);
        Assert.InRange(worldPoint.Y, 54.999999, 55.000001);
        Assert.True(projection.Scale > 0);
    }

    [Fact]
    public void ZoomAtKeepsTheAnchorOnTheSameScreenPoint()
    {
        var projection = SceneViewportProjection.Create(
            [new SceneViewportGeometry(0, 0, 100, 100)],
            viewportWidth: 400,
            viewportHeight: 300);
        var anchor = new Point(210, 145);

        var zoomed = projection.ZoomAt(anchor, 1.5);
        var worldAtAnchor = projection.ToWorld(anchor);
        var zoomedAnchor = zoomed.ToScreen(worldAtAnchor.X, worldAtAnchor.Y);

        Assert.InRange((zoomedAnchor - anchor).Length, 0, 0.000001);
        Assert.Equal(projection.Scale * 1.5, zoomed.Scale, precision: 12);
    }

    [Fact]
    public void TranslationMovesScreenCoordinatesWithoutChangingScale()
    {
        var projection = SceneViewportProjection.CreateEmpty(500, 300);
        var translated = projection.Translate(new Vector(24, -18));
        var original = projection.ToScreen(12, 8);
        var moved = translated.ToScreen(12, 8);

        Assert.Equal(projection.Scale, translated.Scale);
        Assert.Equal(24, moved.X - original.X, precision: 12);
        Assert.Equal(-18, moved.Y - original.Y, precision: 12);
    }

    [Fact]
    public void EmptyProjectionStartsAtTheViewportCenter()
    {
        var projection = SceneViewportProjection.CreateEmpty(800, 600);

        Assert.Equal(new Point(400, 300), projection.ToScreen(0, 0));
        Assert.Equal(1, projection.Scale);
    }

    [Fact]
    public void ObliqueProjectionRoundTripsPlanCoordinatesAndFitsVerticalEnvelope()
    {
        var projection = SceneViewportProjection.Create(
            [new SceneViewportGeometry(100, 50, 80, 40, 25, 10, 36)],
            viewportWidth: 640,
            viewportHeight: 420,
            isObliqueView: true);
        var screenPoint = projection.ToScreen(110, 62, 28);
        var worldPoint = projection.ToWorld(screenPoint, 28);

        Assert.InRange(worldPoint.X, 109.999999, 110.000001);
        Assert.InRange(worldPoint.Y, 61.999999, 62.000001);
        Assert.True(projection.Scale > 0);
        Assert.True(projection.IsObliqueView);
    }

    [Fact]
    public void ObliqueProjectionMatchesR19AxisAndElevationRatios()
    {
        var projection = SceneViewportProjection.CreateEmpty(600, 400, isObliqueView: true);
        var screenPoint = projection.ToScreen(10, 20, 30);
        var worldPoint = projection.ToWorld(screenPoint, 30);

        Assert.Equal(292.2, screenPoint.X, precision: 10);
        Assert.Equal(183.8, screenPoint.Y, precision: 10);
        Assert.InRange(worldPoint.X, 9.999999, 10.000001);
        Assert.InRange(worldPoint.Y, 19.999999, 20.000001);
    }

    [Fact]
    public void ObliqueWorldDeltaInvertsR19ScreenAxes()
    {
        var projection = SceneViewportProjection.CreateEmpty(600, 400, isObliqueView: true);
        var xAxisDelta = projection.ToWorldDelta(new Vector(78, 34));
        var zAxisDelta = projection.ToWorldDelta(new Vector(-78, 34));
        var combinedDelta = projection.ToWorldDelta(new Vector(46.8, 47.6));

        Assert.InRange(xAxisDelta.X, 99.999999, 100.000001);
        Assert.InRange(xAxisDelta.Y, -0.000001, 0.000001);
        Assert.InRange(zAxisDelta.X, -0.000001, 0.000001);
        Assert.InRange(zAxisDelta.Y, 99.999999, 100.000001);
        Assert.InRange(combinedDelta.X, 99.999999, 100.000001);
        Assert.InRange(combinedDelta.Y, 39.999999, 40.000001);
    }

    [Theory]
    [InlineData(0, 35)]
    [InlineData(90, 70)]
    [InlineData(180, 15)]
    [InlineData(270, -40)]
    [InlineData(359, -80)]
    public void OrbitProjectionRoundTripsEachSideAndUnderside(double yaw, double pitch)
    {
        var projection = SceneViewportProjection.Create([new(100, -25, 80, 40, 20, -10, 125)], 640, 420, true, yaw, pitch);
        var screen = projection.ToScreen(112, -18, 35);
        var restored = projection.ToWorld(screen, 35);
        Assert.InRange((restored - new Point(112, -18)).Length, 0, 0.000001);
        Assert.InRange(screen.X, 0, 640);
        Assert.InRange(screen.Y, 0, 420);
    }

    [Fact]
    public void OrbitKeepsItsPivotStableThroughZoomPanAndAFullTurn()
    {
        var projection = SceneViewportProjection.Create([new(10, 20, 100, 60, 0, -25, 90)], 640, 420, true);
        var pivot = new Point(10, 20);
        var screen = projection.ToScreen(10, 20, 20);
        var orbit = projection.OrbitAt(pivot, 20, new Vector(280, -75));
        Assert.InRange((orbit.ToScreen(10, 20, 20) - screen).Length, 0, .000001);
        Assert.NotEqual(projection.ToScreen(50, 20, 20), orbit.ToScreen(50, 20, 20));
        var turned = projection.OrbitAt(pivot, 20, new Vector(360 / .35, 0));
        Assert.InRange((turned.ToScreen(50, 20, 20) - projection.ToScreen(50, 20, 20)).Length, 0, .000001);
        var zoomed = orbit.ZoomAt(screen, 2).Translate(new Vector(15, -12));
        Assert.Equal(orbit.YawDegrees, zoomed.YawDegrees);
        Assert.Equal(orbit.PitchDegrees, zoomed.PitchDegrees);
        Assert.InRange((zoomed.ToScreen(10, 20, 20) - (screen + new Vector(15, -12))).Length, 0, .000001);
    }

    [Fact]
    public void DepthIncreasesTowardTheEyeAlongTheSameProjectedRay()
    {
        foreach (var (yawDegrees, pitchDegrees) in new[] { (45d, SceneViewportProjection.DefaultPitchDegrees), (0d, 65d), (180d, -40d), (275d, 15d) })
        {
            var projection = SceneViewportProjection.CreateEmpty(640, 420, true, yawDegrees, pitchDegrees);
            double yaw = yawDegrees * Math.PI / 180, pitch = pitchDegrees * Math.PI / 180;
            double x = 100 * Math.Sin(yaw) * Math.Cos(pitch), y = 100 * Math.Cos(yaw) * Math.Cos(pitch), elevation = 100 * Math.Sin(pitch);
            Assert.InRange((projection.ToScreen(x, y, elevation) - projection.ToScreen(0, 0, 0)).Length, 0, .000001);
            Assert.Equal(100, projection.Depth(x, y, elevation), precision: 8);
        }
    }

    [Fact]
    public void SideOnCameraRejectsAnAmbiguousEditingPlaneAndNonFiniteOrbitDoesNotChangeTheView()
    {
        var projection = SceneViewportProjection.Create([new(0, 0, 100, 50, 0, 0, 20)], 640, 420, true, 180, 0);
        Assert.False(projection.CanMapToWorld);
        Assert.Throws<InvalidOperationException>(() => projection.ToWorld(new Point(200, 100)));
        Assert.Equal(projection, projection.OrbitAt(default, 0, new Vector(double.NaN, 0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => SceneViewportProjection.Create([], 640, 420, true, 0, double.PositiveInfinity));
    }

    [Fact]
    public async Task PlanViewportHitTestSelectsRotatedComponentAndClearsOnEmptySpace()
    {
        var result = await RunOnStaAsync(() =>
        {
            var item = new LayoutItem(new LayoutComponentDefinition
            {
                Id = "rotated-machine-frame",
                Name = "Rotated machine frame",
                Kind = LayoutComponentKind.MachineFrame,
                Transform = new Transform2D { X = 80, Y = 40, RotationDegrees = 37 },
                Size = new Size2D { Width = 80, Height = 32 }
            }, gridSize: 10, snapToGrid: true);
            var viewport = new MachineSceneViewport { IsObliqueView = false, ItemsSource = [item] };
            viewport.Measure(new Size(640, 420));
            viewport.Arrange(new Rect(0, 0, 640, 420));

            var bounds = viewport.GetItemScreenBounds(item.Id);
            if (bounds is null)
            {
                throw new InvalidOperationException("The arranged planar viewport did not project its component.");
            }

            var center = new Point(bounds.Value.Left + (bounds.Value.Width / 2), bounds.Value.Top + (bounds.Value.Height / 2));
            var selected = viewport.SelectItemAt(center) && ReferenceEquals(viewport.SelectedItem, item);
            var emptySpaceClearedSelection = !viewport.SelectItemAt(new Point(0, 0)) && viewport.SelectedItem is null;
            return (selected, emptySpaceClearedSelection, viewport.ActualWidth, viewport.ActualHeight);
        });

        Assert.True(result.selected);
        Assert.True(result.emptySpaceClearedSelection);
        Assert.Equal(640, result.ActualWidth);
        Assert.Equal(420, result.ActualHeight);
    }

    private static Task<T> RunOnStaAsync<T>(Func<T> action)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                completion.SetResult(action());
            }
            catch (Exception exception)
            {
                completion.SetException(exception);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EquipmentProjectionUsesTheR24ViewBoxAndRetainsInverseAfterZoom(bool oblique)
    {
        var items = new[] { new SceneViewportGeometry(100, 20, 200, 80, BaseElevation: 0, VerticalHeight: 80) };
        var compact = SceneViewportProjection.CreateEquipment(items, 1000, 600, oblique);
        var wide = SceneViewportProjection.CreateEquipment(items, 1500, 900, oblique);
        Assert.True(compact.IsEquipmentPresentation);
        var point = compact.ToScreen(110, 35, 20);
        var scaled = wide.ToScreen(110, 35, 20);
        Assert.Equal(point.X * 1.5, scaled.X, 8);
        Assert.Equal(point.Y * 1.5, scaled.Y, 8);
        var restored = compact.ToWorld(point, 20);
        Assert.Equal(110, restored.X, 8);
        Assert.Equal(35, restored.Y, 8);
        var zoomed = compact.ZoomAt(point, 1.7).Translate(new Vector(5, 9));
        Assert.True(zoomed.IsEquipmentPresentation);
        var after = zoomed.ToWorld(zoomed.ToScreen(110, 35, 20), 20);
        Assert.Equal(110, after.X, 8);
        Assert.Equal(35, after.Y, 8);
    }

}
