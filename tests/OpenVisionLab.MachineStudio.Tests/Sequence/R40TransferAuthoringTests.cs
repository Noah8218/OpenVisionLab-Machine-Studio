using System.IO;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using OpenVisionLab;
using OpenVisionLab.Machine.Core.Channels;
using OpenVisionLab.Machine.Core.Devices;
using OpenVisionLab.Machine.Core.Layouts;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Core.Sequences;
using OpenVisionLab.Machine.Sequence.Authoring;
using OpenVisionLab.Machine.Simulation.Compilation;
using OpenVisionLab.MachineStudio.Model;
using OpenVisionLab.MachineStudio.View.Sequence;
using OpenVisionLab.MachineStudio.View.Shell;
using OpenVisionLab.MachineStudio.ViewModel;
using OpenVisionLab.TestSupport;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

[Collection(StudioUiTestCollection.Name)]
public sealed class R40TransferAuthoringTests(StudioUiTestHost ui)
{
    [Theory]
    [InlineData(OpenVisionLanguage.Korean, true)]
    [InlineData(OpenVisionLanguage.English, true)]
    [InlineData(OpenVisionLanguage.Korean, false)]
    [InlineData(OpenVisionLanguage.English, false)]
    public Task LegacyTransferRequiresExplicitDestinationDraftAndApply(OpenVisionLanguage language, bool fullArea) => ui.InvokeAsync(() =>
    {
        var previous = OpenVisionLanguageService.CurrentLanguage; OpenVisionLanguageService.SetLanguage(language, save: false);
        var project = CreateProject(); using var main = new MainViewModel(project);
        main.Navigation.HideStartupChoice(); main.Navigation.IsProcessWorkspace = true;
        var editor = main.SequenceEditor; var window = Show(main, fullArea);
        try
        {
            string before = JsonSerializer.Serialize(project);
            WithDialog(window, dialog =>
            {
                Assert.Same(editor, dialog.DataContext); Assert.Equal(before, JsonSerializer.Serialize(project));
                Capture(dialog, $"legacy-dialog-{language}-{fullArea}", false);
                var add = Descendants(dialog).OfType<Button>().Single(button => button.CommandParameter is SequenceStepTemplateDefinition template && template.Id == "transfer-unit");
                add.BringIntoView(); Pump(dialog); Invoke(add); Pump(window);
            });
            var created = project.Sequences[0].Steps.Single(step => step.Id == "step-1");
            Assert.Equal(SequenceStepAction.TransferUnitWorkpiece, created.Action); Assert.Equal("conveyor-a", created.TargetId);
            Assert.Equal("1", created.Parameter); Assert.Equal(30000, created.TimeoutMs); Assert.True(string.IsNullOrEmpty(created.DestinationUnitId));
            Assert.NotEmpty(editor.ValidationIssues);
            var inspector = Descendants(window).OfType<SequenceStepInspectorView>().Single(view => view.IsVisible);
            var target = Descendants(inspector).OfType<ComboBox>().Single(combo => BindingOperations.GetBinding(combo, Selector.SelectedValueProperty)?.Path?.Path == "SelectedStepDraft.TargetId");
            var destination = Descendants(inspector).OfType<ComboBox>().Single(combo => BindingOperations.GetBinding(combo, Selector.SelectedValueProperty)?.Path?.Path == "SelectedStepDraft.DestinationUnitId");
            target.SetCurrentValue(Selector.SelectedValueProperty, "conveyor-b"); destination.SetCurrentValue(Selector.SelectedValueProperty, "dest-b"); Pump(window);
            Assert.Equal("conveyor-a", created.TargetId); Assert.True(string.IsNullOrEmpty(created.DestinationUnitId)); Assert.True(editor.HasPendingStepDraft);
            Invoke((Button)inspector.FindName("ApplyStepDraftButton")); Pump(window);
            Assert.Equal("conveyor-b", created.TargetId); Assert.Equal("dest-b", created.DestinationUnitId); Assert.Empty(editor.ValidationIssues);
            Assert.Equal("done", created.NextStepId); Assert.Equal("done", project.Sequences[0].Steps.Last().Id); Assert.True(main.HasUnsavedChanges); Assert.False(main.IsRunning);
            Capture(window, $"legacy-added-{language}-{(fullArea ? "work-area" : "compact")}", fullArea);
            return true;
        }
        finally { CloseFixture(main, window, previous); }
    });

