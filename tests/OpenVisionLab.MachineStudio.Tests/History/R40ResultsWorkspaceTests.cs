using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using OpenVisionLab;
using OpenVisionLab.Machine.Core.History;
using OpenVisionLab.Machine.Core.Devices;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.MachineStudio.Model;
using OpenVisionLab.Machine.Persistence.History;
using OpenVisionLab.Machine.Persistence.Projects;
using OpenVisionLab.MachineStudio.View.Diagnostics;
using OpenVisionLab.MachineStudio.View.Shell;
using OpenVisionLab.MachineStudio.ViewModel;
using OpenVisionLab.TestSupport;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

[Collection(StudioUiTestCollection.Name)]
public sealed class R40ResultsWorkspaceTests(StudioUiTestHost ui)
{
    [Theory]
    [InlineData(true, OpenVisionLanguage.Korean)]
    [InlineData(false, OpenVisionLanguage.Korean)]
    [InlineData(true, OpenVisionLanguage.English)]
    [InlineData(false, OpenVisionLanguage.English)]
    public Task RenderStoredRunBaseline(bool fullArea, OpenVisionLanguage language) => ui.InvokeTaskAsync(() => WithHistoryAsync(async (main, window, root, project) =>
    {
        var view = Assert.Single(Descendants(window).OfType<RunHistoryView>());
        var tabs = Assert.IsType<TabControl>(view.FindName("RunEvidenceTabs"));
        Assert.Equal(language == OpenVisionLanguage.Korean ? "결과 분석" : "Result analysis", Assert.IsType<TextBlock>(view.FindName("RunAnalysisTitle")).Text);
        Assert.Equal(language == OpenVisionLanguage.Korean ? "결과 분석" : "Result analysis", window.ResultsWorkspaceButton.Content);
        tabs.SelectedIndex = 1;
        Pump(window);
        Assert.NotNull(Assert.IsType<Image>(view.FindName("RunCaptureImage")).Source);
        Capture(window, $"results-images-{language}-{(fullArea ? "work-area" : "compact")}");
        Assert.False(main.HasUnsavedChanges || main.IsRunning);
        await Task.CompletedTask;
    }, fullArea, language));

    [Theory]
    [InlineData(true, OpenVisionLanguage.Korean)]
    [InlineData(false, OpenVisionLanguage.Korean)]
    [InlineData(true, OpenVisionLanguage.English)]
    [InlineData(false, OpenVisionLanguage.English)]
    public Task ComparisonPickerShowsRecordedDifferencesAndRefreshPreservesBothIdentities(bool fullArea, OpenVisionLanguage language) => ui.InvokeTaskAsync(() => WithHistoryAsync(async (main, window, root, project) =>
    {
        var history = main.RunHistory;
        var view = Assert.Single(Descendants(window).OfType<RunHistoryView>());
        var tabs = Assert.IsType<TabControl>(view.FindName("RunEvidenceTabs"));
        var selected = history.Selected!;
        var other = Assert.Single(history.ComparisonRuns);
        var stored = Directory.GetFiles(root, "*.json", SearchOption.AllDirectories).ToDictionary(path => path, File.ReadAllBytes);
        string projectBefore = new ProjectDocumentStore().Serialize(project);
        tabs.SelectedIndex = 3;
        Pump(window);
        var picker = Assert.IsType<ComboBox>(view.FindName("RunComparisonPicker"));
        picker.SetCurrentValue(System.Windows.Controls.Primitives.Selector.SelectedItemProperty, other);
        Pump(window);
        Assert.Same(other, history.ComparedRun);
        Assert.True(history.HasComparison);
        var rows = Assert.IsType<DataGrid>(view.FindName("RunComparisonGrid"));
        Assert.Equal(history.ComparisonRows, rows.Items.Cast<RunComparisonRow>());
        var duration = Assert.Single(history.ComparisonRows, row => row.Label == OpenVisionLanguageService.T("RunHistory.Duration"));
        Assert.Equal("12", duration.SelectedValue);
        Assert.Equal("16.5", duration.ComparedValue);
        Assert.Equal("+4.5", duration.Difference);
        var feeds = Assert.Single(history.ComparisonRows, row => row.Label == OpenVisionLanguageService.T("RunHistory.ActualFeed"));
        Assert.Equal("2", feeds.SelectedValue);
        Assert.Equal(OpenVisionLanguageService.T("RunHistory.Missing"), feeds.ComparedValue);
        Assert.Equal(OpenVisionLanguageService.T("RunHistory.Missing"), feeds.Difference);
        var alarms = Assert.Single(history.ComparisonRows, row => row.Label == OpenVisionLanguageService.T("RunHistory.RecordedAlarms"));
        Assert.Equal("0", alarms.SelectedValue);
        Assert.Equal("1", alarms.ComparedValue);
        Assert.Equal("+1", alarms.Difference);
        Assert.Contains(history.ComparisonRows, row => row.ComparedValue.Contains("CAMERA_TIMEOUT", StringComparison.Ordinal));
        Assert.NotNull(Assert.IsType<Image>(view.FindName("RunComparisonSelectedImage")).Source);
        Assert.NotNull(Assert.IsType<Image>(view.FindName("RunComparisonOtherImage")).Source);
        var selectedImage = Assert.IsAssignableFrom<BitmapSource>(Assert.IsType<Image>(view.FindName("RunComparisonSelectedImage")).Source);
        var comparedImage = Assert.IsAssignableFrom<BitmapSource>(Assert.IsType<Image>(view.FindName("RunComparisonOtherImage")).Source);
        var selectedPixel = new byte[1];
        var comparedPixel = new byte[1];
        selectedImage.CopyPixels(new Int32Rect(0, 0, 1, 1), selectedPixel, 1, 0);
        comparedImage.CopyPixels(new Int32Rect(0, 0, 1, 1), comparedPixel, 1, 0);
        Assert.Equal(30, selectedPixel[0]);
        Assert.Equal(225, comparedPixel[0]);
        Capture(window, $"results-comparison-{language}-{(fullArea ? "work-area" : "compact")}");
        var selectedFrame = Assert.IsType<Border>(view.FindName("RunComparisonSelectedFrame"));
        double imageHeight = selectedFrame.ActualHeight;
        var selection = Assert.IsType<ListBox>(view.FindName("RunComparisonSelectedCaptures"));
        selection.SetCurrentValue(System.Windows.Controls.Primitives.Selector.SelectedIndexProperty, 1);
        Pump(window);
        Assert.Null(Assert.IsType<Image>(view.FindName("RunComparisonSelectedImage")).Source);
        Assert.Equal(OpenVisionLanguageService.T("RunHistory.ImageMissing"), history.CaptureImageStatusText);
        selection.SetCurrentValue(System.Windows.Controls.Primitives.Selector.SelectedIndexProperty, 0);
        Pump(window);
        Assert.NotNull(Assert.IsType<Image>(view.FindName("RunComparisonSelectedImage")).Source);
        var otherSelection = Assert.IsType<ListBox>(view.FindName("RunComparisonOtherCaptures"));
        Assert.Same(history.ComparedCaptureThumbnail, otherSelection.SelectedItem);
        var summary = Assert.IsType<Expander>(view.FindName("RunComparisonSummary"));
        summary.IsExpanded = true;
        Pump(window);
        Assert.Equal(imageHeight, selectedFrame.ActualHeight);
        if (!fullArea && language == OpenVisionLanguage.Korean)
        {
            summary.BringIntoView();
            Pump(window);
            Capture(window, "results-comparison-summary-compact");
        }
        var settings = Assert.IsType<Expander>(view.FindName("RunComparisonSettings"));
        settings.IsExpanded = true;
        Pump(window);
        Assert.Equal(selected.Record.AppliedRecipeJson, Assert.IsType<TextBox>(view.FindName("RunComparisonSelectedSettings")).Text);
        Assert.Equal(other.Record.AppliedRecipeJson, Assert.IsType<TextBox>(view.FindName("RunComparisonOtherSettings")).Text);
        Assert.All(Descendants(settings).OfType<TextBox>(), box => Assert.True(box.IsReadOnly));
        var scroll = Assert.IsType<ScrollViewer>(view.FindName("RunComparisonSettingsScroll"));
        scroll.ScrollToBottom();
        Pump(window);
        Assert.True(scroll.VerticalOffset > 0);
        Assert.Equal(imageHeight, selectedFrame.ActualHeight);
        if (!fullArea && language == OpenVisionLanguage.Korean)
        {
            Assert.IsType<ScrollViewer>(view.FindName("RunComparisonSidebar")).ScrollToBottom();
            Pump(window);
            Capture(window, "results-comparison-details-compact");
        }
        settings.IsExpanded = false;
        summary.IsExpanded = false;
        history.SearchText = "no matching run";
        Pump(window);
        Assert.True(history.HasNoSearchMatches && history.HasComparison);
        Assert.Same(selected, history.Selected);
        history.ClearSearchCommand.Execute(null);
        await history.RefreshAsync();
        Pump(window);
        Assert.Equal(selected.RunId, history.Selected!.RunId);
        Assert.Equal(other.RunId, history.ComparedRun!.RunId);
        Assert.Same(history.ComparedRun, picker.SelectedItem);
        Assert.Equal(projectBefore, new ProjectDocumentStore().Serialize(project));
        foreach (var saved in stored) Assert.Equal(saved.Value, File.ReadAllBytes(saved.Key));
        Assert.False(main.HasUnsavedChanges || main.IsRunning);
    }, fullArea, language));

