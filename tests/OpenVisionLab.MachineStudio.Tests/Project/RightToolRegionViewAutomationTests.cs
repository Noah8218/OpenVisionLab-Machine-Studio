using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using OpenVisionLab;
using OpenVisionLab.Machine.Core.Axes;
using OpenVisionLab.Machine.Core.Channels;
using OpenVisionLab.Machine.Core.Devices;
using OpenVisionLab.Machine.Core.Layouts;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Persistence.Projects;
using OpenVisionLab.Machine.Simulation.Sequences;
using OpenVisionLab.MachineStudio.Model;
using OpenVisionLab.MachineStudio.View.Inspector;
using OpenVisionLab.MachineStudio.ViewModel;
using OpenVisionLab.TestSupport;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

[Collection(StudioUiTestCollection.Name)]
public sealed class RightToolRegionViewAutomationTests
{
    private readonly StudioUiTestHost _ui;

    public RightToolRegionViewAutomationTests(StudioUiTestHost ui) => _ui = ui;

    [Theory]
    [InlineData(OpenVisionLanguage.Korean)]
    [InlineData(OpenVisionLanguage.English)]
    public async Task EquipmentUnitInspectorActionsUseExistingNavigationAndRemovalPolicy(OpenVisionLanguage language)
    {
        await _ui.InvokeAsync(() =>
        {
            var previousLanguage = OpenVisionLanguageService.CurrentLanguage;
            OpenVisionLanguageService.SetLanguage(language, save: false);
            try
            {
                var path = Environment.GetEnvironmentVariable("OPENVISIONLAB_MACHINE_STUDIO_SCOPE_REVIEW_PROJECT")
                    ?? Path.Combine(AppContext.BaseDirectory, "Samples", "AutomaticTransferCell.ovmachine");
                var project = new ProjectDocumentStore().Load(File.ReadAllText(path));
                var station = project.Stations.First();
                var assignedUnit = station.Units.First();
                var emptyUnit = new MachineUnitDefinition { Id = "owned-empty-unit", Name = "Owned empty unit" };
                station.Units.Add(emptyUnit);
                var serialized = new ProjectDocumentStore().SerializeForEvidence(project);
                using var model = new MainViewModel(project);
                var blocks = 0; var prompts = 0;
                model.EquipmentUnitRemovalBlockedPresenter = _ => blocks++;
                model.EquipmentUnitRemovalPrompt = (_, _) => { prompts++; return false; };
                model.UnsavedProjectPrompt = () => UnsavedProjectDecision.Discard;
                model.Navigation.IsEquipmentWorkspace = true;
                var view = new RightToolRegionView { DataContext = model };
                using var source = new HwndSource(new HwndSourceParameters("OwnedUnitInspectorActions") { Width = 360, Height = 900, WindowStyle = unchecked((int)0x80000000) });
                source.RootVisual = view;
                void Settle(string phase)
                {
                    ArrangeUnitInspector(view, 360);
                    new RenderTargetBitmap(360, 900, 96, 96, PixelFormats.Pbgra32).Render(view);
                    Assert.Equal(serialized, new ProjectDocumentStore().SerializeForEvidence(project));
                    Assert.False(model.HasUnsavedChanges || model.IsRunning);
                    CaptureUnitInspector(view, 360, language, "scope-action-" + phase);
                }
                void Invoke(Button button)
                {
                    var peer = UIElementAutomationPeer.CreatePeerForElement(button);
                    Assert.IsAssignableFrom<IInvokeProvider>(peer?.GetPattern(PatternInterface.Invoke)).Invoke();
                    PumpBindings();
                }
                model.ShowEquipmentUnitCommand.Execute(assignedUnit.Id); Settle("assigned");
                var delete = Assert.IsType<Button>(view.FindName("EquipmentUnitDeleteButton"));
                Invoke(delete); Invoke(delete); Settle("blocked-repeat");
                Assert.Equal(2, blocks); Assert.Equal(0, prompts);
                model.ShowEquipmentUnitCommand.Execute(emptyUnit.Id); Settle("empty");
                Assert.Equal(emptyUnit.Id, delete.CommandParameter);
                Invoke(delete); Settle("cancelled");
                Assert.Equal(2, blocks); Assert.Equal(1, prompts); Assert.Contains(emptyUnit, station.Units);
                model.ShowEquipmentUnitCommand.Execute(assignedUnit.Id); Settle("unit-return");
                Invoke(Assert.IsType<Button>(view.FindName("EquipmentUnitSequenceButton"))); Settle("sequence-open");
                Assert.True(model.Navigation.IsSimulationWorkspace); Assert.Equal(1, model.Navigation.SelectedExecutionTabIndex);
                model.Navigation.IsEquipmentWorkspace = true;
                model.ShowEquipmentUnitCommand.Execute(assignedUnit.Id); Settle("navigation-return");
                Assert.Equal(assignedUnit.Id, delete.CommandParameter); Assert.True(delete.IsVisible && delete.IsEnabled);
                source.RootVisual = null;
            }
            finally { OpenVisionLanguageService.SetLanguage(previousLanguage, save: false); }
            return true;
        });
    }

    [Theory]
    [InlineData(OpenVisionLanguage.Korean, false)]
    [InlineData(OpenVisionLanguage.Korean, true)]
    [InlineData(OpenVisionLanguage.English, false)]
    [InlineData(OpenVisionLanguage.English, true)]
    public async Task EquipmentScopeInspectorShowsActualUnitAndStationWithoutMutatingTheDefinition(OpenVisionLanguage language, bool longName)
    {
        await _ui.InvokeAsync(() =>
        {
            var previousLanguage = OpenVisionLanguageService.CurrentLanguage;
            OpenVisionLanguageService.SetLanguage(language, save: false);
            try
            {
                var path = Environment.GetEnvironmentVariable("OPENVISIONLAB_MACHINE_STUDIO_SCOPE_REVIEW_PROJECT")
                    ?? Path.Combine(AppContext.BaseDirectory, "Samples", "AutomaticTransferCell.ovmachine");
                var project = new ProjectDocumentStore().Load(File.ReadAllText(path));
                var station = project.Stations.First();
                var unit = station.Units.First();
                if (longName)
                {
                    station.Name = new string(language == OpenVisionLanguage.Korean ? '스' : 'S', 80);
                    unit.Name = new string(language == OpenVisionLanguage.Korean ? '유' : 'U', 80);
                }
                // A real definition gives a non-empty step tally without executing or inventing a runtime result.
                project.Sequences.RemoveAll(sequence => sequence.UnitId == unit.Id);
                project.Sequences.Add(new OpenVisionLab.Machine.Core.Sequences.SequenceDefinition
                {
                    Id = "scope-readonly-sequence", Name = "Owned scope definition", UnitId = unit.Id,
                    Steps = [new OpenVisionLab.Machine.Core.Sequences.SequenceStepDefinition { Id = "scope-complete", Name = "Complete", Action = OpenVisionLab.Machine.Core.Sequences.SequenceStepAction.Complete }]
                });
                var component = project.Layouts[0].Components.First(item => item.Kind == LayoutComponentKind.LinearStage);
                var expectedDeviceCount = project.Layouts.SelectMany(layout => layout.Components).Count(item => item.UnitId == unit.Id && item.Kind != LayoutComponentKind.Workpiece);
                var serialized = new ProjectDocumentStore().SerializeForEvidence(project);
                using var model = new MainViewModel(project);
                model.UnsavedProjectPrompt = () => UnsavedProjectDecision.Discard;
                model.Navigation.IsEquipmentWorkspace = true;
                var view = new RightToolRegionView { DataContext = model };
                using var source = new HwndSource(new HwndSourceParameters("OwnedEquipmentScopeInspector") { Width = 360, Height = 900, WindowStyle = unchecked((int)0x80000000) });
                source.RootVisual = view;
                var stateSuffix = longName ? "long" : "normal";
                IEnumerable<GlyphRunDrawing> Drawings(Drawing drawing) => drawing is GlyphRunDrawing glyph ? [glyph]
                    : drawing is DrawingGroup group ? group.Children.SelectMany(Drawings) : [];
                GlyphRunDrawing[] Glyphs(Visual visual) => new[] { visual }.Concat(Descendants(visual).OfType<Visual>())
                    .Select(VisualTreeHelper.GetDrawing).Where(drawing => drawing is not null).SelectMany(drawing => Drawings(drawing!)).ToArray();
                Rect Bounds(FrameworkElement element) => element.TransformToAncestor(view).TransformBounds(new Rect(element.RenderSize));
                void Settle(string state)
                {
                    ArrangeUnitInspector(view, 360);
                    new RenderTargetBitmap(360, 900, 96, 96, PixelFormats.Pbgra32).Render(view);
                    Assert.Equal(1, VisualTreeHelper.GetDpi(view).DpiScaleX);
                    Assert.Equal(serialized, new ProjectDocumentStore().SerializeForEvidence(project));
                    Assert.False(model.HasUnsavedChanges || model.IsRunning);
                    CaptureUnitInspector(view, 360, language, "scope-" + state + "-" + stateSuffix);
                }
                void CheckGlyph(FrameworkElement element, double size, int weight)
                {
                    var glyphs = Glyphs(element);
                    Assert.NotEmpty(glyphs);
                    Assert.All(glyphs, glyph => { Assert.Equal(size, glyph.GlyphRun.FontRenderingEmSize); Assert.Equal(weight, glyph.GlyphRun.GlyphTypeface.Weight.ToOpenTypeWeight()); });
                    var bounds = Bounds(element);
                    Assert.True(bounds.Left >= -1 && bounds.Top >= -1 && bounds.Right <= 361 && bounds.Bottom <= 901, $"{element.Name}: {bounds}");
                }
                void CheckScope(bool isUnit)
                {
                    Assert.True(Assert.IsType<StackPanel>(view.FindName("EquipmentScopeInspectorHeader")).IsVisible);
                    Assert.True(Assert.IsType<StackPanel>(view.FindName("EquipmentScopeInspector")).IsVisible);
                    Assert.False(Assert.IsType<StackPanel>(view.FindName("EquipmentOverviewInspector")).IsVisible);
                    Assert.False(Assert.IsType<TextBlock>(view.FindName("EquipmentInspectorGenericTitle")).IsVisible);
                    var title = Assert.IsType<TextBlock>(view.FindName("EquipmentScopeTitle"));
                    var identity = Assert.IsType<TextBlock>(view.FindName("EquipmentScopeIdentity"));
                    var caption = Assert.IsType<TextBlock>(view.FindName("EquipmentScopeNameCaption"));
                    var value = Assert.IsType<TextBox>(view.FindName("EquipmentScopeNameValue"));
                    Assert.Equal(isUnit ? unit.Name : station.Name, title.Text);
                    Assert.Equal(isUnit ? unit.Id : station.Id, identity.Text);
                    Assert.Equal(title.Text, value.Text);
                    Assert.Equal(language == OpenVisionLanguage.Korean ? isUnit ? "유닛 이름" : "스테이션 이름" : isUnit ? "Unit name" : "Station name", caption.Text);
                    Assert.True(value.IsReadOnly);
                    Assert.Equal(BindingMode.OneWay, BindingOperations.GetBinding(value, TextBox.TextProperty)!.Mode);
                    Assert.Equal(28, value.ActualHeight);
                    CheckGlyph(title, 13, 700); CheckGlyph(identity, 11.5, 400); CheckGlyph(caption, 12, 400); CheckGlyph(value, 12, 400);
                    var valueProvider = Assert.IsAssignableFrom<IValueProvider>(UIElementAutomationPeer.CreatePeerForElement(value)?.GetPattern(PatternInterface.Value));
                    Assert.True(valueProvider.IsReadOnly);
                    Assert.Throws<ElementNotEnabledException>(() => valueProvider.SetValue("Rejected readonly write"));
                    Assert.Equal(title.Text, value.Text);
                    if (longName)
                    {
                        value.ScrollToHorizontalOffset(double.MaxValue); ArrangeUnitInspector(view, 360);
                        Assert.True(value.HorizontalOffset > 0);
                        value.ScrollToHorizontalOffset(0); ArrangeUnitInspector(view, 360);
                    }
                    var sequenceButton = Assert.IsType<Button>(view.FindName("EquipmentUnitSequenceButton"));
                    var deleteButton = Assert.IsType<Button>(view.FindName("EquipmentUnitDeleteButton"));
                    Assert.Equal(isUnit, sequenceButton.IsVisible); Assert.Equal(isUnit, deleteButton.IsVisible);
                    if (isUnit)
                    {
                        Assert.Same(model.Navigation.OpenSequenceEditorCommand, sequenceButton.Command);
                        Assert.Same(model.RemoveEquipmentUnitCommand, deleteButton.Command);
                        Assert.Equal(unit.Id, deleteButton.CommandParameter);
                        Assert.Equal(language == OpenVisionLanguage.Korean ? "시퀀스 편집 →" : "Edit sequence →", sequenceButton.Content);
                        Assert.Equal(language == OpenVisionLanguage.Korean ? "유닛 삭제…" : "Delete unit…", deleteButton.Content);
                        Assert.True(sequenceButton.IsEnabled); Assert.True(deleteButton.IsEnabled);
                        Assert.Equal("#FFFF9AA6", ((SolidColorBrush)deleteButton.Foreground).Color.ToString());
                        CheckGlyph(sequenceButton, 13, 400); CheckGlyph(deleteButton, 13, 400);
                    }
                    var stationValue = Assert.IsType<TextBox>(view.FindName("EquipmentScopeStationValue"));
                    Assert.Equal(isUnit, stationValue.IsVisible);
                    Assert.Equal(isUnit, Assert.IsType<TextBlock>(view.FindName("EquipmentScopeHint")).IsVisible);
                    if (isUnit)
                    {
                        Assert.Equal(station.Name, stationValue.Text); Assert.True(stationValue.IsReadOnly); CheckGlyph(stationValue, 12, 400);
                        var devices = Assert.IsType<TextBlock>(view.FindName("EquipmentScopeDeviceCount"));
                        var steps = Assert.IsType<TextBlock>(view.FindName("EquipmentScopeStepCount"));
                        Assert.Equal(language == OpenVisionLanguage.Korean ? $"{expectedDeviceCount}개" : expectedDeviceCount.ToString(CultureInfo.CurrentCulture), devices.Text);
                        Assert.Equal(language == OpenVisionLanguage.Korean ? "1개" : "1", steps.Text);
                        CheckGlyph(devices, 12, 400); CheckGlyph(steps, 12, 400);
                    }
                    RecordUnitObservation(360, language, "scope-" + (isUnit ? "unit" : "station") + "-" + stateSuffix, new
                    {
                        title.Text, id = identity.Text, caption = caption.Text, name = value.Text, station = stationValue.Text,
                        readonlyValue = valueProvider.IsReadOnly, value.IsEnabled, nameHeight = value.ActualHeight, titleBounds = Bounds(title).ToString(), idBounds = Bounds(identity).ToString(),
                        glyphs = new[] { title, identity, caption }.Cast<FrameworkElement>().Append(value).Select(element => new { element.Name, Glyphs = Glyphs(element).Select(glyph => new { glyph.GlyphRun.FontRenderingEmSize, weight = glyph.GlyphRun.GlyphTypeface.Weight.ToOpenTypeWeight(), color = glyph.ForegroundBrush.ToString() }) }),
                        model.HasUnsavedChanges, model.IsRunning, windowsInput = "unverified"
                    });
                }
                model.ShowEquipmentOverviewCommand.Execute(null); Settle("overview");
                Assert.True(Assert.IsType<StackPanel>(view.FindName("EquipmentOverviewInspector")).IsVisible);
                model.ShowEquipmentUnitCommand.Execute(unit.Id); Settle("unit"); CheckScope(true);
                // Invalid public command targets must preserve the current valid scope just as the station command does.
                model.ShowEquipmentUnitCommand.Execute("missing-owned-unit"); Settle("invalid-unit"); CheckScope(true);
                model.ShowEquipmentStationCommand.Execute(station.Id); Settle("station"); CheckScope(false);
                model.Layout.Select(component.Id); Settle("component");
                Assert.False(Assert.IsType<StackPanel>(view.FindName("EquipmentScopeInspectorHeader")).IsVisible);
                Assert.False(Assert.IsType<StackPanel>(view.FindName("EquipmentScopeInspector")).IsVisible);
                Assert.False(Assert.IsType<StackPanel>(view.FindName("EquipmentOverviewInspector")).IsVisible);
                Assert.True(Assert.IsType<TextBlock>(view.FindName("EquipmentInspectorTitle")).IsVisible);
                model.ShowEquipmentUnitCommand.Execute(unit.Id); Settle("unit-return"); CheckScope(true);
                OpenVisionLanguageService.SetLanguage(language == OpenVisionLanguage.Korean ? OpenVisionLanguage.English : OpenVisionLanguage.Korean, save: false);
                ArrangeUnitInspector(view, 360);
                Assert.Equal(language == OpenVisionLanguage.Korean ? "Unit name" : "유닛 이름", Assert.IsType<TextBlock>(view.FindName("EquipmentScopeNameCaption")).Text);
                OpenVisionLanguageService.SetLanguage(language, save: false);
                model.Navigation.IsResultsWorkspace = true; Settle("other-workspace");
                Assert.False(Assert.IsType<StackPanel>(view.FindName("EquipmentScopeInspectorHeader")).IsVisible);
                Assert.False(Assert.IsType<StackPanel>(view.FindName("EquipmentScopeInspector")).IsVisible);
                model.Navigation.IsEquipmentWorkspace = true; Settle("workspace-return"); CheckScope(true);
                model.ShowEquipmentOverviewCommand.Execute(null); Settle("overview-return");
                Assert.True(Assert.IsType<StackPanel>(view.FindName("EquipmentOverviewInspector")).IsVisible);
                Assert.False(model.Layout.HasEquipmentScopeSelection);
                // Loading a different definition through the existing scope owner clears the previous scope.
                var other = new MachineProjectDocument { Id = "owned-empty-scope", Layouts = [new MachineLayoutDefinition { Id = "other-layout", Name = "Empty layout" }] };
                model.Layout.Load(other); Settle("empty-definition");
                Assert.Null(model.Layout.ActiveUnitId); Assert.Null(model.Layout.ActiveStationId); Assert.False(model.Layout.HasEquipmentScopeSelection);
                Assert.Equal(string.Empty, model.Layout.EquipmentScopeName); Assert.Equal(string.Empty, model.Layout.EquipmentScopeId);
                source.RootVisual = null;
            }
            finally { OpenVisionLanguageService.SetLanguage(previousLanguage, save: false); }
            return true;
        });
    }

