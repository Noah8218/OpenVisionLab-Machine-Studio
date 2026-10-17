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
public sealed class R40UnitAuthoringTests(StudioUiTestHost ui)
{
    [Theory]
    [InlineData(OpenVisionLanguage.Korean, true)]
    [InlineData(OpenVisionLanguage.English, true)]
    [InlineData(OpenVisionLanguage.Korean, false)]
    [InlineData(OpenVisionLanguage.English, false)]
    public Task LegacyUnitTemplatesKeepCurrentOwnerAndExplicitDirectAdd(OpenVisionLanguage language, bool fullArea) => ui.InvokeAsync(() =>
    {
        var previous = OpenVisionLanguageService.CurrentLanguage; OpenVisionLanguageService.SetLanguage(language, save: false);
        var project = CreateProject(); using var main = new MainViewModel(project);
        main.Navigation.HideStartupChoice(); main.Navigation.IsProcessWorkspace = true;
        var window = Show(main, fullArea);
        try
        {
            foreach (string id in new[] { "feed-unit", "receive-unit", "eject-unit" })
            {
                string before = JsonSerializer.Serialize(project);
                WithDialog(window, dialog =>
                {
                    Assert.Same(main.SequenceEditor, dialog.DataContext); Assert.Equal(before, JsonSerializer.Serialize(project));
                    if (id == "feed-unit") Capture(dialog, $"legacy-dialog-{language}-{fullArea}", false);
                    var add = Descendants(dialog).OfType<Button>().Single(button => button.CommandParameter is SequenceStepTemplateDefinition template && template.Id == id);
                    add.BringIntoView(); Pump(dialog); Invoke(add); Pump(window);
                });
            }
            var created = project.Sequences[0].Steps.Skip(1).Take(3).ToArray();
            Assert.Equal(new[] { SequenceStepAction.FeedUnitWorkpiece, SequenceStepAction.ReceiveUnitWorkpiece, SequenceStepAction.EjectUnitWorkpiece }, created.Select(step => step.Action));
            Assert.All(created, step => Assert.Equal("second", step.TargetId));
            Assert.Equal(new[] { "", "", "auto" }, created.Select(step => step.Parameter)); Assert.Equal(new[] { 30000, 30000, 0 }, created.Select(step => step.TimeoutMs));
            Assert.Equal("done", project.Sequences[0].Steps.Last().Id); Assert.True(main.HasUnsavedChanges); Assert.False(main.IsRunning); Assert.Empty(main.SequenceEditor.ValidationIssues);
            Capture(window, $"legacy-added-{language}-{(fullArea ? "work-area" : "compact")}", fullArea);
            return true;
        }
        finally { CloseFixture(main, window, previous); }
    });

