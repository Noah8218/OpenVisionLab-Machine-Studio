using System.Threading.Channels;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using OpenVisionLab;
using OpenVisionLab.Machine.Core.History;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Persistence.History;
using OpenVisionLab.MachineStudio.View.Diagnostics;
using OpenVisionLab.MachineStudio.View.Project;
using OpenVisionLab.MachineStudio.ViewModel;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

[Collection(StudioUiTestCollection.Name)]
public sealed class RunHistoryViewTests(StudioUiTestHost ui)
{
    [Theory]
    [InlineData(1054, 722, OpenVisionLanguage.Korean)]
    [InlineData(1054, 722, OpenVisionLanguage.English)]
    [InlineData(1688, 854, OpenVisionLanguage.Korean)]
    [InlineData(1688, 854, OpenVisionLanguage.English)]
    public Task RecordedEvidenceSelectionAndConfirmationRoundTrip(double width, double height, OpenVisionLanguage language) => ui.InvokeTaskAsync(async () =>
    {
        var previous = OpenVisionLanguageService.CurrentLanguage;
        OpenVisionLanguageService.SetLanguage(language, save: false);
        try
        {
            var root = Root();
            var store = new MachineRunRecordFileStore(root);
            var project = new MachineProjectDocument { Id = "recipe-a", Name = "Current recipe" };
            var record = Record(project) with { Captures = [PngCapture(), new() { AcquisitionId = "missing", CameraId = "cam" }, new() { AcquisitionId = "unreadable", CameraId = "cam", Pixels = [1, 2, 3] }], Products = [new("product", "Product", null, "Exited", "OK", null, 0, 1)], Inspections = [new("request", null, null, "{\"verdict\":\"OK\"}"), new("result", null, "{\"productId\":\"product\"}", null)] };
            var second = record with { Id = Guid.NewGuid().ToString("N"), StartedAtUtc = record.StartedAtUtc.AddMinutes(-1) };
            var other = Record(new MachineProjectDocument { Id = "recipe-b" });
            await store.SaveAsync(record); await store.SaveAsync(second); await store.SaveAsync(other);
            int applyCount = 0;
            using var vm = new RunHistoryViewModel(store, () => project, () => true, _ => { applyCount++; return Task.FromResult(false); }, action => { action(); return Task.CompletedTask; });
            await vm.RefreshAsync();
            using var view = View(vm, width, height);
            var tabs = (TabControl)view.FindName("RunEvidenceTabs");
            Assert.Equal(new[] { "RunHistory.Product", "RunHistory.Images", "RunHistory.Inspection", "RunHistory.Compare", "RunHistory.Settings" }.Select(OpenVisionLanguageService.T), tabs.Items.Cast<TabItem>().Select(tab => tab.Header));
            var list = (ListBox)view.FindName("SavedRunList");
            list.SetCurrentValue(System.Windows.Controls.Primitives.Selector.SelectedIndexProperty, 1); Layout(view);
            Assert.Same(vm.Runs[1], vm.Selected);
            vm.Selected = vm.Runs[0]; Layout(view); Assert.Same(vm.Selected, list.SelectedItem);
            tabs.SelectedIndex = 1; Layout(view);
            var captureGrid = (ListBox)view.FindName("RunCaptureThumbnails");
            var image = (Image)view.FindName("RunCaptureImage");
            var status = (TextBlock)view.FindName("RunCaptureStatus");
            Assert.NotNull(image.Source); Assert.False(status.IsVisible);
            captureGrid.SetCurrentValue(System.Windows.Controls.Primitives.Selector.SelectedIndexProperty, 1); Layout(view);
            Assert.Equal(record.Captures[1].AcquisitionId, vm.SelectedCapture!.AcquisitionId);
            Assert.Null(image.Source); Assert.Equal(OpenVisionLanguageService.T("RunHistory.ImageMissing"), status.Text); Assert.Equal(Visibility.Visible, status.Visibility);
            captureGrid.SetCurrentValue(System.Windows.Controls.Primitives.Selector.SelectedIndexProperty, 2); Layout(view);
            Assert.Null(image.Source); Assert.Equal(OpenVisionLanguageService.T("RunHistory.ImageUnreadable"), status.Text);
            vm.SelectedCapture = vm.Captures[0]; Layout(view); Assert.Same(vm.SelectedCapture, ((RunCaptureDisplay)captureGrid.SelectedItem).Record); Assert.NotNull(image.Source); Assert.Equal(Visibility.Collapsed, status.Visibility);
            await Click(view, "RunHistoryDraftButton"); Assert.True(vm.HasDraft); Assert.Equal(0, applyCount);
            Assert.False(vm.IsDeletePending); Assert.Equal("Current recipe", project.Name);
            await Click(view, "RunHistoryDeleteButton"); Assert.False(vm.HasDraft); Assert.True(vm.IsDeletePending);
            Assert.Equal(record.Id, ((TextBlock)view.FindName("RunDeleteId")).Text);
            await Click(view, "RunHistoryCancelDeleteButton"); Assert.False(vm.IsDeletePending); Assert.Equal(2, (await store.ReadAsync(project.Id)).Records.Count);
            await Click(view, "RunHistoryDraftButton"); await Click(view, "RunHistoryCancelDraftButton"); Assert.False(vm.HasDraft);
            await Click(view, "RunHistoryDraftButton"); list.SetCurrentValue(System.Windows.Controls.Primitives.Selector.SelectedIndexProperty, 1); Layout(view); Assert.False(vm.HasDraft || vm.IsDeletePending);
            list.SetCurrentValue(System.Windows.Controls.Primitives.Selector.SelectedIndexProperty, 0); Layout(view);
            await Click(view, "RunHistoryDeleteButton"); await Click(view, "RunHistoryConfirmDeleteButton"); await Until(() => !vm.IsBusy);
            await Click(view, "RunHistoryRefreshButton"); await Until(() => !vm.IsBusy);
            Assert.Equal(second.Id, Assert.Single((await new MachineRunRecordFileStore(root).ReadAsync(project.Id)).Records).Id);
            Assert.Single((await store.ReadAsync("recipe-b")).Records); Assert.Equal("Current recipe", project.Name); Assert.Equal(0, applyCount);
            tabs.SelectedIndex = 2; Layout(view);
            var raw = Descendants((ScrollViewer)view.FindName("RunInspectionScroll")).OfType<TextBox>().ToArray();
            Assert.False(((ComboBox)view.FindName("RunHistoryPicker")).IsEditable);
            Assert.All(raw, box => Assert.True(box.IsReadOnly));
            Assert.Contains(raw, box => box.Text == OpenVisionLanguageService.T("RunHistory.Missing"));
            Assert.Null(vm.Inspections[0].RequestJson); Assert.Null(vm.Inspections[1].ResultJson);
            view.DataContext = null;
        }
        finally { OpenVisionLanguageService.SetLanguage(previous, save: false); }
    });

