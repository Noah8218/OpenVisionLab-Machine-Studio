using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using OpenVisionLab;
using OpenVisionLab.Machine.Core.Axes;
using OpenVisionLab.Machine.Core.Layouts;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Core.Sequences;
using OpenVisionLab.Machine.Persistence.Projects;
using OpenVisionLab.MachineStudio.Model;
using OpenVisionLab.MachineStudio.View.Sequence;
using OpenVisionLab.MachineStudio.View.Shell;
using OpenVisionLab.MachineStudio.ViewModel;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

[Collection(StudioUiTestCollection.Name)]
public sealed class SequenceStepInspectorViewTests
{
    private readonly StudioUiTestHost _ui;
    public SequenceStepInspectorViewTests(StudioUiTestHost ui) => _ui = ui;

    [Theory]
    [InlineData(1280, 760, OpenVisionLanguage.Korean)]
    [InlineData(1280, 760, OpenVisionLanguage.English)]
    [InlineData(1920, 1040, OpenVisionLanguage.Korean)]
    [InlineData(1920, 1040, OpenVisionLanguage.English)]
    public async Task DockedStepDraftAppliesExplicitlyAndRecoversWithoutOverlap(double width, double height, OpenVisionLanguage language)
    {
        await _ui.InvokeAsync(() =>
        {
            var previousLanguage = OpenVisionLanguageService.CurrentLanguage;
            OpenVisionLanguageService.SetLanguage(language, save: false);
            var project = CreateProject();
            using var vm = new MainViewModel(project);
            vm.Navigation.HideStartupChoice();
            var window = new ShellWindow { DataContext = vm, Width = width, Height = height, ShowInTaskbar = false, ShowActivated = false, UseLayoutRounding = true };
            window.Show();
            try
            {
                vm.Navigation.OpenSequenceEditorCommand.Execute(null);
                Settle(window);
                var view = Descendants(window).OfType<SequenceStepInspectorView>().Single(control => control.IsVisible);
                var editor = Descendants(window).OfType<SequenceEditorView>().Single(control => control.IsVisible);
                var viewport = Descendants(window).OfType<OpenVisionLab.MachineStudio.View.Scene.MachineSceneViewport>().Single(control => control.IsVisible);
                var inspectorBounds = Bounds(view, window);
                var sceneBounds = Bounds(viewport, window);
                var listBounds = Bounds(editor, window);
                Assert.True(sceneBounds.Right <= inspectorBounds.Left && listBounds.Right <= inspectorBounds.Left, $"scene={sceneBounds};list={listBounds};inspector={inspectorBounds}");
                Assert.True(new Rect(window.RenderSize).Contains(inspectorBounds));
                Assert.False(editor.ShowInlineEditor);
                Assert.Same(vm.SequenceEditor, view.DataContext);
                var applied = vm.SequenceEditor.SelectedStep!;
                string original = JsonSerializer.Serialize(project);
                var applyBorder = Descendants(view.ApplyStepDraftButton).OfType<Border>().Single(border => border.Name == "InspectorButtonBorder");
                Assert.Equal("#FFE4F4EF", applyBorder.Background.ToString());
                Assert.Equal("#FFB7DBCA", applyBorder.BorderBrush.ToString());
                Assert.Equal("#FF718398", view.ApplyStepDraftButton.Foreground.ToString());
                Assert.Equal(1, view.ApplyStepDraftButton.Opacity);
                Assert.False(vm.SequenceEditor.IsStepParameterInvalid);
                Assert.Equal("#FFFFFFFF", view.StepParameterFrame.Background.ToString());
                Capture(window, $"{width}x{height}-{language}-entry");
                view.StepDraftName.SetCurrentValue(TextBox.TextProperty, new string('N', 80));
                view.StepDraftParameter.SetCurrentValue(TextBox.TextProperty, "NaN");
                Settle(window);
                Assert.Equal(original, JsonSerializer.Serialize(project));
                Assert.False(vm.HasUnsavedChanges || vm.IsRunning);
                Assert.True(vm.SequenceEditor.HasPendingStepDraft);
                Assert.False(view.ApplyStepDraftButton.IsEnabled);
                Assert.False(string.IsNullOrEmpty(view.StepDraftError.Text));
                Assert.True(vm.SequenceEditor.IsStepParameterInvalid);
                Assert.Equal("#FFFFF0F3", view.StepParameterFrame.Background.ToString());
                Assert.Equal("#FFBD3C50", view.StepParameterFrame.BorderBrush.ToString());
                Assert.True(view.StepParameterError.IsVisible);
                Assert.Equal(OpenVisionLanguageService.T("R24.InvalidNumber"), view.StepParameterError.Text);
                Capture(window, $"{width}x{height}-{language}-invalid");
                view.StepDraftTimeout.SetCurrentValue(TextBox.TextProperty, "not-a-number");
                view.StepDraftParameter.SetCurrentValue(TextBox.TextProperty, "3");
                Settle(window);
                Assert.False(view.ApplyStepDraftButton.IsEnabled);
                Assert.False(vm.SequenceEditor.IsStepParameterInvalid || view.StepParameterError.IsVisible);
                Assert.Equal("#FFFFFFFF", view.StepParameterFrame.Background.ToString());
                view.StepDraftTimeout.SetCurrentValue(TextBox.TextProperty, "0");
                Settle(window);
                Assert.True(view.ApplyStepDraftButton.IsEnabled);
                Assert.Equal("#FF087F73", applyBorder.Background.ToString());
                Assert.Equal("#FFFFFFFF", view.ApplyStepDraftButton.Foreground.ToString());
                Assert.True(vm.SequenceEditor.TryApplyStepDraft());
                Settle(window);
                Assert.Same(applied, vm.SequenceEditor.SelectedStep);
                Assert.Equal(new string('N', 80), applied.Name);
                Assert.Equal("3", applied.Parameter);
                Assert.True(vm.HasUnsavedChanges);
                Assert.False(vm.SequenceEditor.HasPendingStepDraft || vm.IsRunning);
                Assert.Equal(applied.Name, view.StepDraftName.Text);
                Assert.Equal(applied.TargetId, view.StepDraftTarget.SelectedValue);
                var title = (TextBlock)view.FindName("StepIdentityName");
                Assert.Equal(TextWrapping.Wrap, title.TextWrapping);
                Assert.Equal(TextTrimming.None, title.TextTrimming);
                Assert.True(Bounds(title, view).Right <= view.ActualWidth);
                view.StepDraftParameter.SetCurrentValue(TextBox.TextProperty, "4");
                Settle(window);
                vm.PlacementDraftPrompt = () => PlacementDraftDecision.Cancel;
                vm.SequenceEditor.SelectedStep = vm.SequenceEditor.Steps.Last();
                Settle(window);
                Assert.Same(applied, vm.SequenceEditor.SelectedStep);
                Assert.Equal("4", vm.SequenceEditor.SelectedStepDraft!.Parameter);
                vm.Navigation.IsInspectorOpen = false;
                Assert.True(vm.Navigation.IsInspectorOpen);
                Assert.True(vm.SequenceEditor.HasPendingStepDraft);
                vm.SequenceEditor.DiscardStepDraftCommand.Execute(null);
                Settle(window);
                Assert.Equal("3", vm.SequenceEditor.SelectedStepDraft!.Parameter);
                view.StepInspectorScrollViewer.ScrollToBottom();
                Settle(window);
                Assert.True(Bounds(view.StepInspectorFooter, view).Bottom <= view.ActualHeight);
                Capture(window, $"{width}x{height}-{language}-recovered-footer");
                vm.SequenceEditor.IsEditable = false;
                Settle(window);
                Assert.False(view.StepDraftFields.IsEnabled || view.ApplyStepDraftButton.IsEnabled || view.TestSelectedStepButton.IsEnabled);
                vm.SequenceEditor.IsEditable = true;
                vm.SequenceEditor.SelectedStep = null;
                Settle(window);
                Assert.False(view.StepDraftFields.IsEnabled || view.ApplyStepDraftButton.IsEnabled);
                vm.SequenceEditor.SelectedStep = applied;
                Settle(window);
                Assert.True(view.StepDraftFields.IsEnabled);
                Assert.False(vm.IsRunning);
                return true;
            }
            finally { window.DataContext = null; window.Close(); OpenVisionLanguageService.SetLanguage(previousLanguage, save: false); }
        });
    }

    [Theory]
    [InlineData(OpenVisionLanguage.Korean)]
    [InlineData(OpenVisionLanguage.English)]
    public async Task PendingStepDecisionsSaveAndReopenOnlyAppliedDefinition(OpenVisionLanguage language)
    {
        await _ui.InvokeTaskAsync(async () =>
        {
            var previousLanguage = OpenVisionLanguageService.CurrentLanguage;
            OpenVisionLanguageService.SetLanguage(language, save: false);
            var project = CreateProject();
            using var vm = new MainViewModel(project);
            vm.Navigation.OpenSequenceEditorCommand.Execute(null);
            string root = EvidenceRoot();
            Directory.CreateDirectory(root);
            string file = Path.Combine(root, $"step-{language}-{Guid.NewGuid():N}.ovmachine");
            try
            {
                await vm.SaveProjectAsync(file);
                byte[] saved = await File.ReadAllBytesAsync(file);
                var editor = vm.SequenceEditor;
                editor.SelectedStepDraft!.Parameter = "NaN";
                vm.PlacementDraftPrompt = () => PlacementDraftDecision.Apply;
                await Assert.ThrowsAsync<OperationCanceledException>(() => vm.SaveProjectAsync(file));
                Assert.Equal(saved, await File.ReadAllBytesAsync(file));
                Assert.False(vm.HasUnsavedChanges);
                vm.PlacementDraftPrompt = () => PlacementDraftDecision.Cancel;
                vm.Navigation.SelectedWorkspaceIndex = 0;
                Assert.True(vm.Navigation.IsSequenceWorkspace);
                Assert.Equal("NaN", editor.SelectedStepDraft.Parameter);
                editor.SelectedStepDraft.Parameter = "5";
                vm.PlacementDraftPrompt = () => PlacementDraftDecision.Apply;
                await vm.SaveProjectAsync(file);
                Assert.False(editor.HasPendingStepDraft || vm.HasUnsavedChanges || vm.IsRunning);
                var reopened = await new ProjectDocumentFileStore().LoadAsync(file);
                Assert.Equal("5", reopened.Sequences[0].Steps[0].Parameter);
                using var fresh = new MainViewModel(reopened);
                Assert.Equal("5", fresh.SequenceEditor.SelectedStepDraft!.Parameter);
                Assert.False(fresh.IsRunning || fresh.HasUnsavedChanges);
                editor.SelectedStepDraft.Parameter = "6";
                vm.PlacementDraftPrompt = () => PlacementDraftDecision.Discard;
                vm.Navigation.SelectedWorkspaceIndex = 0;
                Assert.True(vm.Navigation.IsEquipmentWorkspace);
                Assert.Equal("5", project.Sequences[0].Steps[0].Parameter);
                editor.SelectedStepDraft.Parameter = "7";
                editor.IsEditable = false;
                editor.ApplyStepDraftCommand.Execute(null);
                Assert.Equal("5", project.Sequences[0].Steps[0].Parameter);
                editor.IsEditable = true;
                editor.DiscardStepDraft();
                var oldDraft = editor.SelectedStepDraft;
                editor.Load(CreateProject());
                oldDraft!.Parameter = "9";
                Assert.Equal("2", editor.SelectedStep!.Parameter);
                Assert.False(editor.HasPendingStepDraft);
                return true;
            }
            finally { OpenVisionLanguageService.SetLanguage(previousLanguage, save: false); }
        });
    }

    [Theory]
    [InlineData(OpenVisionLanguage.Korean)]
    [InlineData(OpenVisionLanguage.English)]
    public async Task SelectedStepTestUsesIsolatedEngineAndCancelsStaleResult(OpenVisionLanguage language)
    {
        await _ui.InvokeTaskAsync(async () =>
        {
            var previousLanguage = OpenVisionLanguageService.CurrentLanguage;
            OpenVisionLanguageService.SetLanguage(language, save: false);
            var project = CreateProject();
            using var vm = new MainViewModel(project);
            try
            {
                var editor = vm.SequenceEditor;
                var payload = JsonSerializer.Serialize(project);
                var clock = vm.SimulationStatusText;
                await editor.TestSelectedStepAsync();
                Assert.NotNull(editor.StepTestResult);
                Assert.True(editor.StepTestResult.IsCompleted, editor.StepTestResult.Detail);
                Assert.Equal(payload, JsonSerializer.Serialize(project));
                Assert.Equal(clock, vm.SimulationStatusText);
                Assert.False(vm.IsRunning || vm.HasUnsavedChanges || editor.IsStepTestBusy);
                editor.SelectedStepDraft!.Parameter = "4";
                editor.PendingStepDraftPrompt = () => PlacementDraftDecision.Cancel;
                await editor.TestSelectedStepAsync();
                Assert.True(editor.HasPendingStepDraft);
                Assert.Null(editor.StepTestResult);
                Assert.Equal("2", editor.SelectedStep!.Parameter);
                editor.PendingStepDraftPrompt = () => PlacementDraftDecision.Apply;
                await editor.TestSelectedStepAsync();
                Assert.Equal("4", editor.SelectedStep!.Parameter);
                Assert.True(editor.StepTestResult!.IsCompleted);
                var appliedPayload = JsonSerializer.Serialize(project);
                var pending = editor.TestSelectedStepAsync();
                Assert.True(editor.IsStepTestBusy);
                await editor.TestSelectedStepAsync();
                editor.SelectedStep = editor.Steps.Last();
                await pending;
                Assert.Null(editor.StepTestResult);
                Assert.False(editor.IsStepTestBusy || editor.IsStepTestSupported || editor.TestSelectedStepCommand.CanExecute(null));
                Assert.Equal(appliedPayload, JsonSerializer.Serialize(project));
                Assert.False(vm.IsRunning);
                editor.SelectedStep = editor.Steps.First();
                var disposedRun = editor.TestSelectedStepAsync();
                editor.Dispose();
                await disposedRun;
                Assert.Null(editor.StepTestResult);
                Assert.False(editor.TestSelectedStepCommand.CanExecute(null));
                return true;
            }
            finally { OpenVisionLanguageService.SetLanguage(previousLanguage, save: false); }
        });
    }

    [Theory]
    [InlineData(OpenVisionLanguage.Korean)]
    [InlineData(OpenVisionLanguage.English)]
    public async Task ActionChangeKeepsNormalizedInputsAndDraftOnlyUntilApply(OpenVisionLanguage language)
    {
        await _ui.InvokeAsync(() =>
        {
            var previousLanguage = OpenVisionLanguageService.CurrentLanguage;
            OpenVisionLanguageService.SetLanguage(language, save: false);
            var project = CreateProject();
            using var vm = new MainViewModel(project);
            vm.Navigation.HideStartupChoice();
            var window = new ShellWindow { DataContext = vm, Width = 1280, Height = 760, ShowInTaskbar = false, ShowActivated = false };
            window.Show();
            try
            {
                vm.Navigation.OpenSequenceEditorCommand.Execute(null);
                Settle(window);
                var view = Descendants(window).OfType<SequenceStepInspectorView>().Single(control => control.IsVisible);
                var editor = vm.SequenceEditor;
                string original = JsonSerializer.Serialize(project);
                view.StepDraftTimeout.SetCurrentValue(TextBox.TextProperty, "500");
                view.StepDraftAction.SetCurrentValue(System.Windows.Controls.Primitives.Selector.SelectedItemProperty, SequenceStepAction.Delay);
                view.StepDraftParameter.SetCurrentValue(TextBox.TextProperty, "15");
                Settle(window);
                Capture(window, $"action-{language}-normalized-delay");
                Assert.Equal(0, editor.SelectedStepDraft!.TimeoutMs);
                Assert.Equal("0", view.StepDraftTimeout.Text);
                Assert.Equal(original, JsonSerializer.Serialize(project));
                Assert.True(editor.TryApplyStepDraft());
                Assert.Equal(SequenceStepAction.Delay, editor.SelectedStep!.Action);
                Assert.Equal("Move axis", editor.SelectedStep.Name);
                Assert.Equal("Move axis", view.StepDraftName.Text);
                Assert.False(view.TestSelectedStepButton.IsEnabled);
                Assert.Equal(OpenVisionLanguageService.T("Sequence.StepTestUnsupported"), view.StepTestStatus.Text);
                view.StepDraftAction.SetCurrentValue(System.Windows.Controls.Primitives.Selector.SelectedItemProperty, SequenceStepAction.MoveAxis);
                view.StepDraftParameter.SetCurrentValue(TextBox.TextProperty, "3");
                Settle(window);
                Assert.Equal("axis", view.StepDraftTarget.SelectedValue);
                Assert.Equal("0", view.StepDraftTimeout.Text);
                vm.PlacementDraftPrompt = () => PlacementDraftDecision.Cancel;
                vm.Navigation.SelectedExecutionTabIndex = 0;
                Assert.True(vm.Navigation.IsSequenceWorkspace);
                Assert.Equal(SequenceStepAction.Delay, editor.SelectedStep.Action);
                Assert.True(editor.TryApplyStepDraft());
                Settle(window);
                Assert.Equal(SequenceStepAction.MoveAxis, editor.SelectedStep.Action);
                Assert.Equal("Move axis", editor.SelectedStep.Name);
                Assert.Equal("Move axis", view.StepDraftName.Text);
                Assert.True(view.TestSelectedStepButton.IsEnabled);
                Assert.False(editor.HasPendingStepDraft || vm.IsRunning);
                Capture(window, $"action-{language}-recovered-move");
                return true;
            }
            finally
            {
                window.DataContext = null;
                window.Close();
                OpenVisionLanguageService.SetLanguage(previousLanguage, save: false);
            }
        });
    }