    [Theory]
    [InlineData("feed-unit", SequenceStepAction.FeedUnitWorkpiece, "", 30000)]
    [InlineData("receive-unit", SequenceStepAction.ReceiveUnitWorkpiece, "", 30000)]
    [InlineData("eject-unit", SequenceStepAction.EjectUnitWorkpiece, "auto", 0)]
    public Task UnitPreviewAndAddPreserveTheCurrentOwnerAndSingleFactory(string id, SequenceStepAction action, string parameter, int timeout) => ui.InvokeAsync(() =>
    {
        var project = CreateProject(); var editor = new SequenceEditorViewModel();
        try
        {
            editor.Load(project); string before = JsonSerializer.Serialize(project); var selected = editor.SelectedStep; var draft = editor.SelectedStepDraft;
            int changed = 0, added = 0, otherAdded = 0;
            editor.DefinitionChanged += (_, _) => changed++; editor.UnitStepAdded += (_, _) => added++;
            editor.AxisStepAdded += (_, _) => otherAdded++; editor.CameraStepAdded += (_, _) => otherAdded++;
            editor.SignalStepAdded += (_, _) => otherAdded++; editor.SubsequenceStepAdded += (_, _) => otherAdded++;
            Assert.Equal("second", editor.UnitStepTarget!.Id);
            Assert.Equal(new[] { "feed-unit", "receive-unit", "eject-unit" }, editor.UnitStepTemplates.Select(template => template.Id));
            Assert.Null(editor.UnitStepPreview); Assert.False(editor.AddUnitStepCommand.CanExecute(null));
            editor.UnitStepTemplateId = id;
            var preview = editor.UnitStepPreview!;
            Assert.Equal(action, preview.Action); Assert.Equal("second", preview.TargetId); Assert.Equal(parameter, preview.Parameter); Assert.Equal(timeout, preview.TimeoutMs);
            Assert.Equal(0, preview.EjectionDelayMilliseconds); Assert.True(string.IsNullOrEmpty(preview.DestinationUnitId)); Assert.True(string.IsNullOrEmpty(preview.WorkpieceComponentId));
            Assert.Equal(before, JsonSerializer.Serialize(project)); Assert.Same(selected, editor.SelectedStep); Assert.Same(draft, editor.SelectedStepDraft);
            Assert.False(editor.HasPendingStepDraft); Assert.Equal(0, changed + added + otherAdded);
            Assert.True(editor.AddUnitStepCommand.CanExecute(null)); editor.AddUnitStepCommand.Execute(null);
            var created = project.Sequences[0].Steps.Single(step => step.Id == "step-1");
            Assert.Equal(action, created.Action); Assert.Equal("second", created.TargetId); Assert.Equal(parameter, created.Parameter); Assert.Equal(timeout, created.TimeoutMs);
            Assert.Equal(0, created.EjectionDelayMilliseconds); Assert.True(string.IsNullOrEmpty(created.DestinationUnitId)); Assert.True(string.IsNullOrEmpty(created.WorkpieceComponentId));
            Assert.Equal("step-1", project.Sequences[0].Steps[0].NextStepId); Assert.Equal("done", created.NextStepId); Assert.Equal("done", project.Sequences[0].Steps.Last().Id);
            Assert.Empty(editor.ValidationIssues); Assert.Equal(1, changed); Assert.Equal(1, added); Assert.Equal(0, otherAdded);
            Assert.Equal(string.Empty, editor.UnitStepTemplateId); Assert.Null(editor.UnitStepPreview); Assert.False(editor.AddUnitStepCommand.CanExecute(null));
            editor.AddUnitStepCommand.Execute(null); Assert.Equal(3, project.Sequences[0].Steps.Count);
            return true;
        }
        finally { editor.Dispose(); }
    });

    [Theory]
    [InlineData("readonly")]
    [InlineData("nonlinear")]
    [InlineData("common")]
    [InlineData("removed-unit")]
    [InlineData("duplicate-unit")]
    [InlineData("cross-station-duplicate")]
    [InlineData("blank-id")]
    [InlineData("removed-caller")]
    [InlineData("foreign-caller")]
    [InlineData("template-removed")]
    [InlineData("branch-not-candidate")]
    [InlineData("transfer-not-candidate")]
    [InlineData("workpiece-not-candidate")]
    [InlineData("disposed")]
    public Task UnitAddRejectsMissingAmbiguousOrUnsupportedContext(string boundary) => ui.InvokeAsync(() =>
    {
        var project = CreateProject(); var editor = new SequenceEditorViewModel();
        try
        {
            editor.Load(project); editor.UnitStepTemplateId = "feed-unit";
            switch (boundary)
            {
                case "readonly": editor.IsEditable = false; break;
                case "nonlinear": project.Sequences[0].Steps[0].NextStepId = "feed"; break;
                case "common": project.Sequences[0].UnitId = null; break;
                case "removed-unit": project.Stations[0].Units.RemoveAt(1); break;
                case "duplicate-unit": project.Stations[0].Units.Add(new() { Id = "second" }); break;
                case "cross-station-duplicate": project.Stations.Add(new() { Id = "other-station", Units = [new() { Id = "second" }] }); break;
                case "blank-id": project.Stations[0].Units[1].Id = " "; project.Sequences[0].UnitId = " "; break;
                case "removed-caller": project.Sequences.Clear(); break;
                case "foreign-caller": editor.SelectedSequence = new SequenceDefinition { Id = "sequence", UnitId = "second", Steps = [new() { Id = "done", Action = SequenceStepAction.Complete }] }; editor.UnitStepTemplateId = "feed-unit"; break;
                case "template-removed": editor.Templates.Clear(); break;
                case "branch-not-candidate": editor.UnitStepTemplateId = string.Empty; editor.UnitStepTemplateId = "branch-inspection"; break;
                case "transfer-not-candidate": editor.UnitStepTemplateId = string.Empty; editor.UnitStepTemplateId = "transfer-unit"; break;
                case "workpiece-not-candidate": editor.UnitStepTemplateId = string.Empty; editor.UnitStepTemplateId = "feed-workpiece"; break;
                case "disposed": editor.Dispose(); break;
            }
            string before = JsonSerializer.Serialize(project);
            Assert.False(editor.AddUnitStepCommand.CanExecute(null)); editor.AddUnitStepCommand.Execute(null); Assert.Equal(before, JsonSerializer.Serialize(project));
            return true;
        }
        finally { editor.Dispose(); }
    });