    [Fact]
    public Task TransferSelectionPreviewAndSingleAddUseActualIds() => ui.InvokeAsync(() =>
    {
        var project = CreateProject(); var editor = new SequenceEditorViewModel();
        try
        {
            editor.Load(project); string before = JsonSerializer.Serialize(project); var selected = editor.SelectedStep; var draft = editor.SelectedStepDraft;
            int changed = 0, added = 0; editor.DefinitionChanged += (_, _) => changed++; editor.TransferStepAdded += (_, _) => added++;
            Assert.Equal(new[] { "conveyor-a", "conveyor-b" }, editor.TransferStepTargets.Select(target => target.Id));
            Assert.Equal(new[] { "dest-a", "dest-b" }, editor.TransferStepDestinations.Select(target => target.Id));
            Assert.Null(editor.TransferStepPreview); Assert.False(editor.AddTransferStepCommand.CanExecute(null));
            editor.TransferStepTargetId = "conveyor-b"; Assert.Null(editor.TransferStepPreview); Assert.False(editor.AddTransferStepCommand.CanExecute(null));
            editor.TransferStepDestinationId = "dest-b"; var preview = editor.TransferStepPreview!;
            Assert.Equal(SequenceStepAction.TransferUnitWorkpiece, preview.Action); Assert.Equal("conveyor-b", preview.TargetId); Assert.Equal("dest-b", preview.DestinationUnitId);
            Assert.Equal("1", preview.Parameter); Assert.Equal(30000, preview.TimeoutMs); Assert.True(string.IsNullOrEmpty(preview.WorkpieceComponentId));
            Assert.Equal(before, JsonSerializer.Serialize(project)); Assert.Same(selected, editor.SelectedStep); Assert.Same(draft, editor.SelectedStepDraft);
            Assert.False(editor.HasPendingStepDraft); Assert.Equal(0, changed + added);
            editor.AddTransferStepCommand.Execute(null); var created = project.Sequences[0].Steps.Single(step => step.Id == "step-1");
            Assert.Equal("conveyor-b", created.TargetId); Assert.Equal("dest-b", created.DestinationUnitId); Assert.Equal("source", project.Sequences[0].UnitId);
            Assert.Equal(SequenceStepAction.TransferUnitWorkpiece, created.Action); Assert.Equal("1", created.Parameter); Assert.Equal(30000, created.TimeoutMs);
            Assert.Equal("step-1", project.Sequences[0].Steps[0].NextStepId); Assert.Equal("done", created.NextStepId); Assert.Equal("done", project.Sequences[0].Steps.Last().Id);
            Assert.Empty(editor.ValidationIssues); Assert.Equal(1, changed); Assert.Equal(1, added);
            Assert.Equal(string.Empty, editor.TransferStepTargetId); Assert.Equal(string.Empty, editor.TransferStepDestinationId); Assert.Null(editor.TransferStepPreview);
            editor.AddTransferStepCommand.Execute(null); Assert.Equal(3, project.Sequences[0].Steps.Count);
            var store = new OpenVisionLab.Machine.Core.Projects.ProjectDocumentStore();
            var reopened = store.Load(store.Serialize(project));
            Assert.Equal("conveyor-b", reopened.Sequences[0].Steps[1].TargetId); Assert.Equal("dest-b", reopened.Sequences[0].Steps[1].DestinationUnitId);
            return true;
        }
        finally { editor.Dispose(); }
    });

    [Theory]
    [InlineData("readonly")]
    [InlineData("nonlinear")]
    [InlineData("common")]
    [InlineData("removed-owner")]
    [InlineData("duplicate-owner")]
    [InlineData("removed-conveyor")]
    [InlineData("duplicate-conveyor")]
    [InlineData("wrong-kind")]
    [InlineData("inactive-layout")]
    [InlineData("ambiguous-layout")]
    [InlineData("missing-destination")]
    [InlineData("self-destination")]
    [InlineData("removed-destination")]
    [InlineData("duplicate-destination")]
    [InlineData("cross-station-duplicate")]
    [InlineData("blank-target")]
    [InlineData("blank-destination")]
    [InlineData("removed-caller")]
    [InlineData("foreign-caller")]
    [InlineData("template-removed")]
    [InlineData("disposed")]
    public Task TransferRejectsMissingAmbiguousOrUnsupportedContext(string boundary) => ui.InvokeAsync(() =>
    {
        var project = CreateProject(); var editor = new SequenceEditorViewModel();
        try
        {
            editor.Load(project); editor.TransferStepTargetId = "conveyor-b"; editor.TransferStepDestinationId = "dest-b";
            switch (boundary)
            {
                case "readonly": editor.IsEditable = false; break;
                case "nonlinear": project.Sequences[0].Steps[0].NextStepId = "feed"; break;
                case "common": project.Sequences[0].UnitId = null; break;
                case "removed-owner": project.Stations[0].Units.RemoveAt(0); break;
                case "duplicate-owner": project.Stations[0].Units.Add(new() { Id = "source" }); break;
                case "removed-conveyor": project.Layouts[0].Components.RemoveAll(component => component.Id == "conveyor-b"); break;
                case "duplicate-conveyor": project.Layouts[0].Components.Add(new() { Id = "conveyor-b", Kind = LayoutComponentKind.Conveyor }); break;
                case "wrong-kind": project.Layouts[0].Components.Single(component => component.Id == "conveyor-b").Kind = LayoutComponentKind.DigitalIo; break;
                case "inactive-layout": project.Simulation.ActiveLayoutId = "missing"; break;
                case "ambiguous-layout": project.Simulation.ActiveLayoutId = null; project.Layouts.Add(new() { Id = "other-layout" }); break;
                case "missing-destination": editor.TransferStepDestinationId = string.Empty; break;
                case "self-destination": editor.TransferStepDestinationId = "source"; break;
                case "removed-destination": project.Stations[0].Units.RemoveAt(2); break;
                case "duplicate-destination": project.Stations[0].Units.Add(new() { Id = "dest-b" }); break;
                case "cross-station-duplicate": project.Stations.Add(new() { Id = "other", Units = [new() { Id = "dest-b" }] }); break;
                case "blank-target": editor.TransferStepTargetId = " "; break;
                case "blank-destination": editor.TransferStepDestinationId = " "; break;
                case "removed-caller": project.Sequences.Clear(); break;
                case "foreign-caller": editor.SelectedSequence = new() { Id = "sequence", UnitId = "source", Steps = [new() { Id = "done", Action = SequenceStepAction.Complete }] }; editor.TransferStepTargetId = "conveyor-b"; editor.TransferStepDestinationId = "dest-b"; break;
                case "template-removed": editor.Templates.Clear(); break;
                case "disposed": editor.Dispose(); break;
            }
            string before = JsonSerializer.Serialize(project);
            Assert.False(editor.AddTransferStepCommand.CanExecute(null)); editor.AddTransferStepCommand.Execute(null);
            Assert.Equal(before, JsonSerializer.Serialize(project)); return true;
        }
        finally { editor.Dispose(); }
    });

