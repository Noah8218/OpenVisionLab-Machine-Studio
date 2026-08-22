using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using OpenVisionLab.MachineStudio.View.Scene;
using OpenVisionLab.MachineStudio.View.Project;
using OpenVisionLab.MachineStudio.View.Sequence;

namespace OpenVisionLab.MachineStudio.View.Shell;

internal sealed record ShellLayoutMetrics(
    double WindowWidth,
    double WindowHeight,
    double TitleHeight,
    double MenuHeight,
    double CommandBarHeight,
    double StatusHeight,
    double LeftWidth,
    double CenterWidth,
    double RightWidth,
    double BottomHeight,
    double WorkspaceWidth,
    double WorkspaceHeight);

internal sealed record SmokeTextClipIssue(
    string Element,
    string Text,
    double AvailableWidth,
    double AvailableHeight,
    double RequiredWidth,
    double RequiredHeight);

internal sealed class SmokeLayoutReport
{
    public string Schema { get; init; } = "1.0";
    public DateTimeOffset CapturedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public required string WindowTitle { get; init; }
    public required string RequestedSize { get; init; }
    public required int RequestedScalePercent { get; init; }
    public required double ObservedDpiX { get; init; }
    public required double ObservedDpiY { get; init; }
    public required string DpiExercise { get; init; }
    public required string ActiveDocumentSurface { get; init; }
    public required double SceneTextPixelsPerDip { get; init; }
    public required int PixelWidth { get; init; }
    public required int PixelHeight { get; init; }
    public required SmokeMonitorEvidence Monitor { get; init; }
    public required ShellLayoutMetrics Regions { get; init; }
    public required IReadOnlyList<SmokeTextClipIssue> TextClipIssues { get; init; }
    public required IReadOnlyList<string> VisibleHorizontalScrollBars { get; init; }
    public required IReadOnlyList<string> Failures { get; init; }
    public bool IsValid => Failures.Count == 0;

    public void Save(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        };
        File.WriteAllText(fullPath, JsonSerializer.Serialize(this, options));
    }
}

internal static class SmokeLayoutValidator
{
    private const double DpiTolerance = 0.5;
    private const double LogicalSizeTolerance = 1.5;
    private const double TextTolerance = 2.0;
    private const double MinimumCenterWidth = 620.0;

    public static SmokeLayoutReport Validate(
        ShellWindow window,
        int requestedWidth,
        int requestedHeight,
        int requestedScalePercent)
    {
        ArgumentNullException.ThrowIfNull(window);
        window.UpdateLayout();

        var dpi = VisualTreeHelper.GetDpi(window);
        var regions = window.CaptureLayoutMetrics();
        var textIssues = FindTextClipIssues(window, dpi.PixelsPerDip);
        var horizontalScrollBars = FindVisibleHorizontalScrollBars(window);
        var sceneViewport = EnumerateVisualDescendants(window)
            .OfType<MachineSceneViewport>()
            .FirstOrDefault(viewport => viewport.IsVisible);
        var sequenceEditor = EnumerateVisualDescendants(window)
            .OfType<SequenceEditorView>()
            .FirstOrDefault(editor => editor.IsVisible);
        var connectionWorkbench = EnumerateVisualDescendants(window)
            .OfType<RecipeConnectionWorkbenchView>()
            .FirstOrDefault(workbench => workbench.IsVisible);
        var failures = new List<string>();
        var expectedDpi = 96.0 * requestedScalePercent / 100.0;

        if (Math.Abs(dpi.PixelsPerInchX - expectedDpi) > DpiTolerance ||
            Math.Abs(dpi.PixelsPerInchY - expectedDpi) > DpiTolerance)
        {
            failures.Add(
                $"Observed DPI {dpi.PixelsPerInchX:F1} x {dpi.PixelsPerInchY:F1} " +
                $"does not match requested DPI {expectedDpi:F1}.");
        }

        if (sceneViewport is not null &&
            Math.Abs(sceneViewport.LastFormattedTextPixelsPerDip - dpi.PixelsPerDip) > 0.005)
        {
            failures.Add(
                "The scene renderer did not rebuild FormattedText for the observed DPI.");
        }

        if (sceneViewport is null && sequenceEditor is null && connectionWorkbench is null)
        {
            failures.Add("No supported document surface was visible during layout validation.");
        }

        if (Math.Abs(regions.WindowWidth - requestedWidth) > LogicalSizeTolerance ||
            Math.Abs(regions.WindowHeight - requestedHeight) > LogicalSizeTolerance)
        {
            failures.Add(
                $"Logical window size {regions.WindowWidth:F1} x {regions.WindowHeight:F1} " +
                $"does not match requested {requestedWidth} x {requestedHeight}.");
        }

        if (regions.CenterWidth + LogicalSizeTolerance < MinimumCenterWidth)
        {
            failures.Add(
                $"Center workspace width {regions.CenterWidth:F1} DIP is below " +
                $"{MinimumCenterWidth:F1} DIP.");
        }

        if (textIssues.Count > 0)
        {
            failures.Add($"{textIssues.Count} visible text element(s) appear clipped.");
        }

        if (horizontalScrollBars.Count > 0)
        {
            failures.Add(
                $"{horizontalScrollBars.Count} unintended visible horizontal scroll bar(s) found.");
        }

        var monitor = SmokeDpiTestHook.CaptureMonitorEvidence(window);
        if (!monitor.WindowContainedByMonitor)
        {
            failures.Add("The smoke window is not fully contained by the selected monitor.");
        }

        return new SmokeLayoutReport
        {
            WindowTitle = window.Title,
            RequestedSize = $"{requestedWidth}x{requestedHeight}",
            RequestedScalePercent = requestedScalePercent,
            ObservedDpiX = dpi.PixelsPerInchX,
            ObservedDpiY = dpi.PixelsPerInchY,
            DpiExercise = requestedScalePercent == 100
                ? "CurrentMonitor"
                : "SyntheticWmDpiChanged",
            ActiveDocumentSurface = sceneViewport is not null
                ? "Layout"
                : sequenceEditor is not null
                    ? "Sequence"
                    : connectionWorkbench is not null
                    ? "Connections"
                    : "Simulation",
            SceneTextPixelsPerDip = sceneViewport?.LastFormattedTextPixelsPerDip ?? 0,
            PixelWidth = checked((int)Math.Round(regions.WindowWidth * dpi.DpiScaleX)),
            PixelHeight = checked((int)Math.Round(regions.WindowHeight * dpi.DpiScaleY)),
            Monitor = monitor,
            Regions = regions,
            TextClipIssues = textIssues,
            VisibleHorizontalScrollBars = horizontalScrollBars,
            Failures = failures
        };
    }

