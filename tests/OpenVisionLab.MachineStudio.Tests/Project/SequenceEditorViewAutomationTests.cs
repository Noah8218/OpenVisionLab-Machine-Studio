using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using OpenVisionLab;
using OpenVisionLab.Machine.Core.Devices;
using OpenVisionLab.Machine.Core.Layouts;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Core.Sequences;
using OpenVisionLab.MachineStudio.View.Sequence;
using OpenVisionLab.MachineStudio.ViewModel;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

[Collection(StudioUiTestCollection.Name)]
public sealed class SequenceEditorViewAutomationTests
{
    private readonly StudioUiTestHost _ui;

    public SequenceEditorViewAutomationTests(StudioUiTestHost ui) => _ui = ui;

    [Theory]
    [InlineData(760)]
    [InlineData(1680)]
    public async Task UnitRepeatBindingRejectsInvalidValuesAndUpdatesOnlyTheSelectedOwner(double width)
    {
        await _ui.InvokeAsync(() =>
        {
            var project = CreateProject();
            project.Stations = [new MachineStationDefinition { Id = "station", Name = "Station", Units = [new MachineUnitDefinition { Id = "a", Name = "A" }, new MachineUnitDefinition { Id = "b", Name = "B" }] }];
            project.Sequences[0].UnitId = "a";
            foreach (var component in project.Layouts.SelectMany(layout => layout.Components)) component.UnitId = "a";
            var editor = new SequenceEditorViewModel();
            editor.Load(project);
            var view = new SequenceEditorView { DataContext = editor };
            var window = new Window { Content = view, Width = width, Height = 538, Left = -2000, Top = -2000, UseLayoutRounding = true, ShowInTaskbar = false, ShowActivated = false };
            window.Show();
            try
            {
                window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(window.UpdateLayout));
                var field = view.UnitRepeatCountTextBox;
                var apply = view.ApplyUnitRepeatButton;
                var provider = Assert.IsAssignableFrom<IInvokeProvider>(new ButtonAutomationPeer(apply).GetPattern(PatternInterface.Invoke));
                Assert.True(field.IsVisible && apply.IsVisible);
                Assert.True(new Rect(view.RenderSize).Contains(field.TransformToAncestor(view).TransformBounds(new Rect(field.RenderSize))));
                field.SetCurrentValue(TextBox.TextProperty, "0");
                PumpBindings();
                provider.Invoke();
                window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(window.UpdateLayout));
                Assert.Equal(1, project.Stations[0].Units[0].RepeatCount);
                Assert.Contains("1–100", editor.StructuralEditStatus);
                field.SetCurrentValue(TextBox.TextProperty, "3");
                PumpBindings();
                provider.Invoke();
                window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(window.UpdateLayout));
                Assert.Equal(3, project.Stations[0].Units[0].RepeatCount);
                Assert.Equal(1, project.Stations[0].Units[1].RepeatCount);
                var store = new ProjectDocumentStore();
                editor.Load(store.Load(store.Serialize(project)));
                PumpBindings();
                Assert.Equal("3", field.Text);
                editor.IsEditable = false;
                window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(window.UpdateLayout));
                Assert.False(field.IsEnabled || apply.IsEnabled);
                return true;
            }
            finally
            {
                window.Close();
                editor.Dispose();
            }
        });
    }

    [Fact]
    public async Task EditingStepUpdatesCardNameAndReportsTheCorrectTransitionFieldAfterRecovery()
    {
        await _ui.InvokeAsync(() =>
        {
            var editor = new SequenceEditorViewModel();
            editor.Load(CreateProject());
            var step = editor.Steps.Single(item => item.Id == "trigger-camera");
            editor.SelectedStep = step;
            var view = new SequenceEditorView { DataContext = editor };
            var window = new Window { Content = view, Width = 1680, Height = 850, ShowInTaskbar = false, ShowActivated = false };
            window.Show();
            try
            {
                window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(window.UpdateLayout));
                var controls = Descendants(view).ToArray();
                var name = controls.OfType<TextBox>().Single(control => control.GetBindingExpression(TextBox.TextProperty)?.ParentBinding.Path.Path == "SelectedStep.Name");
                var cardName = controls.OfType<TextBlock>().Single(control => ReferenceEquals(control.DataContext, step)
                    && control.GetBindingExpression(TextBlock.TextProperty)?.ParentBinding.Path.Path == "DisplayName");
                name.Text = "상면 촬영 · 수정 이름";
                PumpBindings();
                Assert.Equal(name.Text, cardName.Text);

                foreach (var property in new[] { "NextStepId", "ErrorStepId", "FailureStepId" })
                {
                    var field = controls.OfType<TextBox>().Single(control => control.GetBindingExpression(TextBox.TextProperty)?.ParentBinding.Path.Path == "SelectedStep." + property);
                    var original = field.Text;
                    field.Text = "missing-step";
                    PumpBindings();
                    Assert.Contains(editor.ValidationIssues, issue => issue.PropertyName == "Step." + property && issue.Message.Contains("missing-step", StringComparison.Ordinal));
                    field.Text = original;
                    PumpBindings();
                    Assert.Empty(editor.ValidationIssues);
                }
                return true;
            }
            finally
            {
                window.Close();
                editor.Dispose();
            }
        });
    }

    [Theory]
    [InlineData(760)]
    [InlineData(1680)]
    public async Task GraphCardsFillAvailableWidthAndRouteFieldsRemainReachable(double width)
    {
        await _ui.InvokeAsync(() =>
        {
            var editor = new SequenceEditorViewModel();
            editor.Load(CreateProject());
            editor.SelectedStep = editor.Steps.First();
            editor.SelectedStep.NextStepId = "wait-cylinder-extended";
            var view = new SequenceEditorView { DataContext = editor };
            var window = new Window { Width = width, Height = 800, WindowStyle = WindowStyle.None, Content = view };
            window.Show();
            try
            {
                window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(window.UpdateLayout));
                var controls = Descendants(view).ToArray();
                var graph = controls.OfType<ListBox>().Single(list =>
                    list.GetBindingExpression(ItemsControl.ItemsSourceProperty)?.ParentBinding.Path.Path == "Steps");
                var container = Assert.IsType<ListBoxItem>(graph.ItemContainerGenerator.ContainerFromItem(editor.SelectedStep));
                var card = Descendants(container).OfType<StackPanel>().First(panel => ReferenceEquals(panel.DataContext, editor.SelectedStep));
                if (width > 1000) Assert.InRange(card.ActualWidth, 569, 571);
                else Assert.InRange(card.ActualWidth, graph.ActualWidth * .8, graph.ActualWidth);

                foreach (var path in new[] { "NextStepId", "ErrorStepId", "FailureStepId", "Id" })
                {
                    var field = controls.OfType<TextBox>().Single(textBox =>
                        textBox.GetBindingExpression(TextBox.TextProperty)?.ParentBinding.Path.Path == "SelectedStep." + path);
                    var bounds = field.TransformToAncestor(view).TransformBounds(new Rect(field.RenderSize));
                    Assert.InRange(bounds.Right, 0, view.ActualWidth);
                    Assert.InRange(bounds.Bottom, 0, view.ActualHeight);
                    var line = new FormattedText("wait-cylinder-extended", System.Globalization.CultureInfo.CurrentCulture,
                        FlowDirection.LeftToRight, new Typeface(field.FontFamily, field.FontStyle, field.FontWeight, field.FontStretch),
                        field.FontSize, Brushes.White, VisualTreeHelper.GetDpi(field).PixelsPerDip);
                    Assert.True(field.ActualWidth - field.Padding.Left - field.Padding.Right >= line.Width,
                        $"{path} cannot show a normal step ID at view width {width}.");
                }
            }
            finally
            {
                window.Close();
                editor.Dispose();
            }
            return true;
        });
    }

    [Fact]
    public async Task GraphConnectorShowsConfiguredRoutesAndTerminalCompletion()
    {
        await _ui.InvokeAsync(() =>
        {
            var project = CreateProject();
            var sequence = Assert.Single(project.Sequences);
            var cameraDefinition = sequence.Steps.Single(step => step.Id == "trigger-camera");
            cameraDefinition.ErrorStepId = "error-complete";
            cameraDefinition.FailureStepId = "failure-complete";
            sequence.Steps.Add(new SequenceStepDefinition { Id = "error-complete", Action = SequenceStepAction.Complete });
            sequence.Steps.Add(new SequenceStepDefinition { Id = "failure-complete", Action = SequenceStepAction.Complete });

            var editor = new SequenceEditorViewModel();
            editor.Load(project);
            var cameraStep = editor.Steps.Single(step => step.Id == "trigger-camera");
            editor.SelectedStep = cameraStep;
            var terminalStep = editor.Steps.Single(step => step.Id == "complete");
            var view = new SequenceEditorView { DataContext = editor };
            var window = new Window
            {
                Width = 980,
                Height = 720,
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
                var graph = controls.OfType<ListBox>().Single(listBox =>
                    listBox.GetBindingExpression(ItemsControl.ItemsSourceProperty)?.ParentBinding.Path.Path == "Steps");
                var errorRoute = controls.OfType<TextBox>().Single(textBox =>
                    textBox.GetBindingExpression(TextBox.TextProperty)?.ParentBinding.Path.Path == "SelectedStep.ErrorStepId");
                var errorRouteValue = Assert.IsAssignableFrom<IValueProvider>(
                    UIElementAutomationPeer.CreatePeerForElement(errorRoute)?.GetPattern(PatternInterface.Value));
                TextBlock cameraConnector = FindTransitionSummary(graph, cameraStep);

                Assert.Equal(
                    $"{OpenVisionLanguageService.T("Sequence.Next")} → complete"
                    + $"  |  {OpenVisionLanguageService.T("Sequence.Error")} → error-complete"
                    + $"  |  {OpenVisionLanguageService.T("Sequence.Failure")} → failure-complete",
                    cameraConnector.Text);

                errorRouteValue.SetValue(string.Empty);
                PumpBindings();
                Assert.Equal(string.Empty, cameraStep.ErrorStepId);
                Assert.Equal(
                    $"{OpenVisionLanguageService.T("Sequence.Next")} → complete"
                    + $"  |  {OpenVisionLanguageService.T("Sequence.Failure")} → failure-complete",
                    cameraConnector.Text);

                TextBlock terminalConnector = FindTransitionSummary(graph, terminalStep);
                Assert.Equal(OpenVisionLanguageService.T("Sequence.Complete", "완료", "Complete"), terminalConnector.Text);
            }
            finally
            {
                window.Close();
                editor.Dispose();
            }

            return true;
        });
    }

    [Fact]
    public async Task WorkpieceTargetComboBoxBindingSurvivesFeedAndEjectActionChanges()
    {
        var state = await _ui.InvokeAsync(() =>
        {
            var project = CreateProject();
            var editor = new SequenceEditorViewModel();
            editor.Load(project);
            editor.SelectedStep = Assert.Single(editor.Steps, step => step.Id == "trigger-camera");
            var step = editor.SelectedStep!;
            var view = new SequenceEditorView { DataContext = editor };
            var window = new Window
            {
                Width = 980,
                Height = 720,
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
                var workpiece = controls.OfType<ComboBox>().Single(comboBox =>
                    comboBox.GetBindingExpression(Selector.SelectedValueProperty)?.ParentBinding.Path.Path
                        == "SelectedStep.WorkpieceComponentId");
                var action = controls.OfType<ComboBox>().Single(comboBox =>
                    comboBox.GetBindingExpression(Selector.SelectedItemProperty)?.ParentBinding.Path.Path
                        == "SelectedStep.Action");
                workpiece.SelectedValue = "workpiece-1";
                PumpBindings();
                var cameraState = (step.Action, step.TargetId, step.WorkpieceComponentId);

                action.SelectedItem = SequenceStepAction.FeedWorkpiece;
                PumpBindings();
                var feedState = (step.Action, step.TargetId, step.WorkpieceComponentId);

                action.SelectedItem = SequenceStepAction.EjectWorkpiece;
                PumpBindings();
                var ejectState = (step.Action, step.TargetId, step.WorkpieceComponentId);

                action.SelectedItem = SequenceStepAction.TriggerCamera;
                PumpBindings();
                var restoredCameraState = (step.Action, step.TargetId, step.WorkpieceComponentId);

                return (cameraState, feedState, ejectState, restoredCameraState,
                    modelWorkpieceId: project.Sequences.Single().Steps.Single(candidate => candidate.Id == step.Id)
                        .WorkpieceComponentId);
            }
            finally
            {
                window.Close();
            }
        });

        Assert.Equal((SequenceStepAction.TriggerCamera, "camera-1", "workpiece-1"), state.cameraState);
        Assert.Equal((SequenceStepAction.FeedWorkpiece, string.Empty, "workpiece-1"), state.feedState);
        Assert.Equal((SequenceStepAction.EjectWorkpiece, string.Empty, "workpiece-1"), state.ejectState);
        Assert.Equal((SequenceStepAction.TriggerCamera, "camera-1", "workpiece-1"), state.restoredCameraState);
        Assert.Equal("workpiece-1", state.modelWorkpieceId);
    }

    [Theory]
    [InlineData(1280, 760, OpenVisionLanguage.Korean)]
    [InlineData(1280, 760, OpenVisionLanguage.English)]
    [InlineData(1920, 1040, OpenVisionLanguage.Korean)]
    [InlineData(1920, 1040, OpenVisionLanguage.English)]
    public async Task SequenceWorkspaceKeepsSceneAndReadableStepListTogetherWithoutRunning(double width, double height, OpenVisionLanguage language)
    {
        await _ui.InvokeAsync(() =>
        {
            var previousLanguage = OpenVisionLanguageService.CurrentLanguage;
            OpenVisionLanguageService.SetLanguage(language, save: false);
            var project = CreateProject();
            project.Layouts[0].Name = "Sequence scene";
            project.Layouts[0].Components = [new LayoutComponentDefinition { Id = "frame", Name = "Frame", Kind = LayoutComponentKind.MachineFrame, Size = new Size2D { Width = 240, Height = 140 } }];
            project.Sequences[0].Name = new string('S', 80);
            project.Sequences[0].Steps[0].Name = new string('N', 80);
            project.Devices[0].Name = new string('C', 80);
            using var vm = new MainViewModel(project);
            vm.Navigation.HideStartupChoice();
            var store = new ProjectDocumentStore();
            var original = System.Text.Json.JsonSerializer.Serialize(project);
            var window = new OpenVisionLab.MachineStudio.View.Shell.ShellWindow
            {
                DataContext = vm, Width = width, MinWidth = width, Height = height,
                ShowInTaskbar = false, ShowActivated = false, UseLayoutRounding = true
            };
            window.Show();
            try
            {
                vm.Navigation.OpenSequenceEditorCommand.Execute(null);
                Settle();
                Capture("entry");
                var editor = Descendants(window).OfType<SequenceEditorView>().Single(view => view.IsVisible);
                var viewport = Descendants(window).OfType<OpenVisionLab.MachineStudio.View.Scene.MachineSceneViewport>().Single(view => view.IsVisible);
                Assert.Equal(vm.IsSceneEditable, viewport.IsDesignMode);
                Assert.Same(vm.Layout.SceneItems, viewport.ItemsSource);
                Assert.Same(vm.SceneSnapshotSource, viewport.SnapshotSource);
                var table = Descendants(editor).OfType<DataGrid>().Single(grid => grid.IsVisible);
                Assert.True(table.ActualHeight >= 70, $"Step table height {table.ActualHeight}");
                Assert.Same(vm.SequenceEditor.Steps, table.ItemsSource);
                var sceneBounds = viewport.TransformToAncestor(window).TransformBounds(new Rect(viewport.RenderSize));
                var editorBounds = editor.TransformToAncestor(window).TransformBounds(new Rect(editor.RenderSize));
                Assert.True(sceneBounds.Height > editorBounds.Height && sceneBounds.Bottom <= editorBounds.Top, $"Primary scene must retain more space than the step panel: scene={sceneBounds};editor={editorBounds}");
                Assert.DoesNotContain(Descendants(table).OfType<ScrollBar>(), bar => bar.IsVisible && bar.Orientation == Orientation.Horizontal);
                Assert.True(new Rect(window.RenderSize).Contains(editorBounds));
                Assert.Contains(table.Columns, column => column is DataGridTextColumn text
                    && text.Binding is System.Windows.Data.Binding binding && binding.Path.Path == "Parameter");
                Assert.Contains(table.Columns, column => column is DataGridTextColumn text
                    && text.Binding is System.Windows.Data.Binding binding && binding.Path.Path == "TransitionSummary");
                var first = vm.SequenceEditor.Steps.First();
                table.SelectedItem = first;
                table.ScrollIntoView(first);
                Settle();
                Assert.Same(first, vm.SequenceEditor.SelectedStep);
                var row = Assert.IsType<DataGridRow>(table.ItemContainerGenerator.ContainerFromItem(first));
                foreach (var property in new[] { "DisplayName", "TargetDisplayName" })
                {
                    var value = Descendants(row).OfType<TextBlock>().Single(text =>
                        text.GetBindingExpression(TextBlock.TextProperty)?.ParentBinding.Path.Path == property);
                    Assert.Equal(TextWrapping.Wrap, value.TextWrapping);
                    Assert.Equal(TextTrimming.None, value.TextTrimming);
                    var cell = Ancestor<DataGridCell>(value);
                    Assert.NotNull(cell);
                    var bounds = value.TransformToAncestor(cell!).TransformBounds(new Rect(value.RenderSize));
                    Assert.True(bounds.Right <= cell!.ActualWidth + 1 && bounds.Bottom <= cell.ActualHeight + 1,
                        $"{property}: {bounds} in {cell.RenderSize}");
                }
                Capture("long-selected");
                var tabs = Descendants(editor).OfType<TabControl>().Single();
                tabs.SelectedIndex = 0;
                Settle();
                Assert.True(Descendants(editor).OfType<ListBox>().Single(list =>
                    list.GetBindingExpression(ItemsControl.ItemsSourceProperty)?.ParentBinding.Path.Path == "Steps").IsVisible);
                tabs.SelectedIndex = 1;
                Settle();
                table.SelectedItem = vm.SequenceEditor.Steps.Last();
                PumpBindings();
                Assert.Same(vm.SequenceEditor.Steps.Last(), vm.SequenceEditor.SelectedStep);
                vm.SequenceEditor.SelectedStep = first;
                PumpBindings();
                Assert.Same(first, table.SelectedItem);
                Settle();
                var inspector = Descendants(window).OfType<SequenceStepInspectorView>().Single(view => view.IsVisible);
                var actionChoice = inspector.StepDraftAction;
                var targetChoice = inspector.StepDraftTarget;
                Assert.Equal(first.Action, actionChoice.SelectedItem);
                Assert.Equal(first.TargetId, targetChoice.SelectedValue);
                Assert.Equal(original, System.Text.Json.JsonSerializer.Serialize(project));
                Assert.False(vm.IsRunning || vm.HasUnsavedChanges);
                Capture("recovered");
                return true;

                void Settle() => window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(window.UpdateLayout));
                void Capture(string state)
                {
                    var folder = System.Environment.GetEnvironmentVariable("OPENVISIONLAB_MACHINE_STUDIO_SEQUENCE_LAYOUT_EVIDENCE");
                    if (string.IsNullOrWhiteSpace(folder)) return;
                    System.IO.Directory.CreateDirectory(folder);
                    var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(window);
                    var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                    encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                    using var output = System.IO.File.Create(System.IO.Path.Combine(folder, $"sequence-{width:0}x{height:0}-{language}-{state}.png"));
                    encoder.Save(output);
                }
            }
            finally
            {
                window.DataContext = null;
                window.Close();
                OpenVisionLanguageService.SetLanguage(previousLanguage, save: false);
            }
        });

        static T? Ancestor<T>(DependencyObject child) where T : DependencyObject
        {
            for (var parent = VisualTreeHelper.GetParent(child); parent is not null; parent = VisualTreeHelper.GetParent(parent))
                if (parent is T match) return match;
            return null;
        }
    }

    private static MachineProjectDocument CreateProject()
    {
        var project = new MachineProjectDocument
        {
            Id = "camera-workpiece-project",
            Name = "Camera workpiece project",
            Simulation = new SimulationDefinition { ActiveLayoutId = "layout-1" },
            Layouts =
            [
                new MachineLayoutDefinition
                {
                    Id = "layout-1",
                    Components =
                    [
                        new LayoutComponentDefinition
                        {
                            Id = "workpiece-1",
                            Name = "Wafer",
                            Kind = LayoutComponentKind.Workpiece,
                            BehaviorBindingId = "workpiece-device"
                        }
                    ]
                }
            ],
            Devices =
            [
                new DeviceDefinition { Id = "camera-1", Name = "Top camera", Kind = DeviceKind.Camera },
                new DeviceDefinition
                {
                    Id = "workpiece-device",
                    Name = "Wafer",
                    Kind = DeviceKind.Workpiece,
                    Workpiece = new WorkpieceDefinition { Type = "Wafer" }
                }
            ],
            Sequences =
            [
                new SequenceDefinition
                {
                    Id = "sequence-1",
                    Name = "Sequence 1",
                    Steps =
                    [
                        new SequenceStepDefinition
                        {
                            Id = "trigger-camera",
                            Action = SequenceStepAction.TriggerCamera,
                            TargetId = "camera-1",
                            Parameter = "presence-check",
                            NextStepId = "complete"
                        },
                        new SequenceStepDefinition { Id = "complete", Action = SequenceStepAction.Complete }
                    ]
                }
            ]
        };
        return project;
    }

    private static void PumpBindings() =>
        Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.DataBind, new Action(() => { }));

    private static TextBlock FindTransitionSummary(ListBox graph, SequenceStepEditorItem item)
    {
        graph.ScrollIntoView(item);
        PumpBindings();
        var container = Assert.IsType<ListBoxItem>(graph.ItemContainerGenerator.ContainerFromItem(item));
        return Descendants(container).OfType<TextBlock>().Single(textBlock =>
            textBlock.DataContext == item
            && textBlock.GetBindingExpression(TextBlock.TextProperty)?.ParentBinding.Path.Path
                == nameof(SequenceStepEditorItem.TransitionSummary));
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

}
