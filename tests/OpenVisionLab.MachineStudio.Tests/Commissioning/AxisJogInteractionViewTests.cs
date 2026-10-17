using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Xaml.Behaviors;
using OpenVisionLab;
using OpenVisionLab.Machine.Core.Axes;
using OpenVisionLab.Machine.Core.Layouts;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Simulation.Axis;
using OpenVisionLab.Machine.Simulation.Commands;
using OpenVisionLab.Machine.Simulation.Engine;
using OpenVisionLab.MachineStudio.Behavior;
using OpenVisionLab.MachineStudio.Model;
using OpenVisionLab.MachineStudio.View.Inspector;
using OpenVisionLab.MachineStudio.ViewModel;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

[Collection(StudioUiTestCollection.Name)]
public sealed class AxisJogInteractionViewTests
{
    private readonly StudioUiTestHost _ui;

    public AxisJogInteractionViewTests(StudioUiTestHost ui) => _ui = ui;

    [Theory]
    [InlineData("key-up")]
    [InlineData("mouse-up")]
    [InlineData("capture-lost")]
    [InlineData("focus-lost")]
    [InlineData("unloaded")]
    [InlineData("disabled")]
    [InlineData("detached")]
    public async Task RenderedJogEndsOnceAtInputLifetimeBoundaryAndCanRecover(string boundary)
    {
        await _ui.InvokeTaskAsync(async () =>
        {
            var commands = new List<SimulationCommand>();
            using var vm = new AxisCommissioningViewModel((command, _) =>
            {
                commands.Add(command);
                return Task.FromResult(Accept(command));
            }, exception => throw exception);
            vm.ApplyProjection(Projection("axis.x"));
            var view = new AxisCommissioningPanelView { DataContext = new { AxisCommissioning = vm } };
            var parent = InspectorParent(view);
            using var source = Mount(parent, 300);
            var button = view.JogPositiveButtonControl;
            var behavior = Assert.IsType<PressAndReleaseCommandBehavior>(Interaction.GetBehaviors(button).Single());
            Assert.True(button.IsVisible && button.IsEnabled);
            Assert.Same(vm.BeginAxisJogPositiveCommand, behavior.PressCommand);
            Assert.Same(vm.EndAxisJogCommand, behavior.ReleaseCommand);
            if (boundary is "mouse-up" or "capture-lost")
                button.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent });
            else Press(button, source);
            Assert.Equal("axis.x", Assert.IsType<JogAxisCommand>(Assert.Single(commands)).AxisId);
            Capture(parent, 300, boundary + "-held");
            switch (boundary)
            {
                case "key-up": Release(button, source); break;
                case "mouse-up": button.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseLeftButtonUpEvent }); break;
                case "capture-lost": button.ReleaseMouseCapture(); break;
                case "focus-lost": button.RaiseEvent(new KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice, 0, button, view.HomeAxisButtonControl) { RoutedEvent = Keyboard.LostKeyboardFocusEvent }); break;
                case "unloaded": button.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent)); break;
                case "disabled": button.IsEnabled = false; break;
                case "detached": Interaction.GetBehaviors(button).Remove(behavior); break;
            }
            await Task.Yield();
            Capture(parent, 300, boundary + "-released");
            Assert.Equal(2, commands.Count);
            Assert.Equal("axis.x", Assert.IsType<StopAxisCommand>(commands[1]).AxisId);
            if (boundary == "disabled") button.ClearValue(UIElement.IsEnabledProperty);
            if (boundary == "detached") Interaction.GetBehaviors(button).Add(behavior);
            Release(button, source);
            Assert.Equal(2, commands.Count);
            Press(button, source);
            Release(button, source);
            await Task.Yield();
            Assert.Equal(4, commands.Count);
            Assert.IsType<JogAxisCommand>(commands[2]);
            Assert.IsType<StopAxisCommand>(commands[3]);
            Assert.True(vm.CanJogAxis);
        });
    }

    [Theory]
    [InlineData(300, OpenVisionLanguage.Korean)]
    [InlineData(420, OpenVisionLanguage.English)]
    public async Task SelectedAxisRuntimeStopsOnFocusAndSelectionLossThenRecovers(int width, OpenVisionLanguage language)
    {
        await _ui.InvokeTaskAsync(async () =>
        {
            var previousLanguage = OpenVisionLanguageService.CurrentLanguage;
            OpenVisionLanguageService.SetLanguage(language, save: false);
            try
            {
                var project = new ProjectDocumentStore().Load(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Samples", "AutomaticTransferCell.ovmachine")));
                var payload = System.Text.Json.JsonSerializer.Serialize(project);
                using var vm = new MainViewModel(project);
                vm.Navigation.HideStartupChoice();
                vm.Navigation.IsEquipmentWorkspace = true;
                var stage = vm.Layout.Items.Single(item => item.Component?.Kind == LayoutComponentKind.LinearStage);
                var sensor = vm.Layout.Items.First(item => item.Component?.Kind == LayoutComponentKind.DigitalSensor);
                var axisId = stage.Component!.BehaviorBindingId;
                vm.Layout.Select(stage.Id);
                await WaitAsync(() => vm.CanStartManualEquipmentControl);
                vm.OpenEquipmentSinglePartCheckCommand.Execute(null);
                var view = new RightToolRegionView { DataContext = vm };
                using var source = Mount(view, width);
                var entry = Assert.IsType<Button>(view.FindName("EnterManualCommissioningButton"));
                Assert.IsAssignableFrom<System.Windows.Automation.Provider.IInvokeProvider>(
                    new System.Windows.Automation.Peers.ButtonAutomationPeer(entry).GetPattern(System.Windows.Automation.Peers.PatternInterface.Invoke)).Invoke();
                await WaitAsync(() => vm.IsRunning && vm.CanJogAxis);
                view.UpdateLayout();
                var button = view.JogPositiveButton;
                Assert.True(button.IsVisible && button.IsEnabled);
                var start = Axis().Position;
                Press(button, source);
                await WaitAsync(() => Axis().State == AxisState.Moving && Axis().Position > start);
                Capture(view, width, "runtime-" + language + "-jog");
                button.RaiseEvent(new KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice, 0, button, view.HomeAxisButton) { RoutedEvent = Keyboard.LostKeyboardFocusEvent });
                await WaitAsync(() => Axis().State == AxisState.Stopped && vm.CanJogAxis);
                var stopped = Axis().Position;
                await Task.Delay(30);
                Assert.Equal(stopped, Axis().Position);
                Capture(view, width, "runtime-" + language + "-focus-stopped");
                Press(button, source);
                await WaitAsync(() => Axis().State == AxisState.Moving);
                vm.Layout.Select(sensor.Id);
                await WaitAsync(() => Axis().State == AxisState.Stopped);
                Assert.Equal(sensor.Id, vm.Layout.SelectedItem?.Id);
                Assert.False(vm.CanJogAxis);
                Release(button, source);
                vm.Layout.Select(stage.Id);
                await WaitAsync(() => vm.CanJogAxis);
                view.UpdateLayout();
                Press(button, source);
                await WaitAsync(() => Axis().State == AxisState.Moving);
                Release(button, source);
                await WaitAsync(() => Axis().State == AxisState.Stopped && vm.CanJogAxis);
                Assert.Equal(payload, System.Text.Json.JsonSerializer.Serialize(project));
                Assert.False(vm.HasUnsavedChanges);
                vm.IsDesignMode = true;
                await WaitAsync(() => vm.IsDesignMode && !vm.IsRunning && vm.SceneSnapshots.Latest?.SimulationTime == TimeSpan.Zero);
                Assert.Equal(stage.Id, vm.Layout.SelectedItem?.Id);
                Assert.Equal(payload, System.Text.Json.JsonSerializer.Serialize(project));
                Assert.False(vm.CanJogAxis || vm.HasUnsavedChanges);
                Capture(view, width, "runtime-" + language + "-edit");

                AxisSnapshot Axis() => vm.SceneSnapshots.Latest!.Axes.Single(axis => axis.Id == axisId);
            }
            finally { OpenVisionLanguageService.SetLanguage(previousLanguage, save: false); }
            async Task WaitAsync(Func<bool> condition)
            {
                var deadline = DateTime.UtcNow.AddSeconds(5);
                while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(10);
                Assert.True(condition(), "The selected-axis JOG fixture did not settle.");
            }
        });
    }

    [Theory]
    [InlineData("selection")]
    [InlineData("no-selection")]
    [InlineData("read-only")]
    public async Task HeldJogStopsOriginalAxisWhenProjectionLosesAdmissionAndRecovers(string boundary)
    {
        await _ui.InvokeTaskAsync(async () =>
        {
            var commands = new List<SimulationCommand>();
            using var vm = new AxisCommissioningViewModel((command, _) =>
            {
                commands.Add(command);
                return Task.FromResult(Accept(command));
            }, exception => throw exception);
            var projection = Projection("axis.x");
            vm.ApplyProjection(projection);
            var view = new AxisCommissioningPanelView { DataContext = new { AxisCommissioning = vm } };
            var parent = InspectorParent(view);
            using var source = Mount(parent, 420);
            var button = view.JogPositiveButtonControl;
            Press(button, source);
            vm.ApplyProjection(boundary switch
            {
                "selection" => Projection("axis.y"),
                "no-selection" => projection with { HasSelectedAxisStage = false },
                _ => projection with { IsValidationBusy = true }
            });
            view.UpdateLayout();
            await Task.Yield();
            Capture(parent, 420, boundary + "-projection");
            Assert.Equal(2, commands.Count);
            Assert.Equal("axis.x", Assert.IsType<StopAxisCommand>(commands[1]).AxisId);
            if (boundary != "selection") Assert.False(button.IsEnabled || vm.CanJogAxis);
            Release(button, source);
            Assert.Equal(2, commands.Count);
            vm.ApplyProjection(Projection("axis.y"));
            view.UpdateLayout();
            Press(button, source);
            Release(button, source);
            await Task.Yield();
            Assert.Equal(4, commands.Count);
            Assert.Equal("axis.y", Assert.IsType<JogAxisCommand>(commands[2]).AxisId);
            Assert.Equal("axis.y", Assert.IsType<StopAxisCommand>(commands[3]).AxisId);
            Assert.True(button.IsEnabled && vm.CanJogAxis);
        });
    }

    [Theory]
    [InlineData(300, OpenVisionLanguage.Korean)]
    [InlineData(420, OpenVisionLanguage.Korean)]
    [InlineData(300, OpenVisionLanguage.English)]
    [InlineData(420, OpenVisionLanguage.English)]
    public async Task AxisSoftLimitAndRejectedTargetRecoverThroughRenderedCommands(int width, OpenVisionLanguage language)
    {
        await _ui.InvokeTaskAsync(async () =>
        {
            var previousLanguage = OpenVisionLanguageService.CurrentLanguage;
            OpenVisionLanguageService.SetLanguage(language, save: false);
            try
            {
                var project = new ProjectDocumentStore().Load(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Samples", "AutomaticTransferCell.ovmachine")));
                var payload = System.Text.Json.JsonSerializer.Serialize(project);
                using var vm = new MainViewModel(project);
                vm.Navigation.HideStartupChoice();
                vm.Navigation.IsEquipmentWorkspace = true;
                var stage = vm.Layout.Items.Single(item => item.Component?.Kind == LayoutComponentKind.LinearStage);
                var axisId = stage.Component!.BehaviorBindingId;
                var definition = project.Axes.Single(axis => axis.Id == axisId);
                vm.Layout.Select(stage.Id);
                await WaitForAxisRecoveryAsync(() => vm.CanStartManualEquipmentControl, "manual entry");
                vm.OpenEquipmentSinglePartCheckCommand.Execute(null);
                var view = new RightToolRegionView { DataContext = vm };
                using var source = Mount(view, width);
                Invoke(Assert.IsType<Button>(view.FindName("EnterManualCommissioningButton")));
                await WaitForAxisRecoveryAsync(() => vm.IsRunning && vm.CanJogAxis, "manual ready");

                view.AxisTargetPositionTextBox.Text = "NaN";
                view.UpdateLayout();
                Assert.True(vm.HasAxisTargetPositionError && view.AxisTargetValidationText.IsVisible);
                Assert.False(view.MoveAxisAbsoluteButton.IsEnabled || view.MoveAxisAbsoluteButton.Command!.CanExecute(null));
                Assert.Equal(definition.HomePosition, Axis().Position);
                RecordAxisRecovery(vm, view, width, language, "limit-nan");
                view.AxisTargetPositionTextBox.Text = (definition.SoftLimitMax!.Value + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
                view.UpdateLayout();
                Assert.True(vm.HasAxisTargetPositionError && view.AxisTargetValidationText.IsVisible);
                Assert.False(view.MoveAxisAbsoluteButton.IsEnabled);
                RecordAxisRecovery(vm, view, width, language, "limit-outside");
                view.AxisTargetPositionTextBox.Text = (definition.HomePosition + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
                Invoke(view.MoveAxisAbsoluteButton);
                try
                {
                    await WaitForAxisRecoveryAsync(() => Axis().State == AxisState.Idle && AtPosition(definition.HomePosition + 1) && vm.CanJogAxis, "valid absolute retry");
                }
                catch
                {
                    RecordAxisRecovery(vm, view, width, language, "limit-valid-retry-unsettled");
                    throw;
                }
                Assert.False(vm.HasAxisTargetPositionError || view.AxisTargetValidationText.IsVisible);

                var positionBeforeRejection = Axis().Position;
                view.AxisRelativeDistanceTextBox.Text = (definition.SoftLimitMax.Value + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
                var rejectedStatus = string.Format(System.Globalization.CultureInfo.CurrentCulture,
                    OpenVisionLanguageService.T("Axis.StatusRejected"), OpenVisionLanguageService.T("Axis.ActionMoveRelative"));
                Invoke(view.MoveAxisRelativeButton);
                await WaitForAxisRecoveryAsync(() => vm.StatusMessage == rejectedStatus && vm.CanMoveAxisRelative, "relative engine rejection");
                Assert.Equal(positionBeforeRejection, Axis().Position);
                Assert.Equal(AxisState.Idle, Axis().State);
                RecordAxisRecovery(vm, view, width, language, "limit-relative-rejected");
                view.AxisRelativeDistanceTextBox.Text = "-0.5";
                Invoke(view.MoveAxisRelativeButton);
                await WaitForAxisRecoveryAsync(() => Axis().State == AxisState.Idle && AtPosition(definition.HomePosition + 0.5) && vm.CanJogAxis, "relative retry");

                Press(view.JogPositiveButton, source);
                await WaitForAxisRecoveryAsync(() => Axis().State == AxisState.Limited && AtPosition(definition.SoftLimitMax.Value), "upper limit");
                Release(view.JogPositiveButton, source);
                await WaitForAxisRecoveryAsync(() => !vm.EndAxisJogCommand.CanExecute(null) && vm.CanJogAxis, "upper release");
                Assert.Equal(AxisState.Limited, Axis().State);
                RecordAxisRecovery(vm, view, width, language, "limit-upper");
                Press(view.JogNegativeButton, source);
                await WaitForAxisRecoveryAsync(() => Axis().State == AxisState.Moving && Axis().Position < definition.SoftLimitMax.Value, "reverse from upper limit");
                await WaitForAxisRecoveryAsync(() => Axis().State == AxisState.Limited && AtPosition(definition.SoftLimitMin!.Value), "lower limit");
                Release(view.JogNegativeButton, source);
                await WaitForAxisRecoveryAsync(() => !vm.EndAxisJogCommand.CanExecute(null) && vm.CanJogAxis, "lower release");
                Assert.Equal(AxisState.Limited, Axis().State);
                RecordAxisRecovery(vm, view, width, language, "limit-lower");
                view.AxisTargetPositionTextBox.Text = (definition.HomePosition + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
                Invoke(view.MoveAxisAbsoluteButton);
                await WaitForAxisRecoveryAsync(() => Axis().State == AxisState.Idle && AtPosition(definition.HomePosition + 1) && vm.CanJogAxis, "move away from home");
                Invoke(view.HomeAxisButton);
                await WaitForAxisRecoveryAsync(() => Axis().State == AxisState.Idle && AtPosition(definition.HomePosition) && vm.CanJogAxis, "stored home");
                Assert.Equal(0, Axis().Velocity);
                Assert.Equal(SimulationControlOwner.Manual, vm.SceneSnapshots.Latest!.ControlOwner);
                Assert.Equal(payload, System.Text.Json.JsonSerializer.Serialize(project));
                Assert.False(vm.HasUnsavedChanges);
                RecordAxisRecovery(vm, view, width, language, "limit-home");
                vm.IsDesignMode = true;
                await WaitForAxisRecoveryAsync(() => vm.IsDesignMode && !vm.IsRunning && vm.SceneSnapshots.Latest?.SimulationTime == TimeSpan.Zero, "Edit reset");
                Assert.Equal(stage.Id, vm.Layout.SelectedItem?.Id);
                Assert.Equal(payload, System.Text.Json.JsonSerializer.Serialize(project));
                Assert.False(vm.HasUnsavedChanges || vm.CanJogAxis);
                RecordAxisRecovery(vm, view, width, language, "limit-edit");

                bool AtPosition(double target) => Math.Abs(Axis().Position - target) < 1e-9;
                AxisSnapshot Axis() => vm.SceneSnapshots.Latest!.Axes.Single(axis => axis.Id == axisId);
            }
            finally { OpenVisionLanguageService.SetLanguage(previousLanguage, save: false); }
        });
    }

    [Theory]
    [InlineData(300, OpenVisionLanguage.Korean)]
    [InlineData(420, OpenVisionLanguage.Korean)]
    [InlineData(300, OpenVisionLanguage.English)]
    [InlineData(420, OpenVisionLanguage.English)]
    public async Task AxisFaultsKeepCommandsBlockedUntilAllCausesClear(int width, OpenVisionLanguage language)
    {
        await _ui.InvokeTaskAsync(async () =>
        {
            var previousLanguage = OpenVisionLanguageService.CurrentLanguage;
            OpenVisionLanguageService.SetLanguage(language, save: false);
            try
            {
                var project = new ProjectDocumentStore().Load(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Samples", "AutomaticTransferCell.ovmachine")));
                var payload = System.Text.Json.JsonSerializer.Serialize(project);
                using var vm = new MainViewModel(project);
                vm.Navigation.HideStartupChoice();
                vm.Navigation.IsEquipmentWorkspace = true;
                var stage = vm.Layout.Items.Single(item => item.Component?.Kind == LayoutComponentKind.LinearStage);
                var axisId = stage.Component!.BehaviorBindingId;
                vm.Layout.Select(stage.Id);
                await WaitForAxisRecoveryAsync(() => vm.CanStartManualEquipmentControl, "manual entry");
                vm.OpenEquipmentSinglePartCheckCommand.Execute(null);
                var view = new RightToolRegionView { DataContext = vm };
                using var source = Mount(view, width);
                Invoke(Assert.IsType<Button>(view.FindName("EnterManualCommissioningButton")));
                await WaitForAxisRecoveryAsync(() => vm.IsRunning && vm.CanJogAxis, "manual ready");
                var manager = vm.FaultManager;
                await InjectAsync(OpenVisionLab.Machine.Simulation.Faults.SimulationFaultKind.AxisMotionBlocked);
                await WaitForAxisRecoveryAsync(() => Axis().State == AxisState.Error && vm.IsCurrentAxisInterlocked, "motion interlock");
                CheckBlocked(alarm: false, "fault-interlock");
                var blockedPosition = Axis().Position;
                vm.BeginAxisJogPositiveCommand.Execute(null);
                vm.HomeAxisCommand.Execute(null);
                await Task.Yield();
                Assert.Equal(blockedPosition, Axis().Position);
                await ClearAsync(OpenVisionLab.Machine.Simulation.Faults.SimulationFaultKind.AxisMotionBlocked);
                await WaitForAxisRecoveryAsync(() => Axis().State == AxisState.Stopped && vm.CanJogAxis, "interlock cleared");
                CheckReady("fault-interlock-cleared");

                await InjectAsync(OpenVisionLab.Machine.Simulation.Faults.SimulationFaultKind.AxisFollowingError);
                Assert.False(Axis().DriveAlarmActive);
                Press(view.JogPositiveButton, source);
                await WaitForAxisRecoveryAsync(() => Axis().DriveAlarmActive && Axis().State == AxisState.Error && vm.IsCurrentAxisDriveAlarmActive, "following-error alarm");
                Release(view.JogPositiveButton, source);
                CheckBlocked(alarm: true, "fault-drive-alarm");
                Assert.Equal(0, Axis().Velocity);
                Assert.True(Math.Abs(Axis().FollowingError) >= Axis().FollowingErrorLimit);
                await InjectAsync(OpenVisionLab.Machine.Simulation.Faults.SimulationFaultKind.AxisMotionBlocked);
                Assert.Equal(2, manager.ActiveFaults.Count(fault => fault.TargetId == axisId));
                CheckBlocked(alarm: true, "fault-overlap");
                await ClearAsync(OpenVisionLab.Machine.Simulation.Faults.SimulationFaultKind.AxisFollowingError);
                await WaitForAxisRecoveryAsync(() => !Axis().DriveAlarmActive && Axis().State == AxisState.Error && !vm.IsCurrentAxisDriveAlarmActive, "alarm clear retains motion block");
                CheckBlocked(alarm: false, "fault-alarm-cleared-block-retained");
                Assert.Equal(0, Axis().FollowingError);
                Assert.Single(manager.ActiveFaults.Where(fault => fault.TargetId == axisId));
                await ClearAsync(OpenVisionLab.Machine.Simulation.Faults.SimulationFaultKind.AxisMotionBlocked);
                await WaitForAxisRecoveryAsync(() => Axis().State == AxisState.Stopped && vm.CanJogAxis, "all faults cleared");
                CheckReady("fault-all-cleared");
                var recoveredPosition = Axis().Position;
                Press(view.JogPositiveButton, source);
                await WaitForAxisRecoveryAsync(() => Axis().State == AxisState.Moving && Axis().Position > recoveredPosition, "new JOG after recovery");
                Release(view.JogPositiveButton, source);
                await WaitForAxisRecoveryAsync(() => Axis().State == AxisState.Stopped && vm.CanJogAxis, "recovered JOG release");
                Assert.Empty(manager.ActiveFaults);
                Assert.Equal(payload, System.Text.Json.JsonSerializer.Serialize(project));
                Assert.False(vm.HasUnsavedChanges);
                RecordAxisRecovery(vm, view, width, language, "fault-recovered-jog");
                vm.IsDesignMode = true;
                await WaitForAxisRecoveryAsync(() => vm.IsDesignMode && !vm.IsRunning && vm.SceneSnapshots.Latest?.SimulationTime == TimeSpan.Zero, "Edit reset");
                Assert.Equal(stage.Id, vm.Layout.SelectedItem?.Id);
                Assert.Equal(payload, System.Text.Json.JsonSerializer.Serialize(project));
                Assert.False(vm.HasUnsavedChanges || vm.CanJogAxis);
                RecordAxisRecovery(vm, view, width, language, "fault-edit");

                AxisSnapshot Axis() => vm.SceneSnapshots.Latest!.Axes.Single(axis => axis.Id == axisId);
                async Task InjectAsync(OpenVisionLab.Machine.Simulation.Faults.SimulationFaultKind kind)
                {
                    manager.SelectedKind = manager.AvailableKinds.Single(option => option.Kind == kind);
                    manager.SelectedTarget = manager.Targets.Single(target => target.Id == axisId);
                    Assert.True(manager.InjectCommand.CanExecute(null));
                    manager.InjectCommand.Execute(null);
                    await WaitForAxisRecoveryAsync(() => !manager.IsOperationPending && manager.ActiveFaults.Any(fault => fault.Kind == kind && fault.TargetId == axisId), "fault injection");
                }
                async Task ClearAsync(OpenVisionLab.Machine.Simulation.Faults.SimulationFaultKind kind)
                {
                    manager.SelectedActiveFault = manager.ActiveFaults.Single(fault => fault.Kind == kind && fault.TargetId == axisId);
                    Assert.True(manager.ClearSelectedCommand.CanExecute(null));
                    manager.ClearSelectedCommand.Execute(null);
                    await WaitForAxisRecoveryAsync(() => !manager.IsOperationPending && !manager.ActiveFaults.Any(fault => fault.Kind == kind && fault.TargetId == axisId), "fault clear");
                }
                void CheckBlocked(bool alarm, string state)
                {
                    view.UpdateLayout();
                    Assert.Equal(OpenVisionLanguageService.T("Axis.InterlockBlocked"), view.AxisInterlockStatusText.Text);
                    Assert.Equal(OpenVisionLanguageService.T(alarm ? "Axis.DriveAlarmActive" : "Axis.DriveAlarmReady"), view.AxisDriveAlarmStatusText.Text);
                    Assert.Same(Application.Current.FindResource("State.Fault"), view.AxisInterlockStatusText.Foreground);
                    if (alarm) Assert.Same(Application.Current.FindResource("State.Fault"), view.AxisDriveAlarmStatusText.Foreground);
                    Assert.Equal(OpenVisionLanguageService.T(alarm ? "Axis.ClearDriveAlarmHint" : "Axis.ClearInterlockHint"), vm.AxisCommissioningHintText);
                    Assert.False(view.JogNegativeButton.IsEnabled || view.JogPositiveButton.IsEnabled || view.HomeAxisButton.IsEnabled || view.MoveAxisAbsoluteButton.IsEnabled);
                    Assert.False(vm.BeginAxisJogPositiveCommand.CanExecute(null) || vm.MoveAxisAbsoluteCommand.CanExecute(null) || vm.HomeAxisCommand.CanExecute(null));
                    RecordAxisRecovery(vm, view, width, language, state);
                }
                void CheckReady(string state)
                {
                    view.UpdateLayout();
                    Assert.Equal(OpenVisionLanguageService.T("Axis.InterlockReady"), view.AxisInterlockStatusText.Text);
                    Assert.Equal(OpenVisionLanguageService.T("Axis.DriveAlarmReady"), view.AxisDriveAlarmStatusText.Text);
                    Assert.NotSame(Application.Current.FindResource("State.Fault"), view.AxisInterlockStatusText.Foreground);
                    Assert.NotSame(Application.Current.FindResource("State.Fault"), view.AxisDriveAlarmStatusText.Foreground);
                    Assert.True(view.JogNegativeButton.IsEnabled && view.JogPositiveButton.IsEnabled && view.HomeAxisButton.IsEnabled && view.MoveAxisAbsoluteButton.IsEnabled);
                    RecordAxisRecovery(vm, view, width, language, state);
                }
            }
            finally { OpenVisionLanguageService.SetLanguage(previousLanguage, save: false); }
        });
    }

    private static async Task WaitForAxisRecoveryAsync(Func<bool> condition, string state)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(10);
        Assert.True(condition(), "Selected-axis state did not settle: " + state);
    }

    private static void Invoke(Button button)
    {
        button.UpdateLayout();
        Assert.True(button.IsEnabled && button.Command!.CanExecute(null));
        Assert.IsAssignableFrom<System.Windows.Automation.Provider.IInvokeProvider>(
            new System.Windows.Automation.Peers.ButtonAutomationPeer(button).GetPattern(System.Windows.Automation.Peers.PatternInterface.Invoke)).Invoke();
    }

    private static void RecordAxisRecovery(MainViewModel vm, RightToolRegionView view, int width, OpenVisionLanguage language, string state)
    {
        view.UpdateLayout();
        var captureName = state + "-" + language + "-" + width;
        Capture(view, width, captureName);
        var directory = Environment.GetEnvironmentVariable("OPENVISIONLAB_MACHINE_STUDIO_JOG_EVIDENCE");
        if (string.IsNullOrWhiteSpace(directory)) return;
        var observation = new
        {
            atUtc = DateTime.UtcNow, state, language = language.ToString(), width,
            sourceDll = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(typeof(MainViewModel).Assembly.Location))),
            snapshot = vm.SceneSnapshots.Latest!.Axes.Select(axis => new { axis.Id, state = axis.State.ToString(), axis.Position, axis.Velocity, axis.DriveAlarmActive, axis.FollowingError, axis.FollowingErrorLimit }),
            renderedInterlock = view.AxisInterlockStatusText.Text, renderedDriveAlarm = view.AxisDriveAlarmStatusText.Text,
            projectedState = vm.CurrentAxisStateText, projectedPosition = vm.CurrentAxisPositionText,
            jogEnabled = view.JogPositiveButton.IsEnabled, homeEnabled = view.HomeAxisButton.IsEnabled,
            vm.IsRunning, vm.IsDesignMode, vm.HasUnsavedChanges, selectedId = vm.Layout.SelectedItem?.Id,
            clock = vm.SceneSnapshots.Latest.SimulationTime, native = false, physicalInput = false
        };
        File.WriteAllText(Path.Combine(directory, captureName + ".json"), System.Text.Json.JsonSerializer.Serialize(observation, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }

    private static AxisCommissioningProjection Projection(string id) => new(
        new AxisSnapshot(id, id, AxisState.Idle, 12.5, 0),
        new VirtualAxisDefinition { Id = id, SoftLimitMin = 0, SoftLimitMax = 100, MaxVelocity = 50 },
        HasSelectedAxisStage: true, IsRunMode: true, IsApplyingProject: false, IsValidationBusy: false,
        RuntimeDefinitionDirty: false, IsRunning: true, ControlOwner: SimulationControlOwner.Manual,
        AutomaticRunActive: false, SequenceRunActive: false);

    private static SimulationCommandResult Accept(SimulationCommand command) => new(
        command.CommandId, true, 1, TimeSpan.Zero, SimulationCommandErrorCode.None, null);

    private static RightToolRegionView InspectorParent(FrameworkElement view) => new()
    {
        Content = new Border { Background = (Brush)Application.Current.FindResource("Surface.Panel"), Child = view }
    };

    private static HwndSource Mount(FrameworkElement view, int width)
    {
        var source = new HwndSource(new HwndSourceParameters("AxisJogInteractionFixture")
        {
            Width = width, Height = 900, WindowStyle = unchecked((int)0x80000000)
        });
        source.RootVisual = view;
        view.Measure(new Size(width, 900));
        view.Arrange(new Rect(0, 0, width, 900));
        view.UpdateLayout();
        return source;
    }

    private static void Press(Button button, HwndSource source) => button.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Space)
    {
        RoutedEvent = UIElement.PreviewKeyDownEvent
    });

    private static void Release(Button button, HwndSource source) => button.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Space)
    {
        RoutedEvent = UIElement.PreviewKeyUpEvent
    });

    private static void Capture(FrameworkElement view, int width, string state)
    {
        var directory = Environment.GetEnvironmentVariable("OPENVISIONLAB_MACHINE_STUDIO_JOG_EVIDENCE");
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory);
        var bitmap = new RenderTargetBitmap(width, 900, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(view);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(directory, state + ".png"));
        encoder.Save(stream);
    }
}
