using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using OpenVisionLab;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Core.Axes;
using OpenVisionLab.Machine.Core.Layouts;
using OpenVisionLab.Machine.Core.Sequences;
using OpenVisionLab.Machine.Sequence.Runtime;
using OpenVisionLab.Machine.Simulation.Commissioning;
using OpenVisionLab.Machine.Simulation.Engine;
using OpenVisionLab.Machine.Simulation.Snapshots;
using OpenVisionLab.MachineStudio.View.Diagnostics;
using OpenVisionLab.MachineStudio.View.Scene;
using OpenVisionLab.MachineStudio.View.Shell;
using OpenVisionLab.MachineStudio.ViewModel;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

[Collection(StudioUiTestCollection.Name)]
public sealed class B06HistoryViewAutomationTests
{
    private readonly StudioUiTestHost _ui;
    public B06HistoryViewAutomationTests(StudioUiTestHost ui) => _ui = ui;

    [Theory]
    [InlineData(1280, 760, OpenVisionLanguage.Korean)]
    [InlineData(1280, 760, OpenVisionLanguage.English)]
    [InlineData(1920, 1040, OpenVisionLanguage.Korean)]
    [InlineData(1920, 1040, OpenVisionLanguage.English)]
    public async Task CapturedHistorySelectionShowsRecordedIdentityAndRecoversFromEmptyReference(double width, double height, OpenVisionLanguage language)
    {
        await _ui.InvokeTaskAsync(async () =>
        {
            var previous = OpenVisionLanguageService.CurrentLanguage;
            OpenVisionLanguageService.SetLanguage(language, save: false);
            var project = new MachineProjectDocument
            {
                Name = "Owned multi-axis history", Simulation = new SimulationDefinition { ActiveLayoutId = "layout" },
                Axes = [new VirtualAxisDefinition { Id = "axis", Name = "Axis", HomePosition = 0, SoftLimitMin = -10, SoftLimitMax = 10 }, new VirtualAxisDefinition { Id = "axis-y", Name = "Axis Y", HomePosition = 0, SoftLimitMin = -10, SoftLimitMax = 10 }],
                Layouts = [new MachineLayoutDefinition { Id = "layout", Name = "History scene", Components = [new LayoutComponentDefinition { Id = "frame", Name = "Frame", Kind = LayoutComponentKind.MachineFrame }] }]
            };
            project.Id = new string('P', 80);
            project.MultiAxisCommissioningRecipe = new MultiAxisCommissioningRecipeDefinition
            {
                Id = new string('R', 80), Name = "Owned history fixture", ValidationRepetitions = 2,
                Targets = [new MultiAxisCommissioningTargetDefinition { AxisId = project.Axes[0].Id, TargetPosition = 1 }, new MultiAxisCommissioningTargetDefinition { AxisId = project.Axes[1].Id, TargetPosition = 2 }]
            };
            using var model = new MainViewModel(project);
            model.Navigation.HideStartupChoice();
            var window = CreateWindow(model, width, height);
            window.Show();
            try
            {
                await WaitFor(() => model.SceneSnapshots.Latest?.Axes.Count > 0);
                var ownedFile = Path.Combine(EvidenceRoot(), $"history-{width}-{language}.ovmachine");
                Directory.CreateDirectory(EvidenceRoot());
                await model.SaveProjectAsync(ownedFile);
                model.IsRunMode = true;
                await WaitFor(() => !model.IsModeTransitioning);
                Assert.True(model.ValidateMultiAxisCommissioningRecipeCommand.CanExecute(null), model.MultiAxisCommissioningRecipe.ValidationMessage);
                model.ValidateMultiAxisCommissioningRecipeCommand.Execute(null);
                await WaitFor(() => model.CommissioningResultHistoryEntries.Count == 1 && !model.IsCommissioningValidationRunning);
                var first = model.CommissioningResultHistoryEntries[0];
                Assert.True(first.IsSuccess, JsonSerializer.Serialize(model.LatestCommissioningResult));
                model.ValidateMultiAxisCommissioningRecipeCommand.Execute(null);
                await WaitFor(() => model.CommissioningResultHistoryEntries.Count == 2 && !model.IsCommissioningValidationRunning);
                var payload = JsonSerializer.Serialize(project);
                model.Navigation.IsResultsWorkspace = true;
                Pump(window);
                var scene = Descendants(window).OfType<SceneDocumentView>().Single();
                scene.ResultsWorkspaceTabs.SelectedIndex = 1;
                Pump(window);
                Capture(window, $"history-{width}-{language}-entry");
                var layout = Assert.IsType<Grid>(scene.FindName("CommissioningHistoryLayout"));
                var list = Assert.IsType<ListBox>(scene.FindName("CommissioningResultHistoryList"));
                Assert.Equal(2, list.Items.Count);
                var item = Assert.IsType<ListBoxItem>(list.ItemContainerGenerator.ContainerFromIndex(0));
                ((ISelectionItemProvider)new ListBoxItemAutomationPeer(list.Items[0], new ListBoxAutomationPeer(list)).GetPattern(PatternInterface.SelectionItem)).Select();
                Pump(window);
                Assert.Same(first, model.SelectedCommissioningHistoryEntry);
                var details = Assert.IsType<Border>(scene.FindName("CommissioningHistoryDetails"));
                Assert.Same(first, details.DataContext);
                Assert.False(Bounds(list, layout).IntersectsWith(Bounds(details, layout)));
                AssertValue(scene, "CommissioningHistoryProjectId", first.Reference!.ProjectId);
                AssertValue(scene, "CommissioningHistoryRecipeId", first.Reference.RecipeId);
                AssertValue(scene, "CommissioningHistoryPackageHash", first.PackageEvidenceHash);
                AssertValue(scene, "CommissioningHistoryProjectHash", first.Reference.ProjectHash);
                AssertValue(scene, "CommissioningHistoryRecipeHash", first.Reference.RecipeHash);
                var state = Assert.IsType<TextBlock>(scene.FindName("CommissioningHistoryOutcome"));
                Assert.Equal(OpenVisionLanguageService.T("History.ValidationPassed"), state.Text);
                Assert.Contains(first.CapturedAtUtc.ToString("yyyy-MM-dd"), Assert.IsType<TextBlock>(scene.FindName("CommissioningHistoryCapturedAt")).Text);
                Capture(window, $"history-{width}-{language}-selected");
                var empty = Assert.IsType<TextBlock>(scene.FindName("CommissioningHistoryEmptySelection"));
                list.SelectedItem = null;
                Pump(window);
                Assert.Null(model.SelectedCommissioningHistoryEntry);
                Assert.True(empty.IsVisible);
                Assert.False(details.IsVisible);
                var missingReference = first with { Sequence = 3, IsSuccess = false, Reference = null, FirstMismatch = new DeterministicCommissioningMismatch(2, 37, "Axis", project.Axes[0].Id, "expected", "actual") };
                model.SelectedCommissioningHistoryEntry = missingReference;
                Pump(window);
                Assert.Equal(OpenVisionLanguageService.T("History.ValidationFailed"), state.Text);
                Assert.True(Assert.IsType<TextBlock>(scene.FindName("CommissioningHistoryReferenceUnavailable")).IsVisible);
                Assert.False(Assert.IsType<Grid>(scene.FindName("CommissioningHistoryReference")).IsVisible);
                AssertValue(scene, "CommissioningHistoryMismatchTarget", missingReference.FirstMismatch.TargetId);
                Capture(window, $"history-{width}-{language}-missing-reference");
                list.SelectedIndex = 0;
                Pump(window);
                Assert.Same(first, model.SelectedCommissioningHistoryEntry);
                model.Navigation.IsSimulationWorkspace = true;
                Pump(window);
                model.Navigation.IsResultsWorkspace = true;
                Pump(window);
                Assert.Same(first, model.SelectedCommissioningHistoryEntry);
                Assert.Equal(payload, JsonSerializer.Serialize(project));
                Assert.False(model.IsRunning || model.HasUnsavedChanges);
                Capture(window, $"history-{width}-{language}-returned");
                using var reopened = new MainViewModel();
                await reopened.OpenProjectAsync(ownedFile);
                Assert.Equal(2, reopened.CommissioningResultHistoryEntries.Count);
                Assert.Equal(first.EvidenceHash, reopened.CommissioningResultHistoryEntries[0].EvidenceHash);
                Assert.False(reopened.IsRunning || reopened.HasUnsavedChanges);
                Console.WriteLine($"B06 identity {typeof(MainViewModel).Assembly.Location}; selected UTC={first.CapturedAtUtc:O}; fixed ticks={first.Reference.FixedStepTicks}; history={list.Items.Count}");
                return true;
            }
            finally { window.DataContext = null; window.Close(); OpenVisionLanguageService.SetLanguage(previous, save: false); }
        });
    }

