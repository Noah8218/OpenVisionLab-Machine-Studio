using System.Windows;
using OpenVisionLab.MachineStudio.View.Scene;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

public sealed class SceneViewportProjectionTests
{
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
}