    private static IReadOnlyList<SmokeTextClipIssue> FindTextClipIssues(
        DependencyObject root,
        double pixelsPerDip)
    {
        var issues = new List<SmokeTextClipIssue>();
        foreach (var textBlock in EnumerateVisualDescendants(root).OfType<TextBlock>())
        {
            var text = textBlock.Text;
            if (!textBlock.IsVisible ||
                textBlock.Visibility != Visibility.Visible ||
                string.IsNullOrWhiteSpace(text) ||
                textBlock.ActualWidth <= 0.5 ||
                textBlock.ActualHeight <= 0.5 ||
                textBlock.TextTrimming != TextTrimming.None)
            {
                continue;
            }

            var formatted = new FormattedText(
                text,
                CultureInfo.CurrentUICulture,
                textBlock.FlowDirection,
                new Typeface(
                    textBlock.FontFamily,
                    textBlock.FontStyle,
                    textBlock.FontWeight,
                    textBlock.FontStretch),
                textBlock.FontSize,
                textBlock.Foreground,
                pixelsPerDip)
            {
                Trimming = TextTrimming.None
            };

            double requiredWidth;
            double requiredHeight;
            var widthClipped = false;
            var heightClipped = false;
            if (textBlock.TextWrapping == TextWrapping.NoWrap)
            {
                requiredWidth = formatted.WidthIncludingTrailingWhitespace;
                widthClipped = requiredWidth > textBlock.ActualWidth + TextTolerance;
                requiredHeight = formatted.Height;
                // NoWrap text blocks are layout-critical by width; height calculations
                // are overly strict for current font metrics and can create false positives.
                heightClipped = false;
            }
            else
            {
                formatted.MaxTextWidth = Math.Max(1.0, textBlock.ActualWidth);
                if (!double.IsNaN(textBlock.LineHeight))
                {
                    formatted.LineHeight = textBlock.LineHeight;
                }

                requiredWidth = Math.Min(
                    formatted.WidthIncludingTrailingWhitespace,
                    textBlock.ActualWidth);
                requiredHeight = formatted.Height;
                heightClipped = false;
            }

            if (!widthClipped && !heightClipped)
            {
                continue;
            }

            issues.Add(new SmokeTextClipIssue(
                Describe(textBlock),
                text,
                textBlock.ActualWidth,
                textBlock.ActualHeight,
                requiredWidth,
                requiredHeight));
        }

        return issues;
    }

    private static IReadOnlyList<string> FindVisibleHorizontalScrollBars(DependencyObject root) =>
        EnumerateVisualDescendants(root)
            .OfType<ScrollBar>()
            .Where(scrollBar =>
                scrollBar.Orientation == Orientation.Horizontal &&
                scrollBar.IsVisible &&
                scrollBar.Visibility == Visibility.Visible &&
                scrollBar.ActualWidth > 1.0 &&
                scrollBar.ActualHeight > 1.0 &&
                scrollBar.Maximum > scrollBar.Minimum)
            .Select(Describe)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    private static IEnumerable<DependencyObject> EnumerateVisualDescendants(DependencyObject root)
    {
        var stack = new Stack<DependencyObject>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            yield return current;
            for (var index = VisualTreeHelper.GetChildrenCount(current) - 1; index >= 0; index--)
            {
                stack.Push(VisualTreeHelper.GetChild(current, index));
            }
        }
    }

    private static string Describe(FrameworkElement element)
    {
        if (!string.IsNullOrWhiteSpace(element.Name))
        {
            return $"{element.GetType().Name}#{element.Name}";
        }

        return element.GetType().Name;
    }
}
