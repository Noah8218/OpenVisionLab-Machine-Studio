using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using OpenVisionLab;
using OpenVisionLab.Machine.Core.Axes;
using OpenVisionLab.Machine.Core.Channels;
using OpenVisionLab.Machine.Core.Devices;
using OpenVisionLab.Machine.Core.Layouts;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Core.Sequences;
using OpenVisionLab.MachineStudio.View.Sequence;
using OpenVisionLab.MachineStudio.ViewModel;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

[Collection(StudioUiTestCollection.Name)]
public sealed class SequenceTargetPresentationTests
{
    private readonly StudioUiTestHost _ui;
    private const string AxisA = "f8a0d6ae-4721-4f59-a7b7-09652061b351";
    private const string AxisB = "8a06a2b9-3f78-4532-b708-e3b6c643589e";
    private const string UnitA = "b4bde781-78cd-4c88-85a7-a3c59ddc5cda";
    private const string UnitB = "f2befb04-b3c0-4bd9-a027-5cc69ddbc6f5";
    private const string PartName = "웨이퍼 위치 축 · 검사 위치와 정렬 상태를 확인하는 이송 단계";

    public SequenceTargetPresentationTests(StudioUiTestHost ui) => _ui = ui;

    [Fact]
    public async Task LayoutRenameRefreshesSequenceTargetNameWithoutChangingItsSelection()
    {
        await _ui.InvokeAsync(() =>
        {
            using var main = new MainViewModel(CreateProject());
            main.SequenceEditor.SelectedStep = main.SequenceEditor.Steps.Single(item => item.Id == "position");
            main.Layout.Select("axis-part-b");
            var editor = Assert.IsType<LayoutComponentEditorViewModel>(main.Layout.SelectedComponentEditor);
            editor.Name = "새로 지정한 위치 축";
            Assert.Equal("position", main.SequenceEditor.SelectedStep?.Id);
            Assert.Equal(AxisB, main.SequenceEditor.SelectedStep?.TargetId);
            Assert.Equal("새로 지정한 위치 축", main.SequenceEditor.SelectedStep?.TargetDisplayName);
            Assert.True(main.HasUnsavedChanges);
            return true;
        });
    }

    [Fact]
    public void NamesDescribePartsUnitsSlotsAndDestinationWithoutWritingDefinitions()
    {
        var project = CreateProject();
        var vm = new SequenceEditorViewModel();
        try
        {
            vm.Load(project);
            var before = new ProjectDocumentStore().Serialize(project);
            int changes = 0;
            vm.DefinitionChanged += (_, _) => changes++;
            Assert.Equal("검사 유닛 / 회전 자리", vm.Steps.Single(item => item.Id == "feed").TargetDisplayName);
            var position = vm.Steps.Single(item => item.Id == "position");
            Assert.Equal(PartName, position.TargetDisplayName);
            Assert.Equal(AxisB, position.TargetIdentityText);
            Assert.Equal(2, position.AvailableTargets.Count(target => target.Name.StartsWith(PartName, StringComparison.Ordinal)));
            Assert.Equal("검사 촬영부", vm.Steps.Single(item => item.Id == "capture").TargetDisplayName);
            var transfer = vm.Steps.Single(item => item.Id == "transfer");
            Assert.Equal("검사 이송 컨베이어 → 검사 유닛 / 검사 대기 자리", transfer.TargetDisplayName);
            Assert.Equal($"conveyor → {UnitB}", transfer.TargetIdentityText);
            Assert.Equal(UnitB, transfer.DestinationUnitId);
            Assert.Equal("—", vm.Steps.Single(item => item.Id == "complete").TargetDisplayName);
            Assert.Equal(before, new ProjectDocumentStore().Serialize(project));
            Assert.Equal(0, changes);
        }
        finally { vm.Dispose(); }
    }