    [Fact]
    public Task RunPickerBindingsRejectForeignComparisonAndClearDeletedOrChangedTargets() => ui.InvokeTaskAsync(() => WithHistoryAsync(async (main, window, root, project) =>
    {
        var history = main.RunHistory;
        var view = Assert.Single(Descendants(window).OfType<RunHistoryView>());
        var selected = history.Selected!;
        var other = Assert.Single(history.ComparisonRuns);
        history.ComparedRun = new RunHistoryEntry(other.Record with { RecipeId = "foreign" });
        Assert.Null(history.ComparedRun);
        history.ComparedRun = selected;
        Assert.Null(history.ComparedRun);
        history.ComparedRun = other;
        history.ComparedRun = new RunHistoryEntry(other.Record);
        Assert.Same(other, history.ComparedRun);
        var picker = Assert.IsType<ComboBox>(view.FindName("RunHistoryPicker"));
        picker.SetCurrentValue(System.Windows.Controls.Primitives.Selector.SelectedItemProperty, other);
        Pump(window);
        Assert.Same(other, history.Selected);
        Assert.Equal(OpenVisionLanguageService.T("RunHistory.Faulted"), Assert.IsType<TextBlock>(view.FindName("RunOutcomeText")).Text);
        Assert.Equal(((SolidColorBrush)view.FindResource("State.FaultSurface")).Color, ((SolidColorBrush)Assert.IsType<Border>(view.FindName("RunOutcomeBadge")).Background).Color);
        Capture(window, "results-alarm-selected-compact");
        Assert.Null(history.ComparedRun);
        Assert.Empty(history.ComparisonRows);
        history.ComparedRun = selected;
        history.PrepareRerunCommand.Execute(null);
        Assert.True(history.HasDraft);
        history.Selected = selected;
        Pump(window);
        Assert.Same(selected, picker.SelectedItem);
        Assert.False(history.HasDraft || history.HasComparison);
        history.ComparedRun = other;
        var selectedId = selected.RunId;
        new MachineRunRecordFileStore(root).Delete(project.Id, other.RunId);
        await history.RefreshAsync();
        Pump(window);
        Assert.Equal(selectedId, history.Selected!.RunId);
        Assert.False(history.HasComparison);
        Assert.Null(history.ComparedRun);
        Assert.Equal(OpenVisionLanguageService.T("RunHistory.ComparisonNeedsTwo"), history.ComparisonHint);
        Assert.False(main.IsRunning || main.HasUnsavedChanges);
    }));

