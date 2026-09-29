using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.MachineStudio.ViewModel;
using OpenVisionLab.MachineStudio.View.Shell;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

[Collection(LayoutStartupTestCollection.Name)]
public sealed class SmokeLayoutHistoryVerifierTests
{
    private static string SamplePath => Path.Combine(
        AppContext.BaseDirectory,
        "Samples",
        "AutomaticTransferCell.ovmachine");

    [Fact]
    public async Task VerifiesLayoutHistoryAndClipboardRoundTrip()
    {
        var evidenceRoot = Path.Combine(
            @"D:\OpenVisionLab-TestData\Machine\workspace-commands-20260924",
            "focused");
        Directory.CreateDirectory(evidenceRoot);
        var reportPath = Path.Combine(evidenceRoot, "layout-history-report.json");

        var report = await RunOnStaAsync(async () =>
        {
            var project = new ProjectDocumentStore().Load(File.ReadAllText(SamplePath));
            using var viewModel = new MainViewModel(project);

            var result = await SmokeLayoutHistoryVerifier.VerifyAsync(viewModel, reportPath);
            result.Save(reportPath);

            Assert.True(result.IsValid, string.Join(", ", result.Failures));
            Assert.NotEmpty(result.PastedComponentIds);
            Assert.All(result.Checks, check => Assert.True(check.Value, check.Key));
            Assert.True(viewModel.IsDesignMode);
            Assert.False(viewModel.IsRunning);
            return result;
        });

        Assert.True(File.Exists(reportPath));

        using var json = JsonDocument.Parse(File.ReadAllText(reportPath));
        Assert.True(json.RootElement.TryGetProperty("checks", out _));
        Assert.True(json.RootElement.TryGetProperty("pastedComponentIds", out _));
    }