    [Theory]
    [InlineData("removed-conveyor")]
    [InlineData("removed-destination")]
    [InlineData("duplicate-destination")]
    [InlineData("owner-changed")]
    [InlineData("choice-changed")]
    [InlineData("sequence-switch")]
    [InlineData("same-id-load")]
    [InlineData("readonly")]
    [InlineData("nonlinear")]
    [InlineData("template-removed")]
    public Task TransferRevalidatesAfterDraftAdmissionAndRecovers(string boundary) => ui.InvokeAsync(() =>
    {
        var project = CreateProject(); var replacement = CreateProject(); var editor = new SequenceEditorViewModel();
        try
        {
            editor.Load(project); editor.TransferStepTargetId = "conveyor-b"; editor.TransferStepDestinationId = "dest-b";
            editor.SelectedStepDraft!.Parameter = "0.2"; int added = 0; editor.TransferStepAdded += (_, _) => added++;
            editor.PendingStepDraftPrompt = () =>
            {
                switch (boundary)
                {
                    case "removed-conveyor": project.Layouts[0].Components.RemoveAll(component => component.Id == "conveyor-b"); break;
                    case "removed-destination": project.Stations[0].Units.RemoveAt(2); break;
                    case "duplicate-destination": project.Stations[0].Units.Add(new() { Id = "dest-b" }); break;
                    case "owner-changed": project.Sequences[0].UnitId = "dest-a"; break;
                    case "choice-changed": editor.TransferStepDestinationId = "dest-a"; break;
                    case "sequence-switch": editor.DiscardStepDraft(); var other = new SequenceDefinition { Id = "other", UnitId = "source", Steps = [new() { Id = "done", Action = SequenceStepAction.Complete }] }; project.Sequences.Add(other); editor.SelectedSequence = other; editor.TransferStepTargetId = "conveyor-b"; editor.TransferStepDestinationId = "dest-b"; break;
                    case "same-id-load": editor.Load(replacement); editor.TransferStepTargetId = "conveyor-b"; editor.TransferStepDestinationId = "dest-b"; break;
                    case "readonly": editor.IsEditable = false; break;
                    case "nonlinear": project.Sequences[0].Steps[0].NextStepId = "feed"; break;
                    case "template-removed": editor.Templates.Clear(); break;
                }
                return PlacementDraftDecision.Discard;
            };
            Assert.True(editor.AddTransferStepCommand.CanExecute(null)); editor.AddTransferStepCommand.Execute(null);
            Assert.Equal(0, added); Assert.Equal(2, project.Sequences[0].Steps.Count); Assert.Equal(2, replacement.Sequences[0].Steps.Count);
            Assert.Equal("0.1", project.Sequences[0].Steps[0].Parameter);
            editor.IsEditable = true; var recovery = CreateProject(); editor.Load(recovery);
            editor.TransferStepTargetId = "conveyor-b"; editor.TransferStepDestinationId = "dest-b"; editor.AddTransferStepCommand.Execute(null);
            Assert.Equal(1, added); Assert.Equal("dest-b", recovery.Sequences[0].Steps[1].DestinationUnitId); Assert.Empty(editor.ValidationIssues);
            return true;
        }
        finally { editor.DiscardStepDraft(); editor.Dispose(); }
    });

    [Fact]
    public Task TransferRefreshUnitSequenceLoadAndDisposeKeepSelectionScoped() => ui.InvokeAsync(() =>
    {
        var project = CreateProject(); var editor = new SequenceEditorViewModel();
        try
        {
            editor.Load(project); editor.TransferStepTargetId = "conveyor-b"; editor.TransferStepDestinationId = "dest-b";
            project.Stations[0].Units[2].Name = "다음 검사 자리"; editor.RefreshAuthoringTargets();
            Assert.Equal("다음 검사 자리 (dest-b)", editor.TransferStepDestination!.Name); Assert.NotNull(editor.TransferStepPreview);
            editor.SelectedUnitId = "dest-a"; Assert.Equal(string.Empty, editor.TransferStepTargetId); Assert.Equal(string.Empty, editor.TransferStepDestinationId);
            editor.TransferStepTargetId = "conveyor-b"; editor.TransferStepDestinationId = "dest-b";
            var other = new SequenceDefinition { Id = "other", UnitId = "source", Steps = [new() { Id = "done", Action = SequenceStepAction.Complete }] };
            project.Sequences.Add(other); editor.SelectedSequence = other; Assert.Null(editor.TransferStepPreview); Assert.Equal(string.Empty, editor.TransferStepTargetId);
            editor.TransferStepTargetId = "conveyor-b"; editor.TransferStepDestinationId = "dest-b"; editor.Load(CreateProject());
            Assert.Null(editor.TransferStepPreview); Assert.Equal(string.Empty, editor.TransferStepTargetId); Assert.Equal(string.Empty, editor.TransferStepDestinationId);
            editor.TransferStepTargetId = "conveyor-b"; editor.TransferStepDestinationId = "dest-b";
            project = CreateProject(); editor.Load(project); editor.TransferStepTargetId = "conveyor-b"; editor.TransferStepDestinationId = "dest-b";
            project.Layouts[0].Components.RemoveAll(component => component.Id == "conveyor-b"); editor.RefreshAuthoringTargets(); Assert.Equal(string.Empty, editor.TransferStepTargetId);
            editor.Dispose(); Assert.Empty(editor.TransferStepTargets); Assert.Empty(editor.TransferStepDestinations); Assert.Null(editor.TransferStepPreview);
            return true;
        }
        finally { editor.Dispose(); }
    });