    [Fact]
    public void RefreshPreservesStepSelectionAndReloadKeepsTargetIds()
    {
        var project = CreateProject();
        var vm = new SequenceEditorViewModel();
        try
        {
            vm.Load(project);
            vm.SelectedStep = vm.Steps.Single(item => item.Id == "position");
            int changes = 0;
            vm.DefinitionChanged += (_, _) => changes++;
            project.Layouts[0].Components.Single(component => component.BehaviorBindingId == AxisB).Name = "재설정한 위치 축";
            project.Stations[0].Units[1].Name = "도착 검사 유닛";
            project.Stations[0].Units[1].SlotName = "촬영 자리";
            var renamed = new ProjectDocumentStore().Serialize(project);
            vm.RefreshAuthoringTargets();
            Assert.Equal("position", vm.SelectedStep?.Id);
            Assert.Equal(AxisB, vm.SelectedStep?.TargetId);
            Assert.Equal("재설정한 위치 축", vm.SelectedStep?.TargetDisplayName);
            Assert.Equal("검사 이송 컨베이어 → 도착 검사 유닛 / 촬영 자리", vm.Steps.Single(item => item.Id == "transfer").TargetDisplayName);
            Assert.Equal(renamed, new ProjectDocumentStore().Serialize(project));
            var axis = project.Axes.Single(axis => axis.Id == AxisB);
            project.Axes.Remove(axis);
            vm.RefreshAuthoringTargets();
            Assert.EndsWith($" · {AxisB}", vm.SelectedStep!.TargetDisplayName);
            Assert.Equal(AxisB, vm.SelectedStep.TargetIdentityText);
            Assert.Equal(AxisB, vm.SelectedStep.TargetId);
            Assert.Contains(vm.ValidationIssues, issue => issue.StepId == "position" && issue.TargetId == AxisB);
            project.Axes.Add(axis);
            vm.RefreshAuthoringTargets();
            Assert.Empty(vm.ValidationIssues);
            Assert.Equal("재설정한 위치 축", vm.SelectedStep!.TargetDisplayName);
            Assert.Equal(0, changes);
            var stored = new ProjectDocumentStore().Load(new ProjectDocumentStore().Serialize(project));
            vm.Load(stored);
            vm.SelectedStep = vm.Steps.Single(item => item.Id == "position");
            Assert.Equal(AxisB, vm.SelectedStep?.TargetId);
            Assert.Equal(UnitB, vm.Steps.Single(item => item.Id == "transfer").DestinationUnitId);
        }
        finally { vm.Dispose(); }
    }

    [Fact]
    public async Task KindMismatchUnassignedAndLegacyWorkpieceDisplayRecoverThroughExistingSetters()
    {
        await _ui.InvokeAsync(() =>
        {
            var previousLanguage = OpenVisionLanguageService.CurrentLanguage;
            OpenVisionLanguageService.SetLanguage(OpenVisionLanguage.Korean, save: false);
            var vm = new SequenceEditorViewModel();
            try
            {
                vm.Load(CreateProject());
                var step = vm.Steps.Single(item => item.Id == "position");
                var changed = new List<string?>();
                step.PropertyChanged += (_, args) => changed.Add(args.PropertyName);
                step.TargetId = "camera";
                Assert.Equal("미연결 대상 · camera", step.TargetDisplayName);
                Assert.Equal("camera", step.TargetIdentityText);
                step.TargetId = string.Empty;
                Assert.Equal("대상 미설정", step.TargetDisplayName);
                Assert.Equal(string.Empty, step.TargetId);
                step.TargetId = AxisA;
                Assert.Equal(PartName, step.TargetDisplayName);
                Assert.Equal(AxisA, step.TargetIdentityText);
                step.Action = SequenceStepAction.Delay;
                Assert.Equal("—", step.TargetDisplayName);
                Assert.Equal(string.Empty, step.TargetIdentityText);
                step.Action = SequenceStepAction.FeedWorkpiece;
                step.WorkpieceComponentId = string.Empty;
                Assert.Equal("제품 연결 안 함", step.TargetDisplayName);
                step.WorkpieceComponentId = "wafer";
                Assert.Equal("검사 웨이퍼", step.TargetDisplayName);
                Assert.Equal("wafer", step.TargetIdentityText);
                Assert.Contains(nameof(SequenceStepEditorItem.TargetDisplayName), changed);
                Assert.Contains(nameof(SequenceStepEditorItem.TargetIdentityText), changed);
            }
            finally
            {
                vm.Dispose();
                OpenVisionLanguageService.SetLanguage(previousLanguage, save: false);
            }
            return true;
        });
    }

