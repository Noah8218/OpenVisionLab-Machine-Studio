using System.Windows;
using System.Windows.Controls;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using OpenVisionLab;
using OpenVisionLab.Machine.Core.Axes;
using OpenVisionLab.Machine.Core.Layouts;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Simulation.Axis;
using OpenVisionLab.Machine.Simulation.Commands;
using OpenVisionLab.Machine.Simulation.Engine;
using OpenVisionLab.Machine.Simulation.Sequences;
using OpenVisionLab.MachineStudio.View.Inspector;
using OpenVisionLab.MachineStudio.Model;
using OpenVisionLab.MachineStudio.ViewModel;
using Xunit;
using NumberBox = Wpf.Ui.Controls.NumberBox;

namespace OpenVisionLab.MachineStudio.Tests;

[Collection(StudioUiTestCollection.Name)]
public sealed class ManualCommissioningEntryViewTests
{
    private readonly StudioUiTestHost _ui;

    public ManualCommissioningEntryViewTests(StudioUiTestHost ui) => _ui = ui;

    [Theory]
    [InlineData(300, OpenVisionLanguage.Korean)]
    [InlineData(420, OpenVisionLanguage.Korean)]
    [InlineData(300, OpenVisionLanguage.English)]
    [InlineData(420, OpenVisionLanguage.English)]
    public async Task AxisDefinitionInspectorPreservesTuningBindingsAndVisibleValidationWhenCollapsed(double width, OpenVisionLanguage language)
    {
        await _ui.InvokeTaskAsync(async () =>
        {
            var previousLanguage = OpenVisionLanguageService.CurrentLanguage;
            OpenVisionLanguageService.SetLanguage(language, save: false);
            try
            {
                var project = new ProjectDocumentStore().Load(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Samples", "R19InspectionFlow.ovmachine")));
                using var vm = new MainViewModel(project);
                vm.IsDesignMode = true;
                var deadline = DateTime.UtcNow.AddSeconds(5);
                while (!vm.IsDesignMode && DateTime.UtcNow < deadline) await Task.Delay(10);
                Assert.True(vm.IsDesignMode);
                vm.ProjectTree.SelectedNode = vm.ProjectTree.Roots.Single().Children.Single(node => node.Kind == TreeNodeKind.Axes).Children[0];
                var editor = Assert.IsType<AxisDriveTuningEditorViewModel>(vm.AxisDriveTuningEditor);
                var view = new RightToolRegionView { DataContext = vm };
                using var inputSource = new System.Windows.Interop.HwndSource(new System.Windows.Interop.HwndSourceParameters("AxisDefinitionInspectorTest")
                {
                    Width = (int)width,
                    Height = 900,
                    WindowStyle = unchecked((int)0x80000000)
                });
                inputSource.RootVisual = view;
                Arrange(view, width);
                var advanced = Assert.IsType<Expander>(view.FindName("AxisAdvancedDriveSettings"));
                var validation = Assert.IsType<TextBlock>(view.FindName("AxisTuningValidationMessage"));
                Assert.False(advanced.IsExpanded);
                Assert.True(validation.IsVisible);
                Capture(view, width, language, "axis-drive-collapsed");

                advanced.IsExpanded = true;
                await Task.Delay(250);
                Arrange(view, width);
                var velocity = Descendants(view).OfType<NumberBox>().Single(control => System.Windows.Automation.AutomationProperties.GetAutomationId(control) == "AxisMaxVelocityNumberBox");
                Assert.True(velocity.IsVisible);
                velocity.SetCurrentValue(NumberBox.ValueProperty, 25d);
                BindingOperations.GetBindingExpression(velocity, NumberBox.ValueProperty)!.UpdateSource();
                Assert.Equal(25, editor.MaxVelocity);
                editor.MaxVelocity = 27;
                Arrange(view, width);
                Assert.Equal(27, velocity.Value);
                Capture(view, width, language, "axis-drive-expanded");

                advanced.IsExpanded = false;
                await Task.Delay(250);
                editor.HomePosition = editor.SoftLimitMax + 1;
                Arrange(view, width);
                Assert.True(editor.HasValidationErrors);
                Assert.True(validation.IsVisible);
                Assert.Equal(editor.ValidationMessage, validation.Text);
                Assert.False(string.IsNullOrWhiteSpace(validation.Text));
                Capture(view, width, language, "axis-drive-invalid-collapsed");
                editor.HomePosition = editor.SoftLimitMin;
                Arrange(view, width);
                Assert.False(editor.HasValidationErrors);
                Assert.False(vm.IsRunning);
            }
            finally { OpenVisionLanguageService.SetLanguage(previousLanguage, save: false); }
        });
    }

    [Theory]
    [InlineData(300)]
    [InlineData(420)]
    public async Task InspectorSelectionAndTabChangesResetScrollWithoutExecutingOrDiscardingInput(double width)
    {
        await _ui.InvokeAsync(() =>
        {
            var project = new ProjectDocumentStore().Load(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Samples", "R19InspectionFlow.ovmachine")));
            using var vm = new MainViewModel(project);
            vm.Navigation.IsEquipmentWorkspace = true;
            var axis = vm.Layout.Items.First(item => item.Component?.Kind == LayoutComponentKind.LinearStage);
            var camera = vm.Layout.Items.First(item => item.Component?.Kind == LayoutComponentKind.Camera);
            vm.Layout.Select(axis.Id);
            var view = new RightToolRegionView { DataContext = vm };
            Arrange(view, width, 430);
            var designScroll = Assert.IsType<ScrollViewer>(view.FindName("DesignInspectorScrollViewer"));
            var runScroll = Assert.IsType<ScrollViewer>(view.FindName("RunInspectorScrollViewer"));
            designScroll.ScrollToBottom();
            Arrange(view, width, 430);
            Assert.True(designScroll.VerticalOffset > 0);
            vm.OpenEquipmentDriveTabCommand.Execute(null);
            Arrange(view, width, 430);
            Assert.Equal(0, designScroll.VerticalOffset);
            vm.OpenEquipmentSinglePartCheckCommand.Execute(null);
            Arrange(view, width, 430);
            var advanced = Descendants(view).OfType<Expander>().Single(expander => expander.Name == "AxisAdvancedMotionControls");
            advanced.IsExpanded = true;
            vm.AxisTargetPositionText = "17";
            runScroll.ScrollToBottom();
            Arrange(view, width, 430);
            Assert.True(runScroll.VerticalOffset > 0);
            vm.Layout.Select(camera.Id);
            Arrange(view, width, 430);
            Assert.Equal(0, runScroll.VerticalOffset);
            vm.Layout.Select(axis.Id);
            Arrange(view, width, 430);
            Assert.Equal(0, runScroll.VerticalOffset);
            Assert.Equal("17", vm.AxisTargetPositionText);
            vm.OpenEquipmentPropertiesCommand.Execute(null);
            Arrange(view, width, 430);
            Assert.Equal(0, designScroll.VerticalOffset);
            Assert.True(vm.IsDesignMode);
            Assert.False(vm.IsRunning || vm.HasUnsavedChanges);
            return true;
        });
    }

    [Theory]
    [InlineData(300, OpenVisionLanguage.Korean)]
    [InlineData(420, OpenVisionLanguage.Korean)]
    [InlineData(300, OpenVisionLanguage.English)]
    [InlineData(420, OpenVisionLanguage.English)]
    public async Task InspectorEntryShowsSelectedTargetOwnershipAndClosingStateWithoutRunning(double width, OpenVisionLanguage language)
    {
        await _ui.InvokeTaskAsync(async () =>
        {
            var previousLanguage = OpenVisionLanguageService.CurrentLanguage;
            OpenVisionLanguageService.SetLanguage(language, save: false);
            try
            {
                var project = new ProjectDocumentStore().Load(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Samples", "AutomaticTransferCell.ovmachine")));
                using var vm = new MainViewModel(project);
                vm.Navigation.IsEquipmentWorkspace = true;
                var axis = vm.Layout.Items.First(item => item.Component?.Kind == LayoutComponentKind.LinearStage);
                vm.Layout.Select(axis.Id);
                var deadline = DateTime.UtcNow.AddSeconds(5);
                while (!vm.CanStartManualEquipmentControl && DateTime.UtcNow < deadline) await Task.Delay(10);
                Assert.True(vm.CanStartManualEquipmentControl, vm.ManualEquipment.ManualControlHintText);
                vm.OpenEquipmentSinglePartCheckCommand.Execute(null);
                var view = new RightToolRegionView { DataContext = vm };
                Arrange(view, width);
                var entry = Assert.IsType<StackPanel>(view.FindName("ManualCommissioningEntryPanel"));
                var button = Assert.IsType<Button>(view.FindName("EnterManualCommissioningButton"));
                var owner = Assert.IsType<TextBlock>(view.FindName("ManualEntryControlOwnerText"));
                var hint = Assert.IsType<TextBlock>(view.FindName("ManualEntryHintText"));
                Assert.Equal(Visibility.Visible, entry.Visibility);
                Assert.Same(vm.ManualEquipment, entry.DataContext);
                Assert.Same(vm.StartManualEquipmentControlCommand, button.Command);
                Assert.Equal(vm.ManualEquipment.StartManualControlText, button.Content);
                Assert.Equal(vm.ManualEquipment.ManualControlOwnerText, owner.Text);
                Assert.Equal(vm.ManualEquipment.ManualControlHintText, hint.Text);
                Assert.True(button.IsEnabled);
                Assert.True(owner.ActualWidth > 0 && hint.ActualHeight > 0);
                Assert.Equal(TextWrapping.Wrap, hint.TextWrapping);
                Assert.DoesNotContain("Equipment.ManualHint.", hint.Text);
                Assert.True(button.ActualWidth <= width);
                var tabs = Descendants(view).OfType<Button>().Where(candidate =>
                    ReferenceEquals(candidate.Command, vm.OpenEquipmentPropertiesCommand)
                    || ReferenceEquals(candidate.Command, vm.OpenEquipmentDriveTabCommand)
                    || ReferenceEquals(candidate.Command, vm.OpenEquipmentSinglePartCheckCommand)).ToArray();
                Assert.Equal(3, tabs.Length);
                foreach (var tab in tabs)
                {
                    var caption = Descendants(tab).OfType<TextBlock>().Single(text => text.Text == (string)tab.Content);
                    Assert.Equal(TextWrapping.Wrap, caption.TextWrapping);
                    Assert.True(caption.DesiredSize.Width <= caption.ActualWidth + 0.5);
                    Assert.True(caption.DesiredSize.Height <= caption.ActualHeight + 0.5);
                }
                Capture(view, width, language, "ready");

                vm.ManualEquipment.SetSessionCloseAdmission(true);
                Arrange(view, width);
                Assert.False(button.IsEnabled);
                Assert.Equal(1, button.Opacity);
                Assert.Equal(vm.ManualEquipment.ManualControlHintText, hint.Text);
                Assert.DoesNotContain("Equipment.ManualHint.", hint.Text);
                Capture(view, width, language, "closing");
                vm.ManualEquipment.SetSessionCloseAdmission(false);
                Arrange(view, width);
                Assert.True(button.IsEnabled);
                Assert.True(vm.IsDesignMode);
                Assert.False(vm.IsRunning || vm.HasUnsavedChanges);

                var frame = vm.Layout.Items.First(item => item.Component?.Kind == LayoutComponentKind.MachineFrame);
                vm.Layout.Select(frame.Id);
                Arrange(view, width);
                Assert.Equal(Visibility.Collapsed, entry.Visibility);
                Assert.False(vm.StartManualEquipmentControlCommand.CanExecute(null));
            }
            finally { OpenVisionLanguageService.SetLanguage(previousLanguage, save: false); }
        });
    }

    [Theory]
    [InlineData(300, OpenVisionLanguage.Korean)]
    [InlineData(420, OpenVisionLanguage.Korean)]
    [InlineData(300, OpenVisionLanguage.English)]
    [InlineData(420, OpenVisionLanguage.English)]
    public async Task ManualAxisPanelKeepsMotionCaptionsAndSignedLimitsVisibleAcrossInputRecovery(double width, OpenVisionLanguage language)
    {
        await _ui.InvokeAsync(() =>
        {
            var previousLanguage = OpenVisionLanguageService.CurrentLanguage;
            OpenVisionLanguageService.SetLanguage(language, save: false);
            try
            {
                var dispatchCount = 0;
                using var vm = new AxisCommissioningViewModel((command, _) =>
                {
                    dispatchCount++;
                    return Task.FromResult(new SimulationCommandResult(command.CommandId, true, 1, TimeSpan.Zero, SimulationCommandErrorCode.None, null));
                }, exception => throw exception);
                var definition = new VirtualAxisDefinition
                {
                    Id = "axis-signed", Name = "Signed axis", Unit = "mm",
                    SoftLimitMin = -123456.789, SoftLimitMax = 123456.789,
                    HomePosition = -17.25, MaxVelocity = 50
                };
                var projection = new AxisCommissioningProjection(new AxisSnapshot(definition.Id, definition.Name, AxisState.Idle, 12.5, 0),
                    definition, true, true, false, false, false, true, SimulationControlOwner.Manual, false, false);
                vm.ApplyProjection(projection);
                var view = new AxisCommissioningPanelView { DataContext = new { AxisCommissioning = vm } };
                var parent = new RightToolRegionView
                {
                    Content = new Border { Background = (Brush)Application.Current.FindResource("Surface.Panel"), Child = view }
                };
                Arrange(parent, width);
                Capture(parent, width, language, "manual-axis-ready");
                foreach (var name in new[] { "MoveAxisAbsoluteButton", "JogNegativeButton", "JogPositiveButton", "HomeAxisButton", "StopAxisMotionButton" })
                {
                    var button = Assert.IsType<Button>(view.FindName(name));
                    var caption = Descendants(button).OfType<TextBlock>().Single(text => text.Text == (string)button.Content);
                    Assert.True(caption.DesiredSize.Width <= caption.ActualWidth + 0.5, $"{name}: {caption.Text} {caption.DesiredSize.Width} > {caption.ActualWidth}");
                    Assert.True(caption.DesiredSize.Height <= caption.ActualHeight + 0.5);
                }
                foreach (var text in Descendants(view).OfType<TextBlock>().Where(text =>
                    text.Text == OpenVisionLanguageService.T("Axis.SoftLimits") || text.Text == OpenVisionLanguageService.T("Axis.HomePosition")
                    || text.Text == OpenVisionLanguageService.T("Axis.MotionInterlock") || text.Text == OpenVisionLanguageService.T("Axis.DriveAlarm")
                    || text.Text == vm.CurrentAxisLimitsText || text.Text == vm.CurrentAxisHomeText))
                {
                    var glyphs = new FormattedText(text.Text, CultureInfo.CurrentUICulture, text.FlowDirection,
                        new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch), text.FontSize,
                        text.Foreground, VisualTreeHelper.GetDpi(text).PixelsPerDip);
                    var grid = Assert.IsType<Grid>(VisualTreeHelper.GetParent(text));
                    var availableWidth = grid.ColumnDefinitions[Grid.GetColumn(text)].ActualWidth - text.Margin.Left - text.Margin.Right;
                    if (text.TextWrapping == TextWrapping.NoWrap)
                        Assert.True(glyphs.WidthIncludingTrailingWhitespace <= availableWidth + 0.5, $"{text.Text}: {glyphs.WidthIncludingTrailingWhitespace} > cell {availableWidth}");
                    else
                    {
                        Assert.True(text.ActualWidth <= availableWidth + 0.5);
                        glyphs.MaxTextWidth = availableWidth;
                        Assert.True(glyphs.Height <= text.ActualHeight + 0.5, $"{text.Text}: wrapped height {glyphs.Height} > {text.ActualHeight}");
                    }
                }
                var input = Assert.IsType<TextBox>(view.FindName("AxisTargetPositionTextBox"));
                input.SetCurrentValue(TextBox.TextProperty, "NaN");
                BindingOperations.GetBindingExpression(input, TextBox.TextProperty)!.UpdateSource();
                Arrange(parent, width);
                Assert.Equal("NaN", vm.AxisTargetPositionText);
                Assert.True(vm.HasAxisTargetPositionError);
                Assert.False(vm.MoveAxisAbsoluteCommand.CanExecute(null));
                Assert.False(Assert.IsType<Button>(view.FindName("MoveAxisAbsoluteButton")).IsEnabled);
                var validation = Assert.IsType<TextBlock>(view.FindName("AxisTargetValidationText"));
                Assert.Equal(Visibility.Visible, validation.Visibility);
                Assert.Equal(vm.AxisTargetPositionValidationText, validation.Text);
                Capture(parent, width, language, "manual-axis-invalid");
                input.SetCurrentValue(TextBox.TextProperty, "-123456.789");
                BindingOperations.GetBindingExpression(input, TextBox.TextProperty)!.UpdateSource();
                Arrange(parent, width);
                Assert.True(vm.IsAxisTargetPositionValid);
                Assert.True(vm.MoveAxisAbsoluteCommand.CanExecute(null));
                vm.AxisTargetPositionText = "123456.790";
                Arrange(parent, width);
                Assert.Equal("123456.790", input.Text);
                Assert.True(vm.HasAxisTargetPositionError);
                Assert.False(vm.MoveAxisAbsoluteCommand.CanExecute(null));
                vm.AxisTargetPositionText = "123456.789";
                Arrange(parent, width);
                Assert.Equal("123456.789", input.Text);
                Assert.False(vm.HasAxisTargetPositionError);
                Assert.True(vm.MoveAxisAbsoluteCommand.CanExecute(null));
                Capture(parent, width, language, "manual-axis-recovered");
                vm.ApplyProjection(projection with { IsRunMode = false });
                Arrange(parent, width);
                Assert.False(vm.MoveAxisAbsoluteCommand.CanExecute(null));
                Assert.False(vm.HomeAxisCommand.CanExecute(null));
                Assert.False(vm.CanJogAxis);
                vm.ApplyProjection(projection with { Snapshot = new AxisSnapshot(definition.Id, definition.Name, AxisState.Error, 12.5, 0) });
                Assert.False(vm.MoveAxisAbsoluteCommand.CanExecute(null));
                Assert.False(vm.HomeAxisCommand.CanExecute(null));
                Assert.False(vm.CanJogAxis);
                Assert.Equal(0, dispatchCount);
                Assert.Equal(12.5, projection.Snapshot!.Position);
            }
            finally { OpenVisionLanguageService.SetLanguage(previousLanguage, save: false); }
            return true;
        });
    }

    [Theory]
    [InlineData(300, OpenVisionLanguage.Korean)]
    [InlineData(420, OpenVisionLanguage.Korean)]
    [InlineData(300, OpenVisionLanguage.English)]
    [InlineData(420, OpenVisionLanguage.English)]
    public async Task AxisDriveIdentityFitsLongNamesAndSelectionChanges(double width, OpenVisionLanguage language)
    {
        await _ui.InvokeAsync(() =>
        {
            var previousLanguage = OpenVisionLanguageService.CurrentLanguage;
            OpenVisionLanguageService.SetLanguage(language, save: false);
            try
            {
                var project = new MachineProjectDocument { Name = "Inspector identity fixture" };
                var axis = new VirtualAxisDefinition
                {
                    Id = "axis-" + new string('i', 75), Name = new string(language == OpenVisionLanguage.Korean ? '축' : 'N', 80),
                    SoftLimitMin = -100, SoftLimitMax = 100, MaxVelocity = 25
                };
                var other = new VirtualAxisDefinition { Id = "axis-other", Name = "Other axis", SoftLimitMin = -50, SoftLimitMax = 50 };
                project.Axes.AddRange([axis, other]);
                using var vm = new MainViewModel(project);
                vm.IsDesignMode = true;
                var axes = vm.ProjectTree.Roots.Single().Children.Single(node => node.Kind == TreeNodeKind.Axes);
                vm.ProjectTree.SelectedNode = axes.Children.Single(node => node.Id == axis.Id);
                var original = System.Text.Json.JsonSerializer.Serialize(project);
                var view = new RightToolRegionView { DataContext = vm };
                using var source = new System.Windows.Interop.HwndSource(new System.Windows.Interop.HwndSourceParameters("InspectorIdentityFixture")
                {
                    Width = (int)width, Height = 900, WindowStyle = unchecked((int)0x80000000)
                });
                source.RootVisual = view;
                Arrange(view, width);
                var panel = Assert.IsType<Border>(view.FindName("AxisDriveTuningPanel"));
                var identity = Descendants(panel).OfType<TextBlock>().Where(text => text.Text == axis.Name || text.Text == axis.Id).ToArray();
                Assert.Equal(2, identity.Length);
                Capture(view, width, language, "drive-long-identity");
                var measurements = identity.Select(text =>
                {
                    var glyphs = new FormattedText(text.Text, CultureInfo.CurrentUICulture, text.FlowDirection,
                        new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch), text.FontSize,
                        text.Foreground, VisualTreeHelper.GetDpi(text).PixelsPerDip);
                    if (text.TextWrapping != TextWrapping.NoWrap) glyphs.MaxTextWidth = Math.Max(1, text.ActualWidth);
                    var bounds = text.TransformToAncestor(panel).TransformBounds(new Rect(text.RenderSize));
                    return new { text.Text, wrapping = text.TextWrapping.ToString(), bounds.X, bounds.Right, bounds.Bottom,
                        width = text.ActualWidth, height = text.ActualHeight, glyphWidth = glyphs.WidthIncludingTrailingWhitespace,
                        glyphHeight = glyphs.Height, panelWidth = panel.ActualWidth, panelHeight = panel.ActualHeight };
                }).ToArray();
                RecordInspectorObservation(width, language, "drive-long-identity", measurements);
                foreach (var measure in measurements)
                {
                    Assert.True(measure.X >= -0.5 && measure.Right <= measure.panelWidth + 0.5,
                        $"Identity parent bounds: {measure.X}..{measure.Right} / {measure.panelWidth}");
                    Assert.True(measure.glyphWidth <= measure.width + 0.5, $"Identity glyph width {measure.glyphWidth} > {measure.width}");
                    Assert.True(measure.glyphHeight <= measure.height + 0.5, $"Identity glyph height {measure.glyphHeight} > {measure.height}");
                    Assert.True(measure.Bottom <= measure.panelHeight + 0.5);
                }
                var oldEditor = Assert.IsType<AxisDriveTuningEditorViewModel>(vm.AxisDriveTuningEditor);
                vm.ProjectTree.SelectedNode = axes.Children.Single(node => node.Id == other.Id);
                Arrange(view, width);
                Assert.Equal(other.Id, vm.AxisDriveTuningEditor?.Id);
                Assert.DoesNotContain(Descendants(panel).OfType<TextBlock>(), text => text.IsVisible && (text.Text == axis.Name || text.Text == axis.Id));
                Capture(view, width, language, "drive-selection-other");
                oldEditor.MaxVelocity = 33;
                oldEditor.ResetDriveDefaultsCommand.Execute(null);
                Assert.False(oldEditor.ResetDriveDefaultsCommand.CanExecute(null));
                vm.ProjectTree.SelectedNode = null;
                Arrange(view, width);
                Assert.Null(vm.AxisDriveTuningEditor);
                Assert.False(panel.IsVisible);
                Assert.NotEmpty(vm.Properties.Items);
                Capture(view, width, language, "drive-selection-none");
                Assert.Equal(original, System.Text.Json.JsonSerializer.Serialize(project));
                Assert.False(vm.IsRunning || vm.HasUnsavedChanges);
            }
            finally { OpenVisionLanguageService.SetLanguage(previousLanguage, save: false); }
            return true;
        });
    }

    [Theory]
    [InlineData(300, OpenVisionLanguage.Korean)]
    [InlineData(420, OpenVisionLanguage.English)]
    public async Task AxisDrivePlaybackRejectsWritesThenRestoresEditing(double width, OpenVisionLanguage language)
    {
        await _ui.InvokeTaskAsync(async () =>
        {
            var previousLanguage = OpenVisionLanguageService.CurrentLanguage;
            OpenVisionLanguageService.SetLanguage(language, save: false);
            try
            {
                var store = new ProjectDocumentStore();
                var project = store.Load(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Samples", "AutomaticTransferCell.ovmachine")));
                var result = await new DeterministicRecipeDryRunRunner().RunAsync(project, project.Sequences[0].Id);
                var trace = result.Timeline[0];
                var step = new RecipeDryRunStepPresentation(result.SequenceId, trace.StepId, null, "#1", trace.Name,
                    "boundary", trace.HasIssue, trace.HasCheckpoint, trace.HasCheckpointMismatch, string.Empty, trace.BoundarySnapshot);
                using var vm = new MainViewModel(project);
                vm.IsDesignMode = true;
                var axes = vm.ProjectTree.Roots.Single().Children.Single(node => node.Kind == TreeNodeKind.Axes);
                vm.ProjectTree.SelectedNode = axes.Children[0];
                var editor = Assert.IsType<AxisDriveTuningEditorViewModel>(vm.AxisDriveTuningEditor);
                var original = store.Serialize(project);
                var view = new RightToolRegionView { DataContext = vm };
                using var source = new System.Windows.Interop.HwndSource(new System.Windows.Interop.HwndSourceParameters("InspectorPlaybackFixture")
                {
                    Width = (int)width, Height = 900, WindowStyle = unchecked((int)0x80000000)
                });
                source.RootVisual = view;
                Assert.IsType<Expander>(view.FindName("AxisAdvancedDriveSettings")).IsExpanded = true;
                await Task.Delay(250);
                Arrange(view, width);
                var velocity = Descendants(view).OfType<NumberBox>().Single(control =>
                    System.Windows.Automation.AutomationProperties.GetAutomationId(control) == "AxisMaxVelocityNumberBox");
                var reset = Assert.IsType<Button>(view.FindName("ResetAxisDriveDefaultsButton"));
                var initialVelocity = editor.MaxVelocity;
                vm.DryRunPlayback.Show(step, [step]);
                Arrange(view, width);
                Assert.True(vm.IsDryRunPlaybackActive);
                Assert.False(vm.IsSceneEditable);
                var inputDisabled = !velocity.IsEnabled;
                var resetDisabled = !reset.IsEnabled;
                var resetDenied = !editor.ResetDriveDefaultsCommand.CanExecute(null);
                Capture(view, width, language, "drive-playback-readonly");
                velocity.SetCurrentValue(NumberBox.ValueProperty, initialVelocity + 1);
                BindingOperations.GetBindingExpression(velocity, NumberBox.ValueProperty)!.UpdateSource();
                var bindingWriteRejected = store.Serialize(project) == original && vm.IsDryRunPlaybackActive && !vm.HasUnsavedChanges;
                vm.DryRunPlayback.Show(step, [step]);
                editor.ResetDriveDefaultsCommand.Execute(null);
                var resetWriteRejected = store.Serialize(project) == original && vm.IsDryRunPlaybackActive && !vm.HasUnsavedChanges;
                RecordInspectorObservation(width, language, "drive-playback-readonly", new
                {
                    inputDisabled, resetDisabled, resetDenied, bindingWriteRejected, resetWriteRejected,
                    vm.IsDryRunPlaybackActive, vm.HasUnsavedChanges, velocity = editor.MaxVelocity, initialVelocity
                });
                Capture(view, width, language, "drive-playback-after-attempt");
                Assert.True(inputDisabled && resetDisabled && resetDenied, "Playback must disable axis tuning input and reset command.");
                Assert.True(bindingWriteRejected && resetWriteRejected, "Read-only axis writes must preserve playback, full project payload and clean state.");
                editor.HomePosition += 1;
                editor.SoftLimitMin -= 1;
                editor.SoftLimitMax += 1;
                editor.MaxAcceleration += 1;
                editor.MaxDeceleration += 1;
                editor.FollowingErrorLimit += 1;
                Assert.Equal(original, store.Serialize(project));
                Assert.True(vm.IsDryRunPlaybackActive);
                Assert.False(vm.IsRunning || vm.HasUnsavedChanges);
                vm.DryRunPlayback.ExitCommand.Execute(null);
                Arrange(view, width);
                Assert.True(velocity.IsEnabled && reset.IsEnabled && editor.ResetDriveDefaultsCommand.CanExecute(null));
                velocity.SetCurrentValue(NumberBox.ValueProperty, initialVelocity + 2);
                BindingOperations.GetBindingExpression(velocity, NumberBox.ValueProperty)!.UpdateSource();
                Arrange(view, width);
                Assert.Equal(initialVelocity + 2, editor.MaxVelocity);
                editor.MaxVelocity = initialVelocity + 3;
                Arrange(view, width);
                Assert.Equal(initialVelocity + 3, velocity.Value);
                Assert.True(vm.HasUnsavedChanges);
                Assert.False(vm.IsRunning || vm.IsDryRunPlaybackActive);
                Capture(view, width, language, "drive-playback-edit-restored");
                vm.Layout.Select(vm.Layout.Items.First(item => item.Component?.Kind == LayoutComponentKind.MachineFrame).Id);
                Arrange(view, width);
                Assert.Null(vm.AxisDriveTuningEditor);
                var changed = store.Serialize(project);
                editor.MaxVelocity = initialVelocity + 4;
                editor.ResetDriveDefaultsCommand.Execute(null);
                Assert.False(editor.ResetDriveDefaultsCommand.CanExecute(null));
                Assert.Equal(changed, store.Serialize(project));
                vm.ProjectTree.SelectedNode = axes.Children[0];
                Arrange(view, width);
                Assert.NotSame(editor, vm.AxisDriveTuningEditor);
                Assert.Equal(initialVelocity + 3, vm.AxisDriveTuningEditor?.MaxVelocity);
                vm.ProjectTree.SelectedNode = null;
                Arrange(view, width);
                Assert.Null(vm.AxisDriveTuningEditor);
                Assert.False(Assert.IsType<Border>(view.FindName("AxisDriveTuningPanel")).IsVisible);
                Assert.False(vm.IsRunning);
            }
            finally { OpenVisionLanguageService.SetLanguage(previousLanguage, save: false); }
        });
    }

    [Theory]
    [InlineData(300, OpenVisionLanguage.Korean, true)]
    [InlineData(420, OpenVisionLanguage.English, true)]
    [InlineData(300, OpenVisionLanguage.Korean, false)]
    [InlineData(420, OpenVisionLanguage.English, false)]
    public async Task ManualEntryPreparationRestoresSceneAndInspectorAfterRejectionOrSelectionChange(double width, OpenVisionLanguage language, bool rejectDefinition)
    {
        await _ui.InvokeTaskAsync(async () =>
        {
            var previousLanguage = OpenVisionLanguageService.CurrentLanguage;
            OpenVisionLanguageService.SetLanguage(language, save: false);
            try
            {
                var project = new ProjectDocumentStore().Load(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Samples", "AutomaticTransferCell.ovmachine")));
                using var vm = new MainViewModel(project);
                vm.Navigation.IsEquipmentWorkspace = true;
                var stage = vm.Layout.Items.First(item => item.Component?.Kind == LayoutComponentKind.LinearStage);
                var frame = vm.Layout.Items.First(item => item.Component?.Kind == LayoutComponentKind.MachineFrame);
                vm.Layout.Select(stage.Id);
                await WaitAsync(() => vm.CanStartManualEquipmentControl);
                var editor = Assert.IsType<LayoutComponentEditorViewModel>(vm.Layout.SelectedComponentEditor);
                editor.RotationDegrees += 1;
                var originalStep = project.Simulation.FixedStepMilliseconds;
                if (rejectDefinition) project.Simulation.FixedStepMilliseconds = originalStep + 1;
                Assert.True(vm.CanStartManualEquipmentControl && vm.HasUnsavedChanges);
                var payload = System.Text.Json.JsonSerializer.Serialize(project);
                var scene = new OpenVisionLab.MachineStudio.View.Scene.SceneDocumentView();
                vm.OpenEquipmentSinglePartCheckCommand.Execute(null);
                var inspector = new RightToolRegionView();
                var root = new Grid { DataContext = vm };
                root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(600) });
                root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(width) });
                root.Children.Add(scene);
                Grid.SetColumn(inspector, 1);
                root.Children.Add(inspector);
                using var source = new System.Windows.Interop.HwndSource(new System.Windows.Interop.HwndSourceParameters("ManualAdmissionBindingFixture")
                {
                    Width = (int)(600 + width), Height = 900, WindowStyle = unchecked((int)0x80000000)
                });
                source.RootVisual = root;
                Arrange(root, 600 + width);
                var viewport = Assert.IsType<OpenVisionLab.MachineStudio.View.Scene.MachineSceneViewport>(scene.FindName("SceneViewport"));
                var name = Assert.IsType<TextBox>(inspector.FindName("ComponentNameTextBox"));
                var start = Assert.IsType<Button>(inspector.FindName("EnterManualCommissioningButton"));
                var hint = Assert.IsType<TextBlock>(inspector.FindName("ManualEntryHintText"));
                Assert.True(viewport.IsDesignMode && name.IsEnabled && start.IsEnabled);
                Assert.Same(editor, name.DataContext);
                var sceneHint = Descendants(scene).OfType<TextBlock>().Single(control => control.Text == OpenVisionLanguageService.T("Scene.EquipmentEditHint"));
                Assert.Equal(language, OpenVisionLanguageService.CurrentLanguage);
                var state = rejectDefinition ? "definition-rejected" : "selection-changed";
                Capture(root, 600 + width, language, "manual-admission-" + state + "-before");
                var lockCount = 0;
                var sceneLocked = false;
                var cardLocked = false;
                var entryLocked = false;
                var ownerWriteRejected = false;
                var busyHintVisible = false;
                var scenePreparationHintCorrect = false;
                var preparingHintText = string.Empty;
                var preparingHintFits = false;
                System.ComponentModel.PropertyChangedEventHandler onAdmission = (_, args) =>
                {
                    if (args.PropertyName != nameof(ManualEquipmentCommissioningViewModel.IsManualControlAdmissionLocked)
                        || !vm.ManualEquipment.IsManualControlAdmissionLocked) return;
                    lockCount++;
                    Arrange(root, 600 + width);
                    sceneLocked = !vm.IsSceneEditable && !vm.Layout.IsEditable && !viewport.IsDesignMode;
                    cardLocked = !name.IsEnabled; // The placement card is hidden while the manual tab is open.
                    entryLocked = !start.IsEnabled && !start.Command!.CanExecute(null);
                    busyHintVisible = hint.IsVisible && hint.Text == vm.ManualEquipment.ManualControlHintText
                        && hint.Text == OpenVisionLanguageService.T("Equipment.ManualHint.Busy");
                    preparingHintText = sceneHint.Text;
                    scenePreparationHintCorrect = !vm.IsRunning && preparingHintText == OpenVisionLanguageService.T("Equipment.ManualHint.Busy");
                    var glyph = new FormattedText(sceneHint.Text, CultureInfo.CurrentCulture, sceneHint.FlowDirection,
                        new Typeface(sceneHint.FontFamily, sceneHint.FontStyle, sceneHint.FontWeight, sceneHint.FontStretch),
                        sceneHint.FontSize, sceneHint.Foreground, VisualTreeHelper.GetDpi(sceneHint).PixelsPerDip) { MaxTextWidth = sceneHint.ActualWidth };
                    var hintBounds = sceneHint.TransformToAncestor(scene).TransformBounds(new Rect(sceneHint.RenderSize));
                    preparingHintFits = sceneHint.IsVisible && glyph.Width <= sceneHint.ActualWidth + 0.5
                        && glyph.Height <= sceneHint.ActualHeight + 0.5 && new Rect(scene.RenderSize).Contains(hintBounds);
                    editor.RotationDegrees += 3;
                    ownerWriteRejected = payload == System.Text.Json.JsonSerializer.Serialize(project);
                    Capture(root, 600 + width, language, "manual-admission-" + state + "-locked");
                    if (!rejectDefinition) vm.Layout.Select(frame.Id);
                };
                vm.ManualEquipment.PropertyChanged += onAdmission;
                try
                {
                    Invoke(start);
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    await WaitAsync(() => !vm.ManualEquipment.IsStartingManualControl);
                }
                finally { vm.ManualEquipment.PropertyChanged -= onAdmission; }
                Arrange(root, 600 + width);
                var restoredScene = vm.IsSceneEditable && vm.Layout.IsEditable && viewport.IsDesignMode;
                var restoredCard = name.IsEnabled;
                var unchanged = payload == System.Text.Json.JsonSerializer.Serialize(project);
                var selectedId = vm.Layout.SelectedItem?.Id;
                Capture(root, 600 + width, language, "manual-admission-" + state + "-recovered");
                RecordInspectorObservation(width, language, "manual-admission-" + state, new
                {
                    rejectDefinition, lockCount, sceneLocked, cardLocked, entryLocked, ownerWriteRejected, busyHintVisible,
                    scenePreparationHintCorrect, preparingHintFits, preparingHintText, actualLanguage = OpenVisionLanguageService.CurrentLanguage.ToString(),
                    restoredScene, restoredCard, unchanged, selectedId, vm.IsRunning, vm.IsDesignMode,
                    vm.ManualEquipment.IsManualControlAdmissionLocked, vm.HasUnsavedChanges, hint.Text
                });
                Assert.Equal(1, lockCount);
                Assert.True(scenePreparationHintCorrect, "Preparing without running displayed: " + preparingHintText);
                Assert.True(preparingHintFits, "The rendered preparation caption must fit its scene container.");
                Assert.Equal(OpenVisionLanguageService.T("Scene.EquipmentEditHint"), sceneHint.Text);
                Assert.True(sceneLocked && cardLocked && entryLocked && ownerWriteRejected && busyHintVisible);
                Assert.True(restoredScene && restoredCard && unchanged && vm.HasUnsavedChanges);
                Assert.False(vm.IsRunning || vm.ManualEquipment.IsManualControlAdmissionLocked);
                Assert.Equal(rejectDefinition ? stage.Id : frame.Id, selectedId);
                if (rejectDefinition) Assert.Equal(OpenVisionLanguageService.T("Equipment.ManualHint.NotPrepared"), hint.Text);

                project.Simulation.FixedStepMilliseconds = originalStep;
                vm.Layout.Select(stage.Id);
                await WaitAsync(() => vm.CanStartManualEquipmentControl);
                Arrange(root, 600 + width);
                Invoke(start);
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                await WaitAsync(() => !vm.ManualEquipment.IsStartingManualControl && vm.IsRunning && vm.IsRunMode);
                Arrange(root, 600 + width);
                Assert.False(viewport.IsDesignMode || vm.ManualEquipment.IsManualControlAdmissionLocked);
                Assert.Equal(OpenVisionLanguageService.T("Scene.EquipmentRunHint"), sceneHint.Text);
                Assert.Equal(SimulationControlOwner.Manual, vm.SceneSnapshots.Latest!.ControlOwner);
                Assert.False(vm.SceneSnapshots.Latest.AutomaticRun.IsActive);
                Assert.DoesNotContain(vm.SceneSnapshots.Latest.Sequences, sequence => sequence.Status == OpenVisionLab.Machine.Sequence.Runtime.SequenceExecutionStatus.Running);
                vm.IsDesignMode = true;
                await WaitAsync(() => vm.IsDesignMode && vm.IsSceneEditable && !vm.IsRunning);
                Arrange(root, 600 + width);
                Assert.True(viewport.IsDesignMode && name.IsEnabled);
                Assert.Equal(stage.Id, vm.Layout.SelectedItem?.Id);
                Assert.Equal(editor.RotationDegrees, stage.Component!.Transform.RotationDegrees);
                Assert.True(vm.HasUnsavedChanges);
                Assert.Equal(0, vm.SceneSnapshots.Latest!.SimulationTime.TotalSeconds);
                RecordInspectorObservation(width, language, "manual-admission-" + state + "-retry", new
                {
                    selectedId = vm.Layout.SelectedItem?.Id, vm.IsRunning, vm.IsDesignMode, vm.IsSceneEditable,
                    sceneEditable = viewport.IsDesignMode, inspectorEnabled = name.IsEnabled, sceneHint = sceneHint.Text,
                    vm.HasUnsavedChanges, simulationTime = vm.SceneSnapshots.Latest.SimulationTime.TotalSeconds
                });
                source.RootVisual = null;
                root.DataContext = null;
            }
            finally { OpenVisionLanguageService.SetLanguage(previousLanguage, save: false); }
            async Task WaitAsync(Func<bool> condition)
            {
                var deadline = DateTime.UtcNow.AddSeconds(10);
                while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(10);
                Assert.True(condition(), "The manual preparation boundary did not recover.");
            }
            static void Invoke(Button button) => Assert.IsAssignableFrom<System.Windows.Automation.Provider.IInvokeProvider>(
                new System.Windows.Automation.Peers.ButtonAutomationPeer(button).GetPattern(System.Windows.Automation.Peers.PatternInterface.Invoke)).Invoke();
        });
    }

    [Theory]
    [InlineData(300, OpenVisionLanguage.Korean)]
    [InlineData(420, OpenVisionLanguage.Korean)]
    [InlineData(300, OpenVisionLanguage.English)]
    [InlineData(420, OpenVisionLanguage.English)]
    public async Task ManualDeviceStateCaptionsAndSelectedTargetRecoverThroughRenderedCommands(double width, OpenVisionLanguage language)
    {
        await _ui.InvokeTaskAsync(async () =>
        {
            var previousLanguage = OpenVisionLanguageService.CurrentLanguage;
            OpenVisionLanguageService.SetLanguage(language, save: false);
            try
            {
                var project = new ProjectDocumentStore().Load(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Samples", "AutomaticTransferCell.ovmachine")));
                foreach (var component in project.Layouts.SelectMany(layout => layout.Components).Where(component =>
                    component.Kind is LayoutComponentKind.DigitalSensor or LayoutComponentKind.PneumaticCylinder or LayoutComponentKind.Conveyor))
                    component.Name = component.Kind.ToString() + "-" + new string('X', 79 - component.Kind.ToString().Length);
                using var vm = new MainViewModel(project);
                vm.Navigation.HideStartupChoice();
                vm.Navigation.IsEquipmentWorkspace = true;
                var sensor = vm.Layout.Items.First(item => item.Component?.Kind == LayoutComponentKind.DigitalSensor);
                var cylinder = vm.Layout.Items.First(item => item.Component?.Kind == LayoutComponentKind.PneumaticCylinder);
                var conveyor = vm.Layout.Items.First(item => item.Component?.Kind == LayoutComponentKind.Conveyor);
                var payload = System.Text.Json.JsonSerializer.Serialize(project);
                vm.Layout.Select(sensor.Id);
                await WaitAsync(() => vm.CanStartManualEquipmentControl);
                var view = new RightToolRegionView { DataContext = vm };
                using var source = new System.Windows.Interop.HwndSource(new System.Windows.Interop.HwndSourceParameters("ManualDeviceStateBindingFixture")
                {
                    Width = (int)width, Height = 900, WindowStyle = unchecked((int)0x80000000)
                });
                source.RootVisual = view;
                Arrange(view, width);
                Invoke(Assert.IsType<Button>(view.FindName("EnterManualCommissioningButton")));
                await WaitAsync(() => vm.IsRunning && vm.SceneSnapshots.Latest?.ControlOwner == OpenVisionLab.Machine.Simulation.Engine.SimulationControlOwner.Manual);
                await WaitAsync(() => !vm.ManualEquipment.IsStartingManualControl && vm.PauseCommand.CanExecute(null));
                vm.PauseCommand.Execute(null);
                await WaitAsync(() => !vm.IsRunning && vm.StepCommand.CanExecute(null));
                Arrange(view, width);
                var problems = new List<string>();
                CheckSettledHint("after-pause");
                var sensorPanel = Assert.IsType<StackPanel>(view.FindName("SensorCommissioningPanel"));
                var cylinderPanel = Assert.IsType<StackPanel>(view.FindName("CylinderCommissioningPanel"));
                var conveyorPanel = Assert.IsType<StackPanel>(view.FindName("ConveyorCommissioningPanel"));
                var sensorState = Assert.IsType<TextBlock>(view.FindName("SensorForceStatusText"));
                var interlockState = Assert.IsType<TextBlock>(view.FindName("CylinderInterlockStatusText"));
                var on = Assert.IsType<Button>(view.FindName("ForceSensorOnButton"));
                var off = Assert.IsType<Button>(view.FindName("ForceSensorOffButton"));
                var clear = Assert.IsType<Button>(view.FindName("ClearSensorForceButton"));
                var extend = Assert.IsType<Button>(view.FindName("ExtendCylinderButton"));
                var retract = Assert.IsType<Button>(view.FindName("RetractCylinderButton"));
                var forward = Assert.IsType<Button>(view.FindName("RunConveyorForwardButton"));
                var reverse = Assert.IsType<Button>(view.FindName("RunConveyorReverseButton"));
                var stop = Assert.IsType<Button>(view.FindName("StopConveyorButton"));
                Assert.True(sensorPanel.IsVisible && !cylinderPanel.IsVisible && !conveyorPanel.IsVisible);
                Assert.Equal(OpenVisionLanguageService.T("Sensor.ForceReleased"), sensorState.Text);
                CheckPanel(sensorPanel, "sensor-released");
                Invoke(on);
                await WaitAsync(() => vm.CurrentSelectedSensorSignal?.OverrideValue == true);
                Arrange(view, width);
                Assert.Equal(OpenVisionLanguageService.T("Sensor.ForcedOn"), sensorState.Text);
                Assert.False(on.IsEnabled);
                Assert.True(off.IsEnabled && clear.IsEnabled);
                CheckPanel(sensorPanel, "sensor-on");
                Invoke(off);
                await WaitAsync(() => vm.CurrentSelectedSensorSignal?.OverrideValue == false);
                Arrange(view, width);
                Assert.Equal(OpenVisionLanguageService.T("Sensor.ForcedOff"), sensorState.Text);
                Assert.False(off.IsEnabled);
                Invoke(clear);
                await WaitAsync(() => vm.CurrentSelectedSensorSignal?.OverrideValue is null);
                Arrange(view, width);
                Assert.Equal(vm.CurrentSelectedSensorSignal!.NominalValue, vm.CurrentSelectedSensorSignal.Value);
                Assert.Equal(OpenVisionLanguageService.T("Sensor.ForceReleased"), sensorState.Text);
                Assert.False(clear.IsEnabled);
                Assert.True(on.IsEnabled && off.IsEnabled);

                vm.Layout.Select(cylinder.Id);
                Arrange(view, width);
                Assert.True(!sensorPanel.IsVisible && cylinderPanel.IsVisible && !conveyorPanel.IsVisible);
                Assert.Equal(OpenVisionLanguageService.T("Cylinder.InterlockReady"), interlockState.Text);
                CheckPanel(cylinderPanel, "cylinder-ready");
                var manager = vm.FaultManager;
                manager.SelectedKind = manager.AvailableKinds.Single(option => option.Kind == OpenVisionLab.Machine.Simulation.Faults.SimulationFaultKind.CylinderTravelBlocked);
                manager.SelectedTarget = manager.Targets.Single(target => target.Id == cylinder.Id);
                manager.InjectCommand.Execute(null);
                await WaitAsync(() => !manager.IsOperationPending && vm.IsCurrentCylinderInterlocked);
                Arrange(view, width);
                Assert.Equal(OpenVisionLanguageService.T("Cylinder.InterlockBlocked"), interlockState.Text);
                Assert.False(extend.IsEnabled || retract.IsEnabled);
                Assert.False(extend.Command!.CanExecute(null) || retract.Command!.CanExecute(null));
                CheckPanel(cylinderPanel, "cylinder-blocked");
                manager.SelectedActiveFault = manager.ActiveFaults.Single(fault => fault.TargetId == cylinder.Id);
                manager.ClearSelectedCommand.Execute(null);
                await WaitAsync(() => !manager.IsOperationPending && !vm.IsCurrentCylinderInterlocked);
                Arrange(view, width);
                Assert.Equal(OpenVisionLanguageService.T("Cylinder.InterlockReady"), interlockState.Text);
                Assert.True(extend.IsEnabled || retract.IsEnabled);

                vm.Layout.Select(conveyor.Id);
                Arrange(view, width);
                Assert.True(!sensorPanel.IsVisible && !cylinderPanel.IsVisible && conveyorPanel.IsVisible);
                CheckPanel(conveyorPanel, "conveyor-stopped");
                await InvokeConveyorAsync(forward, "Conveyor.ActionRunForward");
                await WaitAsync(() => SelectedSnapshot().ConveyorRunning == true && SelectedSnapshot().ConveyorDirection == OpenVisionLab.Machine.Simulation.Layout.ConveyorDirection.Forward);
                Arrange(view, width);
                Assert.False(forward.IsEnabled);
                Assert.True(reverse.IsEnabled && stop.IsEnabled);
                CheckPanel(conveyorPanel, "conveyor-forward");
                await InvokeConveyorAsync(reverse, "Conveyor.ActionRunReverse");
                await WaitAsync(() => SelectedSnapshot().ConveyorRunning == true && SelectedSnapshot().ConveyorDirection == OpenVisionLab.Machine.Simulation.Layout.ConveyorDirection.Reverse);
                Arrange(view, width);
                Assert.False(reverse.IsEnabled);
                Assert.True(forward.IsEnabled && stop.IsEnabled);
                await InvokeConveyorAsync(stop, "Conveyor.ActionStop");
                await WaitAsync(() => SelectedSnapshot().ConveyorRunning == false);
                Arrange(view, width);
                Assert.False(stop.IsEnabled);
                Assert.True(forward.IsEnabled && reverse.IsEnabled);

                vm.Layout.Select(sensor.Id);
                Arrange(view, width);
                Assert.True(sensorPanel.IsVisible && !cylinderPanel.IsVisible && !conveyorPanel.IsVisible);
                Assert.Equal(OpenVisionLanguageService.T("Sensor.ForceReleased"), sensorState.Text);
                Assert.Equal(sensor.Id, vm.Layout.SelectedItem!.Id);
                Assert.Equal(payload, System.Text.Json.JsonSerializer.Serialize(project));
                Assert.False(vm.HasUnsavedChanges || vm.IsRunning);
                Assert.Equal(language, OpenVisionLanguageService.CurrentLanguage);
                RecordInspectorObservation(width, language, "manual-device-state", new
                {
                    sourceDll = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(typeof(MainViewModel).Assembly.Location))),
                    language = language.ToString(), width, problems, selectedId = vm.Layout.SelectedItem.Id,
                    owner = vm.SceneSnapshots.Latest!.ControlOwner.ToString(), authoredBytesPreserved = true,
                    sensorRestored = sensorState.Text, cylinderInterlockCleared = !vm.IsCurrentCylinderInterlocked,
                    native = false, actualKeysOrPointer = false
                });
                vm.IsDesignMode = true;
                await WaitAsync(() => vm.IsDesignMode && vm.IsSceneEditable);
                Assert.Equal(sensor.Id, vm.Layout.SelectedItem!.Id);
                Assert.Equal(payload, System.Text.Json.JsonSerializer.Serialize(project));
                Assert.True(problems.Count == 0, string.Join("; ", problems));

                async Task InvokeConveyorAsync(Button button, string key)
                {
                    var action = OpenVisionLanguageService.T(key);
                    var count = vm.LogMessages.Count(line => line.Contains(action, StringComparison.CurrentCulture));
                    Invoke(button);
                    await WaitAsync(() => vm.LogMessages.Count(line => line.Contains(action, StringComparison.CurrentCulture)) > count);
                    await ApplyTickAsync();
                }
                async Task ApplyTickAsync()
                {
                    await WaitAsync(() => vm.StepCommand.CanExecute(null));
                    var tick = vm.SceneSnapshots.Latest!.TickIndex;
                    vm.StepCommand.Execute(null);
                    await WaitAsync(() => vm.SceneSnapshots.Latest!.TickIndex > tick);
                    Assert.Equal(tick + 1, vm.SceneSnapshots.Latest!.TickIndex);
                    await WaitAsync(() => vm.StepCommand.CanExecute(null));
                    CheckSettledHint("after-step");
                }
                OpenVisionLab.Machine.Simulation.Layout.LayoutComponentSnapshot SelectedSnapshot() => vm.SceneSnapshots.Latest!.LayoutComponents.Single(item => item.Id == conveyor.Id);
                void CheckSettledHint(string state)
                {
                    Assert.False(vm.IsRunning || vm.ManualEquipment.IsStartingManualControl || vm.ManualEquipment.IsManualControlAdmissionLocked);
                    Assert.True(vm.StepCommand.CanExecute(null));
                    if (vm.ManualEquipment.ManualControlHintText != OpenVisionLanguageService.T("Equipment.ManualHint.Paused")
                        || !vm.CanStartManualEquipmentControl)
                        problems.Add(state + ": settled manual pause retained " + vm.ManualEquipment.ManualControlHintText);
                }
                void CheckPanel(StackPanel panel, string state)
                {
                    Arrange(view, width);
                    var scroll = Assert.IsType<ScrollViewer>(view.FindName("RunInspectorScrollViewer"));
                    panel.BringIntoView();
                    Arrange(view, width);
                    foreach (var text in Descendants(panel).OfType<TextBlock>().Where(text => text.IsVisible && !string.IsNullOrEmpty(text.Text)))
                    {
                        var glyph = new FormattedText(text.Text, CultureInfo.CurrentCulture, text.FlowDirection,
                            new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch), text.FontSize, text.Foreground, 1);
                        if (text.TextWrapping == TextWrapping.Wrap) glyph.MaxTextWidth = Math.Max(1, text.ActualWidth);
                        if (glyph.Width > text.ActualWidth + 1 || glyph.Height > text.ActualHeight + 1)
                            problems.Add(state + ": glyph " + text.Text + " " + glyph.Width + ">" + text.ActualWidth);
                        if (VisualTreeHelper.GetParent(text) is Grid grid && grid.ColumnDefinitions.Count > 0)
                        {
                            var cellWidth = grid.ColumnDefinitions[Grid.GetColumn(text)].ActualWidth - text.Margin.Left - text.Margin.Right;
                            if (text.ActualWidth > cellWidth + 1) problems.Add(state + ": cell " + text.Text + " " + text.ActualWidth + ">" + cellWidth);
                        }
                        if (VisualTreeHelper.GetParent(text) is FrameworkElement parent)
                        {
                            var bounds = text.TransformToAncestor(parent).TransformBounds(new Rect(0, 0, text.ActualWidth, text.ActualHeight));
                            if (bounds.Left < -1 || bounds.Right > parent.ActualWidth + 1)
                                problems.Add(state + ": parent " + text.Text + " " + bounds.Right + ">" + parent.ActualWidth);
                        }
                    }
                    foreach (var button in Descendants(panel).OfType<Button>().Where(button => button.IsVisible))
                        Assert.Equal(System.Windows.Automation.AutomationProperties.GetName(button), button.Content);
                    Capture(view, width, language, "manual-device-" + state);
                    RecordInspectorObservation(width, language, "manual-device-" + state, new { problems = problems.ToArray(), verticalOffset = scroll.VerticalOffset, state });
                }
            }
            finally { OpenVisionLanguageService.SetLanguage(previousLanguage, save: false); }
            async Task WaitAsync(Func<bool> condition)
            {
                var deadline = DateTime.UtcNow.AddSeconds(10);
                while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(10);
                Assert.True(condition(), "The selected manual device state did not recover.");
            }
            static void Invoke(Button button) => Assert.IsAssignableFrom<System.Windows.Automation.Provider.IInvokeProvider>(
                new System.Windows.Automation.Peers.ButtonAutomationPeer(button).GetPattern(System.Windows.Automation.Peers.PatternInterface.Invoke)).Invoke();
        });
    }

    [Theory]
    [InlineData(300, OpenVisionLanguage.Korean)]
    [InlineData(420, OpenVisionLanguage.Korean)]
    [InlineData(300, OpenVisionLanguage.English)]
    [InlineData(420, OpenVisionLanguage.English)]
    public async Task SensorFaultClearingRestoresRenderedStateAndSelectedCommands(double width, OpenVisionLanguage language)
    {
        await _ui.InvokeTaskAsync(async () =>
        {
            var previousLanguage = OpenVisionLanguageService.CurrentLanguage;
            OpenVisionLanguageService.SetLanguage(language, save: false);
            try
            {
                var project = new ProjectDocumentStore().Load(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Samples", "AutomaticTransferCell.ovmachine")));
                using var vm = new MainViewModel(project);
                vm.Navigation.HideStartupChoice();
                vm.Navigation.IsEquipmentWorkspace = true;
                var sensor = vm.Layout.Items.First(item => item.Component?.Kind == LayoutComponentKind.DigitalSensor);
                var cylinder = vm.Layout.Items.First(item => item.Component?.Kind == LayoutComponentKind.PneumaticCylinder);
                vm.Layout.Select(sensor.Id);
                var payload = System.Text.Json.JsonSerializer.Serialize(project);
                var view = new RightToolRegionView { DataContext = vm };
                using var source = new System.Windows.Interop.HwndSource(new System.Windows.Interop.HwndSourceParameters("SensorFaultRecoveryBindingFixture")
                {
                    Width = (int)width, Height = 900, WindowStyle = unchecked((int)0x80000000)
                });
                source.RootVisual = view;
                await WaitAsync(() => vm.CanStartManualEquipmentControl);
                Arrange(view, width);
                Invoke(Assert.IsType<Button>(view.FindName("EnterManualCommissioningButton")));
                await WaitAsync(() => vm.IsRunning && !vm.ManualEquipment.IsStartingManualControl && vm.PauseCommand.CanExecute(null));
                vm.PauseCommand.Execute(null);
                await WaitAsync(() => !vm.IsRunning && vm.StepCommand.CanExecute(null));
                var manager = vm.FaultManager;
                var channelId = vm.SceneSnapshots.Latest!.LayoutComponents.Single(item => item.Id == sensor.Id).SensorOutputChannelId;
                Assert.NotNull(channelId);
                var nominal = vm.CurrentSelectedSensorSignal!.NominalValue;
                var kind = Assert.IsType<ComboBox>(view.FindName("FaultKindComboBox"));
                var target = Assert.IsType<ComboBox>(view.FindName("FaultTargetComboBox"));
                var value = Assert.IsType<ComboBox>(view.FindName("FaultForcedValueComboBox"));
                kind.SelectedItem = manager.AvailableKinds.Single(option => option.Kind == OpenVisionLab.Machine.Simulation.Faults.SimulationFaultKind.StuckDigitalInput);
                target.SelectedItem = manager.Targets.Single(option => option.Id == channelId);
                value.SelectedItem = manager.ForcedValueOptions.Single(option => option.Value == !nominal);
                Arrange(view, width);
                Invoke(Assert.IsType<Button>(view.FindName("InjectFaultButton")));
                await WaitAsync(() => !manager.IsOperationPending && vm.IsCurrentSensorFaulted && vm.SelectedEquipmentStatus!.IsFaulted);
                var on = Assert.IsType<Button>(view.FindName("ForceSensorOnButton"));
                var off = Assert.IsType<Button>(view.FindName("ForceSensorOffButton"));
                var clearForce = Assert.IsType<Button>(view.FindName("ClearSensorForceButton"));
                var state = Assert.IsType<TextBlock>(view.FindName("SensorForceStatusText"));
                var panel = Assert.IsType<StackPanel>(view.FindName("SensorCommissioningPanel"));
                var renderProblems = new List<string>();
                Arrange(view, width);
                Assert.Equal(!nominal, vm.CurrentSelectedSensorSignal!.Value);
                Assert.Equal(OpenVisionLanguageService.T("Sensor.FaultOverride"), state.Text);
                Assert.False(on.IsEnabled || off.IsEnabled || clearForce.IsEnabled);
                Assert.False(on.Command!.CanExecute(null) || off.Command!.CanExecute(null) || clearForce.Command!.CanExecute(null));
                panel.BringIntoView();
                Arrange(view, width);
                CheckFaultPanel();
                Capture(view, width, language, "sensor-fault-active");
                vm.Layout.Select(cylinder.Id);
                Arrange(view, width);
                Assert.False(panel.IsVisible);
                vm.Layout.Select(sensor.Id);
                Arrange(view, width);
                Assert.True(panel.IsVisible && vm.IsCurrentSensorFaulted);
                var active = Assert.IsType<ListBox>(view.FindName("ActiveFaultListBox"));
                active.SelectedItem = manager.ActiveFaults.Single(fault => fault.TargetId == channelId);
                Arrange(view, width);
                Invoke(Assert.IsType<Button>(view.FindName("ClearSelectedFaultButton")));
                await WaitAsync(() => !manager.IsOperationPending && !vm.IsCurrentSensorFaulted && !vm.SelectedEquipmentStatus!.IsFaulted);
                Arrange(view, width);
                Assert.Equal(nominal, vm.CurrentSelectedSensorSignal!.Value);
                Assert.Null(vm.CurrentSelectedSensorSignal.OverrideValue);
                Assert.Equal(OpenVisionLanguageService.T("Sensor.ForceReleased"), state.Text);
                Assert.True(on.IsEnabled && off.IsEnabled && !clearForce.IsEnabled);
                Assert.True(on.Command!.CanExecute(null) && off.Command!.CanExecute(null));
                Assert.Empty(manager.ActiveFaults);
                panel.BringIntoView();
                Arrange(view, width);
                CheckFaultPanel();
                Capture(view, width, language, "sensor-fault-cleared");
                Invoke(nominal ? off : on);
                await WaitAsync(() => vm.CurrentSelectedSensorSignal?.OverrideValue == !nominal);
                Arrange(view, width);
                Invoke(clearForce);
                await WaitAsync(() => vm.CurrentSelectedSensorSignal?.OverrideValue is null);
                Assert.Equal(nominal, vm.CurrentSelectedSensorSignal!.Value);
                Assert.Equal(sensor.Id, vm.Layout.SelectedItem!.Id);
                Assert.Equal(payload, System.Text.Json.JsonSerializer.Serialize(project));
                Assert.False(vm.HasUnsavedChanges || vm.IsRunning);
                RecordInspectorObservation(width, language, "sensor-fault-recovery", new
                {
                    sourceDll = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(typeof(MainViewModel).Assembly.Location))),
                    channelId, nominal, actual = vm.CurrentSelectedSensorSignal.Value, activeFaults = manager.ActiveFaults.Count,
                    selected = vm.Layout.SelectedItem.Id, on = on.IsEnabled, off = off.IsEnabled, clear = clearForce.IsEnabled, renderProblems
                });
                vm.IsDesignMode = true;
                await WaitAsync(() => vm.IsDesignMode && vm.IsSceneEditable);
                Assert.Equal(sensor.Id, vm.Layout.SelectedItem!.Id);
                Assert.Equal(0, vm.SceneSnapshots.Latest!.SimulationTime.TotalSeconds);
                Assert.True(renderProblems.Count == 0, string.Join("; ", renderProblems));
                void CheckFaultPanel()
                {
                    foreach (var text in Descendants(panel).OfType<TextBlock>().Where(text => text.IsVisible && !string.IsNullOrEmpty(text.Text)))
                    {
                        var glyph = new FormattedText(text.Text, CultureInfo.CurrentCulture, text.FlowDirection,
                            new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch), text.FontSize, text.Foreground, 1);
                        if (text.TextWrapping == TextWrapping.Wrap) glyph.MaxTextWidth = Math.Max(1, text.ActualWidth);
                        if (glyph.Width > text.ActualWidth + 1 || glyph.Height > text.ActualHeight + 1)
                            renderProblems.Add("fault panel glyph: " + text.Text);
                        if (VisualTreeHelper.GetParent(text) is Grid grid && grid.ColumnDefinitions.Count > 0)
                        {
                            var cell = grid.ColumnDefinitions[Grid.GetColumn(text)].ActualWidth - text.Margin.Left - text.Margin.Right;
                            if (text.ActualWidth > cell + 1) renderProblems.Add("fault panel cell: " + text.Text);
                        }
                    }
                }
                Assert.Equal(payload, System.Text.Json.JsonSerializer.Serialize(project));
            }
            finally { OpenVisionLanguageService.SetLanguage(previousLanguage, save: false); }
            async Task WaitAsync(Func<bool> condition)
            {
                var deadline = DateTime.UtcNow.AddSeconds(10);
                while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(10);
                Assert.True(condition(), "The fault recovery state did not settle.");
            }
            static void Invoke(Button button) => Assert.IsAssignableFrom<System.Windows.Automation.Provider.IInvokeProvider>(
                new System.Windows.Automation.Peers.ButtonAutomationPeer(button).GetPattern(System.Windows.Automation.Peers.PatternInterface.Invoke)).Invoke();
        });
    }

    [Theory]
    [InlineData(300, OpenVisionLanguage.Korean)]
    [InlineData(420, OpenVisionLanguage.Korean)]
    [InlineData(300, OpenVisionLanguage.English)]
    [InlineData(420, OpenVisionLanguage.English)]
    public async Task LongDeviceIdentitiesStayReadableInRuntimeAndDriveSelection(double width, OpenVisionLanguage language)
    {
        await _ui.InvokeTaskAsync(async () =>
        {
            var previousLanguage = OpenVisionLanguageService.CurrentLanguage;
            OpenVisionLanguageService.SetLanguage(language, save: false);
            try
            {
                var cylinderId = "device.cylinder-" + new string('X', 64);
                var conveyorId = "device.conveyor-" + new string('Y', 64);
                var fixture = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Samples", "AutomaticTransferCell.ovmachine"))
                    .Replace("\"device.cylinder-1\"", "\"" + cylinderId + "\"", StringComparison.Ordinal)
                    .Replace("\"device.conveyor-1\"", "\"" + conveyorId + "\"", StringComparison.Ordinal);
                var project = new ProjectDocumentStore().Load(fixture);
                using var vm = new MainViewModel(project);
                vm.Navigation.HideStartupChoice();
                vm.Navigation.IsEquipmentWorkspace = true;
                var items = new[]
                {
                    vm.Layout.Items.First(item => item.Component?.Kind == LayoutComponentKind.PneumaticCylinder),
                    vm.Layout.Items.First(item => item.Component?.Kind == LayoutComponentKind.Conveyor)
                };
                var payload = System.Text.Json.JsonSerializer.Serialize(project);
                vm.Layout.Select(items[0].Id);
                var view = new RightToolRegionView { DataContext = vm };
                using var source = new System.Windows.Interop.HwndSource(new System.Windows.Interop.HwndSourceParameters("LongDeviceIdentityBindingFixture")
                {
                    Width = (int)width, Height = 900, WindowStyle = unchecked((int)0x80000000)
                });
                source.RootVisual = view;
                await WaitAsync(() => vm.CanStartManualEquipmentControl);
                Arrange(view, width);
                Invoke(Assert.IsType<Button>(view.FindName("EnterManualCommissioningButton")));
                await WaitAsync(() => vm.IsRunning && !vm.ManualEquipment.IsStartingManualControl && vm.PauseCommand.CanExecute(null));
                vm.PauseCommand.Execute(null);
                await WaitAsync(() => !vm.IsRunning && vm.StepCommand.CanExecute(null));
                var problems = new List<string>();
                foreach (var item in items)
                {
                    vm.Layout.Select(item.Id);
                    Arrange(view, width);
                    var card = Assert.IsType<Border>(view.FindName("SelectedEquipmentRuntimeCard"));
                    card.BringIntoView();
                    Arrange(view, width);
                    Assert.True(card.IsVisible);
                    foreach (var text in Descendants(card).OfType<TextBlock>().Where(text => text.IsVisible &&
                        text.GetBindingExpression(TextBlock.TextProperty)?.ParentBinding.Path?.Path.StartsWith("SelectedEquipmentStatus.", StringComparison.Ordinal) == true))
                        CheckText(text, "runtime-" + item.Kind);
                    Capture(view, width, language, "identity-runtime-" + item.Kind);
                }
                vm.IsDesignMode = true;
                await WaitAsync(() => vm.IsDesignMode && vm.IsSceneEditable);
                Invoke(Descendants(view).OfType<Button>().Single(button => button.IsVisible && ReferenceEquals(button.Command, vm.OpenEquipmentDriveTabCommand)));
                foreach (var item in items)
                {
                    vm.Layout.Select(item.Id);
                    Arrange(view, width);
                    var combo = Assert.IsType<ComboBox>(view.FindName("BehaviorBindingComboBox"));
                    combo.BringIntoView();
                    Arrange(view, width);
                    Assert.True(combo.IsVisible);
                    Assert.Equal(item.BehaviorBindingId, combo.SelectedValue);
                    var selectedText = Descendants(combo).OfType<TextBlock>().Single(text => text.IsVisible && text.Text.Contains(item.BehaviorBindingId!, StringComparison.Ordinal));
                    CheckText(selectedText, "drive-" + item.Kind);
                    Capture(view, width, language, "identity-drive-" + item.Kind);
                }
                Assert.Equal(payload, System.Text.Json.JsonSerializer.Serialize(project));
                Assert.False(vm.HasUnsavedChanges || vm.IsRunning);
                RecordInspectorObservation(width, language, "device-identity", new
                {
                    sourceDll = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(typeof(MainViewModel).Assembly.Location))),
                    cylinderId, conveyorId, problems = problems.ToArray(), selected = vm.Layout.SelectedItem!.Id
                });
                Assert.True(problems.Count == 0, string.Join("; ", problems));
                void CheckText(TextBlock text, string state)
                {
                    var glyph = new FormattedText(text.Text, CultureInfo.CurrentCulture, text.FlowDirection,
                        new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch), text.FontSize, text.Foreground, 1);
                    if (text.TextWrapping == TextWrapping.Wrap) glyph.MaxTextWidth = Math.Max(1, text.ActualWidth);
                    if (glyph.Width > text.ActualWidth + 1 || glyph.Height > text.ActualHeight + 1)
                        problems.Add(state + ": glyph " + text.Text + " " + glyph.Width + ">" + text.ActualWidth);
                    if (VisualTreeHelper.GetParent(text) is Grid grid && grid.ColumnDefinitions.Count > 0)
                    {
                        var cell = grid.ColumnDefinitions[Grid.GetColumn(text)].ActualWidth - text.Margin.Left - text.Margin.Right;
                        if (text.ActualWidth > cell + 1) problems.Add(state + ": cell " + text.Text + " " + text.ActualWidth + ">" + cell);
                    }
                    if (VisualTreeHelper.GetParent(text) is FrameworkElement parent)
                    {
                        var bounds = text.TransformToAncestor(parent).TransformBounds(new Rect(0, 0, text.ActualWidth, text.ActualHeight));
                        if (bounds.Left < -1 || bounds.Right > parent.ActualWidth + 1)
                            problems.Add(state + ": parent " + text.Text + " " + bounds.Right + ">" + parent.ActualWidth);
                    }
                }
            }
            finally { OpenVisionLanguageService.SetLanguage(previousLanguage, save: false); }
            async Task WaitAsync(Func<bool> condition)
            {
                var deadline = DateTime.UtcNow.AddSeconds(10);
                while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(10);
                Assert.True(condition(), "The long identity fixture did not settle.");
            }
            static void Invoke(Button button) => Assert.IsAssignableFrom<System.Windows.Automation.Provider.IInvokeProvider>(
                new System.Windows.Automation.Peers.ButtonAutomationPeer(button).GetPattern(System.Windows.Automation.Peers.PatternInterface.Invoke)).Invoke();
        });
    }

    private static void RecordInspectorObservation(double width, OpenVisionLanguage language, string state, object observation)
    {
        var directory = Environment.GetEnvironmentVariable("OPENVISIONLAB_MACHINE_STUDIO_MANUAL_ENTRY_EVIDENCE");
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, $"inspector-{width}-{language}-{state}.json"),
            System.Text.Json.JsonSerializer.Serialize(observation, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }

    private static void Arrange(FrameworkElement view, double width, double height = 900)
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
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private static void Capture(FrameworkElement view, double width, OpenVisionLanguage language, string state)
    {
        var directory = Environment.GetEnvironmentVariable("OPENVISIONLAB_MACHINE_STUDIO_MANUAL_ENTRY_EVIDENCE");
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory);
        var bitmap = new RenderTargetBitmap((int)width, 900, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(view);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(directory, $"inspector-{width}-{language}-{state}.png"));
        encoder.Save(stream);
    }
}