    [Theory]
    [InlineData(OpenVisionLanguage.Korean, true)]
    [InlineData(OpenVisionLanguage.English, true)]
    [InlineData(OpenVisionLanguage.Korean, false)]
    [InlineData(OpenVisionLanguage.English, false)]
    public Task RenderedTransferChoicesPreviewAddAndInspectorApplyRemainSeparate(OpenVisionLanguage language, bool fullArea) => ui.InvokeAsync(() =>
    {
        var previous = OpenVisionLanguageService.CurrentLanguage; OpenVisionLanguageService.SetLanguage(language, save: false);
        var project = CreateProject(); using var main = new MainViewModel(project); main.Navigation.HideStartupChoice(); main.Navigation.IsProcessWorkspace = true;
        var editor = main.SequenceEditor; var window = Show(main, fullArea);
        try
        {
            string before = JsonSerializer.Serialize(project); var selected = editor.SelectedStep; var draft = editor.SelectedStepDraft;
            bool dirty = main.HasUnsavedChanges; var clock = main.SceneSnapshots.Latest!.SimulationTime;
            string snapshot = MaterialSnapshot(main);
            WithDialog(window, dialog =>
            {
                Assert.Same(editor, dialog.DataContext); var area = (Expander)dialog.FindName("TransferStepAuthoring"); Assert.False(area.IsExpanded);
                area.IsExpanded = true; WaitForTransferContent(dialog, area);
                var target = (ComboBox)dialog.FindName("TransferStepTarget"); var destination = (ComboBox)dialog.FindName("TransferStepDestination"); var add = (Button)dialog.FindName("AddTransferStepButton");
                Assert.Null(target.SelectedItem); Assert.Null(destination.SelectedItem); Assert.False(add.IsEnabled);
                Assert.Equal(2, target.Items.Count); Assert.Equal(2, destination.Items.Count);
                Assert.Equal("검사 유닛 (source)", ((TextBlock)dialog.FindName("TransferStepOwner")).Text.Replace("\u2060", string.Empty));
                target.SetCurrentValue(Selector.SelectedValueProperty, "conveyor-b"); Pump(dialog); Assert.Equal("conveyor-b", editor.TransferStepTargetId); Assert.False(add.IsEnabled);
                destination.SetCurrentValue(Selector.SelectedValueProperty, "dest-b"); Pump(dialog); Assert.Equal("dest-b", editor.TransferStepDestinationId);
                editor.TransferStepTargetId = "conveyor-a"; editor.TransferStepDestinationId = "dest-a"; Pump(dialog);
                Assert.Equal("conveyor-a", target.SelectedValue); Assert.Equal("dest-a", destination.SelectedValue);
                target.SetCurrentValue(Selector.SelectedValueProperty, "conveyor-b"); destination.SetCurrentValue(Selector.SelectedValueProperty, "dest-b"); Pump(dialog);
                Assert.Equal("제품 이송 컨베이어 (conveyor-b)", ((TextBlock)dialog.FindName("TransferStepPreviewTarget")).Text.Replace("\u2060", string.Empty));
                Assert.Equal("검사 유닛 (dest-b)", ((TextBlock)dialog.FindName("TransferStepPreviewDestination")).Text.Replace("\u2060", string.Empty));
                Assert.Equal("1 s", ((TextBlock)dialog.FindName("TransferStepPreviewValue")).Text); Assert.Equal("30000", ((TextBlock)dialog.FindName("TransferStepPreviewTimeout")).Text);
                Assert.Equal(before, JsonSerializer.Serialize(project)); Assert.Same(selected, editor.SelectedStep); Assert.Same(draft, editor.SelectedStepDraft);
                Assert.False(editor.HasPendingStepDraft || main.IsRunning); Assert.Equal(dirty, main.HasUnsavedChanges); Assert.Equal(clock, main.SceneSnapshots.Latest!.SimulationTime); Assert.Equal(snapshot, MaterialSnapshot(main));
                editor.IsEditable = false; Pump(dialog); Assert.False(target.IsEnabled || destination.IsEnabled || add.IsEnabled); Capture(dialog, $"transfer-readonly-{language}-{fullArea}", false);
                editor.IsEditable = true; Pump(dialog); Assert.True(add.IsEnabled);
                var camera = (Expander)dialog.FindName("CameraStepAuthoring"); double collapsed = camera.ActualHeight;
                camera.IsExpanded = true; WaitForTransferContent(dialog, area); camera.IsExpanded = false; WaitForTransferContent(dialog, area, collapsed);
                area.IsExpanded = false; Pump(dialog); area.IsExpanded = true; WaitForTransferContent(dialog, area);
                Capture(dialog, $"transfer-preview-{language}-{fullArea}", false); Assert.Same(editor.AddTransferStepCommand, add.Command); Invoke(add); Pump(window); Assert.False(dialog.IsVisible);
            });
            var created = project.Sequences[0].Steps.Single(step => step.Id == "step-1");
            Assert.Equal("conveyor-b", created.TargetId); Assert.Equal("dest-b", created.DestinationUnitId); Assert.Empty(editor.ValidationIssues);
            Assert.True(main.HasUnsavedChanges); Assert.False(main.IsRunning); Assert.Equal(clock, main.SceneSnapshots.Latest!.SimulationTime); Assert.Equal(snapshot, MaterialSnapshot(main));
            Capture(window, $"transfer-added-{language}-{(fullArea ? "work-area" : "compact")}", fullArea);
            editor.SelectedStepDraft!.DestinationUnitId = "dest-a"; Assert.Equal("dest-b", created.DestinationUnitId); editor.DiscardStepDraft();
            editor.SelectedStepDraft!.DestinationUnitId = "dest-a";
            var inspector = Descendants(window).OfType<SequenceStepInspectorView>().Single(view => view.IsVisible);
            Invoke((Button)inspector.FindName("ApplyStepDraftButton")); Pump(window); Assert.Equal("dest-a", created.DestinationUnitId); Assert.False(editor.HasPendingStepDraft);
            return true;
        }
        finally { CloseFixture(main, window, previous); }
    });