    [Theory]
    [InlineData(1280, 760, OpenVisionLanguage.Korean)]
    [InlineData(1280, 760, OpenVisionLanguage.English)]
    [InlineData(1920, 1040, OpenVisionLanguage.Korean)]
    [InlineData(1920, 1040, OpenVisionLanguage.English)]
    public async Task AlarmOriginWrapsAndExistingErrorCommandReturnsWithoutChangingRecords(double width, double height, OpenVisionLanguage language)
    {
        await _ui.InvokeTaskAsync(async () =>
        {
            var previous = OpenVisionLanguageService.CurrentLanguage;
            OpenVisionLanguageService.SetLanguage(language, save: false);
            var project = LoadProject();
            var sequence = project.Sequences[0];
            sequence.Name = new string('S', 80);
            var step = sequence.Steps.First(item => item.Action == SequenceStepAction.TriggerCamera);
            using var model = new MainViewModel(project);
            model.Navigation.HideStartupChoice();
            model.Navigation.IsResultsWorkspace = true;
            var window = CreateWindow(model, width, height);
            window.Show();
            try
            {
                await WaitFor(() => model.SceneSnapshots.Latest?.Axes.Count > 0);
                var payload = JsonSerializer.Serialize(project);
                var error = new SequenceExecutionError(SequenceExecutionErrorCode.CameraTriggerFailed, sequence.Id, step.Id, "Owned camera trigger failure");
                model.RuntimeDebugger.ApplySnapshot(new SimulationSnapshot(TimeSpan.Zero, 5, SimulationRunMode.Paused, SimulationControlOwner.EmbeddedSequence, 1,
                    [], 0, [], [new SequenceExecutionSnapshot(sequence.Id, SequenceExecutionStatus.Faulted, step.Id, 0, TimeSpan.Zero, TimeSpan.Zero, 5, error, TimeSpan.FromSeconds(10))]));
                Pump(window);
                Descendants(window).OfType<SceneDocumentView>().Single().ResultsWorkspaceTabs.SelectedIndex = 0;
                Pump(window);
                var journal = Descendants(window).OfType<EventJournalView>().Single(view => view.IsInline);
                journal.EvidenceTabs.SelectedIndex = 1;
                Pump(window);
                Capture(window, $"alarm-{width}-{language}-entry");
                var button = Assert.IsType<Button>(journal.FindName("NavigateAlarmRuntimeErrorButton"));
                Assert.Same(model.NavigateRuntimeErrorCommand, button.Command);
                Assert.Same(model.RuntimeDebugger.LastSequenceError, button.CommandParameter);
                Assert.True(button.IsEnabled);
                var alarms = Assert.IsType<ListBox>(journal.FindName("DebuggerAlarmList"));
                var container = Assert.IsType<ListBoxItem>(alarms.ItemContainerGenerator.ContainerFromIndex(0));
                var source = Descendants(container).OfType<TextBlock>().Single(text => text.Text == model.RuntimeDebugger.Alarms[0].Source);
                Assert.Equal(TextWrapping.Wrap, source.TextWrapping);
                Assert.Equal(TextTrimming.None, source.TextTrimming);
                AssertFits(source);
                ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
                Pump(window);
                Assert.True(model.Navigation.IsSimulationWorkspace);
                Assert.Equal(sequence.Id, model.SequenceEditor.SelectedSequence?.Id);
                Assert.Equal(step.Id, model.SequenceEditor.SelectedStep?.Id);
                var target = project.Layouts.SelectMany(layout => layout.Components).First(item => item.BehaviorBindingId == step.TargetId);
                Assert.Equal(target.Id, model.Layout.SelectedItem?.Id);
                model.Navigation.IsResultsWorkspace = true;
                Pump(window);
                Assert.Equal(1, journal.EvidenceTabs.SelectedIndex);
                Assert.Single(model.RuntimeDebugger.Alarms);
                var obsolete = model.RuntimeDebugger.LastSequenceError! with { ProjectId = "another-project" };
                Assert.False(button.Command.CanExecute(obsolete));
                button.Command.Execute(obsolete);
                Assert.True(model.Navigation.IsResultsWorkspace);
                Assert.Equal(payload, JsonSerializer.Serialize(project));
                Assert.False(model.IsRunning || model.HasUnsavedChanges);
                Capture(window, $"alarm-{width}-{language}-returned");
                return true;
            }
            finally { window.DataContext = null; window.Close(); OpenVisionLanguageService.SetLanguage(previous, save: false); }
        });
    }

