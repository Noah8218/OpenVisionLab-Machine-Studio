using System.IO;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using OpenVisionLab;
using OpenVisionLab.Machine.Core.Channels;
using OpenVisionLab.Machine.Core.Layouts;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Core.Sequences;
using OpenVisionLab.Machine.Sequence.Authoring;
using OpenVisionLab.MachineStudio.Model;
using OpenVisionLab.MachineStudio.View.Sequence;
using OpenVisionLab.MachineStudio.View.Shell;
using OpenVisionLab.MachineStudio.ViewModel;
using OpenVisionLab.TestSupport;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

[Collection(StudioUiTestCollection.Name)]
public sealed class R40SignalAuthoringTests(StudioUiTestHost ui)
{
    [Theory]
    [InlineData(OpenVisionLanguage.Korean, true)]
    [InlineData(OpenVisionLanguage.English, true)]
    [InlineData(OpenVisionLanguage.Korean, false)]
    [InlineData(OpenVisionLanguage.English, false)]
    public Task LegacySignalTemplatesKeepDefaultTargetsAndExplicitAdd(OpenVisionLanguage language, bool fullArea) => ui.InvokeAsync(() =>
    {
        var previous = OpenVisionLanguageService.CurrentLanguage; OpenVisionLanguageService.SetLanguage(language, save: false);
        var project = CreateProject(); using var main = new MainViewModel(project);
        main.Navigation.HideStartupChoice(); main.Navigation.IsProcessWorkspace = true;
        var window = Show(main, fullArea);
        try
        {
            foreach (var choice in new[] { (Template: "wait-input-off", Target: "di-a", Parameter: "false"), (Template: "set-output-on", Target: "do-a", Parameter: "true") })
            {
                string before = JsonSerializer.Serialize(project);
                WithDialog(window, dialog =>
                {
                    Assert.Same(main.SequenceEditor, dialog.DataContext); Assert.Equal(before, JsonSerializer.Serialize(project));
                    Capture(dialog, $"legacy-dialog-{choice.Template}-{language}-{fullArea}", false);
                    var add = Descendants(dialog).OfType<Button>().Single(button => button.CommandParameter is SequenceStepTemplateDefinition template && template.Id == choice.Template);
                    Invoke(add); Pump(window);
                });
                var created = project.Sequences[0].Steps[^2]; Assert.Equal(choice.Target, created.TargetId); Assert.Equal(choice.Parameter, created.Parameter);
                Assert.Equal("done", project.Sequences[0].Steps.Last().Id); Assert.False(main.IsRunning); Assert.Empty(main.SequenceEditor.ValidationIssues);
            }
            Assert.Equal(4, project.Sequences[0].Steps.Count); Assert.True(main.HasUnsavedChanges);
            Capture(window, $"legacy-added-{language}-{(fullArea ? "work-area" : "compact")}", fullArea);
            return true;
        }
        finally { CloseFixture(main, window, previous); }
    });

