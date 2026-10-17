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
public sealed class R40WorkpieceAuthoringTests(StudioUiTestHost ui)
{
    [Theory]
    [InlineData(OpenVisionLanguage.Korean, true)]
    [InlineData(OpenVisionLanguage.English, true)]
    [InlineData(OpenVisionLanguage.Korean, false)]
    [InlineData(OpenVisionLanguage.English, false)]
    public Task LegacyWorkpieceTemplatesRequireExplicitInspectorAssociation(OpenVisionLanguage language, bool fullArea) => ui.InvokeAsync(() =>
    {
        var previous = OpenVisionLanguageService.CurrentLanguage; OpenVisionLanguageService.SetLanguage(language, save: false);
        var project = CreateProject(); using var main = new MainViewModel(project); main.Navigation.HideStartupChoice(); main.Navigation.IsProcessWorkspace = true;
        var editor = main.SequenceEditor; var window = Show(main, fullArea);
        try
        {
            var clock = main.SceneSnapshots.Latest!.SimulationTime; string snapshot = MaterialSnapshot(main);
            foreach (string id in new[] { "feed-workpiece", "eject-workpiece" })
            {
                string before = JsonSerializer.Serialize(project);
                WithDialog(window, dialog =>
                {
                    Assert.Same(editor, dialog.DataContext); Assert.Equal(before, JsonSerializer.Serialize(project));
                    if (id == "feed-workpiece") Capture(dialog, $"legacy-dialog-{language}-{fullArea}", false);
                    var add = Descendants(dialog).OfType<Button>().Single(button => button.CommandParameter is SequenceStepTemplateDefinition template && template.Id == id);
                    add.BringIntoView(); Pump(dialog); Invoke(add); Pump(window);
                });
                var created = project.Sequences[0].Steps.Single(step => step.Id == editor.SelectedStep!.Id);
                Assert.Equal(string.Empty, created.TargetId); Assert.True(string.IsNullOrEmpty(created.WorkpieceComponentId)); Assert.Equal(string.Empty, created.Parameter); Assert.Equal(0, created.TimeoutMs);
                Assert.Contains(editor.ValidationIssues, issue => issue.Code == OpenVisionLab.Machine.Sequence.Compilation.SequenceCompilationErrorCode.WorkpieceComponentIdRequired);
                var inspector = Descendants(window).OfType<SequenceStepInspectorView>().Single(view => view.IsVisible);
                var product = Descendants(inspector).OfType<ComboBox>().Single(combo => BindingOperations.GetBinding(combo, Selector.SelectedValueProperty)?.Path?.Path == "SelectedStepDraft.WorkpieceComponentId");
                product.SetCurrentValue(Selector.SelectedValueProperty, "product-b"); Pump(window);
                Assert.True(string.IsNullOrEmpty(created.WorkpieceComponentId)); Assert.True(editor.HasPendingStepDraft);
                Invoke((Button)inspector.FindName("ApplyStepDraftButton")); Pump(window);
                Assert.Equal("product-b", created.WorkpieceComponentId); Assert.Empty(editor.ValidationIssues);
            }
            Assert.Equal(new[] { SequenceStepAction.FeedWorkpiece, SequenceStepAction.EjectWorkpiece }, project.Sequences[0].Steps.Skip(1).Take(2).Select(step => step.Action));
            Assert.Equal("done", project.Sequences[0].Steps.Last().Id); Assert.True(main.HasUnsavedChanges); Assert.False(main.IsRunning);
            Assert.Equal(clock, main.SceneSnapshots.Latest!.SimulationTime); Assert.Equal(snapshot, MaterialSnapshot(main));
            Capture(window, $"legacy-added-{language}-{(fullArea ? "work-area" : "compact")}", fullArea); return true;
        }
        finally { CloseFixture(main, window, previous); }
    });