    [Fact]
    public Task BusyApplyRejectsThenAcceptsAndPendingArchiveRetriesThroughButtons() => ui.InvokeTaskAsync(async () =>
    {
        string root = Root(), blocked = Path.Combine(root, "history"); Directory.CreateDirectory(root); await File.WriteAllTextAsync(blocked, "owned archive failure");
        var project = new MachineProjectDocument { Id = "recipe", Name = "Current" };
        var historical = new MachineProjectDocument { Id = project.Id, Name = "Historical" };
        bool accept = false; int applied = 0;
        TaskCompletionSource<bool>? waiting = null;
        using var vm = new RunHistoryViewModel(new MachineRunRecordFileStore(blocked), () => project, () => true,
            async draft => { applied++; bool result = waiting is null ? accept : await waiting.Task; if (result) project = draft; return result; }, action => ui.InvokeAsync(() => { action(); return true; }));
        var channel = Channel.CreateUnbounded<MachineRunRecord>(); vm.StartRecording(channel.Reader); await channel.Writer.WriteAsync(Record(historical)); channel.Writer.Complete(); await vm.RecordingCompletion;
        using var view = View(vm, 1054, 722);
        Assert.True(vm.HasPendingSaves); Assert.False(((Button)view.FindName("RunHistoryDeleteButton")).IsEnabled);
        File.Delete(blocked); await Click(view, "RunHistoryRetryButton"); await Until(() => !vm.IsBusy && !vm.HasPendingSaves); Layout(view);
        await Click(view, "RunHistoryRefreshButton"); await Until(() => !vm.IsBusy); Layout(view);
        Assert.Single((await new MachineRunRecordFileStore(blocked).ReadAsync(project.Id)).Records);
        AssertListSurface(view);
        await Click(view, "RunHistoryDraftButton"); await Click(view, "RunHistoryApplyButton"); await Until(() => !vm.IsBusy); Assert.True(vm.HasDraft); Assert.Equal("Current", project.Name);
        waiting = new(TaskCreationOptions.RunContinuationsAsynchronously); accept = true;
        await Click(view, "RunHistoryApplyButton"); Assert.True(vm.IsBusy); Layout(view);
        foreach (string name in new[] { "RunHistoryRefreshButton", "RunHistoryRetryButton", "RunHistoryDraftButton", "RunHistoryDeleteButton", "RunHistoryApplyButton", "RunHistoryCancelDraftButton" }) Assert.False(((Button)view.FindName(name)).IsEnabled);
        Assert.False(((ListBox)view.FindName("SavedRunList")).IsEnabled);
        Assert.False(((ComboBox)view.FindName("RunHistoryPicker")).IsEnabled);
        Assert.False(((ComboBox)view.FindName("RunComparisonPicker")).IsEnabled);
        Assert.False(((ListBox)view.FindName("RunComparisonSelectedCaptures")).IsEnabled);
        Assert.False(((ListBox)view.FindName("RunComparisonOtherCaptures")).IsEnabled);
        AssertListSurface(view);
        Capture(view, Path.Combine(root, "in-process-busy.png"));
        ((Button)view.FindName("RunHistoryApplyButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Assert.Equal(2, applied);
        waiting.SetResult(true); await Until(() => !vm.IsBusy); Layout(view);
        Assert.False(vm.HasDraft); Assert.Equal("Historical", project.Name); Assert.Equal("recipe", project.Id); Assert.Null(project.Simulation.AutomaticRun);
        AssertListSurface(view);
        Assert.Equal("Historical", new ProjectDocumentStore().Load(Assert.Single((await new MachineRunRecordFileStore(blocked).ReadAsync(project.Id)).Records).AppliedRecipeJson).Name);
        view.DataContext = null;
    });

    [Fact]
    public Task EmptyAndLongEvidenceRemainAccessibleWithoutPushingTheImageAway() => ui.InvokeTaskAsync(async () =>
    {
        var project = new MachineProjectDocument { Id = "long", Name = new string('N', 250) };
        string root = Root(); Directory.CreateDirectory(root);
        var store = new MachineRunRecordFileStore(root);
        using var vm = new RunHistoryViewModel(store, () => project, () => true, _ => Task.FromResult(false), action => { action(); return Task.CompletedTask; });
        await vm.RefreshAsync(); using var view = View(vm, 1054, 722); Assert.True(vm.IsEmpty);
        Assert.Contains(Descendants(view).OfType<TextBlock>(), text => text.Text == OpenVisionLanguageService.T("RunHistory.Empty") && text.Visibility == Visibility.Visible);
        Assert.False(((Button)view.FindName("RunHistoryDraftButton")).IsEnabled);
        Capture(view, Path.Combine(root, "in-process-empty.png"));
        var record = Record(project) with { Captures = [PngCapture() with { AcquisitionId = new string('A', 600) }], Inspections = [new("long-request", null, "{\"payload\":\"" + new string('R', 5000) + "\"}", null)] };
        await store.SaveAsync(record); await vm.RefreshAsync(); Layout(view);
        ((TabControl)view.FindName("RunEvidenceTabs")).SelectedIndex = 1; Layout(view);
        double imageHeight = ((Border)view.FindName("RunCaptureFrame")).ActualHeight;
        await Click(view, "RunHistoryDraftButton"); Layout(view);
        var frame = (Border)view.FindName("RunCaptureFrame"); Assert.True(frame.ActualHeight > 0); Assert.True(frame.TransformToAncestor(view).TransformBounds(new Rect(frame.RenderSize)).Bottom <= view.ActualHeight);
        Assert.Equal(imageHeight, frame.ActualHeight);
        Capture(view, Path.Combine(root, "in-process-long-draft.png"));
        Assert.NotNull(((Image)view.FindName("RunCaptureImage")).Source);
        await Click(view, "RunHistoryCancelDraftButton");
        var captureScroll = Descendants((ListBox)view.FindName("RunCaptureThumbnails")).OfType<ScrollViewer>().First(scroll => scroll.ScrollableHeight > 0);
        captureScroll.ScrollToBottom(); Layout(view); Assert.True(captureScroll.VerticalOffset > 0);
        var captureDetails = (ScrollViewer)view.FindName("RunCaptureDetailsScroll"); captureDetails.ScrollToBottom(); Layout(view); Assert.True(captureDetails.VerticalOffset > 0);
        ((TabControl)view.FindName("RunEvidenceTabs")).SelectedIndex = 2; Layout(view);
        var scroll = (ScrollViewer)view.FindName("RunInspectionScroll"); Assert.True(scroll.ScrollableHeight > 0); scroll.ScrollToBottom(); Layout(view); Assert.True(scroll.VerticalOffset > 0);
        Assert.Contains(Descendants(view).OfType<TextBox>(), box => box.Text.Contains(new string('R', 5000), StringComparison.Ordinal));
        ((TabControl)view.FindName("RunEvidenceTabs")).SelectedIndex = 4; Layout(view); var settings = (ScrollViewer)view.FindName("RunSettingsScroll"); settings.ScrollToBottom(); Layout(view); Assert.True(settings.VerticalOffset > 0);
        Assert.Equal(record.AppliedRecipeJson, ((TextBox)view.FindName("RunAppliedRecipe")).Text);
        view.DataContext = null;
    });

    [Fact]
    public Task SearchMatchMissAndClearPreserveSelectedEvidenceDraftAndStoredBytes() => ui.InvokeTaskAsync(async () =>
    {
        var project = new MachineProjectDocument { Id = "search-recipe", Name = "Current" };
        var record = Record(project) with { RecipeName = "Historical needle", Captures = [PngCapture()], Scope = "unit-A", Faults = ["ALARM-42"] };
        string root = Root(); var store = new MachineRunRecordFileStore(root);
        await store.SaveAsync(record); await store.SaveAsync(record with { Id = Guid.NewGuid().ToString("N"), RecipeName = "Other record", StartedAtUtc = record.StartedAtUtc.AddMinutes(-1) });
        var before = Directory.GetFiles(root, "*.json", SearchOption.AllDirectories).ToDictionary(path => path, File.ReadAllBytes);
        int applied = 0;
        using var vm = new RunHistoryViewModel(store, () => project, () => true, _ => { applied++; return Task.FromResult(false); }, action => { action(); return Task.CompletedTask; });
        await vm.RefreshAsync(); using var view = View(vm, 1054, 722);
        var list = (ListBox)view.FindName("SavedRunList"); var search = (TextBox)view.FindName("RunHistorySearch");
        var selected = vm.Selected; var capture = vm.SelectedCapture;
        await Click(view, "RunHistoryDraftButton");
        foreach (string query in new[] { " needle ", record.Id.ToUpperInvariant(), selected!.Outcome, "unit-A", "alarm-42", "no-such-record", "", "   " })
        {
            search.SetCurrentValue(TextBox.TextProperty, query); search.GetBindingExpression(TextBox.TextProperty)!.UpdateSource(); Layout(view);
            Assert.Equal(query, vm.SearchText); Assert.Same(selected, vm.Selected); Assert.Same(capture, vm.SelectedCapture); Assert.True(vm.HasDraft); Assert.Equal(0, applied);
            int expected = query.Trim() == "needle" || query == record.Id.ToUpperInvariant() ? 1 : query == "no-such-record" ? 0 : 2;
            Assert.Equal(expected, list.Items.Count); Assert.Equal(expected == 0, vm.HasNoSearchMatches);
        }
        vm.SearchText = "no-such-record"; Layout(view); await Click(view, "RunHistoryClearSearch");
        Assert.Equal("", search.Text); Assert.Same(selected, list.SelectedItem); Assert.True(vm.HasDraft);
        await vm.RefreshAsync(); Layout(view); Assert.Equal(record.Id, vm.Selected!.Record.Id); Assert.Equal(2, list.Items.Count);
        foreach (var saved in before) Assert.Equal(saved.Value, File.ReadAllBytes(saved.Key));
        Assert.Equal("Current", project.Name); Assert.Null(project.Simulation.AutomaticRun); Assert.Equal(0, applied);
    });

    private sealed class HistorySurface : Grid, IDisposable
    {
        private readonly ShellNavigationViewModel _navigation;
        private readonly LeftToolRegionView _left;
        public RunHistoryView Detail { get; }
        public HistorySurface(RunHistoryViewModel vm, double width, double height)
        {
            _navigation = new ShellNavigationViewModel(false, () => false, () => false, () => Task.CompletedTask, () => { }, _ => { });
            _navigation.IsResultsWorkspace = true;
            Width = width; Height = height;
            DataContext = new { RunHistory = vm, Navigation = _navigation };
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(240) });
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            _left = new LeftToolRegionView(); Children.Add(_left);
            Detail = new RunHistoryView { DataContext = vm }; SetColumn(Detail, 1); Children.Add(Detail);
        }
        public new object FindName(string name) => Detail.FindName(name) ?? _left.FindName(name);
        public void Dispose() { Detail.DataContext = null; DataContext = null; _navigation.Dispose(); }
    }