    [Fact]
    public Task UnitRefreshOwnerChangeSequenceAndSameIdLoadResetCandidate() => ui.InvokeAsync(() =>
    {
        var project = CreateProject(); var editor = new SequenceEditorViewModel();
        try
        {
            editor.Load(project); editor.UnitStepTemplateId = "eject-unit";
            project.Stations[0].Units[1].Name = "새 유닛 이름"; editor.RefreshAuthoringTargets();
            Assert.Equal("새 유닛 이름 (second)", editor.UnitStepTargetText); Assert.Equal("eject-unit", editor.UnitStepTemplateId);
            editor.SelectedUnitId = "first"; Assert.Equal(string.Empty, editor.UnitStepTemplateId); Assert.Equal("first", editor.UnitStepTarget!.Id);
            editor.UnitStepTemplateId = "feed-unit";
            var other = new SequenceDefinition { Id = "other", UnitId = "second", Steps = [new() { Id = "done", Action = SequenceStepAction.Complete }] };
            project.Sequences.Add(other); editor.SelectedSequence = other; Assert.Equal(string.Empty, editor.UnitStepTemplateId);
            editor.UnitStepTemplateId = "feed-unit";
            var replacement = JsonSerializer.Deserialize<MachineProjectDocument>(JsonSerializer.Serialize(project))!;
            string before = JsonSerializer.Serialize(replacement); editor.Load(replacement);
            Assert.Equal(string.Empty, editor.UnitStepTemplateId); Assert.Null(editor.UnitStepPreview); Assert.Equal(before, JsonSerializer.Serialize(replacement));
            editor.UnitStepTemplateId = "receive-unit"; replacement.Stations[0].Units.RemoveAt(0); editor.RefreshAuthoringTargets();
            Assert.Null(editor.UnitStepTarget); Assert.Equal(string.Empty, editor.UnitStepTemplateId); Assert.False(editor.AddUnitStepCommand.CanExecute(null));
            return true;
        }
        finally { editor.Dispose(); }
    });

    [Theory]
    [InlineData("removed-unit")]
    [InlineData("duplicate-unit")]
    [InlineData("owner-changed")]
    [InlineData("sequence-switch")]
    [InlineData("same-id-load")]
    [InlineData("readonly")]
    [InlineData("nonlinear")]
    [InlineData("template-removed")]
    public Task UnitAddRevalidatesAfterDraftAdmissionAndRecoversWithFreshContext(string boundary) => ui.InvokeAsync(() =>
    {
        var project = CreateProject(); var replacement = CreateProject(); var editor = new SequenceEditorViewModel();
        try
        {
            editor.Load(project); editor.UnitStepTemplateId = "feed-unit"; editor.SelectedStepDraft!.Parameter = "0.2";
            int added = 0; editor.UnitStepAdded += (_, _) => added++;
            editor.PendingStepDraftPrompt = () =>
            {
                switch (boundary)
                {
                    case "removed-unit": project.Stations[0].Units.RemoveAt(1); break;
                    case "duplicate-unit": project.Stations[0].Units.Add(new() { Id = "second" }); break;
                    case "owner-changed": project.Sequences[0].UnitId = "first"; break;
                    case "sequence-switch": editor.DiscardStepDraft(); var other = new SequenceDefinition { Id = "other", UnitId = "first", Steps = [new() { Id = "done", Action = SequenceStepAction.Complete }] }; project.Sequences.Add(other); editor.SelectedSequence = other; editor.UnitStepTemplateId = "feed-unit"; break;
                    case "same-id-load": editor.Load(replacement); editor.UnitStepTemplateId = "feed-unit"; break;
                    case "readonly": editor.IsEditable = false; break;
                    case "nonlinear": project.Sequences[0].Steps[0].NextStepId = "feed"; break;
                    case "template-removed": editor.Templates.Clear(); break;
                }
                return PlacementDraftDecision.Discard;
            };
            Assert.True(editor.AddUnitStepCommand.CanExecute(null)); editor.AddUnitStepCommand.Execute(null);
            Assert.Equal(0, added); Assert.Equal(2, project.Sequences[0].Steps.Count); Assert.Equal(2, replacement.Sequences[0].Steps.Count); Assert.Equal("0.1", project.Sequences[0].Steps[0].Parameter);
            editor.IsEditable = true; var recovery = CreateProject(); editor.Load(recovery); editor.UnitStepTemplateId = "feed-unit"; editor.AddUnitStepCommand.Execute(null);
            Assert.Equal(1, added); Assert.Equal("second", recovery.Sequences[0].Steps.Single(step => step.Id == "step-1").TargetId); Assert.Empty(editor.ValidationIssues);
            return true;
        }
        finally { editor.DiscardStepDraft(); editor.Dispose(); }
    });