    [Theory]
    [InlineData("feed-workpiece", SequenceStepAction.FeedWorkpiece, false)]
    [InlineData("eject-workpiece", SequenceStepAction.EjectWorkpiece, false)]
    [InlineData("feed-workpiece", SequenceStepAction.FeedWorkpiece, true)]
    [InlineData("eject-workpiece", SequenceStepAction.EjectWorkpiece, true)]
    public Task WorkpiecePreviewAndAddKeepExactComponentAndSingleFactory(string template, SequenceStepAction action, bool common) => ui.InvokeAsync(() =>
    {
        var project = CreateProject(); if (common) project.Sequences[0].UnitId = null; var editor = new SequenceEditorViewModel();
        try
        {
            editor.Load(project); editor.TransferStepTargetId = "conveyor-b"; editor.TransferStepDestinationId = "dest-b";
            string before = JsonSerializer.Serialize(project); var selected = editor.SelectedStep; var draft = editor.SelectedStepDraft;
            int changed = 0, added = 0, otherAdded = 0; editor.DefinitionChanged += (_, _) => changed++; editor.WorkpieceStepAdded += (_, _) => added++;
            editor.AxisStepAdded += (_, _) => otherAdded++; editor.CameraStepAdded += (_, _) => otherAdded++; editor.SignalStepAdded += (_, _) => otherAdded++;
            editor.SubsequenceStepAdded += (_, _) => otherAdded++; editor.UnitStepAdded += (_, _) => otherAdded++; editor.TransferStepAdded += (_, _) => otherAdded++;
            Assert.Equal(new[] { "product-a", "product-b" }, editor.WorkpieceStepTargets.Select(target => target.Id)); Assert.Empty(editor.WorkpieceStepTemplates);
            Assert.Null(editor.WorkpieceStepPreview); Assert.False(editor.AddWorkpieceStepCommand.CanExecute(null));
            editor.WorkpieceStepTargetId = "product-b"; Assert.Null(editor.WorkpieceStepPreview); Assert.False(editor.AddWorkpieceStepCommand.CanExecute(null));
            Assert.Equal(new[] { "feed-workpiece", "eject-workpiece" }, editor.WorkpieceStepTemplates.Select(item => item.Id)); editor.WorkpieceStepTemplateId = template;
            var preview = editor.WorkpieceStepPreview!; Assert.Equal(action, preview.Action); Assert.Equal("product-b", preview.WorkpieceComponentId);
            Assert.Equal(string.Empty, preview.TargetId); Assert.Equal(string.Empty, preview.Parameter); Assert.Equal(0, preview.TimeoutMs); Assert.True(string.IsNullOrEmpty(preview.DestinationUnitId));
            Assert.Equal(before, JsonSerializer.Serialize(project)); Assert.Same(selected, editor.SelectedStep); Assert.Same(draft, editor.SelectedStepDraft); Assert.False(editor.HasPendingStepDraft); Assert.Equal(0, changed + added + otherAdded);
            editor.AddWorkpieceStepCommand.Execute(null); var created = project.Sequences[0].Steps.Single(step => step.Id == "step-1");
            Assert.Equal(action, created.Action); Assert.Equal("product-b", created.WorkpieceComponentId); Assert.Equal(string.Empty, created.TargetId); Assert.Equal(string.Empty, created.Parameter); Assert.Equal(0, created.TimeoutMs);
            Assert.Equal("step-1", project.Sequences[0].Steps[0].NextStepId); Assert.Equal("done", created.NextStepId); Assert.Equal("done", project.Sequences[0].Steps.Last().Id); Assert.Empty(editor.ValidationIssues);
            Assert.Equal(1, changed); Assert.Equal(1, added); Assert.Equal(0, otherAdded); Assert.Equal(string.Empty, editor.WorkpieceStepTargetId); Assert.Equal(string.Empty, editor.WorkpieceStepTemplateId);
            Assert.Equal("conveyor-b", editor.TransferStepTargetId); Assert.Equal("dest-b", editor.TransferStepDestinationId);
            editor.AddWorkpieceStepCommand.Execute(null); Assert.Equal(3, project.Sequences[0].Steps.Count);
            var store = new OpenVisionLab.Machine.Core.Projects.ProjectDocumentStore(); var reopened = store.Load(store.Serialize(project));
            Assert.Equal("product-b", reopened.Sequences[0].Steps[1].WorkpieceComponentId); Assert.Equal(string.Empty, reopened.Sequences[0].Steps[1].TargetId); return true;
        }
        finally { editor.Dispose(); }
    });