    [Theory]
    [InlineData("di-b", "wait-input-on", SequenceStepAction.WaitSignal, "true", 5000)]
    [InlineData("di-b", "wait-input-off", SequenceStepAction.WaitSignal, "false", 5000)]
    [InlineData("do-b", "set-output-on", SequenceStepAction.SetSignal, "true", 0)]
    [InlineData("do-b", "set-output-off", SequenceStepAction.SetSignal, "false", 0)]
    public Task ExplicitSignalAddUsesActualIdAndUnchangedSingleFactory(string targetId, string templateId, SequenceStepAction action, string parameter, int timeout) => ui.InvokeAsync(() =>
    {
        var project = CreateProject(); var editor = new SequenceEditorViewModel();
        try
        {
            editor.Load(project); var selected = editor.SelectedStep; var draft = editor.SelectedStepDraft;
            string before = JsonSerializer.Serialize(project); int changed = 0, added = 0, otherAdded = 0;
            editor.DefinitionChanged += (_, _) => changed++; editor.SignalStepAdded += (_, _) => added++;
            editor.AxisStepAdded += (_, _) => otherAdded++; editor.CameraStepAdded += (_, _) => otherAdded++;
            Assert.Equal(new[] { "di-a", "di-b", "do-a", "do-b" }, editor.SignalStepTargets.Select(target => target.Id));
            Assert.Empty(editor.SignalStepTemplates); Assert.Null(editor.SignalStepPreview); Assert.False(editor.AddSignalStepCommand.CanExecute(null));
            editor.SignalStepTargetId = targetId;
            Assert.Equal(string.Empty, editor.SignalStepTemplateId); Assert.False(editor.AddSignalStepCommand.CanExecute(null));
            editor.SignalStepTemplateId = templateId;
            Assert.Equal(action, editor.SignalStepPreview!.Action); Assert.Equal(targetId, editor.SignalStepPreview.TargetId);
            Assert.Equal(parameter, editor.SignalStepPreview.Parameter); Assert.Equal(timeout, editor.SignalStepPreview.TimeoutMs);
            Assert.Same(selected, editor.SelectedStep); Assert.Same(draft, editor.SelectedStepDraft); Assert.False(editor.HasPendingStepDraft);
            Assert.Equal(before, JsonSerializer.Serialize(project)); Assert.Equal(0, changed + added + otherAdded);
            editor.AddSignalStepCommand.Execute(null);
            var step = project.Sequences[0].Steps.Single(item => item.Id == "step-1");
            Assert.Equal(action, step.Action); Assert.Equal(targetId, step.TargetId); Assert.Equal(parameter, step.Parameter); Assert.Equal(timeout, step.TimeoutMs);
            Assert.Equal("step-1", project.Sequences[0].Steps[0].NextStepId); Assert.Equal("done", step.NextStepId); Assert.Equal("done", project.Sequences[0].Steps.Last().Id);
            Assert.Equal(3, project.Sequences[0].Steps.Count); Assert.Empty(editor.ValidationIssues);
            Assert.Equal(1, changed); Assert.Equal(1, added); Assert.Equal(0, otherAdded);
            Assert.Equal(string.Empty, editor.SignalStepTargetId); Assert.Equal(string.Empty, editor.SignalStepTemplateId);
            Assert.False(editor.AddSignalStepCommand.CanExecute(null)); editor.AddSignalStepCommand.Execute(null); Assert.Equal(3, project.Sequences[0].Steps.Count);
            return true;
        }
        finally { editor.Dispose(); }
    });

    [Fact]
    public Task ChannelKindChoosesOnlyMatchingCandidatesAndEveryTargetChangeRequiresAChoice() => ui.InvokeAsync(() =>
    {
        var project = CreateProject(); var editor = new SequenceEditorViewModel();
        try
        {
            editor.Load(project); string before = JsonSerializer.Serialize(project);
            editor.SignalStepTargetId = "di-b";
            Assert.Equal(new[] { "wait-input-on", "wait-input-off" }, editor.SignalStepTemplates.Select(item => item.Id));
            editor.SignalStepTemplateId = "set-output-on"; Assert.Equal(string.Empty, editor.SignalStepTemplateId); Assert.Null(editor.SignalStepPreview);
            editor.SignalStepTemplateId = "wait-input-off"; Assert.Equal("OFF", editor.SignalStepPreviewValue); Assert.Equal("5000", editor.SignalStepPreviewTimeout);
            editor.SignalStepTargetId = "di-a"; Assert.Equal(string.Empty, editor.SignalStepTemplateId); Assert.Null(editor.SignalStepPreview);
            editor.SignalStepTargetId = "do-b";
            Assert.Equal(new[] { "set-output-on", "set-output-off" }, editor.SignalStepTemplates.Select(item => item.Id));
            editor.SignalStepTemplateId = "wait-input-on"; Assert.Equal(string.Empty, editor.SignalStepTemplateId);
            editor.SignalStepTemplateId = "set-output-on"; Assert.Equal("ON", editor.SignalStepPreviewValue); Assert.Equal("0", editor.SignalStepPreviewTimeout);
            Assert.Equal(before, JsonSerializer.Serialize(project)); Assert.False(editor.HasPendingStepDraft);
            return true;
        }
        finally { editor.Dispose(); }
    });