    [Fact]
    public Task LongEvidenceCorruptArchiveAndEmptyListRecoverWithoutShrinkingTheImage() => ui.InvokeTaskAsync(() => WithHistoryAsync(async (main, window, root, project) =>
    {
        var history = main.RunHistory;
        var view = Assert.Single(Descendants(window).OfType<RunHistoryView>());
        var tabs = Assert.IsType<TabControl>(view.FindName("RunEvidenceTabs"));
        var store = new MachineRunRecordFileStore(root);
        var selected = history.Selected!.Record;
        var longRecord = selected with
        {
            Id = "33333333333333333333333333333333", RecipeName = string.Join(" ", Enumerable.Repeat("촬영 결과 확인용 레시피", 20)),
            Captures = [selected.Captures[0] with { AcquisitionId = new string('A', 600) }], StartedAtUtc = selected.StartedAtUtc.AddMinutes(1), EndedAtUtc = selected.EndedAtUtc.AddMinutes(1)
        };
        await store.SaveAsync(longRecord);
        await history.RefreshAsync();
        history.Selected = history.Runs.Single(run => run.RunId == longRecord.Id);
        tabs.SelectedIndex = 1;
        Pump(window);
        var frame = Assert.IsType<Border>(view.FindName("RunCaptureFrame"));
        double height = frame.ActualHeight;
        Assert.True(height > 180);
        Invoke(Assert.IsType<Button>(view.FindName("RunHistoryDraftButton")));
        Pump(window);
        Assert.True(history.HasDraft);
        var details = Assert.IsType<ScrollViewer>(view.FindName("RunDetailsScroll"));
        foreach (string name in new[] { "RunHistoryApplyButton", "RunHistoryCancelDraftButton" })
        {
            var button = Assert.IsType<Button>(view.FindName(name));
            Assert.True(button.IsVisible && button.IsEnabled);
            Assert.True(new Rect(details.RenderSize).Contains(button.TransformToAncestor(details).TransformBounds(new Rect(button.RenderSize))), name);
        }
        Assert.Equal(height, frame.ActualHeight);
        Assert.True(new Rect(window.RenderSize).Contains(frame.TransformToAncestor(window).TransformBounds(new Rect(frame.RenderSize))));
        Capture(window, "results-long-draft-compact");
        history.CancelDraftCommand.Execute(null);
        string[] files = Directory.GetFiles(root, "*.json", SearchOption.AllDirectories);
        string corrupt = Path.Combine(Path.GetDirectoryName(files[0])!, "44444444444444444444444444444444.json");
        await File.WriteAllTextAsync(corrupt, "{\"schemaVersion\":");
        await history.RefreshAsync();
        Assert.Equal(3, history.Runs.Count);
        Assert.Contains(OpenVisionLanguageService.T("RunHistory.ReadFailed"), history.StatusText);
        Pump(window);
        Assert.Equal(height, frame.ActualHeight);
        Assert.Contains(OpenVisionLanguageService.T("RunHistory.ReadFailed"), Assert.IsType<TextBlock>(view.FindName("RunHistoryStatus")).Text);
        Capture(window, "results-read-error-compact");
        File.Delete(corrupt);
        await history.RefreshAsync();
        Assert.Equal(string.Empty, history.StatusText);
        foreach (var run in history.Runs.ToArray()) store.Delete(project.Id, run.RunId);
        await history.RefreshAsync();
        Pump(window);
        Assert.True(history.IsEmpty);
        Assert.Null(history.Selected);
        Assert.Empty(history.ComparisonRows);
        Assert.Null(Assert.IsType<Image>(view.FindName("RunCaptureImage")).Source);
        Assert.False(Assert.IsType<Button>(view.FindName("RunHistoryDraftButton")).IsEnabled);
        Capture(window, "results-empty-compact");
        await store.SaveAsync(selected);
        await history.RefreshAsync();
        Pump(window);
        Assert.Equal(selected.Id, history.Selected!.RunId);
        Assert.NotNull(Assert.IsType<Image>(view.FindName("RunCaptureImage")).Source);
        Assert.False(main.HasUnsavedChanges || main.IsRunning);
    }));

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(4, false)]
    public void ReturnNavigationRestoresThePriorWorkspaceAndHonorsDraftModalAndDisposeGuards(int workspace, bool process)
    {
        bool allow = true;
        using var navigation = new ShellNavigationViewModel(false, () => false, () => false, () => Task.CompletedTask, () => { }, _ => { }, resolvePendingPlacementDraft: () => allow);
        navigation.SelectedWorkspaceIndex = workspace;
        if (process) navigation.IsProcessWorkspace = true;
        navigation.IsInspectorOpen = true;
        var before = (navigation.SelectedWorkspaceIndex, navigation.SelectedDocumentTabIndex, navigation.SelectedLeftToolTabIndex, navigation.SelectedExecutionTabIndex);
        navigation.IsResultsWorkspace = true;
        navigation.OpenResultsWorkspaceCommand.Execute(null);
        allow = false;
        navigation.ReturnFromResultsCommand.Execute(null);
        Assert.True(navigation.IsResultsWorkspace);
        allow = true;
        navigation.OpenTestScenarioCommand.Execute(null);
        Assert.False(navigation.ReturnFromResultsCommand.CanExecute(null));
        navigation.ReturnFromResultsCommand.Execute(null);
        Assert.True(navigation.IsResultsWorkspace);
        navigation.CloseTestScenarioCommand.Execute(null);
        navigation.ReturnFromResultsCommand.Execute(null);
        Assert.Equal(before, (navigation.SelectedWorkspaceIndex, navigation.SelectedDocumentTabIndex, navigation.SelectedLeftToolTabIndex, navigation.SelectedExecutionTabIndex));
        Assert.True(navigation.IsInspectorOpen);
        Assert.False(navigation.ReturnFromResultsCommand.CanExecute(null));
        navigation.IsResultsWorkspace = true;
        navigation.Dispose();
        Assert.False(navigation.ReturnFromResultsCommand.CanExecute(null));
        navigation.ReturnFromResultsCommand.Execute(null);
        Assert.True(navigation.IsResultsWorkspace);
    }

    [Fact]
    public Task RenderedReturnButtonPreservesTheSelectedStepAndResolvingDraftsNeverStartsTheMachine() => ui.InvokeTaskAsync(() => WithHistoryAsync(async (main, window, root, project) =>
    {
        main.Navigation.IsProcessWorkspace = true;
        main.SequenceEditor.SelectedStep = main.SequenceEditor.Steps[3];
        main.Navigation.IsResultsWorkspace = true;
        Pump(window);
        var view = Assert.Single(Descendants(window).OfType<RunHistoryView>());
        var button = Assert.IsType<Button>(view.FindName("RunHistoryReturnButton"));
        Assert.True(button.IsVisible && button.IsEnabled);
        Assert.Same(main.Navigation.ReturnFromResultsCommand, button.Command);
        main.SequenceEditor.SelectedStepDraft!.Parameter = "unsaved-recipe";
        main.SequenceEditor.PendingStepDraftPrompt = () => PlacementDraftDecision.Cancel;
        Invoke(button);
        Pump(window);
        Assert.True(main.Navigation.IsResultsWorkspace);
        Assert.Equal("unsaved-recipe", main.SequenceEditor.SelectedStepDraft.Parameter);
        main.SequenceEditor.PendingStepDraftPrompt = () => PlacementDraftDecision.Discard;
        Invoke(button);
        Pump(window);
        Assert.True(main.Navigation.IsProcessWorkspace);
        Assert.Equal("capture-1", main.SequenceEditor.SelectedStep!.Id);
        Assert.Equal("inspection-recipe", main.SequenceEditor.SelectedStepDraft!.Parameter);
        Assert.False(main.HasUnsavedChanges || main.IsRunning);
        await Task.CompletedTask;
    }));

    [Fact]
    public Task ComparisonDoesNotSurviveARecipeChangeAndDisposeReleasesCollectionNotifications() => ui.InvokeTaskAsync(async () =>
    {
        string root = Path.Combine(TestStorage.RootPath, "comparison-lifetime", Guid.NewGuid().ToString("N"));
        var project = new MachineProjectDocument { Id = "comparison-a", Name = "Recipe A" };
        var store = new MachineRunRecordFileStore(root);
        var record = Record(project);
        await store.SaveAsync(record);
        await store.SaveAsync(record with { Id = "22222222222222222222222222222222" });
        using var history = new RunHistoryViewModel(store, () => project, () => true, _ => Task.FromResult(false), action => { action(); return Task.CompletedTask; });
        await history.RefreshAsync();
        history.ComparedRun = Assert.Single(history.ComparisonRuns);
        var previousComparison = history.ComparedRun;
        Assert.True(history.HasComparison);
        project = new MachineProjectDocument { Id = "comparison-b", Name = "Recipe B" };
        await store.SaveAsync(Record(project));
        await store.SaveAsync(Record(project) with { Id = "22222222222222222222222222222222" });
        history.LoadProject();
        await WaitForAsync(() => !history.IsBusy && history.Runs.Count == 2 && history.Runs.All(run => run.Record.RecipeId == "comparison-b"));
        Assert.Null(history.ComparedRun);
        Assert.False(history.HasComparison);
        history.ComparedRun = previousComparison;
        Assert.Null(history.ComparedRun);
        history.ComparedRun = Assert.Single(history.ComparisonRuns);
        Assert.True(history.HasComparison);
        int notifications = 0;
        history.PropertyChanged += (_, _) => notifications++;
        history.Dispose();
        history.Runs.Clear();
        Assert.Equal(0, notifications);
        Assert.Null(history.ComparedRun);
        Assert.False(history.HasComparison);
        Assert.Empty(history.ComparisonRows);
        Assert.Null(history.ComparedImage);
        Assert.Empty(history.ComparedCaptureThumbnails);
    });

    [Theory]
    [InlineData(true, OpenVisionLanguage.Korean, false)]
    [InlineData(false, OpenVisionLanguage.Korean, false)]
    [InlineData(true, OpenVisionLanguage.English, false)]
    [InlineData(false, OpenVisionLanguage.English, false)]
    [InlineData(false, OpenVisionLanguage.Korean, true)]
    public Task RecordedCameraSettingsRoundTripPreservesComparisonAndNeverInfersAStep(bool fullArea, OpenVisionLanguage language, bool longName) => ui.InvokeTaskAsync(() => WithHistoryAsync(async (main, window, root, project) =>
    {
        var history = main.RunHistory;
        var selected = history.Selected!;
        var compared = Assert.Single(history.ComparisonRuns);
        history.ComparedRun = compared;
        main.Navigation.IsProcessWorkspace = true;
        main.SequenceEditor.SelectedStep = main.SequenceEditor.Steps[6];
        var selectedStep = main.SequenceEditor.SelectedStep;
        main.Camera.SelectCameraCommand.Execute("other-camera");
        Assert.Equal("other-camera", main.Camera.SelectedCameraId);
        main.Camera.CaptureEditor.RecipeName = "other camera retained draft";
        main.Navigation.IsResultsWorkspace = true;
        Pump(window);
        var view = Assert.Single(Descendants(window).OfType<RunHistoryView>());
        var tabs = Assert.IsType<TabControl>(view.FindName("RunEvidenceTabs"));
        tabs.SelectedIndex = 3;
        Pump(window);
        var capture = history.SelectedCapture!;
        var selectedImage = Assert.IsType<Image>(view.FindName("RunComparisonSelectedImage")).Source;
        var comparedImage = Assert.IsType<Image>(view.FindName("RunComparisonOtherImage")).Source;
        var button = Assert.IsType<Button>(view.FindName("RunHistoryCameraSettingsButton"));
        Assert.Same(history.OpenCameraSettingsCommand, button.Command);
        Assert.Same(capture, button.CommandParameter);
        Assert.True(button.IsVisible && button.IsEnabled);
        var details = Assert.IsType<ScrollViewer>(view.FindName("RunDetailsScroll"));
        Assert.True(new Rect(details.RenderSize).Contains(button.TransformToAncestor(details).TransformBounds(new Rect(button.RenderSize))));
        Assert.Contains(project.Devices[0].Name, history.CurrentCameraSettingsTargetText);
        Assert.Contains(capture.CameraId, history.CurrentCameraSettingsTargetText);
        Assert.Contains(language == OpenVisionLanguage.Korean ? "기록되지 않았습니다" : "not recorded", Assert.IsType<TextBlock>(view.FindName("RunHistoryCameraSettingsHint")).Text);
        string before = new ProjectDocumentStore().Serialize(project);
        var stored = Directory.GetFiles(root, "*.json", SearchOption.AllDirectories).ToDictionary(path => path, File.ReadAllBytes);
        string suffix = $"{language}-{(fullArea ? "work-area" : "compact")}-{(longName ? "long" : "normal")}";
        Capture(window, "results-camera-link-" + suffix);
        var targetScroll = Assert.IsType<ScrollViewer>(view.FindName("RunHistoryCameraTargetScroll"));
        if (longName) { targetScroll.ScrollToBottom(); Pump(window); Assert.True(targetScroll.VerticalOffset > 0); }
        var hint = Assert.IsType<TextBlock>(view.FindName("RunHistoryCameraSettingsHint"));
        Assert.True(new Rect(targetScroll.RenderSize).Contains(hint.TransformToAncestor(targetScroll).TransformBounds(new Rect(hint.RenderSize))));
        if (longName) Capture(window, "results-camera-target-long-scrolled");
        Invoke(button);
        Pump(window);
        Assert.True(main.Navigation.IsInspectionWorkspace && main.Navigation.IsInspectionSettingsExpanded && main.Navigation.HasResultSettingsReturn);
        Assert.Equal(0, main.Navigation.SelectedInspectionSettingsTabIndex);
        Assert.Equal(capture.CameraId, main.Camera.SelectedCameraId);
        Assert.Same(selectedStep, main.SequenceEditor.SelectedStep);
        Assert.Equal("capture-2", main.SequenceEditor.SelectedStep!.Id);
        Assert.True(main.Camera.CaptureEditor.CanEdit);
        main.Camera.CaptureEditor.RecipeName = "record camera retained draft";
        var back = Assert.Single(Descendants(window).OfType<Button>(), item => item.Name == "MmiReturnToResultsButton");
        Assert.True(back.IsVisible && back.IsEnabled);
        Assert.Same(main.Navigation.ReturnToResultsCommand, back.Command);
        Assert.True(new Rect(window.RenderSize).Contains(back.TransformToAncestor(window).TransformBounds(new Rect(back.RenderSize))));
        Capture(window, "camera-settings-from-results-" + suffix);
        Invoke(back);
        Pump(window);
        Assert.True(main.Navigation.IsResultsWorkspace);
        Assert.False(main.Navigation.HasResultSettingsReturn);
        Assert.Same(selected, history.Selected);
        Assert.Same(compared, history.ComparedRun);
        Assert.Same(capture, history.SelectedCapture);
        Assert.Equal(3, tabs.SelectedIndex);
        Assert.Same(selectedImage, Assert.IsType<Image>(view.FindName("RunComparisonSelectedImage")).Source);
        Assert.Same(comparedImage, Assert.IsType<Image>(view.FindName("RunComparisonOtherImage")).Source);
        Capture(window, "results-camera-return-" + suffix);
        Assert.Equal("record camera retained draft", main.Camera.CaptureEditor.RecipeName);
        main.Camera.SelectCameraCommand.Execute("other-camera");
        Assert.Equal("other camera retained draft", main.Camera.CaptureEditor.RecipeName);
        Assert.Equal(before, new ProjectDocumentStore().Serialize(project));
        foreach (var saved in stored) Assert.Equal(saved.Value, File.ReadAllBytes(saved.Key));
        Assert.False(main.HasUnsavedChanges || main.IsRunning);
        Invoke(Assert.IsType<Button>(view.FindName("RunHistoryReturnButton")));
        Pump(window);
        Assert.True(main.Navigation.IsProcessWorkspace);
        Assert.Same(selectedStep, main.SequenceEditor.SelectedStep);
        await Task.CompletedTask;
    }, fullArea, language, project =>
    {
        project.Devices.Add(new DeviceDefinition { Id = "other-camera", Name = "Other camera", Kind = DeviceKind.Camera, Camera = new VirtualCameraDefinition() });
        if (longName) project.Devices[0].Name = string.Join(" ", Enumerable.Repeat("후공정 둘째 촬영 위치의 검사 카메라", 9));
    }));

    [Fact]
    public Task CameraSettingsRejectsMissingForeignStaleAndDeletedTargetsThenRecovers() => ui.InvokeTaskAsync(() => WithHistoryAsync(async (main, window, root, project) =>
    {
        var history = main.RunHistory;
        var selected = history.Selected!;
        var capture = history.SelectedCapture!;
        var command = history.OpenCameraSettingsCommand;
        var camera = project.Devices[0];
        Assert.True(command.CanExecute(capture));
        Assert.False(command.CanExecute(capture with { }));
        Assert.False(command.CanExecute(selected.Record.Captures[1]));
        history.SelectedCapture = capture with { AcquisitionId = "foreign-capture" };
        Assert.False(command.CanExecute(history.SelectedCapture));
        history.SelectedCapture = capture;
        history.Selected = new RunHistoryEntry(selected.Record);
        Assert.False(command.CanExecute(history.SelectedCapture));
        history.Selected = selected;
        foreach (string cameraId in new[] { "", "deleted-camera" })
        {
            var missing = new RunHistoryEntry(selected.Record with { Id = Guid.NewGuid().ToString("N"), Captures = [capture with { CameraId = cameraId }] });
            history.Runs.Add(missing); history.Selected = missing;
            Assert.False(command.CanExecute(history.SelectedCapture));
            command.Execute(history.SelectedCapture);
            Assert.True(main.Navigation.IsResultsWorkspace);
            Assert.Contains(OpenVisionLanguageService.T(cameraId.Length == 0 ? "RunHistory.CameraNotRecorded" : "RunHistory.CurrentCameraMissing").Split('{')[0], history.CurrentCameraSettingsTargetText);
            history.Runs.Remove(missing);
        }
        var foreign = new RunHistoryEntry(selected.Record with { RecipeId = "another-recipe" });
        history.Runs.Add(foreign); history.Selected = foreign;
        Assert.False(command.CanExecute(history.SelectedCapture));
        command.Execute(history.SelectedCapture);
        Assert.True(main.Navigation.IsResultsWorkspace);
        history.Runs.Remove(foreign); history.Selected = selected;
        project.Devices.Remove(camera);
        history.InvalidateCommands(); Pump(window);
        var view = Assert.Single(Descendants(window).OfType<RunHistoryView>());
        Assert.False(Assert.IsType<Button>(view.FindName("RunHistoryCameraSettingsButton")).IsEnabled);
        command.Execute(capture);
        Assert.True(main.Navigation.IsResultsWorkspace);
        project.Devices.Add(camera);
        history.Runs.Remove(selected);
        Assert.False(command.CanExecute(capture));
        command.Execute(capture);
        Assert.True(main.Navigation.IsResultsWorkspace);
        history.Runs.Add(selected);
        await history.RefreshAsync();
        Assert.False(command.CanExecute(capture));
        history.Selected = history.Runs.Single(run => run.RunId == selected.RunId);
        var missingPixels = history.Selected.Record.Captures[1];
        history.SelectedCapture = missingPixels;
        Assert.Null(history.CaptureImage);
        Assert.True(command.CanExecute(missingPixels));
        Pump(window);
        var settingsButton = Assert.IsType<Button>(view.FindName("RunHistoryCameraSettingsButton"));
        Assert.Same(missingPixels, settingsButton.CommandParameter);
        Assert.True(settingsButton.IsEnabled);
        Invoke(settingsButton);
        Pump(window);
        Assert.True(main.Navigation.IsInspectionWorkspace);
        Assert.Equal(missingPixels.CameraId, main.Camera.SelectedCameraId);
        main.Navigation.ReturnToResultsCommand.Execute(null);
        Pump(window);
        Assert.True(main.Navigation.IsResultsWorkspace);
        Assert.Same(missingPixels, history.SelectedCapture);
        Assert.False(main.IsRunning || main.HasUnsavedChanges);
    }));

    [Fact]
    public Task CameraSettingsHonorsDraftCancelRevalidatesAfterDiscardAndPreservesReturnContext() => ui.InvokeTaskAsync(() => WithHistoryAsync(async (main, window, root, project) =>
    {
        var history = main.RunHistory;
        var capture = history.SelectedCapture!;
        var camera = project.Devices[0];
        var command = history.OpenCameraSettingsCommand;
        main.SequenceEditor.SelectedStep = main.SequenceEditor.Steps[6];
        main.SequenceEditor.SelectedStepDraft!.Parameter = "pending-capture-recipe";
        main.SequenceEditor.PendingStepDraftPrompt = () => PlacementDraftDecision.Cancel;
        command.Execute(capture);
        Assert.True(main.Navigation.IsResultsWorkspace);
        Assert.Equal("pending-capture-recipe", main.SequenceEditor.SelectedStepDraft.Parameter);
        main.SequenceEditor.PendingStepDraftPrompt = () => { project.Devices.Remove(camera); return PlacementDraftDecision.Discard; };
        command.Execute(capture);
        Assert.True(main.Navigation.IsResultsWorkspace);
        Assert.False(main.Navigation.HasResultSettingsReturn);
        project.Devices.Add(camera);
        main.SequenceEditor.SelectedStepDraft.Parameter = "pending-again";
        main.SequenceEditor.PendingStepDraftPrompt = () => PlacementDraftDecision.Discard;
        command.Execute(capture);
        Pump(window);
        Assert.True(main.Navigation.IsInspectionWorkspace);
        main.SequenceEditor.SelectedStepDraft!.Parameter = "cancel-return";
        main.SequenceEditor.PendingStepDraftPrompt = () => PlacementDraftDecision.Cancel;
        main.Navigation.ReturnToResultsCommand.Execute(null);
        Assert.True(main.Navigation.IsInspectionWorkspace && main.Navigation.HasResultSettingsReturn);
        main.SequenceEditor.PendingStepDraftPrompt = () => PlacementDraftDecision.Discard;
        main.Navigation.OpenTestScenarioCommand.Execute(null);
        Assert.False(main.Navigation.ReturnToResultsCommand.CanExecute(null));
        main.Navigation.ReturnToResultsCommand.Execute(null);
        Assert.True(main.Navigation.IsInspectionWorkspace);
        main.Navigation.CloseTestScenarioCommand.Execute(null);
        main.Navigation.ReturnToResultsCommand.Execute(null);
        Pump(window);
        Assert.True(main.Navigation.IsResultsWorkspace);
        Assert.False(main.HasUnsavedChanges || main.IsRunning);
        command.Execute(capture);
        Assert.True(main.Navigation.HasResultSettingsReturn);
        main.Navigation.OpenEquipmentWorkspaceCommand.Execute(null);
        Assert.False(main.Navigation.HasResultSettingsReturn || main.Navigation.ReturnToResultsCommand.CanExecute(null));
        main.Navigation.OpenResultsWorkspaceCommand.Execute(null);
        Pump(window);
        Assert.True(main.Navigation.IsResultsWorkspace);
        main.Navigation.OpenTestScenarioCommand.Execute(null);
        Assert.False(command.CanExecute(capture));
        Pump(window);
        var view = Assert.Single(Descendants(window).OfType<RunHistoryView>());
        Assert.False(Assert.IsType<Button>(view.FindName("RunHistoryCameraSettingsButton")).IsEnabled);
        command.Execute(capture);
        Assert.True(main.Navigation.IsResultsWorkspace);
        main.Navigation.CloseTestScenarioCommand.Execute(null);
        Pump(window);
        Assert.True(Assert.IsType<Button>(view.FindName("RunHistoryCameraSettingsButton")).IsEnabled);
        File.AppendAllText(Path.Combine(root, "l04-admission-trace.txt"), "before run mode\n");
        main.IsDesignMode = false;
        await WaitForAsync(() => !main.IsModeTransitioning);
        File.AppendAllText(Path.Combine(root, "l04-admission-trace.txt"), "run mode admitted\n");
        Assert.False(command.CanExecute(capture));
        command.Execute(capture);
        Assert.True(main.Navigation.IsResultsWorkspace);
        main.IsDesignMode = true;
        await WaitForAsync(() => !main.IsModeTransitioning);
        File.AppendAllText(Path.Combine(root, "l04-admission-trace.txt"), "design mode recovered\n");
        Assert.True(command.CanExecute(capture));
        main.UnsavedProjectPrompt = () => UnsavedProjectDecision.Discard;
        File.AppendAllText(Path.Combine(root, "l04-admission-trace.txt"), "before close admission\n");
        var closeResult = await main.RequestCloseAsync(TimeSpan.FromSeconds(5));
        File.AppendAllText(Path.Combine(root, "l04-admission-trace.txt"), "close outcome: " + closeResult.Outcome + "\n");
        Assert.True(closeResult.IsApproved);
        Assert.False(command.CanExecute(capture));
        command.Execute(capture);
        Assert.True(main.Navigation.IsResultsWorkspace);
    }));

    [Fact]
    public Task CameraSettingsReturnIsClearedWhenAnotherRecipeIsOpened() => ui.InvokeTaskAsync(() => WithHistoryAsync(async (main, window, root, project) =>
    {
        var capture = main.RunHistory.SelectedCapture!;
        main.RunHistory.OpenCameraSettingsCommand.Execute(capture);
        Assert.True(main.Navigation.HasResultSettingsReturn);
        var other = R40ProcessWorkspaceTests.CreateProject();
        other.Id = "different-recipe";
        string path = Path.Combine(root, "different-recipe.ovmachine");
        await new ProjectDocumentFileStore().SaveAsync(other, path);
        main.UnsavedProjectPrompt = () => UnsavedProjectDecision.Discard;
        Assert.True(await main.OpenProjectAsync(path));
        Pump(window);
        Assert.False(main.Navigation.HasResultSettingsReturn || main.Navigation.ReturnToResultsCommand.CanExecute(null));
        Assert.False(main.RunHistory.OpenCameraSettingsCommand.CanExecute(capture));
        Assert.Equal("camera", main.Camera.SelectedCameraId);
        Assert.False(main.IsRunning);
    }));

    [Fact]
    public Task CameraSettingsBusyApplyAndDisposeRejectDirectExecutionAndDoNotRetainNewSubscriptions() => ui.InvokeTaskAsync(async () =>
    {
        var project = R40ProcessWorkspaceTests.CreateProject();
        var record = Record(project);
        bool allow = true;
        int opened = 0;
        var waiting = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var history = new RunHistoryViewModel(new MachineRunRecordFileStore(Path.Combine(TestStorage.RootPath, "camera-settings-busy")), () => project, () => allow,
            _ => waiting.Task, action => { action(); return Task.CompletedTask; }, openCameraSettings: _ => opened++, canOpenCameraSettings: _ => true);
        var run = new RunHistoryEntry(record);
        history.Runs.Add(run); history.Selected = run;
        var capture = history.SelectedCapture!;
        history.PrepareRerunCommand.Execute(null);
        var apply = history.ApplyDraftAsync();
        Assert.True(history.IsBusy);
        Assert.False(history.OpenCameraSettingsCommand.CanExecute(capture));
        history.OpenCameraSettingsCommand.Execute(capture);
        Assert.Equal(0, opened);
        waiting.SetResult(false); await apply;
        Assert.True(history.HasDraft);
        allow = false;
        Assert.False(history.OpenCameraSettingsCommand.CanExecute(capture));
        history.OpenCameraSettingsCommand.Execute(capture);
        Assert.Equal(0, opened);
        allow = true;
        Assert.True(history.OpenCameraSettingsCommand.CanExecute(capture));
        history.OpenCameraSettingsCommand.Execute(capture);
        Assert.Equal(1, opened);
        history.Dispose();
        Assert.False(history.OpenCameraSettingsCommand.CanExecute(capture));
        history.OpenCameraSettingsCommand.Execute(capture);
        Assert.Equal(1, opened);
        int notifications = 0;
        history.PropertyChanged += (_, _) => notifications++;
        history.Runs.Clear();
        Assert.Equal(0, notifications);
    });

    private static async Task WithHistoryAsync(Func<MainViewModel, ShellWindow, string, MachineProjectDocument, Task> action, bool fullArea = false, OpenVisionLanguage language = OpenVisionLanguage.Korean, Action<MachineProjectDocument>? configure = null)
    {
        string? previousRoot = Environment.GetEnvironmentVariable("OPENVISIONLAB_MACHINE_STUDIO_RUN_HISTORY_DIR");
        var previousLanguage = OpenVisionLanguageService.CurrentLanguage;
        string root = Path.Combine(TestStorage.RootPath, "result-records", Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("OPENVISIONLAB_MACHINE_STUDIO_RUN_HISTORY_DIR", root);
        OpenVisionLanguageService.SetLanguage(language, save: false);
        try
        {
            var project = R40ProcessWorkspaceTests.CreateProject();
            project.Id = "r40-results-fixture";
            configure?.Invoke(project);
            var store = new MachineRunRecordFileStore(root);
            var record = Record(project);
            await store.SaveAsync(record);
            await store.SaveAsync(record with
            {
                Id = "22222222222222222222222222222222", StartedAtUtc = record.StartedAtUtc.AddMinutes(-5), EndedAtUtc = record.EndedAtUtc.AddMinutes(-5),
                Outcome = "Faulted", ActualFeedCount = null, EndedSimulationSeconds = 16.5, Faults = ["CAMERA_TIMEOUT: inspection response was not recorded"],
                Captures = [PngCapture(inverted: true) with { AcquisitionId = "image-b", Verdict = null }], Products = [], RuntimeSettingsJson = null
            });
            using var main = new MainViewModel(project);
            main.Navigation.HideStartupChoice();
            main.Navigation.IsResultsWorkspace = true;
            await WaitForAsync(() => !main.RunHistory.IsBusy && main.RunHistory.Runs.Count == 2);
            var window = Show(main, fullArea);
            try { await action(main, window, root, project); }
            finally { window.DataContext = null; window.Close(); }
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENVISIONLAB_MACHINE_STUDIO_RUN_HISTORY_DIR", previousRoot);
            OpenVisionLanguageService.SetLanguage(previousLanguage, save: false);
        }
    }

    private static MachineRunRecord Record(MachineProjectDocument project) => new()
    {
        Id = "11111111111111111111111111111111", RecipeId = project.Id, RecipeName = project.Name,
        AppliedRecipeJson = new ProjectDocumentStore().Serialize(project), Outcome = "Completed", Scope = "Full machine",
        StartedAtUtc = new DateTimeOffset(2026, 10, 16, 0, 0, 0, TimeSpan.Zero), EndedAtUtc = new DateTimeOffset(2026, 10, 16, 0, 0, 12, TimeSpan.Zero),
        StartedSimulationSeconds = 0, EndedSimulationSeconds = 12, TargetFeedCount = 2, ActualFeedCount = 2,
        Products = [new("product-1", "샘플 제품", "unit-a", "Exited", "OK", "OK route", 10, 100)],
        Captures = [PngCapture(), new() { AcquisitionId = "image-missing", CameraId = "camera", ProductId = "product-2" }],
        Inspections = [new("request-1", "frame-1", "{\"productId\":\"product-1\"}", "{\"verdict\":\"OK\"}")], RuntimeSettingsJson = "{\"runMode\":\"automatic\"}",
        BuildIdentity = "Owned UI fixture; not an optical or production run"
    };

    private static RunCaptureRecord PngCapture(bool inverted = false)
    {
        const int width = 320, height = 200;
        var pixels = new byte[width * height];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++) pixels[y * width + x] = (byte)(x > 60 && x < 260 && y > 45 && y < 155 ? 180 + y / 4 : 30 + x / 8);
        if (inverted)
            for (int i = 0; i < pixels.Length; i++) pixels[i] = (byte)(255 - pixels[i]);
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Gray8, null, pixels, width);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return new() { AcquisitionId = "image-a", CameraId = "camera", ProductId = "product-1", FrameId = "frame-1", Width = width, Height = height, PixelFormat = "Gray8", Pixels = stream.ToArray(), Verdict = "OK", Tick = 100 };
    }

    private static ShellWindow Show(MainViewModel main, bool fullArea)
    {
        var window = new ShellWindow { DataContext = main, ShowInTaskbar = false, ShowActivated = false };
        SmokeDpiTestHook.PlaceOnTestMonitor(window, 1280, 760);
        window.Show();
        if (fullArea)
        {
            var area = SmokeDpiTestHook.CaptureMonitorEvidence(window).WorkingArea.Split(',').Select(int.Parse).ToArray();
            var scale = VisualTreeHelper.GetDpi(window);
            window.Left = area[0] / scale.DpiScaleX;
            window.Top = area[1] / scale.DpiScaleY;
            window.Width = area[2] / scale.DpiScaleX;
            window.Height = area[3] / scale.DpiScaleY;
        }
        Pump(window);
        return window;
    }

    private static void Pump(Window window) => window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(window.UpdateLayout));
    private static void Invoke(Button button) => Assert.IsAssignableFrom<IInvokeProvider>(new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(20);
        Assert.True(condition(), "Expected result-workspace state was not observed within the bounded wait.");
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var item in Descendants(child)) yield return item;
        }
    }

    private static void Capture(Window window, string name)
    {
        Pump(window);
        var monitor = SmokeDpiTestHook.CaptureMonitorEvidence(window);
        Assert.True(monitor.WindowIntersectsMonitor && monitor.WindowContainedByMonitor);
        var dpi = VisualTreeHelper.GetDpi(window);
        int width = (int)Math.Round(window.ActualWidth * dpi.DpiScaleX), height = (int)Math.Round(window.ActualHeight * dpi.DpiScaleY);
        var directory = Path.Combine(TestStorage.RootPath, "results-workspace");
        Directory.CreateDirectory(directory);
        var bitmap = new RenderTargetBitmap(width, height, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var output = File.Create(Path.Combine(directory, name + ".png"))) encoder.Save(output);
        File.WriteAllText(Path.Combine(directory, name + ".json"), JsonSerializer.Serialize(new
        {
            monitor, nativeDpi = GetDpiForWindow(new WindowInteropHelper(window).Handle), wpfDpi = dpi.PixelsPerInchX,
            width, height, method = "In-process WPF RenderTargetBitmap; isolated recorded-data fixture, no native input qualification", crop = "none"
        }));
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);
}