    [Fact]
    public async Task LocalizationAndEmptyNamesKeepStoredTargetIdsAndLiteralNameSuffixes()
    {
        await _ui.InvokeAsync(() =>
        {
            var previousLanguage = OpenVisionLanguageService.CurrentLanguage;
            OpenVisionLanguageService.SetLanguage(OpenVisionLanguage.Korean, save: false);
            var project = CreateProject();
            var vm = new SequenceEditorViewModel();
            try
            {
                vm.Load(project);
                var step = vm.Steps.Single(item => item.Id == "position");
                step.TargetId = "missing-axis";
                Assert.Equal("미연결 대상 · missing-axis", step.TargetDisplayName);
                OpenVisionLanguageService.SetLanguage(OpenVisionLanguage.English, save: false);
                vm.RefreshLocalization();
                Assert.Equal("Missing target · missing-axis", step.TargetDisplayName);
                Assert.Equal("missing-axis", step.TargetId);
                step.TargetId = string.Empty;
                Assert.Equal("No target selected", step.TargetDisplayName);
                project.Layouts[0].Components.Single(component => component.BehaviorBindingId == AxisB).Name = string.Empty;
                project.Axes.Single(axis => axis.Id == AxisB).Name = string.Empty;
                step.TargetId = AxisB;
                vm.RefreshAuthoringTargets();
                Assert.Equal(AxisB, vm.Steps.Single(item => item.Id == "position").TargetDisplayName);
                var literalName = $"입력한 이름 · {AxisB}";
                project.Layouts[0].Components.Single(component => component.BehaviorBindingId == AxisB).Name = literalName;
                vm.RefreshAuthoringTargets();
                Assert.Equal(literalName, vm.Steps.Single(item => item.Id == "position").TargetDisplayName);
            }
            finally
            {
                vm.Dispose();
                OpenVisionLanguageService.SetLanguage(previousLanguage, save: false);
            }
            return true;
        });
    }

    [Fact]
    public async Task RenderedSelectionRoundTripsUseIdsForDuplicateNamesAndKeepFullIdentityTooltips()
    {
        await _ui.InvokeAsync(() =>
        {
            var vm = new SequenceEditorViewModel();
            var view = new SequenceEditorView { DataContext = vm };
            try
            {
                vm.Load(CreateProject());
                var step = vm.Steps.Single(item => item.Id == "position");
                vm.SelectedStep = step;
                Arrange(view, 1280, 760);
                var target = Assert.IsAssignableFrom<ComboBox>(view.FindName("SequenceTargetComboBox"));
                Assert.Equal(AxisB, target.SelectedValue);
                target.SetCurrentValue(Selector.SelectedValueProperty, AxisA);
                Arrange(view, 1280, 760);
                Assert.Equal(AxisA, step.TargetId);
                Assert.Equal(PartName, step.TargetDisplayName);
                step.TargetId = AxisB;
                Arrange(view, 1280, 760);
                Assert.Equal(AxisB, target.SelectedValue);
                var graph = Descendants(view).OfType<ListBox>().Single(list => ReferenceEquals(list.ItemsSource, vm.Steps));
                var rendered = Descendants(graph).OfType<TextBlock>().Single(text => ReferenceEquals(text.DataContext, step)
                    && BindingOperations.GetBinding(text, TextBlock.TextProperty)?.Path?.Path == nameof(SequenceStepEditorItem.TargetDisplayName));
                Assert.Equal(PartName, rendered.Text);
                Assert.Equal(AxisB, rendered.ToolTip);
                Assert.Equal(TextWrapping.Wrap, rendered.TextWrapping);
                Assert.True(rendered.DesiredSize.Height <= rendered.ActualHeight + rendered.Margin.Top + rendered.Margin.Bottom + 0.5);
                graph.SelectedItem = vm.Steps.Single(item => item.Id == "transfer");
                Arrange(view, 1280, 760);
                Assert.Equal("transfer", vm.SelectedStep?.Id);
                Descendants(view).OfType<TabControl>().Single().SelectedIndex = 1;
                Arrange(view, 1280, 760);
                var table = Descendants(view).OfType<DataGrid>().Single();
                table.SelectedItem = step;
                Arrange(view, 1280, 760);
                Assert.Same(step, vm.SelectedStep);
                var cell = Descendants(table).OfType<TextBlock>().Single(text => ReferenceEquals(text.DataContext, step)
                    && BindingOperations.GetBinding(text, TextBlock.TextProperty)?.Path?.Path == nameof(SequenceStepEditorItem.TargetDisplayName));
                Assert.Equal(PartName, cell.Text);
                Assert.Equal(AxisB, cell.ToolTip);
                Assert.True(cell.DesiredSize.Height <= cell.ActualHeight + cell.Margin.Top + cell.Margin.Bottom + 0.5);
                vm.IsEditable = false;
                Arrange(view, 1280, 760);
                Assert.False(target.IsEnabled);
                Assert.Equal(PartName, cell.Text);
            }
            finally { view.DataContext = null; vm.Dispose(); }
            return true;
        });
    }