    [Theory]
    [InlineData("readonly")]
    [InlineData("nonlinear")]
    [InlineData("removed-channel")]
    [InlineData("kind-changed")]
    [InlineData("analog-changed")]
    [InlineData("duplicate-channel")]
    [InlineData("duplicate-analog")]
    [InlineData("empty-id")]
    [InlineData("removed-sequence")]
    [InlineData("foreign-target")]
    [InlineData("layout-id")]
    [InlineData("analog-target")]
    [InlineData("template-removed")]
    [InlineData("disposed")]
    [InlineData("foreign-sequence")]
    public Task SignalAddRejectsUnavailableOrStaleContext(string boundary) => ui.InvokeAsync(() =>
    {
        var project = CreateProject(); var editor = new SequenceEditorViewModel();
        try
        {
            editor.Load(project); editor.SignalStepTargetId = "di-b"; editor.SignalStepTemplateId = "wait-input-off";
            switch (boundary)
            {
                case "readonly": editor.IsEditable = false; break;
                case "nonlinear": project.Sequences[0].Steps[0].NextStepId = "feed"; break;
                case "removed-channel": project.Channels.RemoveAt(1); break;
                case "kind-changed": project.Channels[1].Kind = ChannelKind.DigitalOutput; break;
                case "analog-changed": project.Channels[1].Kind = ChannelKind.AnalogInput; break;
                case "duplicate-channel": project.Channels.Add(new ChannelDefinition { Id = "di-b", Kind = ChannelKind.DigitalInput }); break;
                case "duplicate-analog": project.Channels.Add(new ChannelDefinition { Id = "di-b", Kind = ChannelKind.AnalogInput }); break;
                case "empty-id": project.Channels[1].Id = string.Empty; break;
                case "removed-sequence": project.Sequences.Clear(); break;
                case "foreign-target": editor.SignalStepTargetId = "camera"; break;
                case "layout-id": editor.SignalStepTargetId = "stage"; break;
                case "analog-target": editor.SignalStepTargetId = "analog"; break;
                case "template-removed": editor.Templates.Clear(); break;
                case "disposed": editor.Dispose(); break;
                case "foreign-sequence": editor.SelectedSequence = new SequenceDefinition { Id = "sequence", Steps = [new() { Id = "done", Action = SequenceStepAction.Complete }] }; break;
            }
            string before = JsonSerializer.Serialize(project);
            Assert.False(editor.AddSignalStepCommand.CanExecute(null)); editor.AddSignalStepCommand.Execute(null);
            Assert.Equal(before, JsonSerializer.Serialize(project));
            return true;
        }
        finally { editor.Dispose(); }
    });