    private static HistorySurface View(RunHistoryViewModel vm, double width, double height) { var view = new HistorySurface(vm, width, height); Layout(view); return view; }
    private static void Layout(HistorySurface view) { view.Measure(new Size(view.Width, view.Height)); view.Arrange(new Rect(0, 0, view.Width, view.Height)); view.UpdateLayout(); view.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(view.UpdateLayout)); }
    private static async Task Click(HistorySurface view, string name) { var button = (Button)view.FindName(name); Assert.True(button.IsEnabled, name); button.BringIntoView(); Layout(view); ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke(); await Task.Delay(20); Layout(view); }
    private static async Task Until(Func<bool> predicate) { var limit = DateTime.UtcNow.AddSeconds(5); while (!predicate() && DateTime.UtcNow < limit) await Task.Delay(10); Assert.True(predicate()); }
    private static string Root() => Path.Combine(Path.GetTempPath(), "RunHistoryViewEvidence", Guid.NewGuid().ToString("N"));
    private static MachineRunRecord Record(MachineProjectDocument project) => new() { Id = Guid.NewGuid().ToString("N"), RecipeId = project.Id, RecipeName = project.Name, AppliedRecipeJson = new ProjectDocumentStore().Serialize(project), Outcome = "Completed", StartedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1), EndedAtUtc = DateTimeOffset.UtcNow };
    private static RunCaptureRecord PngCapture()
    {
        var source = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Gray8, null,
            new byte[] { 0, 100, 200, 255 }, 2);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return new()
        {
            CameraId = "cam",
            AcquisitionId = "valid",
            Pixels = stream.ToArray(),
            Width = 2,
            Height = 2,
            PixelFormat = "Gray8"
        };
    }
    private static void Capture(HistorySurface view, string path)
    {
        var bitmap = new RenderTargetBitmap((int)view.ActualWidth, (int)view.ActualHeight,
            96, 96, PixelFormats.Pbgra32);
        bitmap.Render(view);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
    private static void AssertListSurface(HistorySurface view)
    {
        var list = (ListBox)view.FindName("SavedRunList");
        var frame = Descendants(list).OfType<Border>().Single(border => border.Name == "HistoryListBorder");
        var expected = ((SolidColorBrush)view.FindResource("Surface.Panel")).Color;
        Assert.Equal(expected, ((SolidColorBrush)frame.Background).Color);
        var bitmap = new RenderTargetBitmap((int)view.ActualWidth, (int)view.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(view);
        var sample = list.TransformToAncestor(view).Transform(new Point(list.ActualWidth / 2, list.ActualHeight - 10));
        byte[] pixel = new byte[4]; bitmap.CopyPixels(new Int32Rect((int)sample.X, (int)sample.Y, 1, 1), pixel, 4, 0);
        Assert.Equal(new byte[] { expected.B, expected.G, expected.R, expected.A }, pixel);
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root) { for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++) { var child = VisualTreeHelper.GetChild(root, index); yield return child; foreach (var descendant in Descendants(child)) yield return descendant; } }
}