    [Theory]
    [InlineData(1280, 760, OpenVisionLanguage.Korean)]
    [InlineData(1280, 760, OpenVisionLanguage.English)]
    [InlineData(1920, 1040, OpenVisionLanguage.Korean)]
    [InlineData(1920, 1040, OpenVisionLanguage.English)]
    public async Task SequenceUnitSettingsMoveToInspectorAndKeepTheirExistingWriters(double width, double height, OpenVisionLanguage language)
    {
        await _ui.InvokeAsync(() =>
        {
            var previousLanguage = OpenVisionLanguageService.CurrentLanguage;
            OpenVisionLanguageService.SetLanguage(language, save: false);
            var project = CreateProject();
            project.Stations = [new MachineStationDefinition { Id = "station", Name = "Station", Units = [new MachineUnitDefinition { Id = "a", Name = new string('A', 80) }, new MachineUnitDefinition { Id = "b", Name = new string('B', 80) }, new MachineUnitDefinition { Id = "c", Name = new string('C', 80) }] }];
            project.Sequences[0].UnitId = "a";
            project.Sequences.Add(new SequenceDefinition { Id = "other", Name = new string('S', 80), UnitId = "b", Steps = [new SequenceStepDefinition { Id = "complete", Name = "Complete", Action = SequenceStepAction.Complete }] });
            using var vm = new MainViewModel(project);
            vm.Navigation.HideStartupChoice();
            var window = new ShellWindow { DataContext = vm, Width = width, Height = height, ShowInTaskbar = false, ShowActivated = false, UseLayoutRounding = true };
            window.Show();
            try
            {
                vm.Navigation.OpenSequenceEditorCommand.Execute(null);
                Settle(window);
                var view = Descendants(window).OfType<SequenceStepInspectorView>().Single(control => control.IsVisible);
                var center = Descendants(window).OfType<SequenceEditorView>().Single(control => control.IsVisible);
                var centerOwners = Descendants(center).OfType<ComboBox>().Where(control => control.IsVisible && control.GetBindingExpression(System.Windows.Controls.Primitives.Selector.SelectedValueProperty)?.ParentBinding.Path.Path == "SelectedUnitId").ToArray();
                Capture(window, $"unit-{width}-{language}-entry");
                Directory.CreateDirectory(EvidenceRoot());
                File.WriteAllText(Path.Combine(EvidenceRoot(), $"unit-{width}-{language}-geometry.json"), JsonSerializer.Serialize(new { center = Bounds(center, window).ToString(), tabs = Bounds(center.StepViewTabs, center).ToString(), centerOwnerCount = centerOwners.Length }));
                Assert.Empty(centerOwners);
                var settings = Assert.IsType<Expander>(view.FindName("SequenceUnitSettings"));
                settings.SetCurrentValue(Expander.IsExpandedProperty, true);
                Settle(window);
                var owner = Assert.IsType<ComboBox>(view.FindName("SequenceOwnerUnit"));
                var repeat = Assert.IsType<TextBox>(view.FindName("InspectorUnitRepeatCount"));
                var apply = Assert.IsType<Button>(view.FindName("InspectorApplyUnitRepeat"));
                Assert.Same(vm.SequenceEditor, view.DataContext);
                Assert.Equal("a", owner.SelectedValue);
                Assert.Equal("1", repeat.Text);
                Assert.Contains(vm.SequenceEditor.AvailableUnits, unit => unit.Id == "" && unit.Name == (language == OpenVisionLanguage.Korean ? "공용 시퀀스" : "Common sequence"));
                var selectedText = Descendants(owner).OfType<TextBlock>().Single(textBlock => textBlock.Text == project.Stations[0].Units[0].Name);
                Assert.Equal(TextWrapping.Wrap, selectedText.TextWrapping);
                Assert.True(Bounds(selectedText, owner).Right <= owner.ActualWidth + 1);
                Assert.True(selectedText.ActualHeight > selectedText.FontSize * 2);
                owner.IsDropDownOpen = true;
                Settle(window);
                var popup = Descendants(owner).OfType<System.Windows.Controls.Primitives.Popup>().Single();
                var choices = Descendants(popup.Child).OfType<TextBlock>().Where(textBlock => textBlock.Text.Length == 80).ToArray();
                Assert.Equal(3, choices.Length);
                Assert.All(choices, textBlock =>
                {
                    Assert.Equal(TextWrapping.Wrap, textBlock.TextWrapping);
                    Assert.True(Bounds(textBlock, (Visual)popup.Child).Right <= ((FrameworkElement)popup.Child).ActualWidth + 1);
                });
                owner.IsDropDownOpen = false;
                Settle(window);
                Capture(window, $"unit-{width}-{language}-expanded");
                string original = JsonSerializer.Serialize(project);
                foreach (string invalid in new[] { "0", "101", "NaN" })
                {
                    repeat.SetCurrentValue(TextBox.TextProperty, invalid);
                    Settle(window);
                    apply.Command.Execute(null);
                    Settle(window);
                    Assert.Equal(original, JsonSerializer.Serialize(project));
                    Assert.Contains("1–100", vm.SequenceEditor.StructuralEditStatus);
                    if (language == OpenVisionLanguage.English) Assert.Equal("Unit repeat count must be an integer from 1–100.", vm.SequenceEditor.StructuralEditStatus);
                }
                Capture(window, $"unit-{width}-{language}-invalid");
                repeat.SetCurrentValue(TextBox.TextProperty, "3");
                Settle(window);
                apply.Command.Execute(null);
                Settle(window);
                Assert.Equal(3, project.Stations[0].Units[0].RepeatCount);
                Assert.Equal(1, project.Stations[0].Units[1].RepeatCount);
                Assert.True(vm.HasUnsavedChanges);
                Assert.False(vm.IsRunning);
                owner.SetCurrentValue(System.Windows.Controls.Primitives.Selector.SelectedValueProperty, "b");
                Settle(window);
                Assert.Equal("a", vm.SequenceEditor.SelectedUnitId);
                Assert.Equal("a", owner.SelectedValue);
                Assert.Equal(language == OpenVisionLanguage.Korean ? "이 유닛에는 이미 시퀀스가 있습니다." : "This unit already has a sequence.", vm.SequenceEditor.StructuralEditStatus);
                view.StepDraftName.SetCurrentValue(TextBox.TextProperty, "Pending step name");
                Settle(window);
                vm.PlacementDraftPrompt = () => PlacementDraftDecision.Cancel;
                owner.SetCurrentValue(System.Windows.Controls.Primitives.Selector.SelectedValueProperty, "");
                Settle(window);
                Assert.Equal("a", vm.SequenceEditor.SelectedUnitId);
                Assert.True(vm.SequenceEditor.HasPendingStepDraft);
                Assert.Equal("Pending step name", view.StepDraftName.Text);
                vm.SequenceEditor.DiscardStepDraftCommand.Execute(null);
                owner.SetCurrentValue(System.Windows.Controls.Primitives.Selector.SelectedValueProperty, "");
                Settle(window);
                Assert.Null(project.Sequences[0].UnitId);
                Assert.False(repeat.IsVisible);
                var add = Assert.IsType<Button>(view.FindName("InspectorAddUnitSequence"));
                Assert.True(add.IsEnabled);
                add.Command.Execute(null);
                Settle(window);
                Assert.Equal("a", vm.SequenceEditor.SelectedUnitId);
                Assert.Equal(3, project.Sequences.Count);
                Assert.Equal("3", repeat.Text);
                Assert.Equal(language == OpenVisionLanguage.Korean ? project.Stations[0].Units[0].Name + " 시퀀스" : project.Stations[0].Units[0].Name + " sequence", vm.SequenceEditor.SelectedSequence!.Name);
                vm.SequenceEditor.IsEditable = false;
                Settle(window);
                Assert.False(owner.IsEnabled || repeat.IsEnabled || apply.IsEnabled || add.IsEnabled);
                vm.SequenceEditor.IsEditable = true;
                Settle(window);
                Assert.True(owner.IsEnabled && repeat.IsEnabled);
                string file = Path.Combine(EvidenceRoot(), $"unit-{width}-{language}-{Guid.NewGuid():N}.ovmachine");
                var store = new ProjectDocumentFileStore();
                store.SaveAsync(project, file).GetAwaiter().GetResult();
                byte[] bytes = File.ReadAllBytes(file);
                var reopened = store.LoadAsync(file).GetAwaiter().GetResult();
                using var fresh = new MainViewModel(reopened);
                fresh.SequenceEditor.SelectSequence(vm.SequenceEditor.SelectedSequence.Id);
                Assert.Equal("a", fresh.SequenceEditor.SelectedUnitId);
                Assert.Equal("3", fresh.SequenceEditor.UnitRepeatCount);
                Assert.Equal(new string('A', 80), reopened.Stations[0].Units[0].Name);
                Assert.Equal(new string('S', 80), reopened.Sequences.Single(sequence => sequence.Id == "other").Name);
                Assert.False(fresh.IsRunning || fresh.HasUnsavedChanges);
                Assert.Equal(bytes, File.ReadAllBytes(file));
                var nextLanguage = language == OpenVisionLanguage.Korean ? OpenVisionLanguage.English : OpenVisionLanguage.Korean;
                string payload = JsonSerializer.Serialize(project);
                OpenVisionLanguageService.SetLanguage(nextLanguage, save: false);
                Settle(window);
                Assert.Contains(vm.SequenceEditor.AvailableUnits, unit => unit.Id == "" && unit.Name == (nextLanguage == OpenVisionLanguage.Korean ? "공용 시퀀스" : "Common sequence"));
                Assert.Equal(payload, JsonSerializer.Serialize(project));
                Assert.Equal("a", owner.SelectedValue);
                settings.IsExpanded = false;
                Settle(window);
                Assert.True(Bounds(view.StepInspectorFooter, view).Bottom <= view.ActualHeight);
                Capture(window, $"unit-{width}-{language}-recovered");
                return true;
            }
            finally { window.DataContext = null; window.Close(); OpenVisionLanguageService.SetLanguage(previousLanguage, save: false); }
        });
    }


