using System.Globalization;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OpenVisionLab.Machine.Core.Devices;
using OpenVisionLab.Machine.Simulation.Camera;
using OpenVisionLab.MachineStudio.Converter;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

[Collection(StudioUiTestCollection.Name)]
public sealed class NumericalFramePreviewConverterTests(StudioUiTestHost ui)
{
    [Fact]
    public async Task CameraImageSourceConsumerRendersTheActualNonSquareFrame()
    {
        await ui.InvokeAsync(() =>
        {
            using var fixture = new CameraExecutionInputFixture();
            var original = fixture.Snapshot.Cameras.Single(camera => camera.Id == "camera-4");
            var input = NumericalCameraInput.Generate(new NumericalCameraSettings
            {
                InputKind = NumericalCameraInputKind.HeightMap3D,
                ResolutionWidth = 80,
                ResolutionHeight = 60
            }, original.Id, original.CurrentAcquisitionId!, 1);
            fixture.Snapshot = fixture.CreateSnapshot(fixture.Snapshot.Cameras.Select(camera => camera.Id != original.Id ? camera : camera with
            {
                FrameEvidence = input.Frame,
                Result = camera.Result! with { FrameEvidence = input.Frame, NumericalInput = input }
            }));
            fixture.Camera.SelectedCameraId = original.Id;
            var view = new OpenVisionLab.MachineStudio.View.Inspector.CameraImageSourceView { DataContext = fixture.Camera };
            try
            {
                view.Measure(new System.Windows.Size(400, 900));
                view.Arrange(new System.Windows.Rect(0, 0, 400, 900));
                view.UpdateLayout();
                view.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                var image = Descendants(view).OfType<System.Windows.Controls.Image>().Single();
                var bitmap = Assert.IsAssignableFrom<BitmapSource>(image.Source);
                Assert.Equal((80, 60), (bitmap.PixelWidth, bitmap.PixelHeight));
                Assert.Equal(Stretch.Uniform, image.Stretch);
                var pixels = new byte[80 * 60];
                bitmap.CopyPixels(pixels, 80, 0);
                Assert.Equal(input.CopyPreview(), pixels);
                Assert.Equal(0, fixture.SideEffects);
                return true;
            }
            finally { view.DataContext = null; }
        });
    }

    private static IEnumerable<System.Windows.DependencyObject> Descendants(System.Windows.DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    [Theory]
    [InlineData(128, 128, false)]
    [InlineData(160, 120, false)]
    [InlineData(3, 5, false)]
    [InlineData(128, 128, true)]
    [InlineData(80, 60, true)]
    [InlineData(3, 5, true)]
    public async Task PreservesFrameDimensionsAndSourceBytesForPreviewAndHeight(int width, int height, bool heightMap)
    {
        await ui.InvokeAsync(() =>
        {
            var input = NumericalCameraInput.Generate(new NumericalCameraSettings
            {
                InputKind = heightMap ? NumericalCameraInputKind.HeightMap3D : NumericalCameraInputKind.Image2D,
                ResolutionWidth = width,
                ResolutionHeight = height
            }, "camera", "owned-frame", 1);
            var original = input.CopyBytes();
            var converter = new NumericalFramePreviewConverter();
            var preview = Assert.IsAssignableFrom<BitmapSource>(converter.Convert(input, typeof(BitmapSource), null!, CultureInfo.InvariantCulture));
            Assert.Equal((width, height), (preview.PixelWidth, preview.PixelHeight));
            Assert.Equal(PixelFormats.Gray8, preview.Format);
            var pixels = new byte[width * height];
            preview.CopyPixels(pixels, width, 0);
            Assert.Equal(input.CopyPreview(), pixels);
            Assert.True(preview.IsFrozen);
            if (heightMap)
            {
                var colored = Assert.IsAssignableFrom<BitmapSource>(converter.Convert(input, typeof(BitmapSource), "height", CultureInfo.InvariantCulture));
                Assert.Equal((width, height), (colored.PixelWidth, colored.PixelHeight));
                Assert.Equal(PixelFormats.Bgra32, colored.Format);
                var colors = new byte[width * height * 4];
                colored.CopyPixels(colors, width * 4, 0);
                Assert.All(Enumerable.Range(0, width * height), index => Assert.Equal(255, colors[index * 4 + 3]));
                Assert.True(colored.IsFrozen);
            }
            Assert.Equal(original, input.CopyBytes());
            return true;
        });
    }
}