    [Theory]
    [InlineData(OpenVisionLanguage.Korean)]
    [InlineData(OpenVisionLanguage.English)]
    public Task TransferCancelRetainsDraftAndChoicesUntilExplicitSuccess(OpenVisionLanguage language) => ui.InvokeAsync(() =>
    {
        var previous = OpenVisionLanguageService.CurrentLanguage; OpenVisionLanguageService.SetLanguage(language, save: false);
        var project = CreateProject(); using var main = new MainViewModel(project); main.Navigation.HideStartupChoice(); main.Navigation.IsProcessWorkspace = true;
        var editor = main.SequenceEditor; var window = Show(main, false);
        try
        {
            editor.SelectedStepDraft!.Parameter = "0.2"; editor.PendingStepDraftPrompt = () => PlacementDraftDecision.Cancel; string before = JsonSerializer.Serialize(project);
            WithDialog(window, dialog =>
            {
                var area = (Expander)dialog.FindName("TransferStepAuthoring"); area.IsExpanded = true; WaitForTransferContent(dialog, area);
                ((ComboBox)dialog.FindName("TransferStepTarget")).SetCurrentValue(Selector.SelectedValueProperty, "conveyor-b");
                ((ComboBox)dialog.FindName("TransferStepDestination")).SetCurrentValue(Selector.SelectedValueProperty, "dest-b"); Pump(dialog);
                Invoke((Button)dialog.FindName("AddTransferStepButton")); Pump(window);
                Assert.True(dialog.IsVisible && editor.HasPendingStepDraft); Assert.Equal("conveyor-b", editor.TransferStepTargetId); Assert.Equal("dest-b", editor.TransferStepDestinationId); Assert.Equal(before, JsonSerializer.Serialize(project));
                editor.PendingStepDraftPrompt = () => PlacementDraftDecision.Discard; Invoke((Button)dialog.FindName("AddTransferStepButton")); Pump(window); Assert.False(dialog.IsVisible);
            });
            Assert.Equal(3, project.Sequences[0].Steps.Count); Assert.Equal("0.1", project.Sequences[0].Steps[0].Parameter); return true;
        }
        finally { CloseFixture(main, window, previous); }
    });

    [Theory]
    [InlineData(OpenVisionLanguage.Korean)]
    [InlineData(OpenVisionLanguage.English)]
    public Task TransferLongIdentitiesAndDataContextReplacementKeepResultLifetime(OpenVisionLanguage language) => ui.InvokeAsync(() =>
    {
        var previous = OpenVisionLanguageService.CurrentLanguage; OpenVisionLanguageService.SetLanguage(language, save: false);
        var project = CreateProject(); project.Layouts[0].Components.Single(component => component.Id == "conveyor-b").Name = string.Join(" ", Enumerable.Repeat(language == OpenVisionLanguage.Korean ? "두 번째 검사 자리로 제품을 옮기는 컨베이어" : "Conveyor carrying product to the second inspection position", 5));
        project.Stations[0].Units[2].Name = string.Join(" ", Enumerable.Repeat(language == OpenVisionLanguage.Korean ? "다음 촬영을 위한 제품 도착 유닛" : "Product destination unit for the next acquisition", 5));
        using var main = new MainViewModel(project); main.Navigation.HideStartupChoice(); main.Navigation.IsProcessWorkspace = true; var window = Show(main, false);
        try
        {
            WithDialog(window, dialog =>
            {
                var area = (Expander)dialog.FindName("TransferStepAuthoring"); area.IsExpanded = true; WaitForTransferContent(dialog, area);
                ((ComboBox)dialog.FindName("TransferStepTarget")).SetCurrentValue(Selector.SelectedValueProperty, "conveyor-b");
                ((ComboBox)dialog.FindName("TransferStepDestination")).SetCurrentValue(Selector.SelectedValueProperty, "dest-b"); WaitForTransferContent(dialog, area);
                foreach (var pair in new[] { (Name: "TransferStepPreviewTarget", Value: main.SequenceEditor.TransferStepTarget!.Name), (Name: "TransferStepPreviewDestination", Value: main.SequenceEditor.TransferStepDestination!.Name) })
                {
                    var block = (TextBlock)dialog.FindName(pair.Name); Assert.Equal(pair.Value, block.Text.Replace("\u2060", string.Empty));
                    block.Measure(new Size(block.ActualWidth, double.PositiveInfinity)); Assert.True(block.DesiredSize.Height <= block.ActualHeight + block.Margin.Top + block.Margin.Bottom + 1);
                }
                Capture(dialog, $"transfer-long-{language}", false);
                var replacement = new SequenceEditorViewModel(); replacement.Load(CreateProject()); replacement.TransferStepTargetId = "conveyor-b"; replacement.TransferStepDestinationId = "dest-b";
                try
                {
                    dialog.DataContext = replacement; Pump(dialog);
                    main.SequenceEditor.AddTransferStepCommand.Execute(null); Pump(window); Assert.True(dialog.IsVisible);
                    var add = (Button)dialog.FindName("AddTransferStepButton"); add.BringIntoView(); Pump(dialog); Invoke(add); Pump(window); Assert.False(dialog.IsVisible);
                    replacement.TransferStepTargetId = "conveyor-a"; replacement.TransferStepDestinationId = "dest-a"; replacement.AddTransferStepCommand.Execute(null); Assert.Equal(4, replacement.SelectedSequence!.Steps.Count);
                }
                finally { replacement.Dispose(); }
            });
            return true;
        }
        finally { CloseFixture(main, window, previous); }
    });