    [Theory]
    [InlineData(360, OpenVisionLanguage.Korean, false)]
    [InlineData(360, OpenVisionLanguage.Korean, true)]
    [InlineData(360, OpenVisionLanguage.English, false)]
    [InlineData(360, OpenVisionLanguage.English, true)]
    [InlineData(400, OpenVisionLanguage.Korean, false)]
    [InlineData(400, OpenVisionLanguage.Korean, true)]
    [InlineData(400, OpenVisionLanguage.English, false)]
    [InlineData(400, OpenVisionLanguage.English, true)]
    public async Task InspectorR24TabsSeparateManualEntryAndKeepDraftActionsPinned(double width, OpenVisionLanguage language, bool longName)
    {
        await _ui.InvokeTaskAsync(async () =>
        {
            var previousLanguage = OpenVisionLanguageService.CurrentLanguage;
            OpenVisionLanguageService.SetLanguage(language, save: false);
            try
            {
                var project = new ProjectDocumentStore().Load(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Samples", "AutomaticTransferCell.ovmachine")));
                var stage = project.Layouts[0].Components.First(component => component.Kind == LayoutComponentKind.LinearStage);
                if (longName) stage.Name = new string(language == OpenVisionLanguage.Korean ? '축' : 'N', 60);
                project.Axes.Single(axis => axis.Id == stage.BehaviorBindingId).TeachingPositions = new() { ["검사 위치"] = 150 };
                using var vm = new MainViewModel(project);
                vm.UnsavedProjectPrompt = () => UnsavedProjectDecision.Discard;
                vm.Navigation.IsEquipmentWorkspace = true;
                vm.Layout.Select(stage.Id);
                for (var attempt = 0; attempt < 500 && !vm.CanStartManualEquipmentControl; attempt++) await Task.Delay(10);
                Assert.True(vm.CanStartManualEquipmentControl, vm.ManualEquipment.ManualControlHintText);
                var editor = Assert.IsType<LayoutComponentEditorViewModel>(vm.Layout.SelectedComponentEditor);
                var payload = new ProjectDocumentStore().SerializeForEvidence(project);
                var view = new RightToolRegionView { DataContext = vm };
                using var source = new HwndSource(new HwndSourceParameters("R24AuthoritativeInspectorFixture") { Width = (int)width, Height = 640, WindowStyle = unchecked((int)0x80000000) });
                source.RootVisual = view;
                void Settle()
                {
                    view.Measure(new Size(width, 640)); view.Arrange(new Rect(0, 0, width, 640)); view.UpdateLayout(); PumpBindings();
                    new RenderTargetBitmap((int)width, 640, 96, 96, PixelFormats.Pbgra32).Render(view);
                }
                Button ButtonFor(System.Windows.Input.ICommand command) => Descendants(view).OfType<Button>().Single(button => ReferenceEquals(button.Command, command));
                Rect Bounds(FrameworkElement element) => element.TransformToAncestor(view).TransformBounds(new Rect(element.RenderSize));
                bool Pinned(FrameworkElement element) => element.IsVisible && Bounds(element).Bottom >= 590 && Bounds(element).Bottom <= 641 && Bounds(element).Right <= width + 1;
                void Invoke(Button button)
                {
                    Assert.True(button.IsEnabled);
                    Assert.IsAssignableFrom<IInvokeProvider>(UIElementAutomationPeer.CreatePeerForElement(button)?.GetPattern(PatternInterface.Invoke)).Invoke();
                    Settle();
                }
                IEnumerable<GlyphRun> Runs(Drawing drawing)
                {
                    if (drawing is GlyphRunDrawing glyph) yield return glyph.GlyphRun;
                    if (drawing is DrawingGroup group) foreach (var child in group.Children) foreach (var run in Runs(child)) yield return run;
                }
                GlyphRun[] Glyphs(Visual element) => Descendants(element).OfType<Visual>().Select(VisualTreeHelper.GetDrawing).Where(drawing => drawing is not null).SelectMany(drawing => Runs(drawing!)).ToArray();
                Settle();
                var applyPlacement = ButtonFor(editor.ApplyEquipmentPlacementDraftCommand);
                var x = Descendants(view).OfType<TextBox>().Single(box => box.IsVisible && AutomationProperties.GetAutomationId(box) == "EquipmentCoordinateX");
                var y = Descendants(view).OfType<TextBox>().Single(box => box.IsVisible && AutomationProperties.GetAutomationId(box) == "EquipmentCoordinateY");
                Assert.True(Pinned(applyPlacement));
                Assert.True(applyPlacement.IsEnabled); // R24 permits Apply with no pending change.
                Assert.False(Assert.IsType<TextBox>(view.FindName("EquipmentDraftWidthTextBox")).IsVisible);
                Assert.False(Assert.IsType<TextBox>(view.FindName("EquipmentDraftRotationTextBox")).IsVisible);
                Assert.Equal(9, Bounds(y).Left - Bounds(x).Right, 1);
                Assert.All(new[] { x, y }, input => { Assert.Equal(28, input.ActualHeight); Assert.NotEmpty(Glyphs(input)); Assert.All(Glyphs(input), run => Assert.Equal(12, run.FontRenderingEmSize)); });
                var title = Assert.IsType<TextBlock>(view.FindName("EquipmentInspectorTitle"));
                CaptureUnitInspector(view, width, language, "r24-title-diagnostic");
                Assert.Equal(stage.Name, title.Text);
                Assert.Equal(TextTrimming.CharacterEllipsis, title.TextTrimming);
                Assert.True(title.IsVisible && Bounds(title).Right <= width);
                if (longName) Assert.Equal(60, title.Text.Length);
                else
                {
                    var id = Assert.IsType<TextBlock>(view.FindName("EquipmentInspectorId"));
                    Assert.True(id.ActualHeight < 20); Assert.InRange(Bounds(id).Left - Bounds(title).Right, 7, 9);
                }
                // The standalone HwndSource does not expose title glyph drawings; the full-shell render evidence verifies the rendered title font.
                CaptureUnitInspector(view, width, language, "r24-authoritative-placement");
                var nameInput = Assert.IsType<TextBox>(view.FindName("R24ComponentNameTextBox"));
                nameInput.SetCurrentValue(TextBox.TextProperty, "Review name"); nameInput.GetBindingExpression(TextBox.TextProperty)!.UpdateSource(); Settle();
                var draftStatus = Assert.IsType<TextBlock>(view.FindName("PlacementDraftStateText"));
                Assert.Equal(OpenVisionLanguageService.T("R24.UnappliedDraft"), draftStatus.Text); Assert.Equal("#FF986610", draftStatus.Foreground.ToString());
                Invoke(ButtonFor(editor.DiscardPlacementDraftCommand));
                x.SetCurrentValue(TextBox.TextProperty, "NaN"); x.GetBindingExpression(TextBox.TextProperty)!.UpdateSource(); Settle();
                Assert.Equal("NaN", editor.DraftEquipmentXText);
                Invoke(applyPlacement);
                Assert.True(editor.HasPlacementValidationErrors);
                Assert.Equal(OpenVisionLanguageService.T("R24.NumberRequired"), editor.PlacementXError);
                Assert.Equal("#FFBD3C50", x.BorderBrush.ToString());
                Assert.True(Pinned(applyPlacement));
                Assert.Equal(payload, new ProjectDocumentStore().SerializeForEvidence(project));
                CaptureUnitInspector(view, width, language, "r24-authoritative-placement-error");
                Invoke(ButtonFor(editor.DiscardPlacementDraftCommand));
                Assert.False(editor.HasPlacementValidationErrors); Assert.False(editor.HasPendingPlacementDraft);
                Assert.Equal(editor.DraftEquipmentXText, x.Text);
                Invoke(ButtonFor(vm.OpenEquipmentDriveTabCommand));
                var applySettings = ButtonFor(editor.ApplySettingsDraftCommand);
                Assert.True(Pinned(applySettings));
                Assert.True(applySettings.IsEnabled);
                var minimum = Descendants(view).OfType<TextBox>().Single(box => box.IsVisible && AutomationProperties.GetAutomationId(box) == "min");
                minimum.SetCurrentValue(TextBox.TextProperty, "NaN"); minimum.GetBindingExpression(TextBox.TextProperty)!.UpdateSource(); Settle();
                Assert.Equal("NaN", editor.SettingsFields.Single(field => field.Id == "min").Value);
                Invoke(applySettings);
                Assert.True(editor.HasSettingsValidationErrors);
                Assert.Equal("#FFBD3C50", minimum.BorderBrush.ToString());
                Assert.True(Pinned(applySettings));
                CaptureUnitInspector(view, width, language, "r24-authoritative-settings-error");
                Invoke(ButtonFor(editor.DiscardSettingsDraftCommand));
                Assert.False(editor.HasSettingsValidationErrors); Assert.False(editor.HasPendingSettingsDraft);
                Assert.Equal(payload, new ProjectDocumentStore().SerializeForEvidence(project));
                Invoke(ButtonFor(vm.OpenEquipmentSinglePartCheckCommand));
                Assert.True(Assert.IsType<StackPanel>(view.FindName("R24EquipmentManualPanel")).IsVisible);
                Assert.False(Assert.IsType<StackPanel>(view.FindName("ManualCommissioningEntryPanel")).IsVisible);
                Assert.False(Assert.IsType<Button>(view.FindName("EnterManualCommissioningButton")).IsVisible);
                var target = Assert.IsType<TextBox>(view.FindName("R24ManualTargetPosition"));
                Assert.Equal("150", target.Text); Assert.Equal("10", Assert.IsType<TextBox>(view.FindName("R24ManualJogDistance")).Text);
                Assert.NotEmpty(Glyphs(target)); Assert.All(Glyphs(target), run => Assert.Equal(12, run.FontRenderingEmSize));
                Assert.False(vm.IsRunning); Assert.False(vm.HasUnsavedChanges); // Opening tabs does not operate or save.
                var scope = Assert.IsType<TextBlock>(view.FindName("ManualControlScopeText"));
                Assert.True(Pinned(scope));
                CaptureUnitInspector(view, width, language, "r24-authoritative-manual");
                Invoke(ButtonFor(vm.OpenEquipmentPropertiesCommand));
                vm.IsDesignMode = false;
                for (var attempt = 0; attempt < 500 && vm.IsModeTransitioning; attempt++) await Task.Delay(10);
                Settle();
                Assert.True(x.IsVisible); Assert.False(x.IsEnabled); Assert.False(applyPlacement.IsEnabled);
                Assert.Equal(0.6, x.Opacity);
                var readonlyBitmap = new RenderTargetBitmap((int)width, 640, 96, 96, PixelFormats.Pbgra32); readonlyBitmap.Render(view);
                var readonlyPixel = new byte[4]; readonlyBitmap.CopyPixels(new Int32Rect((int)Bounds(x).Right - 20, (int)Bounds(x).Top + 12, 1, 1), readonlyPixel, 4, 0);
                Assert.InRange(readonlyPixel[2], 254, 255); Assert.InRange(readonlyPixel[1], 254, 255); Assert.InRange(readonlyPixel[0], 254, 255);
                var readOnlyLock = Assert.IsType<Border>(view.FindName("R24ReadOnlyLock"));
                Assert.True(readOnlyLock.IsVisible); Assert.Equal("#FFE5CF9A", readOnlyLock.BorderBrush.ToString()); Assert.Equal("#FFFFF8E9", readOnlyLock.Background.ToString());
                Assert.True(ButtonFor(vm.OpenEquipmentDriveTabCommand).IsEnabled);
                CaptureUnitInspector(view, width, language, "r24-authoritative-readonly");
                Assert.Equal(payload, new ProjectDocumentStore().SerializeForEvidence(project));
                await vm.RequestCloseAsync();
            }
            finally { OpenVisionLanguageService.SetLanguage(previousLanguage, save: false); }
        });
    }

    [Theory]
    [InlineData(360)]
    [InlineData(400)]
    public async Task R24InspectorAxisRejectsInvalidInputBeforeManualPreparationAndRecovers(double width)
    {
        await _ui.InvokeTaskAsync(async () =>
        {
            var store = new ProjectDocumentStore();
            var project = store.Load(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Samples", "AutomaticTransferCell.ovmachine")));
            var stage = project.Layouts[0].Components.First(component => component.Kind == LayoutComponentKind.LinearStage);
            var definition = project.Axes.Single(axis => axis.Id == stage.BehaviorBindingId);
            definition.HomePosition = 6;
            var path = Path.Combine(TestStorage.RootPath, $"v09-axis-input-{width:0}", "axis.ovmachine");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, store.Serialize(project));
            using var vm = new MainViewModel(project, path);
            vm.Navigation.IsEquipmentWorkspace = true;
            vm.Layout.Select(stage.Id);
            for (var attempt = 0; attempt < 500 && !vm.CanStartManualEquipmentControl; attempt++) await Task.Delay(10);
            Assert.True(vm.CanStartManualEquipmentControl);
            vm.OpenEquipmentSinglePartCheckCommand.Execute(null);
            var payload = store.SerializeForEvidence(project);
            var saved = File.ReadAllBytes(path);
            var editor = Assert.IsType<LayoutComponentEditorViewModel>(vm.Layout.SelectedComponentEditor);
            var view = new RightToolRegionView { DataContext = vm };
            using var source = new HwndSource(new HwndSourceParameters("R24AxisInputFixture") { Width = (int)width, Height = 740, WindowStyle = unchecked((int)0x80000000) });
            source.RootVisual = view;
            void Settle()
            {
                view.Measure(new Size(width, 740)); view.Arrange(new Rect(0, 0, width, 740)); view.UpdateLayout(); PumpBindings();
                new RenderTargetBitmap((int)width, 740, 96, 96, PixelFormats.Pbgra32).Render(view);
            }
            async Task Wait(Func<bool> done)
            {
                for (var attempt = 0; attempt < 1000 && !done(); attempt++) await Task.Delay(10);
                Assert.True(done(), $"position={vm.AxisCommissioning.InspectorCurrentPosition}; state={vm.AxisCommissioning.CurrentAxisStateText}; status={vm.StatusMessage}; target={vm.AxisCommissioning.AxisTargetPositionText}; distance={vm.AxisCommissioning.AxisRelativeDistanceText}; running={vm.IsRunning}; admission={vm.EquipmentInspectorManualCommand.CanExecute("home")}"); Settle();
            }
            Settle();
            var target = Assert.IsType<TextBox>(view.FindName("R24ManualTargetPosition"));
            var jog = Assert.IsType<TextBox>(view.FindName("R24ManualJogDistance"));
            Button Action(string action) => Descendants(view).OfType<Button>().Single(button => button.IsVisible && (string?)button.CommandParameter == action);
            void Input(TextBox box, string text)
            {
                box.SetCurrentValue(TextBox.TextProperty, text); box.GetBindingExpression(TextBox.TextProperty)!.UpdateSource(); Settle();
                Assert.Equal(text, ReferenceEquals(box, target) ? vm.AxisCommissioning.AxisTargetPositionText : vm.AxisCommissioning.AxisRelativeDistanceText);
            }
            void Reject(TextBox input, string text, params string[] actions)
            {
                Input(input, text);
                var beforeStatus = vm.StatusMessage;
                var beforeOwner = vm.SceneSnapshots.Latest!.ControlOwner;
                var beforePosition = vm.AxisCommissioning.InspectorCurrentPosition;
                var beforeDirty = vm.HasUnsavedChanges;
                var beforePlacementDraft = editor.HasPendingPlacementDraft;
                var beforeSettingsDraft = editor.HasPendingSettingsDraft;
                var beforeDraftX = editor.DraftXText;
                var beforePayload = store.SerializeForEvidence(project);
                var error = Assert.IsType<TextBlock>(view.FindName(ReferenceEquals(input, target) ? "R24ManualTargetValidationText" : "R24ManualJogValidationText"));
                Assert.True(error.IsVisible);
                Assert.Equal(ReferenceEquals(input, target) ? vm.AxisCommissioning.AxisTargetPositionValidationText : vm.AxisCommissioning.AxisRelativeDistanceValidationText, error.Text);
                Assert.NotEmpty(error.Text);
                foreach (var action in actions)
                {
                    Assert.False(Action(action).IsEnabled);
                    Assert.False(vm.EquipmentInspectorManualCommand.CanExecute(action));
                    var invoke = Assert.IsAssignableFrom<IInvokeProvider>(UIElementAutomationPeer.CreatePeerForElement(Action(action))!.GetPattern(PatternInterface.Invoke));
                    Assert.Throws<System.Windows.Automation.ElementNotEnabledException>(() => invoke.Invoke());
                    vm.EquipmentInspectorManualCommand.Execute(action); vm.EquipmentInspectorManualCommand.Execute(action); Settle();
                    Assert.Equal(beforeStatus, vm.StatusMessage);
                    Assert.Equal(beforeOwner, vm.SceneSnapshots.Latest!.ControlOwner);
                    Assert.Equal(beforePosition, vm.AxisCommissioning.InspectorCurrentPosition);
                    Assert.False(vm.IsRunning || vm.ManualEquipment.IsStartingManualControl || vm.ManualEquipment.IsManualControlAdmissionLocked);
                    Assert.Equal(stage.Id, vm.Layout.SelectedItem?.Component?.Id);
                    Assert.Equal(beforeDirty, vm.HasUnsavedChanges);
                    Assert.Equal(beforePlacementDraft, editor.HasPendingPlacementDraft);
                    Assert.Equal(beforeSettingsDraft, editor.HasPendingSettingsDraft);
                    Assert.Equal(beforeDraftX, editor.DraftXText);
                    Assert.Equal(beforePayload, store.SerializeForEvidence(project));
                    Assert.Equal(saved, File.ReadAllBytes(path));
                }
            }
            foreach (var text in new[] { "", " ", "bad", "NaN", "Infinity", "-Infinity", "1e309", "0", "-0" }) Reject(jog, text, "jog+", "jog-");
            foreach (var text in new[] { "", "bad", "NaN", "Infinity", "-Infinity", "1e309", ((definition.SoftLimitMin ?? 0) - 1).ToString(System.Globalization.CultureInfo.InvariantCulture), ((definition.SoftLimitMax ?? 300) + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) }) Reject(target, text, "target");
            var promptCount = 0;
            vm.PlacementDraftPrompt = () => { promptCount++; return PlacementDraftDecision.Apply; };
            editor.DraftXText = "25";
            Assert.True(editor.HasPendingPlacementDraft);
            Reject(jog, "NaN", "jog+"); Reject(target, "NaN", "target");
            Assert.Equal(0, promptCount);
            editor.DiscardPlacementDraftCommand.Execute(null);
            editor.DraftXText = "25";
            editor.ApplyEquipmentPlacementDraftCommand.Execute(null);
            Assert.True(vm.HasUnsavedChanges);
            Reject(jog, "NaN", "jog-"); Reject(target, "NaN", "target");
            Assert.Equal(0, promptCount);
            await vm.SaveProjectAsync(path);
            payload = store.SerializeForEvidence(project); saved = File.ReadAllBytes(path);
            Assert.False(vm.HasUnsavedChanges);
            foreach (var value in new[] { definition.SoftLimitMin ?? 0, definition.SoftLimitMax ?? 300, 0d })
            {
                Input(target, value.ToString(System.Globalization.CultureInfo.InvariantCulture));
                Assert.True(Action("target").IsEnabled);
                Assert.False(Assert.IsType<TextBlock>(view.FindName("R24ManualTargetValidationText")).IsVisible);
            }
            Input(target, " +9e0 "); Input(jog, " -2 ");
            Assert.True(Action("target").IsEnabled && Action("jog+").IsEnabled && Action("jog-").IsEnabled);
            async Task Invoke(string action, double expected)
            {
                Assert.IsAssignableFrom<IInvokeProvider>(UIElementAutomationPeer.CreatePeerForElement(Action(action))!.GetPattern(PatternInterface.Invoke)).Invoke();
                // Match the existing axis simulation tests at six decimal places after motion is idle.
                await Wait(() => vm.EquipmentInspectorManualCommand.CanExecute("home") && vm.SceneSnapshots.Latest!.Axes.Single(axis => axis.Id == definition.Id).State == OpenVisionLab.Machine.Simulation.Axis.AxisState.Idle && Math.Round(vm.AxisCommissioning.InspectorCurrentPosition ?? double.NaN, 6) == expected);
                Assert.Equal(expected, vm.AxisCommissioning.InspectorCurrentPosition!.Value, 6);
                var position = Descendants(view).OfType<TextBlock>().Single(text => text.IsVisible && BindingOperations.GetBindingExpression(text, TextBlock.TextProperty)?.ParentBinding.Path.Path == nameof(MainViewModel.EquipmentInspectorPositionText));
                Assert.Equal(vm.EquipmentInspectorPositionText, position.Text);
            }
            await Invoke("target", 9); await Invoke("home", 6);
            vm.EquipmentInspectorManualCommand.Execute("jog+");
            Assert.False(vm.EquipmentInspectorManualCommand.CanExecute("jog+"));
            vm.EquipmentInspectorManualCommand.Execute("jog+");
            await Wait(() => vm.EquipmentInspectorManualCommand.CanExecute("home") && vm.SceneSnapshots.Latest!.Axes.Single(axis => axis.Id == definition.Id).State == OpenVisionLab.Machine.Simulation.Axis.AxisState.Idle && Math.Round(vm.AxisCommissioning.InspectorCurrentPosition ?? double.NaN, 6) == 8);
            Assert.Equal(8, vm.AxisCommissioning.InspectorCurrentPosition!.Value, 6);
            await Invoke("jog-", 6);
            vm.AxisCommissioning.AxisTargetPositionText = "11"; Settle(); Assert.Equal("11", target.Text);
            Assert.Equal(payload, store.SerializeForEvidence(project)); Assert.Equal(saved, File.ReadAllBytes(path));
            Assert.False(vm.HasUnsavedChanges);
            await vm.RequestCloseAsync();
        });
    }

    [Fact]
    public async Task R24InspectorOutputInvokesActualManualRuntimeWithoutSeparateStartOrRecipeMutation()
    {
        await _ui.InvokeTaskAsync(async () =>
        {
            var project = new ProjectDocumentStore().Load(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Samples", "AutomaticTransferCell.ovmachine")));
            project.Channels.Add(new ChannelDefinition { Id = "r24-test.out", Name = "Test output", Kind = ChannelKind.DigitalOutput });
            var component = new LayoutComponentDefinition { Id = "r24-test", Name = "Test I/O", Kind = LayoutComponentKind.DigitalIo, BehaviorBindingId = "r24-test.out", Size = new Size2D { Width = 24, Height = 24 } };
            project.Layouts[0].Components.Add(component);
            using var vm = new MainViewModel(project);
            vm.UnsavedProjectPrompt = () => UnsavedProjectDecision.Discard;
            vm.Navigation.IsEquipmentWorkspace = true; vm.Layout.Select(component.Id);
            for (var attempt = 0; attempt < 500 && !vm.CanStartManualEquipmentControl; attempt++) await Task.Delay(10);
            vm.OpenEquipmentSinglePartCheckCommand.Execute(null);
            var payload = new ProjectDocumentStore().SerializeForEvidence(project);
            var view = new RightToolRegionView { DataContext = vm };
            using var source = new HwndSource(new HwndSourceParameters("R24ManualOutputFixture") { Width = 400, Height = 640, WindowStyle = unchecked((int)0x80000000) });
            source.RootVisual = view; view.Measure(new Size(400, 640)); view.Arrange(new Rect(0, 0, 400, 640)); view.UpdateLayout(); PumpBindings();
            var output = Descendants(view).OfType<Button>().Single(button => button.IsVisible && (string?)button.CommandParameter == "ioOn");
            Assert.True(output.IsEnabled);
            var invoke = Assert.IsAssignableFrom<IInvokeProvider>(UIElementAutomationPeer.CreatePeerForElement(output)?.GetPattern(PatternInterface.Invoke));
            invoke.Invoke(); invoke.Invoke(); // AsyncRelayCommand must serialize repeated requests.
            for (var attempt = 0; attempt < 500 && vm.EquipmentInspectorManualStatusText != "ON"; attempt++) await Task.Delay(10);
            Assert.Equal("ON", vm.EquipmentInspectorManualStatusText);
            Assert.True(vm.SceneSnapshots.Latest!.Signals.Single(signal => signal.Id == component.BehaviorBindingId).Value);
            Assert.Equal(payload, new ProjectDocumentStore().SerializeForEvidence(project));
            Assert.False(vm.HasUnsavedChanges);
            for (var attempt = 0; attempt < 500 && !vm.EquipmentInspectorManualCommand.CanExecute("ioOff"); attempt++) await Task.Delay(10);
            vm.EquipmentInspectorManualCommand.Execute("ioOff");
            for (var attempt = 0; attempt < 500 && vm.EquipmentInspectorManualStatusText != "OFF"; attempt++) await Task.Delay(10);
            Assert.Equal("OFF", vm.EquipmentInspectorManualStatusText);
            CaptureUnitInspector(view, 400, OpenVisionLanguageService.CurrentLanguage, "r24-authoritative-manual-output-recovered");
            await vm.RequestCloseAsync();
        });
    }

    [Fact]
    public async Task UnitAssignmentComboBoxUpdatesSelectedComponentThroughBinding()
    {
        var assignment = await _ui.InvokeAsync(() =>
        {
            var project = CreateProject();
            using var layout = new MachineLayoutViewModel();
            layout.Load(project);
            layout.Select("camera-01");
            var editor = Assert.IsType<LayoutComponentEditorViewModel>(layout.SelectedComponentEditor);
            using var navigation = new ShellNavigationViewModel(false, () => false, () => false,
                () => Task.CompletedTask, () => { }, _ => { });
            navigation.SelectedDocumentTabIndex = 0;

            var view = new RightToolRegionView
            {
                DataContext = new InspectorDataContext(layout, navigation, IsRunMode: false)
            };
            Arrange(view);

            var comboBox = Descendants(view).OfType<ComboBox>().Single(candidate =>
                ReferenceEquals(candidate.ItemsSource, editor.UnitOptions));
            var initialValue = comboBox.SelectedValue as string;
            comboBox.SelectedValue = "unit-camera";
            Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.DataBind, new Action(() => { }));

            var roundTripRoot = Path.Combine(TestStorage.RootPath, "r19-priority-12-unit-assignment", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(roundTripRoot);
            var projectPath = Path.Combine(roundTripRoot, "unit-assignment.ovmachine");
            var fileStore = new ProjectDocumentFileStore();
            fileStore.SaveAsync(project, projectPath).GetAwaiter().GetResult();
            Assert.True(File.Exists(projectPath));
            var reopenedProject = fileStore.Load(projectPath);
            using var reopenedLayout = new MachineLayoutViewModel();
            reopenedLayout.Load(reopenedProject);
            reopenedLayout.Select("camera-01");
            var reopenedEditor = Assert.IsType<LayoutComponentEditorViewModel>(reopenedLayout.SelectedComponentEditor);
            var reopenedView = new RightToolRegionView
            {
                DataContext = new InspectorDataContext(reopenedLayout, navigation, IsRunMode: false)
            };
            Arrange(reopenedView);
            var reopenedComboBox = Descendants(reopenedView).OfType<ComboBox>().Single(candidate =>
                ReferenceEquals(candidate.ItemsSource, reopenedEditor.UnitOptions));

            return (
                InitialValue: initialValue,
                EditorUnitId: editor.UnitId,
                ComponentUnitId: layout.SelectedItem?.Component?.UnitId,
                SavedProjectPath: projectPath,
                ReopenedEditorUnitId: reopenedEditor.UnitId,
                ReopenedComboBoxValue: reopenedComboBox.SelectedValue as string);
        });

        Assert.Equal("unit-press", assignment.InitialValue);
        Assert.Equal("unit-camera", assignment.EditorUnitId);
        Assert.Equal("unit-camera", assignment.ComponentUnitId);
        Assert.True(File.Exists(assignment.SavedProjectPath));
        Assert.Equal("unit-camera", assignment.ReopenedEditorUnitId);
        Assert.Equal("unit-camera", assignment.ReopenedComboBoxValue);
    }

    [Theory]
    [InlineData(300, OpenVisionLanguage.Korean)]
    [InlineData(420, OpenVisionLanguage.English)]
    public async Task ProductInspectorConveyorAndVerdictSelectionRoundTrips(double width, OpenVisionLanguage language)
    {
        var result = await _ui.InvokeTaskAsync(async () =>
        {
            var previousLanguage = OpenVisionLanguageService.CurrentLanguage;
            try
            {
                OpenVisionLanguageService.SetLanguage(language, save: false);
                var project = CreateLongProductProject(language);
                using var vm = new MainViewModel(project);
                vm.IsDesignMode = true;
                vm.Layout.Select("workpiece-1");
                vm.OpenEquipmentDriveTabCommand.Execute(null);
                var view = new RightToolRegionView { DataContext = vm };
                using var source = new HwndSource(new HwndSourceParameters("ProductInspectorSelectionFixture")
                {
                    Width = (int)width, Height = 900, WindowStyle = unchecked((int)0x80000000)
                });
                source.RootVisual = view;
                ArrangeUnitInspector(view, width);
                var editor = Assert.IsType<LayoutComponentEditorViewModel>(vm.Layout.SelectedComponentEditor);
                var carrier = DriveCombo(view, nameof(editor.WorkpieceConveyorComponentId));
                var state = DriveCombo(view, nameof(editor.WorkpieceInspectionStateId));
                var section = Descendants(view).OfType<StackPanel>().Single(panel =>
                    panel.GetBindingExpression(UIElement.VisibilityProperty)?.ParentBinding.Path.Path == "ShowWorkpieceProperties");
                var heading = Descendants(section).OfType<TextBlock>().First().Text;
                var labels = state.Items.OfType<LayoutPropertyOption>().Select(option => option.DisplayName).ToArray();
                var namesCorrect = AutomationProperties.GetName(carrier) == OpenVisionLanguageService.T("Properties.Conveyor") &&
                    AutomationProperties.GetName(state) == OpenVisionLanguageService.T("Properties.InspectionState") &&
                    AutomationProperties.GetName(Descendants(section).OfType<TextBox>().Single(box => box.GetBindingExpression(TextBox.TextProperty)?.ParentBinding.Path.Path == "WorkpieceType")) == OpenVisionLanguageService.T("Properties.WorkpieceType");
                var captionsCorrect = namesCorrect && heading == (language == OpenVisionLanguage.Korean ? "제품" : "Workpiece") &&
                    labels.SequenceEqual(language == OpenVisionLanguage.Korean ? ["검사 대기", "OK", "NG", "건너뜀"] : new[] { "Pending", "Passed", "Failed", "Skipped" });
                var selectedFit = true;
                var selections = new List<object>();
                foreach (var id in new[] { "conveyor-2", "conveyor-1" })
                {
                    carrier.SetCurrentValue(Selector.SelectedValueProperty, id);
                    carrier.GetBindingExpression(Selector.SelectedValueProperty)!.UpdateSource();
                    PumpBindings();
                    Assert.Equal(id, editor.WorkpieceConveyorComponentId);
                    carrier.BringIntoView();
                    PumpBindings();
                    var option = Assert.IsType<LayoutPropertyOption>(carrier.SelectedItem);
                    var display = Descendants(carrier).OfType<TextBlock>().Single(candidate => candidate.Text == option.DisplayName);
                    var fits = UnitTextFits(display, carrier);
                    selectedFit &= fits;
                    selections.Add(new { id, fits, textWidth = display.ActualWidth, comboWidth = carrier.ActualWidth });
                    CaptureUnitInspector(view, width, language, $"product-{id}-selected");
                }
                var original = System.Text.Json.JsonSerializer.Serialize(project);
                carrier.IsDropDownOpen = true;
                var popupFit = false;
                try
                {
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    PumpBindings();
                    var popup = Descendants(carrier).OfType<Popup>().Single();
                    var child = Assert.IsAssignableFrom<FrameworkElement>(popup.Child);
                    var displays = Descendants(child).OfType<TextBlock>().Where(candidate =>
                        carrier.Items.OfType<LayoutPropertyOption>().Any(option => option.DisplayName == candidate.Text)).ToArray();
                    Assert.Equal(2, displays.Length);
                    popupFit = displays.All(display =>
                    {
                        DependencyObject? parent = display;
                        while (parent is not null && parent is not ComboBoxItem) parent = VisualTreeHelper.GetParent(parent);
                        return parent is ComboBoxItem item && UnitTextFits(display, item);
                    });
                    CaptureUnitInspector(child, width, language, "popup-product-carriers");
                    carrier.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.Escape) { RoutedEvent = Keyboard.KeyDownEvent });
                    PumpBindings();
                    Assert.False(carrier.IsDropDownOpen);
                    Assert.Equal(original, System.Text.Json.JsonSerializer.Serialize(project));
                }
                finally { carrier.IsDropDownOpen = false; }
                foreach (var id in new[] { "Failed", "Skipped", "Pending", "Passed" })
                {
                    state.SetCurrentValue(Selector.SelectedValueProperty, id);
                    state.GetBindingExpression(Selector.SelectedValueProperty)!.UpdateSource();
                    PumpBindings();
                    Assert.Equal(id, editor.WorkpieceInspectionStateId);
                    Assert.Equal(id, project.Devices.Single(device => device.Kind == DeviceKind.Workpiece).Workpiece!.InspectionState.ToString());
                    state.BringIntoView();
                    PumpBindings();
                    CaptureUnitInspector(view, width, language, $"product-state-{id}");
                }
                var snapshot = System.Text.Json.JsonSerializer.Serialize(project);
                OpenVisionLanguageService.SetLanguage(language == OpenVisionLanguage.Korean ? OpenVisionLanguage.English : OpenVisionLanguage.Korean, save: false);
                PumpBindings();
                Assert.Equal("Passed", state.SelectedValue);
                Assert.Equal(snapshot, System.Text.Json.JsonSerializer.Serialize(project));
                OpenVisionLanguageService.SetLanguage(language, save: false);
                PumpBindings();
                Assert.Equal("Passed", state.SelectedValue);
                Assert.Equal(snapshot, System.Text.Json.JsonSerializer.Serialize(project));
                Assert.Empty(new MachineProjectLayoutValidator().Validate(project).Errors);
                Assert.True(vm.IsDesignMode);
                Assert.False(vm.IsDryRunPlaybackActive);
                RecordUnitObservation(width, language, "product-selection", new { selections, selectedFit, popupFit, heading, labels, captionsCorrect, escapedWithoutMutation = true, localePreservesPayload = true, appRun = 0 });
                return (selectedFit, popupFit, captionsCorrect);
            }
            finally { OpenVisionLanguageService.SetLanguage(previousLanguage, save: false); }
        });
        Assert.True(result.selectedFit, "Required product conveyor identity must fit its selected-value control.");
        Assert.True(result.popupFit);
        Assert.True(result.captionsCorrect, "Product heading and initial verdict labels must use the confirmed glossary without changing English.");
    }