    [Theory]
    [InlineData(OpenVisionLanguage.Korean, true)]
    [InlineData(OpenVisionLanguage.English, true)]
    [InlineData(OpenVisionLanguage.Korean, false)]
    [InlineData(OpenVisionLanguage.English, false)]
    public Task RenderedUnitCandidatesPreviewAddAndExistingInspectorApplyStaySeparate(OpenVisionLanguage language, bool fullArea) => ui.InvokeAsync(() =>
    {
        var previous = OpenVisionLanguageService.CurrentLanguage; OpenVisionLanguageService.SetLanguage(language, save: false);
        var project = CreateProject(); using var main = new MainViewModel(project); main.Navigation.HideStartupChoice(); main.Navigation.IsProcessWorkspace = true;
        var editor = main.SequenceEditor; var window = Show(main, fullArea);
        try
        {
            var clock = main.SceneSnapshots.Latest!.SimulationTime;
            foreach (var choice in new[] { (Id: "feed-unit", Value: "—", Timeout: "30000"), (Id: "receive-unit", Value: "—", Timeout: "30000"), (Id: "eject-unit", Value: OpenVisionLanguageService.T("MaterialFlow.AutoRoute"), Timeout: "0") })
            {
                string before = JsonSerializer.Serialize(project); var selected = editor.SelectedStep; var draft = editor.SelectedStepDraft; bool dirty = main.HasUnsavedChanges;
                string snapshot = JsonSerializer.Serialize(new { main.SceneSnapshots.Latest!.Axes, main.SceneSnapshots.Latest.Signals, main.SceneSnapshots.Latest.Cameras, main.SceneSnapshots.Latest.UnitMaterialSlots, main.SceneSnapshots.Latest.UnitMaterialExits, main.SceneSnapshots.Latest.MaterialSupply });
                WithDialog(window, dialog =>
                {
                    Assert.Same(editor, dialog.DataContext);
                    var area = (Expander)dialog.FindName("UnitStepAuthoring"); var camera = (Expander)dialog.FindName("CameraStepAuthoring"); double cameraHeight = camera.ActualHeight;
                    Assert.False(area.IsExpanded); area.IsExpanded = true; WaitForUnitContent(dialog, area);
                    var template = (ComboBox)dialog.FindName("UnitStepTemplate"); var add = (Button)dialog.FindName("AddUnitStepButton");
                    Assert.Equal(3, template.Items.Count); Assert.Null(template.SelectedItem); Assert.False(add.IsEnabled);
                    Assert.Equal("검사 유닛 (second)", ((TextBlock)dialog.FindName("UnitStepTarget")).Text.Replace("\u2060", string.Empty));
                    Assert.Single(Descendants((DependencyObject)area.Content).OfType<ComboBox>());
                    template.SetCurrentValue(Selector.SelectedValueProperty, choice.Id); Pump(dialog); Assert.Equal(choice.Id, editor.UnitStepTemplateId);
                    editor.UnitStepTemplateId = choice.Id == "eject-unit" ? "feed-unit" : "eject-unit"; Pump(dialog); Assert.Equal(editor.UnitStepTemplateId, template.SelectedValue);
                    template.SetCurrentValue(Selector.SelectedValueProperty, choice.Id); Pump(dialog);
                    Assert.Equal(choice.Value, ((TextBlock)dialog.FindName("UnitStepPreviewValue")).Text); Assert.Equal(choice.Timeout, ((TextBlock)dialog.FindName("UnitStepPreviewTimeout")).Text);
                    Assert.Equal(before, JsonSerializer.Serialize(project)); Assert.Same(selected, editor.SelectedStep); Assert.Same(draft, editor.SelectedStepDraft);
                    Assert.False(editor.HasPendingStepDraft || main.IsRunning); Assert.Equal(dirty, main.HasUnsavedChanges); Assert.Equal(clock, main.SceneSnapshots.Latest!.SimulationTime);
                    Assert.Equal(snapshot, JsonSerializer.Serialize(new { main.SceneSnapshots.Latest.Axes, main.SceneSnapshots.Latest.Signals, main.SceneSnapshots.Latest.Cameras, main.SceneSnapshots.Latest.UnitMaterialSlots, main.SceneSnapshots.Latest.UnitMaterialExits, main.SceneSnapshots.Latest.MaterialSupply }));
                    if (choice.Id == "feed-unit")
                    {
                        editor.IsEditable = false; Pump(dialog); Assert.False(template.IsEnabled || add.IsEnabled); Capture(dialog, $"unit-readonly-{language}-{fullArea}", false);
                        editor.IsEditable = true; Pump(dialog); camera.IsExpanded = true; WaitForUnitContent(dialog, area); Capture(dialog, $"unit-both-{language}-{fullArea}", false);
                        camera.IsExpanded = false; WaitForUnitContent(dialog, area, cameraHeight);
                    }
                    Capture(dialog, $"unit-preview-{choice.Id}-{language}-{fullArea}", false);
                    Assert.Same(editor.AddUnitStepCommand, add.Command); Invoke(add); Pump(window); Assert.False(dialog.IsVisible);
                });
                Assert.Equal(string.Empty, editor.UnitStepTemplateId); Assert.Equal(clock, main.SceneSnapshots.Latest!.SimulationTime); Assert.False(main.IsRunning);
            }
            var created = project.Sequences[0].Steps.Skip(1).Take(3).ToArray();
            Assert.Equal(new[] { SequenceStepAction.FeedUnitWorkpiece, SequenceStepAction.ReceiveUnitWorkpiece, SequenceStepAction.EjectUnitWorkpiece }, created.Select(step => step.Action));
            Assert.All(created, step => Assert.Equal("second", step.TargetId)); Assert.Equal(new[] { "", "", "auto" }, created.Select(step => step.Parameter)); Assert.Equal(new[] { 30000, 30000, 0 }, created.Select(step => step.TimeoutMs));
            Assert.Equal("done", created.Last().NextStepId); Assert.Equal(5, project.Sequences[0].Steps.Count); Assert.Empty(editor.ValidationIssues); Assert.True(main.HasUnsavedChanges);
            Capture(window, $"unit-added-{language}-{(fullArea ? "work-area" : "compact")}", fullArea);
            var eject = created[2]; editor.SelectedStepDraft!.Parameter = "NG"; Assert.Equal("auto", eject.Parameter); Assert.True(editor.HasPendingStepDraft);
            editor.DiscardStepDraft(); Assert.Equal("auto", editor.SelectedStepDraft!.Parameter); editor.SelectedStepDraft.Parameter = "NG";
            var inspector = Descendants(window).OfType<SequenceStepInspectorView>().Single(item => item.IsVisible); Invoke((Button)inspector.FindName("ApplyStepDraftButton")); Pump(window);
            Assert.Equal("NG", eject.Parameter); Assert.False(editor.HasPendingStepDraft || main.IsRunning); Assert.Equal(clock, main.SceneSnapshots.Latest!.SimulationTime);
            return true;
        }
        finally { CloseFixture(main, window, previous); }
    });