    [Theory]
    [InlineData("readonly")]
    [InlineData("nonlinear")]
    [InlineData("removed-product")]
    [InlineData("duplicate-product")]
    [InlineData("wrong-kind")]
    [InlineData("inactive-layout")]
    [InlineData("ambiguous-layout")]
    [InlineData("empty-target")]
    [InlineData("blank-target")]
    [InlineData("device-id")]
    [InlineData("unit-id")]
    [InlineData("conveyor-id")]
    [InlineData("camera-id")]
    [InlineData("sequence-id")]
    [InlineData("channel-id")]
    [InlineData("removed-caller")]
    [InlineData("foreign-caller")]
    [InlineData("template-removed")]
    [InlineData("unsupported-action")]
    [InlineData("disposed")]
    public Task WorkpieceAddRejectsInvalidContextOrOtherIdentityKinds(string boundary) => ui.InvokeAsync(() =>
    {
        var project = CreateProject(); var editor = new SequenceEditorViewModel();
        try
        {
            editor.Load(project); editor.WorkpieceStepTargetId = "product-b"; editor.WorkpieceStepTemplateId = "feed-workpiece";
            switch (boundary)
            {
                case "readonly": editor.IsEditable = false; break;
                case "nonlinear": project.Sequences[0].Steps[0].NextStepId = "feed"; break;
                case "removed-product": project.Layouts[0].Components.RemoveAll(component => component.Id == "product-b"); break;
                case "duplicate-product": project.Layouts[0].Components.Add(new() { Id = "product-b", Kind = LayoutComponentKind.Workpiece }); break;
                case "wrong-kind": project.Layouts[0].Components.Single(component => component.Id == "product-b").Kind = LayoutComponentKind.DigitalIo; break;
                case "inactive-layout": project.Simulation.ActiveLayoutId = "missing"; break;
                case "ambiguous-layout": project.Simulation.ActiveLayoutId = null; project.Layouts.Add(new() { Id = "other-layout" }); break;
                case "empty-target": editor.WorkpieceStepTargetId = string.Empty; break;
                case "blank-target": editor.WorkpieceStepTargetId = " "; break;
                case "device-id": editor.WorkpieceStepTargetId = "product-device-b"; break;
                case "unit-id": editor.WorkpieceStepTargetId = "dest-b"; break;
                case "conveyor-id": editor.WorkpieceStepTargetId = "conveyor-b"; break;
                case "camera-id": editor.WorkpieceStepTargetId = "camera"; break;
                case "sequence-id": editor.WorkpieceStepTargetId = "sequence"; break;
                case "channel-id": editor.WorkpieceStepTargetId = "run-b"; break;
                case "removed-caller": project.Sequences.Clear(); break;
                case "foreign-caller": editor.SelectedSequence = new() { Id = "sequence", Steps = [new() { Id = "done", Action = SequenceStepAction.Complete }] }; editor.WorkpieceStepTargetId = "product-b"; editor.WorkpieceStepTemplateId = "feed-workpiece"; break;
                case "template-removed": editor.Templates.Clear(); break;
                case "unsupported-action": editor.WorkpieceStepTemplateId = string.Empty; editor.WorkpieceStepTemplateId = "feed-unit"; break;
                case "disposed": editor.Dispose(); break;
            }
            string before = JsonSerializer.Serialize(project); Assert.False(editor.AddWorkpieceStepCommand.CanExecute(null)); editor.AddWorkpieceStepCommand.Execute(null);
            Assert.Equal(before, JsonSerializer.Serialize(project)); return true;
        }
        finally { editor.Dispose(); }
    });

    [Theory]
    [InlineData("removed-product")]
    [InlineData("duplicate-product")]
    [InlineData("wrong-kind")]
    [InlineData("inactive-layout")]
    [InlineData("choice-changed")]
    [InlineData("sequence-switch")]
    [InlineData("same-id-load")]
    [InlineData("readonly")]
    [InlineData("nonlinear")]
    [InlineData("template-removed")]
    [InlineData("removed-caller")]
    public Task WorkpieceAddRevalidatesAfterDraftAdmissionAndRecovers(string boundary) => ui.InvokeAsync(() =>
    {
        var project = CreateProject(); var original = project.Sequences[0]; var replacement = CreateProject(); var editor = new SequenceEditorViewModel();
        try
        {
            editor.Load(project); editor.WorkpieceStepTargetId = "product-b"; editor.WorkpieceStepTemplateId = "feed-workpiece"; editor.SelectedStepDraft!.Parameter = "0.2";
            int added = 0; editor.WorkpieceStepAdded += (_, _) => added++;
            editor.PendingStepDraftPrompt = () =>
            {
                switch (boundary)
                {
                    case "removed-product": project.Layouts[0].Components.RemoveAll(component => component.Id == "product-b"); break;
                    case "duplicate-product": project.Layouts[0].Components.Add(new() { Id = "product-b", Kind = LayoutComponentKind.Workpiece }); break;
                    case "wrong-kind": project.Layouts[0].Components.Single(component => component.Id == "product-b").Kind = LayoutComponentKind.DigitalIo; break;
                    case "inactive-layout": project.Simulation.ActiveLayoutId = "missing"; break;
                    case "choice-changed": editor.WorkpieceStepTargetId = "product-a"; editor.WorkpieceStepTemplateId = "eject-workpiece"; break;
                    case "sequence-switch": editor.DiscardStepDraft(); var other = new SequenceDefinition { Id = "other", Steps = [new() { Id = "done", Action = SequenceStepAction.Complete }] }; project.Sequences.Add(other); editor.SelectedSequence = other; editor.WorkpieceStepTargetId = "product-b"; editor.WorkpieceStepTemplateId = "feed-workpiece"; break;
                    case "same-id-load": editor.Load(replacement); editor.WorkpieceStepTargetId = "product-b"; editor.WorkpieceStepTemplateId = "feed-workpiece"; break;
                    case "readonly": editor.IsEditable = false; break;
                    case "nonlinear": original.Steps[0].NextStepId = "feed"; break;
                    case "template-removed": editor.Templates.Clear(); break;
                    case "removed-caller": project.Sequences.Clear(); break;
                }
                return PlacementDraftDecision.Discard;
            };
            Assert.True(editor.AddWorkpieceStepCommand.CanExecute(null)); editor.AddWorkpieceStepCommand.Execute(null);
            Assert.Equal(0, added); Assert.Equal(2, original.Steps.Count); Assert.Equal(2, replacement.Sequences[0].Steps.Count); Assert.Equal("0.1", original.Steps[0].Parameter);
            editor.IsEditable = true; var recovery = CreateProject(); editor.Load(recovery); editor.WorkpieceStepTargetId = "product-b"; editor.WorkpieceStepTemplateId = "feed-workpiece"; editor.AddWorkpieceStepCommand.Execute(null);
            Assert.Equal(1, added); Assert.Equal("product-b", recovery.Sequences[0].Steps[1].WorkpieceComponentId); Assert.Empty(editor.ValidationIssues); return true;
        }
        finally { editor.DiscardStepDraft(); editor.Dispose(); }
    });