    [Theory]
    [InlineData(1280, 900, OpenVisionLanguage.Korean, false)]
    [InlineData(1280, 900, OpenVisionLanguage.English, false)]
    [InlineData(1920, 1032, OpenVisionLanguage.Korean, false)]
    [InlineData(1920, 1032, OpenVisionLanguage.English, false)]
    [InlineData(900, 700, OpenVisionLanguage.Korean, true)]
    [InlineData(900, 700, OpenVisionLanguage.English, true)]
    public async Task LiveActionAndTemplateLanguagePreservesSelectionAndDraft(double width, double height, OpenVisionLanguage language, bool standalone)
    {
        await _ui.InvokeAsync(() =>
        {
            var previousLanguage = OpenVisionLanguageService.CurrentLanguage;
            OpenVisionLanguageService.SetLanguage(language, save: false);
            var project = CreateProject();
            using var vm = new MainViewModel(project);
            vm.PlacementDraftPrompt = () => PlacementDraftDecision.Cancel;
            vm.Navigation.HideStartupChoice();
            vm.Navigation.OpenSequenceEditorCommand.Execute(null);
            var editor = vm.SequenceEditor;
            editor.SelectedTemplate = editor.Templates.Single(template => template.Id == "move-axis-home");
            var standaloneView = standalone ? new SequenceEditorView { DataContext = editor, StepViewIndex = 1 } : null;
            Window window = standalone
                ? new Window { Content = standaloneView, Width = width, Height = height, ShowInTaskbar = false, ShowActivated = false }
                : new ShellWindow { DataContext = vm, Width = width, Height = height, ShowInTaskbar = false, ShowActivated = false };
            SmokeDpiTestHook.PlaceOnTestMonitor(window, (int)width, (int)height);
            window.Show();
            try
            {
                Settle(window);
                var monitor = SmokeDpiTestHook.CaptureMonitorEvidence(window);
                Assert.True(monitor.WindowIntersectsMonitor);
                string? evidence = Environment.GetEnvironmentVariable("OPENVISIONLAB_MACHINE_STUDIO_CENTRAL_LAYOUT_EVIDENCE");
                if (evidence is not null)
                {
                    Assert.StartsWith("D:\\", Path.GetFullPath(evidence), StringComparison.OrdinalIgnoreCase);
                    Directory.CreateDirectory(evidence);
                    File.WriteAllText(Path.Combine(evidence, $"template-consumer-{width}-{height}-{language}-monitor.json"), JsonSerializer.Serialize(monitor));
                }
                var list = standaloneView ?? Descendants(window).OfType<SequenceEditorView>().Single(view => view.IsVisible);
                var action = standalone
                    ? Descendants(list).OfType<ComboBox>().Single(combo => ReferenceEquals(combo.ItemsSource, editor.SelectedStep!.AvailableActions))
                    : Descendants(window).OfType<SequenceStepInspectorView>().Single(view => view.IsVisible).StepDraftAction;
                var template = Descendants(list).OfType<ComboBox>().Single(combo => ReferenceEquals(combo.ItemsSource, editor.Templates));
                var selectedStep = editor.SelectedStep!;
                var selectedDraft = editor.SelectedStepDraft!;
                var selectedTemplate = editor.SelectedTemplate!;
                string payload = JsonSerializer.Serialize(project);
                string clock = vm.SimulationStatusText;
                if (!standalone) selectedDraft.Name = new string('D', 80);
                string pendingDraft = selectedDraft.DefinitionSnapshot;
                var failures = new List<string>();
                foreach (var current in new[] { language == OpenVisionLanguage.Korean ? OpenVisionLanguage.English : OpenVisionLanguage.Korean, language })
                {
                    OpenVisionLanguageService.SetLanguage(current, save: false);
                    Settle(window);
                    string actionText = current == OpenVisionLanguage.Korean ? "축 이동 명령" : "Move axis";
                    string templateText = current == OpenVisionLanguage.Korean ? "축 이동 명령" : "Move axis to home";
                    void CheckChoice(ComboBox combo, object item, string expected, string location)
                    {
                        var rendered = Descendants(combo).OfType<TextBlock>().FirstOrDefault(text => Equals(text.DataContext, item));
                        if (rendered?.Text != expected) failures.Add(location + ": expected " + expected + ", actual " + rendered?.Text);
                        combo.IsDropDownOpen = true;
                        Settle(window);
                        var container = combo.ItemContainerGenerator.ContainerFromItem(item) as ComboBoxItem;
                        var popupText = container is null ? null : Descendants(container).OfType<TextBlock>().FirstOrDefault(text => Equals(text.DataContext, item));
                        if (popupText?.Text != expected) failures.Add(location + " popup: expected " + expected + ", actual " + popupText?.Text);
                        combo.IsDropDownOpen = false;
                        Settle(window);
                    }
                    CheckChoice(action, SequenceStepAction.MoveAxis, actionText, "action " + current);
                    if (standalone) CheckChoice(template, selectedTemplate, templateText, "template " + current);
                    else
                    {
                        Assert.False(template.IsVisible);
                        Exception? popupError = null;
                        bool popupChecked = false;
                        window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
                        {
                            var dialog = Application.Current.Windows.OfType<SequenceStepTemplateDialog>().Single(candidate => candidate.IsVisible);
                            try
                            {
                                foreach (var popupLanguage in new[] { current == OpenVisionLanguage.Korean ? OpenVisionLanguage.English : OpenVisionLanguage.Korean, current })
                                {
                                    OpenVisionLanguageService.SetLanguage(popupLanguage, save: false);
                                    Settle(dialog);
                                    var choice = Descendants(dialog).OfType<Button>().Single(button => ReferenceEquals(button.CommandParameter, selectedTemplate));
                                    Assert.Same(editor.AddStepCommand, choice.Command);
                                    string expected = popupLanguage == OpenVisionLanguage.Korean ? "축 이동 명령" : "Move axis to home";
                                    var caption = Descendants(choice).OfType<TextBlock>().First(text => text.IsVisible);
                                    if (caption.Text != expected) failures.Add("actual template dialog " + popupLanguage + ": expected " + expected + ", actual " + caption.Text);
                                    Capture(dialog, $"locale-template-{width}-{language}-{current}-{popupLanguage}");
                                }
                                popupChecked = true;
                            }
                            catch (Exception exception) { popupError = exception; }
                            finally { Descendants(dialog).OfType<Button>().Single(button => button.IsCancel).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); }
                        }));
                        list.DockedAddStepButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        Assert.Null(popupError);
                        Assert.True(popupChecked);
                        Settle(window);
                    }
                    if (standalone)
                    {
                        var completion = Descendants(list).OfType<CheckBox>().Single(check => check.GetBindingExpression(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty)?.ParentBinding.Path.Path == "SelectedStep.WaitForAxisCompletion");
                        Assert.Equal(current == OpenVisionLanguage.Korean ? "축 이동 완료 후 다음 스텝" : "Continue after axis motion completes", completion.Content);
                        Assert.False(completion.IsChecked ?? true);
                    }
                    if (!Descendants(list.StepListDataGrid).OfType<TextBlock>().Any(text => text.DataContext is SequenceStepEditorItem row && row.Id == selectedStep.Id && text.Text == actionText))
                        failures.Add("table action " + current + ": current caption missing");
                    if (standalone) Assert.Equal(selectedStep.Id, editor.SelectedStep!.Id);
                    else Assert.Same(selectedStep, editor.SelectedStep);
                    if (!standalone) Assert.Same(selectedDraft, editor.SelectedStepDraft);
                    Assert.Same(selectedTemplate, editor.SelectedTemplate);
                    Assert.Equal(SequenceStepAction.MoveAxis, action.SelectedItem);
                    Assert.Equal(payload, JsonSerializer.Serialize(project));
                    Assert.Equal(pendingDraft, editor.SelectedStepDraft!.DefinitionSnapshot);
                    Assert.Equal(!standalone, editor.HasPendingStepDraft);
                    Assert.False(vm.HasUnsavedChanges || vm.IsRunning);
                    Capture(window, $"locale-{width}-{language}-{standalone}-{current}");
                }
                if (!standalone)
                {
                    Assert.True(editor.TryApplyStepDraft());
                    Assert.Equal(new string('D', 80), selectedStep.Name);
                    Assert.Equal(SequenceStepAction.MoveAxis, selectedStep.Action);
                    Assert.True(vm.HasUnsavedChanges);
                    Assert.False(editor.HasPendingStepDraft);
                }
                Assert.Equal(clock, vm.SimulationStatusText);
                Assert.False(vm.IsRunning);
                File.WriteAllText(Path.Combine(EvidenceRoot(), $"locale-{width}-{language}-{standalone}.json"), JsonSerializer.Serialize(new { failures, selectedStep.Id, selectedStep.Action, templateId = selectedTemplate.Id, standalone }));
                Assert.Empty(failures);
                return true;
            }
            finally { editor.DiscardStepDraftCommand.Execute(null); window.DataContext = null; window.Close(); OpenVisionLanguageService.SetLanguage(previousLanguage, save: false); }
        });
    }


    [Theory]
    [InlineData(OpenVisionLanguage.Korean)]
    [InlineData(OpenVisionLanguage.English)]
    public async Task LiveGraphActionLanguageKeepsPendingDraftAndCompleteIdentity(OpenVisionLanguage language)
    {
        await _ui.InvokeAsync(() =>
        {
            var previousLanguage = OpenVisionLanguageService.CurrentLanguage;
            OpenVisionLanguageService.SetLanguage(language, save: false);
            var project = CreateProject();
            using var vm = new MainViewModel(project);
            vm.PlacementDraftPrompt = () => PlacementDraftDecision.Cancel;
            vm.Navigation.HideStartupChoice();
            vm.Navigation.OpenSequenceEditorCommand.Execute(null);
            var window = new ShellWindow { DataContext = vm, Width = 1280, Height = 760, ShowInTaskbar = false, ShowActivated = false };
            window.Show();
            try
            {
                Settle(window);
                var list = Descendants(window).OfType<SequenceEditorView>().Single(view => view.IsVisible);
                list.StepViewIndex = 0;
                var editor = vm.SequenceEditor;
                editor.SelectedStepDraft!.Name = "Pending graph name";
                string payload = JsonSerializer.Serialize(project);
                string draft = editor.SelectedStepDraft.DefinitionSnapshot;
                var other = language == OpenVisionLanguage.Korean ? OpenVisionLanguage.English : OpenVisionLanguage.Korean;
                foreach (var current in new[] { other, language })
                {
                    OpenVisionLanguageService.SetLanguage(current, save: false);
                    Settle(window);
                    string move = current == OpenVisionLanguage.Korean ? "축 이동 명령" : "Move axis";
                    string complete = current == OpenVisionLanguage.Korean ? "완료" : "Complete";
                    var visibleText = Descendants(list).OfType<TextBlock>().Where(text => text.IsVisible).ToArray();
                    Assert.Contains(visibleText, text => text.DataContext is SequenceStepEditorItem step && step.Id == "move" && text.Text == move);
                    Assert.Contains(visibleText, text => text.DataContext is SequenceStepEditorItem step && step.Id == "complete" && text.Text == complete);
                    Assert.Equal("move", editor.SelectedStep!.Id);
                    Assert.Equal(draft, editor.SelectedStepDraft!.DefinitionSnapshot);
                    Assert.Equal(payload, JsonSerializer.Serialize(project));
                    Assert.True(editor.HasPendingStepDraft);
                    Assert.False(vm.IsRunning || vm.HasUnsavedChanges);
                    Capture(window, $"graph-locale-{language}-{current}");
                }
                return true;
            }
            finally { vm.SequenceEditor.DiscardStepDraftCommand.Execute(null); window.DataContext = null; window.Close(); OpenVisionLanguageService.SetLanguage(previousLanguage, save: false); }
        });
    }

    [Theory]
    [InlineData(1280, OpenVisionLanguage.Korean)]
    [InlineData(1280, OpenVisionLanguage.English)]
    [InlineData(1920, OpenVisionLanguage.Korean)]
    [InlineData(1920, OpenVisionLanguage.English)]
    public async Task ActualAxisUnitRefreshesRenderedCaptionWithoutApplyingDraft(double width, OpenVisionLanguage language)
    {
        await _ui.InvokeAsync(() =>
        {
            var previousLanguage = OpenVisionLanguageService.CurrentLanguage;
            OpenVisionLanguageService.SetLanguage(language, save: false);
            var project = CreateProject();
            project.Axes.Add(new VirtualAxisDefinition { Id = "rotary", Name = "Rotary", Kind = AxisKind.Rotary, Unit = "deg", HomePosition = 0, SoftLimitMin = -180, SoftLimitMax = 180 });
            project.Axes.Add(new VirtualAxisDefinition { Id = "blank-unit", Name = "Axis without unit", Unit = "", HomePosition = 0, SoftLimitMin = -10, SoftLimitMax = 10 });
            using var vm = new MainViewModel(project);
            vm.Navigation.HideStartupChoice();
            var window = new ShellWindow { DataContext = vm, Width = width, Height = 900, ShowInTaskbar = false, ShowActivated = false };
            SmokeDpiTestHook.PlaceOnTestMonitor(window, (int)width, 900);
            window.Show();
            try
            {
                vm.PlacementDraftPrompt = () => PlacementDraftDecision.Cancel;
                vm.Navigation.OpenSequenceEditorCommand.Execute(null);
                Settle(window);
                var view = Descendants(window).OfType<SequenceStepInspectorView>().Single(control => control.IsVisible);
                var monitor = SmokeDpiTestHook.CaptureMonitorEvidence(window);
                Assert.True(monitor.WindowIntersectsMonitor);
                Assert.Equal(96, VisualTreeHelper.GetDpi(window).PixelsPerInchX);
                var editor = vm.SequenceEditor;
                string original = JsonSerializer.Serialize(project);
                AssertCaption(language == OpenVisionLanguage.Korean ? "목표 위치 (mm)" : "Target position (mm)");
                view.StepDraftTarget.SetCurrentValue(System.Windows.Controls.Primitives.Selector.SelectedValueProperty, "rotary");
                Assert.Equal("rotary", editor.SelectedStepDraft!.TargetId);
                Assert.Contains(Descendants(view.StepDraftTarget).OfType<TextBlock>(), text => text.Text == "Rotary (rotary)");
                AssertCaption(language == OpenVisionLanguage.Korean ? "목표 위치 (deg)" : "Target position (deg)");
                Assert.Equal(original, JsonSerializer.Serialize(project));
                Assert.True(editor.HasPendingStepDraft);
                var other = language == OpenVisionLanguage.Korean ? OpenVisionLanguage.English : OpenVisionLanguage.Korean;
                OpenVisionLanguageService.SetLanguage(other, save: false);
                AssertCaption(other == OpenVisionLanguage.Korean ? "목표 위치 (deg)" : "Target position (deg)");
                OpenVisionLanguageService.SetLanguage(language, save: false);
                editor.DiscardStepDraft();
                AssertCaption(language == OpenVisionLanguage.Korean ? "목표 위치 (mm)" : "Target position (mm)");
                Assert.False(editor.HasPendingStepDraft || vm.HasUnsavedChanges || vm.IsRunning);
                view.StepDraftTarget.SetCurrentValue(System.Windows.Controls.Primitives.Selector.SelectedValueProperty, "blank-unit");
                AssertCaption(language == OpenVisionLanguage.Korean ? "목표 위치" : "Target position");
                editor.SelectedStepDraft!.TargetId = "missing-axis";
                AssertCaption(language == OpenVisionLanguage.Korean ? "목표 위치" : "Target position");
                Assert.Equal(original, JsonSerializer.Serialize(project));
                editor.DiscardStepDraft();
                view.StepDraftAction.SetCurrentValue(System.Windows.Controls.Primitives.Selector.SelectedItemProperty, SequenceStepAction.Delay);
                AssertCaption(language == OpenVisionLanguage.Korean ? "딜레이 (s)" : "Delay (s)");
                view.StepDraftAction.SetCurrentValue(System.Windows.Controls.Primitives.Selector.SelectedItemProperty, SequenceStepAction.TransferUnitWorkpiece);
                AssertCaption(language == OpenVisionLanguage.Korean ? "이송 시간 (s)" : "Transfer time (s)", language == OpenVisionLanguage.Korean ? "이송 시간" : "Transfer time");
                Assert.Equal(original, JsonSerializer.Serialize(project));
                editor.DiscardStepDraft();
                editor.IsEditable = false;
                AssertCaption(language == OpenVisionLanguage.Korean ? "목표 위치 (mm)" : "Target position (mm)");
                Assert.False(view.StepDraftParameter.IsEnabled);
                editor.IsEditable = true;
                editor.SelectedStep = null;
                AssertCaption(language == OpenVisionLanguage.Korean ? "값" : "Value");
                editor.SelectedStep = editor.Steps.First();
                AssertCaption(language == OpenVisionLanguage.Korean ? "목표 위치 (mm)" : "Target position (mm)");
                Assert.Equal(original, JsonSerializer.Serialize(project));
                Directory.CreateDirectory(EvidenceRoot());
                File.WriteAllText(Path.Combine(EvidenceRoot(), $"caption-{width}-{language}-recovery-state.json"), JsonSerializer.Serialize(new { monitor, editor.HasPendingStepDraft, vm.HasUnsavedChanges, vm.IsRunning, selected = editor.SelectedStep!.DefinitionSnapshot, draft = editor.SelectedStepDraft!.DefinitionSnapshot, editor.StepTimeoutText, editor.StepEjectionDelayText }));
                Assert.False(editor.HasPendingStepDraft || vm.HasUnsavedChanges || vm.IsRunning);
                Capture(window, $"caption-{width}-{language}-recovered");
                return true;

                void AssertCaption(string expected, string? accessibleName = null)
                {
                    Settle(window);
                    Assert.Equal(accessibleName ?? expected, System.Windows.Automation.AutomationProperties.GetName(view.StepParameterCaption));
                    if (view.StepParameterCaption.IsVisible)
                    {
                        var caption = view.StepParameterCaption;
                        var rendered = new RenderTargetBitmap((int)Math.Ceiling(caption.ActualWidth), (int)Math.Ceiling(caption.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                        rendered.Render(caption);
                        Assert.Equal(expected, CaptionCharacters(VisualTreeHelper.GetDrawing(caption)!));
                    }
                    Assert.Equal(accessibleName ?? expected, System.Windows.Automation.AutomationProperties.GetName(view.StepDraftParameter));
                    Assert.True(Bounds(view.StepParameterCaption, view).Right <= view.ActualWidth);
                }
                static string CaptionCharacters(Drawing drawing)
                {
                    if (drawing is DrawingGroup group) return string.Concat(group.Children.Select(CaptionCharacters));
                    if (drawing is not GlyphRunDrawing glyph) return string.Empty;
                    var run = glyph.GlyphRun;
                    // Empty inline runs produce zero-advance formatting markers, not visible caption characters.
                    return new string(run.Characters!.Where((character, index) => character != '​' || run.AdvanceWidths[run.ClusterMap?[index] ?? index] != 0).ToArray());
                }
            }
            finally { vm.SequenceEditor.IsEditable = true; vm.SequenceEditor.DiscardStepDraft(); window.DataContext = null; window.Close(); OpenVisionLanguageService.SetLanguage(previousLanguage, save: false); }
        });
    }

    [Theory]
    [InlineData(1280, 900, OpenVisionLanguage.Korean)]
    [InlineData(1280, 900, OpenVisionLanguage.English)]
    [InlineData(1920, 1032, OpenVisionLanguage.Korean)]
    [InlineData(1920, 1032, OpenVisionLanguage.English)]
    public async Task StepSelectionHighlightsActualBoundTargetAndPreservesExplicitDraftOwners(double width, double height, OpenVisionLanguage language)
    {
        await _ui.InvokeAsync(() =>
        {
            var previousLanguage = OpenVisionLanguageService.CurrentLanguage;
            OpenVisionLanguageService.SetLanguage(language, save: false);
            var project = CreateProject();
            project.Axes.Add(new VirtualAxisDefinition { Id = "axis.two", Name = "Same display name", Kind = AxisKind.Rotary, Unit = "deg", SoftLimitMin = -10, SoftLimitMax = 10 });
            project.Axes.Add(new VirtualAxisDefinition { Id = "axis.missing", Name = "Declared axis without a scene part", SoftLimitMin = -10, SoftLimitMax = 10 });
            project.Axes.Add(new VirtualAxisDefinition { Id = "axis.ambiguous", Name = "Ambiguous", SoftLimitMin = -10, SoftLimitMax = 10 });
            project.Devices.Add(new OpenVisionLab.Machine.Core.Devices.DeviceDefinition { Id = "camera.actual", Name = "Actual camera", Kind = OpenVisionLab.Machine.Core.Devices.DeviceKind.Camera });
            project.Layouts[0].Components.AddRange([
                new LayoutComponentDefinition { Id = "scene.axis.one", Name = "Same display name", Kind = LayoutComponentKind.LinearStage, BehaviorBindingId = "axis" },
                new LayoutComponentDefinition { Id = "scene.axis.two", Name = "Same display name", Kind = LayoutComponentKind.RotaryStage, BehaviorBindingId = "axis.two" },
                new LayoutComponentDefinition { Id = "scene.camera", Name = "Actual camera", Kind = LayoutComponentKind.Camera, BehaviorBindingId = "camera.actual" },
                new LayoutComponentDefinition { Id = "ambiguous.one", Name = "Ambiguous", Kind = LayoutComponentKind.LinearStage, BehaviorBindingId = "axis.ambiguous" },
                new LayoutComponentDefinition { Id = "ambiguous.two", Name = "Ambiguous", Kind = LayoutComponentKind.LinearStage, BehaviorBindingId = "axis.ambiguous" }
            ]);
            project.Channels.AddRange([
                new OpenVisionLab.Machine.Core.Channels.ChannelDefinition { Id = "signal.in", Name = "Sensor signal", Kind = OpenVisionLab.Machine.Core.Channels.ChannelKind.DigitalInput },
                new OpenVisionLab.Machine.Core.Channels.ChannelDefinition { Id = "signal.out", Name = "Conveyor signal", Kind = OpenVisionLab.Machine.Core.Channels.ChannelKind.DigitalOutput },
                new OpenVisionLab.Machine.Core.Channels.ChannelDefinition { Id = "signal.reverse", Kind = OpenVisionLab.Machine.Core.Channels.ChannelKind.DigitalOutput },
                new OpenVisionLab.Machine.Core.Channels.ChannelDefinition { Id = "signal.no.owner", Kind = OpenVisionLab.Machine.Core.Channels.ChannelKind.DigitalInput },
                new OpenVisionLab.Machine.Core.Channels.ChannelDefinition { Id = "signal.no.part", Name = "Unplaced sensor", Kind = OpenVisionLab.Machine.Core.Channels.ChannelKind.DigitalInput }
            ]);
            project.Devices.AddRange([
                new OpenVisionLab.Machine.Core.Devices.DeviceDefinition { Id = "sensor.owner", Name = "Actual sensor", Kind = OpenVisionLab.Machine.Core.Devices.DeviceKind.Sensor, ChannelIds = ["signal.in"], Sensor = new() { OutputChannelId = "signal.in", TargetComponentId = "frame" } },
                new OpenVisionLab.Machine.Core.Devices.DeviceDefinition { Id = "conveyor.owner", Name = "Actual conveyor", Kind = OpenVisionLab.Machine.Core.Devices.DeviceKind.Conveyor, ChannelIds = ["signal.out", "signal.reverse"], Conveyor = new() { RunCommandChannelId = "signal.out", ReverseCommandChannelId = "signal.reverse" } },
                new OpenVisionLab.Machine.Core.Devices.DeviceDefinition { Id = "sensor.no.part", Name = "Unplaced sensor", Kind = OpenVisionLab.Machine.Core.Devices.DeviceKind.Sensor, ChannelIds = ["signal.no.part"], Sensor = new() { OutputChannelId = "signal.no.part", TargetComponentId = "frame" } }
            ]);
            project.Layouts[0].Components.AddRange([
                new LayoutComponentDefinition { Id = "scene.sensor", Name = "Actual sensor", Kind = LayoutComponentKind.DigitalSensor, BehaviorBindingId = "sensor.owner" },
                new LayoutComponentDefinition { Id = "scene.conveyor", Name = "Actual conveyor", Kind = LayoutComponentKind.Conveyor, BehaviorBindingId = "conveyor.owner" }
            ]);
            var steps = project.Sequences[0].Steps;
            steps.InsertRange(1, [
                new SequenceStepDefinition { Id = "wait.signal", Name = "Wait sensor", Action = SequenceStepAction.WaitSignal, TargetId = "signal.in", Parameter = "1" },
                new SequenceStepDefinition { Id = "set.signal", Name = "Run conveyor", Action = SequenceStepAction.SetSignal, TargetId = "signal.out", Parameter = "1" },
                new SequenceStepDefinition { Id = "unplaced.signal", Name = "Wait unplaced sensor", Action = SequenceStepAction.WaitSignal, TargetId = "signal.no.part", Parameter = "1" },
                new SequenceStepDefinition { Id = "unknown.signal", Name = "Channel without device owner", Action = SequenceStepAction.WaitSignal, TargetId = "signal.no.owner", Parameter = "1" },
                new SequenceStepDefinition { Id = "move.two", Name = "Move second axis", Action = SequenceStepAction.MoveAxis, TargetId = "axis.two", Parameter = "2" },
                new SequenceStepDefinition { Id = "wait.axis", Name = "Wait actual axis", Action = SequenceStepAction.WaitAxisDone, TargetId = "axis" },
                new SequenceStepDefinition { Id = "camera", Name = "Capture", Action = SequenceStepAction.TriggerCamera, TargetId = "camera.actual", Parameter = "recipe.actual" },
                new SequenceStepDefinition { Id = "missing", Name = "Missing target", Action = SequenceStepAction.MoveAxis, TargetId = "axis.missing", Parameter = "2" },
                new SequenceStepDefinition { Id = "ambiguous", Name = "Ambiguous target", Action = SequenceStepAction.MoveAxis, TargetId = "axis.ambiguous", Parameter = "2" },
                new SequenceStepDefinition { Id = "delay", Name = "Delay", Action = SequenceStepAction.Delay, Parameter = "1" }
            ]);
            using var vm = new MainViewModel(project);
            vm.Navigation.HideStartupChoice();
            var window = new ShellWindow { DataContext = vm, Width = width, Height = height, ShowInTaskbar = false, ShowActivated = false, UseLayoutRounding = true };
            SmokeDpiTestHook.PlaceOnTestMonitor(window, (int)width, (int)height);
            window.Show();
            try
            {
                var testMonitor = SmokeDpiTestHook.CaptureMonitorEvidence(window);
                Assert.True(testMonitor.WindowIntersectsMonitor);
                Assert.Equal(96, VisualTreeHelper.GetDpi(window).PixelsPerInchX);
                Directory.CreateDirectory(EvidenceRoot());
                File.WriteAllText(Path.Combine(EvidenceRoot(), $"selection-monitor-{width}-{language}.json"), JsonSerializer.Serialize(testMonitor));
                string original = JsonSerializer.Serialize(project);
                var originalTreeNode = vm.ProjectTree.SelectedNode;
                vm.Navigation.OpenSequenceEditorCommand.Execute(null);
                Settle(window);
                var center = Descendants(window).OfType<SequenceEditorView>().Single(control => control.IsVisible);
                var grid = Descendants(center).OfType<DataGrid>().Single(control => control.IsVisible && control.GetBindingExpression(System.Windows.Controls.Primitives.Selector.SelectedItemProperty)?.ParentBinding.Path.Path == "SelectedStep");
                var viewport = Descendants(window).OfType<OpenVisionLab.MachineStudio.View.Scene.MachineSceneViewport>().Single(control => control.IsVisible);
                var view = Descendants(window).OfType<SequenceStepInspectorView>().Single(control => control.IsVisible);
                var title = Descendants(window).OfType<TextBlock>().Single(text => text.IsVisible && text.Name == "SequenceSceneTitle");
                Assert.Equal(vm.Layout.EquipmentSceneTitleText, title.Text);
                Assert.Equal(14, title.FontSize);
                Assert.Equal(FontWeights.Bold, title.FontWeight);
                Assert.Equal("#FF243448", title.Foreground.ToString());
                var hint = Descendants(window).OfType<TextBlock>().Single(text => text.IsVisible && text.Name == "SequenceSceneHint");
                Assert.Equal(OpenVisionLanguageService.T("R24.SequenceSceneHint"), hint.Text);
                var legend = Descendants(window).OfType<WrapPanel>().Single(control => control.IsVisible && control.Name == "SequenceSceneLegend");
                Assert.False(legend.IsHitTestVisible);
                var selection = Descendants(window).OfType<Border>().Single(control => control.Name == "SequenceSceneSelection");
                var selectedName = Descendants(selection).OfType<TextBlock>().Single(text => text.Name == "SequenceSceneSelectionName");
                var references = Descendants(selection).OfType<Button>().Single();
                Assert.Same(vm.OpenEquipmentSequenceReferencesCommand, references.Command);
                Assert.True(references.IsEnabled && references.Command.CanExecute(null));
                vm.Navigation.SelectedExecutionTabIndex = 0;
                Settle(window);
                Assert.False(references.Command.CanExecute(null));
                vm.Navigation.SelectedExecutionTabIndex = 1;
                Settle(window);
                Assert.True(references.IsEnabled && references.Command.CanExecute(null));
                Assert.Equal(vm.Layout.EquipmentSelectionStepsText, references.Content);
                Assert.Equal(vm.Layout.EquipmentSelectionNameText, selectedName.Text);
                var plan = Descendants(window).OfType<RadioButton>().Single(control => control.Name == "SequenceScenePlanButton");
                Assert.Same(vm.Layout.ShowTopViewCommand, plan.Command);
                Assert.Equal(24, plan.ActualHeight);
                Assert.Equal(OpenVisionLanguageService.T("Scene.FitShort"), Descendants(window).OfType<Button>().Single(control => control.Name == "SequenceSceneFitButton").Content);
                Assert.True(viewport.UseEquipmentPresentation);
                Assert.Same(vm.ProjectAxes, viewport.EquipmentAxes);
                Assert.Same(vm.ProjectDevices, viewport.EquipmentDevices);
                Assert.Equal(vm.IsSceneEditable, viewport.IsDesignMode);
                AssertTarget("scene.axis.one");
                Select("move.two"); AssertTarget("scene.axis.two");
                Select("wait.axis"); AssertTarget("scene.axis.one");
                Select("wait.signal"); AssertTarget("scene.sensor");
                Assert.Equal(new[] { ("sequence", "wait.signal") }, vm.Layout.EquipmentSelectionStepReferences);
                Assert.Equal(vm.Layout.EquipmentSelectionStepsText, references.Content);
                Assert.True(references.IsEnabled);
                references.Command.Execute(null); Settle(window);
                Assert.Equal("wait.signal", vm.SequenceEditor.SelectedStep!.Id);
                AssertTarget("scene.sensor");
                Assert.Equal(Visibility.Visible, selection.Visibility);
                Assert.Equal(vm.Layout.EquipmentSelectionNameText, selectedName.Text);
                Select("set.signal"); AssertTarget("scene.conveyor");
                Assert.Equal(new[] { ("sequence", "set.signal") }, vm.Layout.EquipmentSelectionStepReferences);
                Assert.Equal(vm.Layout.EquipmentSelectionStepsText, references.Content);
                Select("unplaced.signal"); AssertTarget("scene.conveyor");
                Select("unknown.signal"); AssertTarget("scene.conveyor");
                var duplicateOwner = new OpenVisionLab.Machine.Core.Devices.DeviceDefinition { Id = "duplicate.owner", Kind = OpenVisionLab.Machine.Core.Devices.DeviceKind.Sensor, ChannelIds = ["signal.in"], Sensor = new() { OutputChannelId = "signal.in", TargetComponentId = "frame" } };
                project.Devices.Add(duplicateOwner);
                Select("move"); Select("wait.signal"); AssertTarget("scene.axis.one");
                project.Devices.Remove(duplicateOwner);
                Select("set.signal"); AssertTarget("scene.conveyor");
                Select("wait.signal"); AssertTarget("scene.sensor");
                var conveyorPart = project.Layouts[0].Components.Single(component => component.Id == "scene.conveyor");
                conveyorPart.BehaviorBindingId = "sensor.owner";
                Select("move"); Select("wait.signal"); AssertTarget("scene.axis.one");
                conveyorPart.BehaviorBindingId = "conveyor.owner";
                Select("set.signal"); AssertTarget("scene.conveyor");
                Select("wait.signal"); AssertTarget("scene.sensor");
                Assert.Same(originalTreeNode, vm.ProjectTree.SelectedNode);
                Assert.Equal(original, JsonSerializer.Serialize(project));
                Assert.False(vm.HasUnsavedChanges || vm.IsRunning || vm.SequenceEditor.HasPendingStepDraft);
                vm.SequenceEditor.IsEditable = false;
                Select("wait.signal"); AssertTarget("scene.sensor");
                Select("camera"); AssertTarget("scene.camera");
                vm.SequenceEditor.IsEditable = true;
                Select("missing"); AssertTarget("scene.camera");
                Select("ambiguous"); AssertTarget("scene.camera");
                Select("delay"); AssertTarget("scene.camera");
                Select("move"); AssertTarget("scene.axis.one");
                view.StepDraftTarget.SetCurrentValue(System.Windows.Controls.Primitives.Selector.SelectedValueProperty, "axis.two");
                Settle(window);
                AssertTarget("scene.axis.one");
                Assert.True(vm.SequenceEditor.HasPendingStepDraft);
                vm.PlacementDraftPrompt = () => PlacementDraftDecision.Cancel;
                Select("camera");
                Assert.Equal("move", vm.SequenceEditor.SelectedStep!.Id);
                AssertTarget("scene.axis.one");
                vm.SequenceEditor.DiscardStepDraft();
                var placement = Assert.IsType<LayoutComponentEditorViewModel>(vm.Layout.SelectedComponentEditor);
                placement.DraftXText = (vm.Layout.SelectedItem!.CurrentX + 20).ToString(System.Globalization.CultureInfo.InvariantCulture);
                Select("move.two");
                AssertTarget("scene.axis.one");
                Assert.True(placement.HasPendingPlacementDraft);
                placement.DiscardInspectorDraft();
                Select("move"); Select("move.two"); AssertTarget("scene.axis.two");
                Assert.Equal(original, JsonSerializer.Serialize(project));
                Assert.False(vm.HasUnsavedChanges || vm.IsRunning || vm.IsRunMode);
                Assert.Same(originalTreeNode, vm.ProjectTree.SelectedNode);
                Assert.Null(vm.Layout.ActiveUnitId);
                Assert.Null(vm.Layout.ActiveStationId);
                Capture(window, $"scene-selection-{width}-{language}-recovered");
                var applyTrace = new List<string>();
                System.ComponentModel.PropertyChangedEventHandler traceSelection = (sender, args) =>
                {
                    if (args.PropertyName is nameof(MachineLayoutViewModel.SelectedItem) or nameof(SequenceEditorViewModel.SelectedStep))
                        applyTrace.Add($"{sender?.GetType().Name}.{args.PropertyName}: step={vm.SequenceEditor.SelectedStep?.Id}; scene={vm.Layout.SelectedItem?.Id}");
                };
                vm.Layout.PropertyChanged += traceSelection;
                vm.SequenceEditor.PropertyChanged += traceSelection;
                try
                {
                    view.StepDraftTarget.SetCurrentValue(System.Windows.Controls.Primitives.Selector.SelectedValueProperty, "axis");
                    Assert.True(vm.SequenceEditor.TryApplyStepDraft());
                    Settle(window);
                    Directory.CreateDirectory(EvidenceRoot());
                    File.WriteAllText(Path.Combine(EvidenceRoot(), $"selection-apply-{width}-{language}.json"), JsonSerializer.Serialize(new { applyTrace, selectedStep = vm.SequenceEditor.SelectedStep?.Id, scene = vm.Layout.SelectedItem?.Id, saved = project.Sequences[0].Steps.Single(step => step.Id == "move.two").TargetId }));
                    AssertTarget("scene.axis.one");
                }
                finally { vm.Layout.PropertyChanged -= traceSelection; vm.SequenceEditor.PropertyChanged -= traceSelection; }
                Assert.Equal("axis", project.Sequences[0].Steps.Single(step => step.Id == "move.two").TargetId);
                Assert.True(vm.HasUnsavedChanges);
                Assert.False(vm.IsRunning || vm.SequenceEditor.HasPendingStepDraft);
                string applied = JsonSerializer.Serialize(project);
                var firstReference = vm.Layout.EquipmentSelectionStepReferences.First();
                references.Command.Execute(null);
                Settle(window);
                Assert.True(vm.Navigation.IsSequenceWorkspace);
                Assert.Equal(firstReference.SequenceId, vm.SequenceEditor.SelectedSequence?.Id);
                Assert.Equal(firstReference.StepId, vm.SequenceEditor.SelectedStep?.Id);
                Assert.Equal(applied, JsonSerializer.Serialize(project));
                Assert.False(vm.IsRunning || vm.SequenceEditor.HasPendingStepDraft);
                var retainedSelection = vm.Layout.SelectedItem;
                vm.Layout.SelectedItem = null;
                Settle(window);
                Assert.Equal(Visibility.Collapsed, selection.Visibility);
                vm.Layout.SelectedItem = retainedSelection;
                Settle(window);
                Assert.Equal(Visibility.Visible, selection.Visibility);
                Assert.Equal(applied, JsonSerializer.Serialize(project));
                Assert.True(vm.HasUnsavedChanges);
                Assert.False(vm.IsRunning || vm.SequenceEditor.HasPendingStepDraft);
                return true;

                void Select(string id) { grid.SetCurrentValue(System.Windows.Controls.Primitives.Selector.SelectedItemProperty, vm.SequenceEditor.Steps.Single(step => step.Id == id)); Settle(window); }
                void AssertTarget(string id) { Settle(window); Assert.Equal(id, vm.Layout.SelectedItem?.Id); Assert.Same(vm.Layout.SelectedItem, viewport.SelectedItem); Assert.Single(vm.Layout.Items.Where(item => item.IsSelected)); }
            }
            finally { vm.SequenceEditor.IsEditable = true; vm.SequenceEditor.DiscardStepDraft(); window.DataContext = null; window.Close(); OpenVisionLanguageService.SetLanguage(previousLanguage, save: false); }
        });
    }

    [Theory]
    [InlineData(1280, 760, OpenVisionLanguage.Korean)]
    [InlineData(1920, 1040, OpenVisionLanguage.Korean)]
    [InlineData(1280, 760, OpenVisionLanguage.English)]
    [InlineData(1920, 1040, OpenVisionLanguage.English)]
    public async Task TeachingSelectionFillsOnlyCurrentDraftAndPreservesExplicitApply(double width, double height, OpenVisionLanguage language)
    {
        await _ui.InvokeAsync(() =>
        {
            var previousLanguage = OpenVisionLanguageService.CurrentLanguage;
            OpenVisionLanguageService.SetLanguage(language, save: false);
            var project = CreateProject();
            project.Sequences[0].Steps.Single(step => step.Id == "move").Parameter = "1";
            project.Axes[0].TeachingPositions = new Dictionary<string, double> { ["실제 티칭"] = 2, [new string('T', 90)] = 3, [string.Empty] = 4 };
            project.Axes.Add(new VirtualAxisDefinition { Id = "axis.empty", Name = "Empty teaching", Unit = "deg", Kind = AxisKind.Rotary, SoftLimitMin = -10, SoftLimitMax = 10 });
            project.Layouts[0].Components.Add(new LayoutComponentDefinition { Id = "scene.axis.empty", Name = "Empty teaching", Kind = LayoutComponentKind.RotaryStage, BehaviorBindingId = "axis.empty" });
            using var vm = new MainViewModel(project);
            vm.Navigation.HideStartupChoice();
            var window = new ShellWindow { DataContext = vm, Width = width, Height = height, ShowInTaskbar = false, ShowActivated = false, UseLayoutRounding = true };
            window.Show();
            try
            {
                vm.Navigation.OpenSequenceEditorCommand.Execute(null);
                vm.SequenceEditor.SelectedStep = vm.SequenceEditor.Steps.Single(step => step.Id == "move");
                Settle(window);
                var view = Descendants(window).OfType<SequenceStepInspectorView>().Single(control => control.IsVisible);
                var teaching = (ComboBox)view.FindName("StepDraftTeaching");
                project.Axes[0].TeachingPositions!["invalid"] = double.NaN;
                Assert.Equal(3, vm.SequenceEditor.StepTeachingOptions.Count);
                project.Axes[0].TeachingPositions!.Remove("invalid");
                var original = JsonSerializer.Serialize(project);
                Assert.Equal(string.Empty, vm.SequenceEditor.StepTeachingName);
                Assert.Equal(string.Empty, teaching.SelectedValue);
                Assert.Equal(3, teaching.Items.Count);
                teaching.SetCurrentValue(System.Windows.Controls.Primitives.Selector.SelectedValueProperty, "실제 티칭");
                Settle(window);
                Assert.Equal("실제 티칭", vm.SequenceEditor.StepTeachingName);
                Assert.Equal("2", view.StepDraftParameter.Text);
                Assert.True(vm.SequenceEditor.HasPendingStepDraft && view.ApplyStepDraftButton.IsEnabled);
                Assert.Equal(original, JsonSerializer.Serialize(project));
                Assert.False(vm.HasUnsavedChanges || vm.IsRunning);
                Assert.Equal(9, Bounds(teaching, view).Left - Bounds(view.StepDraftParameter, view).Right);
                view.StepDraftParameter.SetCurrentValue(TextBox.TextProperty, "NaN");
                Settle(window);
                Assert.Equal(string.Empty, vm.SequenceEditor.StepTeachingName);
                Assert.True(vm.SequenceEditor.IsStepParameterInvalid);
                teaching.SetCurrentValue(System.Windows.Controls.Primitives.Selector.SelectedValueProperty, "실제 티칭");
                Settle(window);
                Assert.Equal("2", view.StepDraftParameter.Text);
                Assert.False(vm.SequenceEditor.IsStepParameterInvalid);
                vm.SequenceEditor.IsEditable = false;
                vm.SequenceEditor.StepTeachingName = new string('T', 90);
                Settle(window);
                Assert.False(teaching.IsEnabled);
                Assert.Equal("2", view.StepDraftParameter.Text);
                vm.SequenceEditor.IsEditable = true;
                vm.SequenceEditor.DiscardStepDraft();
                Settle(window);
                Assert.Equal(string.Empty, teaching.SelectedValue);
                Assert.Equal(original, JsonSerializer.Serialize(project));
                vm.SequenceEditor.StepTeachingName = "missing teaching";
                Assert.False(vm.SequenceEditor.HasPendingStepDraft);
                teaching.SetCurrentValue(System.Windows.Controls.Primitives.Selector.SelectedValueProperty, "실제 티칭");
                view.StepDraftTarget.SetCurrentValue(System.Windows.Controls.Primitives.Selector.SelectedValueProperty, "axis.empty");
                Settle(window);
                Assert.Equal(string.Empty, vm.SequenceEditor.StepTeachingName);
                Assert.Single(teaching.Items.Cast<object>());
                Assert.Equal("2", view.StepDraftParameter.Text);
                view.StepDraftTarget.SetCurrentValue(System.Windows.Controls.Primitives.Selector.SelectedValueProperty, "missing-axis");
                Settle(window);
                Assert.Single(vm.SequenceEditor.StepTeachingOptions);
                Assert.Equal(string.Empty, vm.SequenceEditor.StepTeachingName);
                vm.SequenceEditor.DiscardStepDraft();
                Settle(window);
                teaching.SetCurrentValue(System.Windows.Controls.Primitives.Selector.SelectedValueProperty, new string('T', 90));
                Settle(window);
                Assert.Equal("3", view.StepDraftParameter.Text);
                Assert.Equal(28, teaching.ActualHeight);
                var selectedTeachingText = Descendants(teaching).OfType<TextBlock>().Single(text => text.IsVisible && text.Text == vm.SequenceEditor.StepTeachingOptions.Single(pair => pair.Key == new string('T', 90)).Value);
                Assert.Equal(TextWrapping.NoWrap, selectedTeachingText.TextWrapping);
                Assert.Equal(TextTrimming.CharacterEllipsis, selectedTeachingText.TextTrimming);
                vm.SequenceEditor.ApplyStepDraftCommand.Execute(null);
                Settle(window);
                Assert.Equal("3", project.Sequences[0].Steps.Single(step => step.Id == "move").Parameter);
                Assert.True(vm.HasUnsavedChanges);
                Assert.False(vm.IsRunning || vm.SequenceEditor.HasPendingStepDraft);
                Assert.Equal(string.Empty, vm.SequenceEditor.StepTeachingName);
                Capture(window, $"teaching-{width}-{language}-applied");
                teaching.SetCurrentValue(System.Windows.Controls.Primitives.Selector.SelectedValueProperty, "실제 티칭");
                Settle(window);
                Assert.Equal("실제 티칭", vm.SequenceEditor.StepTeachingName);
                vm.Dispose();
                var disposeFrame = new DispatcherFrame();
                var disposeWatch = System.Diagnostics.Stopwatch.StartNew();
                var disposeTimer = new DispatcherTimer(DispatcherPriority.Background, window.Dispatcher) { Interval = TimeSpan.FromMilliseconds(10) };
                disposeTimer.Tick += (_, _) => { if (vm.SequenceEditor.SelectedStepDraft is null || disposeWatch.Elapsed >= TimeSpan.FromSeconds(6)) disposeFrame.Continue = false; };
                disposeTimer.Start();
                try { Dispatcher.PushFrame(disposeFrame); }
                finally { disposeTimer.Stop(); }
                Assert.Null(vm.SequenceEditor.SelectedStepDraft);
                Assert.Equal(string.Empty, vm.SequenceEditor.StepTeachingName);
                vm.SequenceEditor.StepTeachingName = new string('T', 90);
                Assert.Equal(string.Empty, vm.SequenceEditor.StepTeachingName);
                Assert.Equal("3", project.Sequences[0].Steps.Single(step => step.Id == "move").Parameter);
                return true;
            }
            finally { vm.SequenceEditor.IsEditable = true; vm.SequenceEditor.DiscardStepDraft(); window.DataContext = null; window.Close(); OpenVisionLanguageService.SetLanguage(previousLanguage, save: false); }
        });
    }

    [Theory]
    [InlineData(1280, 760, OpenVisionLanguage.Korean)]
    [InlineData(1920, 1040, OpenVisionLanguage.Korean)]
    [InlineData(1280, 760, OpenVisionLanguage.English)]
    [InlineData(1920, 1040, OpenVisionLanguage.English)]
    public async Task NextStepSelectionPreservesRealIdsUnknownValuesAndExplicitApply(double width, double height, OpenVisionLanguage language)
    {
        await _ui.InvokeTaskAsync(async () =>
        {
            var previousLanguage = OpenVisionLanguageService.CurrentLanguage;
            OpenVisionLanguageService.SetLanguage(language, save: false);
            var project = CreateProject();
            project.Sequences[0].Steps[1].Name = new string('N', 90);
            project.Sequences[0].Steps.Insert(1, new SequenceStepDefinition { Id = "END", Name = "실제 사용자 END", Action = SequenceStepAction.Delay, Parameter = "0", NextStepId = "complete" });
            project.Axes[0].TeachingPositions = new Dictionary<string, double> { ["실제 티칭"] = 3 };
            using var vm = new MainViewModel(project);
            vm.PlacementDraftPrompt = () => PlacementDraftDecision.Cancel;
            vm.Navigation.HideStartupChoice();
            var window = new ShellWindow { DataContext = vm, Width = width, Height = height, ShowInTaskbar = false, ShowActivated = false, UseLayoutRounding = true };
            window.Show();
            try
            {
                vm.Navigation.OpenSequenceEditorCommand.Execute(null);
                Settle(window);
                var editor = vm.SequenceEditor;
                var view = Descendants(window).OfType<SequenceStepInspectorView>().Single(control => control.IsVisible);
                var next = (ComboBox)view.FindName("StepDraftNext");
                var caption = (TextBlock)view.FindName("StepNextCaption");
                Assert.Equal(OpenVisionLanguageService.T("R24.NextStep"), caption.Text);
                Assert.Equal(caption.Text, System.Windows.Automation.AutomationProperties.GetName(next));
                Assert.Equal(4, next.Items.Count);
                Assert.Equal("complete", next.SelectedValue);
                Assert.Equal(OpenVisionLanguageService.T("R24.DefaultNextStep"), editor.StepNextStepOptions[0].Value);
                Assert.Equal("2. 실제 사용자 END", editor.StepNextStepOptions.Single(option => option.Key == "END").Value);
                var selectedText = Descendants(next).OfType<TextBlock>().Single(value => value.IsVisible && value.Text == editor.StepNextStepOptions.Single(option => option.Key == "complete").Value);
                Assert.Equal(28, next.ActualHeight);
                Assert.Equal(TextWrapping.NoWrap, selectedText.TextWrapping);
                Assert.Equal(TextTrimming.CharacterEllipsis, selectedText.TextTrimming);
                var original = JsonSerializer.Serialize(project);
                next.SetCurrentValue(System.Windows.Controls.Primitives.Selector.SelectedValueProperty, "END");
                Settle(window);
                Assert.Equal("END", editor.SelectedStepDraft!.NextStepId);
                Assert.Equal(original, JsonSerializer.Serialize(project));
                Assert.False(vm.HasUnsavedChanges || vm.IsRunning);
                view.StepDraftParameter.SetCurrentValue(TextBox.TextProperty, "4");
                var teaching = (ComboBox)view.FindName("StepDraftTeaching");
                teaching.SetCurrentValue(System.Windows.Controls.Primitives.Selector.SelectedValueProperty, "실제 티칭");
                Settle(window);
                Assert.Equal("END", next.SelectedValue);
                OpenVisionLanguageService.SetLanguage(language == OpenVisionLanguage.Korean ? OpenVisionLanguage.English : OpenVisionLanguage.Korean, save: false);
                Settle(window);
                Assert.Equal("END", next.SelectedValue);
                OpenVisionLanguageService.SetLanguage(language, save: false);
                editor.StepNextStepId = null!;
                Settle(window);
                Assert.Equal("END", editor.StepNextStepId);
                editor.IsEditable = false;
                editor.StepNextStepId = "complete";
                Settle(window);
                Assert.False(next.IsEnabled);
                Assert.Equal("END", editor.StepNextStepId);
                editor.IsEditable = true;
                next.SetCurrentValue(System.Windows.Controls.Primitives.Selector.SelectedValueProperty, string.Empty);
                Settle(window);
                Assert.Equal(string.Empty, editor.SelectedStepDraft.NextStepId);
                Assert.Equal(string.Empty, next.SelectedValue);
                Assert.Single(Descendants(next).OfType<TextBlock>().Where(value => value.IsVisible && value.Text == OpenVisionLanguageService.T("R24.DefaultNextStep")));
                editor.DiscardStepDraft();
                teaching.SetCurrentValue(System.Windows.Controls.Primitives.Selector.SelectedValueProperty, "실제 티칭");
                next.SetCurrentValue(System.Windows.Controls.Primitives.Selector.SelectedValueProperty, string.Empty);
                Settle(window);
                Assert.Equal(string.Empty, next.SelectedValue);
                Assert.Single(Descendants(next).OfType<TextBlock>().Where(value => value.IsVisible && value.Text == OpenVisionLanguageService.T("R24.DefaultNextStep")));
                string unknown = "missing-next-" + new string('X', 90);
                editor.SelectedStepDraft.NextStepId = unknown;
                Settle(window);
                Assert.Equal(unknown, next.SelectedValue);
                Assert.Equal(unknown, editor.StepNextStepOptions.Single(option => option.Key == unknown).Value);
                Assert.False(view.ApplyStepDraftButton.IsEnabled);
                Assert.False(string.IsNullOrEmpty(view.StepDraftError.Text));
                view.StepDraftParameter.SetCurrentValue(TextBox.TextProperty, "4");
                Settle(window);
                Assert.Equal(unknown, next.SelectedValue);
                Assert.Equal(original, JsonSerializer.Serialize(project));
                next.SetCurrentValue(System.Windows.Controls.Primitives.Selector.SelectedValueProperty, "END");
                Settle(window);
                Assert.True(view.ApplyStepDraftButton.IsEnabled);
                editor.ApplyStepDraftCommand.Execute(null);
                Settle(window);
                Assert.Equal("END", project.Sequences[0].Steps[0].NextStepId);
                Assert.Equal("END", next.SelectedValue);
                Assert.True(vm.HasUnsavedChanges);
                Assert.False(editor.HasPendingStepDraft || vm.IsRunning);
                string file = Path.Combine(EvidenceRoot(), $"next-{width}-{language}-{Guid.NewGuid():N}.ovmachine");
                Directory.CreateDirectory(EvidenceRoot());
                await vm.SaveProjectAsync(file);
                var reopened = await new ProjectDocumentFileStore().LoadAsync(file);
                Assert.Equal("END", reopened.Sequences[0].Steps[0].NextStepId);
                Assert.False(vm.HasUnsavedChanges || vm.IsRunning);
                var oldDraft = editor.SelectedStepDraft;
                vm.Dispose();
                var disposeFrame = new DispatcherFrame();
                var disposeWatch = System.Diagnostics.Stopwatch.StartNew();
                var disposeTimer = new DispatcherTimer(DispatcherPriority.Background, window.Dispatcher) { Interval = TimeSpan.FromMilliseconds(10) };
                disposeTimer.Tick += (_, _) => { if (editor.SelectedStepDraft is null || disposeWatch.Elapsed >= TimeSpan.FromSeconds(6)) disposeFrame.Continue = false; };
                disposeTimer.Start();
                try { Dispatcher.PushFrame(disposeFrame); }
                finally { disposeTimer.Stop(); }
                Assert.Null(editor.SelectedStepDraft);
                editor.StepNextStepId = "complete";
                oldDraft!.NextStepId = "missing-after-shutdown";
                Assert.Equal(string.Empty, editor.StepNextStepId);
                Assert.Equal("END", project.Sequences[0].Steps[0].NextStepId);
                return true;
            }
            finally { window.DataContext = null; window.Close(); OpenVisionLanguageService.SetLanguage(previousLanguage, save: false); }
        });
    }

    [Theory]
    [InlineData(1280, 760, OpenVisionLanguage.Korean)]
    [InlineData(1920, 1040, OpenVisionLanguage.Korean)]
    [InlineData(1280, 760, OpenVisionLanguage.English)]
    [InlineData(1920, 1040, OpenVisionLanguage.English)]
    public async Task AxisMoveFieldsFollowSourceOrderWithoutChangingSavedPolicy(double width, double height, OpenVisionLanguage language)
    {
        await _ui.InvokeAsync(() =>
        {
            var previousLanguage = OpenVisionLanguageService.CurrentLanguage;
            OpenVisionLanguageService.SetLanguage(language, save: false);
            var project = CreateProject();
            var definition = project.Sequences[0].Steps[0];
            definition.TimeoutMs = 4337;
            definition.ErrorStepId = "complete";
            definition.WaitForAxisCompletion = true;
            using var vm = new MainViewModel(project);
            vm.PlacementDraftPrompt = () => PlacementDraftDecision.Cancel;
            vm.Navigation.HideStartupChoice();
            var window = new ShellWindow { DataContext = vm, Width = width, Height = height, ShowInTaskbar = false, ShowActivated = false, UseLayoutRounding = true };
            window.Show();
            try
            {
                vm.Navigation.OpenSequenceEditorCommand.Execute(null);
                Settle(window);
                var view = Descendants(window).OfType<SequenceStepInspectorView>().Single(control => control.IsVisible);
                var editor = vm.SequenceEditor;
                var next = (ComboBox)view.FindName("StepDraftNext");
                Assert.True(view.StepDraftAction.IsVisible && view.StepDraftTimeout.IsVisible);
                Assert.True(view.StepDraftName.IsVisible && view.StepDraftTarget.IsVisible && view.StepDraftParameter.IsVisible && next.IsVisible);
                Assert.True(Bounds(view.StepDraftName, view).Bottom <= Bounds(view.StepDraftTarget, view).Top);
                Assert.True(Bounds(view.StepDraftTarget, view).Bottom <= Bounds(view.StepDraftParameter, view).Top);
                Assert.True(Bounds(view.StepDraftParameter, view).Bottom <= Bounds(next, view).Top);
                var primaryActions = Descendants(view).OfType<Button>().Where(button => button.Command == editor.MoveStepUpCommand || button.Command == editor.MoveStepDownCommand || button.Command == editor.DeleteStepCommand).ToArray();
                Assert.Equal(3, primaryActions.Length);
                Assert.True(primaryActions.All(button => Bounds(button, view).Bottom <= Bounds(view.TestSelectedStepButton, view).Top));
                Assert.Equal("4.337", view.StepDraftTimeout.Text);
                var completion = Descendants(view).OfType<CheckBox>().Single(check => check.GetBindingExpression(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty)?.ParentBinding.Path.Path == "SelectedStepDraft.WaitForAxisCompletion");
                Assert.True(completion.IsVisible && completion.IsChecked == true);
                var policyBefore = JsonSerializer.Serialize(project);
                ((System.Windows.Automation.Provider.IToggleProvider)new System.Windows.Automation.Peers.CheckBoxAutomationPeer(completion).GetPattern(System.Windows.Automation.Peers.PatternInterface.Toggle)).Toggle(); Settle(window);
                Assert.False(editor.SelectedStepDraft!.WaitForAxisCompletion);
                Assert.True(view.StepDraftTimeout.IsVisible);
                Assert.Equal("4.337", view.StepDraftTimeout.Text);
                view.StepDraftTimeout.SetCurrentValue(TextBox.TextProperty, "0"); Settle(window);
                Assert.Equal(0, editor.SelectedStepDraft.TimeoutMs);
                Assert.Equal(4337, editor.SelectedStep!.TimeoutMs);
                Assert.Equal(policyBefore, JsonSerializer.Serialize(project));
                ((System.Windows.Automation.Provider.IInvokeProvider)new System.Windows.Automation.Peers.ButtonAutomationPeer(view.DiscardStepDraftButton).GetPattern(System.Windows.Automation.Peers.PatternInterface.Invoke)).Invoke(); Settle(window);
                Assert.True(editor.SelectedStepDraft.WaitForAxisCompletion && view.StepDraftTimeout.IsVisible);
                Assert.Equal("4.337", view.StepDraftTimeout.Text);
                Assert.Equal(policyBefore, JsonSerializer.Serialize(project));
                var original = JsonSerializer.Serialize(project);
                Capture(window, $"axis-fields-{width}-{language}-normal");
                editor.SelectedStepDraft!.NextStepId = "missing-real-step-" + new string('X', 90);
                Settle(window);
                Assert.False(view.ApplyStepDraftButton.IsEnabled);
                Assert.False(string.IsNullOrEmpty(view.StepDraftError.Text));
                view.StepDraftParameter.SetCurrentValue(TextBox.TextProperty, "4");
                next.SetCurrentValue(System.Windows.Controls.Primitives.Selector.SelectedValueProperty, "complete");
                Settle(window);
                Assert.Equal(original, JsonSerializer.Serialize(project));
                Assert.False(vm.HasUnsavedChanges || vm.IsRunning);
                Assert.True(view.ApplyStepDraftButton.IsEnabled);
                editor.ApplyStepDraftCommand.Execute(null);
                Settle(window);
                Assert.Equal("4", definition.Parameter);
                Assert.Equal(4337, definition.TimeoutMs);
                Assert.Equal("complete", definition.ErrorStepId);
                Assert.Null(definition.FailureStepId);
                Assert.True(definition.WaitForAxisCompletion);
                Assert.Equal("complete", definition.NextStepId);
                Assert.False(editor.HasPendingStepDraft || vm.IsRunning);
                original = JsonSerializer.Serialize(project);
                editor.SelectedStepDraft!.Action = SequenceStepAction.Delay;
                Settle(window);
                Assert.True(view.StepDraftAction.IsVisible);
                Assert.False(view.StepDraftTimeout.IsVisible);
                Assert.True(((StackPanel)view.FindName("StepCheckpointFields")).IsVisible);
                Assert.False(((ComboBox)view.FindName("StepDraftTeaching")).IsVisible);
                Assert.Equal(original, JsonSerializer.Serialize(project));
                editor.DiscardStepDraft();
                Settle(window);
                Assert.True(view.StepDraftAction.IsVisible && view.StepDraftTimeout.IsVisible);
                Assert.Equal(4337, editor.SelectedStepDraft!.TimeoutMs);
                Assert.Equal("complete", editor.SelectedStepDraft.ErrorStepId);
                Assert.True(editor.SelectedStepDraft.WaitForAxisCompletion);
                editor.IsEditable = false;
                Settle(window);
                Assert.False(view.StepDraftFields.IsEnabled || view.ApplyStepDraftButton.IsEnabled || view.TestSelectedStepButton.IsEnabled);
                editor.IsEditable = true;
                editor.SelectedStep = null;
                Settle(window);
                Assert.False(view.StepDraftFields.IsVisible || view.StepDraftFields.IsEnabled);
                Assert.Equal(original, JsonSerializer.Serialize(project));
                Assert.False(vm.IsRunning);
                Capture(window, $"axis-fields-{width}-{language}-empty");
                return true;
            }
            finally { window.DataContext = null; window.Close(); OpenVisionLanguageService.SetLanguage(previousLanguage, save: false); }
        });
    }

    [Theory]
    [InlineData(1280, 900, OpenVisionLanguage.Korean)]
    [InlineData(1920, 1032, OpenVisionLanguage.Korean)]
    [InlineData(1280, 900, OpenVisionLanguage.English)]
    [InlineData(1920, 1032, OpenVisionLanguage.English)]
    public async Task SignalChoicesUseOnOffWithoutRewritingSavedValues(double width, double height, OpenVisionLanguage language)
    {
        await _ui.InvokeAsync(() =>
        {
            var previousLanguage = OpenVisionLanguageService.CurrentLanguage;
            OpenVisionLanguageService.SetLanguage(language, save: false);
            var project = CreateProject();
            project.Channels.Add(new OpenVisionLab.Machine.Core.Channels.ChannelDefinition { Id = "signal.in", Name = "Sensor signal", Kind = OpenVisionLab.Machine.Core.Channels.ChannelKind.DigitalInput });
            project.Channels.Add(new OpenVisionLab.Machine.Core.Channels.ChannelDefinition { Id = "signal.out", Name = "Output signal", Kind = OpenVisionLab.Machine.Core.Channels.ChannelKind.DigitalOutput });
            project.Sequences[0].Steps.InsertRange(0, [
                new SequenceStepDefinition { Id = "wait", Name = "Wait signal", Action = SequenceStepAction.WaitSignal, TargetId = "signal.in", Parameter = "1", TimeoutMs = 10000 },
                new SequenceStepDefinition { Id = "output", Name = "Output signal", Action = SequenceStepAction.SetSignal, TargetId = "signal.out", Parameter = "1" },
                new SequenceStepDefinition { Id = "delay", Name = "Delay", Action = SequenceStepAction.Delay, Parameter = "1.5" }
            ]);
            using var vm = new MainViewModel(project);
            vm.PlacementDraftPrompt = () => PlacementDraftDecision.Cancel;
            vm.Navigation.HideStartupChoice();
            var work = SystemParameters.WorkArea;
            var window = new ShellWindow { DataContext = vm, Width = width, Height = height, WindowStartupLocation = WindowStartupLocation.Manual, Left = work.Left, Top = work.Top, ShowInTaskbar = false, ShowActivated = false, UseLayoutRounding = true };
            SmokeDpiTestHook.PlaceOnTestMonitor(window, (int)width, (int)height);
            window.Show();
            try
            {
                var testMonitor = SmokeDpiTestHook.CaptureMonitorEvidence(window);
                Assert.True(testMonitor.WindowIntersectsMonitor);
                Assert.Equal(96, VisualTreeHelper.GetDpi(window).PixelsPerInchX);
                Directory.CreateDirectory(EvidenceRoot());
                File.WriteAllText(Path.Combine(EvidenceRoot(), $"signal-monitor-{width}-{language}.json"), JsonSerializer.Serialize(testMonitor));
                vm.Navigation.OpenSequenceEditorCommand.Execute(null);
                var editor = vm.SequenceEditor;
                editor.SelectedStep = editor.Steps.Single(step => step.Id == "wait");
                Settle(window);
                var view = Descendants(window).OfType<SequenceStepInspectorView>().Single(control => control.IsVisible);
                var choice = (ComboBox)view.FindName("StepSignalChoice");
                string original = JsonSerializer.Serialize(project);
                Assert.Equal(1, PresentationSource.FromVisual(view)!.CompositionTarget!.TransformToDevice.M11);
                Assert.Equal("true", choice.SelectedItem);
                Assert.Equal("1", editor.SelectedStepDraft!.Parameter);
                Assert.False(editor.HasPendingStepDraft || vm.HasUnsavedChanges || vm.IsRunning);
                Assert.Equal(OpenVisionLanguageService.T("R24.StepSignal"), System.Windows.Automation.AutomationProperties.GetName(view.StepDraftTarget));
                Assert.Equal(OpenVisionLanguageService.T("R24.StepExpectedValue"), System.Windows.Automation.AutomationProperties.GetName(choice));
                CheckChoice("ON");
                choice.IsDropDownOpen = true;
                Settle(window);
                var popup = Descendants(choice).OfType<System.Windows.Controls.Primitives.Popup>().FirstOrDefault()
                    ?? choice.Template.FindName("PART_Popup", choice) as System.Windows.Controls.Primitives.Popup
                    ?? choice.Template.FindName("Popup", choice) as System.Windows.Controls.Primitives.Popup;
                Assert.NotNull(popup?.Child);
                var popupText = Descendants(popup!.Child).OfType<TextBlock>().Where(text => text.IsVisible).Select(text => text.Text).ToArray();
                Assert.Contains("ON", popupText); Assert.Contains("OFF", popupText);
                choice.IsDropDownOpen = false;
                foreach (var alias in new[] { "true", "TRUE", "1", "false", "FALSE", "0" })
                {
                    editor.SelectedStepDraft.Parameter = alias;
                    Settle(window);
                    Assert.Equal(alias, editor.SelectedStepDraft.Parameter);
                    CheckChoice(alias is "false" or "FALSE" or "0" ? "OFF" : "ON");
                    Assert.Equal(original, JsonSerializer.Serialize(project));
                }
                editor.DiscardStepDraft(); Settle(window);
                var inline = new SequenceEditorView { DataContext = editor, ShowInlineEditor = true };
                var inlineWindow = new Window { Owner = window, Content = inline, Width = 850, Height = 550, WindowStartupLocation = WindowStartupLocation.Manual, Left = work.Left, Top = work.Top, ShowInTaskbar = false, ShowActivated = false };
                try
                {
                    inlineWindow.Show(); Settle(inlineWindow);
                    var inlineChoice = Descendants(inline).OfType<ComboBox>().Single(control => control.IsVisible && control.Items.Cast<object>().SequenceEqual(new object[] { "true", "false" }));
                    Assert.Equal("true", inlineChoice.SelectedItem);
                    Assert.Equal("1", editor.SelectedStep!.Parameter);
                    Assert.Equal(original, JsonSerializer.Serialize(project));
                }
                finally { inlineWindow.Content = null; inlineWindow.Close(); inline.DataContext = null; }
                editor.IsEditable = false; Settle(window);
                Assert.False(choice.IsEnabled || view.ApplyStepDraftButton.IsEnabled);
                editor.IsEditable = true;
                choice.SetCurrentValue(System.Windows.Controls.Primitives.Selector.SelectedItemProperty, "false"); Settle(window);
                Assert.Equal("false", editor.SelectedStepDraft!.Parameter);
                Assert.Equal("1", editor.SelectedStep!.Parameter);
                Assert.Equal(original, JsonSerializer.Serialize(project));
                Assert.True(view.ApplyStepDraftButton.IsEnabled);
                view.ApplyStepDraftButton.Command.Execute(null); Settle(window);
                Assert.Equal("false", editor.SelectedStep.Parameter);
                Assert.False(editor.HasPendingStepDraft || vm.IsRunning);
                string applied = new ProjectDocumentStore().Save(project);
                string path = Path.Combine(EvidenceRoot(), $"signal-roundtrip-{width}-{language}.ovmachine");
                Directory.CreateDirectory(EvidenceRoot()); File.WriteAllText(path, applied);
                Assert.Equal("false", new ProjectDocumentStore().Load(File.ReadAllText(path)).Sequences[0].Steps.Single(step => step.Id == "wait").Parameter);
                editor.SelectedStepDraft.Parameter = "invalid-boolean"; Settle(window);
                Assert.Equal("invalid-boolean", editor.SelectedStepDraft.Parameter);
                Assert.False(view.ApplyStepDraftButton.IsEnabled);
                choice.SetCurrentValue(System.Windows.Controls.Primitives.Selector.SelectedItemProperty, "true"); Settle(window);
                Assert.True(view.ApplyStepDraftButton.IsEnabled);
                editor.DiscardStepDraft();
                editor.SelectedStep = editor.Steps.Single(step => step.Id == "output"); Settle(window);
                Assert.Contains(Descendants(view.StepIdentityHeader).OfType<TextBlock>(), text => text.Text == OpenVisionLanguageService.T("R24.OutputOn"));
                choice.SetCurrentValue(System.Windows.Controls.Primitives.Selector.SelectedItemProperty, "false"); Settle(window);
                Assert.Equal("1", editor.SelectedStep!.Parameter);
                view.ApplyStepDraftButton.Command.Execute(null); Settle(window);
                Assert.Contains(Descendants(view.StepIdentityHeader).OfType<TextBlock>(), text => text.Text == OpenVisionLanguageService.T("R24.OutputOff"));
                editor.SelectedStep = editor.Steps.Single(step => step.Id == "delay"); Settle(window);
                Assert.False(((FrameworkElement)view.FindName("StepTargetFields")).IsVisible || choice.IsVisible);
                Assert.Equal(OpenVisionLanguageService.T("Sequence.Action.Delay") + " (s)", ((TextBlock)view.FindName("StepParameterCaption")).Text);
                Assert.Equal("1.5", view.StepDraftParameter.Text);
                view.StepDraftParameter.Text = "2.75"; Settle(window);
                Assert.Equal("2.75", editor.SelectedStepDraft!.Parameter); Assert.Equal("1.5", editor.SelectedStep!.Parameter);
                editor.DiscardStepDraft();
                editor.SelectedStep = editor.Steps.Single(step => step.Id == "move"); Settle(window);
                Assert.True(view.StepDraftTarget.IsVisible && view.StepDraftParameter.IsVisible);
                Assert.Equal(OpenVisionLanguageService.T("Sequence.Target"), System.Windows.Automation.AutomationProperties.GetName(view.StepDraftTarget));
                Capture(window, $"signal-recovered-{width}-{language}");
                editor.SelectedStep = null; Settle(window);
                Assert.False(view.StepDraftFields.IsVisible || view.StepDraftFields.IsEnabled || vm.IsRunning);
                return true;

                void CheckChoice(string expected)
                {
                    var text = Descendants(choice).OfType<TextBlock>().Single(text => text.IsVisible && text.Text == expected);
                    Assert.Equal(12, text.FontSize);
                    Assert.Equal(FontWeights.Normal, text.FontWeight);
                    Assert.NotNull(VisualTreeHelper.GetDrawing(text));
                    var glyphs = Glyphs(VisualTreeHelper.GetDrawing(text)!);
                    Assert.Contains(glyphs, glyph => new string(glyph.Characters?.ToArray() ?? []) == expected && glyph.FontRenderingEmSize == 12 && glyph.GlyphTypeface.Weight.ToOpenTypeWeight() == 400);
                }
                static IEnumerable<GlyphRun> Glyphs(Drawing drawing)
                {
                    if (drawing is GlyphRunDrawing glyph) yield return glyph.GlyphRun;
                    if (drawing is DrawingGroup group) foreach (Drawing child in group.Children) foreach (var run in Glyphs(child)) yield return run;
                }
            }
            finally { window.DataContext = null; window.Close(); OpenVisionLanguageService.SetLanguage(previousLanguage, save: false); }
        });
    }

    [Theory]
    [InlineData(1280, 900, OpenVisionLanguage.Korean)]
    [InlineData(1920, 1032, OpenVisionLanguage.Korean)]
    [InlineData(1280, 900, OpenVisionLanguage.English)]
    [InlineData(1920, 1032, OpenVisionLanguage.English)]
    public async Task WaitSignalTimeoutUsesExactSecondsAndSourceFieldPairWithoutImplicitApply(double width, double height, OpenVisionLanguage language)
    {
        await _ui.InvokeAsync(() =>
        {
            var previousLanguage = OpenVisionLanguageService.CurrentLanguage;
            OpenVisionLanguageService.SetLanguage(language, save: false);
            var project = CreateProject();
            project.Channels.Add(new OpenVisionLab.Machine.Core.Channels.ChannelDefinition { Id = "signal.in", Name = "Sensor signal", Kind = OpenVisionLab.Machine.Core.Channels.ChannelKind.DigitalInput });
            project.Sequences[0].Steps.InsertRange(0, [
                new SequenceStepDefinition { Id = "wait", Name = "Wait signal", Action = SequenceStepAction.WaitSignal, TargetId = "signal.in", Parameter = "1", TimeoutMs = 10000 },
                new SequenceStepDefinition { Id = "delay", Name = "Delay", Action = SequenceStepAction.Delay, Parameter = "1.5" }
            ]);
            using var vm = new MainViewModel(project);
            vm.Navigation.HideStartupChoice();
            var window = new ShellWindow { DataContext = vm, Width = width, Height = height, ShowInTaskbar = false, ShowActivated = false, UseLayoutRounding = true };
            SmokeDpiTestHook.PlaceOnTestMonitor(window, (int)width, (int)height);
            window.Show();
            try
            {
                var monitor = SmokeDpiTestHook.CaptureMonitorEvidence(window);
                Assert.True(monitor.WindowIntersectsMonitor);
                Assert.Equal(96, VisualTreeHelper.GetDpi(window).PixelsPerInchX);
                vm.Navigation.OpenSequenceEditorCommand.Execute(null);
                Settle(window);
                var view = Descendants(window).OfType<SequenceStepInspectorView>().Single(control => control.IsVisible);
                var editor = vm.SequenceEditor;
                var choice = (ComboBox)view.FindName("StepSignalChoice");
                var caption = (TextBlock)view.FindName("StepTimeoutCaption");
                string original = JsonSerializer.Serialize(project);
                Assert.Equal(10000, editor.SelectedStep!.TimeoutMs);
                Assert.Equal("10", view.StepDraftTimeout.Text);
                Assert.Equal(OpenVisionLanguageService.T("Sequence.StepTimeout") + " (s)", string.Concat(Glyphs(VisualTreeHelper.GetDrawing(caption)!).Select(glyph => new string(glyph.Characters?.ToArray() ?? []))));
                var valueBounds = Bounds(choice, window);
                var timeoutBounds = Bounds(view.StepDraftTimeout, window);
                Assert.Equal(valueBounds.Top, timeoutBounds.Top, 3);
                Assert.Equal(valueBounds.Width, timeoutBounds.Width, 3);
                Assert.Equal(9, timeoutBounds.Left - valueBounds.Right, 3);
                Assert.True(valueBounds.Width > 120 && valueBounds.Height >= 28);
                Assert.Contains(Glyphs(VisualTreeHelper.GetDrawing(caption)!), glyph => new string(glyph.Characters?.ToArray() ?? []).Contains("s", StringComparison.Ordinal) && glyph.FontRenderingEmSize == 11 && glyph.GlyphTypeface.Weight.ToOpenTypeWeight() == 400);
                Assert.Contains(Glyphs(VisualTreeHelper.GetDrawing(caption)!), glyph => glyph.FontRenderingEmSize == 12 && glyph.GlyphTypeface.Weight.ToOpenTypeWeight() == 400);
                var renderedValue = Descendants(view.StepDraftTimeout).OfType<Visual>().Select(VisualTreeHelper.GetDrawing).Where(drawing => drawing is not null).SelectMany(drawing => Glyphs(drawing!));
                Assert.Contains(renderedValue, glyph => new string(glyph.Characters?.ToArray() ?? []) == "10" && glyph.FontRenderingEmSize == 12 && glyph.GlyphTypeface.Weight.ToOpenTypeWeight() == 400);
                Capture(window, $"timeout-{width}-{language}-normal");
                editor.SelectedStepDraft!.TimeoutMs = 1250; Settle(window);
                Assert.Equal("1.25", view.StepDraftTimeout.Text);
                Assert.Equal(original, JsonSerializer.Serialize(project));
                editor.DiscardStepDraft(); Settle(window);
                view.StepDraftTimeout.Text = "2147483.647"; Settle(window);
                Assert.Equal(int.MaxValue, editor.SelectedStepDraft!.TimeoutMs);
                Assert.True(view.ApplyStepDraftButton.IsEnabled);
                foreach (string invalid in new[] { "", "NaN", "-0.001", "1.0001", "1.0000000000000000000000000000001", "2147483.648", "2147483647" })
                {
                    view.StepDraftTimeout.Text = invalid; Settle(window);
                    Assert.Equal(invalid, view.StepDraftTimeout.Text);
                    Assert.False(view.ApplyStepDraftButton.IsEnabled || editor.CanApplyStepDraft);
                    Assert.NotEmpty(editor.StepDraftErrorText);
                    Assert.True(view.StepDraftError.IsVisible);
                    Assert.False(view.StepValidationState.IsVisible);
                    Assert.Equal(original, JsonSerializer.Serialize(project));
                }
                Capture(window, $"timeout-{width}-{language}-error");
                view.StepDraftTimeout.Text = "0.001"; Settle(window);
                Assert.Equal(1, editor.SelectedStepDraft!.TimeoutMs);
                Assert.False(view.StepDraftError.IsVisible);
                Assert.True(view.StepValidationState.IsVisible);
                Assert.Equal(10000, editor.SelectedStep!.TimeoutMs);
                Assert.True(view.ApplyStepDraftButton.IsEnabled);
                editor.IsEditable = false; Settle(window);
                Assert.False(view.StepDraftTimeout.IsEnabled || view.ApplyStepDraftButton.IsEnabled);
                Assert.Equal("0.001", view.StepDraftTimeout.Text);
                Capture(window, $"timeout-{width}-{language}-readonly");
                editor.IsEditable = true;
                view.ApplyStepDraftButton.Command.Execute(null); Settle(window);
                Assert.Equal(1, editor.SelectedStep!.TimeoutMs);
                Assert.False(editor.HasPendingStepDraft || vm.IsRunning);
                Assert.True(vm.HasUnsavedChanges);
                var store = new ProjectDocumentStore();
                string saved = store.Save(project);
                string appliedProject = JsonSerializer.Serialize(project);
                Directory.CreateDirectory(EvidenceRoot());
                File.WriteAllText(Path.Combine(EvidenceRoot(), $"timeout-{width}-{language}.ovmachine"), saved);
                Assert.Equal(1, store.Load(saved).Sequences[0].Steps.Single(step => step.Id == "wait").TimeoutMs);
                view.StepDraftTimeout.Text = "2.5"; Settle(window);
                Assert.Equal(2500, editor.SelectedStepDraft!.TimeoutMs);
                Assert.Equal(1, editor.SelectedStep.TimeoutMs);
                editor.DiscardStepDraft(); Settle(window);
                Assert.Equal("0.001", view.StepDraftTimeout.Text);
                Assert.Equal(appliedProject, JsonSerializer.Serialize(project));
                Assert.Equal(saved, File.ReadAllText(Path.Combine(EvidenceRoot(), $"timeout-{width}-{language}.ovmachine")));
                editor.SelectedStep = editor.Steps.Single(step => step.Id == "delay"); Settle(window);
                Assert.Equal("0", view.StepDraftTimeout.Text);
                Assert.False(view.StepDraftTimeout.IsVisible);
                Assert.True(view.StepDraftParameter.IsVisible);
                editor.SelectedStep = editor.Steps.Single(step => step.Id == "move"); Settle(window);
                Assert.False(view.StepDraftTimeout.IsVisible);
                editor.SelectedStep = editor.Steps.Single(step => step.Id == "wait"); Settle(window);
                Capture(window, $"timeout-{width}-{language}-recovered");
                File.WriteAllText(Path.Combine(EvidenceRoot(), $"timeout-{width}-{language}-evidence.json"), JsonSerializer.Serialize(new { monitor, valueBounds, timeoutBounds, StoredTimeoutMs = editor.SelectedStep!.TimeoutMs, Display = view.StepDraftTimeout.Text, editor.HasPendingStepDraft, vm.IsRunning }));
                editor.SelectedStep = null; Settle(window);
                Assert.False(view.StepDraftFields.IsVisible || view.ApplyStepDraftButton.IsEnabled);
                return true;

                static IEnumerable<GlyphRun> Glyphs(Drawing drawing)
                {
                    if (drawing is GlyphRunDrawing glyph) yield return glyph.GlyphRun;
                    if (drawing is DrawingGroup group) foreach (Drawing child in group.Children) foreach (var run in Glyphs(child)) yield return run;
                }
            }
            finally { window.DataContext = null; window.Close(); OpenVisionLanguageService.SetLanguage(previousLanguage, save: false); }
        });
    }

    [Theory]
    [InlineData(OpenVisionLanguage.Korean)]
    [InlineData(OpenVisionLanguage.English)]
    public async Task InspectorRoleDescriptionsRespectAliasesAndLeaveActionNamesUnchanged(OpenVisionLanguage language)
    {
        await _ui.InvokeAsync(() =>
        {
            var previousLanguage = OpenVisionLanguageService.CurrentLanguage;
            OpenVisionLanguageService.SetLanguage(language, save: false);
            try
            {
                var converter = new OpenVisionLab.MachineStudio.Localization.LocalizedSequenceActionConverter();
                foreach (SequenceStepAction action in Enum.GetValues<SequenceStepAction>())
                {
                    string role = action == SequenceStepAction.SetChannel ? "SetSignal" : action == SequenceStepAction.None ? "Complete" : action == SequenceStepAction.Wait ? "WaitAxisDone" : action.ToString();
                    string expected = OpenVisionLanguageService.T($"R24.StepDescription.{role}");
                    Assert.NotEmpty(expected);
                    Assert.NotEqual($"R24.StepDescription.{role}", expected);
                    Assert.Equal(expected, converter.Convert([action, 0, false, ""], typeof(string), "description", System.Globalization.CultureInfo.InvariantCulture));
                    string actionName = OpenVisionLanguageService.T($"Sequence.Action.{action}", action.ToString(), action.ToString());
                    Assert.Equal(actionName, converter.Convert(action, typeof(string), null, System.Globalization.CultureInfo.InvariantCulture));
                    Assert.Equal(actionName, converter.Convert([action, 0], typeof(string), null, System.Globalization.CultureInfo.InvariantCulture));
                }
                foreach (string parameter in new[] { "", "axisDone", " AXISDONE " })
                    Assert.Equal(OpenVisionLanguageService.T("R24.StepDescription.WaitAxisDone"), converter.Convert([SequenceStepAction.Wait, 0, false, parameter], typeof(string), "description", System.Globalization.CultureInfo.InvariantCulture));
                foreach (string parameter in new[] { "true", "false", "1", "0" })
                    Assert.Equal(OpenVisionLanguageService.T("R24.StepDescription.WaitSignal"), converter.Convert([SequenceStepAction.Wait, 0, false, parameter], typeof(string), "description", System.Globalization.CultureInfo.InvariantCulture));
                Assert.Equal(OpenVisionLanguageService.T("R24.StepDescription.MoveAxisWait"), converter.Convert([SequenceStepAction.MoveAxis, 0, true, "2"], typeof(string), "description", System.Globalization.CultureInfo.InvariantCulture));
                Assert.Equal(string.Empty, converter.Convert([], typeof(string), "description", System.Globalization.CultureInfo.InvariantCulture));
                return true;
            }
            finally { OpenVisionLanguageService.SetLanguage(previousLanguage, save: false); }
        });
    }

    [Theory]
    [InlineData(1280, 900, OpenVisionLanguage.Korean)]
    [InlineData(1280, 900, OpenVisionLanguage.English)]
    [InlineData(1920, 1032, OpenVisionLanguage.Korean)]
    [InlineData(1920, 1032, OpenVisionLanguage.English)]
    public async Task InspectorHeaderShowsAppliedLongIdentityAndChangesOnlyAfterExplicitApply(double width, double height, OpenVisionLanguage language)
    {
        await _ui.InvokeAsync(() =>
        {
            var previousLanguage = OpenVisionLanguageService.CurrentLanguage;
            OpenVisionLanguageService.SetLanguage(language, save: false);
            var project = CreateProject();
            project.Stations = [new MachineStationDefinition
            {
                Id = "station", Name = "Station",
                Units = [
                    new MachineUnitDefinition { Id = "unit", Name = "Current unit" },
                    new MachineUnitDefinition { Id = "destination", Name = "Destination unit" }
                ]
            }];
            project.Sequences[0].UnitId = "unit";
            var step = project.Sequences[0].Steps[0];
            step.Id = "stable-step-identity-" + new string('I', 70);
            step.Name = language == OpenVisionLanguage.Korean
                ? "제품 이송 · 이송 장비 식별 정보와 다음 검사 위치를 포함하는 긴 스텝 이름의 전체 표시 확인"
                : "Product transfer with a long equipment identity and the next inspection position for a complete step name";
            step.Action = SequenceStepAction.TransferUnitWorkpiece;
            step.TargetId = "conveyor"; step.DestinationUnitId = "destination"; step.Parameter = "1.5"; step.TimeoutMs = 10000;
            project.Channels.AddRange([
                new OpenVisionLab.Machine.Core.Channels.ChannelDefinition { Id = "conveyor.run", Kind = OpenVisionLab.Machine.Core.Channels.ChannelKind.DigitalOutput },
                new OpenVisionLab.Machine.Core.Channels.ChannelDefinition { Id = "conveyor.reverse", Kind = OpenVisionLab.Machine.Core.Channels.ChannelKind.DigitalOutput }
            ]);
            project.Devices.Add(new OpenVisionLab.Machine.Core.Devices.DeviceDefinition
            {
                Id = "conveyor.device", Name = "Conveyor", Kind = OpenVisionLab.Machine.Core.Devices.DeviceKind.Conveyor,
                ChannelIds = ["conveyor.run", "conveyor.reverse"],
                Conveyor = new()
                {
                    RunCommandChannelId = "conveyor.run", ReverseCommandChannelId = "conveyor.reverse"
                }
            });
            project.Layouts[0].Components.Add(new LayoutComponentDefinition { Id = "conveyor", Name = "Conveyor", Kind = LayoutComponentKind.Conveyor, UnitId = "unit", BehaviorBindingId = "conveyor.device" });
            using var vm = new MainViewModel(project);
            vm.PlacementDraftPrompt = () => PlacementDraftDecision.Cancel;
            vm.Navigation.HideStartupChoice();
            var window = new ShellWindow { DataContext = vm, Width = width, Height = height, ShowInTaskbar = false, ShowActivated = false };
            SmokeDpiTestHook.PlaceOnTestMonitor(window, (int)width, (int)height);
            window.Show();
            try
            {
                vm.Navigation.OpenSequenceEditorCommand.Execute(null); Settle(window);
                var view = Descendants(window).OfType<SequenceStepInspectorView>().Single(control => control.IsVisible);
                var name = (TextBlock)view.FindName("StepIdentityName");
                var action = (TextBlock)view.FindName("StepIdentityAction");
                var identity = (TextBlock)view.FindName("StepIdentityId");
                var description = (TextBlock)view.FindName("StepRoleDescription");
                var header = (Border)view.FindName("StepIdentityHeader");
                var editor = vm.SequenceEditor;
                var applied = editor.SelectedStep!;
                string original = JsonSerializer.Serialize(project);
                Assert.NotNull(vm.SceneSnapshots.Latest);
                var clock = vm.SceneSnapshots.Latest!.SimulationTime;
                Assert.Same(editor, view.DataContext);
                Assert.Equal(applied.DisplayName, name.Text);
                Assert.Equal(applied.Id, identity.Text);
                Assert.Equal(OpenVisionLanguageService.T("Sequence.Action.TransferUnitWorkpiece"), action.Text);
                Assert.Equal(OpenVisionLanguageService.T("R24.StepDescription.TransferUnitWorkpiece"), description.Text);
                Assert.True(name.ActualHeight > name.LineHeight && identity.ActualHeight > identity.LineHeight);
                Assert.Equal(TextWrapping.Wrap, name.TextWrapping);
                foreach (var text in new[] { name, identity, action, description }) Assert.True(Bounds(header, window).Contains(Bounds(text, window)));
                Assert.True(Bounds(name, window).Bottom <= Bounds(identity, window).Top || Bounds(name, window).Right <= Bounds(identity, window).Left);
                Assert.True(Bounds(view, window).Contains(Bounds(view.ApplyStepDraftButton, window)));
                Assert.True(view.StepInspectorScrollViewer.ViewportHeight > 0);
                Capture(window, $"header-{width}-{language}-applied-long-name-id-action");
                view.StepDraftName.Text = "Explicit draft";
                view.StepDraftAction.SetCurrentValue(ComboBox.SelectedItemProperty, SequenceStepAction.Delay);
                view.StepDraftParameter.Text = "1.5"; Settle(window);
                Assert.Equal(applied.DisplayName, name.Text);
                Assert.Equal(OpenVisionLanguageService.T("Sequence.Action.TransferUnitWorkpiece"), action.Text);
                Assert.Equal(OpenVisionLanguageService.T("R24.StepDescription.TransferUnitWorkpiece"), description.Text);
                Assert.True(editor.HasPendingStepDraft);
                Assert.Equal(original, JsonSerializer.Serialize(project));
                Assert.False(vm.HasUnsavedChanges || vm.IsRunning);
                Capture(window, $"header-{width}-{language}-unapplied-header-unchanged");
                Assert.True(view.ApplyStepDraftButton.IsEnabled);
                ((System.Windows.Automation.Provider.IInvokeProvider)new System.Windows.Automation.Peers.ButtonAutomationPeer(view.ApplyStepDraftButton).GetPattern(System.Windows.Automation.Peers.PatternInterface.Invoke)!).Invoke(); Settle(window);
                Assert.False(editor.HasPendingStepDraft);
                Assert.Equal("Explicit draft", name.Text);
                Assert.Equal(language == OpenVisionLanguage.Korean ? "딜레이" : "Delay", action.Text);
                Assert.Equal(language == OpenVisionLanguage.Korean ? "지정한 시뮬레이션 시간만큼 기다립니다." : "Wait for the specified simulation time.", description.Text);
                Assert.NotEqual(original, JsonSerializer.Serialize(project));
                Assert.True(vm.HasUnsavedChanges);
                string savedApplied = JsonSerializer.Serialize(project);
                view.StepDraftName.Text = "Discard this draft"; Settle(window);
                Assert.Equal("Explicit draft", name.Text);
                ((System.Windows.Automation.Provider.IInvokeProvider)new System.Windows.Automation.Peers.ButtonAutomationPeer(view.DiscardStepDraftButton).GetPattern(System.Windows.Automation.Peers.PatternInterface.Invoke)!).Invoke(); Settle(window);
                Assert.False(editor.HasPendingStepDraft);
                Assert.Equal("Explicit draft", name.Text);
                Assert.Equal(savedApplied, JsonSerializer.Serialize(project));
                var otherLanguage = language == OpenVisionLanguage.Korean ? OpenVisionLanguage.English : OpenVisionLanguage.Korean;
                OpenVisionLanguageService.SetLanguage(otherLanguage, save: false); Settle(window);
                Assert.Equal(otherLanguage == OpenVisionLanguage.Korean ? "딜레이" : "Delay", action.Text);
                Assert.Equal(otherLanguage == OpenVisionLanguage.Korean ? "지정한 시뮬레이션 시간만큼 기다립니다." : "Wait for the specified simulation time.", description.Text);
                Assert.Equal(savedApplied, JsonSerializer.Serialize(project));
                Assert.Equal(clock, vm.SceneSnapshots.Latest!.SimulationTime);
                Assert.False(vm.IsRunning);
                view.StepInspectorScrollViewer.ScrollToBottom(); Settle(window);
                Assert.True(Bounds(view, window).Contains(Bounds(view.ApplyStepDraftButton, window)));
                Capture(window, $"header-{width}-{language}-applied-discard-language-restored");
                return true;
            }
            finally { window.DataContext = null; window.Close(); OpenVisionLanguageService.SetLanguage(previousLanguage, save: false); }
        });
    }

    [Theory]
    [InlineData(1920, 1032, OpenVisionLanguage.Korean)]
    [InlineData(1920, 1032, OpenVisionLanguage.English)]
    [InlineData(1280, 900, OpenVisionLanguage.Korean)]
    [InlineData(1280, 900, OpenVisionLanguage.English)]
    public async Task InspectorBasicAdditionalSettingsKeepExplicitWritersAndProductBranchRoutes(double width, double height, OpenVisionLanguage language)
    {
        await _ui.InvokeAsync(() =>
        {
            var previousLanguage = OpenVisionLanguageService.CurrentLanguage;
            OpenVisionLanguageService.SetLanguage(language, save: false);
            var project = LoadInspectorRecipe();
            project.Stations[0].Units.Add(new MachineUnitDefinition { Id = "U-02", Name = "다음 검사 유닛" });
            var sequence = project.Sequences.Single(sequence => sequence.Id == "owned-reference-sequence");
            using var vm = new MainViewModel(project);
            vm.PlacementDraftPrompt = () => PlacementDraftDecision.Cancel;
            vm.Navigation.HideStartupChoice();
            var window = new ShellWindow { DataContext = vm, Width = width, Height = height, ShowInTaskbar = false, ShowActivated = false };
            SmokeDpiTestHook.PlaceOnTestMonitor(window, (int)width, (int)height);
            window.Show();
            try
            {
                vm.Navigation.OpenSequenceEditorCommand.Execute(null);
                var editor = vm.SequenceEditor;
                editor.SelectedSequence = sequence;
                editor.SelectedStep = editor.Steps.Single(step => step.Id == "S-03");
                Settle(window);
                var view = Descendants(window).OfType<SequenceStepInspectorView>().Single(control => control.IsVisible);
                var additional = (Expander)view.FindName("StepAdditionalSettings");
                var alarm = (ComboBox)view.FindName("StepDraftAlarm");
                var badge = (TextBlock)view.FindName("StepAdditionalBadge");
                var failure = (ComboBox)view.FindName("StepDraftFailure");
                var check = (CheckBox)view.FindName("StepExpectedCheck");
                var before = JsonSerializer.Serialize(project);
                var clock = vm.SceneSnapshots.Latest!.SimulationTime;
                Assert.False(additional.IsExpanded);
                Assert.Equal(96, VisualTreeHelper.GetDpi(window).PixelsPerInchX);
                var monitor = SmokeDpiTestHook.CaptureMonitorEvidence(window);
                Assert.True(monitor.WindowIntersectsMonitor);
                File.WriteAllText(Path.Combine(EvidenceRoot(), $"inspector-basic-monitor-{width}-{language}.json"), JsonSerializer.Serialize(monitor));
                Assert.Equal(view.StepDraftTimeout.ActualWidth, ((ComboBox)view.FindName("StepSignalChoice")).ActualWidth, 3);
                Assert.Equal(9, Bounds(view.StepDraftTimeout, view).Left - Bounds((ComboBox)view.FindName("StepSignalChoice"), view).Right, 3);
                Assert.False(failure.IsVisible);
                var expansion = (System.Windows.Automation.Provider.IExpandCollapseProvider)new System.Windows.Automation.Peers.ExpanderAutomationPeer(additional).GetPattern(System.Windows.Automation.Peers.PatternInterface.ExpandCollapse);
                expansion.Expand(); Settle(window);
                alarm.SetCurrentValue(System.Windows.Controls.Primitives.Selector.SelectedItemProperty, editor.StepAlarmOptions.Single(option => option.Key == "S-06"));
                Settle(window);
                Assert.Equal("S-06", editor.SelectedStepDraft!.ErrorStepId);
                Assert.False(((StackPanel)view.FindName("StepExpectedInputs")).IsVisible);
                Assert.Equal(OpenVisionLanguageService.T("R24.Inspector.Configured"), badge.Text);
                expansion.Collapse(); expansion.Expand(); Settle(window);
                Assert.Equal("S-06", editor.SelectedStepDraft.ErrorStepId);
                Assert.Equal(before, JsonSerializer.Serialize(project));
                view.StepDraftTimeout.SetCurrentValue(TextBox.TextProperty, "wrong-time"); Settle(window);
                Assert.False(view.ApplyStepDraftButton.IsEnabled);
                Assert.Contains(OpenVisionLanguageService.T("R24.Inspector.InvalidTimeout"), ((TextBlock)view.FindName("StepTimeoutHint")).Text);
                view.StepDraftTimeout.SetCurrentValue(TextBox.TextProperty, "8.125"); Settle(window);
                Assert.Equal(8125, editor.SelectedStepDraft.TimeoutMs);
                ((System.Windows.Automation.Provider.IToggleProvider)new System.Windows.Automation.Peers.CheckBoxAutomationPeer(check).GetPattern(System.Windows.Automation.Peers.PatternInterface.Toggle)).Toggle();
                editor.SelectedStepDraft.ExpectedState = "idle"; Settle(window);
                Assert.True(((StackPanel)view.FindName("StepExpectedInputs")).IsVisible);
                check.BringIntoView(); Settle(window);
                Assert.True(check.Focus()); Settle(window);
                Assert.True(check.IsKeyboardFocused);
                Capture(window, $"inspector-check-{width}-{language}-focused-checked");
                Assert.True(check.MoveFocus(new System.Windows.Input.TraversalRequest(System.Windows.Input.FocusNavigationDirection.Next))); Settle(window);
                Assert.True(((ComboBox)view.FindName("StepExpectedTarget")).IsKeyboardFocusWithin);
                ((System.Windows.Automation.Provider.IToggleProvider)new System.Windows.Automation.Peers.CheckBoxAutomationPeer(check).GetPattern(System.Windows.Automation.Peers.PatternInterface.Toggle)).Toggle(); Settle(window);
                Assert.False(((StackPanel)view.FindName("StepExpectedInputs")).IsVisible);
                Assert.False(((ComboBox)view.FindName("StepExpectedTarget")).IsKeyboardFocusWithin);
                Capture(window, $"inspector-check-{width}-{language}-unchecked");
                ((System.Windows.Automation.Provider.IToggleProvider)new System.Windows.Automation.Peers.CheckBoxAutomationPeer(check).GetPattern(System.Windows.Automation.Peers.PatternInterface.Toggle)).Toggle();
                editor.SelectedStepDraft.ExpectedState = "idle"; Settle(window);
                Assert.True(((StackPanel)view.FindName("StepExpectedInputs")).IsVisible);
                Assert.Equal("idle", ((ComboBox)view.FindName("StepExpectedState")).SelectedItem);
                Assert.Equal(before, JsonSerializer.Serialize(project));
                view.StepDraftAction.SetCurrentValue(System.Windows.Controls.Primitives.Selector.SelectedItemProperty, SequenceStepAction.WaitVisionResult); Settle(window);
                Assert.Contains(Descendants(view.StepDraftTarget).OfType<TextBlock>(), text => text.IsVisible && text.Text == editor.SelectedStepDraft!.InspectorTargets.Single(target => target.Id == editor.SelectedStepDraft.TargetId).Name);
                AssertInspectorPopupNames(window, view.StepDraftTarget, editor.SelectedStepDraft!.InspectorTargets.Select(target => target.Name).ToArray(), $"inspector-target-popup-{width}-{language}");
                Assert.True(failure.IsVisible);
                Assert.Equal(OpenVisionLanguageService.T("R24.Inspector.OnOK"), ((TextBlock)view.FindName("StepNextCaption")).Text);
                failure.SetCurrentValue(System.Windows.Controls.Primitives.Selector.SelectedItemProperty, editor.StepFailureOptions.Single(option => option.Key == "")); Settle(window);
                Assert.Equal("", editor.SelectedStepDraft.FailureStepId);
                Assert.False(view.ApplyStepDraftButton.IsEnabled);
                failure.SetCurrentValue(System.Windows.Controls.Primitives.Selector.SelectedItemProperty, editor.StepFailureOptions.Single(option => option.Key == "S-04")); Settle(window);
                Assert.Equal("S-04", editor.SelectedStepDraft.FailureStepId);
                Assert.Equal(SequenceStepAction.WaitSignal, editor.SelectedStep!.Action);
                Assert.True(view.ApplyStepDraftButton.IsEnabled);
                Capture(window, $"inspector-basic-{width}-{language}-draft-vision");
                ((System.Windows.Automation.Provider.IInvokeProvider)new System.Windows.Automation.Peers.ButtonAutomationPeer(view.ApplyStepDraftButton).GetPattern(System.Windows.Automation.Peers.PatternInterface.Invoke)).Invoke(); Settle(window);
                Assert.Equal(SequenceStepAction.WaitVisionResult, editor.SelectedStep.Action);
                Assert.Equal(8125, editor.SelectedStep.TimeoutMs);
                Assert.Equal("S-04", editor.SelectedStep.FailureStepId);
                Assert.Equal("idle", editor.SelectedStep.ExpectedState);
                var applied = JsonSerializer.Serialize(project);
                view.StepDraftAction.SetCurrentValue(System.Windows.Controls.Primitives.Selector.SelectedItemProperty, SequenceStepAction.BranchInspection); Settle(window);
                Assert.True(failure.IsVisible);
                Assert.Equal("S-04", editor.SelectedStepDraft!.FailureStepId);
                Assert.Equal(editor.SelectedStep.NextStepId, editor.SelectedStepDraft.NextStepId);
                Assert.Contains(Descendants(view.StepDraftTarget).OfType<TextBlock>(), text => text.IsVisible && text.Text == editor.SelectedStepDraft.InspectorTargets.Single(target => target.Id == editor.SelectedStepDraft.TargetId).Name);
                failure.SetCurrentValue(System.Windows.Controls.Primitives.Selector.SelectedItemProperty, editor.StepFailureOptions.Single(option => option.Key == "")); Settle(window);
                Assert.False(view.ApplyStepDraftButton.IsEnabled);
                failure.SetCurrentValue(System.Windows.Controls.Primitives.Selector.SelectedItemProperty, editor.StepFailureOptions.Single(option => option.Key == "S-04")); Settle(window);
                Assert.True(view.ApplyStepDraftButton.IsEnabled);
                Assert.Equal(applied, JsonSerializer.Serialize(project));
                ((System.Windows.Automation.Provider.IInvokeProvider)new System.Windows.Automation.Peers.ButtonAutomationPeer(view.DiscardStepDraftButton).GetPattern(System.Windows.Automation.Peers.PatternInterface.Invoke)).Invoke(); Settle(window);
                Assert.Equal(SequenceStepAction.WaitVisionResult, editor.SelectedStepDraft.Action);
                Assert.Equal(applied, JsonSerializer.Serialize(project));
                editor.IsEditable = false; Settle(window);
                Assert.False(view.StepDraftFields.IsEnabled || check.IsEnabled || alarm.IsEnabled || view.ApplyStepDraftButton.IsEnabled);
                Assert.True(((StackPanel)view.FindName("StepExpectedInputs")).IsVisible);
                Capture(window, $"inspector-basic-{width}-{language}-readonly");
                editor.IsEditable = true; Settle(window);
                Assert.True(view.StepDraftFields.IsEnabled);
                view.StepInspectorScrollViewer.ScrollToBottom(); Settle(window);
                Capture(window, $"inspector-basic-{width}-{language}-scroll-bottom");
                Assert.True(Bounds(view, window).Contains(Bounds(view.ApplyStepDraftButton, window)));
                view.StepDraftAction.SetCurrentValue(System.Windows.Controls.Primitives.Selector.SelectedItemProperty, SequenceStepAction.TriggerCamera); Settle(window);
                editor.SelectedStepDraft!.WorkpieceComponentId = "unknown-workpiece"; Settle(window);
                var workpiece = Descendants(view).OfType<ComboBox>().Single(combo => System.Windows.Data.BindingOperations.GetBindingExpression(combo, System.Windows.Controls.Primitives.Selector.SelectedValueProperty)?.ParentBinding.Path.Path == "SelectedStepDraft.WorkpieceComponentId");
                Assert.True(workpiece.IsVisible);
                Assert.Equal("unknown-workpiece", workpiece.SelectedValue);
                AssertInspectorPopupNames(window, workpiece, editor.SelectedStepDraft.InspectorWorkpieceTargets.Select(target => target.Name).ToArray(), $"inspector-workpiece-popup-{width}-{language}");
                Assert.Contains(Descendants(workpiece).OfType<TextBlock>(), text => text.IsVisible && text.Text == "unknown-workpiece");
                workpiece.SetCurrentValue(System.Windows.Controls.Primitives.Selector.SelectedValueProperty, null); Settle(window);
                Assert.Equal("unknown-workpiece", editor.SelectedStepDraft.WorkpieceComponentId);
                workpiece.SetCurrentValue(System.Windows.Controls.Primitives.Selector.SelectedValueProperty, editor.SelectedStepDraft.AvailableWorkpieceTargets.First().Id); Settle(window);
                Assert.Equal(editor.SelectedStepDraft.AvailableWorkpieceTargets.First().Id, editor.SelectedStepDraft.WorkpieceComponentId);
                editor.DiscardStepDraft();
                view.StepDraftAction.SetCurrentValue(System.Windows.Controls.Primitives.Selector.SelectedItemProperty, SequenceStepAction.TransferUnitWorkpiece); Settle(window);
                editor.SelectedStepDraft!.DestinationUnitId = "unknown-unit"; Settle(window);
                var destination = Descendants(view).OfType<ComboBox>().Single(combo => System.Windows.Data.BindingOperations.GetBindingExpression(combo, System.Windows.Controls.Primitives.Selector.SelectedValueProperty)?.ParentBinding.Path.Path == "SelectedStepDraft.DestinationUnitId");
                Assert.True(destination.IsVisible);
                Assert.Equal("unknown-unit", destination.SelectedValue);
                AssertInspectorPopupNames(window, destination, editor.SelectedStepDraft.InspectorDestinationUnits.Select(target => target.Name).ToArray(), $"inspector-destination-popup-{width}-{language}");
                Assert.Contains(Descendants(destination).OfType<TextBlock>(), text => text.IsVisible && text.Text == "unknown-unit");
                destination.SetCurrentValue(System.Windows.Controls.Primitives.Selector.SelectedValueProperty, null); Settle(window);
                Assert.Equal("unknown-unit", editor.SelectedStepDraft.DestinationUnitId);
                destination.SetCurrentValue(System.Windows.Controls.Primitives.Selector.SelectedValueProperty, editor.SelectedStepDraft.AvailableDestinationUnits.First().Id); Settle(window);
                Assert.Equal(editor.SelectedStepDraft.AvailableDestinationUnits.First().Id, editor.SelectedStepDraft.DestinationUnitId);
                editor.DiscardStepDraft();
                Assert.Equal(applied, JsonSerializer.Serialize(project));
                Assert.False(vm.IsRunning);
                Assert.Equal(clock, vm.SceneSnapshots.Latest!.SimulationTime);
                return true;
            }
            finally { window.DataContext = null; window.Close(); OpenVisionLanguageService.SetLanguage(previousLanguage, save: false); }
        });
    }

    [Theory]
    [InlineData(1920, 1032, OpenVisionLanguage.Korean)]
    [InlineData(1920, 1032, OpenVisionLanguage.English)]
    [InlineData(1280, 900, OpenVisionLanguage.Korean)]
    [InlineData(1280, 900, OpenVisionLanguage.English)]
    public async Task InspectorUnknownStoredValuesSurviveViewingSaveReopenAndExplicitRepair(double width, double height, OpenVisionLanguage language)
    {
        await _ui.InvokeTaskAsync(async () =>
        {
            var previousLanguage = OpenVisionLanguageService.CurrentLanguage;
            OpenVisionLanguageService.SetLanguage(language, save: false);
            var project = LoadInspectorRecipe();
            var sequence = project.Sequences.Single(sequence => sequence.Id == "owned-reference-sequence");
            using var vm = new MainViewModel(project);
            var definition = sequence.Steps.Single(step => step.Id == "S-03");
            definition.TargetId = "unknown-signal"; definition.Parameter = "MALFORMED"; definition.TimeoutMs = -1500;
            definition.NextStepId = "unknown-next"; definition.ErrorStepId = "unknown-alarm"; definition.FailureStepId = "unknown-ng";
            definition.ExpectedTargetId = "unknown-checkpoint"; definition.ExpectedState = "idle";
            vm.SequenceEditor.Load(project);
            vm.PlacementDraftPrompt = () => PlacementDraftDecision.Cancel;
            vm.Navigation.HideStartupChoice();
            var window = new ShellWindow { DataContext = vm, Width = width, Height = height, ShowInTaskbar = false, ShowActivated = false };
            SmokeDpiTestHook.PlaceOnTestMonitor(window, (int)width, (int)height);
            window.Show();
            try
            {
                vm.Navigation.OpenSequenceEditorCommand.Execute(null);
                var editor = vm.SequenceEditor;
                editor.SelectedSequence = sequence;
                editor.SelectedStep = editor.Steps.Single(step => step.Id == "S-03"); Settle(window);
                var view = Descendants(window).OfType<SequenceStepInspectorView>().Single(control => control.IsVisible);
                var additional = (Expander)view.FindName("StepAdditionalSettings");
                var state = (ComboBox)view.FindName("StepExpectedState");
                var target = (ComboBox)view.FindName("StepExpectedTarget");
                var legacy = (TextBox)view.FindName("StepLegacyFailure");
                var before = JsonSerializer.Serialize(project);
                Assert.True(additional.IsExpanded);
                Assert.Equal("unknown-signal", view.StepDraftTarget.SelectedValue);
                Assert.Contains(Descendants(view.StepDraftTarget).OfType<TextBlock>(), text => text.IsVisible && text.Text == "unknown-signal");
                Assert.Equal("MALFORMED", ((ComboBox)view.FindName("StepSignalChoice")).SelectedItem);
                Assert.Equal("unknown-checkpoint", target.SelectedValue);
                Assert.Equal("idle", state.SelectedItem);
                Assert.Equal("unknown-ng", legacy.Text);
                Assert.Equal("-1.5", view.StepDraftTimeout.Text);
                Assert.False(editor.HasPendingStepDraft || vm.HasUnsavedChanges || vm.IsRunning);
                additional.IsExpanded = false; Settle(window); additional.IsExpanded = true; Settle(window);
                Assert.Equal(before, JsonSerializer.Serialize(project));
                Assert.Equal("idle", editor.SelectedStepDraft!.ExpectedState);
                Capture(window, $"inspector-unknown-{width}-{language}-stored");
                string file = Path.Combine(EvidenceRoot(), $"inspector-roundtrip-{width}-{language}.ovmachine");
                await vm.SaveProjectAsync(file);
                byte[] saved = await File.ReadAllBytesAsync(file);
                var reopened = await new ProjectDocumentFileStore().LoadAsync(file);
                var reopenedStep = reopened.Sequences.Single(sequence => sequence.Id == "owned-reference-sequence").Steps.Single(step => step.Id == "S-03");
                Assert.Equal("unknown-signal", reopenedStep.TargetId); Assert.Equal("MALFORMED", reopenedStep.Parameter);
                Assert.Equal(-1500, reopenedStep.TimeoutMs); Assert.Equal("unknown-next", reopenedStep.NextStepId);
                Assert.Equal("unknown-alarm", reopenedStep.ErrorStepId); Assert.Equal("unknown-ng", reopenedStep.FailureStepId);
                Assert.Equal("unknown-checkpoint", reopenedStep.ExpectedTargetId); Assert.Equal("idle", reopenedStep.ExpectedState);
                var retainedProject = LoadInspectorRecipe();
                using (var validMain = new MainViewModel(retainedProject))
                {
                    string rejection = "";
                    validMain.ProjectOpenFailurePresenter = detail => rejection = detail;
                    string retained = JsonSerializer.Serialize(retainedProject);
                    Assert.False(await validMain.OpenProjectAsync(file));
                    Assert.Contains("UnknownSignal", rejection);
                    Assert.Equal(retained, JsonSerializer.Serialize(retainedProject));
                    Assert.Equal(saved, await File.ReadAllBytesAsync(file));
                    Assert.False(validMain.IsRunning || validMain.HasUnsavedChanges);
                    File.WriteAllText(Path.Combine(EvidenceRoot(), $"inspector-invalid-open-{width}-{language}.json"), JsonSerializer.Serialize(new { Entry = "Main OpenProjectAsync -> ProjectLifecycleCoordinator -> ProjectRuntimeApplicationWorkflow", Accepted = false, Rejection = rejection, RetainedRecipe = true, SavedFileUnchanged = true, Boundary = "Compiler/runtime owner unchanged; malformed file cannot reach the Main Inspector through current user Open path" }));
                }
                // The Main startup gate rejects malformed compilation input. Exercise the existing editor Load owner on the same isolated project instead.
                project.Sequences.Clear(); project.Sequences.AddRange(reopened.Sequences);
                editor.Load(project);
                editor.SelectedSequence = project.Sequences.Single(sequence => sequence.Id == "owned-reference-sequence");
                editor.SelectedStep = editor.Steps.Single(step => step.Id == "S-03"); Settle(window);
                definition = project.Sequences.Single(sequence => sequence.Id == "owned-reference-sequence").Steps.Single(step => step.Id == "S-03");
                Assert.Equal("idle", ((ComboBox)view.FindName("StepExpectedState")).SelectedItem);
                Assert.Equal("unknown-signal", view.StepDraftTarget.SelectedValue);
                Assert.Contains(Descendants(view.StepDraftTarget).OfType<TextBlock>(), text => text.IsVisible && text.Text == "unknown-signal");
                Assert.False(editor.HasPendingStepDraft || vm.HasUnsavedChanges || vm.IsRunning);
                Assert.Equal(saved, await File.ReadAllBytesAsync(file));
                view = Descendants(window).OfType<SequenceStepInspectorView>().Single(control => control.IsVisible);
                ((Expander)view.FindName("StepAdditionalSettings")).IsExpanded = true; Settle(window);
                view.StepDraftTarget.SetCurrentValue(System.Windows.Controls.Primitives.Selector.SelectedItemProperty, editor.SelectedStepDraft.InspectorTargets.Single(target => target.Id == "SEN-01.det"));
                ((ComboBox)view.FindName("StepSignalChoice")).SetCurrentValue(System.Windows.Controls.Primitives.Selector.SelectedItemProperty, "true");
                view.StepDraftTimeout.SetCurrentValue(TextBox.TextProperty, "8.125");
                ((ComboBox)view.FindName("StepDraftNext")).SetCurrentValue(System.Windows.Controls.Primitives.Selector.SelectedItemProperty, editor.StepNextStepOptions.Single(option => option.Key == "S-06"));
                ((ComboBox)view.FindName("StepDraftAlarm")).SetCurrentValue(System.Windows.Controls.Primitives.Selector.SelectedItemProperty, editor.StepAlarmOptions.Single(option => option.Key == "S-06"));
                ((TextBox)view.FindName("StepLegacyFailure")).SetCurrentValue(TextBox.TextProperty, "");
                ((ComboBox)view.FindName("StepExpectedTarget")).SetCurrentValue(System.Windows.Controls.Primitives.Selector.SelectedItemProperty, editor.SelectedStepDraft.InspectorExpectedTargets.Single(target => target.Id == "AX-01")); Settle(window);
                Assert.Equal("idle", editor.SelectedStepDraft.ExpectedState);
                Assert.Equal("idle", ((ComboBox)view.FindName("StepExpectedState")).SelectedItem);
                Assert.Equal(saved, await File.ReadAllBytesAsync(file));
                Assert.True(view.ApplyStepDraftButton.IsEnabled);
                ((System.Windows.Automation.Provider.IInvokeProvider)new System.Windows.Automation.Peers.ButtonAutomationPeer(view.ApplyStepDraftButton).GetPattern(System.Windows.Automation.Peers.PatternInterface.Invoke)).Invoke(); Settle(window);
                Assert.Equal(8125, definition.TimeoutMs); Assert.Null(definition.FailureStepId);
                Assert.Equal(saved, await File.ReadAllBytesAsync(file));
                await vm.SaveProjectAsync(file);
                var repaired = await new ProjectDocumentFileStore().LoadAsync(file);
                var repairedStep = repaired.Sequences.Single(sequence => sequence.Id == "owned-reference-sequence").Steps.Single(step => step.Id == "S-03");
                Assert.Equal("true", repairedStep.Parameter); Assert.Equal(8125, repairedStep.TimeoutMs);
                Assert.Equal("SEN-01.det", repairedStep.TargetId); Assert.Equal("S-06", repairedStep.NextStepId);
                Assert.Equal("S-06", repairedStep.ErrorStepId); Assert.Null(repairedStep.FailureStepId);
                Assert.Equal("AX-01", repairedStep.ExpectedTargetId); Assert.Equal("idle", repairedStep.ExpectedState);
                vm.ProjectOpenFailurePresenter = detail => throw new InvalidOperationException(detail);
                Assert.True(await vm.OpenProjectAsync(file));
                editor.SelectedSequence = editor.Sequences.Single(sequence => sequence.Id == "owned-reference-sequence");
                editor.SelectedStep = editor.Steps.Single(step => step.Id == "S-03"); Settle(window);
                Assert.Equal("idle", ((ComboBox)view.FindName("StepExpectedState")).SelectedItem);
                Assert.Equal(8125, editor.SelectedStepDraft!.TimeoutMs);
                Assert.False(vm.IsRunning || vm.HasUnsavedChanges || editor.HasPendingStepDraft);
                using var reopenedMain = new MainViewModel(repaired);
                reopenedMain.SequenceEditor.SelectedSequence = repaired.Sequences.Single(sequence => sequence.Id == "owned-reference-sequence");
                reopenedMain.SequenceEditor.SelectedStep = reopenedMain.SequenceEditor.Steps.Single(step => step.Id == "S-03");
                Assert.Equal("idle", reopenedMain.SequenceEditor.SelectedStepDraft!.ExpectedState);
                Assert.False(reopenedMain.IsRunning || reopenedMain.HasUnsavedChanges);
                Assert.False(vm.IsRunning || editor.HasPendingStepDraft || vm.HasUnsavedChanges);
                Capture(window, $"inspector-unknown-{width}-{language}-repaired");
                var checkpoint = (ComboBox)view.FindName("StepExpectedTarget");
                var expectedState = (ComboBox)view.FindName("StepExpectedState");
                checkpoint.SetCurrentValue(System.Windows.Controls.Primitives.Selector.SelectedValueProperty, "CV-01"); Settle(window);
                Assert.Equal("CV-01", editor.SelectedStepDraft!.ExpectedTargetId);
                Assert.Equal("Stopped", editor.SelectedStepDraft.ExpectedState);
                Assert.True(expectedState.SelectedIndex >= 0);
                Assert.Equal("Stopped", expectedState.SelectedItem);
                Assert.Contains(Descendants(expectedState).OfType<TextBlock>(), text => text.IsVisible && text.Text == "Stopped");
                AssertInspectorPopupNames(window, checkpoint, editor.SelectedStepDraft.InspectorExpectedTargets.Select(target => target.Name).ToArray(), $"inspector-checkpoint-popup-{width}-{language}");
                AssertInspectorPopupNames(window, expectedState, ["Stopped", "ForwardRunning", "ReverseRunning"], $"inspector-state-popup-{width}-{language}");
                Assert.True(view.ApplyStepDraftButton.IsEnabled);
                ((System.Windows.Automation.Provider.IInvokeProvider)new System.Windows.Automation.Peers.ButtonAutomationPeer(view.ApplyStepDraftButton).GetPattern(System.Windows.Automation.Peers.PatternInterface.Invoke)).Invoke(); Settle(window);
                Assert.Equal("CV-01", editor.SelectedStep!.ExpectedTargetId); Assert.Equal("Stopped", editor.SelectedStep.ExpectedState);
                await vm.SaveProjectAsync(file);
                var conveyorCheckpoint = await new ProjectDocumentFileStore().LoadAsync(file);
                var conveyorStep = conveyorCheckpoint.Sequences.Single(sequence => sequence.Id == "owned-reference-sequence").Steps.Single(step => step.Id == "S-03");
                Assert.Equal("CV-01", conveyorStep.ExpectedTargetId); Assert.Equal("Stopped", conveyorStep.ExpectedState);
                Assert.True(await vm.OpenProjectAsync(file));
                editor.SelectedSequence = editor.Sequences.Single(sequence => sequence.Id == "owned-reference-sequence");
                editor.SelectedStep = editor.Steps.Single(step => step.Id == "S-03"); Settle(window);
                Assert.Equal("Stopped", ((ComboBox)view.FindName("StepExpectedState")).SelectedItem);
                Assert.Contains(Descendants((ComboBox)view.FindName("StepExpectedState")).OfType<TextBlock>(), text => text.IsVisible && text.Text == "Stopped");
                Assert.False(vm.IsRunning || vm.HasUnsavedChanges || editor.HasPendingStepDraft);
                ((ComboBox)view.FindName("StepExpectedState")).BringIntoView(); Settle(window);
                Capture(window, $"inspector-checkpoint-{width}-{language}-conveyor-roundtrip");
            }
            finally { window.DataContext = null; window.Close(); OpenVisionLanguageService.SetLanguage(previousLanguage, save: false); }
        });
    }

    [Fact]
    public async Task InspectorCheckpointWithoutAvailableTargetsCanBeExplicitlyClearedAndReverted()
    {
        await _ui.InvokeAsync(() =>
        {
            var project = CreateProject(); project.Axes.Clear();
            var definition = project.Sequences[0].Steps[0];
            definition.Action = SequenceStepAction.Delay; definition.TargetId = ""; definition.Parameter = "0";
            definition.ExpectedTargetId = "removed-axis"; definition.ExpectedState = "idle";
            using var vm = new MainViewModel(project);
            vm.Navigation.HideStartupChoice();
            var window = new ShellWindow { DataContext = vm, Width = 1280, Height = 900, ShowInTaskbar = false, ShowActivated = false };
            SmokeDpiTestHook.PlaceOnTestMonitor(window, 1280, 900); window.Show();
            try
            {
                vm.Navigation.OpenSequenceEditorCommand.Execute(null); Settle(window);
                var view = Descendants(window).OfType<SequenceStepInspectorView>().Single(control => control.IsVisible);
                var check = (CheckBox)view.FindName("StepExpectedCheck");
                Assert.False(vm.SequenceEditor.SelectedStepDraft!.CanSetExpectedState);
                Assert.True(check.IsEnabled && check.IsChecked == true);
                string before = JsonSerializer.Serialize(project);
                ((System.Windows.Automation.Provider.IToggleProvider)new System.Windows.Automation.Peers.CheckBoxAutomationPeer(check).GetPattern(System.Windows.Automation.Peers.PatternInterface.Toggle)).Toggle(); Settle(window);
                Assert.False(vm.SequenceEditor.SelectedStepDraft.HasExpectedState);
                Assert.Equal(before, JsonSerializer.Serialize(project));
                ((System.Windows.Automation.Provider.IInvokeProvider)new System.Windows.Automation.Peers.ButtonAutomationPeer(view.DiscardStepDraftButton).GetPattern(System.Windows.Automation.Peers.PatternInterface.Invoke)).Invoke(); Settle(window);
                Assert.True(check.IsChecked == true && check.IsEnabled);
                Assert.Equal("idle", ((ComboBox)view.FindName("StepExpectedState")).SelectedItem);
                Assert.Equal(before, JsonSerializer.Serialize(project));
                Assert.False(vm.IsRunning || vm.HasUnsavedChanges);
                return true;
            }
            finally { window.DataContext = null; window.Close(); }
        });
    }

    [Fact]
    public async Task InspectorAxisStoredCompatibilityTimeoutIsInitiallyVisibleAndExplicitlyClearable()
    {
        await _ui.InvokeTaskAsync(async () =>
        {
            var project = CreateProject();
            using var vm = new MainViewModel(project);
            var definition = project.Sequences[0].Steps[0];
            definition.TimeoutMs = 4337;
            vm.SequenceEditor.Load(project);
            string file = Path.Combine(EvidenceRoot(), $"inspector-axis-cleared-compatibility-{Guid.NewGuid():N}.ovmachine");
            await vm.SaveProjectAsync(file);
            byte[] savedLegacyLimit = await File.ReadAllBytesAsync(file);
            vm.Navigation.HideStartupChoice();
            var window = new ShellWindow { DataContext = vm, Width = 1280, Height = 900, ShowInTaskbar = false, ShowActivated = false };
            SmokeDpiTestHook.PlaceOnTestMonitor(window, 1280, 900); window.Show();
            try
            {
                vm.Navigation.OpenSequenceEditorCommand.Execute(null); Settle(window);
                var view = Descendants(window).OfType<SequenceStepInspectorView>().Single(control => control.IsVisible);
                var editor = vm.SequenceEditor;
                Assert.False(editor.SelectedStepDraft!.WaitForAxisCompletion);
                Assert.True(view.StepDraftTimeout.IsVisible);
                Assert.Equal("4.337", view.StepDraftTimeout.Text);
                Assert.Equal(OpenVisionLanguageService.T("R24.Inspector.UnsupportedTimeout"), ((TextBlock)view.FindName("StepTimeoutHint")).Text);
                Assert.False(editor.HasPendingStepDraft || vm.IsRunning || vm.HasUnsavedChanges);
                string before = JsonSerializer.Serialize(project);
                Capture(window, "inspector-axis-stored-nonwait-positive-timeout");
                view.StepDraftTimeout.SetCurrentValue(TextBox.TextProperty, "0"); Settle(window);
                Assert.Equal(0, editor.SelectedStepDraft.TimeoutMs);
                Assert.Equal(before, JsonSerializer.Serialize(project));
                Assert.True(view.ApplyStepDraftButton.IsEnabled, editor.StepDraftErrorText);
                ((System.Windows.Automation.Provider.IInvokeProvider)new System.Windows.Automation.Peers.ButtonAutomationPeer(view.ApplyStepDraftButton).GetPattern(System.Windows.Automation.Peers.PatternInterface.Invoke)).Invoke(); Settle(window);
                Assert.Equal(0, definition.TimeoutMs);
                Assert.False(definition.WaitForAxisCompletion);
                Assert.True(vm.HasUnsavedChanges);
                Assert.False(vm.IsRunning || editor.HasPendingStepDraft);
                Assert.Equal(savedLegacyLimit, await File.ReadAllBytesAsync(file));
                await vm.SaveProjectAsync(file);
                Assert.True(await vm.OpenProjectAsync(file));
                Assert.Equal(0, editor.SelectedStepDraft!.TimeoutMs);
                Assert.False(editor.SelectedStepDraft.WaitForAxisCompletion || vm.IsRunning || vm.HasUnsavedChanges);
                Capture(window, "inspector-axis-stored-timeout-cleared-reopened");
            }
            finally { window.DataContext = null; window.Close(); }
        });
    }

    [Theory]
    [InlineData(OpenVisionLanguage.Korean)]
    [InlineData(OpenVisionLanguage.English)]
    public async Task InspectorEjectUnitTimeoutStartsVisibleAtZeroAndUpdatesOnlyDraft(OpenVisionLanguage language)
    {
        await _ui.InvokeAsync(() =>
        {
            var previousLanguage = OpenVisionLanguageService.CurrentLanguage;
            OpenVisionLanguageService.SetLanguage(language, save: false);
            var project = LoadInspectorRecipe();
            using var vm = new MainViewModel(project);
            vm.PlacementDraftPrompt = () => PlacementDraftDecision.Cancel;
            vm.Navigation.HideStartupChoice();
            var window = new ShellWindow { DataContext = vm, Width = 1280, Height = 900, ShowInTaskbar = false, ShowActivated = false };
            SmokeDpiTestHook.PlaceOnTestMonitor(window, 1280, 900); window.Show();
            try
            {
                vm.Navigation.OpenSequenceEditorCommand.Execute(null);
                var editor = vm.SequenceEditor;
                editor.SelectedSequence = project.Sequences.Single(sequence => sequence.Id == "owned-reference-sequence");
                editor.SelectedStep = editor.Steps.Single(step => step.Id == "S-03"); Settle(window);
                var view = Descendants(window).OfType<SequenceStepInspectorView>().Single(control => control.IsVisible);
                ((Expander)view.FindName("StepAdditionalSettings")).IsExpanded = true;
                view.StepDraftAction.SetCurrentValue(System.Windows.Controls.Primitives.Selector.SelectedItemProperty, SequenceStepAction.EjectUnitWorkpiece); Settle(window);
                Assert.True(view.StepDraftTimeout.IsVisible);
                Assert.Equal("0", view.StepDraftTimeout.Text);
                Assert.Equal(OpenVisionLanguageService.T("R24.Inspector.NoTimeout"), ((TextBlock)view.FindName("StepTimeoutHint")).Text);
                string original = JsonSerializer.Serialize(project);
                view.StepDraftTimeout.SetCurrentValue(TextBox.TextProperty, "1.234"); Settle(window);
                Assert.Equal(1234, editor.SelectedStepDraft!.TimeoutMs);
                Assert.Equal(original, JsonSerializer.Serialize(project));
                Assert.False(vm.IsRunning || vm.HasUnsavedChanges);
                Assert.True(view.ApplyStepDraftButton.IsEnabled, editor.StepDraftErrorText);
                Capture(window, $"inspector-eject-unit-{language}-timeout-draft");
                ((System.Windows.Automation.Provider.IInvokeProvider)new System.Windows.Automation.Peers.ButtonAutomationPeer(view.DiscardStepDraftButton).GetPattern(System.Windows.Automation.Peers.PatternInterface.Invoke)).Invoke(); Settle(window);
                Assert.Equal(SequenceStepAction.WaitSignal, editor.SelectedStepDraft.Action);
                Assert.Equal(10000, editor.SelectedStepDraft.TimeoutMs);
                Assert.Equal(original, JsonSerializer.Serialize(project));
                return true;
            }
            finally { window.DataContext = null; window.Close(); OpenVisionLanguageService.SetLanguage(previousLanguage, save: false); }
        });
    }

    private static MachineProjectDocument LoadInspectorRecipe()
    {
        string file = Environment.GetEnvironmentVariable("OPENVISIONLAB_MACHINE_STUDIO_INSPECTOR_RECIPE")
            ?? throw new InvalidOperationException("D-owned Inspector recipe is required.");
        var project = new ProjectDocumentStore().Load(File.ReadAllText(file));
        project.Sequences.Single(sequence => sequence.Id == "owned-reference-sequence").Steps.Single(step => step.Id == "S-03").Parameter = "true";
        return project;
    }

    private static MachineProjectDocument CreateProject() => new()
    {
        Id = "r24-step-inspector-owned", Name = "R24 step inspector",
        Simulation = new SimulationDefinition { ActiveLayoutId = "layout" },
        Axes = [new VirtualAxisDefinition { Id = "axis", Name = new string('A', 80), HomePosition = 0, SoftLimitMin = -10, SoftLimitMax = 10 }],
        Layouts = [new MachineLayoutDefinition { Id = "layout", Name = "Step inspector scene", Components = [new LayoutComponentDefinition { Id = "frame", Name = "Frame", Kind = LayoutComponentKind.MachineFrame }] }],
        Sequences = [new SequenceDefinition { Id = "sequence", Name = "Sequence", Steps = [new SequenceStepDefinition { Id = "move", Name = "Move axis", Action = SequenceStepAction.MoveAxis, TargetId = "axis", Parameter = "2", TimeoutMs = 0, NextStepId = "complete" }, new SequenceStepDefinition { Id = "complete", Name = "Complete", Action = SequenceStepAction.Complete }] }]
    };

    private static string EvidenceRoot() => Environment.GetEnvironmentVariable("OPENVISIONLAB_MACHINE_STUDIO_STEP_INSPECTOR_EVIDENCE")
        ?? throw new InvalidOperationException("D: step Inspector evidence root is required.");
    private static void Settle(Window window) => window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(window.UpdateLayout));
    private static Rect Bounds(FrameworkElement element, Visual ancestor) => element.TransformToAncestor(ancestor).TransformBounds(new Rect(element.RenderSize));
    private static void AssertInspectorPopupNames(Window window, ComboBox choice, IReadOnlyList<string> expectedNames, string evidenceName)
    {
        var expansion = (System.Windows.Automation.Provider.IExpandCollapseProvider)new System.Windows.Automation.Peers.ComboBoxAutomationPeer(choice).GetPattern(System.Windows.Automation.Peers.PatternInterface.ExpandCollapse);
        expansion.Expand();
        try
        {
            Settle(window);
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
            timer.Start(); Dispatcher.PushFrame(frame); Settle(window);
            var popup = Descendants(choice).OfType<System.Windows.Controls.Primitives.Popup>().FirstOrDefault()
                ?? choice.Template.FindName("PART_Popup", choice) as System.Windows.Controls.Primitives.Popup
                ?? choice.Template.FindName("Popup", choice) as System.Windows.Controls.Primitives.Popup;
            var child = Assert.IsAssignableFrom<FrameworkElement>(popup?.Child);
            var texts = Descendants(child).OfType<TextBlock>().Where(text => text.IsVisible).Select(text => text.Text).ToArray();
            foreach (string name in expectedNames) Assert.Contains(name, texts);
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(child.ActualWidth), (int)Math.Ceiling(child.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(child);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            Directory.CreateDirectory(EvidenceRoot());
            using var stream = File.Create(Path.Combine(EvidenceRoot(), evidenceName + ".png"));
            encoder.Save(stream);
        }
        finally { expansion.Collapse(); Settle(window); }
    }

    private static void Capture(Window window, string name)
    {
        Directory.CreateDirectory(EvidenceRoot());
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(EvidenceRoot(), name + ".png"));
        encoder.Save(stream);
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
}
