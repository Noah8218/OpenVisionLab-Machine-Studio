using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using OpenVisionLab;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Core.Devices;
using OpenVisionLab.Machine.Core.Sequences;
using OpenVisionLab.Machine.Simulation.Camera;
using OpenVisionLab.Machine.Simulation.Commands;
using OpenVisionLab.Machine.Simulation.Engine;
using OpenVisionLab.Machine.Simulation.Compilation;
using OpenVisionLab.MachineStudio.Model;
using OpenVisionLab.MachineStudio.View.Sequence;
using OpenVisionLab.MachineStudio.View.Shell;
using OpenVisionLab.MachineStudio.ViewModel;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

[Collection(StudioUiTestCollection.Name)]
public sealed class ShellRunAndSaveStatusViewTests
{
    private readonly StudioUiTestHost _ui;
    public ShellRunAndSaveStatusViewTests(StudioUiTestHost ui) => _ui = ui;

    [Fact]
    public async Task PausedHeaderKeepsModeOptionsAndGlobalFaultOwnerReachable()
    {
        await _ui.InvokeTaskAsync(async () =>
        {
            var project = new ProjectDocumentStore().Load(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Samples", "AutomaticTransferCell.ovmachine")));
            project.Sequences.Clear();
            project.Simulation.AutomaticRun = null;
            using var vm = new MainViewModel(project);
            var window = CreateWindow(vm, 1280, 760);
            SmokeDpiTestHook.PlaceOnTestMonitor(window, 1280, 760);
            window.Show();
            try
            {
                Settle(window);
                Assert.InRange(VisualTreeHelper.GetDpi(window).PixelsPerInchX, 95.9, 96.1);
                WriteObservation("current-run-monitor", SmokeDpiTestHook.CaptureMonitorEvidence(window));
                var bar = Descendants(window).OfType<GlobalCommandBarView>().Single();
                var options = Assert.IsType<System.Windows.Controls.Primitives.ToggleButton>(bar.FindName("RunOptionsButton"));
                Assert.True(vm.RunCommand.CanExecute(null));
                vm.RunCommand.Execute(null);
                for (int i = 0; i < 100 && !vm.IsRunning; i++) await Task.Delay(20);
                Assert.True(vm.IsRunning);
                Settle(window);
                var runStatus = Descendants(window).OfType<TextBlock>().Single(text => text.Name == "BottomRunStatusText");
                var activeId = System.Text.RegularExpressions.Regex.Match(runStatus.Text, "[0-9a-f]{32}").Value;
                Assert.NotEmpty(activeId);
                Assert.Equal(vm.CurrentRunStatusText, runStatus.Text);
                Assert.Equal(vm.CurrentRunStatusText, runStatus.ToolTip);
                Capture(window, "current-run-running");
                for (int i = 0; i < 100 && !vm.PauseCommand.CanExecute(null); i++) await Task.Delay(20);
                Assert.True(vm.PauseCommand.CanExecute(null));
                vm.PauseCommand.Execute(null);
                for (int i = 0; i < 100 && !vm.RuntimeDebugger.IsPausedForContinuation; i++) await Task.Delay(20);
                Assert.True(vm.RuntimeDebugger.IsPausedForContinuation);
                Settle(window);
                Assert.Contains(activeId, runStatus.Text);
                Assert.Equal(vm.CurrentRunStatusText, runStatus.Text);
                Assert.Equal(vm.CurrentRunStatusText, runStatus.ToolTip);
                Capture(window, "current-run-paused");
                Assert.True(options.IsVisible && options.IsEnabled);
                options.SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, true);
                Settle(window);
                var modePopup = Assert.IsType<System.Windows.Controls.Primitives.Popup>(bar.FindName("RunOptionsPopup"));
                Assert.True(modePopup.IsOpen);
                modePopup.IsOpen = false;
                var title = Descendants(window).OfType<StudioTitleBar>().Single();
                var faultToggle = Assert.IsType<System.Windows.Controls.Primitives.ToggleButton>(title.FindName("FaultSimulationButton"));
                faultToggle.SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, true);
                Settle(window);
                var faultPopup = Assert.IsType<System.Windows.Controls.Primitives.Popup>(title.FindName("FaultSimulationPopup"));
                var panel = Assert.IsType<FaultSimulationPanelView>(faultPopup.Child);
                Assert.True(vm.FaultManager.IsEnabled);
                Assert.Same(vm.FaultManager, panel.DataContext);
                Assert.True(Assert.IsType<ComboBox>(panel.FindName("GlobalFaultKindComboBox")).IsEnabled);
                Assert.Same(vm.FaultManager.InjectCommand, Assert.IsType<Button>(panel.FindName("GlobalInjectFaultButton")).Command);
                Assert.Same(vm.FaultManager.ClearAllCommand, Assert.IsType<Button>(panel.FindName("GlobalClearAllFaultsButton")).Command);
                Assert.False(vm.IsRunning || vm.HasUnsavedChanges);
                faultPopup.IsOpen = false;
            }
            finally { window.DataContext = null; window.Close(); }
        });
    }

    [Theory]
    [InlineData(1280, 760, OpenVisionLanguage.Korean)]
    [InlineData(1280, 760, OpenVisionLanguage.English)]
    [InlineData(1920, 1040, OpenVisionLanguage.Korean)]
    [InlineData(1920, 1040, OpenVisionLanguage.English)]
    public async Task LongWholeMachineTargetPreservesContentAndSceneSpace(double width, double height, OpenVisionLanguage language)
    {
        await _ui.InvokeAsync(() =>
        {
            var previous = OpenVisionLanguageService.CurrentLanguage;
            OpenVisionLanguageService.SetLanguage(language, save: false);
            var project = CreateProject();
            using var vm = new MainViewModel(project);
            vm.Navigation.OpenSequenceEditorCommand.Execute(null);
            var window = CreateWindow(vm, width, height);
            window.Show();
            try
            {
                Settle(window);
                var bar = Descendants(window).OfType<GlobalCommandBarView>().Single();
                var targetButton = Assert.IsType<System.Windows.Controls.Primitives.ToggleButton>(bar.FindName("RunTargetDetailsButton"));
                targetButton.SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, true);
                Settle(window);
                var popup = Assert.IsType<System.Windows.Controls.Primitives.Popup>(bar.FindName("RunTargetDetailsPopup"));
                Assert.True(popup.IsOpen);
                var target = Assert.IsType<TextBlock>(bar.FindName("CommonRunTargetText"));
                var toolbar = Assert.IsType<Grid>(window.FindName("CommonWorkspaceToolbar"));
                var viewport = Descendants(window).OfType<OpenVisionLab.MachineStudio.View.Scene.MachineSceneViewport>().Single(control => control.IsVisible);
                var viewer = bar.FindName("CommonRunTargetScrollViewer") as ScrollViewer;
                var failures = new List<string>();
                if (bar.ActualHeight > 65) failures.Add($"run bar height {bar.ActualHeight} > 65");
                if (toolbar.ActualHeight > (width <= 1400 ? 110 : 66)) failures.Add($"toolbar height {toolbar.ActualHeight}");
                if (viewer is null) failures.Add("long target has no bounded reading surface");
                Assert.Equal(vm.RunTargetText, target.Text);
                Assert.Contains(project.Sequences[0].Name, target.Text);
                Assert.Contains(project.Sequences[0].Id, target.Text);
                Assert.Contains(OpenVisionLanguageService.T("Shell.SingleRunTarget"), target.Text);
                Assert.Equal(TextTrimming.None, target.TextTrimming);
                Assert.True(Bounds(viewport, window).Top >= Bounds(toolbar, window).Bottom);
                Capture(window, $"target-{width}x{height}-{language}-top");
                if (viewer is not null)
                {
                    Assert.True(viewer.IsVisible);
                    Assert.True(viewer.ViewportHeight > 0 && viewer.ViewportHeight <= 240);
                    viewer.ScrollToBottom();
                    Settle(window);
                    Assert.Equal(viewer.ScrollableHeight, viewer.VerticalOffset, precision: 2);
                    Assert.True(Bounds(target, viewer).Bottom <= viewer.ActualHeight + 1);
                    Capture(Assert.IsAssignableFrom<FrameworkElement>(popup.Child), $"target-{width}x{height}-{language}-details");
                    popup.IsOpen = false;
                    Capture(window, $"target-{width}x{height}-{language}-bottom");
                }
                WriteObservation($"target-{width}x{height}-{language}", new { bar.ActualHeight, toolbarHeight = toolbar.ActualHeight, target.Text, targetHeight = target.ActualHeight, viewerHeight = viewer?.ViewportHeight, offset = viewer?.VerticalOffset, failures });
                Assert.False(vm.IsRunning || vm.HasUnsavedChanges);
                Assert.True(string.IsNullOrWhiteSpace(vm.CurrentProjectPath));
                Assert.Empty(failures);
            }
            finally { window.DataContext = null; window.Close(); OpenVisionLanguageService.SetLanguage(previous, save: false); }
            return true;
        });
    }

    [Theory]
    [InlineData(1280, 760, OpenVisionLanguage.Korean)]
    [InlineData(1280, 760, OpenVisionLanguage.English)]
    [InlineData(1920, 1040, OpenVisionLanguage.Korean)]
    [InlineData(1920, 1040, OpenVisionLanguage.English)]
    public async Task FooterDistinguishesUnsavedAppliedAndPendingChangesWithoutRunning(double width, double height, OpenVisionLanguage language)
    {
        await _ui.InvokeTaskAsync(async () =>
        {
            var previous = OpenVisionLanguageService.CurrentLanguage;
            OpenVisionLanguageService.SetLanguage(language, save: false);
            var project = CreateProject();
            using var vm = new MainViewModel(project, integrationSettingsPath: Path.Combine(EvidenceRoot(), $"save-{width}-{language}-integration.json"));
            vm.Navigation.OpenSequenceEditorCommand.Execute(null);
            var window = CreateWindow(vm, width, height);
            SmokeDpiTestHook.PlaceOnTestMonitor(window, (int)width, (int)height);
            window.Show();
            var failures = new List<string>();
            string file = Path.Combine(EvidenceRoot(), $"file-{width}-{language}", new string('R', 80) + ".ovmachine");
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            try
            {
                Settle(window);
                var footer = Descendants(window).OfType<StudioStatusBarView>().Single();
                var caption = footer.FindName("CommonSaveStateText") as TextBlock;
                var title = Descendants(window).OfType<StudioTitleBar>().Single();
                var titleCaption = Assert.IsType<TextBlock>(title.FindName("TitleSaveStateText"));
                void Check(string expected, string state)
                {
                    Settle(window);
                    Assert.Equal(expected, titleCaption.Text);
                    Assert.True(titleCaption.IsVisible);
                    var savedAppearance = state is "saved" or "discarded" or "resaved" or "placement-discarded";
                    Assert.Equal(savedAppearance ? "#FF147854" : "#FF986610", titleCaption.Foreground.ToString());
                    Assert.Equal("Segoe UI, Malgun Gothic", titleCaption.FontFamily.Source);
                    Assert.Equal(11.5, titleCaption.FontSize);
                    Assert.Equal(16.675, titleCaption.LineHeight);
                    var chip = Assert.IsType<Border>(title.FindName("TitleSaveStateChip"));
                    Assert.Equal(savedAppearance ? "#FFB7DBCA" : "#FFE5CF9A", chip.BorderBrush.ToString());
                    Assert.Equal(22, chip.ActualHeight);
                    foreach (var (name, key) in new[] { ("TopUndoButton", "Shell.Undo"), ("TopRedoButton", "Shell.Redo") })
                    {
                        var button = Assert.IsType<Button>(title.FindName(name));
                        var label = Descendants(button).OfType<TextBlock>().Single(text => text.Text == OpenVisionLanguageService.T(key));
                        Assert.True(label.IsVisible);
                        Assert.InRange(label.FontSize, width < 1400 ? 12.4 : 12.9, width < 1400 ? 12.6 : 13.1);
                        Assert.Equal(button.Foreground.ToString(), label.Foreground.ToString());
                        Assert.True(new Rect(window.RenderSize).Contains(Bounds(button, window)));
                        Assert.True(new Rect(button.RenderSize).Contains(Bounds(label, button)));
                    }
                    var menu = Descendants(title).OfType<MainMenuView>().Single();
                    Assert.True(Bounds(menu, window).Right <= Bounds(Assert.IsType<System.Windows.Controls.Primitives.ToggleButton>(title.FindName("FaultSimulationButton")), window).Left);
                    if (caption is null) failures.Add(state + ": separate save state missing");
                    else
                    {
                        Assert.Equal(expected, caption.Text);
                        Assert.Equal(TextTrimming.None, caption.TextTrimming);
                        var displayedCaption = titleCaption;
                        FrameworkElement displayedContainer = title;
                        Assert.True(displayedCaption.IsVisible && Bounds(displayedCaption, displayedContainer).Right <= displayedContainer.ActualWidth);
                        Assert.False(caption.IsVisible);
                        var glyphs = new FormattedText(displayedCaption.Text, CultureInfo.CurrentCulture, displayedCaption.FlowDirection, new Typeface(displayedCaption.FontFamily, displayedCaption.FontStyle, displayedCaption.FontWeight, displayedCaption.FontStretch), displayedCaption.FontSize, displayedCaption.Foreground, VisualTreeHelper.GetDpi(displayedCaption).PixelsPerDip);
                        Assert.True(glyphs.Width <= displayedCaption.ActualWidth + 1 && glyphs.Height <= displayedCaption.ActualHeight + 1);
                    }
                    Capture(window, $"save-{width}x{height}-{language}-{state}");
                }
                string notSaved = language == OpenVisionLanguage.Korean ? "저장 안 됨" : "Not saved";
                string saved = language == OpenVisionLanguage.Korean ? "저장됨" : "Saved";
                string applied = language == OpenVisionLanguage.Korean ? "적용됨 · 미저장" : "Applied · Unsaved";
                string pending = language == OpenVisionLanguage.Korean ? "미적용 변경 있음" : "Unapplied changes";
                Assert.False(vm.HasUnsavedChanges);
                Check(notSaved, "memory");
                await vm.SaveProjectAsync(file);
                Assert.False(vm.HasUnsavedChanges);
                Assert.Equal(file, vm.CurrentProjectPath);
                Check(saved, "saved");
                string payload = JsonSerializer.Serialize(project);
                var inspector = Descendants(window).OfType<SequenceStepInspectorView>().Single(view => view.IsVisible);
                inspector.StepDraftName.SetCurrentValue(TextBox.TextProperty, "Pending complete");
                Settle(window);
                Assert.True(vm.SequenceEditor.HasPendingStepDraft);
                Assert.Equal(payload, JsonSerializer.Serialize(project));
                Check(pending, "step-draft");
                vm.SequenceEditor.DiscardStepDraftCommand.Execute(null);
                Assert.False(vm.SequenceEditor.HasPendingStepDraft);
                Check(saved, "discarded");
                inspector.StepDraftName.SetCurrentValue(TextBox.TextProperty, "Applied complete");
                Settle(window);
                Assert.True(vm.SequenceEditor.ApplyStepDraftCommand.CanExecute(null));
                vm.SequenceEditor.ApplyStepDraftCommand.Execute(null);
                Assert.True(vm.HasUnsavedChanges);
                Check(applied, "applied");
                Assert.Equal("Applied complete", project.Sequences[0].Steps[0].Name);
                await vm.SaveProjectAsync(file);
                Assert.False(vm.HasUnsavedChanges || vm.SequenceEditor.HasPendingStepDraft);
                Check(saved, "resaved");
                byte[] fileBytes = await File.ReadAllBytesAsync(file);
                using var fresh = new MainViewModel(integrationSettingsPath: Path.Combine(EvidenceRoot(), $"reopen-{width}-{language}-integration.json"));
                Assert.True(await fresh.OpenProjectReplacingCurrentAsync(file));
                var reopened = CreateWindow(fresh, width, height);
                SmokeDpiTestHook.PlaceOnTestMonitor(reopened, (int)width, (int)height);
                reopened.Show();
                try
                {
                    Settle(reopened);
                    var reopenedFooter = Descendants(reopened).OfType<StudioStatusBarView>().Single();
                    var reopenedCaption = reopenedFooter.FindName("CommonSaveStateText") as TextBlock;
                    if (reopenedCaption is null) failures.Add("reopened: separate save state missing");
                    else Assert.Equal(saved, reopenedCaption.Text);
                    Assert.Equal(project.Name, fresh.RecipeTitleText);
                    Assert.Equal("Applied complete", fresh.SequenceEditor.Steps.First().Name);
                    Assert.Equal(fileBytes, await File.ReadAllBytesAsync(file));
                    Assert.False(fresh.IsRunning || fresh.HasUnsavedChanges);
                    Assert.Equal(string.Format(CultureInfo.CurrentCulture, OpenVisionLanguageService.T("Shell.SimulationStatus"), TimeSpan.Zero), fresh.SimulationStatusText);
                    Capture(reopened, $"save-{width}x{height}-{language}-reopened");
                }
                finally { reopened.DataContext = null; reopened.Close(); }
                vm.Navigation.SelectedWorkspaceIndex = 0;
                vm.Layout.SelectedItem = vm.Layout.Items.First(item => item.Component is not null);
                Settle(window);
                var editor = vm.Layout.SelectedComponentEditor!;
                editor.DraftName = "Pending placement";
                Assert.True(editor.HasPendingInspectorDraft);
                Check(pending, "placement-draft");
                editor.DiscardPlacementDraftCommand.Execute(null);
                Check(saved, "placement-discarded");
                Assert.False(vm.IsRunning || vm.HasUnsavedChanges);
                Assert.Equal(string.Format(CultureInfo.CurrentCulture, OpenVisionLanguageService.T("Shell.SimulationStatus"), TimeSpan.Zero), vm.SimulationStatusText);
                WriteObservation($"save-{width}x{height}-{language}", new { vm.ProjectStatusText, caption = caption?.Text, file, failures, hash = Convert.ToHexString(SHA256.HashData(fileBytes)) });
                Assert.Empty(failures);
            }
            finally { window.DataContext = null; window.Close(); OpenVisionLanguageService.SetLanguage(previous, save: false); }
        });
    }

    [Theory]
    [InlineData(OpenVisionLanguage.Korean)]
    [InlineData(OpenVisionLanguage.English)]
    public async Task SaveAndPendingCaptionRefreshWhenLanguageChanges(OpenVisionLanguage language)
    {
        await _ui.InvokeTaskAsync(async () =>
        {
            var previous = OpenVisionLanguageService.CurrentLanguage;
            OpenVisionLanguageService.SetLanguage(language, save: false);
            using var vm = new MainViewModel(CreateProject());
            vm.PlacementDraftPrompt = () => OpenVisionLab.MachineStudio.Model.PlacementDraftDecision.Cancel;
            vm.Navigation.OpenSequenceEditorCommand.Execute(null);
            var window = CreateWindow(vm, 1280, 760);
            window.Show();
            string file = Path.Combine(EvidenceRoot(), $"locale-{language}.ovmachine");
            try
            {
                Settle(window);
                var footer = Descendants(window).OfType<StudioStatusBarView>().Single();
                var caption = Assert.IsType<TextBlock>(footer.FindName("CommonSaveStateText"));
                var other = language == OpenVisionLanguage.Korean ? OpenVisionLanguage.English : OpenVisionLanguage.Korean;
                OpenVisionLanguageService.SetLanguage(other, save: false);
                Settle(window);
                Assert.Equal(other == OpenVisionLanguage.Korean ? "저장 안 됨" : "Not saved", caption.Text);
                await vm.SaveProjectAsync(file);
                Settle(window);
                Assert.Equal(other == OpenVisionLanguage.Korean ? "저장됨" : "Saved", caption.Text);
                vm.SequenceEditor.SelectedStepDraft!.Name = "Unapplied locale draft";
                OpenVisionLanguageService.SetLanguage(language, save: false);
                Settle(window);
                Assert.Equal(language == OpenVisionLanguage.Korean ? "미적용 변경 있음" : "Unapplied changes", caption.Text);
                Capture(window, $"locale-{language}-pending");
                vm.SequenceEditor.DiscardStepDraftCommand.Execute(null);
                Settle(window);
                Assert.Equal(language == OpenVisionLanguage.Korean ? "저장됨" : "Saved", caption.Text);
                Assert.False(vm.IsRunning || vm.HasUnsavedChanges);
                Capture(window, $"locale-{language}-recovered");
            }
            finally { vm.SequenceEditor.DiscardStepDraftCommand.Execute(null); window.DataContext = null; window.Close(); OpenVisionLanguageService.SetLanguage(previous, save: false); }
        });
    }

    [Theory]
    [InlineData(1280, 900, OpenVisionLanguage.Korean, PlaceholderInspectionDecision.Pass)]
    [InlineData(1280, 900, OpenVisionLanguage.Korean, PlaceholderInspectionDecision.Fail)]
    [InlineData(1280, 900, OpenVisionLanguage.English, PlaceholderInspectionDecision.Pass)]
    [InlineData(1280, 900, OpenVisionLanguage.English, PlaceholderInspectionDecision.Fail)]
    [InlineData(1920, 1032, OpenVisionLanguage.Korean, PlaceholderInspectionDecision.Pass)]
    [InlineData(1920, 1032, OpenVisionLanguage.Korean, PlaceholderInspectionDecision.Fail)]
    [InlineData(1920, 1032, OpenVisionLanguage.English, PlaceholderInspectionDecision.Pass)]
    [InlineData(1920, 1032, OpenVisionLanguage.English, PlaceholderInspectionDecision.Fail)]
    public async Task EquipmentFooterCountsOnlyActualExitsAndResetsWithoutInventingConnections(double width, double height, OpenVisionLanguage language, PlaceholderInspectionDecision decision)
    {
        await _ui.InvokeTaskAsync(async () =>
        {
            var previous = OpenVisionLanguageService.CurrentLanguage;
            OpenVisionLanguageService.SetLanguage(language, save: false);
            var project = CreateProject();
            project.Layouts.SelectMany(layout => layout.Components).Single(component => component.Id == "belt").Name = new string('장', 60);
            var definition = new SequenceDefinition { Id = "status-sequence", Name = "Owned status sequence", UnitId = "unit-inspection", WatchdogTimeoutMs = 0, Steps =
            [
                new() { Id = "feed", Action = SequenceStepAction.FeedUnitWorkpiece, TargetId = "unit-inspection", NextStepId = "capture" },
                new() { Id = "capture", Action = SequenceStepAction.TriggerCamera, TargetId = "status-camera", Parameter = "status-inspection", NextStepId = "inspect" },
                new() { Id = "inspect", Action = SequenceStepAction.WaitVisionResult, TargetId = "status-camera", TimeoutMs = 30000, NextStepId = "delay", FailureStepId = "delay" },
                new() { Id = "delay", Action = SequenceStepAction.Delay, Parameter = ".03", NextStepId = "exit" },
                new() { Id = "exit", Action = SequenceStepAction.EjectUnitWorkpiece, TargetId = "unit-inspection", Parameter = "auto", NextStepId = "done" },
                new() { Id = "done", Action = SequenceStepAction.Complete }
            ] };
            foreach (var step in definition.Steps) step.Name = step.Id;
            project.Sequences = [definition];
            project.MaterialFlow.Enabled = true;
            project.MaterialFlow.TotalFeedCount = 2;
            project.Stations[0].Units.Single(unit => unit.Id == "unit-inspection").RepeatCount = 2;
            project.Devices.Add(new DeviceDefinition { Id = "status-camera", Name = "Owned status camera", Kind = DeviceKind.Camera, Camera = new VirtualCameraDefinition { ExposureDelayMilliseconds = 5, TransferDelayMilliseconds = 5, PlaceholderDecision = decision } });
            project.Layouts[0].Components.Add(new OpenVisionLab.Machine.Core.Layouts.LayoutComponentDefinition { Id = "status-camera-layout", Name = "Owned status camera", Kind = OpenVisionLab.Machine.Core.Layouts.LayoutComponentKind.Camera, UnitId = "unit-inspection", BehaviorBindingId = "status-camera", Size = new OpenVisionLab.Machine.Core.Layouts.Size2D { Width = 20, Height = 20 } });
            var compilation = new MachineProjectRuntimeCompiler(TimeSpan.FromMilliseconds(5)).Compile(project);
            Assert.True(compilation.IsSuccess, string.Join("; ", compilation.Errors.Select(error => error.Message)));
            string payload = JsonSerializer.Serialize(project);
            using var vm = new MainViewModel(project);
            var window = CreateWindow(vm, width, height);
            SmokeDpiTestHook.PlaceOnTestMonitor(window, (int)width, (int)height);
            window.Show();
            try
            {
                await WaitForStatusAsync(() => vm.IsSceneEditable);
                var monitor = SmokeDpiTestHook.CaptureMonitorEvidence(window);
                Assert.True(monitor.WindowIntersectsMonitor);
                Assert.Equal(96, VisualTreeHelper.GetDpi(window).PixelsPerInchX);
                var footer = Descendants(window).OfType<StudioStatusBarView>().Single();
                var verdict = Assert.IsType<TextBlock>(footer.FindName("StatusEquipmentVerdictText"));
                var names = new[] { "StatusSimulationTimeText", "StatusSimulationSpeedText", "StatusEquipmentVerdictText", "StatusEquipmentTwoDText", "StatusEquipmentThreeDText", "StatusEquipmentAiText" };
                var fields = names.Select(name => Assert.IsType<TextBlock>(footer.FindName(name))).ToArray();
                void CheckFields(string expected)
                {
                    Settle(window);
                    Assert.Equal(expected, verdict.Text);
                    Assert.Equal(language == OpenVisionLanguage.Korean ? "2D Studio: 미확인" : "2D Studio: Unverified", fields[3].Text);
                    Assert.Equal(language == OpenVisionLanguage.Korean ? "3D Studio: 미확인" : "3D Studio: Unverified", fields[4].Text);
                    Assert.Equal(language == OpenVisionLanguage.Korean ? "AI 생성: 미연결" : "AI generation: Not connected", fields[5].Text);
                    Assert.Equal(24, footer.ActualHeight);
                    for (int index = 0; index < fields.Length; index++)
                    {
                        var field = fields[index];
                        Assert.True(field.IsVisible && field.IsEnabled);
                        Assert.Equal("Segoe UI, Malgun Gothic", field.FontFamily.Source);
                        Assert.Equal(11.5, field.FontSize);
                        Assert.Equal(400, field.FontWeight.ToOpenTypeWeight());
                        Assert.Equal(16.675, field.LineHeight);
                        Assert.Equal("#FF52687C", field.Foreground.ToString());
                        Assert.True(new Rect(footer.RenderSize).Contains(Bounds(field, footer)));
                        if (index > 0) Assert.Equal(14, Bounds(field, footer).Left - Bounds(fields[index - 1], footer).Right, precision: 1);
                        var glyphs = StatusGlyphs(VisualTreeHelper.GetDrawing(field)!).ToArray();
                        Assert.NotEmpty(glyphs);
                        Assert.All(glyphs, glyph => { Assert.Equal(11.5, glyph.GlyphRun.FontRenderingEmSize); Assert.Equal(400, glyph.GlyphRun.GlyphTypeface.Weight.ToOpenTypeWeight()); Assert.Equal("#FF52687C", glyph.ForegroundBrush.ToString()); });
                        var measured = new FormattedText(field.Text, CultureInfo.CurrentCulture, field.FlowDirection, new Typeface(field.FontFamily, field.FontStyle, field.FontWeight, field.FontStretch), field.FontSize, field.Foreground, VisualTreeHelper.GetDpi(field).PixelsPerDip);
                        Assert.True(measured.Width <= field.ActualWidth + .5 && measured.Height <= field.ActualHeight + .5);
                    }
                    var selection = Assert.IsType<TextBlock>(footer.FindName("StatusEquipmentSelectionText"));
                    Assert.True(Bounds(fields[^1], footer).Right <= Bounds(selection, footer).Left);
                    Assert.True(new Rect(footer.RenderSize).Contains(Bounds(selection, footer)));
                }
                CheckFields("OK 0 · NG 0");
                var session = (SimulationSessionCoordinator)typeof(MainViewModel).GetField("_simulationSession", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(vm)!;
                var engine = session.Engine;
                Assert.True((await engine.EnqueueCommandAsync(new ConfigureRuntimeCommand(compilation.Configuration!))).IsAccepted);
                Assert.True((await engine.EnqueueCommandAsync(new StartSequenceCommand(definition.Id))).IsAccepted);
                bool sawInspectedBeforeExit = false;
                for (int tick = 0; tick < 120 && engine.CurrentSnapshot.UnitMaterialExits.Count < 2; tick++)
                {
                    Assert.True((await engine.EnqueueCommandAsync(new StepCommand())).IsAccepted);
                    var snapshot = engine.CurrentSnapshot;
                    if (snapshot.UnitMaterialExits.Count == 0 && snapshot.UnitMaterialSlots.Any(slot => slot.InspectionCount > 0))
                    {
                        sawInspectedBeforeExit = true;
                        Assert.Equal("OK 0 · NG 0", vm.EquipmentVerdictStatusText);
                    }
                }
                Assert.True(sawInspectedBeforeExit);
                Assert.Equal(2, engine.CurrentSnapshot.UnitMaterialExits.Count);
                Assert.Equal(2, engine.CurrentSnapshot.UnitMaterialExits.Select(exit => exit.InstanceId).Distinct().Count());
                string expected = decision == PlaceholderInspectionDecision.Pass ? "OK 2 · NG 0" : "OK 0 · NG 2";
                await WaitForStatusAsync(() => verdict.Text == expected);
                vm.Layout.SelectedItem = vm.Layout.Items.Single(item => item.Id == "belt");
                Assert.Contains(new string('장', 60), vm.SelectionStatusText);
                CheckFields(expected);
                Capture(window, $"status-exits-{width}-{language}-{decision}");
                WriteObservation($"status-exits-{width}-{language}-{decision}", new { method = "Actual owned Core engine commands and snapshot -> Main notification -> WPF binding/render; no physical input", monitor, exits = engine.CurrentSnapshot.UnitMaterialExits, fields = fields.Select(field => new { field.Name, field.Text, rectangle = Bounds(field, footer).ToString(), glyphs = StatusGlyphs(VisualTreeHelper.GetDrawing(field)!).Select(glyph => new { text = new string(glyph.GlyphRun.Characters!.ToArray()), glyph.GlyphRun.FontRenderingEmSize, family = glyph.GlyphRun.GlyphTypeface.FontUri.ToString(), weight = glyph.GlyphRun.GlyphTypeface.Weight.ToOpenTypeWeight(), color = glyph.ForegroundBrush.ToString() }) }), vm.HasUnsavedChanges });
                Assert.True((await engine.EnqueueCommandAsync(new ResetCommand())).IsAccepted);
                await WaitForStatusAsync(() => verdict.Text == "OK 0 · NG 0");
                Assert.Empty(engine.CurrentSnapshot.UnitMaterialExits);
                CheckFields("OK 0 · NG 0");
                var other = language == OpenVisionLanguage.Korean ? OpenVisionLanguage.English : OpenVisionLanguage.Korean;
                OpenVisionLanguageService.SetLanguage(other, save: false);
                Settle(window);
                Assert.Equal(other == OpenVisionLanguage.Korean ? "AI 생성: 미연결" : "AI generation: Not connected", fields[5].Text);
                OpenVisionLanguageService.SetLanguage(language, save: false);
                vm.Navigation.OpenSequenceEditorCommand.Execute(null);
                Assert.True(vm.Navigation.IsSequenceWorkspace);
                CheckFields("OK 0 · NG 0");
                Capture(window, $"status-sequence-exits-{width}-{language}-{decision}");
                vm.Navigation.SelectedExecutionTabIndex = 0;
                Settle(window);
                Assert.All(fields.Skip(2), field => Assert.False(field.IsVisible));
                vm.Navigation.SelectedWorkspaceIndex = 0;
                CheckFields("OK 0 · NG 0");
                Assert.Equal(payload, JsonSerializer.Serialize(project));
                Assert.False(vm.HasUnsavedChanges || vm.IsRunning);
            }
            finally { window.DataContext = null; window.Close(); OpenVisionLanguageService.SetLanguage(previous, save: false); }
        });
    }

    [Theory]
    [InlineData(1920, 1032, OpenVisionLanguage.Korean)]
    [InlineData(1280, 900, OpenVisionLanguage.Korean)]
    [InlineData(1920, 1032, OpenVisionLanguage.English)]
    [InlineData(1280, 900, OpenVisionLanguage.English)]
    public async Task EquipmentFooterWaitUsesActualAutomaticRuntimeAndRecoversAfterResultsResetAndAbort(int width, int height, OpenVisionLanguage language)
    {
        await _ui.InvokeTaskAsync(async () =>
        {
            var previous = OpenVisionLanguageService.CurrentLanguage;
            OpenVisionLanguageService.SetLanguage(language, save: false);
            const string sequenceId = "status-external-sequence", cameraId = "status-external-camera", recipeId = "status-external-recipe";
            var project = CreateProject();
            project.Layouts.SelectMany(layout => layout.Components).Single(component => component.Id == "belt").Name = new string('장', 60);
            project.Sequences = [new SequenceDefinition { Id = sequenceId, Name = "Status external sequence", WatchdogTimeoutMs = 0, Steps =
            [
                new() { Id = "capture", Name = "Capture", Action = SequenceStepAction.TriggerCamera, TargetId = cameraId, Parameter = recipeId, NextStepId = "inspect" },
                new() { Id = "inspect", Name = "Inspect", Action = SequenceStepAction.WaitVisionResult, TargetId = cameraId, TimeoutMs = 30000, NextStepId = "done", FailureStepId = "done" },
                new() { Id = "done", Name = "Done", Action = SequenceStepAction.Complete }
            ] }];
            project.Simulation.AutomaticRun = new AutomaticRunDefinition { SequenceId = sequenceId };
            project.Devices.Add(new DeviceDefinition { Id = cameraId, Name = "Actual numerical camera", Kind = DeviceKind.Camera, Camera = new VirtualCameraDefinition
                { ExposureDelayMilliseconds = 5, TransferDelayMilliseconds = 5, NumericalInput = new NumericalCameraSettings { RecipeName = recipeId, ConfirmedVersion = 1, GenerationDelayMilliseconds = 0, ResponseDelayMilliseconds = 0 } } });
            project.Layouts[0].Components.Add(new OpenVisionLab.Machine.Core.Layouts.LayoutComponentDefinition { Id = "status-external-camera-layout", Name = "Actual numerical camera", Kind = OpenVisionLab.Machine.Core.Layouts.LayoutComponentKind.Camera, UnitId = "unit-inspection", BehaviorBindingId = cameraId, Size = new OpenVisionLab.Machine.Core.Layouts.Size2D { Width = 20, Height = 20 } });
            var compilation = new MachineProjectRuntimeCompiler(TimeSpan.FromMilliseconds(5)).Compile(project);
            Assert.True(compilation.IsSuccess, string.Join("; ", compilation.Errors.Select(error => error.Message)));
            string payload = JsonSerializer.Serialize(project);
            // Isolate the UI capture budget from the unchanged five-second product deadline.
            using var vm = new MainViewModel(project, integrationSettingsPath: Path.Combine(EvidenceRoot(), $"wait-{width}-{language}-integration.json"), automaticExternalInspectionWallTimeout: TimeSpan.FromSeconds(30));
            var window = CreateWindow(vm, width, height);
            SmokeDpiTestHook.PlaceOnTestMonitor(window, width, height);
            window.Show();
            try
            {
                await WaitForStatusAsync(() => vm.IsSceneEditable);
                var monitor = SmokeDpiTestHook.CaptureMonitorEvidence(window);
                Assert.True(monitor.WindowIntersectsMonitor);
                Assert.Equal(96, VisualTreeHelper.GetDpi(window).PixelsPerInchX);
                var footer = Descendants(window).OfType<StudioStatusBarView>().Single();
                var wait = Assert.IsType<TextBlock>(footer.FindName("StatusEquipmentExternalWaitText"));
                var speed = Assert.IsType<TextBlock>(footer.FindName("StatusSimulationSpeedText"));
                var verdict = Assert.IsType<TextBlock>(footer.FindName("StatusEquipmentVerdictText"));
                var session = (SimulationSessionCoordinator)typeof(MainViewModel).GetField("_simulationSession", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(vm)!;
                var engine = session.Engine;
                Assert.True((await engine.EnqueueCommandAsync(new ConfigureRuntimeCommand(compilation.Configuration!, project.Id))).IsAccepted);
                void Check(bool waiting, string stage, bool save = true)
                {
                    Settle(window);
                    Assert.Equal(waiting, vm.IsEquipmentExternalInspectionWaiting);
                    Assert.Equal(waiting, wait.IsVisible);
                    foreach (string controlName in new[] { "StatusSimulationTimeText", "StatusSimulationSpeedText", "StatusEquipmentVerdictText", "StatusEquipmentTwoDText", "StatusEquipmentThreeDText", "StatusEquipmentAiText", "StatusEquipmentSelectionText", "StatusEquipmentSimulationOnlyText" })
                    {
                        var field = Assert.IsType<TextBlock>(footer.FindName(controlName));
                        if (controlName is not "StatusEquipmentSelectionText" and not "StatusEquipmentSimulationOnlyText") Assert.True(new Rect(footer.RenderSize).Contains(Bounds(field, footer)), controlName + " exceeded the footer");
                        if (controlName == "StatusEquipmentSelectionText") { Assert.Equal(double.PositiveInfinity, field.MaxWidth); Assert.Equal(TextTrimming.None, field.TextTrimming); }
                        if (controlName != "StatusEquipmentSelectionText")
                        {
                            var text = new FormattedText(field.Text, CultureInfo.CurrentCulture, field.FlowDirection, new Typeface(field.FontFamily, field.FontStyle, field.FontWeight, field.FontStretch), field.FontSize, field.Foreground, VisualTreeHelper.GetDpi(field).PixelsPerDip);
                            Assert.True(text.Width <= field.ActualWidth + .5, controlName + " was clipped");
                        }
                    }
                    Assert.Equal(language == OpenVisionLanguage.Korean ? "대기 중 — 시뮬레이션 정지" : "Waiting — simulation stopped", wait.Text);
                    if (waiting) Assert.Equal(14, Bounds(verdict, footer).Left - Bounds(wait, footer).Right, precision: 1);
                    if (!waiting) Assert.Equal(14, Bounds(verdict, footer).Left - Bounds(speed, footer).Right, precision: 1);
                    if (waiting)
                    {
                        Assert.Equal(14, Bounds(wait, footer).Left - Bounds(speed, footer).Right, precision: 1);
                        Assert.True(new Rect(footer.RenderSize).Contains(Bounds(wait, footer)));
                        Assert.Equal(16.675, wait.LineHeight);
                        var glyphs = StatusGlyphs(VisualTreeHelper.GetDrawing(wait)!).ToArray();
                        Assert.NotEmpty(glyphs);
                        Assert.All(glyphs, glyph => { Assert.Equal(11.5, glyph.GlyphRun.FontRenderingEmSize); Assert.Equal(400, glyph.GlyphRun.GlyphTypeface.Weight.ToOpenTypeWeight()); Assert.Equal("#FF986610", glyph.ForegroundBrush.ToString()); });
                    }
                    if (save)
                    {
                        string name = $"status-wait-{width}-{language}-{stage}";
                        Capture(window, name);
                        WriteObservation(name, new { method = "Actual owned engine commands -> live snapshot -> Main notify -> readonly WPF render; no physical input or real consumer", monitor, snapshot = engine.CurrentSnapshot, waiting, wait.Text, bounds = Bounds(wait, footer).ToString(), glyphs = waiting ? StatusGlyphs(VisualTreeHelper.GetDrawing(wait)!).Select(glyph => new { glyph.GlyphRun.FontRenderingEmSize, weight = glyph.GlyphRun.GlyphTypeface.Weight.ToOpenTypeWeight(), color = glyph.ForegroundBrush.ToString() }).ToArray() : [], vm.HasUnsavedChanges });
                    }
                }
                Check(false, "ready");
                Assert.True((await engine.EnqueueCommandAsync(new StartManualControlCommand())).IsAccepted);
                Assert.True((await engine.EnqueueCommandAsync(new PauseCommand())).IsAccepted);
                Assert.True((await engine.EnqueueCommandAsync(TriggerVirtualCameraCommand.CreateNumerical(cameraId, recipeId, project.Id, engine.CurrentSnapshot.RuntimeGeneration, true))).IsAccepted);
                var manualDeadline = DateTime.UtcNow.AddSeconds(5);
                while (engine.CurrentSnapshot.Cameras.Single().State != VirtualCameraState.AwaitingExternalResult && DateTime.UtcNow < manualDeadline)
                {
                    Assert.True((await engine.EnqueueCommandAsync(new StepCommand())).IsAccepted);
                    await Task.Delay(10);
                }
                Assert.Equal(VirtualCameraState.AwaitingExternalResult, engine.CurrentSnapshot.Cameras.Single().State);
                Check(false, "manual-pending");
                async Task StartWaiting()
                {
                    Assert.True((await engine.EnqueueCommandAsync(new ResetCommand())).IsAccepted);
                    var identity = new SimulationRuntimeIdentity(project.Id, engine.CurrentSnapshot.RuntimeGeneration);
                    Assert.True((await engine.EnqueueCommandAsync(new ArmAutomaticExternalInspectionCommand(identity, sequenceId, new Dictionary<string, VirtualCameraExternalSource>(), [cameraId]))).IsAccepted);
                    Assert.True((await engine.EnqueueCommandAsync(new StartAutomaticRunCommand(waitForExternalResult: true))).IsAccepted);
                    await WaitForStatusAsync(() => engine.CurrentSnapshot.AutomaticRun.IsActive && engine.CurrentSnapshot.Cameras.Single().State == VirtualCameraState.AwaitingExternalResult && wait.IsVisible);
                }
                ApplyExternalInspectionResultCommand Result(ExternalInspectionResultStatus status, ExternalInspectionOutcome outcome, bool stale = false)
                {
                    var snapshot = engine.CurrentSnapshot;
                    var camera = snapshot.Cameras.Single();
                    var frame = Assert.IsType<VirtualCameraFrameEvidence>(camera.FrameEvidence);
                    var consumer = new ExternalInspectionConsumerIdentity("OpenVisionLab.TwoDStudio", "1.0.0", new string('1', 40), "Clean");
                    var correlation = new ExternalInspectionCorrelationIdentity(project.Id, project.Schema, sequenceId, "capture", cameraId, camera.CurrentAcquisitionId!, frame.FrameId, "mm", "TwoD", "Image", frame.ContentSha256, new string('B', 64), consumer);
                    var transaction = Guid.NewGuid(); var handoff = Guid.NewGuid(); var ack = Guid.NewGuid();
                    var chain = new ExternalInspectionMessageChain(transaction, handoff, transaction, handoff, ack, transaction, handoff, ack, Guid.NewGuid(), new string('C', 64));
                    return new ApplyExternalInspectionResultCommand(new SimulationRuntimeIdentity(project.Id, snapshot.RuntimeGeneration + (stale ? 1 : 0)), chain, correlation, correlation, consumer, consumer, true, status, outcome, "status-fixture");
                }
                vm.Integration.Setup.WaitForExternalResult = true;
                await StartWaiting();
                Check(true, "automatic-wait");
                vm.Layout.SelectedItem = vm.Layout.Items.Single(item => item.Id == "belt");
                Check(true, "automatic-wait-long-selection");
                var selection = Assert.IsType<TextBlock>(footer.FindName("StatusEquipmentSelectionText"));
                var ai = Assert.IsType<TextBlock>(footer.FindName("StatusEquipmentAiText"));
                Assert.Contains(new string('장', 60), selection.Text);
                Assert.Equal(double.PositiveInfinity, selection.MaxWidth);
                Assert.Equal(TextTrimming.None, selection.TextTrimming);
                Assert.True(Bounds(ai, footer).Right <= Bounds(selection, footer).Left);
                var pendingTime = engine.CurrentSnapshot.SimulationTime;
                await Task.Delay(25);
                Assert.Equal(pendingTime, engine.CurrentSnapshot.SimulationTime);
                vm.Integration.Setup.WaitForExternalResult = false;
                Check(true, "future-setup-unchanged", save: false);
                var otherLanguage = language == OpenVisionLanguage.Korean ? OpenVisionLanguage.English : OpenVisionLanguage.Korean;
                OpenVisionLanguageService.SetLanguage(otherLanguage, save: false);
                Settle(window);
                Assert.Equal(otherLanguage == OpenVisionLanguage.Korean ? "대기 중 — 시뮬레이션 정지" : "Waiting — simulation stopped", wait.Text);
                OpenVisionLanguageService.SetLanguage(language, save: false);
                vm.Navigation.OpenSequenceEditorCommand.Execute(null);
                Assert.True(vm.Navigation.IsSequenceWorkspace);
                Check(true, "sequence-automatic-wait");
                vm.Navigation.SelectedWorkspaceIndex = 2;
                Check(true, "vision-automatic-wait");
                vm.Navigation.SelectedWorkspaceIndex = 3;
                Check(true, "history-automatic-wait");
                vm.Navigation.SelectedWorkspaceIndex = 1;
                vm.Navigation.SelectedExecutionTabIndex = 0;
                Settle(window); Assert.False(wait.IsVisible);
                var returnTrace = new List<object>();
                void TraceReturn(string stage)
                {
                    var snapshot = engine.CurrentSnapshot;
                    returnTrace.Add(new { stage, utc = DateTime.UtcNow, vm.Navigation.SelectedWorkspaceIndex, vm.Navigation.SelectedExecutionTabIndex, vm.Navigation.SelectedDocumentTabIndex, footer.IsR24Workspace, runtimeWaiting = vm.IsEquipmentExternalInspectionWaiting, wait.IsVisible, wait.Visibility, footerVisible = footer.IsVisible, windowVisible = window.IsVisible, snapshot.RuntimeGeneration, snapshot.AutomaticRun, cameras = snapshot.Cameras.Select(camera => new { camera.State, camera.CurrentAcquisitionId }).ToArray(), vm.HasUnsavedChanges, journal = ((FixedStepSimulationEngine)engine).EventJournal });
                }
                TraceReturn("before-equipment-return");
                vm.Navigation.SelectedWorkspaceIndex = 0;
                TraceReturn("immediate-equipment-return");
                Settle(window);
                TraceReturn("settled-equipment-return");
                bool returnedWaitVisible = wait.IsVisible;
                if (!returnedWaitVisible)
                {
                    await Task.Delay(10);
                    Settle(window);
                    TraceReturn("diagnostic-10ms-probe");
                }
                WriteObservation($"status-wait-{width}-{language}-equipment-return-trace", returnTrace);
                Assert.True(returnedWaitVisible, JsonSerializer.Serialize(returnTrace));
                Assert.False((await engine.EnqueueCommandAsync(Result(ExternalInspectionResultStatus.Completed, ExternalInspectionOutcome.Pass, stale: true))).IsAccepted);
                Check(true, "stale-result-rejected", save: false);
                Assert.True((await engine.EnqueueCommandAsync(Result(ExternalInspectionResultStatus.Completed, language == OpenVisionLanguage.Korean ? ExternalInspectionOutcome.Pass : ExternalInspectionOutcome.Ng))).IsAccepted);
                await WaitForStatusAsync(() => !engine.CurrentSnapshot.AutomaticRun.IsActive && !wait.IsVisible);
                Check(false, "result-completed");
                await StartWaiting();
                Assert.True((await engine.EnqueueCommandAsync(Result(ExternalInspectionResultStatus.Failed, ExternalInspectionOutcome.ExecutionError))).IsAccepted);
                await WaitForStatusAsync(() => engine.CurrentSnapshot.Cameras.Single().State == VirtualCameraState.Faulted && !wait.IsVisible);
                Check(false, "execution-error");
                Assert.True((await engine.EnqueueCommandAsync(new AbortSequenceCommand(sequenceId))).IsAccepted);
                await StartWaiting();
                Assert.True((await engine.EnqueueCommandAsync(new ResetCommand())).IsAccepted);
                await WaitForStatusAsync(() => !wait.IsVisible);
                Check(false, "reset");
                await StartWaiting();
                Assert.True((await engine.EnqueueCommandAsync(new AbortSequenceCommand(sequenceId))).IsAccepted);
                await WaitForStatusAsync(() => !wait.IsVisible);
                Check(false, "abort");
                Assert.True((await engine.EnqueueCommandAsync(new ResetCommand())).IsAccepted);
                Assert.Equal(payload, JsonSerializer.Serialize(project));
                Assert.False(vm.HasUnsavedChanges || vm.IsRunning);
            }
            finally { window.DataContext = null; window.Close(); OpenVisionLanguageService.SetLanguage(previous, save: false); }
        });
    }

    [Theory]
    [InlineData(1920, 1032, OpenVisionLanguage.Korean)]
    [InlineData(1280, 900, OpenVisionLanguage.Korean)]
    [InlineData(1920, 1032, OpenVisionLanguage.English)]
    [InlineData(1280, 900, OpenVisionLanguage.English)]
    public async Task EquipmentFooterSelectionUsesLocalCaptionAndActualTargetWithoutChangingLegacyContract(int width, int height, OpenVisionLanguage language)
    {
        await _ui.InvokeAsync(() =>
        {
            var previous = OpenVisionLanguageService.CurrentLanguage;
            OpenVisionLanguageService.SetLanguage(language, save: false);
            var project = CreateProject();
            project.Name = "Footer selection display fixture";
            project.Sequences.Clear();
            var belt = project.Layouts[0].Components.Single(item => item.Id == "belt");
            belt.Name = new string('장', 60);
            string payload = JsonSerializer.Serialize(project);
            using var vm = new MainViewModel(project, integrationSettingsPath: Path.Combine(EvidenceRoot(), $"selection-{width}-{language}-integration.json"));
            var window = CreateWindow(vm, width, height);
            SmokeDpiTestHook.PlaceOnTestMonitor(window, width, height);
            window.Show();
            try
            {
                Settle(window);
                var monitor = SmokeDpiTestHook.CaptureMonitorEvidence(window);
                var footer = Descendants(window).OfType<StudioStatusBarView>().Single();
                var selection = Assert.IsType<TextBlock>(footer.FindName("StatusEquipmentSelectionText"));
                var marker = Assert.IsType<TextBlock>(footer.FindName("StatusEquipmentSimulationOnlyText"));
                string Prefix(OpenVisionLanguage lang) => lang == OpenVisionLanguage.Korean ? "선택: " : "Selection: ";
                void Check(string expectedName, string phase)
                {
                    Settle(window);
                    Assert.Equal(Prefix(language) + expectedName, selection.Text);
                    Assert.Null(selection.ToolTip);
                    Assert.Equal(double.PositiveInfinity, selection.MaxWidth);
                    Assert.Equal(TextTrimming.None, selection.TextTrimming);
                    Assert.True(selection.IsVisible && selection.IsEnabled);
                    Assert.Equal("Segoe UI, Malgun Gothic", selection.FontFamily.Source);
                    Assert.Equal(16.675, selection.LineHeight);
                    // R24 has intrinsic nowrap fields; a long selection intentionally overflows Compact.
                    Assert.True(Bounds(selection, footer).Left >= 0);
                    Assert.True(Bounds(selection, footer).Right <= Bounds(marker, footer).Left);
                    var glyphs = StatusGlyphs(VisualTreeHelper.GetDrawing(selection)!).ToArray();
                    Assert.NotEmpty(glyphs);
                    Assert.All(glyphs, glyph => { Assert.Equal(11.5, glyph.GlyphRun.FontRenderingEmSize); Assert.Equal(400, glyph.GlyphRun.GlyphTypeface.Weight.ToOpenTypeWeight()); Assert.Equal("#FF52687C", glyph.ForegroundBrush.ToString()); });
                    Assert.Equal(payload, JsonSerializer.Serialize(project));
                    Assert.False(vm.HasUnsavedChanges || vm.IsRunning);
                    string name = $"status-selection-{width}-{language}-{phase}";
                    Capture(window, name);
                    WriteObservation(name, new { method = "Existing selection commands/owner -> readonly Equipment binding -> current WPF render; no physical input", monitor, selection.Text, tooltip = selection.ToolTip, selectedId = vm.Layout.SelectedItem?.Id, selectedCount = vm.Layout.SelectionCount, unitId = vm.Layout.ActiveUnitId, stationId = vm.Layout.ActiveStationId, bounds = Bounds(selection, footer).ToString(), glyphs = glyphs.Select(glyph => new { glyph.GlyphRun.FontRenderingEmSize, weight = glyph.GlyphRun.GlyphTypeface.Weight.ToOpenTypeWeight(), color = glyph.ForegroundBrush.ToString() }), vm.HasUnsavedChanges });
                }
                vm.ShowEquipmentOverviewCommand.Execute(null);
                Check(language == OpenVisionLanguage.Korean ? "없음" : "None", "empty");
                vm.Layout.Select("belt");
                string legacySingle = vm.SelectionStatusText;
                Check(belt.Name, "component-long");
                Assert.Equal("belt", vm.Layout.SelectedItem!.Id);
                vm.Layout.Select("inspection");
                Check(language == OpenVisionLanguage.Korean ? "제품 모델" : "Product model", "product");
                Assert.Equal("inspection", vm.Layout.SelectedItem!.Id);
                vm.ShowEquipmentStationCommand.Execute("station");
                Check("Station", "station");
                Assert.Equal("station", vm.Layout.ActiveStationId);
                vm.ShowEquipmentUnitCommand.Execute("unit-inspection");
                Check("inspection", "unit");
                Assert.Equal("unit-inspection", vm.Layout.ActiveUnitId);
                vm.ShowEquipmentOverviewCommand.Execute(null);
                Check(language == OpenVisionLanguage.Korean ? "없음" : "None", "return-empty");
                vm.Layout.SelectMany(["belt", "inspection"], "belt");
                Check((language == OpenVisionLanguage.Korean ? "2개 · " : "2 items · ") + belt.Name, "multiple");
                Assert.Equal(2, vm.Layout.SelectionCount);
                vm.Layout.Select("belt");
                Assert.Equal(legacySingle, vm.SelectionStatusText);
                var other = language == OpenVisionLanguage.Korean ? OpenVisionLanguage.English : OpenVisionLanguage.Korean;
                OpenVisionLanguageService.SetLanguage(other, save: false);
                Settle(window);
                Assert.Equal(Prefix(other) + belt.Name, selection.Text);
                OpenVisionLanguageService.SetLanguage(language, save: false);
                vm.Navigation.OpenSequenceEditorCommand.Execute(null);
                Assert.True(vm.Navigation.IsSequenceWorkspace);
                Check(belt.Name, "sequence-long-selection");
                vm.Navigation.SelectedExecutionTabIndex = 0;
                Settle(window);
                Assert.False(selection.IsVisible);
                var legacyView = Descendants(footer).OfType<TextBlock>().Single(text => Grid.GetColumn(text) == 3 && text.Text == legacySingle);
                Assert.True(legacyView.IsVisible);
                Assert.Equal(legacySingle, legacyView.ToolTip);
                vm.Navigation.SelectedWorkspaceIndex = 0;
                Check(belt.Name, "workspace-return");
            }
            finally { window.DataContext = null; window.Close(); OpenVisionLanguageService.SetLanguage(previous, save: false); }
            return true;
        });
    }

    [Theory]
    [InlineData(1920, 1032, OpenVisionLanguage.Korean)]
    [InlineData(1280, 900, OpenVisionLanguage.Korean)]
    [InlineData(1920, 1032, OpenVisionLanguage.English)]
    [InlineData(1280, 900, OpenVisionLanguage.English)]
    public async Task GlobalFaultDrawerKeepsOwnerBindingsAndActualTypographyAcrossLanguageResizeAndClose(int width, int height, OpenVisionLanguage language)
    {
        await _ui.InvokeAsync(() =>
        {
            var previous = OpenVisionLanguageService.CurrentLanguage;
            OpenVisionLanguageService.SetLanguage(language, save: false);
            var project = new ProjectDocumentStore().Load(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Samples", "AutomaticTransferCell.ovmachine")));
            project.Sequences.Clear(); project.Simulation.AutomaticRun = null;
            string original = JsonSerializer.Serialize(project);
            using var vm = new MainViewModel(project);
            vm.Navigation.HideStartupChoice(); vm.Navigation.OpenSequenceEditorCommand.Execute(null); vm.IsRunMode = true;
            var window = CreateWindow(vm, width, height);
            SmokeDpiTestHook.PlaceOnTestMonitor(window, width, height);
            window.Show();
            try
            {
                Settle(window);
                var title = Descendants(window).OfType<StudioTitleBar>().Single();
                var toggle = Assert.IsType<System.Windows.Controls.Primitives.ToggleButton>(title.FindName("FaultSimulationButton"));
                toggle.SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, true);
                Settle(window);
                var popup = Assert.IsType<System.Windows.Controls.Primitives.Popup>(title.FindName("FaultSimulationPopup"));
                var panel = Assert.IsType<FaultSimulationPanelView>(popup.Child);
                var kind = Assert.IsType<ComboBox>(panel.FindName("GlobalFaultKindComboBox"));
                var forced = Assert.IsType<ComboBox>(panel.FindName("GlobalFaultForcedValueComboBox"));
                var inject = Assert.IsType<Button>(panel.FindName("GlobalInjectFaultButton"));
                var clearAll = Assert.IsType<Button>(panel.FindName("GlobalClearAllFaultsButton"));
                var close = Assert.IsType<Button>(panel.FindName("CloseFaultDrawerButton"));
                void CheckGlyphs(TextBlock text, double size, int weight, string foreground)
                {
                    var runs = StatusGlyphs(Assert.IsType<DrawingGroup>(VisualTreeHelper.GetDrawing(text))).ToArray();
                    Assert.NotEmpty(runs);
                    Assert.All(runs, run => { Assert.Equal(size, run.GlyphRun.FontRenderingEmSize); Assert.Equal(weight, run.GlyphRun.GlyphTypeface.Weight.ToOpenTypeWeight()); Assert.Equal(foreground, run.ForegroundBrush.ToString()); });
                }
                void Check(string state)
                {
                    Settle(panel);
                    Assert.True(popup.IsOpen && panel.IsVisible);
                    Assert.Same(vm.FaultManager, panel.DataContext);
                    Assert.Same(vm.FaultManager.InjectCommand, inject.Command);
                    Assert.Same(vm.FaultManager.ClearAllCommand, clearAll.Command);
                    Assert.Equal(390, panel.ActualWidth);
                    var workspace = Assert.IsType<Grid>(window.FindName("WorkspaceRowsGrid"));
                    var toolbar = Assert.IsType<Grid>(window.FindName("CommonWorkspaceToolbar"));
                    var top = panel.PointToScreen(new Point());
                    var expected = workspace.PointToScreen(new Point(workspace.ActualWidth - panel.ActualWidth, toolbar.ActualHeight));
                    Assert.Equal(expected.X, top.X, precision: 1); Assert.Equal(expected.Y, top.Y, precision: 1);
                    Assert.Equal(workspace.ActualHeight - toolbar.ActualHeight, panel.ActualHeight, precision: 1);
                    double size = ShellWindow.GetIsCompactLayout(window) ? 12.5 : 13;
                    Assert.Equal(size, panel.FontSize);
                    CheckGlyphs(Assert.IsType<TextBlock>(panel.FindName("FaultDrawerTitle")), size, 700, "#FF243448");
                    CheckGlyphs(Descendants(close).OfType<TextBlock>().Single(), size, 400, "#FF243448");
                    var note = Descendants(panel).OfType<TextBlock>().Single(text => text.IsVisible && text.Text == OpenVisionLanguageService.T("Shell.FaultSessionHint"));
                    Assert.Equal(17.4, note.LineHeight);
                    CheckGlyphs(note, 12, 400, "#FF52687C");
                    foreach (var combo in new[] { kind, forced })
                    {
                        var caption = Descendants(combo).OfType<TextBlock>().Single(text => text.IsVisible && text.FontFamily.Source == "Segoe UI, Malgun Gothic");
                        CheckGlyphs(caption, 12, 400, combo.IsEnabled ? "#FF52687C" : "#FF718398");
                    }
                    var border = Assert.IsType<Border>(panel.FindName("FaultDrawerBorder"));
                    Assert.Equal("#FFFFFFFF", border.Background.ToString()); Assert.Equal(new Thickness(1, 0, 0, 0), border.BorderThickness);
                    var header = Assert.IsType<Border>(panel.FindName("FaultDrawerHeader"));
                    Assert.Equal(new Thickness(14, 10, 14, 10), header.Padding);
                    Assert.True(new Rect(panel.RenderSize).Contains(Bounds(close, panel)));
                    Capture(panel, $"fault-{width}-{language}-{state}");
                    WriteObservation($"fault-{width}-{language}-{state}", new { top, expected, panel.ActualHeight, panel.FontSize, vm.FaultManager.IsEnabled, monitor = SmokeDpiTestHook.CaptureMonitorEvidence(window) });
                }
                Assert.True(vm.FaultManager.IsEnabled);
                Assert.Same(vm.FaultManager.SelectedKind, kind.SelectedItem);
                Assert.Equal(Visibility.Collapsed, Assert.IsType<TextBlock>(panel.FindName("GlobalFaultTargetHelpText")).Visibility);
                Check("normal");
                var dropdown = Assert.IsType<System.Windows.Controls.Primitives.Popup>(kind.Template.FindName("Popup", kind));
                kind.SetCurrentValue(ComboBox.IsDropDownOpenProperty, true); Settle(window);
                Assert.True(dropdown.IsOpen && popup.IsOpen);
                kind.SetCurrentValue(ComboBox.IsDropDownOpenProperty, false); Settle(window);
                Assert.False(dropdown.IsOpen); Assert.True(popup.IsOpen);
                Assert.Same(vm.FaultManager.SelectedKind, kind.SelectedItem);
                var initialKind = vm.FaultManager.SelectedKind;
                var initialValue = vm.FaultManager.SelectedForcedValue;
                OpenVisionLanguageService.SetLanguage(language == OpenVisionLanguage.Korean ? OpenVisionLanguage.English : OpenVisionLanguage.Korean, save: false);
                Settle(window);
                Assert.Contains(Descendants(kind).OfType<TextBlock>(), text => text.IsVisible && text.Text == OpenVisionLanguageService.T("Fault.Kind.StuckDigitalInput"));
                Assert.Contains(Descendants(forced).OfType<TextBlock>(), text => text.IsVisible && text.Text == OpenVisionLanguageService.T("Fault.ForceOff"));
                Assert.Same(initialKind, vm.FaultManager.SelectedKind); Assert.Same(initialValue, vm.FaultManager.SelectedForcedValue);
                Check("language");
                forced.SetCurrentValue(System.Windows.Controls.Primitives.Selector.SelectedItemProperty, vm.FaultManager.ForcedValueOptions.Single(option => option.Value));
                Assert.True(vm.FaultManager.SelectedForcedValue.Value);
                vm.FaultManager.SelectedForcedValue = initialValue;
                Assert.Same(initialValue, forced.SelectedItem);
                vm.FaultManager.IsEnabled = false; Settle(panel);
                Assert.False(kind.IsEnabled || inject.IsEnabled || inject.Command!.CanExecute(null));
                Check("readonly");
                vm.FaultManager.IsEnabled = true;
                window.Width = width == 1920 ? 1280 : 1920; window.Height = width == 1920 ? 900 : 1032;
                Settle(window); Check("resized");
                close.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Settle(window);
                Assert.False(popup.IsOpen || toggle.IsChecked == true);
                toggle.SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, true); Settle(window); Check("reopened");
                Assert.Equal(original, JsonSerializer.Serialize(project)); Assert.False(vm.IsRunning || vm.HasUnsavedChanges);
                // Isolate the View's Unloaded cleanup from the existing asynchronous session-close workflow.
                window.DataContext = null;
                window.Close();
                Settle(window);
                WriteObservation($"fault-{width}-{language}-owner-closed", new { method = "View-only owner Unloaded after DataContext detachment; not the asynchronous product-close workflow", popup.IsOpen, panel.IsVisible });
                Assert.False(popup.IsOpen);
            }
            finally { window.DataContext = null; window.Close(); OpenVisionLanguageService.SetLanguage(previous, save: false); }
            return true;
        });
    }

    [Theory]
    [InlineData(1920, 1032)]
    [InlineData(1280, 900)]
    public async Task GlobalFaultDrawerUsesRealTargetAndInjectClearSnapshotWithoutSaving(int width, int height)
    {
        await _ui.InvokeTaskAsync(async () =>
        {
            var project = new ProjectDocumentStore().Load(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Samples", "AutomaticTransferCell.ovmachine")));
            project.Sequences.Clear(); project.Simulation.AutomaticRun = null;
            string original = JsonSerializer.Serialize(project);
            using var vm = new MainViewModel(project);
            vm.Navigation.HideStartupChoice(); vm.Navigation.OpenSequenceEditorCommand.Execute(null); vm.IsRunMode = true;
            var window = CreateWindow(vm, width, height); SmokeDpiTestHook.PlaceOnTestMonitor(window, width, height); window.Show();
            try
            {
                Settle(window);
                var title = Descendants(window).OfType<StudioTitleBar>().Single();
                Assert.IsType<System.Windows.Controls.Primitives.ToggleButton>(title.FindName("FaultSimulationButton")).SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, true);
                Settle(window);
                var popup = Assert.IsType<System.Windows.Controls.Primitives.Popup>(title.FindName("FaultSimulationPopup"));
                var panel = Assert.IsType<FaultSimulationPanelView>(popup.Child);
                var target = Assert.IsType<ComboBox>(panel.FindName("GlobalFaultTargetComboBox"));
                Assert.NotEmpty(vm.FaultManager.Targets);
                target.SetCurrentValue(System.Windows.Controls.Primitives.Selector.SelectedItemProperty, vm.FaultManager.Targets.Last());
                Assert.Same(target.SelectedItem, vm.FaultManager.SelectedTarget);
                string targetId = vm.FaultManager.SelectedTarget!.Id;
                var forced = Assert.IsType<ComboBox>(panel.FindName("GlobalFaultForcedValueComboBox"));
                forced.SetCurrentValue(System.Windows.Controls.Primitives.Selector.SelectedItemProperty, vm.FaultManager.ForcedValueOptions.Single(option => option.Value));
                var inject = Assert.IsType<Button>(panel.FindName("GlobalInjectFaultButton"));
                Assert.True(inject.Command!.CanExecute(null)); inject.Command.Execute(null);
                await WaitForStatusAsync(() => !vm.FaultManager.IsOperationPending && vm.FaultManager.ActiveFaults.Any(fault => fault.TargetId == targetId));
                Assert.True(vm.FaultManager.ActiveFaults.Single(fault => fault.TargetId == targetId).ForcedValue);
                Settle(panel);
                var active = Assert.IsType<ListBox>(panel.FindName("GlobalActiveFaultList"));
                var activeItem = Assert.IsType<ListBoxItem>(active.ItemContainerGenerator.ContainerFromIndex(0));
                Assert.True(activeItem.IsSelected); Assert.Same(active.SelectedItem, vm.FaultManager.SelectedActiveFault);
                var activeBorder = Assert.IsType<Border>(activeItem.Template.FindName("FaultItemBorder", activeItem));
                Assert.Equal("#FFE4F4EF", activeBorder.Background.ToString()); Assert.Equal("#FF087F73", activeBorder.BorderBrush.ToString());
                var activeGlyphs = Descendants(activeItem).OfType<TextBlock>().SelectMany(text => StatusGlyphs(VisualTreeHelper.GetDrawing(text))).ToArray();
                Assert.NotEmpty(activeGlyphs); Assert.All(activeGlyphs, glyph =>
                { Assert.Equal(12, glyph.GlyphRun.FontRenderingEmSize); Assert.Equal(FontWeights.Normal, glyph.GlyphRun.GlyphTypeface.Weight); Assert.Equal("#FF087F73", glyph.ForegroundBrush.ToString()); });
                Capture(panel, $"fault-runtime-{width}-injected");
                active.SetCurrentValue(System.Windows.Controls.Primitives.Selector.SelectedItemProperty, null); Settle(panel);
                Assert.Null(vm.FaultManager.SelectedActiveFault); Assert.False(activeItem.IsSelected);
                // Clearing selection keeps the current pointer and keyboard focus states.
                var expectedBackground = activeItem.IsMouseOver ? (Brush)activeItem.FindResource("Surface.Hover") : Brushes.Transparent;
                var expectedBorder = activeItem.IsKeyboardFocused ? (Brush)activeItem.FindResource("Accent.Primary") : Brushes.Transparent;
                Assert.Equal(expectedBackground.ToString(), activeBorder.Background.ToString());
                Assert.Equal(expectedBorder.ToString(), activeBorder.BorderBrush.ToString());
                Console.WriteLine($"Fault unselected · hover={activeItem.IsMouseOver} · keyboardFocus={activeItem.IsKeyboardFocused} · background={activeBorder.Background}");
                Capture(panel, $"fault-runtime-{width}-unselected");
                active.SetCurrentValue(System.Windows.Controls.Primitives.Selector.SelectedItemProperty, active.Items[0]); Settle(panel);
                Assert.Same(active.SelectedItem, vm.FaultManager.SelectedActiveFault); Assert.True(activeItem.IsSelected);
                var clear = Assert.IsType<Button>(panel.FindName("GlobalClearAllFaultsButton"));
                Assert.True(clear.Command!.CanExecute(null)); clear.Command.Execute(null);
                await WaitForStatusAsync(() => !vm.FaultManager.IsOperationPending && vm.FaultManager.ActiveFaults.Count == 0);
                Settle(panel); Assert.True(inject.Command.CanExecute(null)); Capture(panel, $"fault-runtime-{width}-cleared");
                Assert.Equal(original, JsonSerializer.Serialize(project)); Assert.False(vm.IsRunning || vm.HasUnsavedChanges);
                Assert.IsType<Button>(panel.FindName("CloseFaultDrawerButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.False(popup.IsOpen);
            }
            finally { window.DataContext = null; window.Close(); }
        });
    }

    [Theory]
    [InlineData(1920, 1032, OpenVisionLanguage.Korean)]
    [InlineData(1280, 900, OpenVisionLanguage.Korean)]
    [InlineData(1920, 1032, OpenVisionLanguage.English)]
    [InlineData(1280, 900, OpenVisionLanguage.English)]
    public async Task R24FooterKeepsActualFieldsAndTopSaveStateAcrossDocumentWorkspaces(int width, int height, OpenVisionLanguage language)
    {
        await _ui.InvokeTaskAsync(async () =>
        {
            var previous = OpenVisionLanguageService.CurrentLanguage;
            OpenVisionLanguageService.SetLanguage(language, save: false);
            var project = CreateProject();
            project.Layouts.SelectMany(layout => layout.Components).Single(component => component.Id == "belt").Name = new string('장', 60);
            using var vm = new MainViewModel(project, integrationSettingsPath: Path.Combine(EvidenceRoot(), $"workspace-{width}-{language}-integration.json"));
            var window = CreateWindow(vm, width, height);
            SmokeDpiTestHook.PlaceOnTestMonitor(window, width, height);
            window.Show();
            try
            {
                await WaitForStatusAsync(() => vm.IsSceneEditable);
                var monitor = SmokeDpiTestHook.CaptureMonitorEvidence(window);
                Assert.True(monitor.WindowIntersectsMonitor);
                Assert.Equal(96, VisualTreeHelper.GetDpi(window).PixelsPerInchX);
                var footer = Descendants(window).OfType<StudioStatusBarView>().Single();
                var title = Descendants(window).OfType<StudioTitleBar>().Single();
                var titleCaption = Assert.IsType<TextBlock>(title.FindName("TitleSaveStateText"));
                var selection = Assert.IsType<TextBlock>(footer.FindName("StatusEquipmentSelectionText"));
                var marker = Assert.IsType<TextBlock>(footer.FindName("StatusEquipmentSimulationOnlyText"));
                var fixedStep = Descendants(footer).OfType<TextBlock>().Single(field => field.GetBindingExpression(TextBlock.TextProperty)?.ParentBinding.Path?.Path == nameof(vm.FixedStepStatusText));
                var names = new[] { "StatusSimulationTimeText", "StatusSimulationSpeedText", "StatusEquipmentVerdictText", "StatusEquipmentTwoDText", "StatusEquipmentThreeDText", "StatusEquipmentAiText" };
                string payload = JsonSerializer.Serialize(project);
                foreach (int index in new[] { 0, 2, 1, 3, 0 })
                {
                    vm.Navigation.SelectedDocumentTabIndex = index;
                    vm.Layout.SelectedItem = vm.Layout.Items.Single(item => item.Component?.Id == "belt");
                    Settle(window);
                    Assert.Same(vm, footer.DataContext);
                    Assert.Equal(24, footer.ActualHeight);
                    Assert.Equal(window.ActualWidth, footer.ActualWidth);
                    var border = Assert.IsType<Border>(footer.Content);
                    Assert.Equal("#FFF8FAFC", border.Background.ToString());
                    Assert.Equal("#FFDCE3EB", border.BorderBrush.ToString());
                    Assert.Equal(new Thickness(0, 1, 0, 0), border.BorderThickness);
                    var fields = names.Select(name => Assert.IsType<TextBlock>(footer.FindName(name))).ToArray();
                    Assert.Equal(new[] { vm.SimulationSecondsStatusText, vm.SimulationSpeedText, vm.EquipmentVerdictStatusText, vm.EquipmentTwoDStatusText, vm.EquipmentThreeDStatusText, vm.EquipmentAiStatusText }, fields.Select(field => field.Text));
                    Assert.Equal(10, Bounds(fields[0], footer).Left, precision: 1);
                    for (int fieldIndex = 1; fieldIndex < fields.Length; fieldIndex++) Assert.Equal(14, Bounds(fields[fieldIndex], footer).Left - Bounds(fields[fieldIndex - 1], footer).Right, precision: 1);
                    foreach (var field in fields.Append(selection).Append(marker))
                    {
                        Assert.True(field.IsVisible);
                        if (field != selection && field != marker) Assert.True(new Rect(footer.RenderSize).Contains(Bounds(field, footer)), field.Name + " exceeded the footer");
                        Assert.Equal(16.675, field.LineHeight);
                        if (!new Rect(footer.RenderSize).IntersectsWith(Bounds(field, footer))) continue;
                        var glyphs = StatusGlyphs(VisualTreeHelper.GetDrawing(field)!).ToArray();
                        Assert.NotEmpty(glyphs);
                        Assert.All(glyphs, glyph => { Assert.Equal(11.5, glyph.GlyphRun.FontRenderingEmSize); Assert.Equal(400, glyph.GlyphRun.GlyphTypeface.Weight.ToOpenTypeWeight()); Assert.Equal("#FF52687C", glyph.ForegroundBrush.ToString()); });
                    }
                    Assert.Equal(vm.EquipmentSelectionStatusText, selection.Text);
                    Assert.Contains(new string('장', 60), selection.Text);
                    Assert.Null(selection.ToolTip);
                    Assert.Equal(double.PositiveInfinity, selection.MaxWidth);
                    Assert.Equal(TextTrimming.None, selection.TextTrimming);
                    Assert.InRange(selection.ActualWidth, 700, 790);
                    Assert.True(Bounds(fields[^1], footer).Right <= Bounds(selection, footer).Left);
                    Assert.True(Bounds(selection, footer).Right <= Bounds(marker, footer).Left);
                    foreach (string name in new[] { "CommonSaveStateText", "StatusRecipeSummaryText" }) Assert.False(Assert.IsType<TextBlock>(footer.FindName(name)).IsVisible);
                    Assert.False(fixedStep.IsVisible);
                    Assert.All(Descendants(footer).OfType<Button>(), button => Assert.False(button.IsVisible));
                    Assert.True(titleCaption.IsVisible);
                    Assert.Equal(vm.ProjectSaveStateText, titleCaption.Text);
                    var menu = Descendants(title).OfType<MainMenuView>().Single();
                    Assert.Contains(Descendants(menu).OfType<MenuItem>().SelectMany(item => item.Items.OfType<MenuItem>()), item => ReferenceEquals(item.Command, vm.Navigation.ToggleEvidenceDrawerCommand));
                    bool expanded = vm.Navigation.IsEvidenceExpanded;
                    vm.Navigation.ToggleEvidenceDrawerCommand.Execute(null);
                    Assert.Equal(!expanded, vm.Navigation.IsEvidenceExpanded);
                    vm.Navigation.ToggleEvidenceDrawerCommand.Execute(null);
                    Assert.Equal(expanded, vm.Navigation.IsEvidenceExpanded);
                    Capture(window, $"footer-workspace-{width}-{language}-{index}");
                    WriteObservation($"footer-workspace-{width}-{language}-{index}", new { monitor, workspace = index, footerHeight = footer.ActualHeight, selection.Text, fields = fields.Select(field => new { field.Name, field.Text, bounds = Bounds(field, footer).ToString() }), save = titleCaption.Text, vm.IsRunning, vm.HasUnsavedChanges });
                    Assert.Equal(payload, JsonSerializer.Serialize(project));
                    Assert.False(vm.HasUnsavedChanges || vm.IsRunning || vm.HasSavedProjectFile);
                }
                vm.Navigation.SelectedWorkspaceIndex = 1;
                vm.Navigation.SelectedExecutionTabIndex = 0;
                Settle(window);
                Assert.False(footer.IsR24Workspace);
                Assert.Equal(29, footer.ActualHeight);
                Assert.True(fixedStep.IsVisible);
                Assert.True(Assert.IsType<TextBlock>(footer.FindName("CommonSaveStateText")).IsVisible);
                vm.Navigation.SelectedDocumentTabIndex = 0;
                Settle(window);
                Assert.Equal(24, footer.ActualHeight);
                Assert.Equal(payload, JsonSerializer.Serialize(project));
                Assert.False(vm.HasUnsavedChanges || vm.IsRunning || vm.HasSavedProjectFile);
            }
            finally { window.DataContext = null; window.Close(); OpenVisionLanguageService.SetLanguage(previous, save: false); }
        });
    }

    private static IEnumerable<GlyphRunDrawing> StatusGlyphs(Drawing drawing) => drawing is GlyphRunDrawing glyph ? [glyph]
        : drawing is DrawingGroup group ? group.Children.SelectMany(StatusGlyphs) : [];
    private static async Task WaitForStatusAsync(Func<bool> ready)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!ready() && DateTime.UtcNow < deadline) await Task.Delay(10);
        Assert.True(ready(), "The status projection did not arrive within five seconds.");
    }

    private static MachineProjectDocument CreateProject()
    {
        var project = EquipmentMaterialFlowViewModelTests.CreateProject();
        project.Name = new string('R', 80);
        project.Sequences = [new SequenceDefinition { Id = new string('s', 80), Name = new string('N', 80), Steps = [new SequenceStepDefinition { Id = "complete", Name = "Complete", Action = SequenceStepAction.Complete }] }];
        return project;
    }
    private static ShellWindow CreateWindow(MainViewModel vm, double width, double height) =>
        new() { DataContext = vm, Width = width, Height = height, ShowInTaskbar = false, ShowActivated = false, UseLayoutRounding = true };
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
    private static Rect Bounds(FrameworkElement element, Visual ancestor) => element.TransformToAncestor(ancestor).TransformBounds(new Rect(element.RenderSize));
    private static void Settle(FrameworkElement element)
    {
        element.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        element.UpdateLayout();
        element.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    }
    private static string EvidenceRoot()
    {
        string root = Environment.GetEnvironmentVariable("OPENVISIONLAB_MACHINE_STUDIO_UI_EVIDENCE_DIR") ?? @"D:\OpenVisionLab-TestData\Machine\ui-regression\common-status";
        Directory.CreateDirectory(root);
        return root;
    }
    private static void Capture(FrameworkElement element, string name)
    {
        Settle(element);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth), (int)Math.Ceiling(element.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(EvidenceRoot(), name + ".png"));
        encoder.Save(stream);
    }
    private static void WriteObservation(string name, object result) =>
        File.WriteAllText(Path.Combine(EvidenceRoot(), name + ".json"), JsonSerializer.Serialize(new { assembly = typeof(MainViewModel).Assembly.Location, dllHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(MainViewModel).Assembly.Location))), result }, new JsonSerializerOptions { WriteIndented = true }));
}