    [Fact]
    public Task WorkpieceRefreshSelectionSequenceLoadAndDisposeKeepComponentScope() => ui.InvokeAsync(() =>
    {
        var project = CreateProject(); var editor = new SequenceEditorViewModel();
        try
        {
            editor.Load(project); editor.WorkpieceStepTargetId = "product-b"; editor.WorkpieceStepTemplateId = "feed-workpiece";
            project.Layouts[0].Components.Single(component => component.Id == "product-b").Name = "다음 검사 제품"; editor.RefreshAuthoringTargets();
            Assert.Equal("다음 검사 제품 (product-b)", editor.WorkpieceStepTarget!.Name); Assert.Equal("feed-workpiece", editor.WorkpieceStepTemplateId);
            editor.SelectedUnitId = string.Empty; Assert.NotNull(editor.WorkpieceStepPreview); Assert.Equal("product-b", editor.WorkpieceStepTargetId);
            editor.WorkpieceStepTargetId = "product-a"; Assert.Equal(string.Empty, editor.WorkpieceStepTemplateId); Assert.Null(editor.WorkpieceStepPreview);
            editor.WorkpieceStepTemplateId = "eject-workpiece";
            var other = new SequenceDefinition { Id = "other", Steps = [new() { Id = "done", Action = SequenceStepAction.Complete }] }; project.Sequences.Add(other); editor.SelectedSequence = other;
            Assert.Equal(string.Empty, editor.WorkpieceStepTargetId); Assert.Equal(string.Empty, editor.WorkpieceStepTemplateId);
            editor.WorkpieceStepTargetId = "product-b"; editor.WorkpieceStepTemplateId = "feed-workpiece"; project = CreateProject(); editor.Load(project);
            Assert.Null(editor.WorkpieceStepPreview); Assert.Equal(string.Empty, editor.WorkpieceStepTargetId);
            editor.WorkpieceStepTargetId = "product-b"; editor.WorkpieceStepTemplateId = "feed-workpiece"; project.Layouts[0].Components.RemoveAll(component => component.Id == "product-b"); editor.RefreshAuthoringTargets();
            Assert.Equal(string.Empty, editor.WorkpieceStepTargetId); Assert.Equal(string.Empty, editor.WorkpieceStepTemplateId);
            editor.Dispose(); Assert.Empty(editor.WorkpieceStepTargets); Assert.Empty(editor.WorkpieceStepTemplates); Assert.Null(editor.WorkpieceStepPreview); return true;
        }
        finally { editor.Dispose(); }
    });