    [Fact]
    public Task SignalRefreshLoadSequenceAndPromptReentryKeepCurrentContext() => ui.InvokeAsync(() =>
    {
        var project = CreateProject(); var editor = new SequenceEditorViewModel();
        try
        {
            editor.Load(project); editor.SignalStepTargetId = "di-b"; editor.SignalStepTemplateId = "wait-input-off";
            project.Channels[1].Name = "실제 입력"; editor.RefreshAuthoringTargets(); Assert.Equal("실제 입력 (di-b)", editor.SignalStepTarget!.Name);
            var other = new SequenceDefinition { Id = "other", Steps = [new() { Id = "done", Action = SequenceStepAction.Complete }] };
            project.Sequences.Add(other); editor.SelectedSequence = other;
            Assert.Equal(string.Empty, editor.SignalStepTargetId); Assert.Equal(string.Empty, editor.SignalStepTemplateId);
            editor.Load(project); editor.SignalStepTargetId = "di-b"; editor.SignalStepTemplateId = "wait-input-off";
            var replacement = JsonSerializer.Deserialize<MachineProjectDocument>(JsonSerializer.Serialize(project))!;
            editor.Load(replacement); Assert.Null(editor.SignalStepPreview);
            editor.SignalStepTargetId = "di-b"; editor.SignalStepTemplateId = "wait-input-off"; editor.SelectedStepDraft!.Parameter = "0.2";
            string before = JsonSerializer.Serialize(replacement);
            editor.PendingStepDraftPrompt = () => { editor.Load(project); return PlacementDraftDecision.Discard; };
            editor.AddSignalStepCommand.Execute(null); Assert.Equal(before, JsonSerializer.Serialize(replacement)); Assert.Equal(2, project.Sequences[0].Steps.Count);
            editor.SignalStepTargetId = "di-b"; editor.SignalStepTemplateId = "wait-input-off"; editor.SelectedStepDraft!.Parameter = "0.3";
            editor.PendingStepDraftPrompt = () => { project.Channels.RemoveAt(1); return PlacementDraftDecision.Discard; };
            editor.AddSignalStepCommand.Execute(null); Assert.Equal(2, project.Sequences[0].Steps.Count); Assert.Null(editor.SignalStepPreview); Assert.Equal(string.Empty, editor.SignalStepTargetId);
            return true;
        }
        finally { editor.Dispose(); }
    });