    [Fact]
    public Task TransferChoicesAndAddStayLockedDuringIsolatedStepTest() => ui.InvokeTaskAsync(async () =>
    {
        var project = CreateProject(); var editor = new SequenceEditorViewModel(); int busyEntries = 0;
        System.ComponentModel.PropertyChangedEventHandler handler = (_, args) =>
        {
            if (args.PropertyName != nameof(editor.IsStepTestBusy) || !editor.IsStepTestBusy) return;
            busyEntries++; string before = JsonSerializer.Serialize(project); Assert.False(editor.AddTransferStepCommand.CanExecute(null)); editor.AddTransferStepCommand.Execute(null);
            editor.TransferStepTargetId = "conveyor-a"; editor.TransferStepDestinationId = "dest-a";
            Assert.Equal("conveyor-b", editor.TransferStepTargetId); Assert.Equal("dest-b", editor.TransferStepDestinationId); Assert.Equal(before, JsonSerializer.Serialize(project));
        };
        try
        {
            project.Sequences[0].Steps[0] = new() { Id = "move", Action = SequenceStepAction.MoveAxis, TargetId = "axis", Parameter = "2" };
            editor.Load(project); editor.TransferStepTargetId = "conveyor-b"; editor.TransferStepDestinationId = "dest-b"; editor.PropertyChanged += handler;
            await editor.TestSelectedStepAsync(); Assert.Equal(1, busyEntries); Assert.False(editor.IsStepTestBusy); Assert.True(editor.StepTestResult!.IsCompleted);
            Assert.True(editor.AddTransferStepCommand.CanExecute(null)); editor.AddTransferStepCommand.Execute(null); Assert.Equal(3, project.Sequences[0].Steps.Count); return true;
        }
        finally { editor.PropertyChanged -= handler; editor.Dispose(); }
    });

    [Theory]
    [InlineData(OpenVisionLanguage.Korean)]
    [InlineData(OpenVisionLanguage.English)]
    public Task TransferUnavailableContextShowsRequiredInputsAndRecovers(OpenVisionLanguage language) => ui.InvokeAsync(() =>
    {
        var previous = OpenVisionLanguageService.CurrentLanguage; OpenVisionLanguageService.SetLanguage(language, save: false);
        var project = CreateProject(); project.Sequences[0].UnitId = null;
        using var main = new MainViewModel(project); main.Navigation.HideStartupChoice(); main.Navigation.IsProcessWorkspace = true; var window = Show(main, false);
        try
        {
            WithDialog(window, dialog =>
            {
                var area = (Expander)dialog.FindName("TransferStepAuthoring"); area.IsExpanded = true; WaitForTransferContent(dialog, area);
                var target = (ComboBox)dialog.FindName("TransferStepTarget"); var destination = (ComboBox)dialog.FindName("TransferStepDestination"); var add = (Button)dialog.FindName("AddTransferStepButton");
                Assert.Empty(target.Items); Assert.Empty(destination.Items); Assert.False(add.IsEnabled);
                Assert.Equal("—", ((TextBlock)dialog.FindName("TransferStepOwner")).Text);
                Assert.Equal(OpenVisionLanguageService.T("R40.TransferStepUnavailable"), ((TextBlock)dialog.FindName("TransferStepHint")).Text);
                Capture(dialog, $"transfer-empty-{language}", false);
                main.SequenceEditor.SelectedUnitId = "source"; Pump(dialog);
                Assert.Equal(2, target.Items.Count); Assert.Equal(2, destination.Items.Count); Assert.Null(target.SelectedItem); Assert.Null(destination.SelectedItem); Assert.False(add.IsEnabled);
                target.SetCurrentValue(Selector.SelectedValueProperty, "conveyor-b"); destination.SetCurrentValue(Selector.SelectedValueProperty, "dest-b"); Pump(dialog);
                Assert.True(add.IsEnabled); Invoke(add); Pump(window); Assert.False(dialog.IsVisible);
            });
            Assert.Equal("dest-b", project.Sequences[0].Steps.Single(step => step.Id == "step-1").DestinationUnitId); Assert.False(main.IsRunning); return true;
        }
        finally { CloseFixture(main, window, previous); }
    });

    private static string MaterialSnapshot(MainViewModel main) => JsonSerializer.Serialize(new { main.SceneSnapshots.Latest!.Axes, main.SceneSnapshots.Latest.Signals, main.SceneSnapshots.Latest.Cameras, main.SceneSnapshots.Latest.UnitMaterialSlots, main.SceneSnapshots.Latest.UnitMaterialExits, main.SceneSnapshots.Latest.MaterialSupply });