    [Theory]
    [InlineData(OpenVisionLanguage.Korean, true)]
    [InlineData(OpenVisionLanguage.English, true)]
    [InlineData(OpenVisionLanguage.Korean, false)]
    [InlineData(OpenVisionLanguage.English, false)]
    public Task RenderedWorkpieceSelectionPreviewAddAndInspectorApplyStaySeparate(OpenVisionLanguage language, bool fullArea) => ui.InvokeAsync(() =>
    {
        var previous = OpenVisionLanguageService.CurrentLanguage; OpenVisionLanguageService.SetLanguage(language, save: false);
        var project = CreateProject(); using var main = new MainViewModel(project); main.Navigation.HideStartupChoice(); main.Navigation.IsProcessWorkspace = true;
        var editor = main.SequenceEditor; var window = Show(main, fullArea);
        try
        {
            var clock = main.SceneSnapshots.Latest!.SimulationTime; string snapshot = MaterialSnapshot(main);
            foreach (string id in new[] { "feed-workpiece", "eject-workpiece" })
            {
                string before = JsonSerializer.Serialize(project); var selected = editor.SelectedStep; var draft = editor.SelectedStepDraft; bool dirty = main.HasUnsavedChanges;
                WithDialog(window, dialog =>
                {
                    Assert.Same(editor, dialog.DataContext); var area = (Expander)dialog.FindName("WorkpieceStepAuthoring"); Assert.False(area.IsExpanded); area.IsExpanded = true; WaitForWorkpieceContent(dialog, area);
                    var target = (ComboBox)dialog.FindName("WorkpieceStepTarget"); var template = (ComboBox)dialog.FindName("WorkpieceStepTemplate"); var add = (Button)dialog.FindName("AddWorkpieceStepButton");
                    Assert.Equal(2, target.Items.Count); Assert.Null(target.SelectedItem); Assert.Empty(template.Items); Assert.False(add.IsEnabled);
                    target.SetCurrentValue(Selector.SelectedValueProperty, "product-b"); Pump(dialog); Assert.Equal("product-b", editor.WorkpieceStepTargetId); Assert.Equal(2, template.Items.Count); Assert.False(add.IsEnabled);
                    template.SetCurrentValue(Selector.SelectedValueProperty, id); Pump(dialog); Assert.Equal(id, editor.WorkpieceStepTemplateId);
                    editor.WorkpieceStepTargetId = "product-a"; Pump(dialog); Assert.Equal("product-a", target.SelectedValue); Assert.Null(template.SelectedItem); Assert.False(add.IsEnabled);
                    editor.WorkpieceStepTemplateId = id; Pump(dialog); Assert.Equal(id, template.SelectedValue);
                    target.SetCurrentValue(Selector.SelectedValueProperty, "product-b"); template.SetCurrentValue(Selector.SelectedValueProperty, id); Pump(dialog);
                    Assert.Equal("검사 제품 (product-b)", ((TextBlock)dialog.FindName("WorkpieceStepPreviewTarget")).Text.Replace("\u2060", string.Empty)); Assert.Equal("0", ((TextBlock)dialog.FindName("WorkpieceStepPreviewTimeout")).Text);
                    Assert.Equal(before, JsonSerializer.Serialize(project)); Assert.Same(selected, editor.SelectedStep); Assert.Same(draft, editor.SelectedStepDraft); Assert.False(editor.HasPendingStepDraft || main.IsRunning);
                    Assert.Equal(dirty, main.HasUnsavedChanges); Assert.Equal(clock, main.SceneSnapshots.Latest!.SimulationTime); Assert.Equal(snapshot, MaterialSnapshot(main));
                    if (id == "feed-workpiece")
                    {
                        editor.IsEditable = false; Pump(dialog); Assert.False(target.IsEnabled || template.IsEnabled || add.IsEnabled); Capture(dialog, $"workpiece-readonly-{language}-{fullArea}", false);
                        editor.IsEditable = true; Pump(dialog); var camera = (Expander)dialog.FindName("CameraStepAuthoring"); double collapsed = camera.ActualHeight;
                        camera.IsExpanded = true; WaitForWorkpieceContent(dialog, area); camera.IsExpanded = false; WaitForWorkpieceContent(dialog, area, collapsed);
                    }
                    Capture(dialog, $"workpiece-preview-{id}-{language}-{fullArea}", false); Assert.Same(editor.AddWorkpieceStepCommand, add.Command); Invoke(add); Pump(window); Assert.False(dialog.IsVisible);
                });
            }
            var created = project.Sequences[0].Steps.Skip(1).Take(2).ToArray(); Assert.Equal(new[] { SequenceStepAction.FeedWorkpiece, SequenceStepAction.EjectWorkpiece }, created.Select(step => step.Action));
            Assert.All(created, step => { Assert.Equal("product-b", step.WorkpieceComponentId); Assert.Equal(string.Empty, step.TargetId); Assert.Equal(string.Empty, step.Parameter); Assert.Equal(0, step.TimeoutMs); });
            Assert.Empty(editor.ValidationIssues); Assert.True(main.HasUnsavedChanges); Assert.False(main.IsRunning); Assert.Equal(clock, main.SceneSnapshots.Latest!.SimulationTime); Assert.Equal(snapshot, MaterialSnapshot(main));
            Capture(window, $"workpiece-added-{language}-{(fullArea ? "work-area" : "compact")}", fullArea);
            editor.SelectedStepDraft!.WorkpieceComponentId = "product-a"; Assert.Equal("product-b", created[1].WorkpieceComponentId); editor.DiscardStepDraft();
            editor.SelectedStepDraft!.WorkpieceComponentId = "product-a"; var inspector = Descendants(window).OfType<SequenceStepInspectorView>().Single(view => view.IsVisible);
            Invoke((Button)inspector.FindName("ApplyStepDraftButton")); Pump(window); Assert.Equal("product-a", created[1].WorkpieceComponentId); Assert.False(editor.HasPendingStepDraft); return true;
        }
        finally { CloseFixture(main, window, previous); }
    });