    [Theory]
    [InlineData(1280, 760)]
    [InlineData(1920, 1040)]
    public async Task RenderFlowAndTablePreservesSelectedStepAndDefinition(double width, double height)
    {
        await _ui.InvokeAsync(() =>
        {
            var language = OpenVisionLanguageService.CurrentLanguage;
            OpenVisionLanguageService.SetLanguage(OpenVisionLanguage.Korean, save: false);
            var project = CreateProject();
            var vm = new SequenceEditorViewModel();
            var view = new SequenceEditorView { DataContext = vm };
            try
            {
                vm.Load(project);
                Assert.Empty(vm.ValidationIssues);
                var step = vm.Steps.Single(item => item.Id == "position");
                vm.SelectedStep = step;
                var before = new ProjectDocumentStore().Serialize(project);
                int changes = 0;
                vm.DefinitionChanged += (_, _) => changes++;
                Arrange(view, width, height);
                var tabs = Descendants(view).OfType<TabControl>().Single();
                var graph = Descendants(view).OfType<ListBox>().Single(list => ReferenceEquals(list.ItemsSource, vm.Steps));
                Assert.Same(step, graph.SelectedItem);
                Assert.Equal(AxisB, step.TargetId);
                Capture(view, width, height, "flow");
                tabs.SelectedIndex = 1;
                Arrange(view, width, height);
                var table = Descendants(view).OfType<DataGrid>().Single();
                Assert.Same(step, table.SelectedItem);
                Capture(view, width, height, "table");
                Assert.Equal(before, new ProjectDocumentStore().Serialize(project));
                Assert.Equal(0, changes);
                Assert.Same(step, vm.SelectedStep);
            }
            finally
            {
                view.DataContext = null;
                vm.Dispose();
                OpenVisionLanguageService.SetLanguage(language, save: false);
            }
            return true;
        });
    }

