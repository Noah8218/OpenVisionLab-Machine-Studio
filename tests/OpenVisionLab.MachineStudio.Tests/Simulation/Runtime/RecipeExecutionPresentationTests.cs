using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using OpenVisionLab;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Core.Layouts;
using OpenVisionLab.Machine.Persistence.Projects;
using OpenVisionLab.Machine.Simulation.Engine;
using OpenVisionLab.Machine.Core.Sequences;
using OpenVisionLab.MachineStudio.View.Shell;
using OpenVisionLab.MachineStudio.ViewModel;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

[Collection(StudioUiTestCollection.Name)]
public sealed class RecipeExecutionPresentationTests
{
    private readonly StudioUiTestHost _ui;
    public RecipeExecutionPresentationTests(StudioUiTestHost ui) => _ui = ui;

    [Theory]
    [InlineData(1000, 720, OpenVisionLanguage.Korean)]
    [InlineData(1000, 720, OpenVisionLanguage.English)]
    [InlineData(1280, 760, OpenVisionLanguage.Korean)]
    [InlineData(1920, 1040, OpenVisionLanguage.Korean)]
    [InlineData(1280, 760, OpenVisionLanguage.English)]
    [InlineData(1920, 1040, OpenVisionLanguage.English)]
    public async Task CompletedParallelRecipeKeepsSimulationRunningAndDisplaysEveryUnit(double width, double height, OpenVisionLanguage language)
    {
        await _ui.InvokeTaskAsync(async () =>
        {
            var previousLanguage = OpenVisionLanguageService.CurrentLanguage;
            OpenVisionLanguageService.SetLanguage(language, save: false);
            using var model = new MainViewModel(CreateProject());
            model.Navigation.IsSimulationWorkspace = true;
            var window = new ShellWindow { DataContext = model, ShowInTaskbar = false, ShowActivated = false };
            SmokeDpiTestHook.PlaceOnTestMonitor(window, (int)width, (int)height);
            window.Show();
            try
            {
                Pump(window);
                var bar = Descendants(window).OfType<GlobalCommandBarView>().Single();
                var state = Assert.IsType<TextBlock>(bar.FindName("CommonRunStateText"));
                var recipe = Assert.IsType<TextBlock>(bar.FindName("CommonRecipeStateText"));
                Assert.False(recipe.IsVisible || model.HasRecipeExecutionStatus);
                Invoke(bar, model.RunCommand);
                await WaitForAsync(() => model.RecipeExecutionStatusText.Contains("3/3", StringComparison.Ordinal));
                Pump(window);
                Assert.True(model.IsRunning && model.IsRunMode && recipe.IsVisible);
                Assert.Equal(OpenVisionLanguageService.T("Shell.Running"), state.Text);
                Assert.Equal(model.RecipeExecutionStatusText, recipe.Text);
                Assert.Contains(OpenVisionLanguageService.T("Equipment.State.Completed"), recipe.Text);
                Assert.InRange(bar.ActualHeight, 34, 65);
                AssertFits(state, window);
                AssertFits(recipe, window);
                foreach (var button in Descendants(bar).OfType<Button>().Where(button => button.IsVisible)) AssertFits(button, window);
                Capture(window, language, width, height);

                Invoke(bar, model.PauseCommand);
                await WaitForAsync(() => !model.IsRunning);
                Pump(window);
                Assert.Equal(OpenVisionLanguageService.T("Shell.Paused"), state.Text);
                Assert.Contains("3/3", recipe.Text);
                model.IsDesignMode = true;
                await WaitForAsync(() => model.IsDesignMode && !model.IsModeTransitioning);
                Pump(window);
                Assert.False(recipe.IsVisible || model.HasRecipeExecutionStatus);
                Assert.Empty(model.RecipeExecutionStatusText);
                Assert.Equal(OpenVisionLanguageService.T("R24.Editing"), state.Text);
            }
            finally { window.DataContext = null; window.Close(); OpenVisionLanguageService.SetLanguage(previousLanguage, save: false); }
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AbortOrRepeatWaitNeverMasqueradesAsFinalCompletionAndResetClearsState(bool repeat)
    {
        await _ui.InvokeTaskAsync(async () =>
        {
            var project = CreateProject();
            if (repeat)
            {
                project.Simulation.AutomaticRun = new AutomaticRunDefinition { SequenceId = "sequence-a", Repeat = true, RepeatDelayMilliseconds = 5000 };
            }
            else foreach (var sequence in project.Sequences) sequence.Steps[0].Parameter = "5";
            using var model = new MainViewModel(project);
            var view = new GlobalCommandBarView { DataContext = model };
            var window = new Window { Content = view, Width = 1280, Height = 120, Left = -2500, Top = -2500, ShowInTaskbar = false, ShowActivated = false };
            window.Show();
            try
            {
                await WaitForAsync(() => model.RunCommand.CanExecute(null));
                Pump(window);
                Invoke(view, model.RunCommand);
                await WaitForAsync(() => model.IsRunning && model.HasRecipeExecutionStatus);
                if (repeat)
                {
                    await WaitForAsync(() => model.RecipeExecutionStatusText.Contains(OpenVisionLanguageService.T("Shell.RecipeRepeatWaitingProgress").Split('·')[0].Trim(), StringComparison.Ordinal));
                    Assert.Contains("3/3", model.RecipeExecutionStatusText);
                    Assert.False(model.RecipeExecutionStatusText.StartsWith(OpenVisionLanguageService.T("Equipment.State.Completed"), StringComparison.Ordinal));
                }
                else
                {
                    Invoke(view, model.AbortSequenceCommand);
                    await WaitForAsync(() => model.RecipeExecutionStatusText.Contains(OpenVisionLanguageService.T("Equipment.State.Aborted"), StringComparison.Ordinal));
                    Assert.Contains("0/3", model.RecipeExecutionStatusText);
                }
                await WaitForAsync(() => model.ResetCommand.CanExecute(null));
                Pump(window);
                Invoke(view, model.ResetCommand);
                await WaitForAsync(() => !model.IsRunning && model.RecipeExecutionStatusText.Contains("0/3", StringComparison.Ordinal)
                    && model.RecipeExecutionStatusText.Contains(OpenVisionLanguageService.T("Equipment.State.Ready"), StringComparison.Ordinal));
                Assert.Contains(OpenVisionLanguageService.T("Equipment.State.Ready"), model.RecipeExecutionStatusText);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public async Task ManualControlDoesNotPublishConfiguredRecipeCompletion()
    {
        await _ui.InvokeTaskAsync(async () =>
        {
            var project = new ProjectDocumentStore().Load(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Samples", "AutomaticTransferCell.ovmachine")));
            Assert.NotEmpty(project.Sequences);
            using var model = new MainViewModel(project);
            model.Navigation.IsEquipmentWorkspace = true;
            model.Layout.Select(model.Layout.Items.First(item => item.Component?.Kind == LayoutComponentKind.LinearStage).Id);
            await WaitForAsync(() => model.StartManualEquipmentControlCommand.CanExecute(null));
            var view = new GlobalCommandBarView { DataContext = model };
            var window = new Window { Content = view, Width = 1280, Height = 120, Left = -2500, Top = -2500, ShowInTaskbar = false, ShowActivated = false };
            window.Show();
            try
            {
                model.StartManualEquipmentControlCommand.Execute(null);
                await WaitForAsync(() => model.IsRunMode && !model.IsModeTransitioning
                    && model.SceneSnapshots.Latest?.ControlOwner == SimulationControlOwner.Manual);
                Pump(window);
                Assert.False(model.HasRecipeExecutionStatus);
                Assert.Empty(model.RecipeExecutionStatusText);
                Assert.False(Assert.IsType<TextBlock>(view.FindName("CommonRecipeStateText")).IsVisible);
                Assert.Equal(model.SimulationEngineStateText, Assert.IsType<TextBlock>(view.FindName("CommonRunStateText")).Text);
                model.IsDesignMode = true;
                await WaitForAsync(() => model.IsDesignMode && !model.IsModeTransitioning);
            }
            finally { window.Close(); }
        });
    }

    private static MachineProjectDocument CreateProject() => new()
    {
        Name = "병렬 유닛 완료 상태 확인",
        Stations = [new MachineStationDefinition { Id = "station", Name = "Station", Units = [new() { Id = "a", Name = "A", RepeatCount = 2 }, new() { Id = "b", Name = "B" }, new() { Id = "c", Name = "C" }] }],
        Sequences = new[] { "a", "b", "c" }.Select(id => new SequenceDefinition
        {
            Id = "sequence-" + id, Name = "Unit " + id, UnitId = id, WatchdogTimeoutMs = 30000,
            Steps = [new() { Id = "delay", Name = "Wait", Action = SequenceStepAction.Delay, Parameter = "0.1", NextStepId = "done" }, new() { Id = "done", Name = "Complete", Action = SequenceStepAction.Complete }]
        }).ToList()
    };

    private static void Invoke(FrameworkElement view, System.Windows.Input.ICommand command)
    {
        var button = Descendants(view).OfType<Button>().Single(button => ReferenceEquals(button.Command, command));
        Assert.True(button.IsEnabled);
        Assert.IsAssignableFrom<IInvokeProvider>(new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(20);
        Assert.True(condition(), "Expected runtime transition was not observed within the bounded wait.");
    }

    private static void Pump(Window window) => window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(window.UpdateLayout));
    private static void AssertFits(FrameworkElement control, Window window) => Assert.True(new Rect(window.RenderSize).Contains(control.TransformToAncestor(window).TransformBounds(new Rect(control.RenderSize))));
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private static void Capture(Window window, OpenVisionLanguage language, double width, double height)
    {
        var directory = Environment.GetEnvironmentVariable("OPENVISIONLAB_RECIPE_STATUS_EVIDENCE");
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory);
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(directory, $"recipe-completed-{language}-{width:0}x{height:0}.png"));
        encoder.Save(stream);
    }
}