    [Theory]
    [InlineData(OpenVisionLanguage.Korean)]
    [InlineData(OpenVisionLanguage.English)]
    public Task WorkpieceDialogCancelRetainsDraftAndCandidateUntilExplicitSuccess(OpenVisionLanguage language) => ui.InvokeAsync(() =>
    {
        var previous = OpenVisionLanguageService.CurrentLanguage; OpenVisionLanguageService.SetLanguage(language, save: false);
        var project = CreateProject(); using var main = new MainViewModel(project); main.Navigation.HideStartupChoice(); main.Navigation.IsProcessWorkspace = true; var editor = main.SequenceEditor; var window = Show(main, false);
        try
        {
            editor.SelectedStepDraft!.Parameter = "0.2"; editor.PendingStepDraftPrompt = () => PlacementDraftDecision.Cancel; string before = JsonSerializer.Serialize(project);
            WithDialog(window, dialog =>
            {
                var area = (Expander)dialog.FindName("WorkpieceStepAuthoring"); area.IsExpanded = true; WaitForWorkpieceContent(dialog, area);
                ((ComboBox)dialog.FindName("WorkpieceStepTarget")).SetCurrentValue(Selector.SelectedValueProperty, "product-b"); ((ComboBox)dialog.FindName("WorkpieceStepTemplate")).SetCurrentValue(Selector.SelectedValueProperty, "eject-workpiece"); Pump(dialog);
                Invoke((Button)dialog.FindName("AddWorkpieceStepButton")); Pump(window); Assert.True(dialog.IsVisible && editor.HasPendingStepDraft); Assert.Equal("product-b", editor.WorkpieceStepTargetId); Assert.Equal("eject-workpiece", editor.WorkpieceStepTemplateId); Assert.Equal(before, JsonSerializer.Serialize(project));
                editor.PendingStepDraftPrompt = () => PlacementDraftDecision.Discard; Invoke((Button)dialog.FindName("AddWorkpieceStepButton")); Pump(window); Assert.False(dialog.IsVisible);
            });
            Assert.Equal(3, project.Sequences[0].Steps.Count); Assert.Equal("0.1", project.Sequences[0].Steps[0].Parameter); return true;
        }
        finally { CloseFixture(main, window, previous); }
    });

    [Theory]
    [InlineData(OpenVisionLanguage.Korean)]
    [InlineData(OpenVisionLanguage.English)]
    public Task WorkpieceLongNameAndContextReplacementKeepFullIdentityAndResultLifetime(OpenVisionLanguage language) => ui.InvokeAsync(() =>
    {
        var previous = OpenVisionLanguageService.CurrentLanguage; OpenVisionLanguageService.SetLanguage(language, save: false);
        var project = CreateProject(); project.Layouts[0].Components.Single(component => component.Id == "product-b").Name = string.Join(" ", Enumerable.Repeat(language == OpenVisionLanguage.Korean ? "두 번째 검사 자리에서 촬영하고 배출할 제품" : "Product for acquisition and ejection at the second inspection position", 5));
        using var main = new MainViewModel(project); main.Navigation.HideStartupChoice(); main.Navigation.IsProcessWorkspace = true; var window = Show(main, false);
        try
        {
            WithDialog(window, dialog =>
            {
                var area = (Expander)dialog.FindName("WorkpieceStepAuthoring"); area.IsExpanded = true; WaitForWorkpieceContent(dialog, area);
                ((ComboBox)dialog.FindName("WorkpieceStepTarget")).SetCurrentValue(Selector.SelectedValueProperty, "product-b"); ((ComboBox)dialog.FindName("WorkpieceStepTemplate")).SetCurrentValue(Selector.SelectedValueProperty, "feed-workpiece"); WaitForWorkpieceContent(dialog, area);
                var preview = (TextBlock)dialog.FindName("WorkpieceStepPreviewTarget"); Assert.Equal(main.SequenceEditor.WorkpieceStepTarget!.Name, preview.Text.Replace("\u2060", string.Empty)); preview.Measure(new Size(preview.ActualWidth, double.PositiveInfinity));
                Assert.True(preview.DesiredSize.Height <= preview.ActualHeight + preview.Margin.Top + preview.Margin.Bottom + 1); Capture(dialog, $"workpiece-long-{language}", false);
                var replacement = new SequenceEditorViewModel(); replacement.Load(CreateProject()); replacement.WorkpieceStepTargetId = "product-b"; replacement.WorkpieceStepTemplateId = "feed-workpiece";
                try
                {
                    dialog.DataContext = replacement; Pump(dialog); main.SequenceEditor.AddWorkpieceStepCommand.Execute(null); Pump(window); Assert.True(dialog.IsVisible);
                    var add = (Button)dialog.FindName("AddWorkpieceStepButton"); add.BringIntoView(); Pump(dialog); Invoke(add); Pump(window); Assert.False(dialog.IsVisible);
                    replacement.WorkpieceStepTargetId = "product-a"; replacement.WorkpieceStepTemplateId = "eject-workpiece"; replacement.AddWorkpieceStepCommand.Execute(null); Assert.Equal(4, replacement.SelectedSequence!.Steps.Count);
                }
                finally { replacement.Dispose(); }
            });
            return true;
        }
        finally { CloseFixture(main, window, previous); }
    });

