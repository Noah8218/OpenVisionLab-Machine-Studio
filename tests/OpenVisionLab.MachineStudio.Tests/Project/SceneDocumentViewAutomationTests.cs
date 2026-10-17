using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using OpenVisionLab;
using OpenVisionLab.Machine.Core.Devices;
using OpenVisionLab.Machine.Core.Layouts;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Persistence.Projects;
using OpenVisionLab.Machine.Sequence.Runtime;
using OpenVisionLab.Machine.Simulation.Engine;
using OpenVisionLab.MachineStudio.Model;
using OpenVisionLab.MachineStudio.View.Inspector;
using OpenVisionLab.MachineStudio.View.Scene;
using OpenVisionLab.MachineStudio.View.Shell;
using OpenVisionLab.MachineStudio.ViewModel;
using OpenVisionLab.TestSupport;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

[Collection(StudioUiTestCollection.Name)]
public sealed class SceneDocumentViewAutomationTests
{
    private readonly StudioUiTestHost _ui;

    public SceneDocumentViewAutomationTests(StudioUiTestHost ui) => _ui = ui;

    [Theory]
    [InlineData(665, false)]
    [InlineData(665, true)]
    [InlineData(705, false)]
    [InlineData(705, true)]
    [InlineData(1285, false)]
    [InlineData(1285, true)]
    public async Task R24EquipmentSelectionWrapsNameAndDetailAsSeparateItems(double width, bool longName)
    {
        await _ui.InvokeTaskAsync(async () =>
        {
            var previousLanguage = OpenVisionLanguageService.CurrentLanguage;
            OpenVisionLanguageService.SetLanguage(OpenVisionLanguage.Korean, save: false);
            try
            {
                var project = new ProjectDocumentStore().Load(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Samples", "AutomaticTransferCell.ovmachine")));
                var stage = project.Layouts[0].Components.First(component => component.Kind == LayoutComponentKind.LinearStage);
                stage.Id = "AX-01";
                stage.Name = longName ? new string('장', 60) : "카메라 이송 축";
                var before = new ProjectDocumentStore().SerializeForEvidence(project);
                using var vm = new MainViewModel(project);
                vm.Navigation.IsEquipmentWorkspace = true; vm.Layout.Select(stage.Id); vm.Layout.ShowOverview();
                vm.Navigation.IsInspectorOpen = true;
                var window = new ShellWindow { DataContext = vm, ShowInTaskbar = false, ShowActivated = false };
                SmokeDpiTestHook.PlaceOnTestMonitor(window, width < 800 ? 1280 : 1920, width < 800 ? 900 : 1032);
                window.Show();
                if (width == 665) ((ColumnDefinition)window.FindName("RightWorkspaceColumn")).Width = new GridLength(400);
                try
                {
                    var view = Assert.IsType<SceneDocumentView>(window.FindName("SceneDocumentView"));
                    async Task Settle()
                    {
                        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                        window.UpdateLayout();
                        new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32).Render(window);
                        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
                        CaptureWindow(window, $"r24-selection-{width:0}-{longName}");
                    }
                    await Settle(); await Settle();
                    Assert.Equal(width, Assert.IsAssignableFrom<FrameworkElement>(view.FindName("SceneViewport")).ActualWidth, 1);
                    var name = Assert.IsType<TextBlock>(view.FindName("EquipmentSelectionName"));
                    var detail = Assert.IsType<TextBlock>(view.FindName("EquipmentSelectionDetail"));
                    var strip = Assert.IsType<Border>(view.FindName("EquipmentSelectionActions"));
                    var actions = Assert.IsType<WrapPanel>(view.FindName("EquipmentSelectionCommands"));
                    Rect Bounds(FrameworkElement e) => e.TransformToAncestor(view).TransformBounds(new Rect(e.RenderSize));
                    var title = Assert.IsType<TextBlock>(view.FindName("EquipmentSceneTitle"));
                    var hint = Assert.IsType<TextBlock>(view.FindName("EquipmentSceneHint"));
                    Assert.InRange(Bounds(title).Left - Bounds(Assert.IsAssignableFrom<FrameworkElement>(view.FindName("SceneViewport"))).Left, 9.5, 10.5);
                    Assert.InRange(Bounds(hint).Top - Bounds(title).Bottom, 0, 0.5);
                    Assert.Equal("#FFE4EDF2", title.Foreground.ToString());
                    Assert.Equal("#FF9FB6C3", hint.Foreground.ToString());
                    if (width == 665) Assert.InRange(hint.ActualHeight, 33, 34);
                    Assert.True(strip.IsVisible && name.IsVisible && detail.IsVisible);
                    Assert.Equal(stage.Name, name.Text); Assert.Contains("리니어 축", detail.Text);
                    Console.WriteLine($"R24 selection: scene={width}, long={longName}, name={Bounds(name)}, detail={Bounds(detail)}, strip={Bounds(strip)}");
                    Assert.InRange(Bounds(detail).Left - Bounds(name).Right, 5, 7);
                    Assert.InRange(strip.ActualWidth, 1, width * 0.6 + 1);
                    Assert.True(Bounds(detail).Right <= Bounds(actions).Left);
                    Assert.All(actions.Children.OfType<Button>(), button => Assert.Equal(28, button.ActualHeight));
                    if (longName)
                    {
                        Assert.True(name.ActualHeight > 30 && detail.ActualHeight > 30);
                        if (width < 800) Assert.True(detail.ActualHeight >= 69.6);
                    }
                    else if (width == 665) Assert.InRange(name.ActualHeight, 33, 36);
                    else Assert.InRange(name.ActualHeight, 17, 18);
                    Assert.Equal(before, new ProjectDocumentStore().SerializeForEvidence(project));
                    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(SmokeDpiTestHook.CaptureMonitorEvidence(window)));
                }
                finally { window.DataContext = null; window.Close(); }
            }
            finally { OpenVisionLanguageService.SetLanguage(previousLanguage, save: false); }
            return true;
        });
    }

    [Theory]
    [InlineData(1280, 760)]
    [InlineData(1920, 1040)]
    public async Task EmptyRecipeNextStepUsesRenderedUnitAndPartCommandsWithoutRunning(double width, double height)
    {
        await _ui.InvokeTaskAsync(async () =>
        {
            using var vm = new MainViewModel(new MachineProjectDocument { Name = "Previous inspection recipe" });
            vm.Navigation.IsInspectionWorkspace = true;
            var window = new ShellWindow { DataContext = vm, Width = width, MinWidth = width, Height = height, ShowActivated = false, ShowInTaskbar = false };
            window.Show();
            try
            {
                Assert.True(await vm.CreateNewProjectAsync("Recipe entry review"));
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                window.UpdateLayout();
                Assert.Equal(width, window.ActualWidth, 1);
                Capture("created");
                Assert.True(vm.Navigation.IsEquipmentWorkspace);
                var next = Descendants(window).OfType<Button>().Single(button => button.Name == "EmptyRecipeNextStepButton");
                Assert.True(next.IsVisible, $"Next-step control visibility: {next.Visibility}, layout={vm.HasAuthoredLayout}");
                Assert.True(next.IsEnabled, $"Next-step command={next.Command?.CanExecute(null)}, editable={vm.IsSceneEditable}");
                Assert.Equal(OpenVisionLanguageService.T("Equipment.StationUnitAddButton"), next.Content);
                Assert.Same(vm.OpenEquipmentComponentLibraryDialogCommand, next.Command);
                Assert.Same(next, Keyboard.Focus(next));
                Capture("empty");

                Invoke(next);
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                Assert.True(vm.IsStationUnitEditorOpen);
                Invoke(CommandButton(vm.CancelStationUnitEditorCommand));
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                Assert.False(vm.IsStationUnitEditorOpen || vm.HasUnsavedChanges);

                Invoke(next);
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                var unitName = Descendants(window).OfType<TextBox>().Single(box => box.Name == "StationUnitNameTextBox");
                unitName.Text = "Inspection transfer and positioning unit";
                PumpBindings();
                Assert.Equal(unitName.Text, vm.UnitNameDraft);
                Capture("unit-dialog");
                Invoke(CommandButton(vm.AddStationUnitCommand));
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                Assert.False(vm.IsStationUnitEditorOpen);
                Assert.NotNull(vm.Layout.ActiveUnitId);
                Assert.Equal(OpenVisionLanguageService.T("Equipment.AddPartButton"), next.Content);
                Assert.True(vm.HasUnsavedChanges);
                Capture("unit-ready");

                Invoke(next);
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                Assert.True(vm.IsEquipmentComponentLibraryDialogOpen);
                Capture("part-dialog");
                var addFrame = Descendants(window).OfType<Button>().Single(button =>
                    ReferenceEquals(button.Command, vm.AddEquipmentComponentFromLibraryCommand)
                    && button.CommandParameter is OpenVisionLab.MachineStudio.Model.ComponentLibraryItem { Kind: LayoutComponentKind.MachineFrame });
                Invoke(addFrame);
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                Assert.False(vm.IsEquipmentComponentLibraryDialogOpen || next.IsVisible);
                var frame = Assert.Single(vm.Layout.Items.Where(item => item.Component is not null));
                Assert.Equal(vm.Layout.ActiveUnitId, frame.Component!.UnitId);
                Assert.True(vm.HasUnsavedChanges);
                Assert.True(vm.IsDesignMode);
                Assert.False(vm.IsRunning);
                Capture("part-added");

                Button CommandButton(System.Windows.Input.ICommand command) => Descendants(window).OfType<Button>()
                    .Single(button => button.IsVisible && ReferenceEquals(button.Command, command));
                static void Invoke(Button button) => Assert.IsAssignableFrom<IInvokeProvider>(new ButtonAutomationPeer(button)
                    .GetPattern(PatternInterface.Invoke)).Invoke();
                void Capture(string state)
                {
                    CaptureWindow(window, $"recipe-entry-{width:0}x{height:0}-{state}");
                }
            }
            finally
            {
                window.DataContext = null;
                window.Close();
            }
            return true;
        });
    }

    [Theory]
    [InlineData(1280, 760)]
    [InlineData(1920, 1040)]
    public async Task ManualEntryAppliesDraftMovesAxisAndReturnsToSavedAuthoringThroughRenderedControls(double width, double height)
    {
        await _ui.InvokeTaskAsync(async () =>
        {
            var originalLanguage = OpenVisionLanguageService.CurrentLanguage;
            OpenVisionLanguageService.SetLanguage(OpenVisionLanguage.Korean, save: false);
            using var vm = new MainViewModel(new MachineProjectDocument { Name = "Manual entry review" });
            var window = new ShellWindow { DataContext = vm, Width = width, MinWidth = width, Height = height, ShowActivated = false, ShowInTaskbar = false };
            window.Show();
            try
            {
                Assert.True(await vm.CreateNewProjectAsync("Manual positioning recipe"));
                vm.OpenStationUnitEditorCommand.Execute(null);
                vm.StationNameDraft = "Inspection station";
                vm.UnitNameDraft = "Transfer and positioning unit";
                vm.AddStationUnitCommand.Execute(null);
                vm.AddEquipmentLayoutComponentCommand.Execute(LayoutComponentKind.LinearStage);
                var stage = Assert.Single(vm.Layout.Items.Where(item => item.Component?.Kind == LayoutComponentKind.LinearStage)).Component!;
                vm.Layout.Select(stage.Id);
                vm.Navigation.IsInspectorOpen = true;
                await IdleAsync();
                var inspector = Descendants(window).OfType<OpenVisionLab.MachineStudio.View.Inspector.RightToolRegionView>().Single();
                var manualTab = Descendants(inspector).OfType<Button>().Single(button => ReferenceEquals(button.Command, vm.OpenEquipmentSinglePartCheckCommand));
                Assert.True(manualTab.IsVisible && manualTab.IsEnabled);
                Capture("design-before");

                var editor = Assert.IsType<LayoutComponentEditorViewModel>(vm.Layout.SelectedComponentEditor);
                var originalX = stage.Transform.X;
                editor.DraftXText = "25";
                vm.PlacementDraftPrompt = () => PlacementDraftDecision.Cancel;
                Invoke(manualTab);
                await WaitAsync(() => !vm.ManualEquipment.IsStartingManualControl);
                Assert.True(editor.HasPendingPlacementDraft && vm.IsSceneEditable && vm.IsDesignMode);
                Assert.False(vm.IsRunning);
                Assert.Equal(originalX, stage.Transform.X);
                Capture("entry-cancelled");

                vm.PlacementDraftPrompt = () => PlacementDraftDecision.Apply;
                Invoke(manualTab);
                await WaitAsync(() => vm.IsEquipmentSinglePartCheckOpen && !editor.HasPendingPlacementDraft && vm.CanStartManualEquipmentControl);
                var start = Descendants(window).OfType<Button>().Single(button => button.Name == "EnterManualCommissioningButton");
                Assert.True(start.IsVisible && start.IsEnabled);
                Assert.Same(vm.ManualEquipment.StartManualEquipmentControlCommand, start.Command);
                Assert.Equal(OpenVisionLanguageService.T("Equipment.EnterManualCommissioning", "수동 시운전 시작", "Start manual commissioning"), start.Content);
                Invoke(start);
                await WaitAsync(() => !vm.ManualEquipment.IsStartingManualControl && vm.IsRunning && vm.IsRunMode);
                await WaitAsync(() => vm.SceneSnapshots.Latest?.ControlOwner == SimulationControlOwner.Manual);
                Assert.Equal(25, stage.Transform.X);
                Assert.False(editor.HasPendingPlacementDraft || vm.IsSceneEditable);
                Assert.True(vm.IsEquipmentSinglePartCheckOpen && vm.Navigation.IsInspectorOpen && vm.HasUnsavedChanges);
                var snapshot = vm.SceneSnapshots.Latest!;
                Assert.False(snapshot.AutomaticRun.IsActive);
                Assert.DoesNotContain(snapshot.Sequences, sequence => sequence.Status == SequenceExecutionStatus.Running);
                Assert.Equal(0, Assert.Single(snapshot.Axes).Position, 3);
                Capture("manual-started");

                var target = Descendants(window).OfType<TextBox>().Single(box => box.Name == "AxisTargetPositionTextBox");
                var move = Descendants(window).OfType<Button>().Single(button => button.Name == "MoveAxisAbsoluteButton");
                var inspectorScroll = Descendants(window).OfType<ScrollViewer>().Single(scroll => scroll.Name == "RunInspectorScrollViewer");
                var diagnostics = Descendants(window).OfType<Expander>().Single(expander => expander.Name == "AxisDriveDiagnostics");
                var advancedMotion = Descendants(window).OfType<Expander>().Single(expander => expander.Name == "AxisAdvancedMotionControls");
                Assert.False(diagnostics.IsExpanded || advancedMotion.IsExpanded);
                Assert.Equal(0, inspectorScroll.VerticalOffset);
                AssertInsideInspector(target);
                AssertInsideInspector(move);
                AssertInsideInspector(Descendants(window).OfType<Button>().Single(button => button.Name == "StopAxisMotionButton"));
                target.SetCurrentValue(TextBox.TextProperty, "invalid");
                await IdleAsync();
                var targetBounds = target.TransformToAncestor(window).TransformBounds(new Rect(target.RenderSize));
                Assert.True(new Rect(window.RenderSize).Contains(targetBounds));
                Assert.Equal("invalid", vm.AxisTargetPositionText);
                Assert.False(move.IsEnabled);
                Assert.True(vm.HasAxisTargetPositionError);
                AssertInsideInspector(Descendants(window).OfType<TextBlock>().Single(text => text.Name == "AxisTargetValidationText"));
                Capture("target-invalid");
                target.SetCurrentValue(TextBox.TextProperty, "10");
                await WaitAsync(() => move.IsEnabled);
                Assert.Equal("10", vm.AxisTargetPositionText);
                Invoke(move);
                await WaitAsync(() => Math.Abs(Assert.Single(vm.SceneSnapshots.Latest!.Axes).Position - 10) < 0.001);
                Assert.False(vm.HasAxisTargetPositionError);
                Descendants(window).OfType<ScrollViewer>().Single(scroll => scroll.Name == "RunInspectorScrollViewer").ScrollToTop();
                await IdleAsync();
                Capture("axis-10");

                vm.IsDesignMode = true;
                await SmokePerformanceVerifier.WaitForModeAsync(vm, window.Dispatcher, false);
                Assert.True(vm.IsSceneEditable && vm.HasUnsavedChanges);
                Assert.False(vm.IsRunning || vm.ManualEquipment.IsManualControlAdmissionLocked);
                Assert.Equal(stage.Id, vm.Layout.SelectedItem?.Id);
                Assert.Equal(25, stage.Transform.X);
                Capture("edit-return");

                var path = Path.Combine(TestStorage.RootPath, $"manual-entry-{width:0}x{height:0}", "manual-positioning.ovmachine");
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await vm.SaveProjectAsync(path);
                var store = new ProjectDocumentStore();
                var saved = store.Load(File.ReadAllText(path));
                Assert.False(vm.HasUnsavedChanges);
                Assert.Equal(25, Assert.Single(saved.Layouts.SelectMany(layout => layout.Components)).Transform.X);
                Assert.True(await vm.OpenProjectAsync(path));
                Assert.True(vm.IsDesignMode && vm.IsSceneEditable);
                Assert.False(vm.IsRunning || vm.HasUnsavedChanges);
                Assert.Equal(stage.Id, Assert.Single(vm.Layout.Items.Where(item => item.Component is not null)).Id);

                void AssertInsideInspector(FrameworkElement element)
                {
                    var bounds = element.TransformToAncestor(inspectorScroll).TransformBounds(new Rect(element.RenderSize));
                    Assert.True(element.IsVisible && bounds.Top >= -0.5 && bounds.Bottom <= inspectorScroll.ViewportHeight + 0.5,
                        $"{element.Name}: {bounds}, viewport height {inspectorScroll.ViewportHeight}");
                }
            }
            finally
            {
                window.DataContext = null;
                window.Close();
                OpenVisionLanguageService.SetLanguage(originalLanguage, save: false);
            }
            return true;

            async Task IdleAsync()
            {
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                window.UpdateLayout();
            }
            async Task WaitAsync(Func<bool> condition)
            {
                var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
                while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(20);
                Assert.True(condition(), "The requested manual commissioning state did not become observable.");
                await IdleAsync();
            }
            static void Invoke(Button button) => Assert.IsAssignableFrom<IInvokeProvider>(new ButtonAutomationPeer(button)
                .GetPattern(PatternInterface.Invoke)).Invoke();
            void Capture(string state) => CaptureWindow(window, $"manual-entry-{width:0}x{height:0}-{state}");
        });
    }

    private static void CaptureWindow(Window window, string name)
    {
        var evidenceDirectory = Environment.GetEnvironmentVariable("OPENVISIONLAB_MACHINE_STUDIO_RECIPE_ENTRY_EVIDENCE");
        if (string.IsNullOrWhiteSpace(evidenceDirectory)) return;
        Directory.CreateDirectory(evidenceDirectory);
        window.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(evidenceDirectory, $"{name}.png"));
        encoder.Save(stream);
    }

    [Theory]
    [InlineData(1674)]
    [InlineData(1034)]
    public async Task MaterialSettingsButtonsMatchCommandsAcrossModeChangesAndRecoverWithoutMutation(double width)
    {
        await _ui.InvokeTaskAsync(async () =>
        {
            var project = new MachineProjectDocument
            {
                Stations = [new() { Id = "station", Name = "Station", Units = [new() { Id = "unit", Name = "Inspection" }] }]
            };
            var store = new ProjectDocumentStore();
            string AuthoredState() => store.SerializeForSave(project, DateTimeOffset.UnixEpoch);
            var saved = AuthoredState();
            using var vm = new MainViewModel(project);
            var view = new SceneDocumentView { DataContext = vm };
            var window = new Window { Content = view, Width = width, Height = 760, WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false };
            window.Show();
            try
            {
                PumpBindings();
                var open = Assert.IsType<MenuItem>(view.FindName("EquipmentMaterialSettingsButton"));
                Assert.Same(vm.MaterialFlowSettings.OpenCommand, open.Command);
                var openProvider = Assert.IsAssignableFrom<IInvokeProvider>(new MenuItemAutomationPeer(open).GetPattern(PatternInterface.Invoke));
                Assert.True(open.IsEnabled);
                openProvider.Invoke();
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                var settings = Assert.Single(Descendants(view).OfType<MaterialFlowSettingsView>());
                var apply = Descendants(settings).OfType<Button>().Single(button => ReferenceEquals(button.Command, vm.MaterialFlowSettings.ApplyCommand));
                Assert.True(vm.MaterialFlowSettings.IsOpen && apply.IsEnabled);
                vm.MaterialFlowSettings.WorkpieceName = "Unapplied draft";

                vm.IsRunMode = true;
                await SmokePerformanceVerifier.WaitForModeAsync(vm, window.Dispatcher, true);
                Assert.False(open.IsEnabled);
                Assert.False(apply.IsEnabled);
                Assert.Equal(vm.MaterialFlowSettings.OpenCommand.CanExecute(null), open.IsEnabled);
                Assert.Equal(vm.MaterialFlowSettings.ApplyCommand.CanExecute(null), apply.IsEnabled);
                Assert.Throws<System.Windows.Automation.ElementNotEnabledException>(() => openProvider.Invoke());
                Assert.Equal(saved, AuthoredState());

                vm.IsRunMode = false;
                await SmokePerformanceVerifier.WaitForModeAsync(vm, window.Dispatcher, false);
                Assert.True(open.IsEnabled && apply.IsEnabled);
                var cancel = Descendants(settings).OfType<Button>().Single(button => ReferenceEquals(button.Command, vm.MaterialFlowSettings.CancelCommand));
                Assert.IsAssignableFrom<IInvokeProvider>(new ButtonAutomationPeer(cancel).GetPattern(PatternInterface.Invoke)).Invoke();
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                Assert.False(vm.MaterialFlowSettings.IsOpen);
                Assert.False(vm.MaterialFlowSettings.ApplyCommand.CanExecute(null));
                openProvider.Invoke();
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                Assert.Equal(project.MaterialFlow.WorkpieceName, vm.MaterialFlowSettings.WorkpieceName);
                Assert.Equal(saved, AuthoredState());
                Assert.False(vm.HasUnsavedChanges || vm.IsRunning);
            }
            finally { window.Close(); }
        });
    }

    [Theory]
    [InlineData(1674)]
    [InlineData(1034)]
    [InlineData(794)]
    public async Task MaterialSettingsBindingsRejectInvalidRepeatAndReopenSavedValuesWithoutRunning(double width)
    {
        await _ui.InvokeAsync(() =>
        {
            var project = new MachineProjectDocument
            {
                Stations = [new() { Id = "station", Name = "Station", Units = [new() { Id = "unit", Name = "Inspection Unit" }] }]
            };
            using var vm = new MainViewModel(project);
            var view = new SceneDocumentView { DataContext = vm };
            var window = new Window { Content = view, Width = width, Height = 538, WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false };
            window.Show();
            try
            {
                vm.MaterialFlowSettings.OpenCommand.Execute(null);
                window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(window.UpdateLayout));
                var settings = Assert.Single(Descendants(view).OfType<MaterialFlowSettingsView>());
                var controls = Descendants(settings).ToArray();
                var name = controls.OfType<TextBox>().Single(box => box.Name == "MaterialWorkpieceNameTextBox");
                var repeat = controls.OfType<TextBox>().Single(box => box.GetBindingExpression(TextBox.TextProperty)?.ParentBinding.Path?.Path == nameof(MaterialFlowSettingsViewModel.RepeatCount));
                var apply = controls.OfType<Button>().Single(button => ReferenceEquals(button.Command, vm.MaterialFlowSettings.ApplyCommand));
                name.SetCurrentValue(TextBox.TextProperty, new string('A', 60));
                repeat.SetCurrentValue(TextBox.TextProperty, "0");
                PumpBindings();
                Assert.Equal(name.Text, vm.MaterialFlowSettings.WorkpieceName);
                Assert.Equal("0", vm.MaterialFlowSettings.RepeatCount);
                var provider = (IInvokeProvider)new ButtonAutomationPeer(apply).GetPattern(PatternInterface.Invoke);
                provider.Invoke();
                window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(window.UpdateLayout));
                Assert.True(vm.MaterialFlowSettings.IsOpen);
                Assert.Contains("1–100", vm.MaterialFlowSettings.ValidationText);
                Assert.False(vm.HasUnsavedChanges);
                var feedback = controls.OfType<TextBlock>().Single(text => text.GetBindingExpression(TextBlock.TextProperty)?.ParentBinding.Path?.Path == nameof(MaterialFlowSettingsViewModel.ValidationText));
                var feedbackBounds = feedback.TransformToAncestor(settings).TransformBounds(new Rect(feedback.RenderSize));
                var applyBounds = apply.TransformToAncestor(settings).TransformBounds(new Rect(apply.RenderSize));
                Assert.True(feedback.IsVisible && feedbackBounds.Bottom <= applyBounds.Top, "Validation feedback must remain visible above Apply in the native workspace height.");
                var bounds = new Rect(view.RenderSize);
                foreach (var button in controls.OfType<Button>().Where(button => button.IsVisible))
                    Assert.True(bounds.Contains(button.TransformToAncestor(view).TransformBounds(new Rect(button.RenderSize))));
                repeat.SetCurrentValue(TextBox.TextProperty, "3");
                PumpBindings();
                provider.Invoke();
                window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(window.UpdateLayout));
                Assert.False(vm.MaterialFlowSettings.IsOpen);
                Assert.True(vm.HasUnsavedChanges);
                Assert.False(vm.IsRunning);
                var store = new ProjectDocumentStore();
                var restored = store.Load(store.Save(project));
                Assert.Equal(new string('A', 60), restored.MaterialFlow.WorkpieceName);
                Assert.Equal(3, restored.Stations[0].Units[0].RepeatCount);
                vm.MaterialFlowSettings.OpenCommand.Execute(null);
                PumpBindings();
                Assert.Equal("3", repeat.Text);
                Assert.Equal(restored.MaterialFlow.WorkpieceName, name.Text);
                vm.MaterialFlowSettings.CancelCommand.Execute(null);
                Assert.False(vm.IsRunning);
                return true;
            }
            finally { window.Close(); }
        });
    }

    [Theory]
    [InlineData(1674, OpenVisionLanguage.Korean)]
    [InlineData(1034, OpenVisionLanguage.Korean)]
    [InlineData(1674, OpenVisionLanguage.English)]
    [InlineData(1034, OpenVisionLanguage.English)]
    public async Task MaterialSettingsLocalizeErrorsAndKeepDraftsAcrossFileReopen(double width, OpenVisionLanguage language)
    {
        await _ui.InvokeTaskAsync(async () =>
        {
            var previousLanguage = OpenVisionLanguageService.CurrentLanguage;
            OpenVisionLanguageService.SetLanguage(language, save: false);
            string unitName = new('U', 80), productName = new('P', 60), slotName = new('S', 60);
            var project = new MachineProjectDocument
            {
                Stations = [new() { Id = "station", Name = "Station", Units = [new() { Id = "unit", Name = unitName }, new() { Id = "output", Name = "Output" }] }]
            };
            using var vm = new MainViewModel(project);
            var view = new SceneDocumentView { DataContext = vm };
            var window = new Window { Content = view, Width = width, Height = 538, WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false };
            window.Show();
            try
            {
                PumpBindings();
                var open = Assert.IsType<MenuItem>(view.FindName("EquipmentMaterialSettingsButton"));
                Assert.Same(vm.MaterialFlowSettings.OpenCommand, open.Command);
                Assert.Equal(OpenVisionLanguageService.T("MaterialFlow.SettingsTitle"), open.Header);
                Assert.IsAssignableFrom<IInvokeProvider>(new MenuItemAutomationPeer(open).GetPattern(PatternInterface.Invoke)).Invoke();
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                var settings = Assert.Single(Descendants(view).OfType<MaterialFlowSettingsView>());
                var controls = Descendants(settings).ToArray();
                var name = controls.OfType<TextBox>().Single(box => box.Name == "MaterialWorkpieceNameTextBox");
                var slot = controls.OfType<TextBox>().Single(box => box.GetBindingExpression(TextBox.TextProperty)?.ParentBinding.Path?.Path == nameof(MaterialFlowSettingsViewModel.SlotName));
                var repeat = controls.OfType<TextBox>().Single(box => box.GetBindingExpression(TextBox.TextProperty)?.ParentBinding.Path?.Path == nameof(MaterialFlowSettingsViewModel.RepeatCount));
                var combo = controls.OfType<ComboBox>().Single();
                var apply = controls.OfType<Button>().Single(button => ReferenceEquals(button.Command, vm.MaterialFlowSettings.ApplyCommand));
                Assert.Equal(OpenVisionLanguageService.T("MaterialFlow.ProductName"), System.Windows.Automation.AutomationProperties.GetName(name));
                name.SetCurrentValue(TextBox.TextProperty, productName);
                slot.SetCurrentValue(TextBox.TextProperty, slotName);
                repeat.SetCurrentValue(TextBox.TextProperty, "0");
                PumpBindings();
                Assert.IsAssignableFrom<IInvokeProvider>(new ButtonAutomationPeer(apply).GetPattern(PatternInterface.Invoke)).Invoke();
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                var feedback = controls.OfType<TextBlock>().Single(text => text.GetBindingExpression(TextBlock.TextProperty)?.ParentBinding.Path?.Path == nameof(MaterialFlowSettingsViewModel.ValidationText));
                Assert.Equal(OpenVisionLanguageService.T("MaterialFlow.InvalidSettings"), feedback.Text);
                Assert.False(vm.HasUnsavedChanges || vm.IsRunning);
                Assert.Null(project.Simulation.AutomaticRun);
                await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ContextIdle);
                CaptureWindow(window, $"material-{width}-{language}-long-error");
                var selectedText = Descendants(combo).OfType<TextBlock>().Single(text => text.Text == unitName);
                var textBounds = selectedText.TransformToAncestor(combo).TransformBounds(new Rect(selectedText.RenderSize));
                Assert.True(new Rect(combo.RenderSize).Contains(textBounds), $"Selected unit text {textBounds} must fit inside {combo.RenderSize}.");
                var glyphs = new FormattedText(selectedText.Text, System.Globalization.CultureInfo.CurrentCulture,
                    selectedText.FlowDirection, new Typeface(selectedText.FontFamily, selectedText.FontStyle, selectedText.FontWeight, selectedText.FontStretch),
                    selectedText.FontSize, selectedText.Foreground, VisualTreeHelper.GetDpi(selectedText).PixelsPerDip);
                if (selectedText.TextWrapping != TextWrapping.NoWrap) glyphs.MaxTextWidth = selectedText.ActualWidth;
                Assert.True(glyphs.WidthIncludingTrailingWhitespace <= selectedText.ActualWidth + 1 && glyphs.Height <= selectedText.ActualHeight + 1,
                    $"Selected unit glyphs {glyphs.WidthIncludingTrailingWhitespace}x{glyphs.Height} exceed {selectedText.RenderSize}.");
                var feedbackBounds = feedback.TransformToAncestor(settings).TransformBounds(new Rect(feedback.RenderSize));
                var applyBounds = apply.TransformToAncestor(settings).TransformBounds(new Rect(apply.RenderSize));
                Assert.True(feedback.IsVisible && feedbackBounds.Bottom <= applyBounds.Top);
                var dialogBorder = controls.OfType<Border>().Single(border => border.Child is DockPanel);
                Assert.True(new Rect(settings.RenderSize).Contains(dialogBorder.TransformToAncestor(settings).TransformBounds(new Rect(dialogBorder.RenderSize))), "The settings panel must fit in its container.");
                var dialogBounds = dialogBorder.TransformToAncestor(window).TransformBounds(new Rect(dialogBorder.RenderSize));
                Assert.True(new Rect(window.RenderSize).Contains(dialogBounds), $"Settings bounds {dialogBounds} exceed actual window {window.RenderSize}.");
                foreach (var button in controls.OfType<Button>().Where(button => button.IsVisible))
                    Assert.True(new Rect(view.RenderSize).Contains(button.TransformToAncestor(view).TransformBounds(new Rect(button.RenderSize))));

                combo.IsDropDownOpen = true;
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                var item = Assert.IsType<ComboBoxItem>(combo.ItemContainerGenerator.ContainerFromIndex(0));
                Assert.True(item.IsVisible);
                Assert.Equal(unitName, ((MachineUnitDefinition)item.Content).Name);
                CaptureWindow(window, $"material-{width}-{language}-unit-popup");
                var popupText = Descendants(item).OfType<TextBlock>().Single(text => text.Text == unitName);
                Assert.True(new Rect(item.RenderSize).Contains(popupText.TransformToAncestor(item).TransformBounds(new Rect(popupText.RenderSize))), "The long unit name must fit in its popup item.");
                combo.IsDropDownOpen = false;
                var otherLanguage = language == OpenVisionLanguage.Korean ? OpenVisionLanguage.English : OpenVisionLanguage.Korean;
                OpenVisionLanguageService.SetLanguage(otherLanguage, save: false);
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                Assert.Equal(OpenVisionLanguageService.T("MaterialFlow.InvalidSettings"), feedback.Text);
                Assert.Equal(productName, name.Text);
                Assert.Equal(slotName, slot.Text);
                Assert.False(vm.HasUnsavedChanges || vm.IsRunning);
                CaptureWindow(window, $"material-{width}-{language}-language-recovery");
                repeat.SetCurrentValue(TextBox.TextProperty, "3");
                PumpBindings();
                Assert.IsAssignableFrom<IInvokeProvider>(new ButtonAutomationPeer(apply).GetPattern(PatternInterface.Invoke)).Invoke();
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                Assert.False(vm.MaterialFlowSettings.IsOpen || vm.IsRunning);
                Assert.True(vm.HasUnsavedChanges);
                var fileStore = new ProjectDocumentFileStore();
                var directory = Path.Combine(TestStorage.RootPath, $"material-{width}-{language}");
                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, "material-settings.ovmachine");
                await fileStore.SaveAsync(project, path);
                var restored = await fileStore.LoadAsync(path);
                var savedBytes = await File.ReadAllBytesAsync(path);
                Assert.Equal(productName, restored.MaterialFlow.WorkpieceName);
                Assert.Equal(slotName, restored.Stations[0].Units[0].SlotName);
                Assert.All(restored.Stations[0].Units, unit => Assert.Equal(3, unit.RepeatCount));
                Assert.Null(restored.Simulation.AutomaticRun);
                var fresh = new MaterialFlowSettingsViewModel(() => restored, () => "unit", () => true, () => throw new InvalidOperationException("Cancel must not apply."));
                fresh.OpenCommand.Execute(null);
                window.Content = new MaterialFlowSettingsView { DataContext = fresh };
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                Assert.Equal(productName, fresh.WorkpieceName);
                Assert.Equal(slotName, fresh.SlotName);
                Assert.Equal("3", fresh.RepeatCount);
                fresh.ResetCommand.Execute(null);
                fresh.CancelCommand.Execute(null);
                fresh.OpenCommand.Execute(null);
                Assert.Equal(productName, fresh.WorkpieceName);
                Assert.Equal(slotName, fresh.SlotName);
                Assert.Equal(savedBytes, await File.ReadAllBytesAsync(path));
                Assert.Null(restored.Simulation.AutomaticRun);
                CaptureWindow(window, $"material-{width}-{language}-file-reopened");
                return true;
            }
            finally
            {
                window.Close();
                OpenVisionLanguageService.SetLanguage(previousLanguage, save: false);
            }
        });
    }

    [Theory]
    [InlineData(1674)]
    [InlineData(1034)]
    [InlineData(794)]
    [InlineData(665)]
    public async Task EquipmentToolbarSeparatesAuthoringAndViewControlsWithoutClipping(double width)
    {
        await _ui.InvokeAsync(() =>
        {
            var project = new ProjectDocumentStore().Load(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Samples", "R19InspectionFlow.ovmachine")));
            using var model = new MainViewModel(project);
            var navigation = model.Navigation;
            navigation.SelectedDocumentTabIndex = 0;
            var view = new SceneDocumentView { DataContext = model };
            var window = new Window
            {
                Width = width, Height = 760, WindowStyle = WindowStyle.None,
                ShowInTaskbar = false, ShowActivated = false, Content = view
            };
            window.Show();
            try
            {
                window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(window.UpdateLayout));
                navigation.IsCompactLayout = width < 1100;
                window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(window.UpdateLayout));
                var heading = Assert.IsType<StackPanel>(view.FindName("EquipmentSceneHeading"));
                var viewing = Assert.IsType<WrapPanel>(view.FindName("EquipmentViewToolbar"));
                var headingBounds = heading.TransformToAncestor(view).TransformBounds(new Rect(heading.RenderSize));
                var viewingBounds = viewing.TransformToAncestor(view).TransformBounds(new Rect(viewing.RenderSize));
                Assert.True(heading.IsVisible && viewing.IsVisible);
                Assert.True(!headingBounds.IntersectsWith(viewingBounds), $"Heading {headingBounds}; toolbar {viewingBounds}; Compact {navigation.IsCompactLayout}");
                var viewport = Assert.IsType<MachineSceneViewport>(view.FindName("SceneViewport"));
                var viewportBounds = viewport.TransformToAncestor(view).TransformBounds(new Rect(viewport.RenderSize));
                Assert.Equal(viewport.ActualWidth * 0.6, heading.MaxWidth, precision: 3);
                var hint = Assert.IsType<TextBlock>(view.FindName("EquipmentSceneHint"));
                Assert.Equal("#FF9FB6C3", hint.Foreground.ToString());
                Assert.True(viewportBounds.Contains(headingBounds));
                Assert.True(viewportBounds.Contains(viewingBounds));
                Assert.Equal(5, viewing.Children.Count);
                Assert.All(viewing.Children.Cast<FrameworkElement>(), control => Assert.Equal(24, control.ActualHeight));
                var group = Assert.IsType<Border>(view.FindName("EquipmentViewToolbarGroup"));
                Assert.Equal(new Thickness(1), group.BorderThickness);
                var header = Assert.IsType<Border>(view.FindName("EquipmentSceneHeader"));
                var headerBounds = header.TransformToAncestor(view).TransformBounds(new Rect(header.RenderSize));
                Assert.True(headerBounds.Contains(headingBounds) && headerBounds.Contains(viewingBounds));
                Assert.Equal(headingBounds.Top, viewingBounds.Top - group.Padding.Top - group.BorderThickness.Top, precision: 1);
                Assert.Null(header.Background);
                Assert.Equal(new Thickness(0), header.BorderThickness);
                Assert.Same(viewport, view.InputHitTest(new Point(headingBounds.Left + 5, headingBounds.Top + 5)));
                Assert.Same(viewport, view.InputHitTest(new Point(headerBounds.Left + 2, headerBounds.Top + 2)));
                Assert.Equal(14, Assert.IsType<TextBlock>(view.FindName("EquipmentSceneTitle")).FontSize);
                Assert.False(Assert.IsType<Border>(view.FindName("SceneIdentityCard")).IsVisible);
                var visibleBounds = new Rect(view.RenderSize);
                foreach (var control in viewing.Children.Cast<FrameworkElement>())
                    Assert.True(visibleBounds.Contains(control.TransformToAncestor(view).TransformBounds(new Rect(control.RenderSize))));
                return true;
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public async Task SceneHitAndMultiSelectionKeepPrimaryComponentInProjectTree()
    {
        var state = await _ui.InvokeAsync(() =>
        {
            var first = new LayoutComponentDefinition
            {
                Id = "frame-01", Name = "Machine Frame 01", Kind = LayoutComponentKind.MachineFrame,
                Transform = new Transform2D { X = 35, Y = 24 },
                Size = new Size2D { Width = 36, Height = 20 }
            };
            var second = new LayoutComponentDefinition
            {
                Id = "frame-02", Name = "Machine Frame 02", Kind = LayoutComponentKind.MachineFrame,
                Transform = new Transform2D { X = 130, Y = 72 },
                Size = new Size2D { Width = 64, Height = 16 }
            };
            var definition = new MachineLayoutDefinition
            {
                Id = "layout-main", Name = "Main", Components = [first, second]
            };
            var project = new MachineProjectDocument
            {
                Name = "P09 scene-tree selection",
                Layouts = [definition]
            };
            project.Simulation.ActiveLayoutId = definition.Id;

            using var viewModel = new MainViewModel(project);
            var serializedBeforeSelection = new ProjectDocumentStore().Serialize(project);
            var firstNode = viewModel.ProjectTree.Roots.Single().Children
                .Single(node => node.Kind == TreeNodeKind.Layouts).Children.Single()
                .Children.Single(node => node.Id == first.Id);
            var view = new SceneDocumentView
            {
                DataContext = new SceneDataContext(viewModel.Layout, viewModel.Navigation, IsSceneEditable: true)
            };
            var window = new Window
            {
                Width = 1000,
                Height = 760,
                ShowInTaskbar = false,
                ShowActivated = false,
                WindowStyle = WindowStyle.None,
                Content = view
            };
            window.Show();
            try
            {
                window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(window.UpdateLayout));
                var viewport = Assert.Single(Descendants(view).OfType<MachineSceneViewport>());
                var bounds = viewport.GetItemScreenBounds(first.Id);
                if (bounds is null)
                {
                    throw new InvalidOperationException("The SceneDocumentView did not project the primary component.");
                }

                var center = new Point(bounds.Value.Left + (bounds.Value.Width / 2), bounds.Value.Top + (bounds.Value.Height / 2));
                var hit = viewport.SelectItemAt(center);
                PumpBindings();
                var hitSynchronizesTree = hit && ReferenceEquals(firstNode, viewModel.ProjectTree.SelectedNode)
                    && ReferenceEquals(first, viewModel.ProjectTree.SelectedNode?.Model)
                    && viewModel.Layout.SelectionCount == 1;

                viewModel.Layout.SelectMany([first.Id, second.Id], first.Id);
                PumpBindings();
                var groupPreservesPrimaryInTree = viewModel.Layout.SelectionCount == 2
                    && viewModel.Layout.SelectedItem?.Id == first.Id
                    && ReferenceEquals(firstNode, viewModel.ProjectTree.SelectedNode)
                    && viewModel.Layout.SelectedItems.Select(item => item.Id).SequenceEqual([first.Id, second.Id]);

                return (hitSynchronizesTree, groupPreservesPrimaryInTree,
                    ProjectUnchanged: serializedBeforeSelection == new ProjectDocumentStore().Serialize(project),
                    viewModel.HasUnsavedChanges);
            }
            finally
            {
                window.Close();
            }
        });

        Assert.True(state.hitSynchronizesTree);
        Assert.True(state.groupPreservesPrimaryInTree);
        Assert.True(state.ProjectUnchanged);
        Assert.False(state.HasUnsavedChanges);
    }

    [Fact]
    public async Task EquipmentViewSelectorsPreserveSelectionAndSessionPreferenceAcrossProjectReload()
    {
        var state = await _ui.InvokeAsync(() =>
        {
            using var layout = CreateLayout(out var project);
            using var navigation = new ShellNavigationViewModel(false, () => false, () => false,
                () => Task.CompletedTask, () => { }, _ => { });
            navigation.SelectedDocumentTabIndex = 0;

            var view = new SceneDocumentView
            {
                DataContext = new SceneDataContext(layout, navigation, IsSceneEditable: true)
            };
            var window = new Window
            {
                Width = 1000,
                Height = 760,
                ShowInTaskbar = false,
                ShowActivated = false,
                WindowStyle = WindowStyle.None,
                Content = view
            };
            window.Show();
            try
            {
                window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(window.UpdateLayout));
                var controls = Descendants(view).ToArray();
                var oblique = controls.OfType<RadioButton>().Single(button =>
                    ReferenceEquals(button.Command, layout.ShowObliqueViewCommand));
                var plan = controls.OfType<RadioButton>().Single(button =>
                    ReferenceEquals(button.Command, layout.ShowTopViewCommand));
                var viewport = Assert.Single(controls.OfType<MachineSceneViewport>());
                var item = layout.Items.Single(candidate => candidate.Id == "camera-01");
                var bounds = viewport.GetItemScreenBounds(item.Id);
                if (bounds is null)
                {
                    throw new InvalidOperationException("The oblique SceneDocumentView did not project the camera component.");
                }

                var center = new Point(bounds.Value.Left + (bounds.Value.Width / 2), bounds.Value.Top + (bounds.Value.Height / 2));
                var hit = viewport.SelectItemAt(center);
                PumpBindings();
                var selectionBound = hit && ReferenceEquals(item, viewport.SelectedItem)
                    && ReferenceEquals(item, layout.SelectedItem)
                    && layout.SelectedComponentEditor is LayoutComponentEditorViewModel;
                var selectedItem = layout.SelectedItem;
                var selectedEditor = layout.SelectedComponentEditor;
                var initial = (oblique.IsChecked == true, plan.IsChecked == true, viewport.IsObliqueView);
                var obliqueBinding = oblique.GetBindingExpression(ToggleButton.IsCheckedProperty);
                var diagnostics = $"model={layout.IsObliqueView}; oblique={oblique.IsChecked}; " +
                    $"binding={obliqueBinding?.Status}; path={obliqueBinding?.ParentBinding.Path.Path}; " +
                    $"source={obliqueBinding?.DataItem?.GetType().FullName}; dataContext={oblique.DataContext?.GetType().FullName}; " +
                    $"viewport={viewport.IsObliqueView}";

                PressSpace(plan, window);
                window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
                PumpBindings();
                var selectionPreservedOnPlan = ReferenceEquals(selectedItem, layout.SelectedItem)
                    && ReferenceEquals(selectedEditor, layout.SelectedComponentEditor);
                var planState = (oblique.IsChecked == true, plan.IsChecked == true, viewport.IsObliqueView);
                var planEmptyHit = viewport.SelectItemAt(new Point(0, 0));
                PumpBindings();
                var planEmptySpaceClearedSelection = !planEmptyHit && viewport.SelectedItem is null
                    && layout.SelectedItem is null && layout.SelectionCount == 0
                    && layout.SelectedComponentEditor is null;
                var planBounds = viewport.GetItemScreenBounds(item.Id);
                var planHit = false;
                if (planBounds is { } boundsInPlan)
                {
                    var planCenter = new Point(boundsInPlan.Left + (boundsInPlan.Width / 2),
                        boundsInPlan.Top + (boundsInPlan.Height / 2));
                    planHit = viewport.SelectItemAt(planCenter);
                }
                PumpBindings();
                var planHitUpdatesSelection = planHit && ReferenceEquals(item, viewport.SelectedItem)
                    && ReferenceEquals(item, layout.SelectedItem)
                    && layout.SelectedComponentEditor is LayoutComponentEditorViewModel;
                var selectedInPlan = layout.SelectedItem;
                var editorInPlan = layout.SelectedComponentEditor;

                PressSpace(oblique, window);
                window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
                PumpBindings();
                var obliqueState = (oblique.IsChecked == true, plan.IsChecked == true, viewport.IsObliqueView);
                var selectionPreservedOnOblique = ReferenceEquals(selectedInPlan, layout.SelectedItem)
                    && ReferenceEquals(editorInPlan, layout.SelectedComponentEditor);
                var emptyHit = viewport.SelectItemAt(new Point(0, 0));
                PumpBindings();
                var emptySpaceClearedSelection = !emptyHit && viewport.SelectedItem is null
                    && layout.SelectedItem is null && layout.SelectionCount == 0
                    && layout.SelectedComponentEditor is null;

                var store = new ProjectDocumentStore();
                var reopenedProject = store.Load(store.Serialize(project));
                layout.Load(reopenedProject);
                PumpBindings();
                var sameSessionRetainedPreference = layout.IsObliqueView && oblique.IsChecked == true
                    && plan.IsChecked == false && viewport.IsObliqueView;

                using var reopenedLayout = new MachineLayoutViewModel();
                reopenedLayout.Load(reopenedProject);
                view.DataContext = new SceneDataContext(reopenedLayout, navigation, IsSceneEditable: true);
                PumpBindings();
                var newSessionUsesDefault = reopenedLayout.IsObliqueView && oblique.IsChecked == true
                    && plan.IsChecked == false && viewport.IsObliqueView;

                return (initial, planState, obliqueState,
                    SelectionPreservedOnPlan: selectionPreservedOnPlan,
                    PlanEmptySpaceClearedSelection: planEmptySpaceClearedSelection,
                    PlanHitUpdatesSelection: planHitUpdatesSelection,
                    SelectionPreservedOnOblique: selectionPreservedOnOblique,
                    SelectionBound: selectionBound,
                    EmptySpaceClearedSelection: emptySpaceClearedSelection,
                    SameSessionRetainedPreference: sameSessionRetainedPreference,
                    NewSessionUsesDefault: newSessionUsesDefault,
                    Diagnostics: diagnostics);
            }
            finally
            {
                window.Close();
            }
        });

        Assert.True(state.initial == (true, false, true), state.Diagnostics);
        Assert.Equal((false, true, false), state.planState);
        Assert.Equal((true, false, true), state.obliqueState);
        Assert.True(state.SelectionPreservedOnPlan);
        Assert.True(state.PlanEmptySpaceClearedSelection);
        Assert.True(state.PlanHitUpdatesSelection);
        Assert.True(state.SelectionPreservedOnOblique);
        Assert.True(state.SelectionBound);
        Assert.True(state.EmptySpaceClearedSelection);
        Assert.True(state.SameSessionRetainedPreference);
        Assert.True(state.NewSessionUsesDefault);
    }

    [Fact]
    public async Task ProjectionRadioButtonsRespondToKeyboardSpaceAndPreserveSelection()
    {
        var state = await _ui.InvokeAsync(() =>
        {
            using var layout = CreateLayout(out _);
            using var navigation = new ShellNavigationViewModel(false, () => false, () => false,
                () => Task.CompletedTask, () => { }, _ => { });
            navigation.SelectedDocumentTabIndex = 0;
            layout.Select("camera-01");
            var selected = layout.SelectedItem;
            var selectedEditor = layout.SelectedComponentEditor;
            var view = new SceneDocumentView
            {
                DataContext = new SceneDataContext(layout, navigation, IsSceneEditable: true)
            };
            var window = new Window
            {
                Width = 1000,
                Height = 760,
                ShowInTaskbar = false,
                ShowActivated = false,
                WindowStyle = WindowStyle.None,
                Content = view
            };
            window.Show();
            try
            {
                window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(window.UpdateLayout));
                var controls = Descendants(view).ToArray();
                var oblique = controls.OfType<RadioButton>().Single(button =>
                    ReferenceEquals(button.Command, layout.ShowObliqueViewCommand));
                var plan = controls.OfType<RadioButton>().Single(button =>
                    ReferenceEquals(button.Command, layout.ShowTopViewCommand));
                var viewport = Assert.Single(controls.OfType<MachineSceneViewport>());

                PressSpace(plan, window);
                var planMode = !layout.IsObliqueView && plan.IsChecked == true
                    && oblique.IsChecked == false && !viewport.IsObliqueView;
                var selectionPreservedOnPlan = ReferenceEquals(selected, layout.SelectedItem)
                    && ReferenceEquals(selectedEditor, layout.SelectedComponentEditor);

                PressSpace(oblique, window);
                var obliqueMode = layout.IsObliqueView && oblique.IsChecked == true
                    && plan.IsChecked == false && viewport.IsObliqueView;
                var selectionPreservedOnOblique = ReferenceEquals(selected, layout.SelectedItem)
                    && ReferenceEquals(selectedEditor, layout.SelectedComponentEditor);
                return (planMode, obliqueMode, selectionPreservedOnPlan, selectionPreservedOnOblique);
            }
            finally
            {
                window.Close();
            }
        });

        Assert.True(state.planMode);
        Assert.True(state.obliqueMode);
        Assert.True(state.selectionPreservedOnPlan);
        Assert.True(state.selectionPreservedOnOblique);
    }

    [Fact]
    public async Task PlanSelectionUpdatesInspectorAndAppliesBoundDraftToSelectedComponent()
    {
        var state = await _ui.InvokeTaskAsync(async () =>
        {
            using var seedLayout = CreateLayout(out var initialProject);
            initialProject.Name = "P14 Inspector edit";
            initialProject.Devices.Add(new DeviceDefinition
            {
                Id = "camera-01",
                Name = "Inspection Camera",
                Kind = DeviceKind.Camera,
                Camera = new VirtualCameraDefinition()
            });
            initialProject.Layouts.Single().Components.Single().BehaviorBindingId = "camera-01";
            using var viewModel = new MainViewModel(initialProject);
            var project = initialProject;
            var layout = viewModel.Layout;
            viewModel.Navigation.SelectedDocumentTabIndex = 0;

            var scene = new SceneDocumentView
            {
                DataContext = viewModel
            };
            var inspector = new RightToolRegionView
            {
                DataContext = viewModel
            };
            var root = new Grid();
            root.ColumnDefinitions.Add(new ColumnDefinition());
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(340) });
            root.Children.Add(scene);
            Grid.SetColumn(inspector, 1);
            root.Children.Add(inspector);

            var window = new Window
            {
                Width = 1280,
                Height = 760,
                ShowInTaskbar = false,
                ShowActivated = false,
                WindowStyle = WindowStyle.None,
                Content = root
            };
            window.Show();
            try
            {
                window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(window.UpdateLayout));
                var viewport = Assert.Single(Descendants(scene).OfType<MachineSceneViewport>());
                layout.ShowTopViewCommand.Execute(null);
                PumpBindings();
                var item = layout.Items.Single(candidate => candidate.Id == "camera-01");
                var bounds = viewport.GetItemScreenBounds(item.Id)
                    ?? throw new InvalidOperationException("The plan view did not project camera-01.");
                var center = new Point(bounds.Left + (bounds.Width / 2), bounds.Top + (bounds.Height / 2));
                var selectedInPlan = viewport.SelectItemAt(center);
                PumpBindings();

                var editor = Assert.IsType<LayoutComponentEditorViewModel>(layout.SelectedComponentEditor);
                var x = Descendants(inspector).OfType<TextBox>().Single(textBox =>
                    textBox.GetBindingExpression(TextBox.TextProperty)?.ParentBinding.Path.Path == "DraftXText");
                var apply = Descendants(inspector).OfType<Button>().Single(button =>
                    ReferenceEquals(button.Command, editor.ApplyPlacementDraftCommand));
                var inspectorSelectionBound = ReferenceEquals(layout.SelectedItem, item)
                    && x.DataContext is LayoutComponentEditorViewModel boundEditor
                    && ReferenceEquals(boundEditor, editor)
                    && x.Text == editor.DraftXText;
                var originalX = item.Component!.Transform.X;
                var boundsBeforeApply = viewport.GetItemScreenBounds(item.Id);

                x.Text = "99";
                PumpBindings();
                var editingDraftRemainsClean = !viewModel.HasUnsavedChanges;
                var validDraftEnabled = editor.HasPendingPlacementDraft && apply.IsEnabled
                    && apply.Command!.CanExecute(apply.CommandParameter);
                var applyPeer = new ButtonAutomationPeer(apply);
                var applyProvider = Assert.IsAssignableFrom<IInvokeProvider>(applyPeer.GetPattern(PatternInterface.Invoke));
                applyProvider.Invoke();
                window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
                PumpBindings();
                var boundsAfterApply = viewport.GetItemScreenBounds(item.Id);
                var appliedDraftDisabled = !editor.HasPendingPlacementDraft && !apply.IsEnabled
                    && !apply.Command!.CanExecute(apply.CommandParameter);
                var appliedValueMarksProjectDirty = viewModel.HasUnsavedChanges
                    && viewModel.Title.EndsWith("*", StringComparison.Ordinal);

                var roundTripRoot = Path.Combine(TestStorage.RootPath, "r19-priority-14-plan-inspector", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(roundTripRoot);
                var projectPath = Path.Combine(roundTripRoot, "plan-inspector-edit.ovmachine");
                var fileStore = new ProjectDocumentFileStore();
                await viewModel.SaveProjectAsync(projectPath);
                var saveClearsDirtyMarker = !viewModel.HasUnsavedChanges
                    && !viewModel.Title.EndsWith("*", StringComparison.Ordinal);
                var reopenedProject = fileStore.Load(projectPath);
                using var reopenedViewModel = new MainViewModel(reopenedProject, projectPath);
                reopenedViewModel.Layout.Select("camera-01");
                var reopenedEditor = Assert.IsType<LayoutComponentEditorViewModel>(
                    reopenedViewModel.Layout.SelectedComponentEditor);

                return (
                    SelectedInPlan: selectedInPlan,
                    InspectorSelectionBound: inspectorSelectionBound,
                    OriginalX: originalX,
                    DraftX: editor.DraftXText,
                    UpdatedComponentX: item.Component.Transform.X,
                    SelectedItemRetained: ReferenceEquals(layout.SelectedItem, item),
                    ValidDraftEnabled: validDraftEnabled,
                    EditingDraftRemainsClean: editingDraftRemainsClean,
                    AppliedDraftDisabled: appliedDraftDisabled,
                    AppliedValueMarksProjectDirty: appliedValueMarksProjectDirty,
                    SaveClearsDirtyMarker: saveClearsDirtyMarker,
                    SelectionBoundsChanged: boundsBeforeApply != boundsAfterApply,
                    SavedProjectPath: projectPath,
                    PersistedX: reopenedViewModel.Layout.SelectedItem?.Component?.Transform.X,
                    ReopenedInspectorDraftX: reopenedEditor.DraftXText,
                    ReopenedProjectIsClean: !reopenedViewModel.HasUnsavedChanges);
            }
            finally
            {
                window.Close();
            }
        });

        Assert.True(state.SelectedInPlan);
        Assert.True(state.InspectorSelectionBound);
        Assert.Equal(35d, state.OriginalX);
        Assert.Equal("99", state.DraftX);
        Assert.Equal(99d, state.UpdatedComponentX);
        Assert.True(state.SelectedItemRetained);
        Assert.True(state.ValidDraftEnabled);
        Assert.True(state.EditingDraftRemainsClean);
        Assert.True(state.AppliedDraftDisabled);
        Assert.True(state.AppliedValueMarksProjectDirty);
        Assert.True(state.SaveClearsDirtyMarker);
        Assert.True(state.SelectionBoundsChanged);
        Assert.True(File.Exists(state.SavedProjectPath));
        Assert.Equal(99d, state.PersistedX);
        Assert.Equal("99", state.ReopenedInspectorDraftX);
        Assert.True(state.ReopenedProjectIsClean);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FocusedSceneArrowMovesUseExactLayoutUnitsAndPreserveEditingGuards(bool oblique)
    {
        await _ui.InvokeAsync(() =>
        {
            using var seed = CreateLayout(out var project);
            project.Layouts.Single().GridSize = 25;
            project.Layouts.Single().SnapToGrid = true;
            project.Devices.Add(new DeviceDefinition
            {
                Id = "camera-01", Name = "Camera", Kind = DeviceKind.Camera,
                Camera = new VirtualCameraDefinition()
            });
            project.Layouts.Single().Components.Single().BehaviorBindingId = "camera-01";
            using var viewModel = new MainViewModel(project);
            viewModel.Navigation.SelectedDocumentTabIndex = 0;
            if (oblique) viewModel.Layout.ShowObliqueViewCommand.Execute(null);
            else viewModel.Layout.ShowTopViewCommand.Execute(null);
            var scene = new SceneDocumentView { DataContext = viewModel };
            var input = new TextBox { Text = "keep draft" };
            var root = new DockPanel();
            DockPanel.SetDock(input, Dock.Top);
            root.Children.Add(input);
            root.Children.Add(scene);
            var window = new Window { Content = root, Width = 1280, Height = 760, ShowInTaskbar = false, WindowStyle = WindowStyle.None };
            try
            {
                window.Show();
                window.UpdateLayout();
                viewModel.Layout.Select("camera-01");
                PumpBindings();
                var viewport = Assert.IsType<MachineSceneViewport>(scene.FindName("SceneViewport"));
                var item = viewModel.Layout.SelectedItem!;
                Assert.Same(viewport, Keyboard.Focus(viewport));
                var source = PresentationSource.FromVisual(viewport)!;
                var right = new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.Right)
                {
                    RoutedEvent = Keyboard.KeyDownEvent
                };
                viewport.RaiseEvent(right);
                PumpBindings();
                Assert.True(right.Handled);
                Assert.Equal(36, item.CurrentX); // Initial 35 + 1, despite the 25-unit grid.
                Assert.Equal(24, item.CurrentY);
                Assert.Equal(36, item.Component!.Transform.X);
                Assert.Equal("36", viewModel.Layout.SelectedComponentEditor!.DraftXText);
                Assert.False(viewModel.Layout.SelectedComponentEditor.HasPendingPlacementDraft);
                Assert.True(viewModel.HasUnsavedChanges);
                Assert.True(viewport.RequestKeyboardMove(Key.Up, ModifierKeys.Shift));
                Assert.Equal(14, item.CurrentY);
                Assert.True(viewport.RequestKeyboardMove(Key.Left, ModifierKeys.Shift));
                Assert.Equal(26, item.CurrentX);
                Assert.True(viewport.RequestKeyboardMove(Key.Down, ModifierKeys.None));
                Assert.Equal(15, item.CurrentY);
                Assert.False(viewport.RequestKeyboardMove(Key.Right, ModifierKeys.Control));
                Assert.False(viewport.RequestKeyboardMove(Key.Down, ModifierKeys.Alt));
                Assert.False(viewport.RequestKeyboardMove(Key.Space, ModifierKeys.None));
                Assert.Equal((26d, 15d), (item.CurrentX, item.CurrentY));
                for (var index = 0; index < 4; index++) viewModel.UndoLayoutEditCommand.Execute(null);
                PumpBindings();
                item = viewModel.Layout.SelectedItem!; // Undo restores the layout items by identity.
                Assert.Equal((35d, 24d), (item.CurrentX, item.CurrentY));
                Assert.Equal("35", viewModel.Layout.SelectedComponentEditor!.DraftXText);

                Assert.Same(input, Keyboard.Focus(input));
                Assert.False(viewport.RequestKeyboardMove(Key.Right, ModifierKeys.None));
                Assert.Equal("keep draft", input.Text);
                Assert.Equal(35, item.CurrentX);
                Assert.Same(viewport, Keyboard.Focus(viewport));
                viewport.IsDesignMode = false;
                Assert.False(viewport.RequestKeyboardMove(Key.Right, ModifierKeys.None));
                Assert.Equal(35, item.CurrentX);
                viewport.IsDesignMode = true;
                viewModel.Layout.IsEditable = false;
                viewport.RequestKeyboardMove(Key.Right, ModifierKeys.None);
                Assert.Equal(35, item.CurrentX);
                viewModel.Layout.IsEditable = true;
                Assert.True(viewport.RequestKeyboardMove(Key.Right, ModifierKeys.None));
                Assert.Equal(36, item.CurrentX);
                var editor = viewModel.Layout.SelectedComponentEditor!;
                editor.DraftXText = "77";
                viewModel.PlacementDraftPrompt = () => PlacementDraftDecision.Cancel;
                viewport.RequestKeyboardMove(Key.Right, ModifierKeys.None);
                Assert.Equal(36, item.CurrentX);
                Assert.Equal("77", editor.DraftXText);
                editor.DiscardPlacementDraftCommand.Execute(null);
                viewport.RequestKeyboardMove(Key.Right, ModifierKeys.None);
                Assert.Equal(37, item.CurrentX);
                Assert.Equal("37", editor.DraftXText);
                return true;
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Theory]
    [InlineData(OpenVisionLanguage.Korean)]
    [InlineData(OpenVisionLanguage.English)]
    public async Task StationRemovalControlBindsGuardAndCommandForAnEmptyStation(OpenVisionLanguage language)
    {
        var state = await _ui.InvokeAsync(() =>
        {
            OpenVisionLanguageService.Load();
            var originalLanguage = OpenVisionLanguageService.CurrentLanguage;
            OpenVisionLanguageService.SetLanguage(language, save: false);
            try
            {
                var unit = new MachineUnitDefinition { Id = "unit-inspection", Name = "Inspection" };
                var station = new MachineStationDefinition
                {
                    Id = "station-main",
                    Name = "Main station",
                    Units = [unit]
                };
                var project = new MachineProjectDocument { Name = "Station deletion UI", Stations = [station] };
                using var viewModel = new MainViewModel(project);
                viewModel.OpenStationUnitEditorCommand.Execute(null);
                var scene = new SceneDocumentView { DataContext = viewModel };
                scene.Measure(new System.Windows.Size(900, 700));
                scene.Arrange(new System.Windows.Rect(0, 0, 900, 700));
                scene.UpdateLayout();
                scene.Dispatcher.Invoke(DispatcherPriority.DataBind, new Action(scene.UpdateLayout));
                var controls = Descendants(scene).OfType<Button>().ToArray();
                var removeButton = Assert.Single(controls.Where(button =>
                    ReferenceEquals(button.Command, viewModel.RemoveSelectedStationCommand)));
                var hint = Descendants(scene).OfType<TextBlock>().Single(textBlock =>
                    textBlock.GetBindingExpression(TextBlock.TextProperty)?.ParentBinding.Path.Path
                    == "StationUnitRemovalHintText");
                var childState = (removeButton.IsEnabled, hint.Visibility,
                    System.Windows.Automation.AutomationProperties.GetName(removeButton));
                var serialized = new ProjectDocumentStore().Serialize(project);
                var stationPromptCount = 0;
                viewModel.EquipmentStationRemovalPrompt = _ => { stationPromptCount++; return true; };
                removeButton.Command.Execute(null);
                Assert.Equal(serialized, new ProjectDocumentStore().Serialize(project));
                Assert.Equal(0, stationPromptCount);
                Assert.False(viewModel.HasUnsavedChanges);
                Assert.Equal(string.Format(OpenVisionLanguageService.T("Equipment.StationRemovalRequiresEmpty"), 1), hint.Text);

                viewModel.CancelStationUnitEditorCommand.Execute(null);
                viewModel.EquipmentUnitRemovalPrompt = (_, _) => true;
                viewModel.RemoveEquipmentUnitCommand.Execute(unit.Id);
                viewModel.OpenStationUnitEditorCommand.Execute(null);
                scene.Dispatcher.Invoke(DispatcherPriority.DataBind, new Action(scene.UpdateLayout));
                var enabledButton = Descendants(scene).OfType<Button>().Single(button =>
                    ReferenceEquals(button.Command, viewModel.RemoveSelectedStationCommand));
                viewModel.EquipmentStationRemovalPrompt = _ => true;
                var commandCanExecute = enabledButton.Command?.CanExecute(null) == true;
                var emptyEnabled = enabledButton.IsEnabled;
                enabledButton.Command?.Execute(null);
                scene.Dispatcher.Invoke(DispatcherPriority.DataBind, new Action(scene.UpdateLayout));

                return (ChildEnabled: childState.IsEnabled,
                    HintVisibility: childState.Visibility,
                    AutomationName: childState.Item3,
                    ExpectedAutomationName: OpenVisionLanguageService.T("Equipment.StationRemove"),
                    EmptyEnabled: emptyEnabled,
                    EmptyCanExecute: commandCanExecute,
                    EditorOpen: viewModel.IsStationUnitEditorOpen,
                    IsCreatingNewStation: viewModel.IsCreatingNewStation,
                    SelectedStationId: viewModel.SelectedStationId,
                    HasSelectedStationChildren: viewModel.HasSelectedStationChildren,
                    IsSceneEditable: viewModel.IsSceneEditable,
                    IsEquipmentWorkspace: viewModel.Navigation.IsEquipmentWorkspace,
                    RemainingStations: project.Stations.Count,
                    HasUnsavedChanges: viewModel.HasUnsavedChanges);
            }
            finally
            {
                OpenVisionLanguageService.SetLanguage(originalLanguage, save: false);
            }
        });

        Assert.False(state.ChildEnabled);
        Assert.Equal(Visibility.Visible, state.HintVisibility);
        Assert.Equal(state.ExpectedAutomationName, state.AutomationName);
        Assert.True(state.EmptyCanExecute,
            $"EditorOpen={state.EditorOpen}; NewStation={state.IsCreatingNewStation}; SelectedStation={state.SelectedStationId}; HasChildren={state.HasSelectedStationChildren}; Editable={state.IsSceneEditable}; Equipment={state.IsEquipmentWorkspace}");
        Assert.True(state.EmptyEnabled);
        Assert.Equal(0, state.RemainingStations);
        Assert.True(state.IsCreatingNewStation);
        Assert.True(state.HasUnsavedChanges);
    }

    [Fact]
    public async Task StationUnitEditorKeepsKeyboardFocusInsideAndReturnsToLauncherOnCancel()
    {
        await _ui.InvokeAsync(() =>
        {
            var project = new MachineProjectDocument { Name = "Station focus" };
            using var viewModel = new MainViewModel(project);
            var window = new ShellWindow
            {
                DataContext = viewModel, Width = 1280, Height = 900, ShowInTaskbar = false
            };
            window.Show();
            try
            {
                window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(window.UpdateLayout));
                var view = Assert.Single(Descendants(window).OfType<SceneDocumentView>());
                var launcher = Descendants(window).OfType<Button>().First(button =>
                    button.IsVisible && ReferenceEquals(button.Command, viewModel.OpenStationUnitEditorCommand));
                var before = new ProjectDocumentStore().Serialize(project);
                for (var attempt = 0; attempt < 2; attempt++)
                {
                    launcher.Focus();
                    ((IInvokeProvider)new ButtonAutomationPeer(launcher).GetPattern(PatternInterface.Invoke)).Invoke();
                    window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(window.UpdateLayout));
                    var name = Assert.IsType<TextBox>(view.FindName("StationUnitNameTextBox"));
                    var overlay = Assert.IsType<Grid>(view.FindName("StationUnitEditorOverlay"));
                    Assert.Same(name, Keyboard.FocusedElement);
                    for (var tab = 0; tab < 12; tab++)
                    {
                        ((UIElement)Keyboard.FocusedElement).MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
                        Assert.Contains((DependencyObject)Keyboard.FocusedElement, Descendants(overlay));
                    }
                    var escape = Assert.Single(overlay.InputBindings.OfType<KeyBinding>(), binding => binding.Key == Key.Escape);
                    Assert.Same(viewModel.CancelStationUnitEditorCommand, escape.Command);
                    escape.Command.Execute(null);
                    window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(window.UpdateLayout));
                    Assert.False(viewModel.IsStationUnitEditorOpen);
                    Assert.Same(launcher, Keyboard.FocusedElement);
                }
                Assert.Equal(before, new ProjectDocumentStore().Serialize(project));
                Assert.False(viewModel.HasUnsavedChanges);
                return true;
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Theory]
    [InlineData(1280, 760)]
    [InlineData(1920, 1032)]
    public async Task StationUnitEditorKeepsActionsInsideItsPanelAcrossValidationAndRecovery(int width, int height)
    {
        await _ui.InvokeAsync(() =>
        {
            var originalLanguage = OpenVisionLanguageService.CurrentLanguage;
            OpenVisionLanguageService.SetLanguage(OpenVisionLanguage.Korean, save: false);
            var store = new ProjectDocumentStore();
            var project = store.Load(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Samples", "R19InspectionFlow.ovmachine")));
            var stationIds = project.Stations.Select(station => station.Id).ToHashSet(StringComparer.Ordinal);
            var sequenceCount = project.Sequences.Count;
            using var model = new MainViewModel(project);
            model.ShowEquipmentUnitCommand.Execute("unit-inspection");
            var window = new ShellWindow { DataContext = model, ShowInTaskbar = false };
            SmokeDpiTestHook.PlaceOnTestMonitor(window, width, height);
            window.Show();
            try
            {
                Pump();
                Assert.Equal(width, window.ActualWidth, 1);
                Assert.Equal(height, window.ActualHeight, 1);
                Assert.True(SmokeDpiTestHook.CaptureMonitorEvidence(window).WindowIntersectsMonitor);
                Assert.True(model.EquipmentMaterialFlow.HasSlots);
                var scene = Assert.Single(Descendants(window).OfType<SceneDocumentView>());
                var before = store.Serialize(project);
                model.OpenStationUnitEditorCommand.Execute(null);
                Pump();
                var overlay = Assert.IsType<Grid>(scene.FindName("StationUnitEditorOverlay"));
                var panel = Assert.Single(overlay.Children.OfType<Border>(), border => border.Child is not null);
                var cancel = CommandButton(model.CancelStationUnitEditorCommand);
                var add = CommandButton(model.AddStationUnitCommand);
                CaptureWindow(window, $"station-actions-{width}x{height}-existing");
                AssertActionsInsidePanel();

                var unitName = Assert.IsType<TextBox>(scene.FindName("StationUnitNameTextBox"));
                unitName.SetCurrentValue(TextBox.TextProperty, "");
                Pump();
                Assert.NotEmpty(model.StationUnitValidationText);
                Assert.False(add.IsEnabled);
                Assert.Equal(before, store.Serialize(project));
                model.IsCreatingNewStation = true;
                Pump();
                CaptureWindow(window, $"station-actions-{width}x{height}-new-error");
                AssertActionsInsidePanel();

                var stationName = Descendants(overlay).OfType<TextBox>().Single(box =>
                    box.GetBindingExpression(TextBox.TextProperty)?.ParentBinding.Path.Path == "StationNameDraft");
                unitName.SetCurrentValue(TextBox.TextProperty, new string('장', 80));
                stationName.SetCurrentValue(TextBox.TextProperty, new string('스', 80));
                Pump();
                Assert.Equal(unitName.Text, model.UnitNameDraft);
                Assert.Equal(stationName.Text, model.StationNameDraft);
                Assert.True(add.IsEnabled);
                AssertActionsInsidePanel();
                Invoke(add);
                Pump();
                Assert.False(model.IsStationUnitEditorOpen || model.IsRunning);
                Assert.True(model.HasUnsavedChanges);
                var created = Assert.Single(project.Stations.Where(station => !stationIds.Contains(station.Id)));
                Assert.Equal(stationName.Text, created.Name);
                Assert.Equal(unitName.Text, Assert.Single(created.Units).Name);
                Assert.Equal(sequenceCount, project.Sequences.Count);
                return true;

                Button CommandButton(ICommand command) => Descendants(overlay).OfType<Button>()
                    .Single(button => ReferenceEquals(button.Command, command));
                static void Invoke(Button button) => Assert.IsAssignableFrom<IInvokeProvider>(new ButtonAutomationPeer(button)
                    .GetPattern(PatternInterface.Invoke)).Invoke();
                void Pump() => window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(window.UpdateLayout));
                void AssertActionsInsidePanel()
                {
                    var bounds = new Rect(panel.RenderSize);
                    bounds.Inflate(0.5, 0.5);
                    foreach (var button in new[] { cancel, add })
                    {
                        var buttonBounds = button.TransformToAncestor(panel).TransformBounds(new Rect(button.RenderSize));
                        Assert.True(button.IsVisible && bounds.Contains(buttonBounds),
                            $"{button.Content}: {buttonBounds}, panel {bounds}, window {width}x{height}");
                    }
                }
            }
            finally
            {
                window.DataContext = null;
                window.Close();
                OpenVisionLanguageService.SetLanguage(originalLanguage, save: false);
            }
        });
    }

    [Theory]
    [InlineData(1280, 760)]
    [InlineData(1920, 1032)]
    public async Task StationUnitNewStationChoiceKeepsFocusInsideFullShell(int width, int height)
    {
        await _ui.InvokeAsync(() =>
        {
            var store = new ProjectDocumentStore();
            var project = store.Load(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Samples", "R19InspectionFlow.ovmachine")));
            using var model = new MainViewModel(project);
            model.ShowEquipmentUnitCommand.Execute("unit-inspection");
            var window = new ShellWindow { DataContext = model, ShowInTaskbar = false };
            SmokeDpiTestHook.PlaceOnTestMonitor(window, width, height);
            window.Show();
            try
            {
                Pump();
                var scene = Assert.Single(Descendants(window).OfType<SceneDocumentView>());
                var launcher = Descendants(window).OfType<Button>().First(button =>
                    button.IsVisible && ReferenceEquals(button.Command, model.OpenStationUnitEditorCommand));
                Assert.True(launcher.Focus());
                model.OpenStationUnitEditorCommand.Execute(null);
                Pump();
                var overlay = Assert.IsType<Grid>(scene.FindName("StationUnitEditorOverlay"));
                var option = Descendants(overlay).OfType<ComboBox>().Single();
                var stationName = Descendants(overlay).OfType<TextBox>().Single(box =>
                    box.GetBindingExpression(TextBox.TextProperty)?.ParentBinding.Path.Path == "StationNameDraft");
                var before = store.Serialize(project);
                Assert.True(option.Focus());
                Pump();
                Assert.Same(option, Keyboard.FocusedElement);
                Assert.IsAssignableFrom<IExpandCollapseProvider>(new ComboBoxAutomationPeer(option).GetPattern(PatternInterface.ExpandCollapse)).Expand();
                Pump();
                var choicePeers = new ComboBoxAutomationPeer(option).GetChildren()
                    .Where(peer => peer.GetPattern(PatternInterface.SelectionItem) is ISelectionItemProvider).ToArray();
                Assert.Equal(option.Items.Count, choicePeers.Length);
                Assert.IsAssignableFrom<ISelectionItemProvider>(choicePeers[^1].GetPattern(PatternInterface.SelectionItem)).Select();
                option.IsDropDownOpen = false;
                Pump();
                Assert.True(model.IsCreatingNewStation);
                Assert.Same(option, Keyboard.FocusedElement);
                Assert.True(option.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next)));
                Pump();
                Assert.Same(stationName, Keyboard.FocusedElement);
                for (var tab = 0; tab < 10; tab++)
                {
                    Assert.True(((UIElement)Keyboard.FocusedElement).MoveFocus(new TraversalRequest(FocusNavigationDirection.Next)));
                    Pump();
                    Assert.Contains((DependencyObject)Keyboard.FocusedElement, Descendants(overlay));
                }
                model.CancelStationUnitEditorCommand.Execute(null);
                Pump();
                Assert.False(model.IsStationUnitEditorOpen || model.HasUnsavedChanges || model.IsRunning);
                Assert.Same(launcher, Keyboard.FocusedElement);
                Assert.Equal(before, store.Serialize(project));
                return true;

                void Pump() => window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(window.UpdateLayout));
            }
            finally
            {
                window.DataContext = null;
                window.Close();
            }
        });
    }

    [Theory]
    [InlineData(1280, 760, OpenVisionLanguage.Korean)]
    [InlineData(1280, 760, OpenVisionLanguage.English)]
    [InlineData(1920, 1032, OpenVisionLanguage.Korean)]
    [InlineData(1920, 1032, OpenVisionLanguage.English)]
    public async Task StationUnitSelectorWrapsLongExistingStationValues(int width, int height, OpenVisionLanguage language)
    {
        await _ui.InvokeAsync(() =>
        {
            var originalLanguage = OpenVisionLanguageService.CurrentLanguage;
            OpenVisionLanguageService.SetLanguage(language, save: false);
            var longName = new string(language == OpenVisionLanguage.Korean ? '스' : 'W', 80);
            var project = new MachineProjectDocument
            {
                Name = "Long station selector",
                Stations = [new() { Id = "station-long", Name = longName, Units = [new() { Id = "unit-long", Name = "Unit" }] }]
            };
            using var model = new MainViewModel(project);
            model.ShowEquipmentUnitCommand.Execute("unit-long");
            var window = new ShellWindow { DataContext = model, ShowInTaskbar = false };
            SmokeDpiTestHook.PlaceOnTestMonitor(window, width, height);
            window.Show();
            try
            {
                Pump();
                model.OpenStationUnitEditorCommand.Execute(null);
                Pump();
                var scene = Assert.Single(Descendants(window).OfType<SceneDocumentView>());
                var overlay = Assert.IsType<Grid>(scene.FindName("StationUnitEditorOverlay"));
                var combo = Descendants(overlay).OfType<ComboBox>().Single();
                var selected = Descendants(combo).OfType<TextBlock>().Single(text => text.Text == longName);
                CaptureWindow(window, $"unit-selector-{width}-{language}-long-selected");
                AssertTextInside(selected, combo);
                combo.IsDropDownOpen = true;
                Pump();
                var item = Assert.IsType<ComboBoxItem>(combo.ItemContainerGenerator.ContainerFromIndex(0));
                var dropdown = Descendants(item).OfType<TextBlock>().Single(text => text.Text == longName);
                AssertTextInside(dropdown, item);
                Assert.Equal(TextWrapping.Wrap, dropdown.TextWrapping);
                combo.IsDropDownOpen = false;
                Pump();
                model.CancelStationUnitEditorCommand.Execute(null);
                Pump();
                Assert.False(model.HasUnsavedChanges || model.IsRunning || model.IsStationUnitEditorOpen);
                return true;

                void Pump() => window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(window.UpdateLayout));
                static void AssertTextInside(TextBlock text, FrameworkElement parent)
                {
                    var bounds = text.TransformToAncestor(parent).TransformBounds(new Rect(text.RenderSize));
                    var parentBounds = new Rect(parent.RenderSize);
                    parentBounds.Inflate(0.5, 0.5);
                    Assert.True(parentBounds.Contains(bounds), $"Long station text {bounds} exceeds parent {parentBounds}.");
                    var formatted = new FormattedText(text.Text, System.Globalization.CultureInfo.CurrentCulture, text.FlowDirection,
                        new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch), text.FontSize, Brushes.White,
                        VisualTreeHelper.GetDpi(text).PixelsPerDip);
                    if (text.TextWrapping != TextWrapping.NoWrap) formatted.MaxTextWidth = Math.Max(1, text.ActualWidth);
                    Assert.True(formatted.Width <= text.ActualWidth + 0.5 && formatted.Height <= text.ActualHeight + 0.5,
                        $"Required station glyphs {formatted.Width}x{formatted.Height} exceed {text.ActualWidth}x{text.ActualHeight}.");
                }
            }
            finally
            {
                window.DataContext = null;
                window.Close();
                OpenVisionLanguageService.SetLanguage(originalLanguage, save: false);
            }
        });
    }

    [Theory]
    [InlineData(1280, 760, OpenVisionLanguage.Korean)]
    [InlineData(1920, 1032, OpenVisionLanguage.English)]
    public async Task StationUnitSelectorUsesOneExistingOrNewChoiceAndPreservesDrafts(int width, int height, OpenVisionLanguage language)
    {
        await _ui.InvokeAsync(() =>
        {
            var originalLanguage = OpenVisionLanguageService.CurrentLanguage;
            OpenVisionLanguageService.SetLanguage(language, save: false);
            var project = new MachineProjectDocument
            {
                Name = "Station choice",
                Stations = [new() { Id = "station-existing", Name = "Existing station", Units = [new() { Id = "unit-existing", Name = "Existing unit" }] }]
            };
            using var model = new MainViewModel(project);
            model.ShowEquipmentUnitCommand.Execute("unit-existing");
            var window = new ShellWindow { DataContext = model, ShowInTaskbar = false };
            SmokeDpiTestHook.PlaceOnTestMonitor(window, width, height);
            window.Show();
            try
            {
                Pump();
                var before = System.Text.Json.JsonSerializer.Serialize(project);
                model.OpenStationUnitEditorCommand.Execute(null);
                Pump();
                var scene = Assert.Single(Descendants(window).OfType<SceneDocumentView>());
                var overlay = Assert.IsType<Grid>(scene.FindName("StationUnitEditorOverlay"));
                var combo = Descendants(overlay).OfType<ComboBox>().Single();
                var unitName = Assert.IsType<TextBox>(scene.FindName("StationUnitNameTextBox"));
                var stationName = Descendants(overlay).OfType<TextBox>().Single(box =>
                    box.GetBindingExpression(TextBox.TextProperty)?.ParentBinding.Path.Path == "StationNameDraft");
                var add = Descendants(overlay).OfType<Button>().Single(button => ReferenceEquals(button.Command, model.AddStationUnitCommand));
                Assert.Equal(project.Stations.Count + 1, combo.Items.Count);
                Assert.Empty(Descendants(overlay).OfType<CheckBox>());
                Assert.Equal(language == OpenVisionLanguage.Korean ? "유닛 추가" : "Add unit",
                    Descendants(overlay).OfType<TextBlock>().Single(text => text.FontSize == 19).Text);
                unitName.SetCurrentValue(TextBox.TextProperty, new string('U', 80));
                combo.SelectedIndex = combo.Items.Count - 1;
                Pump();
                Assert.True(model.IsCreatingNewStation && stationName.IsVisible && combo.IsEnabled);
                stationName.SetCurrentValue(TextBox.TextProperty, "");
                Pump();
                Assert.False(add.IsEnabled);
                Assert.NotEmpty(model.StationUnitValidationText);
                stationName.SetCurrentValue(TextBox.TextProperty, new string('S', 80));
                Pump();
                Assert.True(add.IsEnabled);
                CaptureWindow(window, $"unit-selector-{width}-{language}-new-recovered");
                Assert.True(combo.Focus());
                Assert.True(combo.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next)));
                Pump();
                Assert.Same(stationName, Keyboard.FocusedElement);
                combo.SelectedIndex = 0;
                Pump();
                Assert.False(model.IsCreatingNewStation || stationName.IsVisible);
                Assert.Equal("station-existing", model.SelectedStationId);
                Assert.Equal(new string('U', 80), model.UnitNameDraft);
                Assert.Equal(new string('S', 80), model.StationNameDraft);
                model.CancelStationUnitEditorCommand.Execute(null);
                Pump();
                Assert.Equal(before, System.Text.Json.JsonSerializer.Serialize(project));
                Assert.False(model.HasUnsavedChanges || model.IsRunning);
                model.OpenStationUnitEditorCommand.Execute(null);
                Pump();
                combo.SelectedIndex = combo.Items.Count - 1;
                unitName.SetCurrentValue(TextBox.TextProperty, "Added unit");
                stationName.SetCurrentValue(TextBox.TextProperty, "Added station");
                Pump();
                Assert.IsAssignableFrom<IInvokeProvider>(new ButtonAutomationPeer(add).GetPattern(PatternInterface.Invoke)).Invoke();
                Pump();
                Assert.False(model.IsStationUnitEditorOpen || model.IsRunning);
                var createdStation = Assert.Single(project.Stations.Where(station => station.Id != "station-existing"));
                Assert.Equal("Added station", createdStation.Name);
                Assert.Equal("Added unit", Assert.Single(createdStation.Units).Name);
                Assert.True(model.HasUnsavedChanges);
                return true;

                void Pump() => window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(window.UpdateLayout));
            }
            finally
            {
                window.DataContext = null;
                window.Close();
                OpenVisionLanguageService.SetLanguage(originalLanguage, save: false);
            }
        });
    }

    [Theory]
    [InlineData(1280, 760, OpenVisionLanguage.Korean)]
    [InlineData(1920, 1032, OpenVisionLanguage.English)]
    public async Task StationUnitSelectorCancelsObsoleteDraftAfterProjectReplacement(int width, int height, OpenVisionLanguage language)
    {
        await _ui.InvokeTaskAsync(async () =>
        {
            var originalLanguage = OpenVisionLanguageService.CurrentLanguage;
            OpenVisionLanguageService.SetLanguage(language, save: false);
            var project = new MachineProjectDocument
            {
                Name = "Old station context",
                Stations = [new() { Id = "station-old", Name = "Station", Units = [new() { Id = "unit-old", Name = "Unit" }] }]
            };
            using var model = new MainViewModel(project);
            model.ShowEquipmentUnitCommand.Execute("unit-old");
            var window = new ShellWindow { DataContext = model, ShowInTaskbar = false };
            SmokeDpiTestHook.PlaceOnTestMonitor(window, width, height);
            window.Show();
            try
            {
                Pump();
                model.OpenStationUnitEditorCommand.Execute(null);
                Pump();
                var before = System.Text.Json.JsonSerializer.Serialize(project);
                model.NewProjectCommand.Execute(null);
                Pump();
                model.CancelNewProjectNameCommand.Execute(null);
                Pump();
                Assert.True(model.IsStationUnitEditorOpen);
                Assert.Equal(before, System.Text.Json.JsonSerializer.Serialize(project));
                Assert.True(await model.CreateNewProjectAsync("Replacement"));
                Pump();
                CaptureWindow(window, $"unit-selector-{width}-{language}-project-replaced");
                Assert.False(model.IsStationUnitEditorOpen, "A replaced project must not retain the previous unit form.");
                var fresh = Assert.IsType<MachineProjectDocument>(typeof(MainViewModel)
                    .GetProperty("CurrentProject", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(model));
                var freshPayload = System.Text.Json.JsonSerializer.Serialize(fresh);
                model.AddStationUnitCommand.Execute(null);
                Assert.Equal(freshPayload, System.Text.Json.JsonSerializer.Serialize(fresh));
                Assert.False(model.HasUnsavedChanges || model.IsRunning);
            }
            finally
            {
                window.DataContext = null;
                window.Close();
                OpenVisionLanguageService.SetLanguage(originalLanguage, save: false);
            }
            void Pump() => window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(window.UpdateLayout));
        });
    }


    [Fact]
    public async Task UnitSceneKeepsItsComponentsAcrossPlanAndObliqueViewsAndOverviewRestoresAllUnits()
    {
        var state = await _ui.InvokeAsync(() =>
        {
            using var layout = CreateUnitLayout();
            using var navigation = new ShellNavigationViewModel(false, () => false, () => false,
                () => Task.CompletedTask, () => { }, _ => { });
            var view = new SceneDocumentView
            {
                DataContext = new SceneDataContext(layout, navigation, IsSceneEditable: true)
            };
            var window = new Window
            {
                Width = 1000,
                Height = 760,
                ShowInTaskbar = false,
                ShowActivated = false,
                WindowStyle = WindowStyle.None,
                Content = view
            };
            window.Show();
            try
            {
                window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(window.UpdateLayout));
                var viewport = Assert.Single(Descendants(view).OfType<MachineSceneViewport>());
                layout.ShowUnit("unit-inspection");
                PumpBindings();
                var obliqueIds = viewport.ItemsSource?.Select(item => item.Id).ToArray() ?? [];
                var obliqueBounds = viewport.GetItemScreenBounds("camera-inspection");
                var obliqueModeBound = layout.ActiveUnitId == "unit-inspection"
                    && viewport.IsObliqueView && layout.IsObliqueView;

                layout.ShowTopViewCommand.Execute(null);
                PumpBindings();
                var planIds = viewport.ItemsSource?.Select(item => item.Id).ToArray() ?? [];
                var planBounds = viewport.GetItemScreenBounds("camera-inspection");
                var planModeBound = layout.ActiveUnitId == "unit-inspection"
                    && !viewport.IsObliqueView && !layout.IsObliqueView;

                layout.ShowObliqueViewCommand.Execute(null);
                PumpBindings();
                var returnedObliqueIds = viewport.ItemsSource?.Select(item => item.Id).ToArray() ?? [];
                var returnedObliqueModeBound = layout.ActiveUnitId == "unit-inspection"
                    && viewport.IsObliqueView && layout.IsObliqueView;

                layout.ShowOverview();
                PumpBindings();
                var overviewIds = viewport.ItemsSource?.Select(item => item.Id).ToArray() ?? [];
                var overviewRestored = layout.ActiveUnitId is null && !layout.IsUnitView
                    && overviewIds.Length == 3;

                return (obliqueIds, planIds, returnedObliqueIds, obliqueBounds, planBounds,
                    obliqueModeBound, planModeBound, returnedObliqueModeBound, overviewIds, overviewRestored);
            }
            finally
            {
                window.Close();
            }
        });

        Assert.Equal(new[] { "camera-inspection", "conveyor-inspection" }, state.obliqueIds);
        Assert.Equal(state.obliqueIds, state.planIds);
        Assert.Equal(state.obliqueIds, state.returnedObliqueIds);
        Assert.NotNull(state.obliqueBounds);
        Assert.NotNull(state.planBounds);
        Assert.NotEqual(state.obliqueBounds, state.planBounds);
        Assert.True(state.obliqueModeBound);
        Assert.True(state.planModeBound);
        Assert.True(state.returnedObliqueModeBound);
        Assert.Equal(new[] { "camera-inspection", "camera-loading", "conveyor-inspection" }, state.overviewIds);
        Assert.True(state.overviewRestored);
    }

    [Theory]
    [InlineData(1920, true)]
    [InlineData(1920, false)]
    [InlineData(1280, true)]
    [InlineData(1280, false)]
    public async Task EquipmentSceneLongTitleAndSelectedLabelStaySeparateAcrossInspectorAndResize(int width, bool oblique)
    {
        await _ui.InvokeAsync(() =>
        {
            var store = new ProjectDocumentStore();
            var project = store.Load(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Samples", "R19InspectionFlow.ovmachine")));
            var unit = project.Stations.SelectMany(station => station.Units).First();
            unit.Name = "긴 유닛 이름 " + new string('검', 70);
            var frame = project.Layouts.SelectMany(layout => layout.Components).First(component => component.Kind == LayoutComponentKind.MachineFrame);
            frame.UnitId = unit.Id;
            frame.Name = "긴 장치 이름 " + new string('장', 70);
            frame.Id = "frame-long-" + new string('X', 96);
            using var model = new MainViewModel(project);
            model.ShowEquipmentUnitCommand.Execute(unit.Id);
            model.Layout.Select(frame.Id);
            if (oblique) model.Layout.ShowObliqueViewCommand.Execute(null); else model.Layout.ShowTopViewCommand.Execute(null);
            var before = store.SerializeForEvidence(project);
            var dirty = model.HasUnsavedChanges;
            var window = new ShellWindow { DataContext = model, Width = width, Height = 900, ShowInTaskbar = false, ShowActivated = false };
            window.Show();
            try
            {
                var scene = Assert.Single(Descendants(window).OfType<SceneDocumentView>());
                var viewport = Assert.IsType<MachineSceneViewport>(scene.FindName("SceneViewport"));
                var title = Assert.IsType<TextBlock>(scene.FindName("EquipmentSceneTitle"));
                var toggle = Assert.IsType<MenuItem>(scene.FindName("EquipmentInspectorToggleButton"));
                for (var pass = 0; pass < 3; pass++)
                {
                    window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(window.UpdateLayout));
                    viewport.FitToLayout();
                    window.Dispatcher.Invoke(DispatcherPriority.Render, new Action(() => { }));
                    var label = Assert.IsType<Rect>(viewport.LastRenderedSelectedLabelBounds);
                    Assert.True(new Rect(viewport.RenderSize).Contains(label));
                    var titleBounds = title.TransformToAncestor(scene).TransformBounds(new Rect(title.RenderSize));
                    var labelBounds = viewport.TransformToAncestor(scene).TransformBounds(label);
                    Assert.False(titleBounds.IntersectsWith(labelBounds));
                    Assert.True(title.ActualHeight > 0 && viewport.ActualHeight >= 220);
                    Assert.Equal($"{unit.Name} · {OpenVisionLanguageService.T(oblique ? "Scene.ViewOblique" : "Scene.ViewPlan")}", title.Text);
                    var footer = Assert.IsType<Grid>(scene.FindName("EquipmentFooterRegion"));
                    Assert.False(footer.TransformToAncestor(scene).TransformBounds(new Rect(footer.RenderSize)).IntersectsWith(labelBounds));
                    Assert.Equal(frame.Id, model.Layout.SelectedItem?.Id);
                    Assert.Equal(dirty, model.HasUnsavedChanges);
                    Assert.False(model.IsRunning);
                    Assert.Equal(before, store.SerializeForEvidence(project));
                    if (pass == 0)
                        ((IInvokeProvider)new MenuItemAutomationPeer(toggle).GetPattern(PatternInterface.Invoke)).Invoke();
                    else if (pass == 1)
                    {
                        ((IInvokeProvider)new MenuItemAutomationPeer(toggle).GetPattern(PatternInterface.Invoke)).Invoke();
                        window.Width = width == 1920 ? 1280 : 1920;
                    }
                }
                return true;
            }
            finally { window.Close(); }
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task EquipmentSceneUnitListsExposeLastRealUnitAndScrollWithoutChangingRecipe(bool hasSlots)
    {
        await _ui.InvokeAsync(() =>
        {
            var store = new ProjectDocumentStore();
            var project = hasSlots
                ? store.Load(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Samples", "R19InspectionFlow.ovmachine")))
                : new MachineProjectDocument { Name = "Units without products", Layouts = [new MachineLayoutDefinition { Id = "layout-main", Name = "Main" }],
                    Stations = [new MachineStationDefinition { Id = "station-main", Name = "Station" }] };
            if (!hasSlots) project.Simulation.ActiveLayoutId = "layout-main";
            var station = project.Stations.First();
            for (var index = 0; index < 8; index++)
                station.Units.Add(new MachineUnitDefinition { Id = $"unit-scroll-{index}", Name = "긴 유닛 이름 " + new string('검', 70) });
            using var model = new MainViewModel(project);
            var before = store.SerializeForEvidence(project);
            var window = new ShellWindow { DataContext = model, Width = 1280, Height = 900, ShowInTaskbar = false, ShowActivated = false };
            window.Show();
            try
            {
                window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(window.UpdateLayout));
                var scene = Assert.Single(Descendants(window).OfType<SceneDocumentView>());
                var scroll = Assert.IsType<ScrollViewer>(scene.FindName(hasSlots ? "EquipmentSlotsScrollViewer" : "EquipmentUnitsScrollViewer"));
                Assert.False(scroll.IsVisible);
                var disclosure = Assert.IsType<System.Windows.Controls.Primitives.ToggleButton>(scene.FindName("EquipmentSlotsToggleButton"));
                ((IToggleProvider)new ToggleButtonAutomationPeer(disclosure).GetPattern(PatternInterface.Toggle)).Toggle();
                window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(window.UpdateLayout));
                var buttons = Descendants(scroll).OfType<Button>().ToArray();
                Assert.Equal(project.Stations.Sum(item => item.Units.Count), buttons.Length);
                Assert.True(scroll.IsVisible && scroll.ScrollableWidth > 0);
                var last = buttons.Last();
                last.Focus();
                last.BringIntoView();
                scroll.ScrollToRightEnd();
                scroll.ScrollToBottom();
                window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(window.UpdateLayout));
                Assert.Equal(scroll.ScrollableWidth, scroll.HorizontalOffset, 1);
                Assert.Equal(scroll.ScrollableHeight, scroll.VerticalOffset, 1);
                var bounds = last.TransformToAncestor(scroll).TransformBounds(new Rect(last.RenderSize));
                Assert.True(bounds.Right <= scroll.ActualWidth && bounds.Left >= 0);
                if (!hasSlots)
                {
                    var expected = model.Layout.UnitOverviewItems.Single(item => item.UnitId == "unit-scroll-7");
                    var text = Descendants(last).OfType<TextBlock>().ToArray();
                    foreach (var value in new[] { expected.UnitId, expected.CountText })
                    {
                        var required = Assert.Single(text.Where(item => item.Text == value));
                        var rendered = required.TransformToAncestor(scroll).TransformBounds(new Rect(required.RenderSize));
                        Assert.True(required.IsVisible && rendered.Height > 0 && rendered.Width > 0);
                        Assert.True(rendered.Left >= 0 && rendered.Right <= scroll.ViewportWidth + 1);
                        Assert.True(rendered.Top >= 0 && rendered.Bottom <= scroll.ViewportHeight + 1,
                            $"Required summary content clipped: {value}, {rendered}, viewport={scroll.ViewportHeight}");
                    }
                }
                ((IInvokeProvider)new ButtonAutomationPeer(last).GetPattern(PatternInterface.Invoke)).Invoke();
                window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(window.UpdateLayout));
                Assert.Equal("unit-scroll-7", model.Layout.ActiveUnitId);
                Assert.Equal(before, store.SerializeForEvidence(project));
                Assert.False(model.HasUnsavedChanges || model.IsRunning);
                var report = SmokeLayoutValidator.Validate(window, 1280, 900, 100);
                Assert.NotEmpty(report.IntentionalHorizontalScrollBars);
                Assert.Empty(report.VisibleHorizontalScrollBars);
                return true;
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public async Task EquipmentSceneReferenceButtonShowsRealReferencesAndNavigatesWithoutExecuting()
    {
        await _ui.InvokeAsync(() =>
        {
            var store = new ProjectDocumentStore();
            var project = store.Load(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Samples", "R19InspectionFlow.ovmachine")));
            using var model = new MainViewModel(project);
            model.Layout.Select("wafer-stage");
            var component = project.Layouts.SelectMany(layout => layout.Components).Single(item => item.Id == "wafer-stage");
            var expected = project.Sequences.SelectMany(sequence => sequence.Steps.Where(step =>
                new[] { step.TargetId, step.WorkpieceComponentId, step.ExpectedTargetId }.Any(id =>
                    !string.IsNullOrWhiteSpace(id) && (id == component.Id || id == component.BehaviorBindingId)))
                .Select(step => (SequenceId: sequence.Id, StepId: step.Id))).ToArray();
            Assert.NotEmpty(expected);
            var before = store.SerializeForEvidence(project);
            var window = new ShellWindow { DataContext = model, Width = 1280, Height = 900, ShowInTaskbar = false, ShowActivated = false };
            window.Show();
            try
            {
                window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(window.UpdateLayout));
                var scene = Assert.Single(Descendants(window).OfType<SceneDocumentView>());
                var button = Assert.IsType<Button>(scene.FindName("EquipmentSequenceReferencesButton"));
                Assert.Equal(expected, model.Layout.EquipmentSelectionStepReferences);
                Assert.Equal(model.Layout.EquipmentSelectionStepsText, button.Content);
                Assert.True(button.IsVisible && button.IsEnabled);
                ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
                window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(window.UpdateLayout));
                Assert.True(model.Navigation.IsSimulationWorkspace);
                Assert.Equal(1, model.Navigation.SelectedExecutionTabIndex);
                Assert.Equal(expected[0].SequenceId, model.SequenceEditor.SelectedSequence?.Id);
                Assert.Equal(expected[0].StepId, model.SequenceEditor.SelectedStep?.Id);
                Assert.Equal("wafer-stage", model.Layout.SelectedItem?.Id);
                Assert.Equal(TimeSpan.Zero, model.SceneSnapshots.Latest!.SimulationTime);
                Assert.Equal(before, store.SerializeForEvidence(project));
                Assert.False(model.HasUnsavedChanges || model.IsRunning);
                return true;
            }
            finally { window.Close(); }
        });
    }

    private static MachineLayoutViewModel CreateLayout(out MachineProjectDocument project)
    {
        var definition = new MachineLayoutDefinition { Id = "layout-main", Name = "Main" };
        definition.Components.Add(new LayoutComponentDefinition
        {
            Id = "camera-01", Name = "Inspection Camera", Kind = LayoutComponentKind.Camera,
            Transform = new Transform2D { X = 35, Y = 24, RotationDegrees = 27 },
            Size = new Size2D { Width = 36, Height = 20 }
        });
        project = new MachineProjectDocument { Layouts = [definition] };
        project.Simulation.ActiveLayoutId = definition.Id;
        var layout = new MachineLayoutViewModel();
        layout.Load(project);
        return layout;
    }

    private static MachineLayoutViewModel CreateUnitLayout()
    {
        var inspection = new MachineUnitDefinition { Id = "unit-inspection", Name = "Inspection" };
        var loading = new MachineUnitDefinition { Id = "unit-loading", Name = "Loading" };
        var definition = new MachineLayoutDefinition { Id = "layout-main", Name = "Main" };
        definition.Components.Add(new LayoutComponentDefinition
        {
            Id = "camera-inspection", Name = "Inspection Camera", Kind = LayoutComponentKind.Camera,
            UnitId = inspection.Id, Transform = new Transform2D { X = 35, Y = 24 },
            Size = new Size2D { Width = 36, Height = 20 }
        });
        definition.Components.Add(new LayoutComponentDefinition
        {
            Id = "conveyor-inspection", Name = "Inspection Conveyor", Kind = LayoutComponentKind.Conveyor,
            UnitId = inspection.Id, Transform = new Transform2D { X = 82, Y = 40 },
            Size = new Size2D { Width = 64, Height = 16 }
        });
        definition.Components.Add(new LayoutComponentDefinition
        {
            Id = "camera-loading", Name = "Loading Camera", Kind = LayoutComponentKind.Camera,
            UnitId = loading.Id, Transform = new Transform2D { X = 142, Y = 22 },
            Size = new Size2D { Width = 36, Height = 20 }
        });
        var project = new MachineProjectDocument
        {
            Stations = [new MachineStationDefinition { Id = "station-main", Name = "Main", Units = [inspection, loading] }],
            Layouts = [definition]
        };
        project.Simulation.ActiveLayoutId = definition.Id;
        var layout = new MachineLayoutViewModel();
        layout.Load(project);
        return layout;
    }

    private static void PressSpace(RadioButton button, Window window)
    {
        Assert.Same(button, Keyboard.Focus(button));
        var source = PresentationSource.FromVisual(button)
            ?? throw new InvalidOperationException("The projection radio button had no presentation source.");
        button.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.Space)
        {
            RoutedEvent = Keyboard.KeyDownEvent
        });
        button.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.Space)
        {
            RoutedEvent = Keyboard.KeyUpEvent
        });
        window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(window.UpdateLayout));
        PumpBindings();
    }

    private static void PumpBindings() =>
        Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.DataBind, new Action(() => { }));

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private sealed record SceneDataContext(
        MachineLayoutViewModel Layout,
        ShellNavigationViewModel Navigation,
        bool IsSceneEditable);

    private sealed record InspectorSceneDataContext(
        MachineLayoutViewModel Layout,
        ShellNavigationViewModel Navigation,
        bool IsRunMode);
}