    [Theory]
    [InlineData(OpenVisionLanguage.Korean, true)]
    [InlineData(OpenVisionLanguage.English, true)]
    [InlineData(OpenVisionLanguage.Korean, false)]
    [InlineData(OpenVisionLanguage.English, false)]
    public Task RenderedSignalChoicesPreviewAddAndExistingApplyStaySeparate(OpenVisionLanguage language, bool fullArea) => ui.InvokeAsync(() =>
    {
        var previous = OpenVisionLanguageService.CurrentLanguage; OpenVisionLanguageService.SetLanguage(language, save: false);
        var project = CreateProject(); using var main = new MainViewModel(project);
        main.Navigation.HideStartupChoice(); main.Navigation.IsProcessWorkspace = true;
        var editor = main.SequenceEditor; var window = Show(main, fullArea);
        try
        {
            var clock = main.SceneSnapshots.Latest!.SimulationTime;
            var signals = JsonSerializer.Serialize(main.SceneSnapshots.Latest.Signals);
            var positions = main.SceneSnapshots.Latest.Axes.Select(axis => axis.Position).ToArray();
            foreach (var choice in new[] { (Id: "di-b", Template: "wait-input-off", Kind: "DI", Value: "OFF", Timeout: "5000"), (Id: "do-b", Template: "set-output-on", Kind: "DO", Value: "ON", Timeout: "0") })
            {
                string before = JsonSerializer.Serialize(project); var selected = editor.SelectedStep; var draft = editor.SelectedStepDraft; bool dirtyBefore = main.HasUnsavedChanges;
                WithDialog(window, dialog =>
                {
                    Assert.Same(editor, dialog.DataContext);
                    var expander = (Expander)dialog.FindName("SignalStepAuthoring"); var cameraArea = (Expander)dialog.FindName("CameraStepAuthoring");
                    double cameraCollapsedHeight = cameraArea.ActualHeight;
                    Assert.False(expander.IsExpanded || cameraArea.IsExpanded); expander.IsExpanded = true; WaitForSignalContent(dialog, expander);
                    var channel = (ComboBox)dialog.FindName("SignalStepTarget"); var template = (ComboBox)dialog.FindName("SignalStepTemplate"); var add = (Button)dialog.FindName("AddSignalStepButton");
                    Assert.False(add.IsEnabled); channel.SetCurrentValue(Selector.SelectedValueProperty, choice.Id); Pump(dialog);
                    Assert.Equal(choice.Id, editor.SignalStepTargetId); Assert.Equal(string.Empty, editor.SignalStepTemplateId); Assert.False(add.IsEnabled);
                    Assert.Equal(2, template.Items.Count); template.SetCurrentValue(Selector.SelectedValueProperty, choice.Template); Pump(dialog);
                    Assert.Equal(choice.Template, editor.SignalStepTemplateId);
                    string alternate = choice.Kind == "DI" ? "wait-input-on" : "set-output-off";
                    editor.SignalStepTemplateId = alternate; Pump(dialog); Assert.Equal(alternate, template.SelectedValue);
                    Assert.Equal(choice.Kind == "DI" ? "ON" : "OFF", ((TextBlock)dialog.FindName("SignalStepPreviewValue")).Text);
                    Capture(dialog, $"signal-alternate-{choice.Kind}-{language}-{fullArea}", false);
                    template.SetCurrentValue(Selector.SelectedValueProperty, choice.Template); Pump(dialog);
                    Assert.Equal(choice.Kind, ((TextBlock)dialog.FindName("SignalStepPreviewKind")).Text);
                    Assert.Equal("검사 신호 (" + choice.Id + ")", ((TextBlock)dialog.FindName("SignalStepPreviewTarget")).Text.Replace("\u2060", string.Empty));
                    Assert.Equal(choice.Value, ((TextBlock)dialog.FindName("SignalStepPreviewValue")).Text); Assert.Equal(choice.Timeout, ((TextBlock)dialog.FindName("SignalStepPreviewTimeout")).Text);
                    Assert.Equal(before, JsonSerializer.Serialize(project)); Assert.Same(selected, editor.SelectedStep); Assert.Same(draft, editor.SelectedStepDraft); Assert.False(editor.HasPendingStepDraft || main.IsRunning); Assert.Equal(dirtyBefore, main.HasUnsavedChanges);
                    Assert.Equal(clock, main.SceneSnapshots.Latest!.SimulationTime); Assert.Equal(signals, JsonSerializer.Serialize(main.SceneSnapshots.Latest.Signals)); Assert.Equal(positions, main.SceneSnapshots.Latest.Axes.Select(axis => axis.Position));
                    editor.IsEditable = false; Pump(dialog); Assert.False(channel.IsEnabled || template.IsEnabled || add.IsEnabled);
                    Capture(dialog, $"signal-readonly-{choice.Kind}-{language}-{fullArea}", false);
                    editor.IsEditable = true; Pump(dialog);
                    if (choice.Kind == "DI")
                    {
                        cameraArea.IsExpanded = true; WaitForSignalContent(dialog, expander); Capture(dialog, $"signal-both-{language}-{fullArea}", false);
                        cameraArea.IsExpanded = false; WaitForSignalContent(dialog, expander, cameraCollapsedHeight);
                    }
                    Capture(dialog, $"signal-preview-{choice.Kind}-{language}-{fullArea}", false);
                    Assert.Same(editor.AddSignalStepCommand, add.Command); Invoke(add); Pump(window); Assert.False(dialog.IsVisible);
                });
                Assert.Equal(clock, main.SceneSnapshots.Latest!.SimulationTime); Assert.Equal(signals, JsonSerializer.Serialize(main.SceneSnapshots.Latest.Signals));
            }
            Assert.Equal(new[] { "di-b", "do-b" }, project.Sequences[0].Steps.Skip(1).Take(2).Select(step => step.TargetId));
            Assert.Equal("false", project.Sequences[0].Steps[1].Parameter); Assert.Equal(5000, project.Sequences[0].Steps[1].TimeoutMs);
            var output = project.Sequences[0].Steps[2]; Assert.Equal("true", output.Parameter); Assert.Equal(0, output.TimeoutMs); Assert.Equal("done", output.NextStepId);
            Assert.Equal(4, project.Sequences[0].Steps.Count); Assert.Empty(editor.ValidationIssues); Assert.True(main.HasUnsavedChanges); Assert.False(main.IsRunning);
            Capture(window, $"signal-added-{language}-{(fullArea ? "work-area" : "compact")}", fullArea);
            editor.SelectedStepDraft!.Parameter = "false"; Assert.Equal("true", output.Parameter); Assert.True(editor.HasPendingStepDraft);
            editor.DiscardStepDraft(); Assert.Equal("true", editor.SelectedStepDraft!.Parameter);
            editor.SelectedStepDraft.Parameter = "false";
            var inspector = Descendants(window).OfType<SequenceStepInspectorView>().Single(item => item.IsVisible);
            Invoke((Button)inspector.FindName("ApplyStepDraftButton")); Pump(window); Assert.Equal("false", output.Parameter); Assert.False(editor.HasPendingStepDraft || main.IsRunning);
            Assert.Equal(signals, JsonSerializer.Serialize(main.SceneSnapshots.Latest!.Signals));
            return true;
        }
        finally { CloseFixture(main, window, previous); }
    });