    [Theory]
    [InlineData(OpenVisionLanguage.Korean)]
    [InlineData(OpenVisionLanguage.English)]
    public Task WorkpieceEmptyLayoutShowsHintThenExplicitSelectionRecovers(OpenVisionLanguage language) => ui.InvokeAsync(() =>
    {
        var previous = OpenVisionLanguageService.CurrentLanguage; OpenVisionLanguageService.SetLanguage(language, save: false);
        var project = CreateProject(); var products = project.Layouts[0].Components.Where(component => component.Kind == LayoutComponentKind.Workpiece).ToArray(); project.Layouts[0].Components.RemoveAll(component => component.Kind == LayoutComponentKind.Workpiece);
        using var main = new MainViewModel(project); main.Navigation.HideStartupChoice(); main.Navigation.IsProcessWorkspace = true; var window = Show(main, false);
        try
        {
            WithDialog(window, dialog =>
            {
                var area = (Expander)dialog.FindName("WorkpieceStepAuthoring"); area.IsExpanded = true; WaitForWorkpieceContent(dialog, area);
                var target = (ComboBox)dialog.FindName("WorkpieceStepTarget"); var template = (ComboBox)dialog.FindName("WorkpieceStepTemplate"); var add = (Button)dialog.FindName("AddWorkpieceStepButton"); Assert.Empty(target.Items); Assert.Empty(template.Items); Assert.False(add.IsEnabled);
                Assert.Equal(OpenVisionLanguageService.T("R40.WorkpieceStepNoTargets"), ((TextBlock)dialog.FindName("WorkpieceStepHint")).Text.Replace("\u2060", string.Empty)); Capture(dialog, $"workpiece-empty-{language}", false);
                project.Layouts[0].Components.AddRange(products); main.SequenceEditor.RefreshAuthoringTargets(); Pump(dialog); Assert.Equal(2, target.Items.Count); Assert.Null(target.SelectedItem); Assert.Empty(template.Items); Assert.False(add.IsEnabled);
                target.SetCurrentValue(Selector.SelectedValueProperty, "product-b"); template.SetCurrentValue(Selector.SelectedValueProperty, "feed-workpiece"); Pump(dialog); Assert.True(add.IsEnabled); Invoke(add); Pump(window); Assert.False(dialog.IsVisible);
            });
            Assert.Equal("product-b", project.Sequences[0].Steps.Single(step => step.Id == "step-1").WorkpieceComponentId); Assert.False(main.IsRunning); return true;
        }
        finally { CloseFixture(main, window, previous); }
    });

    [Fact]
    public Task WorkpieceCandidateAndAddStayLockedUntilIsolatedStepTestCompletes() => ui.InvokeTaskAsync(async () =>
    {
        var project = CreateProject(); var editor = new SequenceEditorViewModel(); int busyEntries = 0;
        System.ComponentModel.PropertyChangedEventHandler handler = (_, args) =>
        {
            if (args.PropertyName != nameof(editor.IsStepTestBusy) || !editor.IsStepTestBusy) return;
            busyEntries++; string before = JsonSerializer.Serialize(project); Assert.False(editor.AddWorkpieceStepCommand.CanExecute(null)); editor.AddWorkpieceStepCommand.Execute(null);
            editor.WorkpieceStepTargetId = "product-a"; editor.WorkpieceStepTemplateId = "eject-workpiece"; Assert.Equal("product-b", editor.WorkpieceStepTargetId); Assert.Equal("feed-workpiece", editor.WorkpieceStepTemplateId); Assert.Equal(before, JsonSerializer.Serialize(project));
        };
        try
        {
            project.Sequences[0].Steps[0] = new() { Id = "move", Action = SequenceStepAction.MoveAxis, TargetId = "axis", Parameter = "2" }; editor.Load(project);
            editor.WorkpieceStepTargetId = "product-b"; editor.WorkpieceStepTemplateId = "feed-workpiece"; editor.PropertyChanged += handler; await editor.TestSelectedStepAsync();
            Assert.Equal(1, busyEntries); Assert.False(editor.IsStepTestBusy); Assert.True(editor.StepTestResult!.IsCompleted); Assert.True(editor.AddWorkpieceStepCommand.CanExecute(null)); editor.AddWorkpieceStepCommand.Execute(null); Assert.Equal(3, project.Sequences[0].Steps.Count); return true;
        }
        finally { editor.PropertyChanged -= handler; editor.Dispose(); }
    });

