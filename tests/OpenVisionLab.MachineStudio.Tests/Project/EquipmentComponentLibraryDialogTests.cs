using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using OpenVisionLab.Machine.Core.Layouts;
using OpenVisionLab.Machine.Core.Devices;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.MachineStudio.Model;
using OpenVisionLab.MachineStudio.View.Project;
using OpenVisionLab.MachineStudio.View.Shell;
using OpenVisionLab.MachineStudio.ViewModel;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests.Project;

[Collection(StudioUiTestCollection.Name)]
public sealed class EquipmentComponentLibraryDialogTests
{
    private readonly StudioUiTestHost _ui;

    public EquipmentComponentLibraryDialogTests(StudioUiTestHost ui) => _ui = ui;

    [Theory]
    [InlineData(1280, 760)]
    [InlineData(1920, 1032)]
    public async Task LibrarySearchGetsKeyboardFocusOnEveryOpenAndCancelReturnsIt(int width, int height)
    {
        await _ui.InvokeAsync(() =>
        {
            var project = new MachineProjectDocument
            {
                Name = "Equipment library focus",
                Stations = [new MachineStationDefinition
                {
                    Id = "station",
                    Name = "Inspection",
                    Units = [new MachineUnitDefinition { Id = "unit", Name = new string('u', 80) }]
                }]
            };
            using var model = new MainViewModel(project);
            model.ShowEquipmentUnitCommand.Execute("unit");
            var before = new ProjectDocumentStore().SerializeForEvidence(project);
            var launcher = new Button { Content = "Add device", Command = model.OpenEquipmentComponentLibraryDialogCommand };
            var view = new EquipmentComponentLibraryDialogView { DataContext = model };
            var content = new Grid();
            content.Children.Add(launcher);
            content.Children.Add(view);
            var window = new Window { Content = content, ShowInTaskbar = false, WindowStyle = WindowStyle.None };
            SmokeDpiTestHook.PlaceOnTestMonitor(window, width, height);
            window.Show();
            try
            {
                Pump(window);
                var monitor = SmokeDpiTestHook.CaptureMonitorEvidence(window);
                Assert.True(monitor.WindowIntersectsMonitor);
                Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(monitor));
                var search = Assert.IsType<TextBox>(view.FindName("LibrarySearchTextBox"));
                var escape = Assert.Single(view.InputBindings.OfType<KeyBinding>());
                Assert.Equal(Key.Escape, escape.Key);
                Assert.Same(model.CloseEquipmentComponentLibraryDialogCommand, escape.Command);
                for (var attempt = 0; attempt < 2; attempt++)
                {
                    launcher.Focus();
                    Assert.Same(launcher, Keyboard.FocusedElement);
                    model.OpenEquipmentComponentLibraryDialogCommand.Execute(null);
                    Pump(window);
                    Assert.Same(search, Keyboard.FocusedElement);
                    search.Text = "no-such-device";
                    Pump(window);
                    Assert.True(model.Layout.HasNoLibrarySearchResults);
                    Assert.Equal("no-such-device", model.Layout.LibrarySearchText);
                    for (var tab = 0; tab < 14; tab++)
                    {
                        ((UIElement)Keyboard.FocusedElement).MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
                        Assert.Contains((DependencyObject)Keyboard.FocusedElement, Descendants(view));
                    }
                    escape.Command.Execute(escape.CommandParameter);
                    Pump(window);
                    Assert.False(model.IsEquipmentComponentLibraryDialogOpen);
                    Assert.Same(launcher, Keyboard.FocusedElement);
                    Assert.Equal(before, new ProjectDocumentStore().SerializeForEvidence(project));
                    Assert.False(model.HasUnsavedChanges || model.IsRunning);
                }
                return true;
            }
            finally { window.Close(); }
        });
    }

    [Theory]
    [InlineData(1280, 760, OpenVisionLab.OpenVisionLanguage.Korean, true)]
    [InlineData(1920, 1032, OpenVisionLab.OpenVisionLanguage.English, true)]
    [InlineData(1280, 760, OpenVisionLab.OpenVisionLanguage.Korean, false)]
    [InlineData(1920, 1032, OpenVisionLab.OpenVisionLanguage.English, false)]
    public async Task OpenLibraryAddButtonsTrackManualAdmissionAndRunRecovery(int width, int height, OpenVisionLab.OpenVisionLanguage language, bool rejectDefinition)
    {
        await _ui.InvokeTaskAsync(async () =>
        {
            var previousLanguage = OpenVisionLab.OpenVisionLanguageService.CurrentLanguage;
            OpenVisionLab.OpenVisionLanguageService.SetLanguage(language, save: false);
            try
            {
                var project = CreateLibraryProject();
                using var model = new MainViewModel(project);
                model.ShowEquipmentUnitCommand.Execute("unit-a");
                var stage = model.Layout.Items.First(item => item.Component?.Kind == LayoutComponentKind.LinearStage);
                var frame = model.Layout.Items.First(item => item.Component?.Kind == LayoutComponentKind.MachineFrame);
                model.Layout.Select(stage.Id);
                await WaitAsync(() => model.CanStartManualEquipmentControl);
                var originalStep = project.Simulation.FixedStepMilliseconds;
                if (rejectDefinition)
                {
                    Assert.IsType<LayoutComponentEditorViewModel>(model.Layout.SelectedComponentEditor).RotationDegrees += 1;
                    project.Simulation.FixedStepMilliseconds = originalStep + 1;
                }
                var payload = System.Text.Json.JsonSerializer.Serialize(project);
                var view = new EquipmentComponentLibraryDialogView { DataContext = model };
                using var source = CreateSource(view, width, height);
                model.OpenEquipmentComponentLibraryDialogCommand.Execute(null);
                Arrange(view, width, height);
                var add = FindAddButton(view, LayoutComponentKind.MachineFrame);
                Assert.True(add.IsEnabled && add.Command!.CanExecute(add.CommandParameter));
                var state = rejectDefinition ? "rejection" : "selection-change";
                Capture(view, width, height, language, state + "-open");
                var locked = false;
                var writeRejected = false;
                var admissionCount = 0;
                System.ComponentModel.PropertyChangedEventHandler onAdmission = (_, args) =>
                {
                    if (args.PropertyName != nameof(ManualEquipmentCommissioningViewModel.IsManualControlAdmissionLocked)
                        || !model.ManualEquipment.IsManualControlAdmissionLocked) return;
                    admissionCount++;
                    Arrange(view, width, height);
                    locked = !add.IsEnabled && !add.Command!.CanExecute(add.CommandParameter);
                    add.Command!.Execute(add.CommandParameter);
                    writeRejected = payload == System.Text.Json.JsonSerializer.Serialize(project);
                    Capture(view, width, height, language, state + "-preparing");
                    if (!rejectDefinition) model.Layout.Select(frame.Id);
                };
                model.ManualEquipment.PropertyChanged += onAdmission;
                try
                {
                    model.ManualEquipment.StartManualEquipmentControlCommand.Execute(null);
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    await WaitAsync(() => !model.ManualEquipment.IsStartingManualControl);
                }
                finally { model.ManualEquipment.PropertyChanged -= onAdmission; }
                Arrange(view, width, height);
                var restored = model.IsEquipmentComponentLibraryDialogOpen && model.IsSceneEditable && add.IsEnabled
                    && add.Command!.CanExecute(add.CommandParameter);
                Record(width, height, language, state + "-recovered", new
                {
                    rejectDefinition, admissionCount, locked, writeRejected, restored,
                    selectedId = model.Layout.SelectedItem?.Id, model.IsRunning, model.HasUnsavedChanges,
                    model.ManualEquipment.IsManualControlAdmissionLocked
                });
                Capture(view, width, height, language, state + "-recovered");
                Assert.Equal(1, admissionCount);
                Assert.True(locked, "The open library add button must disable when manual preparation locks scene editing.");
                Assert.True(writeRejected && restored);
                Assert.Equal(payload, System.Text.Json.JsonSerializer.Serialize(project));
                Assert.Equal(rejectDefinition ? stage.Id : frame.Id, model.Layout.SelectedItem?.Id);
                Assert.False(model.IsRunning);
                Assert.Equal(rejectDefinition, model.HasUnsavedChanges);

                project.Simulation.FixedStepMilliseconds = originalStep;
                model.Layout.Select(stage.Id);
                await WaitAsync(() => model.CanStartManualEquipmentControl);
                model.ManualEquipment.StartManualEquipmentControlCommand.Execute(null);
                await WaitAsync(() => model.IsRunning && model.IsRunMode && !model.ManualEquipment.IsStartingManualControl);
                Arrange(view, width, height);
                Capture(view, width, height, language, state + "-manual-run");
                Assert.False(add.IsEnabled || add.Command!.CanExecute(add.CommandParameter));
                Assert.Equal(1, add.Opacity);
                Assert.Equal(Visibility.Visible, Assert.IsType<TextBlock>(view.FindName("LibraryReadOnlyHint")).Visibility);
                Assert.False(Assert.IsType<ComboBox>(view.FindName("LibraryUnitCombo")).IsEnabled);
                Assert.False(model.SceneSnapshots.Latest!.AutomaticRun.IsActive);
                var runningPayload = System.Text.Json.JsonSerializer.Serialize(project);
                add.Command!.Execute(add.CommandParameter);
                Assert.Equal(runningPayload, System.Text.Json.JsonSerializer.Serialize(project));
                model.IsDesignMode = true;
                await WaitAsync(() => model.IsSceneEditable && !model.IsRunning);
                Arrange(view, width, height);
                Assert.True(add.IsEnabled && add.Command!.CanExecute(add.CommandParameter));
                var beforeAdd = project.Layouts.Sum(layout => layout.Components.Count);
                Invoke(add);
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Arrange(view, width, height);
                Assert.Equal(beforeAdd + 1, project.Layouts.Sum(layout => layout.Components.Count));
                Assert.Equal("unit-a", model.Layout.SelectedItem?.Component?.UnitId);
                Assert.False(model.IsEquipmentComponentLibraryDialogOpen || model.IsRunning);
                Assert.True(model.HasUnsavedChanges);
                Assert.Equal(0, model.SceneSnapshots.Latest!.SimulationTime.TotalSeconds);
                Record(width, height, language, state + "-explicit-add", new
                {
                    beforeAdd, afterAdd = project.Layouts.Sum(layout => layout.Components.Count),
                    targetUnit = model.Layout.SelectedItem?.Component?.UnitId, model.HasUnsavedChanges, model.IsRunning
                });
            }
            finally { OpenVisionLab.OpenVisionLanguageService.SetLanguage(previousLanguage, save: false); }
        });
    }

    [Theory]
    [InlineData(1280, 760, OpenVisionLab.OpenVisionLanguage.Korean)]
    [InlineData(1920, 1032, OpenVisionLab.OpenVisionLanguage.English)]
    public async Task OpenLibraryUsesCurrentUnitAndCancelsOldCallbacksAfterProjectReplacement(int width, int height, OpenVisionLab.OpenVisionLanguage language)
    {
        await _ui.InvokeTaskAsync(async () =>
        {
            var previousLanguage = OpenVisionLab.OpenVisionLanguageService.CurrentLanguage;
            OpenVisionLab.OpenVisionLanguageService.SetLanguage(language, save: false);
            try
            {
                var project = CreateLibraryProject();
                using var model = new MainViewModel(project);
                model.UnsavedProjectPrompt = () => UnsavedProjectDecision.Discard;
                model.ShowEquipmentUnitCommand.Execute("unit-a");
                var view = new EquipmentComponentLibraryDialogView { DataContext = model };
                using var source = CreateSource(view, width, height);
                model.OpenEquipmentComponentLibraryDialogCommand.Execute(null);
                Arrange(view, width, height);
                var target = Assert.IsType<ComboBox>(view.FindName("LibraryUnitCombo"));
                target.SelectedValue = "unit-b";
                Arrange(view, width, height);
                Assert.Equal("unit-a", model.Layout.ActiveUnitId);
                Assert.Equal("unit-b", model.EquipmentComponentLibraryUnitId);
                Assert.Contains(new string('b', 80), Assert.IsType<KeyValuePair<string, string>>(target.SelectedItem).Value);
                var add = FindAddButton(view, LayoutComponentKind.MachineFrame);
                var count = project.Layouts.Sum(layout => layout.Components.Count);
                Invoke(add);
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Arrange(view, width, height);
                Assert.Equal(count + 1, project.Layouts.Sum(layout => layout.Components.Count));
                Assert.Equal("unit-b", model.Layout.SelectedItem?.Component?.UnitId);
                Assert.False(model.IsEquipmentComponentLibraryDialogOpen || model.IsRunning);
                model.OpenEquipmentComponentLibraryDialogCommand.Execute(null);
                Arrange(view, width, height);
                var oldAdd = FindAddButton(view, LayoutComponentKind.MachineFrame).Command!;
                model.SimulationWorkspace.SaveProjectScenario(project.Simulation);
                var authored = System.Text.Json.JsonSerializer.Serialize(project);
                model.CloseEquipmentComponentLibraryDialogCommand.Execute(null);
                oldAdd.Execute(LayoutComponentKind.MachineFrame);
                Assert.Equal(authored, System.Text.Json.JsonSerializer.Serialize(project));
                model.OpenEquipmentComponentLibraryDialogCommand.Execute(null);
                Arrange(view, width, height);
                Capture(view, width, height, language, "project-before");
                Assert.True(await model.CreateNewProjectAsync("New library context"));
                Arrange(view, width, height);
                var freshProject = Assert.IsType<MachineProjectDocument>(typeof(MainViewModel)
                    .GetProperty("CurrentProject", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(model));
                var freshPayload = System.Text.Json.JsonSerializer.Serialize(freshProject);
                var obsoleteDialogClosed = !model.IsEquipmentComponentLibraryDialogOpen && !view.IsVisible;
                oldAdd.Execute(LayoutComponentKind.MachineFrame);
                Arrange(view, width, height);
                var lateCallbackRejected = freshPayload == System.Text.Json.JsonSerializer.Serialize(freshProject)
                    && !model.IsStationUnitEditorOpen;
                Capture(view, width, height, language, "project-after");
                Record(width, height, language, "project-after", new
                {
                    obsoleteDialogClosed, lateCallbackRejected, model.IsEquipmentComponentLibraryDialogOpen,
                    model.IsStationUnitEditorOpen, model.EquipmentComponentLibraryDialogStatusText,
                    freshProject.Name, model.HasUnsavedChanges, model.IsRunning
                });
                Assert.True(obsoleteDialogClosed, "A successful project replacement must close the library for the previous project.");
                Assert.True(lateCallbackRejected);
                Assert.False(model.HasUnsavedChanges || model.IsRunning);
                Assert.Equal(authored, System.Text.Json.JsonSerializer.Serialize(project));
                model.Dispose();
                oldAdd.Execute(LayoutComponentKind.MachineFrame);
                Assert.Equal(freshPayload, System.Text.Json.JsonSerializer.Serialize(freshProject));
                Assert.False(oldAdd.CanExecute(LayoutComponentKind.MachineFrame));
            }
            finally { OpenVisionLab.OpenVisionLanguageService.SetLanguage(previousLanguage, save: false); }
        });
    }

    [Theory]
    [InlineData(1280, 760)]
    [InlineData(1920, 1032)]
    public async Task TargetDraftCancelSearchFailureAndRetryPreserveProjectUntilOneSuccessfulAdd(int width, int height)
    {
        await _ui.InvokeAsync(() =>
        {
            var project = new MachineProjectDocument { Stations = [new()
            {
                Id = "station", Name = "Inspection", Units = [new() { Id = "a", Name = "First" }, new() { Id = "b", Name = new string('b', 80) }]
            }] };
            using var model = new MainViewModel(project);
            model.ShowEquipmentUnitCommand.Execute("a");
            var view = new EquipmentComponentLibraryDialogView { DataContext = model };
            using var source = CreateSource(view, width, height);
            var before = new ProjectDocumentStore().SerializeForEvidence(project);
            model.OpenEquipmentComponentLibraryDialogCommand.Execute(null);
            Arrange(view, width, height);
            var target = Assert.IsType<ComboBox>(view.FindName("LibraryUnitCombo"));
            var search = Assert.IsType<TextBox>(view.FindName("LibrarySearchTextBox"));
            target.SelectedValue = "b";
            search.Text = "not-a-device";
            Arrange(view, width, height);
            Assert.True(model.EquipmentComponentLibraryHasNoMatches);
            Assert.Equal("a", model.Layout.ActiveUnitId);
            Assert.Equal(before, new ProjectDocumentStore().SerializeForEvidence(project));
            model.CloseEquipmentComponentLibraryDialogCommand.Execute(null);
            Assert.Equal(before, new ProjectDocumentStore().SerializeForEvidence(project));
            Assert.False(model.HasUnsavedChanges || model.IsRunning);
            model.OpenEquipmentComponentLibraryDialogCommand.Execute(null);
            Arrange(view, width, height);
            Assert.Equal("a", target.SelectedValue);
            Assert.Equal(string.Empty, search.Text);
            target.SelectedValue = "b";
            var failed = FindAddButton(view, LayoutComponentKind.DigitalSensor);
            failed.Command!.Execute(failed.CommandParameter);
            Arrange(view, width, height);
            Assert.True(model.IsEquipmentComponentLibraryDialogOpen);
            Assert.NotEmpty(model.EquipmentComponentLibraryDialogStatusText);
            Assert.Equal(before, new ProjectDocumentStore().SerializeForEvidence(project));
            target.SelectedIndex = -1;
            Arrange(view, width, height);
            Assert.False(model.AddEquipmentComponentFromLibraryCommand.CanExecute(LayoutComponentKind.Conveyor));
            Assert.NotEmpty(model.EquipmentComponentLibraryUnitValidationText);
            target.SelectedValue = "b";
            search.Text = OpenVisionLab.OpenVisionLanguageService.T("Layout.Library.Conveyor.Name");
            Arrange(view, width, height);
            var add = FindAddButton(view, LayoutComponentKind.Conveyor);
            var callback = add.Command!;
            var parameter = add.CommandParameter;
            callback.Execute(parameter);
            callback.Execute(parameter);
            Arrange(view, width, height);
            var component = Assert.Single(Assert.Single(project.Layouts).Components);
            Assert.Equal("b", component.UnitId);
            Assert.Equal(component.Id, model.Layout.SelectedItem?.Id);
            Assert.Equal("b", model.Layout.ActiveUnitId);
            Assert.Equal(component.Id, model.ProjectTree.SelectedNode?.Id);
            Assert.Equal(component.Id, model.Layout.SelectedComponentEditor?.Id);
            Assert.True(model.Navigation.IsInspectorOpen);
            Assert.False(model.IsEquipmentComponentLibraryDialogOpen || model.IsRunning);
            Assert.True(model.HasUnsavedChanges);
            return true;
        });
    }

    [Theory]
    [InlineData(NumericalCameraInputKind.Image2D)]
    [InlineData(NumericalCameraInputKind.HeightMap3D)]
    public async Task CameraVariantBindingAddsTheRequestedInputAndSurvivesUndoSaveAndReload(NumericalCameraInputKind inputKind)
    {
        await _ui.InvokeTaskAsync(async () =>
        {
            var project = CreateLibraryProject();
            using var model = new MainViewModel(project);
            model.ShowEquipmentUnitCommand.Execute("unit-a");
            var view = new EquipmentComponentLibraryDialogView { DataContext = model };
            using var source = CreateSource(view, 1280, 760);
            model.OpenEquipmentComponentLibraryDialogCommand.Execute(null);
            Arrange(view, 1280, 760);
            Assert.IsType<ComboBox>(view.FindName("LibraryUnitCombo")).SelectedValue = "unit-b";
            var camera = model.EquipmentComponentLibraryItems.Single(item => item.CameraInputKind == inputKind);
            Assert.IsType<TextBox>(view.FindName("LibrarySearchTextBox")).Text = camera.Name;
            Arrange(view, 1280, 760);
            var add = Descendants(view).OfType<Button>().Single(button => button.CommandParameter is ComponentLibraryItem item && item.CameraInputKind == inputKind);
            Invoke(add);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var id = model.Layout.SelectedItem!.Id;
            var deviceId = model.Layout.SelectedItem.BehaviorBindingId;
            Assert.Equal("unit-b", model.Layout.SelectedItem.UnitId);
            Assert.Equal(id, model.ProjectTree.SelectedNode?.Id);
            Assert.Equal(id, model.Layout.SelectedComponentEditor?.Id);
            Assert.True(model.Navigation.IsInspectorOpen);
            Assert.Equal(inputKind, project.Devices.Single(device => device.Id == deviceId).Camera!.NumericalInput!.InputKind);
            model.UndoLayoutEditCommand.Execute(null);
            Assert.DoesNotContain(model.Layout.Items, item => item.Id == id);
            model.RedoLayoutEditCommand.Execute(null);
            var path = Path.Combine(OpenVisionLab.TestSupport.TestStorage.RootPath, "v08-camera-" + inputKind + ".ovmachine");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await model.SaveProjectAsync(path);
            var saved = new ProjectDocumentStore().Load(File.ReadAllText(path));
            Assert.Equal("unit-b", saved.Layouts.SelectMany(layout => layout.Components).Single(item => item.Id == id).UnitId);
            Assert.Equal(inputKind, saved.Devices.Single(device => device.Id == deviceId).Camera!.NumericalInput!.InputKind);
            Assert.False(model.IsRunning || model.HasUnsavedChanges);
        });
    }

    [Theory]
    [InlineData(1280, 760, OpenVisionLab.OpenVisionLanguage.Korean)]
    [InlineData(1920, 1032, OpenVisionLab.OpenVisionLanguage.Korean)]
    [InlineData(1280, 760, OpenVisionLab.OpenVisionLanguage.English)]
    [InlineData(1920, 1032, OpenVisionLab.OpenVisionLanguage.English)]
    public async Task LibraryUsesDenseReadableRowsAndVisibleSearchHint(int width, int height, OpenVisionLab.OpenVisionLanguage language)
    {
        await _ui.InvokeAsync(() =>
        {
            var previousLanguage = OpenVisionLab.OpenVisionLanguageService.CurrentLanguage;
            OpenVisionLab.OpenVisionLanguageService.SetLanguage(language, save: false);
            try
            {
                var project = CreateLibraryProject();
                project.Stations[0].Units[0].Name = "Inspection";
                project.Stations[0].Units[1].Name = "Transfer";
                using var model = new MainViewModel(project);
                model.ShowEquipmentUnitCommand.Execute("unit-a");
                var before = new ProjectDocumentStore().SerializeForEvidence(project);
                var view = new EquipmentComponentLibraryDialogView { DataContext = model };
                using var source = CreateSource(view, width, height);
                model.OpenEquipmentComponentLibraryDialogCommand.Execute(null);
                Arrange(view, width, height);
                var search = Assert.IsType<TextBox>(view.FindName("LibrarySearchTextBox"));
                var hint = Assert.IsType<TextBlock>(view.FindName("LibrarySearchPlaceholder"));
                var scroll = Assert.IsType<ScrollViewer>(view.FindName("LibraryListScroll"));
                var target = Assert.IsType<ComboBox>(view.FindName("LibraryUnitCombo"));
                var toggle = Descendants(target).OfType<System.Windows.Controls.Primitives.ToggleButton>().Single();
                var targetCenter = target.TransformToAncestor(view).Transform(new Point(target.ActualWidth / 2, target.ActualHeight / 2));
                var targetHit = view.InputHitTest(targetCenter) as DependencyObject;
                Assert.True(ReferenceEquals(targetHit, toggle) || Descendants(toggle).Contains(targetHit!),
                    $"Unit selector mouse hit={targetHit?.GetType().Name}; toggle={toggle.ActualWidth}x{toggle.ActualHeight}; target={target.ActualWidth}x{target.ActualHeight}.");
                var rows = Descendants(view).OfType<Button>().Where(button => button.CommandParameter is ComponentLibraryItem).ToArray();
                Assert.Equal(12, rows.Length);
                Assert.Equal(10, rows.Count(row => ((ComponentLibraryItem)row.CommandParameter).Kind is not (LayoutComponentKind.FeedPosition or LayoutComponentKind.ExitPosition)));
                Assert.Single(rows, row => ((ComponentLibraryItem)row.CommandParameter).Kind == LayoutComponentKind.FeedPosition);
                Assert.Single(rows, row => ((ComponentLibraryItem)row.CommandParameter).Kind == LayoutComponentKind.ExitPosition);
                Assert.Equal(Visibility.Visible, hint.Visibility);
                Assert.False(hint.IsHitTestVisible);
                Assert.Equal(28, search.ActualHeight);
                Assert.Equal(0, Descendants(search).OfType<ScrollViewer>().Single().ScrollableHeight);
                Assert.DoesNotContain("Equipment.", hint.Text);
                Assert.Equal("Inspection", model.EquipmentComponentLibraryUnits.Single(unit => unit.Key == "unit-a").Value);
                foreach (var row in rows)
                {
                    Assert.InRange(row.ActualHeight, 47, 50);
                    Assert.Equal(new Thickness(0), row.Margin);
                    Assert.Equal(0, Assert.IsType<SolidColorBrush>(row.Background).Color.A);
                    Assert.Equal(0, Assert.IsType<SolidColorBrush>(row.BorderBrush).Color.A);
                }
                var camera2d = rows.Single(row => ((ComponentLibraryItem)row.CommandParameter).CameraInputKind == NumericalCameraInputKind.Image2D);
                var camera3d = rows.Single(row => ((ComponentLibraryItem)row.CommandParameter).CameraInputKind == NumericalCameraInputKind.HeightMap3D);
                var camera2dIcon = Descendants(camera2d).OfType<TextBlock>().Single(text => text.Text == "▣");
                var camera3dIcon = Descendants(camera3d).OfType<TextBlock>().Single(text => text.Text == "▤");
                Assert.NotEqual(Assert.IsType<SolidColorBrush>(camera2dIcon.Foreground).Color, Assert.IsType<SolidColorBrush>(camera3dIcon.Foreground).Color);
                Assert.InRange(camera3dIcon.FontSize, 12, 14);
                if (width == 1920) Assert.Equal(0, scroll.ScrollableHeight);
                var close = Descendants(view).OfType<Button>().Single(button => ReferenceEquals(button.Command, model.CloseEquipmentComponentLibraryDialogCommand));
                var footer = Assert.IsType<Grid>(close.Parent);
                Assert.Same(close, Assert.Single(Descendants(footer).OfType<Button>()));
                var footerText = Assert.Single(Descendants(footer).OfType<TextBlock>().Where(text => !Descendants(close).Contains(text)));
                Assert.Same(view.FindName("LibraryReadOnlyHint"), footerText);
                var closeBounds = close.TransformToAncestor(view).TransformBounds(new Rect(close.RenderSize));
                Assert.InRange(closeBounds.Bottom, 0, height);
                Capture(view, width, height, language, "dense-normal");
                Record(width, height, language, "dense-normal", new
                {
                    rowHeights = rows.Select(row => row.ActualHeight).ToArray(), scroll.ScrollableHeight,
                    closeBounds, hint.Text, camera3dIcon.FontSize
                });
                search.Text = camera3dIcon.DataContext is ComponentLibraryItem camera ? camera.Name : "3D";
                Arrange(view, width, height);
                Assert.Equal(Visibility.Collapsed, hint.Visibility);
                Assert.Single(model.EquipmentComponentLibraryItems);
                var clear = Descendants(view).OfType<Button>().Single(button => ReferenceEquals(button.Command, model.Layout.ClearLibrarySearchCommand));
                var clearBounds = clear.TransformToAncestor(view).TransformBounds(new Rect(clear.RenderSize));
                var searchBounds = search.TransformToAncestor(view).TransformBounds(new Rect(search.RenderSize));
                Assert.True(clearBounds.Top >= searchBounds.Top && clearBounds.Bottom <= searchBounds.Bottom);
                model.Layout.ClearLibrarySearchCommand.Execute(null);
                Arrange(view, width, height);
                Assert.Equal(Visibility.Visible, hint.Visibility);
                Assert.Equal(12, model.EquipmentComponentLibraryItems.Count);
                model.CloseEquipmentComponentLibraryDialogCommand.Execute(null);
                Assert.Equal(before, new ProjectDocumentStore().SerializeForEvidence(project));
                Assert.False(model.IsRunning || model.HasUnsavedChanges);
                return true;
            }
            finally { OpenVisionLab.OpenVisionLanguageService.SetLanguage(previousLanguage, save: false); }
        });
    }

    [Fact]
    public async Task DuplicateUnitNamesStayDistinctWithoutChangingTheTargetIdentity()
    {
        await _ui.InvokeAsync(() =>
        {
            var project = CreateLibraryProject();
            foreach (var unit in project.Stations[0].Units) unit.Name = "Inspection";
            using var model = new MainViewModel(project);
            model.ShowEquipmentUnitCommand.Execute("unit-a");
            var before = new ProjectDocumentStore().SerializeForEvidence(project);
            var view = new EquipmentComponentLibraryDialogView { DataContext = model };
            using var source = CreateSource(view, 1280, 760);
            model.OpenEquipmentComponentLibraryDialogCommand.Execute(null);
            Arrange(view, 1280, 760);
            var target = Assert.IsType<ComboBox>(view.FindName("LibraryUnitCombo"));
            var options = model.EquipmentComponentLibraryUnits;
            Assert.Equal(2, options.Select(option => option.Value).Distinct().Count());
            Assert.All(options, option => Assert.Contains(option.Key, option.Value));
            target.SelectedValue = "unit-b";
            Arrange(view, 1280, 760);
            Assert.Equal("unit-b", model.EquipmentComponentLibraryUnitId);
            Assert.Equal("unit-a", model.Layout.ActiveUnitId);
            model.CloseEquipmentComponentLibraryDialogCommand.Execute(null);
            Assert.Equal(before, new ProjectDocumentStore().SerializeForEvidence(project));
            Assert.False(model.IsRunning || model.HasUnsavedChanges);
            return true;
        });
    }

    private static MachineProjectDocument CreateLibraryProject()
    {
        var project = new ProjectDocumentStore().Load(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Samples", "AutomaticTransferCell.ovmachine")));
        project.Stations = [new MachineStationDefinition
        {
            Id = "station", Name = "Library lifecycle",
            Units = [new MachineUnitDefinition { Id = "unit-a", Name = new string('a', 80) }, new MachineUnitDefinition { Id = "unit-b", Name = new string('b', 80) }]
        }];
        foreach (var component in project.Layouts.SelectMany(layout => layout.Components)) component.UnitId = "unit-a";
        return project;
    }

    private static System.Windows.Interop.HwndSource CreateSource(FrameworkElement view, int width, int height)
    {
        var source = new System.Windows.Interop.HwndSource(new System.Windows.Interop.HwndSourceParameters("LibraryStateBindingFixture")
        {
            Width = width, Height = height, WindowStyle = unchecked((int)0x80000000)
        });
        source.RootVisual = view;
        return source;
    }

    private static Button FindAddButton(DependencyObject view, LayoutComponentKind kind) =>
        Descendants(view).OfType<Button>().Single(button => button.CommandParameter is ComponentLibraryItem item && item.Kind == kind);

    private static void Invoke(Button button) => Assert.IsAssignableFrom<System.Windows.Automation.Provider.IInvokeProvider>(
        new System.Windows.Automation.Peers.ButtonAutomationPeer(button).GetPattern(System.Windows.Automation.Peers.PatternInterface.Invoke)).Invoke();

    private static async Task WaitAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(10);
        Assert.True(condition(), "The library state transition did not settle.");
    }

    private static void Arrange(FrameworkElement view, int width, int height)
    {
        view.Measure(new Size(width, height));
        view.Arrange(new Rect(0, 0, width, height));
        view.UpdateLayout();
        view.Dispatcher.Invoke(DispatcherPriority.DataBind, new Action(() => { }));
        view.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
        view.UpdateLayout();
    }

    private static void Record(int width, int height, OpenVisionLab.OpenVisionLanguage language, string state, object observation)
    {
        var directory = Environment.GetEnvironmentVariable("OPENVISIONLAB_MACHINE_STUDIO_LIBRARY_EVIDENCE");
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, $"library-{width}-{height}-{language}-{state}.json"),
            System.Text.Json.JsonSerializer.Serialize(observation, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }

    private static void Capture(FrameworkElement view, int width, int height, OpenVisionLab.OpenVisionLanguage language, string state)
    {
        var directory = Environment.GetEnvironmentVariable("OPENVISIONLAB_MACHINE_STUDIO_LIBRARY_EVIDENCE");
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory);
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(view);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(directory, $"library-{width}-{height}-{language}-{state}.png"));
        encoder.Save(stream);
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private static void Pump(Window window)
    {
        window.Dispatcher.Invoke(DispatcherPriority.DataBind, new Action(() => { }));
        window.UpdateLayout();
        window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
    }
}