    [Theory]
    [InlineData(OpenVisionLanguage.Korean)]
    [InlineData(OpenVisionLanguage.English)]
    public Task SignalDialogCancelKeepsChoicesAndDraftUntilExplicitAddSucceeds(OpenVisionLanguage language) => ui.InvokeAsync(() =>
    {
        var previous = OpenVisionLanguageService.CurrentLanguage; OpenVisionLanguageService.SetLanguage(language, save: false);
        var project = CreateProject(); using var main = new MainViewModel(project);
        main.Navigation.HideStartupChoice(); main.Navigation.IsProcessWorkspace = true;
        var editor = main.SequenceEditor; var window = Show(main, false);
        try
        {
            editor.SelectedStepDraft!.Parameter = "0.2"; editor.PendingStepDraftPrompt = () => PlacementDraftDecision.Cancel; string before = JsonSerializer.Serialize(project);
            WithDialog(window, dialog =>
            {
                var area = (Expander)dialog.FindName("SignalStepAuthoring"); area.IsExpanded = true; WaitForSignalContent(dialog, area);
                ((ComboBox)dialog.FindName("SignalStepTarget")).SetCurrentValue(Selector.SelectedValueProperty, "di-b"); Pump(dialog);
                ((ComboBox)dialog.FindName("SignalStepTemplate")).SetCurrentValue(Selector.SelectedValueProperty, "wait-input-off"); Pump(dialog);
                Invoke((Button)dialog.FindName("AddSignalStepButton")); Pump(window);
                Assert.True(dialog.IsVisible && editor.HasPendingStepDraft); Assert.Equal("di-b", editor.SignalStepTargetId); Assert.Equal("wait-input-off", editor.SignalStepTemplateId); Assert.Equal(before, JsonSerializer.Serialize(project));
                editor.PendingStepDraftPrompt = () => PlacementDraftDecision.Discard;
                Invoke((Button)dialog.FindName("AddSignalStepButton")); Pump(window); Assert.False(dialog.IsVisible);
            });
            Assert.Equal(3, project.Sequences[0].Steps.Count); Assert.Equal("0.1", project.Sequences[0].Steps[0].Parameter); Assert.True(window.IsVisible);
            return true;
        }
        finally { CloseFixture(main, window, previous); }
    });