    private static string MaterialSnapshot(MainViewModel main) => JsonSerializer.Serialize(new { main.SceneSnapshots.Latest!.Axes, main.SceneSnapshots.Latest.Signals, main.SceneSnapshots.Latest.Cameras, main.SceneSnapshots.Latest.LayoutComponents, main.SceneSnapshots.Latest.Workpieces, main.SceneSnapshots.Latest.UnitMaterialSlots, main.SceneSnapshots.Latest.UnitMaterialExits, main.SceneSnapshots.Latest.MaterialSupply });

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
        project.Name = "제품 투입·배출 공정";
        project.Devices.AddRange([
            new() { Id = "product-device-a", Name = "검사 제품", Kind = DeviceKind.Workpiece, Workpiece = new() { Type = "Wafer", ConveyorComponentId = "conveyor-a", InitiallyPresent = false } },
            new() { Id = "product-device-b", Name = "검사 제품", Kind = DeviceKind.Workpiece, Workpiece = new() { Type = "Wafer", ConveyorComponentId = "conveyor-b", InitiallyPresent = false } }
        ]);
        project.Layouts[0].Components.AddRange([
            new() { Id = "product-a", Name = "검사 제품", Kind = LayoutComponentKind.Workpiece, BehaviorBindingId = "product-device-a", UnitId = "dest-a", Size = new() { Width = 12, Height = 12 } },
            new() { Id = "product-b", Name = "검사 제품", Kind = LayoutComponentKind.Workpiece, BehaviorBindingId = "product-device-b", UnitId = "dest-b", Size = new() { Width = 12, Height = 12 }, Transform = new() { Y = 80 } }
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
        var folder = Path.Combine(TestStorage.RootPath, "workpiece-authoring"); Directory.CreateDirectory(folder);
        File.AppendAllText(Path.Combine(folder, "fixture-windows.jsonl"), JsonSerializer.Serialize(new { ownWindowRetained = remaining.Contains(window),
            window.IsVisible, dirty = main.HasUnsavedChanges, remainingWindows = remaining.Select(item => new { type = item.GetType().Name, item.Title, item.IsVisible }).ToArray() }) + Environment.NewLine);
        Assert.DoesNotContain(window, remaining); Assert.False(window.IsVisible);
        OpenVisionLanguageService.SetLanguage(previous, save: false);
    }

    private static void WaitForWorkpieceContent(Window window, Expander expander, double? cameraCollapsedHeight = null)
    {
        var names = new[] { "WorkpieceStepTarget", "WorkpieceStepTemplate", "WorkpieceStepPreviewTarget", "WorkpieceStepPreviewTimeout", "WorkpieceStepHint", "AddWorkpieceStepButton" };
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
        Assert.True(stable >= 2 && Ready(out _), "Workpiece section must finish revealing before capture; no product animation or native-input acceptance change.");
        probes.Single(probe => probe.Name == "AddWorkpieceStepButton").BringIntoView(); Pump(window);
        var scroll = Descendants(window).OfType<ScrollViewer>().Single(viewer => viewer.Content is Panel panel && panel.Children.Contains(expander));
        var presenter = Descendants(scroll).OfType<ScrollContentPresenter>().Single(item => ReferenceEquals(item.TemplatedParent, scroll));
        var viewport = new Rect(presenter.RenderSize); viewport.Inflate(1e-6, 1e-6);
        foreach (var probe in probes)
        {
            if (probe is TextBlock { Text.Length: 0 }) continue;
            Assert.True(viewport.Contains(probe.TransformToAncestor(presenter).TransformBounds(new Rect(probe.RenderSize))), probe.Name + " must be fully visible after scrolling to the workpiece action.");
        }
    }

    private static void Capture(Window window, string name, bool fullArea)
    {
        Pump(window);
        if (window is SequenceStepTemplateDialog dialog && dialog.FindName("WorkpieceStepAuthoring") is Expander { IsExpanded: true } expander) WaitForWorkpieceContent(window, expander);
        var monitor = SmokeDpiTestHook.CaptureMonitorEvidence(window);
        var dpi = VisualTreeHelper.GetDpi(window);
        var folder = Path.Combine(TestStorage.RootPath, "workpiece-authoring"); Directory.CreateDirectory(folder);
        new SmokeWindowCapture().Capture(window, Path.Combine(folder, name + ".png"));
        File.WriteAllText(Path.Combine(folder, name + ".json"), JsonSerializer.Serialize(new { monitor, width = (int)Math.Round(window.ActualWidth * dpi.DpiScaleX),
            height = (int)Math.Round(window.ActualHeight * dpi.DpiScaleY), wpfDpi = dpi.PixelsPerInchX, fullArea, crop = "none",
            method = "Actual in-process WPF RenderTargetBitmap; dialog is a separate supplemental window image, never composited into the FHD parent." }));
        Assert.True(monitor.WindowContainedByMonitor && monitor.WindowIntersectsMonitor, JsonSerializer.Serialize(monitor));
        var area = monitor.WorkingArea.Split(',').Select(int.Parse).ToArray(); var rect = monitor.WindowRect.Split(',').Select(int.Parse).ToArray();
        Assert.True(new Rect(area[0], area[1], area[2], area[3]).Contains(new Rect(rect[0], rect[1], rect[2], rect[3])), "Owned capture must stay inside the actual work area: " + JsonSerializer.Serialize(monitor));
    }
}