    [Theory]
    [InlineData(OpenVisionLanguage.Korean)]
    [InlineData(OpenVisionLanguage.English)]
    public Task UnitDialogCancelRetainsCandidateAndDraftUntilExplicitSuccess(OpenVisionLanguage language) => ui.InvokeAsync(() =>
    {
        var previous = OpenVisionLanguageService.CurrentLanguage; OpenVisionLanguageService.SetLanguage(language, save: false);
        var project = CreateProject(); using var main = new MainViewModel(project); main.Navigation.HideStartupChoice(); main.Navigation.IsProcessWorkspace = true;
        var editor = main.SequenceEditor; var window = Show(main, false);
        try
        {
            editor.SelectedStepDraft!.Parameter = "0.2"; editor.PendingStepDraftPrompt = () => PlacementDraftDecision.Cancel; string before = JsonSerializer.Serialize(project);
            WithDialog(window, dialog =>
            {
                var area = (Expander)dialog.FindName("UnitStepAuthoring"); area.IsExpanded = true; WaitForUnitContent(dialog, area);
                ((ComboBox)dialog.FindName("UnitStepTemplate")).SetCurrentValue(Selector.SelectedValueProperty, "eject-unit"); Pump(dialog);
                Invoke((Button)dialog.FindName("AddUnitStepButton")); Pump(window);
                Assert.True(dialog.IsVisible && editor.HasPendingStepDraft); Assert.Equal("eject-unit", editor.UnitStepTemplateId); Assert.Equal(before, JsonSerializer.Serialize(project));
                editor.PendingStepDraftPrompt = () => PlacementDraftDecision.Discard; Invoke((Button)dialog.FindName("AddUnitStepButton")); Pump(window); Assert.False(dialog.IsVisible);
            });
            Assert.Equal(3, project.Sequences[0].Steps.Count); Assert.Equal("0.1", project.Sequences[0].Steps[0].Parameter); Assert.True(window.IsVisible);
            return true;
        }
        finally { CloseFixture(main, window, previous); }
    });