    [Theory]
    [InlineData(300, OpenVisionLanguage.Korean)]
    [InlineData(420, OpenVisionLanguage.English)]
    public async Task ProductInspectorValidationRecoversAndReopens(double width, OpenVisionLanguage language)
    {
        await _ui.InvokeTaskAsync(async () =>
        {
            var previousLanguage = OpenVisionLanguageService.CurrentLanguage;
            try
            {
                OpenVisionLanguageService.SetLanguage(language, save: false);
                var project = CreateLongProductProject(language);
                using var vm = new MainViewModel(project);
                vm.IsDesignMode = true;
                vm.Layout.Select("workpiece-1");
                vm.OpenEquipmentDriveTabCommand.Execute(null);
                var view = new RightToolRegionView { DataContext = vm };
                using var source = new HwndSource(new HwndSourceParameters("ProductInspectorRecoveryFixture")
                {
                    Width = (int)width, Height = 900, WindowStyle = unchecked((int)0x80000000)
                });
                source.RootVisual = view;
                ArrangeUnitInspector(view, width);
                var editor = Assert.IsType<LayoutComponentEditorViewModel>(vm.Layout.SelectedComponentEditor);
                var model = Descendants(view).OfType<TextBox>().Single(box => box.GetBindingExpression(TextBox.TextProperty)?.ParentBinding.Path.Path == "WorkpieceType");
                var carrier = DriveCombo(view, nameof(editor.WorkpieceConveyorComponentId));
                var state = DriveCombo(view, nameof(editor.WorkpieceInspectionStateId));
                var expectedModel = "Product - " + new string('M', 70);
                var cases = new (MachineProjectLayoutValidationErrorCode Code, Action Setup, string Correction)[]
                {
                    (MachineProjectLayoutValidationErrorCode.WorkpieceTypeRequired, () => editor.WorkpieceType = " ", "제품 모델"),
                    (MachineProjectLayoutValidationErrorCode.WorkpieceConveyorComponentRequired, () => editor.WorkpieceConveyorComponentId = "", "이송 컨베이어"),
                    (MachineProjectLayoutValidationErrorCode.WorkpieceConveyorComponentNotFound, () => editor.WorkpieceConveyorComponentId = "missing.carrier-" + new string('M', 65), "이송 컨베이어"),
                    (MachineProjectLayoutValidationErrorCode.WorkpieceCarrierMustBeConveyor, () => editor.WorkpieceConveyorComponentId = "frame-1", "같은 레이아웃"),
                    (MachineProjectLayoutValidationErrorCode.WorkpieceInspectionStateInvalid, () => editor.WorkpieceInspectionStateId = "99", "검사 상태")
                };
                var observations = new List<object>();
                var instructionsCorrect = true;
                foreach (var testCase in cases)
                {
                    testCase.Setup();
                    PumpBindings();
                    var error = Assert.Single(new MachineProjectLayoutValidator().Validate(project).Errors, candidate => candidate.Code == testCase.Code && candidate.ComponentId == "workpiece-1");
                    var validation = Descendants(view).OfType<TextBlock>().Single(block => block.Name == "PropertyValidationMessage");
                    Assert.True(editor.HasValidationErrors && validation.IsVisible);
                    Assert.Equal(editor.ValidationMessage, validation.Text);
                    Assert.Contains(error.Message, validation.Text);
                    Assert.True(UnitTextFits(validation, (FrameworkElement)VisualTreeHelper.GetParent(validation)));
                    var corrected = language != OpenVisionLanguage.Korean || validation.Text.Contains(testCase.Correction, StringComparison.Ordinal);
                    instructionsCorrect &= corrected;
                    validation.BringIntoView();
                    PumpBindings();
                    CaptureUnitInspector(view, width, language, $"product-invalid-{testCase.Code}");
                    observations.Add(new { code = testCase.Code.ToString(), corrected, coreDiagnostic = error.Message, rendered = validation.Text, fits = UnitTextFits(validation, (FrameworkElement)VisualTreeHelper.GetParent(validation)) });
                    model.SetCurrentValue(TextBox.TextProperty, expectedModel);
                    model.GetBindingExpression(TextBox.TextProperty)!.UpdateSource();
                    carrier.SetCurrentValue(Selector.SelectedValueProperty, "conveyor-2");
                    carrier.GetBindingExpression(Selector.SelectedValueProperty)!.UpdateSource();
                    state.SetCurrentValue(Selector.SelectedValueProperty, "Passed");
                    state.GetBindingExpression(Selector.SelectedValueProperty)!.UpdateSource();
                    PumpBindings();
                    Assert.False(editor.HasValidationErrors);
                    Assert.Empty(new MachineProjectLayoutValidator().Validate(project).Errors);
                    Assert.Equal(expectedModel, editor.WorkpieceType);
                    Assert.Equal("conveyor-2", editor.WorkpieceConveyorComponentId);
                    Assert.Equal("Passed", editor.WorkpieceInspectionStateId);
                }
                var directory = Path.Combine(TestStorage.RootPath, "r24-b02-product-diagnostics", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(directory);
                var file = Path.Combine(directory, "product-settings.ovmachine");
                var store = new ProjectDocumentFileStore();
                await store.SaveAsync(project, file);
                var savedBytes = File.ReadAllBytes(file);
                var reopened = store.Load(file);
                using var fresh = new MainViewModel(reopened);
                fresh.IsDesignMode = true;
                fresh.Layout.Select("workpiece-1");
                fresh.OpenEquipmentDriveTabCommand.Execute(null);
                var freshView = new RightToolRegionView { DataContext = fresh };
                source.RootVisual = freshView;
                ArrangeUnitInspector(freshView, width);
                var freshEditor = Assert.IsType<LayoutComponentEditorViewModel>(fresh.Layout.SelectedComponentEditor);
                Assert.Equal(expectedModel, freshEditor.WorkpieceType);
                Assert.Equal("conveyor-2", DriveCombo(freshView, nameof(editor.WorkpieceConveyorComponentId)).SelectedValue);
                Assert.Equal("Passed", DriveCombo(freshView, nameof(editor.WorkpieceInspectionStateId)).SelectedValue);
                Assert.Empty(new MachineProjectLayoutValidator().Validate(reopened).Errors);
                Assert.Equal(savedBytes, File.ReadAllBytes(file));
                Assert.True(vm.IsDesignMode && fresh.IsDesignMode);
                Assert.False(vm.IsDryRunPlaybackActive || fresh.IsDryRunPlaybackActive);
                CaptureUnitInspector(freshView, width, language, "product-saved-reopened");
                RecordUnitObservation(width, language, "product-recovery", new { observations, instructionsCorrect, file, modelLength = expectedModel.Length, savedCarrier = freshEditor.WorkpieceConveyorComponentId, savedState = freshEditor.WorkpieceInspectionStateId, explicitFileSave = true, reopenedWithoutRun = true });
                Assert.True(instructionsCorrect, "Korean product validation must include a corrective instruction while retaining the original Core diagnostic.");
            }
            finally { OpenVisionLanguageService.SetLanguage(previousLanguage, save: false); }
        });
    }

    [Theory]
    [InlineData(300, OpenVisionLanguage.Korean)]
    [InlineData(420, OpenVisionLanguage.English)]
    public async Task ConnectionDiagnosticsGiveLocalizedCorrectionAndPreserveCoreDetail(double width, OpenVisionLanguage language)
    {
        await _ui.InvokeTaskAsync(async () =>
        {
            var previousLanguage = OpenVisionLanguageService.CurrentLanguage;
            try
            {
                OpenVisionLanguageService.SetLanguage(language, save: false);
                var project = CreateLongDriveProject(language);
                using var vm = new MainViewModel(project);
                vm.IsDesignMode = true;
                var view = new RightToolRegionView { DataContext = vm };
                using var source = new HwndSource(new HwndSourceParameters("ConnectionDiagnosticFixture")
                {
                    Width = (int)width, Height = 900, WindowStyle = unchecked((int)0x80000000)
                });
                source.RootVisual = view;
                var instructionsCorrect = true;
                var observations = new List<object>();
                var glyphsFit = true;
                foreach (var componentId in new[] { "sensor-1", "cylinder-1", "conveyor-1" })
                {
                    vm.Layout.Select(componentId);
                    vm.OpenEquipmentDriveTabCommand.Execute(null);
                    ArrangeUnitInspector(view, width);
                    var editor = Assert.IsType<LayoutComponentEditorViewModel>(vm.Layout.SelectedComponentEditor);
                    var combo = DriveCombo(view, PrimaryDriveProperty(componentId));
                    var original = Assert.IsType<string>(combo.SelectedValue);
                    foreach (var invalid in new[] { componentId == "sensor-1" ? "do.cycle-active" : "di.station-present", "missing.signal", "" })
                    {
                        SetPrimaryDriveValue(editor, componentId, invalid);
                        PumpBindings();
                        var error = Assert.Single(new MachineProjectLayoutValidator().Validate(project).Errors, candidate => candidate.ComponentId == componentId);
                        var validation = Descendants(view).OfType<TextBlock>().Single(block => block.Name == "PropertyValidationMessage");
                        Assert.True(validation.IsVisible);
                        Assert.Contains(error.Message, validation.Text);
                        if (language == OpenVisionLanguage.English) Assert.Equal(OpenVisionLanguageService.T("Inspector.PropertiesInvalid") + " " + error.Message, validation.Text);
                        var corrected = language != OpenVisionLanguage.Korean || validation.Text.Contains(invalid == "missing.signal" ? "채널" : componentId == "sensor-1" ? "디지털 입력" : "디지털 출력", StringComparison.Ordinal);
                        instructionsCorrect &= corrected;
                        validation.BringIntoView();
                        PumpBindings();
                        var fits = UnitTextFits(validation, (FrameworkElement)VisualTreeHelper.GetParent(validation));
                        glyphsFit &= fits;
                        var glyph = new FormattedText(validation.Text, CultureInfo.CurrentUICulture, validation.FlowDirection,
                            new Typeface(validation.FontFamily, validation.FontStyle, validation.FontWeight, validation.FontStretch), validation.FontSize,
                            validation.Foreground, VisualTreeHelper.GetDpi(validation).PixelsPerDip) { MaxTextWidth = validation.ActualWidth };
                        observations.Add(new { componentId, invalid, code = error.Code.ToString(), corrected, fits, rendered = validation.Text,
                            textWidth = validation.ActualWidth, textHeight = validation.ActualHeight, glyphWidth = glyph.WidthIncludingTrailingWhitespace,
                            glyphHeight = glyph.Height, visibleGlyphWidth = glyph.Width, actualFormattingMode = TextOptions.GetTextFormattingMode(validation).ToString() });
                        CaptureUnitInspector(view, width, language, $"diagnostic-{componentId}-{error.Code}");
                        var snapshot = System.Text.Json.JsonSerializer.Serialize(project);
                        OpenVisionLanguageService.SetLanguage(language == OpenVisionLanguage.Korean ? OpenVisionLanguage.English : OpenVisionLanguage.Korean, save: false);
                        PumpBindings();
                        Assert.Equal(snapshot, System.Text.Json.JsonSerializer.Serialize(project));
                        OpenVisionLanguageService.SetLanguage(language, save: false);
                        PumpBindings();
                        combo.SetCurrentValue(Selector.SelectedValueProperty, original);
                        combo.GetBindingExpression(Selector.SelectedValueProperty)!.UpdateSource();
                        PumpBindings();
                        Assert.False(editor.HasValidationErrors);
                        Assert.Empty(new MachineProjectLayoutValidator().Validate(project).Errors);
                    }
                }
                RecordUnitObservation(width, language, "connection-diagnostics", new { observations, instructionsCorrect, glyphsFit, EnglishExactCoreDetailPreserved = true, localePreservesPayload = true, appRun = 0 });
                Assert.True(instructionsCorrect, "Connection validation must explain the corrective channel kind in Korean.");
                Assert.True(glyphsFit, "Rendered error glyphs must fit their parent.");
                await Task.CompletedTask;
            }
            finally { OpenVisionLanguageService.SetLanguage(previousLanguage, save: false); }
        });
    }

    [Theory]
    [InlineData("sensor-1", 300, OpenVisionLanguage.Korean)]
    [InlineData("sensor-1", 420, OpenVisionLanguage.English)]
    [InlineData("cylinder-1", 300, OpenVisionLanguage.Korean)]
    [InlineData("cylinder-1", 420, OpenVisionLanguage.English)]
    [InlineData("conveyor-1", 300, OpenVisionLanguage.Korean)]
    [InlineData("conveyor-1", 420, OpenVisionLanguage.English)]
    [InlineData("stage-1", 300, OpenVisionLanguage.Korean)]
    [InlineData("stage-1", 420, OpenVisionLanguage.English)]
    public async Task DriveBindingLongSelectionAndPopupRemainReadable(string componentId, double width, OpenVisionLanguage language)
    {
        var result = await _ui.InvokeTaskAsync(async () =>
        {
            var previousLanguage = OpenVisionLanguageService.CurrentLanguage;
            try
            {
                OpenVisionLanguageService.SetLanguage(language, save: false);
                var project = CreateLongDriveProject(language);
                using var vm = new MainViewModel(project);
                vm.IsDesignMode = true;
                vm.Layout.Select(componentId);
                vm.OpenEquipmentDriveTabCommand.Execute(null);
                var editor = Assert.IsType<LayoutComponentEditorViewModel>(vm.Layout.SelectedComponentEditor);
                var view = new RightToolRegionView { DataContext = vm };
                using var source = new HwndSource(new HwndSourceParameters("DriveBindingPopupFixture")
                {
                    Width = (int)width, Height = 900, WindowStyle = unchecked((int)0x80000000)
                });
                source.RootVisual = view;
                ArrangeUnitInspector(view, width);
                var original = System.Text.Json.JsonSerializer.Serialize(project);
                var selections = new List<object>();
                var allSelectedFit = true;
                foreach (var property in DriveSelectionProperties(componentId))
                {
                    var combo = DriveCombo(view, property);
                    combo.BringIntoView();
                    PumpBindings();
                    var option = Assert.IsType<LayoutPropertyOption>(combo.SelectedItem);
                    var text = Descendants(combo).OfType<TextBlock>().Single(candidate => candidate.Text == option.DisplayName);
                    var fits = UnitTextFits(text, combo);
                    allSelectedFit &= fits;
                    selections.Add(new { property, option.Id, fits, comboWidth = combo.ActualWidth, textWidth = text.ActualWidth });
                    CaptureUnitInspector(view, width, language, $"drive-{componentId}-{property}-selected");
                }
                Assert.Equal(project.Channels.Where(channel => channel.Kind == ChannelKind.DigitalInput).Select(channel => channel.Id), editor.DigitalInputOptions.Select(option => option.Id));
                Assert.Equal(project.Channels.Where(channel => channel.Kind == ChannelKind.DigitalOutput).Select(channel => channel.Id), editor.DigitalOutputOptions.Select(option => option.Id));
                Assert.DoesNotContain(editor.TargetComponentOptions, option => option.Id == componentId);
                var primary = DriveCombo(view, PrimaryDriveProperty(componentId));
                primary.BringIntoView();
                PumpBindings();
                primary.IsDropDownOpen = true;
                try
                {
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    PumpBindings();
                    var popup = Descendants(primary).OfType<Popup>().Single();
                    var child = Assert.IsAssignableFrom<FrameworkElement>(popup.Child);
                    Assert.True(popup.IsOpen);
                    var texts = Descendants(child).OfType<TextBlock>().Where(text => primary.Items.OfType<LayoutPropertyOption>().Any(option => option.DisplayName == text.Text)).ToArray();
                    Assert.NotEmpty(texts);
                    var popupFits = texts.All(text =>
                    {
                        DependencyObject? parent = text;
                        while (parent is not null && parent is not ComboBoxItem) parent = VisualTreeHelper.GetParent(parent);
                        return parent is ComboBoxItem item && UnitTextFits(text, item);
                    });
                    CaptureUnitInspector(child, width, language, $"popup-drive-{componentId}");
                    primary.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.Escape) { RoutedEvent = Keyboard.KeyDownEvent });
                    PumpBindings();
                    var cancelled = !primary.IsDropDownOpen;
                    var unchanged = original == System.Text.Json.JsonSerializer.Serialize(project);
                    RecordUnitObservation(width, language, $"drive-{componentId}-long", new { selections, allSelectedFit, popupFits, cancelled, unchanged, vm.IsDesignMode, vm.IsDryRunPlaybackActive });
                    return (allSelectedFit, popupFits, cancelled, unchanged);
                }
                finally { primary.IsDropDownOpen = false; }
            }
            finally { OpenVisionLanguageService.SetLanguage(previousLanguage, save: false); }
        });
        Assert.True(result.allSelectedFit, "Required drive connection names/IDs must fit their selection controls.");
        Assert.True(result.popupFits, "Drive connection dropdown glyphs must fit their item containers.");
        Assert.True(result.cancelled && result.unchanged);
    }

    [Theory]
    [InlineData(300, OpenVisionLanguage.Korean)]
    [InlineData(420, OpenVisionLanguage.English)]
    public async Task DriveBindingInvalidConnectionRecoversAndReopens(double width, OpenVisionLanguage language)
    {
        await _ui.InvokeTaskAsync(async () =>
        {
            var previousLanguage = OpenVisionLanguageService.CurrentLanguage;
            try
            {
                OpenVisionLanguageService.SetLanguage(language, save: false);
                var project = CreateLongDriveProject(language);
                using var vm = new MainViewModel(project);
                vm.IsDesignMode = true;
                var view = new RightToolRegionView { DataContext = vm };
                using var source = new HwndSource(new HwndSourceParameters("DriveBindingRecoveryFixture")
                {
                    Width = (int)width, Height = 900, WindowStyle = unchecked((int)0x80000000)
                });
                source.RootVisual = view;
                var hiddenValidation = new List<string>();
                foreach (var componentId in new[] { "sensor-1", "cylinder-1", "conveyor-1" })
                {
                    vm.Layout.Select(componentId);
                    vm.OpenEquipmentDriveTabCommand.Execute(null);
                    ArrangeUnitInspector(view, width);
                    var editor = Assert.IsType<LayoutComponentEditorViewModel>(vm.Layout.SelectedComponentEditor);
                    var combo = DriveCombo(view, PrimaryDriveProperty(componentId));
                    var originalValue = Assert.IsType<string>(combo.SelectedValue);
                    var wrongKind = componentId == "sensor-1" ? "do.cycle-active" : "di.station-present";
                    SetPrimaryDriveValue(editor, componentId, wrongKind);
                    PumpBindings();
                    Assert.True(editor.HasValidationErrors);
                    var expectedCode = componentId switch
                    {
                        "sensor-1" => MachineProjectLayoutValidationErrorCode.SensorOutputChannelMustBeDigitalInput,
                        "cylinder-1" => MachineProjectLayoutValidationErrorCode.CylinderCommandMustBeDigitalOutput,
                        _ => MachineProjectLayoutValidationErrorCode.ConveyorCommandMustBeDigitalOutput
                    };
                    Assert.Contains(new MachineProjectLayoutValidator().Validate(project).Errors, error => error.Code == expectedCode && error.ComponentId == componentId);
                    var validation = Descendants(view).OfType<TextBlock>().Single(text => text.Name == "PropertyValidationMessage");
                    Assert.Same(editor, validation.DataContext);
                    Assert.Equal(editor.ValidationMessage, validation.Text);
                    var validationVisible = validation.IsVisible;
                    if (!validationVisible) hiddenValidation.Add(componentId);
                    validation.BringIntoView();
                    PumpBindings();
                    CaptureUnitInspector(view, width, language, $"drive-{componentId}-wrong-kind");
                    SetPrimaryDriveValue(editor, componentId, "missing.signal");
                    PumpBindings();
                    Assert.True(editor.HasValidationErrors);
                    Assert.Contains(new MachineProjectLayoutValidator().Validate(project).Errors, error => error.ComponentId == componentId);
                    combo.SetCurrentValue(Selector.SelectedValueProperty, originalValue);
                    combo.GetBindingExpression(Selector.SelectedValueProperty)!.UpdateSource();
                    PumpBindings();
                    Assert.False(editor.HasValidationErrors);
                    Assert.Empty(new MachineProjectLayoutValidator().Validate(project).Errors);
                    var alternative = combo.Items.OfType<LayoutPropertyOption>().First(option => option.Id != originalValue).Id;
                    SetPrimaryDriveValue(editor, componentId, alternative);
                    PumpBindings();
                    Assert.Equal(alternative, combo.SelectedValue);
                    combo.SetCurrentValue(Selector.SelectedValueProperty, originalValue);
                    combo.GetBindingExpression(Selector.SelectedValueProperty)!.UpdateSource();
                    PumpBindings();
                    var directory = Path.Combine(TestStorage.RootPath, "r24-b02-drive-binding", Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(directory);
                    var path = Path.Combine(directory, "drive-binding.ovmachine");
                    var store = new ProjectDocumentFileStore();
                    await store.SaveAsync(project, path);
                    var savedBytes = File.ReadAllBytes(path);
                    var reopened = store.Load(path);
                    using var reopenedVm = new MainViewModel(reopened);
                    reopenedVm.IsDesignMode = true;
                    reopenedVm.Layout.Select(componentId);
                    reopenedVm.OpenEquipmentDriveTabCommand.Execute(null);
                    var reopenedView = new RightToolRegionView { DataContext = reopenedVm };
                    source.RootVisual = reopenedView;
                    ArrangeUnitInspector(reopenedView, width);
                    Assert.Equal(originalValue, DriveCombo(reopenedView, PrimaryDriveProperty(componentId)).SelectedValue);
                    Assert.Equal(project.Channels.Select(channel => channel.Name), reopened.Channels.Select(channel => channel.Name));
                    Assert.Empty(new MachineProjectLayoutValidator().Validate(reopened).Errors);
                    Assert.Equal(savedBytes, File.ReadAllBytes(path));
                    CaptureUnitInspector(reopenedView, width, language, $"drive-{componentId}-saved-reopened");
                    Assert.True(vm.IsDesignMode && reopenedVm.IsDesignMode);
                    Assert.False(vm.IsDryRunPlaybackActive || reopenedVm.IsDryRunPlaybackActive);
                    RecordUnitObservation(width, language, $"drive-{componentId}-recovery", new { expectedCode = expectedCode.ToString(), validationVisible, originalValue, alternative, path, explicitSave = true, missingAndWrongKindRejectedByValidator = true, twoWayRecovered = true, reopenedWithoutRun = true });
                    source.RootVisual = view;
                }
                Assert.Empty(hiddenValidation);
            }
            finally { OpenVisionLanguageService.SetLanguage(previousLanguage, save: false); }
        });
    }

    [Theory]
    [InlineData("sensor-1")]
    [InlineData("cylinder-1")]
    [InlineData("conveyor-1")]
    [InlineData("workpiece-1")]
    [InlineData("behavior-binding")]
    public async Task DriveBindingPlaybackAndStaleEditorWritesAreRejected(string componentId)
    {
        var result = await _ui.InvokeTaskAsync(async () =>
        {
            var previousLanguage = OpenVisionLanguageService.CurrentLanguage;
            OpenVisionLanguageService.SetLanguage(OpenVisionLanguage.English, save: false);
            try
            {
                var actualComponentId = componentId == "behavior-binding" ? "sensor-1" : componentId;
                var project = CreateLongDriveProject(OpenVisionLanguage.English);
                var dryRun = await new DeterministicRecipeDryRunRunner().RunAsync(project, project.Sequences[0].Id);
                var trace = dryRun.Timeline[0];
                var step = new RecipeDryRunStepPresentation(dryRun.SequenceId, trace.StepId, null, "#1", trace.Name,
                    "boundary", trace.HasIssue, trace.HasCheckpoint, trace.HasCheckpointMismatch, string.Empty, trace.BoundarySnapshot);
                using var vm = new MainViewModel(project);
                vm.IsDesignMode = true;
                vm.Layout.Select(actualComponentId);
                vm.OpenEquipmentDriveTabCommand.Execute(null);
                var editor = Assert.IsType<LayoutComponentEditorViewModel>(vm.Layout.SelectedComponentEditor);
                var view = new RightToolRegionView { DataContext = vm };
                using var source = new HwndSource(new HwndSourceParameters("DriveBindingPlaybackFixture")
                {
                    Width = 420, Height = 900, WindowStyle = unchecked((int)0x80000000)
                });
                source.RootVisual = view;
                ArrangeUnitInspector(view, 420);
                var workpiece = componentId == "workpiece-1";
                var combo = workpiece ? null : DriveCombo(view, PrimaryDriveProperty(componentId));
                var text = workpiece ? Descendants(view).OfType<TextBox>().Single(box => box.GetBindingExpression(TextBox.TextProperty)?.ParentBinding.Path.Path == "WorkpieceType") : null;
                var originalValue = workpiece ? editor.WorkpieceType : Assert.IsType<string>(combo!.SelectedValue);
                var alternate = workpiece ? "Recovery product" : combo!.Items.OfType<LayoutPropertyOption>().First(option => option.Id != originalValue).Id;
                vm.DryRunPlayback.Show(step, [step]);
                PumpBindings();
                Assert.True(vm.IsDryRunPlaybackActive);
                Assert.False(vm.IsSceneEditable || vm.Layout.IsEditable);
                var payload = System.Text.Json.JsonSerializer.Serialize(project);
                var disabled = workpiece ? !text!.IsEnabled : !combo!.IsEnabled;
                if (workpiece)
                {
                    text!.SetCurrentValue(TextBox.TextProperty, alternate);
                    text.GetBindingExpression(TextBox.TextProperty)!.UpdateSource();
                }
                else
                {
                    combo!.SetCurrentValue(Selector.SelectedValueProperty, alternate);
                    combo.GetBindingExpression(Selector.SelectedValueProperty)!.UpdateSource();
                }
                PumpBindings();
                var bindingRestored = workpiece ? text!.Text == originalValue : Equals(combo!.SelectedValue, originalValue);
                SetPrimaryDriveValue(editor, componentId, alternate);
                PumpBindings();
                var playbackPreserved = vm.IsDryRunPlaybackActive;
                var readOnlyPreserved = payload == System.Text.Json.JsonSerializer.Serialize(project) && !vm.HasUnsavedChanges;
                CaptureUnitInspector(view, 420, OpenVisionLanguage.English, $"drive-{componentId}-playback-attempt");
                vm.DryRunPlayback.ExitCommand.Execute(null);
                PumpBindings();
                SetPrimaryDriveValue(editor, componentId, alternate);
                PumpBindings();
                var restoredEditing = vm.IsSceneEditable && vm.Layout.IsEditable && vm.HasUnsavedChanges;
                vm.Layout.Select("frame-1");
                PumpBindings();
                var afterEdit = System.Text.Json.JsonSerializer.Serialize(project);
                SetPrimaryDriveValue(editor, componentId, originalValue);
                var stalePreserved = afterEdit == System.Text.Json.JsonSerializer.Serialize(project);
                vm.Layout.Select(actualComponentId);
                PumpBindings();
                var reselected = System.Text.Json.JsonSerializer.Serialize(project);
                SetPrimaryDriveValue(editor, componentId, alternate);
                var reselectedPreserved = reselected == System.Text.Json.JsonSerializer.Serialize(project);
                var currentEditor = Assert.IsType<LayoutComponentEditorViewModel>(vm.Layout.SelectedComponentEditor);
                vm.Dispose();
                var released = System.Text.Json.JsonSerializer.Serialize(project);
                SetPrimaryDriveValue(currentEditor, componentId, originalValue);
                var disposedPreserved = released == System.Text.Json.JsonSerializer.Serialize(project);
                RecordUnitObservation(420, OpenVisionLanguage.English, $"drive-{componentId}-writer-guards", new
                {
                    disabled, bindingRestored, playbackPreserved, readOnlyPreserved, restoredEditing, stalePreserved, reselectedPreserved,
                    disposedPreserved, offlineDryRunForFixture = true, applicationRunInvocations = 0
                });
                return (disabled, bindingRestored, playbackPreserved, readOnlyPreserved, restoredEditing, stalePreserved, reselectedPreserved, disposedPreserved);
            }
            finally { OpenVisionLanguageService.SetLanguage(previousLanguage, save: false); }
        });
        Assert.True(result.disabled && result.bindingRestored && result.playbackPreserved && result.readOnlyPreserved, "Read-only drive binding must preserve the complete document and playback.");
        Assert.True(result.restoredEditing);
        Assert.True(result.stalePreserved && result.reselectedPreserved && result.disposedPreserved, "Released or stale editors must not write device definitions.");
    }


    [Theory]
    [InlineData(300, OpenVisionLanguage.Korean)]
    [InlineData(420, OpenVisionLanguage.English)]
    public async Task UnitAssignmentRejectsReadOnlyStaleAndReleasedInput(double width, OpenVisionLanguage language)
    {
        var result = await _ui.InvokeTaskAsync(async () =>
        {
            var previousLanguage = OpenVisionLanguageService.CurrentLanguage;
            OpenVisionLanguageService.SetLanguage(language, save: false);
            try
            {
                var project = CreatePlacementAdmissionProject(language);
                var step = await CreatePlacementAdmissionBoundary(project);
                using var vm = new MainViewModel(project);
                vm.IsDesignMode = true;
                vm.Layout.Select("frame-1");
                var editor = Assert.IsType<LayoutComponentEditorViewModel>(vm.Layout.SelectedComponentEditor);
                var view = new RightToolRegionView { DataContext = vm };
                using var source = new HwndSource(new HwndSourceParameters("UnitAdmissionFixture")
                {
                    Width = (int)width, Height = 900, WindowStyle = unchecked((int)0x80000000)
                });
                source.RootVisual = view;
                ArrangeUnitInspector(view, width);
                var combo = DriveCombo(view, nameof(LayoutComponentEditorViewModel.UnitId));
                combo.BringIntoView();
                PumpBindings();
                Assert.Equal("unit-press", combo.SelectedValue);
                Assert.True(combo.IsVisible);
                vm.DryRunPlayback.Show(step, [step]);
                PumpBindings();
                var disabled = !combo.IsEnabled;
                var payload = System.Text.Json.JsonSerializer.Serialize(project);
                combo.SetCurrentValue(Selector.SelectedValueProperty, "unit-camera");
                combo.GetBindingExpression(Selector.SelectedValueProperty)!.UpdateSource();
                PumpBindings();
                var bindingRestored = Equals(combo.SelectedValue, "unit-press");
                editor.UnitId = string.Empty;
                PumpBindings();
                var readOnlyPreserved = payload == System.Text.Json.JsonSerializer.Serialize(project) && !vm.HasUnsavedChanges;
                var playbackPreserved = vm.IsDryRunPlaybackActive && !vm.IsSceneEditable;
                CaptureUnitInspector(view, width, language, "admission-unit-readonly");
                vm.DryRunPlayback.ExitCommand.Execute(null);
                PumpBindings();
                editor.UnitId = "unit-camera";
                PumpBindings();
                var recovered = Equals(combo.SelectedValue, "unit-camera") && vm.IsSceneEditable && vm.HasUnsavedChanges;
                vm.IsDesignMode = false;
                PumpBindings();
                Assert.False(vm.IsSceneEditable);
                var runModeDisabled = !combo.IsEnabled;
                var runModeVisible = combo.IsVisible;
                var runModePayload = System.Text.Json.JsonSerializer.Serialize(project);
                combo.SetCurrentValue(Selector.SelectedValueProperty, "unit-press");
                combo.GetBindingExpression(Selector.SelectedValueProperty)!.UpdateSource();
                PumpBindings();
                var runModePreserved = runModePayload == System.Text.Json.JsonSerializer.Serialize(project) && Equals(combo.SelectedValue, "unit-camera");
                CaptureUnitInspector(view, width, language, "admission-unit-runmode");
                vm.IsDesignMode = true;
                editor.UnitId = "unit-camera";
                PumpBindings();
                var frame = project.Layouts[0].Components.Single(component => component.Id == "frame-1");
                Assert.Equal("unit-camera", frame.UnitId);
                Assert.False(vm.IsRunning);
                var directory = Path.Combine(TestStorage.RootPath, "r24-b02-placement-admission", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, "unit-admission.ovmachine");
                var store = new ProjectDocumentFileStore();
                await store.SaveAsync(project, path);
                var saved = File.ReadAllBytes(path);
                using (var reopened = new MainViewModel(store.Load(path)))
                {
                    reopened.IsDesignMode = true;
                    reopened.Layout.Select("frame-1");
                    var reopenedView = new RightToolRegionView { DataContext = reopened };
                    source.RootVisual = reopenedView;
                    ArrangeUnitInspector(reopenedView, width);
                    var reopenedCombo = DriveCombo(reopenedView, nameof(LayoutComponentEditorViewModel.UnitId));
                    reopenedCombo.BringIntoView();
                    PumpBindings();
                    Assert.Equal("unit-camera", reopenedCombo.SelectedValue);
                    Assert.False(reopened.IsDryRunPlaybackActive || reopened.IsRunning);
                    Assert.Equal(saved, File.ReadAllBytes(path));
                    CaptureUnitInspector(reopenedView, width, language, "admission-unit-reopened");
                }
                source.RootVisual = view;
                vm.Layout.Select("conveyor-1");
                PumpBindings();
                var afterEdit = System.Text.Json.JsonSerializer.Serialize(project);
                editor.UnitId = "unit-press";
                var stalePreserved = afterEdit == System.Text.Json.JsonSerializer.Serialize(project);
                vm.Layout.Select("frame-1");
                PumpBindings();
                var reselected = System.Text.Json.JsonSerializer.Serialize(project);
                editor.UnitId = string.Empty;
                var reselectedPreserved = reselected == System.Text.Json.JsonSerializer.Serialize(project);
                var current = Assert.IsType<LayoutComponentEditorViewModel>(vm.Layout.SelectedComponentEditor);
                vm.Dispose();
                var released = System.Text.Json.JsonSerializer.Serialize(project);
                current.UnitId = "unit-press";
                var releasedPreserved = released == System.Text.Json.JsonSerializer.Serialize(project);
                RecordUnitObservation(width, language, "admission-unit-results", new
                {
                    disabled, bindingRestored, readOnlyPreserved, playbackPreserved, recovered, runModeDisabled, runModePreserved, runModeVisible, stalePreserved,
                    reselectedPreserved, releasedPreserved, path, explicitFileSaveLoad = true,
                    offlineDryRunForFixture = true, applicationRunCommandCalls = 0
                });
                return (disabled, bindingRestored, readOnlyPreserved, playbackPreserved, recovered, runModeDisabled, runModePreserved, stalePreserved, reselectedPreserved, releasedPreserved);
            }
            finally { OpenVisionLanguageService.SetLanguage(previousLanguage, save: false); }
        });
        Assert.True(result.disabled && result.bindingRestored && result.readOnlyPreserved && result.playbackPreserved, "Unit binding must preserve the snapshot document, playback and original selected value.");
        Assert.True(result.recovered);
        Assert.True(result.runModeDisabled && result.runModePreserved, "Run-mode unit input must be disabled and preserve its authored assignment.");
        Assert.True(result.stalePreserved && result.reselectedPreserved && result.releasedPreserved, "Previous or released editors must not write unit assignment.");
    }

    [Theory]
    [InlineData(nameof(LayoutComponentEditorViewModel.Name))]
    [InlineData(nameof(LayoutComponentEditorViewModel.X))]
    [InlineData(nameof(LayoutComponentEditorViewModel.Y))]
    [InlineData(nameof(LayoutComponentEditorViewModel.RotationDegrees))]
    [InlineData(nameof(LayoutComponentEditorViewModel.Width))]
    [InlineData(nameof(LayoutComponentEditorViewModel.Height))]
    [InlineData(nameof(LayoutComponentEditorViewModel.VerticalBaseElevation))]
    [InlineData(nameof(LayoutComponentEditorViewModel.VerticalHeight))]
    public async Task DirectPlacementInputsRejectReadOnlyStaleAndReleasedInput(string propertyName)
    {
        var result = await _ui.InvokeTaskAsync(async () =>
        {
            var numberBinding = propertyName is nameof(LayoutComponentEditorViewModel.RotationDegrees) or nameof(LayoutComponentEditorViewModel.Width) or nameof(LayoutComponentEditorViewModel.Height);
            var language = numberBinding ? OpenVisionLanguage.English : OpenVisionLanguage.Korean;
            var width = numberBinding ? 420 : 300;
            var previousLanguage = OpenVisionLanguageService.CurrentLanguage;
            OpenVisionLanguageService.SetLanguage(language, save: false);
            try
            {
                var project = CreatePlacementAdmissionProject(language);
                var step = await CreatePlacementAdmissionBoundary(project);
                using var vm = new MainViewModel(project);
                vm.IsDesignMode = true;
                vm.Layout.Select("frame-1");
                if (numberBinding) vm.Navigation.IsSimulationWorkspace = true;
                vm.Navigation.IsInspectorOpen = true;
                var editor = Assert.IsType<LayoutComponentEditorViewModel>(vm.Layout.SelectedComponentEditor);
                var property = typeof(LayoutComponentEditorViewModel).GetProperty(propertyName)!;
                var original = property.GetValue(editor)!;
                object alternate = original is string name ? name + " changed" : (double)original + 4.5;
                var view = new RightToolRegionView { DataContext = vm };
                using var source = new HwndSource(new HwndSourceParameters("PlacementAdmissionFixture")
                {
                    Width = width, Height = 900, WindowStyle = unchecked((int)0x80000000)
                });
                source.RootVisual = view;
                ArrangeUnitInspector(view, width);
                var number = numberBinding ? Descendants(view).OfType<global::Wpf.Ui.Controls.NumberBox>().Single(box =>
                    box.GetBindingExpression(global::Wpf.Ui.Controls.NumberBox.ValueProperty)?.ParentBinding.Path.Path == propertyName) : null;
                if (number is not null)
                {
                    number.BringIntoView();
                    PumpBindings();
                    Assert.True(number.IsVisible);
                    Assert.Equal(original, number.Value);
                }
                vm.DryRunPlayback.Show(step, [step]);
                PumpBindings();
                var disabled = number is null || !number.IsEnabled;
                var payload = System.Text.Json.JsonSerializer.Serialize(project);
                if (number is not null)
                {
                    number.SetCurrentValue(global::Wpf.Ui.Controls.NumberBox.ValueProperty, alternate);
                    number.GetBindingExpression(global::Wpf.Ui.Controls.NumberBox.ValueProperty)!.UpdateSource();
                    PumpBindings();
                }
                var bindingRestored = number is null || Equals(number.Value, original);
                property.SetValue(editor, alternate);
                PumpBindings();
                var readOnlyPreserved = payload == System.Text.Json.JsonSerializer.Serialize(project) && !vm.HasUnsavedChanges;
                var playbackPreserved = vm.IsDryRunPlaybackActive && !vm.IsSceneEditable;
                CaptureUnitInspector(view, width, language, $"admission-{propertyName}-readonly");
                vm.DryRunPlayback.ExitCommand.Execute(null);
                PumpBindings();
                property.SetValue(editor, alternate);
                PumpBindings();
                var recovered = Equals(property.GetValue(editor), alternate) && vm.IsSceneEditable && vm.HasUnsavedChanges;
                Assert.False(vm.IsRunning);
                var afterEdit = System.Text.Json.JsonSerializer.Serialize(project);
                property.SetValue(editor, alternate);
                var unchangedRepeat = afterEdit == System.Text.Json.JsonSerializer.Serialize(project);
                CaptureUnitInspector(view, width, language, $"admission-{propertyName}-recovered");
                vm.Layout.Select("conveyor-1");
                PumpBindings();
                var selectedElsewhere = System.Text.Json.JsonSerializer.Serialize(project);
                property.SetValue(editor, Equals(property.GetValue(editor), original) ? alternate : original);
                var stalePreserved = selectedElsewhere == System.Text.Json.JsonSerializer.Serialize(project);
                vm.Layout.Select("frame-1");
                PumpBindings();
                var reselected = System.Text.Json.JsonSerializer.Serialize(project);
                property.SetValue(editor, Equals(property.GetValue(editor), original) ? alternate : original);
                var reselectedPreserved = reselected == System.Text.Json.JsonSerializer.Serialize(project);
                var current = Assert.IsType<LayoutComponentEditorViewModel>(vm.Layout.SelectedComponentEditor);
                vm.Dispose();
                var released = System.Text.Json.JsonSerializer.Serialize(project);
                property.SetValue(current, Equals(property.GetValue(current), original) ? alternate : original);
                var releasedPreserved = released == System.Text.Json.JsonSerializer.Serialize(project);
                RecordUnitObservation(width, language, $"admission-{propertyName}-results", new
                {
                    propertyName, original, alternate, numberBinding, disabled, bindingRestored, readOnlyPreserved,
                    playbackPreserved, recovered, unchangedRepeat, stalePreserved, reselectedPreserved, releasedPreserved,
                    offlineDryRunForFixture = true, applicationRunCommandCalls = 0
                });
                return (disabled, bindingRestored, readOnlyPreserved, playbackPreserved, recovered, unchangedRepeat, stalePreserved, reselectedPreserved, releasedPreserved);
            }
            finally { OpenVisionLanguageService.SetLanguage(previousLanguage, save: false); }
        });
        Assert.True(result.disabled && result.bindingRestored && result.readOnlyPreserved && result.playbackPreserved, "Placement input must preserve the snapshot document and playback.");
        Assert.True(result.recovered && result.unchangedRepeat);
        Assert.True(result.stalePreserved && result.reselectedPreserved && result.releasedPreserved, "Previous or released editors must not write placement.");
    }

    private static MachineProjectDocument CreatePlacementAdmissionProject(OpenVisionLanguage language)
    {
        var project = CreateLongDriveProject(language);
        project.Stations.Add(CreateLongUnitProject(language).Stations[0]);
        project.Layouts[0].Components.Single(component => component.Id == "frame-1").UnitId = "unit-press";
        Assert.Empty(new MachineProjectLayoutValidator().Validate(project).Errors);
        return project;
    }

    private static async Task<RecipeDryRunStepPresentation> CreatePlacementAdmissionBoundary(MachineProjectDocument project)
    {
        var dryRun = await new DeterministicRecipeDryRunRunner().RunAsync(project, project.Sequences[0].Id);
        var trace = dryRun.Timeline[0];
        return new RecipeDryRunStepPresentation(dryRun.SequenceId, trace.StepId, null, "#1", trace.Name,
            "boundary", trace.HasIssue, trace.HasCheckpoint, trace.HasCheckpointMismatch, string.Empty, trace.BoundarySnapshot);
    }

    [Theory]
    [InlineData(300, OpenVisionLanguage.Korean)]
    [InlineData(420, OpenVisionLanguage.Korean)]
    [InlineData(300, OpenVisionLanguage.English)]
    [InlineData(420, OpenVisionLanguage.English)]
    public async Task UnitAssignmentLongSelectionAndPopupRemainReadable(double width, OpenVisionLanguage language)
    {
        var result = await _ui.InvokeTaskAsync(async () =>
        {
            var previousLanguage = OpenVisionLanguageService.CurrentLanguage;
            try
            {
                OpenVisionLanguageService.SetLanguage(language, save: false);
                var project = CreateLongUnitProject(language);
                using var vm = new MainViewModel(project);
                vm.IsDesignMode = true;
                var layout = vm.Layout;
                layout.Select("unit-frame");
                var editor = Assert.IsType<LayoutComponentEditorViewModel>(layout.SelectedComponentEditor);
                vm.Navigation.SelectedDocumentTabIndex = 0;
                var view = new RightToolRegionView { DataContext = vm };
                using var source = new HwndSource(new HwndSourceParameters("UnitAssignmentPopupFixture")
                {
                    Width = (int)width, Height = 900, WindowStyle = unchecked((int)0x80000000)
                });
                source.RootVisual = view;
                ArrangeUnitInspector(view, width);
                var combo = Descendants(view).OfType<ComboBox>().Single(candidate => ReferenceEquals(candidate.ItemsSource, editor.UnitOptions));
                combo.BringIntoView();
                PumpBindings();
                var option = editor.UnitOptions.Single(candidate => candidate.Id == "unit-press");
                var selectedText = Descendants(combo).OfType<TextBlock>().Single(candidate => candidate.Text == option.DisplayName);
                var original = System.Text.Json.JsonSerializer.Serialize(project);
                CaptureUnitInspector(view, width, language, "long-selected");
                var selectedFits = UnitTextFits(selectedText, combo);
                var popup = Descendants(combo).OfType<Popup>().Single();
                try
                {
                    combo.IsDropDownOpen = true;
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    PumpBindings();
                    var child = Assert.IsAssignableFrom<FrameworkElement>(popup.Child);
                    Assert.True(popup.IsOpen);
                    Assert.True(child.ActualWidth > 0 && child.ActualHeight > 0);
                    CaptureUnitInspector(child, width, language, "popup-top");
                    var popupTexts = Descendants(child).OfType<TextBlock>().Where(text => editor.UnitOptions.Any(item => item.DisplayName == text.Text)).ToArray();
                    Assert.NotEmpty(popupTexts);
                    var popupFits = popupTexts.All(text =>
                    {
                        DependencyObject? parent = text;
                        while (parent is not null && parent is not ComboBoxItem) parent = VisualTreeHelper.GetParent(parent);
                        return parent is ComboBoxItem item && UnitTextFits(text, item);
                    });
                    var scroll = Descendants(child).OfType<ScrollViewer>().First();
                    var scrollable = scroll.ScrollableHeight > 0;
                    scroll.ScrollToBottom();
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    PumpBindings();
                    var lastItem = Assert.IsType<ComboBoxItem>(combo.ItemContainerGenerator.ContainerFromIndex(combo.Items.Count - 1));
                    Assert.True(lastItem.IsVisible);
                    var lastText = Descendants(lastItem).OfType<TextBlock>().Single(text => text.Text == editor.UnitOptions[^1].DisplayName);
                    var lastFits = UnitTextFits(lastText, lastItem);
                    var lastBounds = lastItem.TransformToAncestor(scroll).TransformBounds(new Rect(lastItem.RenderSize));
                    var bottomReachable = lastBounds.Top >= -1 && lastBounds.Bottom <= scroll.ActualHeight + 1;
                    CaptureUnitInspector(child, width, language, "popup-bottom");
                    combo.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.Escape) { RoutedEvent = Keyboard.KeyDownEvent });
                    PumpBindings();
                    var cancelled = !combo.IsDropDownOpen;
                    var payloadUnchanged = original == System.Text.Json.JsonSerializer.Serialize(project);
                    RecordUnitObservation(width, language, "long-popup", new
                    {
                        selectedFits, popupFits, lastFits, scrollable, bottomReachable, cancelled, payloadUnchanged,
                        comboWidth = combo.ActualWidth, selectedWidth = selectedText.ActualWidth,
                        popupWidth = child.ActualWidth, popupHeight = child.ActualHeight,
                        selectedText = selectedText.Text, selectedValue = combo.SelectedValue
                    });
                    return (selectedFits, popupFits, lastFits, scrollable, bottomReachable, cancelled, payloadUnchanged);
                }
                finally { combo.IsDropDownOpen = false; }
            }
            finally { OpenVisionLanguageService.SetLanguage(previousLanguage, save: false); }
        });

        Assert.True(result.selectedFits, "Selected unit name/ID glyphs must fit the rendered selection control.");
        Assert.True(result.popupFits, "Dropdown text must fit its rendered container.");
        Assert.True(result.lastFits, "The last long unit must remain readable after scrolling.");
        Assert.True(result.scrollable);
        Assert.True(result.bottomReachable);
        Assert.True(result.cancelled);
        Assert.True(result.payloadUnchanged);
    }

    [Theory]
    [InlineData(300, OpenVisionLanguage.Korean)]
    [InlineData(420, OpenVisionLanguage.English)]
    public async Task UnitAssignmentPopupCancelAndSavedReopenKeepExistingContract(double width, OpenVisionLanguage language)
    {
        await _ui.InvokeTaskAsync(async () =>
        {
            var previousLanguage = OpenVisionLanguageService.CurrentLanguage;
            try
            {
                OpenVisionLanguageService.SetLanguage(language, save: false);
                var project = CreateLongUnitProject(language);
                using var vm = new MainViewModel(project);
                vm.IsDesignMode = true;
                var layout = vm.Layout;
                layout.Select("unit-frame");
                var editor = Assert.IsType<LayoutComponentEditorViewModel>(layout.SelectedComponentEditor);
                vm.Navigation.SelectedDocumentTabIndex = 0;
                var view = new RightToolRegionView { DataContext = vm };
                using var source = new HwndSource(new HwndSourceParameters("UnitAssignmentRoundTripFixture")
                {
                    Width = (int)width, Height = 900, WindowStyle = unchecked((int)0x80000000)
                });
                source.RootVisual = view;
                ArrangeUnitInspector(view, width);
                var combo = Descendants(view).OfType<ComboBox>().Single(candidate => ReferenceEquals(candidate.ItemsSource, editor.UnitOptions));
                combo.BringIntoView();
                PumpBindings();
                var original = System.Text.Json.JsonSerializer.Serialize(project);
                combo.IsDropDownOpen = true;
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                combo.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.Escape) { RoutedEvent = Keyboard.KeyDownEvent });
                PumpBindings();
                Assert.False(combo.IsDropDownOpen);
                Assert.Equal("unit-press", combo.SelectedValue);
                Assert.Equal(original, System.Text.Json.JsonSerializer.Serialize(project));
                combo.SelectedValue = "unit-camera";
                PumpBindings();
                Assert.Equal("unit-camera", layout.SelectedItem!.Component!.UnitId);
                var assignmentPayload = System.Text.Json.JsonSerializer.Serialize(project);
                editor.DraftName = "Unsaved placement name";
                editor.DiscardPlacementDraftCommand.Execute(null);
                PumpBindings();
                Assert.Equal(assignmentPayload, System.Text.Json.JsonSerializer.Serialize(project));
                Assert.Equal("unit-camera", combo.SelectedValue);
                combo.SelectedValue = string.Empty;
                PumpBindings();
                Assert.Null(layout.SelectedItem.Component.UnitId);
                Assert.Equal(string.Empty, editor.UnitId);
                editor.UnitId = "unit-camera";
                PumpBindings();
                Assert.Equal("unit-camera", combo.SelectedValue);
                var directory = Path.Combine(TestStorage.RootPath, "r24-b02-unit-assignment", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, "unit-assignment.ovmachine");
                var store = new ProjectDocumentFileStore();
                await store.SaveAsync(project, path);
                var bytes = File.ReadAllBytes(path);
                var reopened = store.Load(path);
                using var reopenedVm = new MainViewModel(reopened);
                reopenedVm.IsDesignMode = true;
                var reopenedLayout = reopenedVm.Layout;
                reopenedLayout.Select("unit-frame");
                var reopenedEditor = Assert.IsType<LayoutComponentEditorViewModel>(reopenedLayout.SelectedComponentEditor);
                var reopenedView = new RightToolRegionView { DataContext = reopenedVm };
                source.RootVisual = reopenedView;
                ArrangeUnitInspector(reopenedView, width);
                var reopenedCombo = Descendants(reopenedView).OfType<ComboBox>().Single(candidate => ReferenceEquals(candidate.ItemsSource, reopenedEditor.UnitOptions));
                reopenedCombo.BringIntoView();
                PumpBindings();
                Assert.Equal("unit-camera", reopenedCombo.SelectedValue);
                Assert.Equal("unit-camera", reopenedEditor.UnitId);
                Assert.Equal(project.Stations[0].Units[1].Name, reopened.Stations[0].Units[1].Name);
                CaptureUnitInspector(reopenedView, width, language, "saved-reopened");
                Assert.Equal(bytes, File.ReadAllBytes(path));
                Assert.True(vm.IsDesignMode && reopenedVm.IsDesignMode);
                Assert.False(vm.IsDryRunPlaybackActive || reopenedVm.IsDryRunPlaybackActive);
                RecordUnitObservation(width, language, "roundtrip", new { path, reopenedEditor.UnitId, savedNameLength = reopened.Stations[0].Units[1].Name.Length, explicitSave = true, popupCancelledWithoutMutation = true, placementDraftDiscardPreservesAssignment = true });
            }
            finally { OpenVisionLanguageService.SetLanguage(previousLanguage, save: false); }
        });
    }

    [Fact]
    public async Task MultiSelectionPropertiesPaneUsesR19FirstSelectedComponent()
    {
        var state = await _ui.InvokeAsync(() =>
        {
            var project = CreateProject();
            var layoutDefinition = Assert.Single(project.Layouts);
            layoutDefinition.Components.Add(new LayoutComponentDefinition
            {
                Id = "frame-02", Name = "Frame 02", Kind = LayoutComponentKind.MachineFrame,
                UnitId = "unit-press", Transform = new Transform2D(), Size = new Size2D { Width = 20, Height = 20 }
            });

            using var layout = new MachineLayoutViewModel();
            layout.Load(project);
            layout.SelectMany(["camera-01", "frame-02"], "frame-02");
            var editor = Assert.IsType<LayoutComponentEditorViewModel>(layout.SelectedComponentEditor);
            using var navigation = new ShellNavigationViewModel(false, () => false, () => false,
                () => Task.CompletedTask, () => { }, _ => { });
            navigation.SelectedDocumentTabIndex = 0;
            var view = new RightToolRegionView
            {
                DataContext = new InspectorDataContext(layout, navigation, IsRunMode: false)
            };
            Arrange(view);

            var title = Descendants(view).OfType<TextBlock>().Single(candidate =>
                candidate.GetBindingExpression(TextBlock.TextProperty)?.ParentBinding.Path.Path == "PaneTitle");
            var headerStack = Assert.IsType<StackPanel>(VisualTreeHelper.GetParent(title));
            var editorHeader = Assert.IsType<Grid>(VisualTreeHelper.GetParent(headerStack));
            var id = Descendants(editorHeader).OfType<TextBlock>().SelectMany(candidate => candidate.Inlines.OfType<System.Windows.Documents.Run>()).Single(candidate =>
                candidate.GetBindingExpression(System.Windows.Documents.Run.TextProperty)?.ParentBinding.Path.Path == "Id");

            return (
                SelectionCount: layout.SelectionCount,
                PrimaryId: layout.SelectedItem?.Id,
                PropertiesComponentId: editor.Id,
                PropertiesTitle: title.Text,
                PropertiesId: id.Text);
        });

        Assert.Equal(2, state.SelectionCount);
        Assert.Equal("frame-02", state.PrimaryId);
        Assert.Equal("camera-01", state.PropertiesComponentId);
        Assert.Equal("Inspection Camera", state.PropertiesTitle);
        Assert.Equal("camera-01", state.PropertiesId);
    }

    [Fact]
    public async Task VerticalEnvelopeTextBoxKeepsInvalidDraftThenAppliesValidBoundary()
    {
        var result = await _ui.InvokeAsync(() =>
        {
            using var layout = CreateEnvelopeLayout();
            layout.Select("frame-01");
            var editor = Assert.IsType<LayoutComponentEditorViewModel>(layout.SelectedComponentEditor);
            var component = layout.SelectedItem!.Component!;
            var changes = 0;
            layout.DefinitionChanged += (_, _) => changes++;
            using var navigation = new ShellNavigationViewModel(false, () => false, () => false,
                () => Task.CompletedTask, () => { }, _ => { });
            navigation.SelectedDocumentTabIndex = 0;
            var view = new RightToolRegionView
            {
                DataContext = new InspectorDataContext(layout, navigation, IsRunMode: false)
            };
            var window = new Window
            {
                Width = 520,
                Height = 900,
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
                var height = controls.OfType<TextBox>().Single(textBox =>
                    AutomationProperties.GetAutomationId(textBox) == "EquipmentDraftVerticalHeight");
                var elevation = controls.OfType<TextBox>().Single(textBox =>
                    textBox.GetBindingExpression(TextBox.TextProperty)?.ParentBinding.Path.Path == "DraftBaseElevationText");
                var error = controls.OfType<TextBlock>().Single(textBlock =>
                    textBlock.GetBindingExpression(TextBlock.TextProperty)?.ParentBinding.Path.Path == "PlacementDraftError");
                var apply = controls.OfType<Button>().Single(button =>
                    ReferenceEquals(button.Command, editor.ApplyPlacementDraftCommand));
                var discard = controls.OfType<Button>().Single(button =>
                    ReferenceEquals(button.Command, editor.DiscardPlacementDraftCommand));
                var heightValue = Assert.IsAssignableFrom<IValueProvider>(
                    UIElementAutomationPeer.CreatePeerForElement(height)?.GetPattern(PatternInterface.Value));
                var elevationValue = Assert.IsAssignableFrom<IValueProvider>(
                    UIElementAutomationPeer.CreatePeerForElement(elevation)?.GetPattern(PatternInterface.Value));
                var applyInvoke = Assert.IsAssignableFrom<IInvokeProvider>(
                    UIElementAutomationPeer.CreatePeerForElement(apply)?.GetPattern(PatternInterface.Invoke));
                var discardInvoke = Assert.IsAssignableFrom<IInvokeProvider>(
                    UIElementAutomationPeer.CreatePeerForElement(discard)?.GetPattern(PatternInterface.Invoke));
                var initiallyDisabled = !apply.IsEnabled && !apply.Command!.CanExecute(apply.CommandParameter)
                    && !discard.IsEnabled && !discard.Command!.CanExecute(discard.CommandParameter);

                heightValue.SetValue("NaN");
                PumpBindings();
                var invalidDraftCommandsEnabled = apply.IsEnabled && apply.Command!.CanExecute(apply.CommandParameter)
                    && discard.IsEnabled && discard.Command!.CanExecute(discard.CommandParameter);
                applyInvoke.Invoke();
                window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
                PumpBindings();
                var invalid = (editor.DraftVerticalHeightText, error.Text, component.VerticalEnvelope, changes,
                    editor.HasPendingPlacementDraft);

                heightValue.SetValue("60");
                elevationValue.SetValue("-10000");
                PumpBindings();
                var validDraftCommandsEnabled = apply.IsEnabled && apply.Command!.CanExecute(apply.CommandParameter)
                    && discard.IsEnabled && discard.Command!.CanExecute(discard.CommandParameter);
                applyInvoke.Invoke();
                window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
                PumpBindings();
                var appliedCommandsDisabled = !apply.IsEnabled && !apply.Command!.CanExecute(apply.CommandParameter)
                    && !discard.IsEnabled && !discard.Command!.CanExecute(discard.CommandParameter);
                heightValue.SetValue("61");
                PumpBindings();
                var reeditCommandsEnabled = apply.IsEnabled && apply.Command!.CanExecute(apply.CommandParameter)
                    && discard.IsEnabled && discard.Command!.CanExecute(discard.CommandParameter);
                discardInvoke.Invoke();
                window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
                PumpBindings();
                var discardRestoredBaseline = height.Text == "60" && !apply.IsEnabled && !discard.IsEnabled
                    && !apply.Command!.CanExecute(apply.CommandParameter)
                    && !discard.Command!.CanExecute(discard.CommandParameter);
                return (invalid, editor.DraftVerticalHeightText, component.VerticalEnvelope, changes,
                    editor.HasPendingPlacementDraft, initiallyDisabled, invalidDraftCommandsEnabled,
                    validDraftCommandsEnabled, appliedCommandsDisabled, reeditCommandsEnabled,
                    discardRestoredBaseline);
            }
            finally
            {
                window.Close();
            }
        });

        Assert.Equal("NaN", result.invalid.DraftVerticalHeightText);
        Assert.NotEmpty(result.invalid.Text);
        Assert.Null(result.invalid.VerticalEnvelope);
        Assert.Equal(0, result.invalid.changes);
        Assert.True(result.invalid.HasPendingPlacementDraft);
        Assert.Equal("60", result.DraftVerticalHeightText);
        Assert.Equal(-10000, result.VerticalEnvelope?.BaseElevation);
        Assert.Equal(60, result.VerticalEnvelope?.Height);
        Assert.Equal(1, result.changes);
        Assert.False(result.HasPendingPlacementDraft);
        Assert.True(result.initiallyDisabled);
        Assert.True(result.invalidDraftCommandsEnabled);
        Assert.True(result.validDraftCommandsEnabled);
        Assert.True(result.appliedCommandsDisabled);
        Assert.True(result.reeditCommandsEnabled);
        Assert.True(result.discardRestoredBaseline);
    }

    [Fact]
    public async Task VerticalEnvelopeDraftActionsAreReachableAndUsableByKeyboard()
    {
        var result = await _ui.InvokeAsync(() =>
        {
            using var layout = CreateEnvelopeLayout();
            layout.Select("frame-01");
            var editor = Assert.IsType<LayoutComponentEditorViewModel>(layout.SelectedComponentEditor);
            var component = layout.SelectedItem!.Component!;
            var changes = 0;
            layout.DefinitionChanged += (_, _) => changes++;
            using var navigation = new ShellNavigationViewModel(false, () => false, () => false,
                () => Task.CompletedTask, () => { }, _ => { });
            navigation.SelectedDocumentTabIndex = 0;
            var view = new RightToolRegionView
            {
                DataContext = new InspectorDataContext(layout, navigation, IsRunMode: false)
            };
            var window = new Window
            {
                Width = 520,
                Height = 900,
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
                var height = controls.OfType<TextBox>().Single(textBox =>
                    AutomationProperties.GetAutomationId(textBox) == "EquipmentDraftVerticalHeight");
                var apply = controls.OfType<Button>().Single(button =>
                    ReferenceEquals(button.Command, editor.ApplyPlacementDraftCommand));
                var discard = controls.OfType<Button>().Single(button =>
                    ReferenceEquals(button.Command, editor.DiscardPlacementDraftCommand));
                var scrollViewer = controls.OfType<ScrollViewer>().Single(candidate =>
                    candidate.Name == "DesignInspectorScrollViewer");
                var heightValue = Assert.IsAssignableFrom<IValueProvider>(
                    UIElementAutomationPeer.CreatePeerForElement(height)?.GetPattern(PatternInterface.Value));

                scrollViewer.ScrollToEnd();
                heightValue.SetValue("61");
                PumpBindings();

                bool ReachButtonByTab(Button target)
                {
                    if (!ReferenceEquals(Keyboard.Focus(height), height)) return false;
                    var visited = new HashSet<UIElement>();
                    for (var step = 0; step < 24; step++)
                    {
                        if (Keyboard.FocusedElement is not UIElement focused || !visited.Add(focused)) return false;
                        if (ReferenceEquals(focused, target)) return true;
                        if (!focused.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next))) return false;
                        window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(window.UpdateLayout));
                    }

                    return false;
                }

                void PressSpace(Button button)
                {
                    var source = PresentationSource.FromVisual(button)
                        ?? throw new InvalidOperationException("The envelope action had no presentation source.");
                    button.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.Space)
                    {
                        RoutedEvent = Keyboard.KeyDownEvent
                    });
                    button.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.Space)
                    {
                        RoutedEvent = Keyboard.KeyUpEvent
                    });
                    window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
                    PumpBindings();
                }

                var applyReached = ReachButtonByTab(apply);
                if (applyReached) PressSpace(apply);
                var appliedHeight = component.VerticalEnvelope?.Height;
                var appliedChanges = changes;
                heightValue.SetValue("62");
                PumpBindings();
                var discardReached = ReachButtonByTab(discard);
                if (discardReached) PressSpace(discard);

                return (applyReached, appliedHeight, appliedChanges, discardReached,
                    restoredDraft: editor.DraftVerticalHeightText, finalHeight: component.VerticalEnvelope?.Height,
                    finalChanges: changes, noPendingDraft: !editor.HasPendingPlacementDraft);
            }
            finally
            {
                window.Close();
            }
        });

        Assert.True(result.applyReached);
        Assert.Equal(61, result.appliedHeight);
        Assert.Equal(1, result.appliedChanges);
        Assert.True(result.discardReached);
        Assert.Equal("61", result.restoredDraft);
        Assert.Equal(61, result.finalHeight);
        Assert.Equal(1, result.finalChanges);
        Assert.True(result.noPendingDraft);
    }

    [Theory]
    [InlineData(500d)]
    [InlineData(900d)]
    public async Task VerticalEnvelopeFieldAndDraftActionsRemainReachableAtScrollEnd(double windowHeight)
    {
        var layoutResult = await _ui.InvokeAsync(() =>
        {
            using var layout = CreateEnvelopeLayout();
            layout.Select("frame-01");
            var editor = Assert.IsType<LayoutComponentEditorViewModel>(layout.SelectedComponentEditor);
            using var navigation = new ShellNavigationViewModel(false, () => false, () => false,
                () => Task.CompletedTask, () => { }, _ => { });
            navigation.SelectedDocumentTabIndex = 0;
            var view = new RightToolRegionView
            {
                DataContext = new InspectorDataContext(layout, navigation, IsRunMode: false)
            };
            var window = new Window
            {
                Width = 520,
                Height = windowHeight,
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
                var scrollViewer = controls.OfType<ScrollViewer>().Single(candidate =>
                    candidate.Name == "DesignInspectorScrollViewer");
                var content = Assert.IsType<StackPanel>(scrollViewer.Content);
                var height = controls.OfType<TextBox>().Single(textBox =>
                    AutomationProperties.GetAutomationId(textBox) == "EquipmentDraftVerticalHeight");
                var apply = controls.OfType<Button>().Single(button =>
                    ReferenceEquals(button.Command, editor.ApplyPlacementDraftCommand));
                var discard = controls.OfType<Button>().Single(button =>
                    ReferenceEquals(button.Command, editor.DiscardPlacementDraftCommand));
                var footerGrid = Assert.IsType<Grid>(VisualTreeHelper.GetParent(apply));
                var footerStack = Assert.IsType<StackPanel>(VisualTreeHelper.GetParent(footerGrid));
                var footer = Assert.IsType<Border>(VisualTreeHelper.GetParent(footerStack));

                scrollViewer.ScrollToEnd();
                window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(window.UpdateLayout));
                var fieldBounds = height.TransformToAncestor(scrollViewer).TransformBounds(
                    new Rect(0, 0, height.ActualWidth, height.ActualHeight));
                var contentBounds = content.TransformToAncestor(scrollViewer).TransformBounds(
                    new Rect(0, 0, content.ActualWidth, content.ActualHeight));
                var scrollBounds = scrollViewer.TransformToAncestor(view).TransformBounds(
                    new Rect(0, 0, scrollViewer.ActualWidth, scrollViewer.ActualHeight));
                var footerBounds = footer.TransformToAncestor(view).TransformBounds(
                    new Rect(0, 0, footer.ActualWidth, footer.ActualHeight));
                var applyBounds = apply.TransformToAncestor(view).TransformBounds(
                    new Rect(0, 0, apply.ActualWidth, apply.ActualHeight));
                var discardBounds = discard.TransformToAncestor(view).TransformBounds(
                    new Rect(0, 0, discard.ActualWidth, discard.ActualHeight));

                return (
                    ViewportHeight: scrollViewer.ViewportHeight,
                    ScrollableHeight: scrollViewer.ScrollableHeight,
                    VerticalOffset: scrollViewer.VerticalOffset,
                    FieldBounds: fieldBounds,
                    ContentBounds: contentBounds,
                    ScrollBounds: scrollBounds,
                    FooterBounds: footerBounds,
                    ApplyBounds: applyBounds,
                    DiscardBounds: discardBounds,
                    ViewHeight: view.ActualHeight,
                    FooterVisible: footer.IsVisible,
                    ApplyVisible: apply.IsVisible,
                    DiscardVisible: discard.IsVisible);
            }
            finally
            {
                window.Close();
            }
        });

        Assert.True(layoutResult.ViewportHeight > 0);
        Assert.True(layoutResult.ScrollableHeight > 0);
        Assert.Equal(layoutResult.ScrollableHeight, layoutResult.VerticalOffset, precision: 2);
        Assert.True(layoutResult.FieldBounds.Top >= -1 && layoutResult.FieldBounds.Bottom <= layoutResult.ViewportHeight + 1);
        Assert.InRange(Math.Abs(layoutResult.ContentBounds.Bottom - layoutResult.ViewportHeight), 0, 1);
        Assert.True(layoutResult.ScrollBounds.Bottom <= layoutResult.FooterBounds.Top + 1,
            $"ScrollViewer/footer overlap at {windowHeight}px: scroll={layoutResult.ScrollBounds}, footer={layoutResult.FooterBounds}, viewHeight={layoutResult.ViewHeight}.");
        Assert.True(layoutResult.FooterBounds.Bottom <= layoutResult.ViewHeight + 1);
        Assert.True(layoutResult.FooterBounds.Contains(layoutResult.ApplyBounds));
        Assert.True(layoutResult.FooterBounds.Contains(layoutResult.DiscardBounds));
        Assert.True(layoutResult.FooterVisible && layoutResult.ApplyVisible && layoutResult.DiscardVisible);
    }

    [Theory]
    [InlineData(360d, false)]
    [InlineData(400d, false)]
    [InlineData(360d, true)]
    [InlineData(400d, true)]
    public async Task EquipmentInputContentHostFitsACompleteTextLine(double inspectorWidth, bool driveTab)
    {
        await _ui.InvokeAsync(() =>
        {
            var project = CreateProject();
            project.Layouts[0].Components[0].Kind = LayoutComponentKind.LinearStage;
            project.Layouts[0].Components[0].BehaviorBindingId = "axis-x";
            project.Axes.Add(new VirtualAxisDefinition { Id = "axis-x", Name = "X", SoftLimitMin = -50, SoftLimitMax = 250 });
            using var layout = new MachineLayoutViewModel();
            layout.Load(project);
            layout.Select("camera-01");
            using var navigation = new ShellNavigationViewModel(false, () => false, () => false,
                () => Task.CompletedTask, () => { }, _ => { });
            navigation.IsEquipmentWorkspace = true;
            var view = new RightToolRegionView
            {
                DataContext = new InspectorDataContext(layout, navigation, IsRunMode: false, IsEquipmentDriveTabOpen: driveTab)
            };
            var window = new Window
            {
                Width = inspectorWidth, Height = 900, ShowInTaskbar = false, ShowActivated = false,
                WindowStyle = WindowStyle.None, Content = view
            };
            window.Show();
            try
            {
                window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(window.UpdateLayout));
                var fields = Descendants(view).OfType<TextBox>().Where(field => field.IsVisible && ReferenceEquals(field.DataContext, layout.SelectedComponentEditor) && field.GetBindingExpression(TextBox.TextProperty) is not null).ToArray();
                Assert.NotEmpty(fields);
                foreach (var field in fields)
                {
                    Assert.Equal(28, field.ActualHeight);
                    var contentHost = Assert.IsAssignableFrom<ScrollViewer>(field.Template.FindName("PART_ContentHost", field));
                    var line = new FormattedText("장비 Ag0123456789", System.Globalization.CultureInfo.GetCultureInfo("ko-KR"),
                        FlowDirection.LeftToRight, new Typeface(field.FontFamily, field.FontStyle, field.FontWeight, field.FontStretch),
                        field.FontSize, Brushes.Black, VisualTreeHelper.GetDpi(field).PixelsPerDip);
                    Assert.True(contentHost.ViewportHeight + 0.5 >= line.Height,
                        $"{field.Name}: input text needs {line.Height:F2} DIP, but its content viewport is {contentHost.ViewportHeight:F2} DIP.");
                }
                return true;
            }
            finally { window.Close(); }
        });
    }

    [Theory]
    [InlineData(360)]
    [InlineData(520)]
    public async Task CoordinateFieldsApplyAndReopenDistinctPlanAndHeightValues(double paneWidth)
    {
        Assert.True(await _ui.InvokeAsync(() =>
        {
            OpenVisionLanguageService.Load();
            var previousLanguage = OpenVisionLanguageService.CurrentLanguage;
            try
            {
                OpenVisionLanguageService.SetLanguage(OpenVisionLanguage.Korean, save: false);
                var project = CreateProject();
                var component = Assert.Single(Assert.Single(project.Layouts).Components);
                component.Transform = new Transform2D { X = 41, Y = 29 };
                component.VerticalEnvelope = new LayoutVerticalEnvelope { BaseElevation = 13, Height = 48 };
                using var layout = new MachineLayoutViewModel();
                layout.Load(project);
                layout.Select(component.Id);
                var editor = Assert.IsType<LayoutComponentEditorViewModel>(layout.SelectedComponentEditor);
                using var navigation = new ShellNavigationViewModel(false, () => false, () => false,
                    () => Task.CompletedTask, () => { }, _ => { });
                var view = new RightToolRegionView { DataContext = new InspectorDataContext(layout, navigation, IsRunMode: false) };
                view.Measure(new Size(paneWidth, 1100));
                view.Arrange(new Rect(0, 0, paneWidth, 1100));
                view.UpdateLayout();
                PumpBindings();
                var controls = Descendants(view).ToArray();
                var x = controls.OfType<TextBox>().Single(box => AutomationProperties.GetAutomationId(box) == "EquipmentCoordinateX" && AutomationProperties.GetName(box) == "X (이송, LU)");
                var y = controls.OfType<TextBox>().Single(box => AutomationProperties.GetAutomationId(box) == "EquipmentCoordinateY" && AutomationProperties.GetName(box) == "Y (폭, LU)");
                var z = controls.OfType<TextBox>().Single(box => AutomationProperties.GetAutomationId(box) == "EquipmentCoordinateZ" && AutomationProperties.GetName(box) == "Z (높이, LU)");
                Assert.Equal("X (이송, LU)", AutomationProperties.GetName(x));
                Assert.Equal("Y (폭, LU)", AutomationProperties.GetName(y));
                Assert.Equal("Z (높이, LU)", AutomationProperties.GetName(z));
                Assert.Equal("41", x.Text);
                Assert.Equal("29", y.Text);
                Assert.Equal("13", z.Text);
                var apply = controls.OfType<Button>().Single(button => button.Name == "EquipmentPlacementApplyButton");
                Assert.Same(editor.ApplyEquipmentPlacementDraftCommand, apply.Command);
                var changes = 0;
                layout.DefinitionChanged += (_, _) => changes++;

                x.SetCurrentValue(TextBox.TextProperty, "37.5");
                y.SetCurrentValue(TextBox.TextProperty, "NaN");
                z.SetCurrentValue(TextBox.TextProperty, "6");
                PumpBindings();
                apply.Command!.Execute(apply.CommandParameter);
                Assert.NotEmpty(editor.PlacementDraftError);
                Assert.Equal(41, component.Transform.X);
                Assert.Equal(29, component.Transform.Y);
                Assert.Equal(13, component.VerticalEnvelope.BaseElevation);
                Assert.Equal(0, changes);

                y.SetCurrentValue(TextBox.TextProperty, "-16.25");
                PumpBindings();
                Assert.Equal("-16.25", editor.DraftDepthText);
                Assert.Equal("6", editor.DraftBaseElevationText);
                Assert.Equal(29, component.Transform.Y);
                Assert.Equal(13, component.VerticalEnvelope.BaseElevation);
                apply.Command.Execute(apply.CommandParameter);
                PumpBindings();
                Assert.Equal(37.5, component.Transform.X);
                Assert.Equal(-16.25, component.Transform.Y);
                Assert.Equal(6, component.VerticalEnvelope.BaseElevation);
                Assert.Equal(48, component.VerticalEnvelope.Height);
                Assert.Equal(1, changes);
                Assert.False(editor.HasPendingPlacementDraft);

                var directory = Path.Combine(TestStorage.RootPath, "r24-b02-coordinates", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(directory);
                var projectPath = Path.Combine(directory, "coordinates.ovmachine");
                var store = new ProjectDocumentFileStore();
                store.SaveAsync(project, projectPath).GetAwaiter().GetResult();
                var reopenedProject = store.Load(projectPath);
                using var reopenedLayout = new MachineLayoutViewModel();
                reopenedLayout.Load(reopenedProject);
                reopenedLayout.Select(component.Id);
                var reopenedView = new RightToolRegionView { DataContext = new InspectorDataContext(reopenedLayout, navigation, IsRunMode: false) };
                Arrange(reopenedView);
                var reopenedFields = Descendants(reopenedView).OfType<TextBox>().ToArray();
                Assert.Equal("37.5", reopenedFields.Single(box => AutomationProperties.GetAutomationId(box) == "EquipmentCoordinateX" && AutomationProperties.GetName(box) == "X (이송, LU)").Text);
                Assert.Equal("-16.25", reopenedFields.Single(box => AutomationProperties.GetAutomationId(box) == "EquipmentCoordinateY" && AutomationProperties.GetName(box) == "Y (폭, LU)").Text);
                Assert.Equal("6", reopenedFields.Single(box => AutomationProperties.GetAutomationId(box) == "EquipmentCoordinateZ" && AutomationProperties.GetName(box) == "Z (높이, LU)").Text);
                return true;
            }
            finally { OpenVisionLanguageService.SetLanguage(previousLanguage, save: false); }
        }));
    }

    private static MachineProjectDocument CreateProject()
    {
        var station = new MachineStationDefinition { Id = "station-main", Name = "Main" };
        station.Units.Add(new MachineUnitDefinition { Id = "unit-press", Name = "Press" });
        station.Units.Add(new MachineUnitDefinition { Id = "unit-camera", Name = "Camera" });
        var definition = new MachineLayoutDefinition { Id = "layout-main", Name = "Main" };
        definition.Components.Add(new LayoutComponentDefinition
        {
            Id = "camera-01", Name = "Inspection Camera", Kind = LayoutComponentKind.Camera,
            UnitId = "unit-press", Transform = new Transform2D(), Size = new Size2D { Width = 20, Height = 20 }
        });
        var project = new MachineProjectDocument
        {
            Stations = [station],
            Layouts = [definition]
        };
        project.Simulation.ActiveLayoutId = definition.Id;
        return project;
    }

    private static MachineLayoutViewModel CreateEnvelopeLayout()
    {
        var definition = new MachineLayoutDefinition { Id = "layout-main", Name = "Main" };
        definition.Components.Add(new LayoutComponentDefinition
        {
            Id = "frame-01", Name = "Frame", Kind = LayoutComponentKind.MachineFrame,
            Transform = new Transform2D(), Size = new Size2D { Width = 100, Height = 80 }
        });
        var project = new MachineProjectDocument { Layouts = [definition] };
        project.Simulation.ActiveLayoutId = definition.Id;
        var layout = new MachineLayoutViewModel();
        layout.Load(project);
        return layout;
    }

    private static MachineProjectDocument CreateLongProductProject(OpenVisionLanguage language)
    {
        var project = CreateLongDriveProject(language);
        var primary = project.Layouts[0].Components.Single(component => component.Id == "conveyor-1");
        var prefix = language == OpenVisionLanguage.Korean ? "제품 이송 컨베이어 - " : "Product conveyor - ";
        primary.Name = prefix + new string(language == OpenVisionLanguage.Korean ? '가' : 'N', 80 - prefix.Length);
        var other = System.Text.Json.JsonSerializer.Deserialize<LayoutComponentDefinition>(System.Text.Json.JsonSerializer.Serialize(primary))!;
        other.Id = "conveyor-2";
        other.Name = prefix + new string(language == OpenVisionLanguage.Korean ? '나' : 'M', 80 - prefix.Length);
        var device = project.Devices.Single(candidate => candidate.Id == primary.BehaviorBindingId);
        var otherDevice = System.Text.Json.JsonSerializer.Deserialize<DeviceDefinition>(System.Text.Json.JsonSerializer.Serialize(device))!;
        otherDevice.Id = "device.conveyor-2";
        foreach (var id in new[] { "do.conveyor-1.run", "do.conveyor-1.reverse" })
        {
            var originalChannel = project.Channels.Single(channel => channel.Id == id);
            var channel = System.Text.Json.JsonSerializer.Deserialize<ChannelDefinition>(System.Text.Json.JsonSerializer.Serialize(originalChannel))!;
            channel.Id = id.Replace("conveyor-1", "conveyor-2", StringComparison.Ordinal);
            project.Channels.Add(channel);
        }
        otherDevice.Conveyor!.RunCommandChannelId = "do.conveyor-2.run";
        otherDevice.Conveyor.ReverseCommandChannelId = "do.conveyor-2.reverse";
        other.BehaviorBindingId = otherDevice.Id;
        project.Layouts[0].Components.Add(other);
        project.Devices.Add(otherDevice);
        Assert.Empty(new MachineProjectLayoutValidator().Validate(project).Errors);
        return project;
    }

    private static MachineProjectDocument CreateLongDriveProject(OpenVisionLanguage language)
    {
        var project = new ProjectDocumentStore().Load(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Samples", "AutomaticTransferCell.ovmachine")));
        var prefix = language == OpenVisionLanguage.Korean ? "연결 대상 - " : "Connection target - ";
        var longName = prefix + new string(language == OpenVisionLanguage.Korean ? '가' : 'N', 80 - prefix.Length);
        foreach (var channel in project.Channels) channel.Name = longName;
        foreach (var device in project.Devices) device.Name = longName;
        foreach (var axis in project.Axes) axis.Name = longName;
        project.Layouts[0].Components.Single(component => component.Id == "workpiece-1").Name = longName;
        Assert.Empty(new MachineProjectLayoutValidator().Validate(project).Errors);
        return project;
    }

    private static string[] DriveSelectionProperties(string componentId) => componentId switch
    {
        "stage-1" => ["BehaviorBindingId"],
        "sensor-1" => ["BehaviorBindingId", "SensorOutputChannelId", "SensorTargetComponentId"],
        "cylinder-1" => ["BehaviorBindingId", "CylinderExtendCommandChannelId", "CylinderExtendedSensorChannelId", "CylinderRetractedSensorChannelId"],
        "conveyor-1" => ["BehaviorBindingId", "ConveyorRunCommandChannelId", "ConveyorReverseCommandChannelId"],
        _ => throw new ArgumentOutOfRangeException(nameof(componentId))
    };

    private static string PrimaryDriveProperty(string componentId) => componentId switch
    {
        "stage-1" or "behavior-binding" => nameof(LayoutComponentEditorViewModel.BehaviorBindingId),
        "sensor-1" => nameof(LayoutComponentEditorViewModel.SensorOutputChannelId),
        "cylinder-1" => nameof(LayoutComponentEditorViewModel.CylinderExtendCommandChannelId),
        "conveyor-1" => nameof(LayoutComponentEditorViewModel.ConveyorRunCommandChannelId),
        _ => throw new ArgumentOutOfRangeException(nameof(componentId))
    };

    private static ComboBox DriveCombo(DependencyObject view, string property) => Descendants(view).OfType<ComboBox>().Single(combo =>
        combo.GetBindingExpression(Selector.SelectedValueProperty)?.ParentBinding.Path.Path == property);

    private static void SetPrimaryDriveValue(LayoutComponentEditorViewModel editor, string componentId, string value)
    {
        switch (componentId)
        {
            case "behavior-binding": editor.BehaviorBindingId = value; break;
            case "sensor-1": editor.SensorOutputChannelId = value; break;
            case "cylinder-1": editor.CylinderExtendCommandChannelId = value; break;
            case "conveyor-1": editor.ConveyorRunCommandChannelId = value; break;
            case "workpiece-1": editor.WorkpieceType = value; break;
            default: throw new ArgumentOutOfRangeException(nameof(componentId));
        }
    }


    private static MachineProjectDocument CreateLongUnitProject(OpenVisionLanguage language)
    {
        var project = CreateProject();
        var component = project.Layouts[0].Components[0];
        component.Id = "unit-frame";
        component.Name = "Unit frame";
        component.Kind = LayoutComponentKind.MachineFrame;
        var units = project.Stations[0].Units;
        var prefix = language == OpenVisionLanguage.Korean ? "검사 유닛 - " : "Inspection unit - ";
        units[0].Name = prefix + new string(language == OpenVisionLanguage.Korean ? '가' : 'N', 80 - prefix.Length);
        units[1].Name = prefix + new string(language == OpenVisionLanguage.Korean ? '나' : 'M', 80 - prefix.Length);
        for (var index = 0; index < 18; index++)
        {
            units.Add(new MachineUnitDefinition
            {
                Id = $"unit-extra-{index:00}",
                Name = prefix + new string(language == OpenVisionLanguage.Korean ? '다' : 'L', 76 - prefix.Length) + $"{index:0000}"
            });
        }
        return project;
    }

    private static void ArrangeUnitInspector(FrameworkElement view, double width)
    {
        view.Measure(new Size(width, 900));
        view.Arrange(new Rect(0, 0, width, 900));
        view.UpdateLayout();
        PumpBindings();
    }

    private static bool UnitTextFits(TextBlock text, FrameworkElement parent)
    {
        var glyph = new FormattedText(text.Text, CultureInfo.CurrentUICulture, text.FlowDirection,
            new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch), text.FontSize,
            text.Foreground, VisualTreeHelper.GetDpi(text).PixelsPerDip);
        if (text.TextWrapping != TextWrapping.NoWrap) glyph.MaxTextWidth = Math.Max(1, text.ActualWidth);
        var bounds = text.TransformToAncestor(parent).TransformBounds(new Rect(text.RenderSize));
        return glyph.Width <= text.ActualWidth + 1 && glyph.Height <= text.ActualHeight + 1 &&
            bounds.Left >= -1 && bounds.Top >= -1 && bounds.Right <= parent.ActualWidth + 1 && bounds.Bottom <= parent.ActualHeight + 1;
    }

    private static void CaptureUnitInspector(FrameworkElement view, double width, OpenVisionLanguage language, string state)
    {
        var directory = Environment.GetEnvironmentVariable("OPENVISIONLAB_MACHINE_STUDIO_UNIT_ASSIGNMENT_EVIDENCE");
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory);
        var surface = state.StartsWith("popup-", StringComparison.Ordinal) && PresentationSource.FromVisual(view)?.RootVisual is FrameworkElement popupRoot ? popupRoot : view;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(surface.ActualWidth), (int)Math.Ceiling(surface.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(surface);
        RecordUnitObservation(width, language, state + "-capture", new
        {
            surfaceType = surface.GetType().Name, surfaceWidth = surface.ActualWidth, surfaceHeight = surface.ActualHeight,
            childType = view.GetType().Name, childWidth = view.ActualWidth, childHeight = view.ActualHeight,
            childOffset = VisualTreeHelper.GetOffset(view).ToString()
        });
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(directory, $"unit-assignment-{width}-{language}-{state}.png"));
        encoder.Save(stream);
    }

    private static void RecordUnitObservation(double width, OpenVisionLanguage language, string state, object result)
    {
        var directory = Environment.GetEnvironmentVariable("OPENVISIONLAB_MACHINE_STUDIO_UNIT_ASSIGNMENT_EVIDENCE");
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, $"unit-assignment-{width}-{language}-{state}.json"), System.Text.Json.JsonSerializer.Serialize(result, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }

    private static void Arrange(FrameworkElement view)
    {
        view.Measure(new Size(520, 900));
        view.Arrange(new Rect(0, 0, 520, 900));
        view.UpdateLayout();
        Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
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

    private static void PumpBindings()
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        dispatcher.Invoke(DispatcherPriority.DataBind, new Action(() => { }));
        dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
    }

    private sealed record InspectorDataContext(
        MachineLayoutViewModel Layout,
        ShellNavigationViewModel Navigation,
        bool IsRunMode,
        bool IsEquipmentSinglePartCheckOpen = false,
        bool IsEquipmentDriveTabOpen = false);
}