    [Theory]
    [InlineData(OpenVisionLanguage.Korean)]
    [InlineData(OpenVisionLanguage.English)]
    public Task LongChannelNamesAndContextReplacementKeepNewContentReachable(OpenVisionLanguage language) => ui.InvokeAsync(() =>
    {
        var previous = OpenVisionLanguageService.CurrentLanguage; OpenVisionLanguageService.SetLanguage(language, save: false);
        var project = CreateProject(); project.Channels[1].Name = language == OpenVisionLanguage.Korean
            ? string.Join(" ", Enumerable.Repeat("두 번째 검사 위치의 제품 감지 입력 신호", 7)) : string.Join(" ", Enumerable.Repeat("Second inspection position workpiece detection input signal", 7));
        using var main = new MainViewModel(project); main.Navigation.HideStartupChoice(); main.Navigation.IsProcessWorkspace = true;
        var window = Show(main, false);
        try
        {
            WithDialog(window, dialog =>
            {
                var area = (Expander)dialog.FindName("SignalStepAuthoring"); area.IsExpanded = true; WaitForSignalContent(dialog, area);
                ((ComboBox)dialog.FindName("SignalStepTarget")).SetCurrentValue(Selector.SelectedValueProperty, "di-b"); Pump(dialog);
                ((ComboBox)dialog.FindName("SignalStepTemplate")).SetCurrentValue(Selector.SelectedValueProperty, "wait-input-off"); Pump(dialog);
                var target = (TextBlock)dialog.FindName("SignalStepPreviewTarget"); Assert.Equal(project.Channels[1].Name + " (di-b)", target.Text.Replace("\u2060", string.Empty));
                target.Measure(new Size(target.ActualWidth, double.PositiveInfinity)); Assert.True(target.DesiredSize.Height <= target.ActualHeight + target.Margin.Top + target.Margin.Bottom + 1);
                Capture(dialog, $"signal-long-{language}", false);
                var replacement = new SequenceEditorViewModel(); replacement.Load(CreateProject()); replacement.SignalStepTargetId = "do-b"; replacement.SignalStepTemplateId = "set-output-on";
                try
                {
                    dialog.DataContext = replacement; Pump(dialog);
                    main.SequenceEditor.AddSignalStepCommand.Execute(null); Pump(window); Assert.True(dialog.IsVisible);
                    Invoke((Button)dialog.FindName("AddSignalStepButton")); Pump(window); Assert.False(dialog.IsVisible);
                    replacement.SignalStepTargetId = "do-b"; replacement.SignalStepTemplateId = "set-output-on"; replacement.AddSignalStepCommand.Execute(null); Assert.Equal(4, replacement.SelectedSequence!.Steps.Count);
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
    public Task EmptyDigitalCandidatesDisableAddAndRecoverWithAnActualChannel(OpenVisionLanguage language) => ui.InvokeAsync(() =>
    {
        var previous = OpenVisionLanguageService.CurrentLanguage; OpenVisionLanguageService.SetLanguage(language, save: false);
        var project = CreateProject(); var input = project.Channels[1]; project.Channels.RemoveAll(channel => channel.Kind is ChannelKind.DigitalInput or ChannelKind.DigitalOutput);
        using var main = new MainViewModel(project); main.Navigation.HideStartupChoice(); main.Navigation.IsProcessWorkspace = true;
        var window = Show(main, false);
        try
        {
            WithDialog(window, dialog =>
            {
                var area = (Expander)dialog.FindName("SignalStepAuthoring"); area.IsExpanded = true; WaitForSignalContent(dialog, area);
                var channel = (ComboBox)dialog.FindName("SignalStepTarget"); var template = (ComboBox)dialog.FindName("SignalStepTemplate"); var add = (Button)dialog.FindName("AddSignalStepButton");
                Assert.Empty(channel.Items); Assert.Empty(template.Items); Assert.False(add.IsEnabled); Capture(dialog, $"signal-empty-{language}", false);
                project.Channels.Add(input); main.SequenceEditor.RefreshAuthoringTargets(); Pump(dialog);
                channel.SetCurrentValue(Selector.SelectedValueProperty, "di-b"); Pump(dialog); Assert.False(add.IsEnabled);
                template.SetCurrentValue(Selector.SelectedValueProperty, "wait-input-off"); Pump(dialog); Assert.True(add.IsEnabled); Invoke(add); Pump(window); Assert.False(dialog.IsVisible);
            });
            Assert.Equal("di-b", project.Sequences[0].Steps.Single(step => step.Id == "step-1").TargetId);
            return true;
        }
        finally { CloseFixture(main, window, previous); }
    });

    private static MachineProjectDocument CreateProject()
    {
        var project = R40ProcessWorkspaceTests.CreateProject();
        project.Channels =
        [
            new() { Id = "di-a", Name = "검사 신호", Kind = ChannelKind.DigitalInput, InitialValue = 0 },
            new() { Id = "di-b", Name = "검사 신호", Kind = ChannelKind.DigitalInput, InitialValue = 1 },
            new() { Id = "do-a", Name = "검사 신호", Kind = ChannelKind.DigitalOutput, InitialValue = 0 },
            new() { Id = "do-b", Name = "검사 신호", Kind = ChannelKind.DigitalOutput, InitialValue = 0 },
            new() { Id = "analog", Name = "검사 신호", Kind = ChannelKind.AnalogInput, InitialValue = 2.5 }
        ];
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
        var folder = Path.Combine(TestStorage.RootPath, "signal-authoring"); Directory.CreateDirectory(folder);
        File.AppendAllText(Path.Combine(folder, "fixture-windows.jsonl"), JsonSerializer.Serialize(new { ownWindowRetained = remaining.Contains(window),
            window.IsVisible, dirty = main.HasUnsavedChanges, remainingWindows = remaining.Select(item => new { type = item.GetType().Name, item.Title, item.IsVisible }).ToArray() }) + Environment.NewLine);
        Assert.DoesNotContain(window, remaining); Assert.False(window.IsVisible);
        OpenVisionLanguageService.SetLanguage(previous, save: false);
    }

    private static void WaitForSignalContent(Window window, Expander expander, double? cameraCollapsedHeight = null)
    {
        var names = new[] { "SignalStepTarget", "SignalStepTemplate", "SignalStepPreviewKind", "SignalStepPreviewTarget", "SignalStepPreviewValue", "SignalStepPreviewTimeout", "SignalStepHint", "AddSignalStepButton" };
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
        Assert.True(stable >= 2 && Ready(out _), "I/O section must finish revealing before capture; no product animation or native-input acceptance change.");
        probes.Single(probe => probe.Name == "AddSignalStepButton").BringIntoView(); Pump(window);
        var scroll = Descendants(window).OfType<ScrollViewer>().Single(viewer => viewer.Content is Panel panel && panel.Children.Contains(expander));
        var presenter = Descendants(scroll).OfType<ScrollContentPresenter>().Single(item => ReferenceEquals(item.TemplatedParent, scroll));
        var viewport = new Rect(presenter.RenderSize); viewport.Inflate(1e-6, 1e-6);
        foreach (var probe in probes)
        {
            if (probe is TextBlock { Text.Length: 0 }) continue;
            Assert.True(viewport.Contains(probe.TransformToAncestor(presenter).TransformBounds(new Rect(probe.RenderSize))), probe.Name + " must be fully visible after scrolling to the I/O action.");
        }
    }

    private static void Capture(Window window, string name, bool fullArea)
    {
        Pump(window);
        if (window is SequenceStepTemplateDialog dialog && dialog.FindName("SignalStepAuthoring") is Expander { IsExpanded: true } expander) WaitForSignalContent(window, expander);
        var monitor = SmokeDpiTestHook.CaptureMonitorEvidence(window);
        var dpi = VisualTreeHelper.GetDpi(window);
        var folder = Path.Combine(TestStorage.RootPath, "signal-authoring"); Directory.CreateDirectory(folder);
        new SmokeWindowCapture().Capture(window, Path.Combine(folder, name + ".png"));
        File.WriteAllText(Path.Combine(folder, name + ".json"), JsonSerializer.Serialize(new { monitor, width = (int)Math.Round(window.ActualWidth * dpi.DpiScaleX),
            height = (int)Math.Round(window.ActualHeight * dpi.DpiScaleY), wpfDpi = dpi.PixelsPerInchX, fullArea, crop = "none",
            method = "Actual in-process WPF RenderTargetBitmap; dialog is a separate supplemental window image, never composited into the FHD parent." }));
        Assert.True(monitor.WindowContainedByMonitor && monitor.WindowIntersectsMonitor, JsonSerializer.Serialize(monitor));
        var area = monitor.WorkingArea.Split(',').Select(int.Parse).ToArray(); var rect = monitor.WindowRect.Split(',').Select(int.Parse).ToArray();
        Assert.True(new Rect(area[0], area[1], area[2], area[3]).Contains(new Rect(rect[0], rect[1], rect[2], rect[3])), "Owned capture must stay inside the actual work area: " + JsonSerializer.Serialize(monitor));
    }
}
