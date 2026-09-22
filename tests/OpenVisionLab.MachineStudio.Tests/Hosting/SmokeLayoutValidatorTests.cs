using System.Globalization;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using OpenVisionLab.MachineStudio.View.Shell;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

public sealed class SmokeLayoutValidatorTests
{
    [Fact]
    public async Task ReportsVisibleWrappedTextThatExceedsItsArrangedHeight()
    {
        var issues = await RunOnStaAsync(() =>
        {
            var window = new Window
            {
                Width = 240,
                Height = 120,
                SizeToContent = SizeToContent.Manual,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.None,
                Content = new Grid
                {
                    Width = 240,
                    Height = 120,
                    ClipToBounds = true,
                    VerticalAlignment = VerticalAlignment.Top
                }
            };
            var root = (Grid)window.Content;
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(10) });
            var textBlock = new TextBlock
            {
                Text = "A long status message that must wrap into more than one line.",
                Width = 90,
                Height = 10,
                MaxHeight = 10,
                VerticalAlignment = VerticalAlignment.Top,
                FontSize = 18,
                Foreground = Brushes.Black,
                TextWrapping = TextWrapping.Wrap
            };
            root.Children.Add(textBlock);
            Grid.SetRow(textBlock, 0);

            window.Show();
            window.UpdateLayout();
            window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(window.UpdateLayout));
            try
            {
                Assert.True(textBlock.IsVisible, $"TextBlock was not visible; visibility={textBlock.Visibility}.");
                Assert.True(textBlock.ActualWidth > 0, $"ActualWidth={textBlock.ActualWidth}.");
                Assert.True(textBlock.ActualHeight > 0, $"ActualHeight={textBlock.ActualHeight}.");
                var formatted = new FormattedText(
                    textBlock.Text,
                    CultureInfo.CurrentUICulture,
                    textBlock.FlowDirection,
                    new Typeface(
                        textBlock.FontFamily,
                        textBlock.FontStyle,
                        textBlock.FontWeight,
                        textBlock.FontStretch),
                    textBlock.FontSize,
                    textBlock.Foreground,
                    1.0)
                {
                    MaxTextWidth = textBlock.ActualWidth
                };
                Assert.True(
                    formatted.Height > textBlock.DesiredSize.Height + 2.0,
                    $"FormattedHeight={formatted.Height}, DesiredHeight={textBlock.DesiredSize.Height}.");
                var method = typeof(SmokeLayoutValidator).GetMethod(
                    "FindTextClipIssues",
                    BindingFlags.NonPublic | BindingFlags.Static);
                Assert.NotNull(method);

                // Keep the validator's private visual-tree helper private; this test
                // exercises the existing owner without adding a production test seam.
                var result = method!.Invoke(null, [window, 1.0]);
                return Assert.IsAssignableFrom<IReadOnlyList<SmokeTextClipIssue>>(result);
            }
            finally
            {
                window.Close();
            }
        });

        Assert.Single(issues);
        Assert.Equal("TextBlock", issues[0].Element);
        Assert.True(issues[0].RequiredHeight > issues[0].AvailableHeight);
        Assert.Equal(10, issues[0].AvailableHeight);
    }

    [Fact]
    public async Task IgnoresWrappedTextThatFitsItsMeasuredHeight()
    {
        var issues = await RunOnStaAsync(() =>
        {
            var window = new Window
            {
                Width = 240,
                Height = 120,
                SizeToContent = SizeToContent.Manual,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.None,
                Content = new TextBlock
                {
                    Text = "Ready",
                    Width = 90,
                    Height = 30,
                    FontSize = 18,
                    Foreground = Brushes.Black,
                    TextWrapping = TextWrapping.Wrap
                }
            };

            window.Show();
            window.UpdateLayout();
            try
            {
                var method = typeof(SmokeLayoutValidator).GetMethod(
                    "FindTextClipIssues",
                    BindingFlags.NonPublic | BindingFlags.Static);
                Assert.NotNull(method);

                // Use the same existing owner as the clipped-text regression above.
                var result = method!.Invoke(null, [window, 1.0]);
                return Assert.IsAssignableFrom<IReadOnlyList<SmokeTextClipIssue>>(result);
            }
            finally
            {
                window.Close();
            }
        });

        Assert.Empty(issues);
    }

    private static Task<T> RunOnStaAsync<T>(Func<T> action)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            try
            {
                completion.SetResult(action());
            }
            catch (Exception exception)
            {
                completion.SetException(exception);
            }
            finally
            {
                dispatcher.BeginInvokeShutdown(DispatcherPriority.Normal);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }
}