    private static MachineProjectDocument CreateProject()
    {
        var project = R40ProcessWorkspaceTests.CreateProject();
        project.Stations = [new MachineStationDefinition { Id = "station", Name = "공정 장비", Units = [new() { Id = "source", Name = "검사 유닛" }, new() { Id = "dest-a", Name = "검사 유닛" }, new() { Id = "dest-b", Name = "검사 유닛" }] }];
        project.Sequences[0].UnitId = "source";
        project.Sequences[0].Steps = [new() { Id = "feed", Name = "준비 대기", Action = SequenceStepAction.Delay, Parameter = "0.1" }, new() { Id = "done", Name = "완료", Action = SequenceStepAction.Complete }];
        project.Simulation.ActiveLayoutId = "layout";
        project.Channels.AddRange([
            new() { Id = "run-a", Kind = ChannelKind.DigitalOutput }, new() { Id = "reverse-a", Kind = ChannelKind.DigitalOutput },
            new() { Id = "run-b", Kind = ChannelKind.DigitalOutput }, new() { Id = "reverse-b", Kind = ChannelKind.DigitalOutput }
        ]);
        project.Devices.AddRange([
            new() { Id = "drive-a", Name = "이송 구동", Kind = DeviceKind.Conveyor, ChannelIds = ["run-a", "reverse-a"], Conveyor = new() { RunCommandChannelId = "run-a", ReverseCommandChannelId = "reverse-a" } },
            new() { Id = "drive-b", Name = "이송 구동", Kind = DeviceKind.Conveyor, ChannelIds = ["run-b", "reverse-b"], Conveyor = new() { RunCommandChannelId = "run-b", ReverseCommandChannelId = "reverse-b" } }
        ]);
        project.Layouts[0].Components.AddRange([
            new() { Id = "conveyor-a", Name = "제품 이송 컨베이어", Kind = LayoutComponentKind.Conveyor, BehaviorBindingId = "drive-a", UnitId = "source", Size = new() { Width = 200, Height = 40 } },
            new() { Id = "conveyor-b", Name = "제품 이송 컨베이어", Kind = LayoutComponentKind.Conveyor, BehaviorBindingId = "drive-b", UnitId = "source", Size = new() { Width = 200, Height = 40 }, Transform = new() { Y = 80 } }
        ]);
        return project;
    }

    private static ShellWindow Show(MainViewModel main, bool fullArea)
    {
        var window = new ShellWindow { DataContext = main, ShowInTaskbar = false, ShowActivated = false };
        SmokeDpiTestHook.PlaceOnTestMonitor(window, 1280, 760);
        window.Show();
        if (fullArea)
        {
            var area = SmokeDpiTestHook.CaptureMonitorEvidence(window).WorkingArea.Split(',').Select(int.Parse).ToArray();
            var dpi = VisualTreeHelper.GetDpi(window);
            window.Left = area[0] / dpi.DpiScaleX; window.Top = area[1] / dpi.DpiScaleY;
            window.Width = area[2] / dpi.DpiScaleX; window.Height = area[3] / dpi.DpiScaleY;
        }
        Pump(window);
        return window;
    }