    [Theory]
    [InlineData(1280, 760, OpenVisionLanguage.Korean)]
    [InlineData(1920, 1040, OpenVisionLanguage.Korean)]
    [InlineData(1280, 760, OpenVisionLanguage.English)]
    [InlineData(1920, 1040, OpenVisionLanguage.English)]
    public async Task TableFlowRoundTripsKeepOneSelectedCardAndDistinctFocusCueWithReadableActions(double width, double height, OpenVisionLanguage language)
    {
        await _ui.InvokeAsync(() =>
        {
            var previousLanguage = OpenVisionLanguageService.CurrentLanguage;
            OpenVisionLanguageService.SetLanguage(language, save: false);
            var project = CreateProject();
            var vm = new SequenceEditorViewModel();
            var view = new SequenceEditorView { DataContext = vm };
            try
            {
                vm.Load(project);
                var before = new ProjectDocumentStore().Serialize(project);
                int changes = 0;
                vm.DefinitionChanged += (_, _) => changes++;
                Arrange(view, width, height);
                var tabs = Descendants(view).OfType<TabControl>().Single();
                bool distinctFocusCue = true;
                bool actionFits = false;
                string actionMeasurement = string.Empty;
                foreach (string id in new[] { "transfer", "position", "complete", "feed" })
                {
                    var step = vm.Steps.Single(item => item.Id == id);
                    string targetId = step.TargetId;
                    tabs.SelectedIndex = 1;
                    Arrange(view, width, height);
                    var table = Descendants(view).OfType<DataGrid>().Single();
                    table.SelectedItem = step;
                    Arrange(view, width, height);
                    Assert.Same(step, vm.SelectedStep);
                    if (id == "transfer")
                    {
                        var action = Descendants(table).OfType<TextBlock>().Single(text => ReferenceEquals(text.DataContext, step)
                            && (BindingOperations.GetBinding(text, TextBlock.TextProperty)?.Path?.Path == nameof(SequenceStepEditorItem.Action)
                                || BindingOperations.GetMultiBinding(text, TextBlock.TextProperty)?.Bindings.OfType<Binding>()
                                    .Any(binding => binding.Path?.Path == nameof(SequenceStepEditorItem.Action)) == true));
                        Assert.NotEmpty(action.Text);
                        Assert.NotEqual(nameof(SequenceStepAction.TransferUnitWorkpiece), action.Text);
                        var formatted = new FormattedText(action.Text, System.Globalization.CultureInfo.CurrentCulture, action.FlowDirection,
                            new Typeface(action.FontFamily, action.FontStyle, action.FontWeight, action.FontStretch), action.FontSize, action.Foreground,
                            VisualTreeHelper.GetDpi(action).PixelsPerDip);
                        double fullWidth = formatted.WidthIncludingTrailingWhitespace;
                        if (action.TextWrapping == TextWrapping.Wrap) formatted.MaxTextWidth = Math.Max(0.1, action.ActualWidth);
                        Rect inkBounds = formatted.BuildGeometry(new Point()).Bounds;
                        actionFits = action.ActualWidth > 0 && formatted.Height <= action.ActualHeight + 0.5
                            && inkBounds.Right <= action.ActualWidth + 0.5
                            && (action.TextWrapping == TextWrapping.Wrap || fullWidth <= action.ActualWidth + 0.5);
                        actionMeasurement = $"Action='{action.Text}', fullWidth={fullWidth:0.00}, inkRight={inkBounds.Right:0.00}, actualWidth={action.ActualWidth:0.00}, requiredHeight={formatted.Height:0.00}, actualHeight={action.ActualHeight:0.00}, wrapping={action.TextWrapping}";
                        Capture(view, width, height, $"selection-{language}-table", "OPENVISIONLAB_SEQUENCE_SELECTION_EVIDENCE");
                    }
                    tabs.SelectedIndex = 0;
                    Arrange(view, width, height);
                    var graph = Descendants(view).OfType<ListBox>().Single(list => ReferenceEquals(list.ItemsSource, vm.Steps));
                    Assert.Same(step, graph.SelectedItem);
                    // Realize the selected container for inspection; this is not automatic-scroll product proof.
                    graph.ScrollIntoView(step);
                    Arrange(view, width, height);
                    var containers = Descendants(graph).OfType<ListBoxItem>().ToArray();
                    var selected = Assert.Single(containers.Where(item => item.IsSelected));
                    Assert.Same(step, selected.DataContext);
                    foreach (var container in containers)
                    {
                        var card = Descendants(container).OfType<Border>().Single(border => border.Padding == new Thickness(12, 9, 12, 9));
                        var expectedBrush = (Brush)view.FindResource(container.IsSelected ? "Accent.Primary" : "Border.Default");
                        Assert.Equal(expectedBrush.ToString(), card.BorderBrush.ToString());
                        Assert.Equal(new Thickness(container.IsSelected ? 2 : 1), card.BorderThickness);
                        distinctFocusCue &= ReferenceEquals(view.FindResource("CommandFocusVisualStyle"), container.FocusVisualStyle);
                    }
                    Assert.Same(step, vm.SelectedStep);
                    Assert.Equal(targetId, step.TargetId);
                    if (id == "transfer") Capture(view, width, height, $"selection-{language}-flow", "OPENVISIONLAB_SEQUENCE_SELECTION_EVIDENCE");
                    graph.SelectedItem = vm.Steps.Single(item => item.Id == "capture");
                    Arrange(view, width, height);
                    Assert.Equal("capture", vm.SelectedStep?.Id);
                    tabs.SelectedIndex = 1;
                    Arrange(view, width, height);
                    Assert.Same(vm.SelectedStep, Descendants(view).OfType<DataGrid>().Single().SelectedItem);
                    vm.IsEditable = !vm.IsEditable;
                }
                Assert.Equal(before, new ProjectDocumentStore().Serialize(project));
                Assert.Equal(0, changes);
                Assert.True(distinctFocusCue && actionFits, $"Distinct shared focus cue={distinctFocusCue}; action fits={actionFits}; {actionMeasurement}");
            }
            finally
            {
                view.DataContext = null;
                vm.Dispose();
                OpenVisionLanguageService.SetLanguage(previousLanguage, save: false);
            }
            return true;
        });
    }