    private static MachineProjectDocument LoadProject() => new ProjectDocumentStore().Load(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Samples", "R19InspectionFlow.ovmachine")));
    private static ShellWindow CreateWindow(MainViewModel model, double width, double height) => new() { DataContext = model, Width = width, Height = height, ShowInTaskbar = false, ShowActivated = false, UseLayoutRounding = true };
    private static void Pump(Window window) => window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(window.UpdateLayout));
    private static async Task WaitFor(Func<bool> predicate)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!predicate())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("Owned history fixture did not settle within 15 seconds.");
            await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            await Task.Delay(10);
        }
    }
    private static Rect Bounds(FrameworkElement element, Visual ancestor) => element.TransformToAncestor(ancestor).TransformBounds(new Rect(element.RenderSize));
    private static void AssertValue(SceneDocumentView scene, string name, string expected)
    {
        var text = Assert.IsType<TextBlock>(scene.FindName(name));
        Assert.Equal(expected, text.Text);
        text.BringIntoView();
        Pump(Window.GetWindow(scene));
        AssertFits(text);
        var scroll = Assert.IsType<ScrollViewer>(scene.FindName("CommissioningHistoryDetailScroll"));
        var visible = Bounds(text, scroll);
        Assert.True(visible.Top >= -1 && visible.Bottom <= scroll.ViewportHeight + 1, $"Unreachable record value: {name}={visible}; viewport={scroll.ViewportHeight}");
    }
    private static void AssertFits(TextBlock text)
    {
        var formatted = new FormattedText(text.Text, System.Globalization.CultureInfo.CurrentCulture, text.FlowDirection,
            new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch), text.FontSize, text.Foreground, VisualTreeHelper.GetDpi(text).PixelsPerDip) { MaxTextWidth = Math.Max(1, text.ActualWidth) };
        Assert.True(formatted.Height <= text.ActualHeight + 1, $"Unshown glyph height {formatted.Height} > {text.ActualHeight}: {text.Text}");
        if (text.Parent is FrameworkElement parent) Assert.True(Bounds(text, parent).Right <= parent.ActualWidth + 1, $"Text exceeds parent: {text.Text}");
    }
    private static string EvidenceRoot() => Environment.GetEnvironmentVariable("OPENVISIONLAB_MACHINE_STUDIO_UI_EVIDENCE_DIR") ?? @"D:\OpenVisionLab-TestData\Machine\r24-overnight-20261005\b06-history-presentation\ui";
    private static void Capture(Window window, string name)
    {
        var root = EvidenceRoot();
        Directory.CreateDirectory(root);
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(root, name + ".png"));
        encoder.Save(stream);
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }
}