    private static void WithDialog(Window window, Action<SequenceStepTemplateDialog> action)
    {
        Exception? failure = null;
        bool completed = false;
        var operation = window.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
        {
            var dialog = Application.Current.Windows.OfType<SequenceStepTemplateDialog>().Single(item => ReferenceEquals(item.Owner, window));
            try { dialog.UpdateLayout(); action(dialog); completed = true; }
            catch (Exception error) { failure = error; }
            finally { if (dialog.IsVisible) dialog.Close(); }
        }));
        try
        {
            var view = Descendants(window).OfType<SequenceEditorView>().Single(item => item.IsVisible);
            Assert.True(view.DockedAddStepButton.IsEnabled);
            view.DockedAddStepButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }
        finally { operation.Abort(); }
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
        Assert.True(completed, "The existing owned template-dialog entry must execute the inspection callback.");
    }

    private static void Pump(Window window) => window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(window.UpdateLayout));
    private static void Invoke(Button button) => Assert.IsAssignableFrom<IInvokeProvider>(new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private static void CloseFixture(MainViewModel main, Window window, OpenVisionLanguage previous)
    {
        main.SequenceEditor.DiscardStepDraft(); window.DataContext = null; window.Close();
        Pump(window);
        var remaining = Application.Current.Windows.Cast<Window>().ToArray();
        var folder = Path.Combine(TestStorage.RootPath, "transfer-authoring"); Directory.CreateDirectory(folder);
        File.AppendAllText(Path.Combine(folder, "fixture-windows.jsonl"), JsonSerializer.Serialize(new { ownWindowRetained = remaining.Contains(window),
            window.IsVisible, dirty = main.HasUnsavedChanges, remainingWindows = remaining.Select(item => new { type = item.GetType().Name, item.Title, item.IsVisible }).ToArray() }) + Environment.NewLine);
        Assert.DoesNotContain(window, remaining); Assert.False(window.IsVisible);
        OpenVisionLanguageService.SetLanguage(previous, save: false);
    }

    private static void WaitForTransferContent(Window window, Expander expander, double? cameraCollapsedHeight = null)
    {
        var names = new[] { "TransferStepOwner", "TransferStepTarget", "TransferStepDestination", "TransferStepPreviewTarget", "TransferStepPreviewDestination", "TransferStepPreviewValue", "TransferStepPreviewTimeout", "TransferStepHint", "AddTransferStepButton" };
        var probes = Descendants((DependencyObject)expander.Content).OfType<FrameworkElement>().Where(element => names.Contains(element.Name)).ToArray();
        bool Ready(out string signature)
        {
            signature = expander.TransformToAncestor(window).TransformBounds(new Rect(expander.RenderSize)).ToString(System.Globalization.CultureInfo.InvariantCulture)
                + window.ActualHeight.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + window.Top.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
            if (window is SequenceStepTemplateDialog dialog && dialog.FindName("CameraStepAuthoring") is Expander camera)
            {
                if (cameraCollapsedHeight is { } height && (camera.IsExpanded || Math.Abs(camera.ActualHeight - height) > 1e-6)) return false;
                signature += camera.ActualHeight.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
                if (camera.IsExpanded && camera.Content is FrameworkElement content)
                {
                    var cameraBounds = new Rect(camera.RenderSize); cameraBounds.Inflate(1e-6, 1e-6);
                    if (!cameraBounds.Contains(content.TransformToAncestor(camera).TransformBounds(new Rect(content.RenderSize)))) return false;
                    for (DependencyObject? item = content; item is not null && item != camera; item = VisualTreeHelper.GetParent(item))
                    {
                        if (item is UIElement element && element.Opacity < 0.99) return false;
                    }
                }
            }
            if (probes.Length != names.Length) return false;
            var outer = new Rect(expander.RenderSize); outer.Inflate(1e-6, 1e-6);
            foreach (var probe in probes)
            {
                if (probe is TextBlock { Text.Length: 0 }) continue;
                Rect bounds = probe.TransformToAncestor(expander).TransformBounds(new Rect(probe.RenderSize));
                if (!probe.IsVisible || probe.ActualHeight <= 0 || !outer.Contains(bounds)) return false;
                signature += bounds.ToString(System.Globalization.CultureInfo.InvariantCulture);
                for (DependencyObject? item = probe; item is not null && item != expander; item = VisualTreeHelper.GetParent(item))
                {
                    if (item is UIElement element)
                    {
                        if (probe.IsEnabled && element.Opacity < 0.99) return false;
                        signature += element.Opacity.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
                    }
                    if (item is not Visual visual || VisualTreeHelper.GetClip(visual) is not { } clip) continue;
                    var clipBounds = clip.Bounds; clipBounds.Inflate(1e-6, 1e-6);
                    if (!clipBounds.Contains(probe.TransformToAncestor(visual).TransformBounds(new Rect(probe.RenderSize)))) return false;
                }
            }
            return true;
        }
        var frame = new DispatcherFrame(); var timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
        string previous = string.Empty; int stable = 0, ticks = 0;
        EventHandler handler = (_, _) =>
        {
            window.UpdateLayout(); bool ready = Ready(out string signature);
            stable = ready && signature == previous ? stable + 1 : 0; previous = signature;
            if (stable >= 2 || ++ticks >= 125) frame.Continue = false;
        };
        timer.Tick += handler;
        try { timer.Start(); Dispatcher.PushFrame(frame); }
        finally { timer.Stop(); timer.Tick -= handler; }
        Assert.True(stable >= 2 && Ready(out _), "Transfer section must finish revealing before capture; no product animation or native-input acceptance change.");
        probes.Single(probe => probe.Name == "AddTransferStepButton").BringIntoView(); Pump(window);
        var scroll = Descendants(window).OfType<ScrollViewer>().Single(viewer => viewer.Content is Panel panel && panel.Children.Contains(expander));
        var presenter = Descendants(scroll).OfType<ScrollContentPresenter>().Single(item => ReferenceEquals(item.TemplatedParent, scroll));
        var viewport = new Rect(presenter.RenderSize); viewport.Inflate(1e-6, 1e-6);
        foreach (var probe in probes)
        {
            if (probe is TextBlock { Text.Length: 0 }) continue;
            Assert.True(viewport.Contains(probe.TransformToAncestor(presenter).TransformBounds(new Rect(probe.RenderSize))), probe.Name + " must be fully visible after scrolling to the transfer action.");
        }
    }

    private static void Capture(Window window, string name, bool fullArea)
    {
        Pump(window);
        if (window is SequenceStepTemplateDialog dialog && dialog.FindName("TransferStepAuthoring") is Expander { IsExpanded: true } expander) WaitForTransferContent(window, expander);
        var monitor = SmokeDpiTestHook.CaptureMonitorEvidence(window);
        var dpi = VisualTreeHelper.GetDpi(window);
        var folder = Path.Combine(TestStorage.RootPath, "transfer-authoring"); Directory.CreateDirectory(folder);
        new SmokeWindowCapture().Capture(window, Path.Combine(folder, name + ".png"));
        File.WriteAllText(Path.Combine(folder, name + ".json"), JsonSerializer.Serialize(new { monitor, width = (int)Math.Round(window.ActualWidth * dpi.DpiScaleX),
            height = (int)Math.Round(window.ActualHeight * dpi.DpiScaleY), wpfDpi = dpi.PixelsPerInchX, fullArea, crop = "none",
            method = "Actual in-process WPF RenderTargetBitmap; dialog is a separate supplemental window image, never composited into the FHD parent." }));
        Assert.True(monitor.WindowContainedByMonitor && monitor.WindowIntersectsMonitor, JsonSerializer.Serialize(monitor));
        var area = monitor.WorkingArea.Split(',').Select(int.Parse).ToArray(); var rect = monitor.WindowRect.Split(',').Select(int.Parse).ToArray();
        Assert.True(new Rect(area[0], area[1], area[2], area[3]).Contains(new Rect(rect[0], rect[1], rect[2], rect[3])), "Owned capture must stay inside the actual work area: " + JsonSerializer.Serialize(monitor));
    }
}