    private static MachineProjectDocument CreateProject() => new()
    {
        Name = "Sequence target presentation",
        Simulation = new SimulationDefinition { ActiveLayoutId = "equipment" },
        Stations = [new MachineStationDefinition
        {
            Id = "station", Name = "검사 스테이션",
            Units = [new MachineUnitDefinition { Id = UnitA, Name = "검사 유닛", SlotName = "회전 자리" },
                new MachineUnitDefinition { Id = UnitB, Name = "검사 유닛", SlotName = "검사 대기 자리" }]
        }],
        Axes = [new VirtualAxisDefinition { Id = AxisA, Name = "Axis drive A" }, new VirtualAxisDefinition { Id = AxisB, Name = "Axis drive B" }],
        Channels = [
            new ChannelDefinition { Id = "do.run", Name = "Conveyor run", Kind = ChannelKind.DigitalOutput },
            new ChannelDefinition { Id = "do.reverse", Name = "Conveyor reverse", Kind = ChannelKind.DigitalOutput }
        ],
        Devices = [
            new DeviceDefinition { Id = "camera", Name = "Camera drive", Kind = DeviceKind.Camera },
            new DeviceDefinition { Id = "conveyor-device", Name = "Conveyor drive", Kind = DeviceKind.Conveyor,
                ChannelIds = ["do.run", "do.reverse"], Conveyor = new ConveyorDefinition { RunCommandChannelId = "do.run", ReverseCommandChannelId = "do.reverse" } },
            new DeviceDefinition { Id = "workpiece-device", Name = "Wafer", Kind = DeviceKind.Workpiece,
                Workpiece = new WorkpieceDefinition { Type = "Wafer", ConveyorComponentId = "conveyor" } }
        ],
        Layouts = [new MachineLayoutDefinition
        {
            Id = "equipment", Name = "검사 장비",
            Components = [
                new LayoutComponentDefinition { Id = "axis-part-a", UnitId = UnitA, Kind = LayoutComponentKind.LinearStage, Name = PartName, BehaviorBindingId = AxisA },
                new LayoutComponentDefinition { Id = "axis-part-b", UnitId = UnitA, Kind = LayoutComponentKind.LinearStage, Name = PartName, BehaviorBindingId = AxisB },
                new LayoutComponentDefinition { Id = "camera-part", UnitId = UnitA, Kind = LayoutComponentKind.Camera, Name = "검사 촬영부", BehaviorBindingId = "camera" },
                new LayoutComponentDefinition { Id = "conveyor", UnitId = UnitA, Kind = LayoutComponentKind.Conveyor, Name = "검사 이송 컨베이어", BehaviorBindingId = "conveyor-device" },
                new LayoutComponentDefinition { Id = "wafer", UnitId = UnitA, Kind = LayoutComponentKind.Workpiece, Name = "검사 웨이퍼", BehaviorBindingId = "workpiece-device" }
            ]
        }],
        Sequences = [new SequenceDefinition
        {
            Id = "inspection-flow", Name = "웨이퍼 위치 확인과 촬영", UnitId = UnitA,
            Steps = [
                new SequenceStepDefinition { Id = "feed", Name = "회전 자리에 작업물 투입", Action = SequenceStepAction.FeedUnitWorkpiece, TargetId = UnitA, TimeoutMs = 1000, NextStepId = "position" },
                new SequenceStepDefinition { Id = "position", Name = "위치 축 이동", Action = SequenceStepAction.MoveAxis, TargetId = AxisB, Parameter = "10", NextStepId = "capture" },
                new SequenceStepDefinition { Id = "capture", Name = "웨이퍼 촬영", Action = SequenceStepAction.TriggerCamera, TargetId = "camera", Parameter = "inspection-recipe", NextStepId = "transfer" },
                new SequenceStepDefinition { Id = "transfer", Name = "검사 대기 자리로 이송", Action = SequenceStepAction.TransferUnitWorkpiece, TargetId = "conveyor", DestinationUnitId = UnitB, Parameter = "100", TimeoutMs = 1000, NextStepId = "complete" },
                new SequenceStepDefinition { Id = "complete", Name = "완료", Action = SequenceStepAction.Complete }
            ]
        }]
    };

    private static void Arrange(FrameworkElement view, double width, double height)
    {
        view.Measure(new Size(width, height));
        view.Arrange(new Rect(0, 0, width, height));
        view.UpdateLayout();
        Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.DataBind, new Action(() => { }));
        Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
        view.UpdateLayout();
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private static void Capture(FrameworkElement view, double width, double height, string state, string evidenceVariable = "OPENVISIONLAB_SEQUENCE_TARGET_EVIDENCE")
    {
        var directory = Environment.GetEnvironmentVariable(evidenceVariable);
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory);
        var bitmap = new RenderTargetBitmap((int)width, (int)height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(view);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(directory, $"sequence-{state}-{width:0}x{height:0}.png"));
        encoder.Save(stream);
    }
}