    [Theory]
    [InlineData(OpenVisionLanguage.Korean)]
    [InlineData(OpenVisionLanguage.English)]
    public Task LongUnitNamesAndContextReplacementKeepContentAndResultLifetime(OpenVisionLanguage language) => ui.InvokeAsync(() =>
    {
        var previous = OpenVisionLanguageService.CurrentLanguage; OpenVisionLanguageService.SetLanguage(language, save: false);
        var project = CreateProject(); project.Stations[0].Units[1].Name = language == OpenVisionLanguage.Korean
            ? string.Join(" ", Enumerable.Repeat("두 번째 검사 위치의 제품 투입과 수신을 위한 유닛", 5)) : string.Join(" ", Enumerable.Repeat("Second inspection position product feeding and receiving unit", 5));
        using var main = new MainViewModel(project); main.Navigation.HideStartupChoice(); main.Navigation.IsProcessWorkspace = true;
        var window = Show(main, false);
        try
        {
            WithDialog(window, dialog =>
            {
                var area = (Expander)dialog.FindName("UnitStepAuthoring"); area.IsExpanded = true; WaitForUnitContent(dialog, area);
                ((ComboBox)dialog.FindName("UnitStepTemplate")).SetCurrentValue(Selector.SelectedValueProperty, "eject-unit"); Pump(dialog);
                var target = (TextBlock)dialog.FindName("UnitStepTarget"); Assert.Equal(project.Stations[0].Units[1].Name + " (second)", target.Text.Replace("\u2060", string.Empty));
                target.Measure(new Size(target.ActualWidth, double.PositiveInfinity)); Assert.True(target.DesiredSize.Height <= target.ActualHeight + target.Margin.Top + target.Margin.Bottom + 1);
                var title = Descendants(dialog).OfType<TextBlock>().Single(block => block.Inlines.OfType<Run>().Any(run => BindingOperations.GetBinding(run, Run.TextProperty)?.Path?.Path == nameof(SequenceEditorViewModel.SequenceUnitNameText)));
                var titleName = title.Inlines.OfType<Run>().Single(run => BindingOperations.GetBinding(run, Run.TextProperty)?.Path?.Path == nameof(SequenceEditorViewModel.SequenceUnitNameText));
                Assert.Equal(project.Stations[0].Units[1].Name, titleName.Text.Replace("\u2060", string.Empty));
                title.Measure(new Size(title.ActualWidth, double.PositiveInfinity)); Assert.True(title.DesiredSize.Height <= title.ActualHeight + title.Margin.Top + title.Margin.Bottom + 1);
                Capture(dialog, $"unit-long-{language}", false);
                var replacement = new SequenceEditorViewModel(); replacement.Load(CreateProject()); replacement.UnitStepTemplateId = "eject-unit";
                try
                {
                    dialog.DataContext = replacement; Pump(dialog);
                    main.SequenceEditor.AddUnitStepCommand.Execute(null); Pump(window); Assert.True(dialog.IsVisible);
                    var add = (Button)dialog.FindName("AddUnitStepButton"); add.BringIntoView(); Pump(dialog); Invoke(add); Pump(window); Assert.False(dialog.IsVisible);
                    replacement.UnitStepTemplateId = "feed-unit"; replacement.AddUnitStepCommand.Execute(null); Assert.Equal(4, replacement.SelectedSequence!.Steps.Count);
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
    public Task UnassignedUnitDisablesCandidatesAndExistingAssignmentRestoresExplicitChoice(OpenVisionLanguage language) => ui.InvokeAsync(() =>
    {
        var previous = OpenVisionLanguageService.CurrentLanguage; OpenVisionLanguageService.SetLanguage(language, save: false);
        var project = CreateProject(); project.Sequences[0].UnitId = null;
        using var main = new MainViewModel(project); main.Navigation.HideStartupChoice(); main.Navigation.IsProcessWorkspace = true;
        var window = Show(main, false);
        try
        {
            WithDialog(window, dialog =>
            {
                var area = (Expander)dialog.FindName("UnitStepAuthoring"); area.IsExpanded = true; WaitForUnitContent(dialog, area);
                var template = (ComboBox)dialog.FindName("UnitStepTemplate"); var add = (Button)dialog.FindName("AddUnitStepButton");
                Assert.Empty(template.Items); Assert.False(add.IsEnabled); Assert.Equal("—", ((TextBlock)dialog.FindName("UnitStepTarget")).Text); Capture(dialog, $"unit-empty-{language}", false);
                main.SequenceEditor.SelectedUnitId = "second"; Pump(dialog); Assert.Equal(3, template.Items.Count); Assert.Null(template.SelectedItem); Assert.False(add.IsEnabled);
                template.SetCurrentValue(Selector.SelectedValueProperty, "feed-unit"); Pump(dialog); Assert.True(add.IsEnabled); Invoke(add); Pump(window); Assert.False(dialog.IsVisible);
            });
            Assert.Equal("second", project.Sequences[0].Steps.Single(step => step.Id == "step-1").TargetId); Assert.False(main.IsRunning);
            return true;
        }
        finally { CloseFixture(main, window, previous); }
    });

    [Fact]
    public Task UnitAddAndCandidateChangeStayLockedDuringIsolatedStepTest() => ui.InvokeTaskAsync(async () =>
    {
        var project = CreateProject(); var editor = new SequenceEditorViewModel();
        int busyEntries = 0;
        System.ComponentModel.PropertyChangedEventHandler handler = (_, args) =>
        {
            if (args.PropertyName != nameof(editor.IsStepTestBusy) || !editor.IsStepTestBusy) return;
            busyEntries++;
            string before = JsonSerializer.Serialize(project);
            Assert.False(editor.AddUnitStepCommand.CanExecute(null)); editor.AddUnitStepCommand.Execute(null);
            editor.UnitStepTemplateId = "eject-unit";
            Assert.Equal("feed-unit", editor.UnitStepTemplateId); Assert.Equal(before, JsonSerializer.Serialize(project));
        };
        try
        {
            project.Sequences[0].Steps[0] = new() { Id = "move", Action = SequenceStepAction.MoveAxis, TargetId = "axis", Parameter = "2" };
            editor.Load(project); editor.UnitStepTemplateId = "feed-unit";
            Assert.True(editor.IsStepTestSupported);
            string before = JsonSerializer.Serialize(project); editor.PropertyChanged += handler;
            await editor.TestSelectedStepAsync();
            Assert.Equal(1, busyEntries); Assert.False(editor.IsStepTestBusy); Assert.True(editor.StepTestResult!.IsCompleted);
            Assert.Equal(before, JsonSerializer.Serialize(project)); Assert.True(editor.AddUnitStepCommand.CanExecute(null));
            editor.AddUnitStepCommand.Execute(null); Assert.Equal(3, project.Sequences[0].Steps.Count);
            return true;
        }
        finally { editor.PropertyChanged -= handler; editor.Dispose(); }
    });

    private static MachineProjectDocument CreateProject()
    {
        var project = R40ProcessWorkspaceTests.CreateProject();
        project.Stations = [new MachineStationDefinition { Id = "station", Name = "공정 장비", Units = [new() { Id = "first", Name = "검사 유닛" }, new() { Id = "second", Name = "검사 유닛" }] }];
        project.Sequences[0].UnitId = "second";
        project.Sequences[0].Steps = [new() { Id = "feed", Name = "준비 대기", Action = SequenceStepAction.Delay, Parameter = "0.1" }, new() { Id = "done", Name = "완료", Action = SequenceStepAction.Complete }];
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
        var folder = Path.Combine(TestStorage.RootPath, "unit-authoring"); Directory.CreateDirectory(folder);
        File.AppendAllText(Path.Combine(folder, "fixture-windows.jsonl"), JsonSerializer.Serialize(new { ownWindowRetained = remaining.Contains(window),
            window.IsVisible, dirty = main.HasUnsavedChanges, remainingWindows = remaining.Select(item => new { type = item.GetType().Name, item.Title, item.IsVisible }).ToArray() }) + Environment.NewLine);
        Assert.DoesNotContain(window, remaining); Assert.False(window.IsVisible);
        OpenVisionLanguageService.SetLanguage(previous, save: false);
    }

    private static void WaitForUnitContent(Window window, Expander expander, double? cameraCollapsedHeight = null)
    {
        var names = new[] { "UnitStepTarget", "UnitStepTemplate", "UnitStepPreviewValue", "UnitStepPreviewTimeout", "UnitStepHint", "AddUnitStepButton" };
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
        Assert.True(stable >= 2 && Ready(out _), "Unit section must finish revealing before capture; no product animation or native-input acceptance change.");
        probes.Single(probe => probe.Name == "AddUnitStepButton").BringIntoView(); Pump(window);
        var scroll = Descendants(window).OfType<ScrollViewer>().Single(viewer => viewer.Content is Panel panel && panel.Children.Contains(expander));
        var presenter = Descendants(scroll).OfType<ScrollContentPresenter>().Single(item => ReferenceEquals(item.TemplatedParent, scroll));
        var viewport = new Rect(presenter.RenderSize); viewport.Inflate(1e-6, 1e-6);
        foreach (var probe in probes)
        {
            if (probe is TextBlock { Text.Length: 0 }) continue;
            Assert.True(viewport.Contains(probe.TransformToAncestor(presenter).TransformBounds(new Rect(probe.RenderSize))), probe.Name + " must be fully visible after scrolling to the unit action.");
        }
    }

    private static void Capture(Window window, string name, bool fullArea)
    {
        Pump(window);
        if (window is SequenceStepTemplateDialog dialog && dialog.FindName("UnitStepAuthoring") is Expander { IsExpanded: true } expander) WaitForUnitContent(window, expander);
        var monitor = SmokeDpiTestHook.CaptureMonitorEvidence(window);
        var dpi = VisualTreeHelper.GetDpi(window);
        var folder = Path.Combine(TestStorage.RootPath, "unit-authoring"); Directory.CreateDirectory(folder);
        new SmokeWindowCapture().Capture(window, Path.Combine(folder, name + ".png"));
        File.WriteAllText(Path.Combine(folder, name + ".json"), JsonSerializer.Serialize(new { monitor, width = (int)Math.Round(window.ActualWidth * dpi.DpiScaleX),
            height = (int)Math.Round(window.ActualHeight * dpi.DpiScaleY), wpfDpi = dpi.PixelsPerInchX, fullArea, crop = "none",
            method = "Actual in-process WPF RenderTargetBitmap; dialog is a separate supplemental window image, never composited into the FHD parent." }));
        Assert.True(monitor.WindowContainedByMonitor && monitor.WindowIntersectsMonitor, JsonSerializer.Serialize(monitor));
        var area = monitor.WorkingArea.Split(',').Select(int.Parse).ToArray(); var rect = monitor.WindowRect.Split(',').Select(int.Parse).ToArray();
        Assert.True(new Rect(area[0], area[1], area[2], area[3]).Contains(new Rect(rect[0], rect[1], rect[2], rect[3])), "Owned capture must stay inside the actual work area: " + JsonSerializer.Serialize(monitor));
    }
}