    [Fact]
    public async Task ModeMeasurementWaitsForResetAndRejectsWrongMode()
    {
        await RunOnStaAsync(async () =>
        {
            var project = new ProjectDocumentStore().Load(File.ReadAllText(SamplePath));
            using var viewModel = new MainViewModel(project);
            viewModel.IsRunMode = true;
            viewModel.IsDesignMode = true;

            await SmokePerformanceVerifier.WaitForModeAsync(viewModel, Dispatcher.CurrentDispatcher, false);

            Assert.False(viewModel.IsModeTransitioning);
            Assert.True(viewModel.IsDesignMode);
            Assert.False(viewModel.IsRunning);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                SmokePerformanceVerifier.WaitForModeAsync(viewModel, Dispatcher.CurrentDispatcher, true));
            viewModel.IsRunMode = true;
            await SmokePerformanceVerifier.WaitForModeAsync(viewModel, Dispatcher.CurrentDispatcher, true);
            return true;
        });
    }

    [Fact]
    public async Task PerformanceVerifierHonorsIndependentIsEnabledConstraints()
    {
        await RunOnStaAsync(async () =>
        {
            var project = new ProjectDocumentStore().Load(File.ReadAllText(SamplePath));
            using var viewModel = new MainViewModel(project);
            var command = new RelayCommand(_ => { });
            var enabledButton = new Button { Command = command, Content = new DockPanel() };
            var locallyDisabledButton = new Button { Command = command, Content = new DockPanel(), DataContext = false };
            BindingOperations.SetBinding(locallyDisabledButton, UIElement.IsEnabledProperty, new Binding("."));
            var inheritedDisabledButton = new Button { Command = command, Content = new DockPanel() };
            var disabledParent = new Grid { IsEnabled = false };
            disabledParent.Children.Add(inheritedDisabledButton);
            var content = new StackPanel();
            content.Children.Add(enabledButton);
            content.Children.Add(locallyDisabledButton);
            content.Children.Add(disabledParent);
            var window = new Window { Width = 320, Height = 120, Content = content };
            window.Show();
            try
            {
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);

                Assert.True(enabledButton.IsVisible);
                Assert.True(enabledButton.IsEnabled);
                Assert.True(command.CanExecute(null));
                Assert.True(locallyDisabledButton.IsVisible);
                Assert.False(locallyDisabledButton.IsEnabled);
                Assert.True(inheritedDisabledButton.IsVisible);
                Assert.False(inheritedDisabledButton.IsEnabled);
                Assert.True(BindingOperations.IsDataBound(locallyDisabledButton, UIElement.IsEnabledProperty));

                SmokePerformanceVerifier.ValidateModeCommandSources(window, viewModel);
            }
            finally
            {
                window.Close();
            }

            return true;
        });
    }

    [Fact]
    public async Task EquipmentAddButtonsFollowDesignAndRunCommandAvailability()
    {
        await RunOnStaAsync(async () =>
        {
            var project = new ProjectDocumentStore().Load(File.ReadAllText(SamplePath));
            using var viewModel = new MainViewModel(project);
            var libraryButton = new Button { Command = viewModel.OpenEquipmentComponentLibraryDialogCommand };
            var addPartButton = new Button { Command = viewModel.AddEquipmentLayoutComponentCommand };
            var window = new Window
            {
                Width = 320,
                Height = 120,
                Content = new StackPanel { Children = { libraryButton, addPartButton } }
            };
            window.Show();
            try
            {
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                Assert.True(viewModel.Navigation.IsEquipmentWorkspace);
                Assert.True(libraryButton.IsEnabled);
                Assert.True(addPartButton.IsEnabled);

                viewModel.IsRunMode = true;
                await SmokePerformanceVerifier.WaitForModeAsync(viewModel, Dispatcher.CurrentDispatcher, true);
                Assert.False(viewModel.OpenEquipmentComponentLibraryDialogCommand.CanExecute(null));
                Assert.False(viewModel.AddEquipmentLayoutComponentCommand.CanExecute(null));
                Assert.False(libraryButton.IsEnabled);
                Assert.False(addPartButton.IsEnabled);

                viewModel.IsRunMode = false;
                await SmokePerformanceVerifier.WaitForModeAsync(viewModel, Dispatcher.CurrentDispatcher, false);
                Assert.True(viewModel.OpenEquipmentComponentLibraryDialogCommand.CanExecute(null));
                Assert.True(viewModel.AddEquipmentLayoutComponentCommand.CanExecute(null));
                Assert.True(libraryButton.IsEnabled);
                Assert.True(addPartButton.IsEnabled);
            }
            finally
            {
                window.Close();
            }

            return true;
        });
    }

    [Fact]
    public async Task GlobalCommandBarReflectsPauseResumeAndDesignResetAvailability()
    {
        await RunOnStaAsync(async () =>
        {
            if (Application.Current is null)
            {
                new App().InitializeComponent();
            }

            var project = new ProjectDocumentStore().Load(File.ReadAllText(SamplePath));
            using var viewModel = new MainViewModel(project);
            var commandBar = new GlobalCommandBarView { DataContext = viewModel };
            var window = new Window { Width = 1200, Height = 120, Content = commandBar };
            window.Show();
            try
            {
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                var runButton = FindCommandButton(commandBar, viewModel.RunCommand);
                var pauseButton = FindCommandButton(commandBar, viewModel.PauseCommand);
                var resetButton = FindCommandButton(commandBar, viewModel.ResetCommand);
                var designModeRadio = FindModeRadio(commandBar, nameof(MainViewModel.IsDesignMode));
                var runModeRadio = FindModeRadio(commandBar, nameof(MainViewModel.IsRunMode));

                Assert.True(
                    designModeRadio.IsChecked == true,
                    $"Design radio did not reflect initial mode. Model={viewModel.IsDesignMode}, run radio={runModeRadio.IsChecked}, binding={BindingOperations.GetBindingExpression(designModeRadio, ToggleButton.IsCheckedProperty)?.Status}.");
                Assert.False(pauseButton.IsVisible);
                Assert.False(resetButton.IsVisible);

                runModeRadio.IsChecked = true;
                await SmokePerformanceVerifier.WaitForModeAsync(viewModel, window.Dispatcher, true);
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                Assert.True(runButton.IsVisible);
                Assert.True(runButton.IsEnabled);
                Assert.False(pauseButton.IsEnabled);

                runButton.Command.Execute(null);
                await WaitForStateAsync(() => viewModel.IsRunning);
                Assert.True(pauseButton.IsVisible);
                Assert.True(pauseButton.IsEnabled);
                Assert.Equal(pauseButton.Command.CanExecute(null), pauseButton.IsEnabled);

                pauseButton.Command.Execute(null);
                await WaitForStateAsync(() => !viewModel.IsRunning);
                Assert.True(runButton.IsEnabled);
                Assert.False(pauseButton.IsEnabled);
                Assert.Equal(runButton.Command.CanExecute(null), runButton.IsEnabled);

                runButton.Command.Execute(null);
                await WaitForStateAsync(() => viewModel.IsRunning);
                designModeRadio.IsChecked = true;
                await SmokePerformanceVerifier.WaitForModeAsync(viewModel, window.Dispatcher, false);
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.DataBind);
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                Assert.False(viewModel.IsRunning);
                Assert.True(
                    designModeRadio.IsChecked == true,
                    $"Design radio remained unchecked after reset. Model={viewModel.IsDesignMode}, run radio={runModeRadio.IsChecked}, binding={BindingOperations.GetBindingExpression(designModeRadio, ToggleButton.IsCheckedProperty)?.Status}, DataContextMatches={ReferenceEquals(designModeRadio.DataContext, viewModel)}.");
                Assert.False(pauseButton.IsVisible);
                Assert.False(resetButton.IsVisible);
            }
            finally
            {
                window.Close();
            }

            return true;
        });
    }

    private static Button FindCommandButton(DependencyObject root, ICommand command)
    {
        foreach (var child in GetVisualDescendants(root))
        {
            if (child is Button button && ReferenceEquals(button.Command, command))
            {
                return button;
            }
        }

        throw new InvalidOperationException($"Command bar button for {command.GetType().Name} was not found.");
    }

    private static RadioButton FindModeRadio(DependencyObject root, string propertyName)
    {
        foreach (var child in GetVisualDescendants(root))
        {
            if (child is RadioButton radioButton
                && BindingOperations.GetBindingExpression(radioButton, ToggleButton.IsCheckedProperty)
                    ?.ParentBinding.Path?.Path == propertyName)
            {
                return radioButton;
            }
        }

        throw new InvalidOperationException($"Mode radio for {propertyName} was not found.");
    }

    private static IEnumerable<DependencyObject> GetVisualDescendants(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < count; index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            yield return child;
            foreach (var descendant in GetVisualDescendants(child))
            {
                yield return descendant;
            }
        }
    }

    private static async Task WaitForStateAsync(Func<bool> predicate)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!predicate())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException("The requested simulation run state was not reached within 10 seconds.");
            }

            await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            await Task.Delay(10);
        }
    }

    private static Task<T> RunOnStaAsync<T>(Func<Task<T>> action)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            _ = RunAsync();
            Dispatcher.Run();

            async Task RunAsync()
            {
                try
                {
                    completion.SetResult(await action());
                }
                catch (Exception exception)
                {
                    completion.SetException(exception);
                }
                finally
                {
                    dispatcher.BeginInvokeShutdown(DispatcherPriority.Normal);
                }
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }
}
