using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Xaml.Behaviors;
using OpenVisionLab.MachineStudio.Behavior;
using OpenVisionLab.MachineStudio.ViewModel;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

public sealed class ComboBoxSelectionCommandBehaviorTests
{
    [Fact]
    public async Task RoutesKeyboardSelectionButIgnoresBindingUpdatesAndDetachesEvents()
    {
        var routedValues = await RunOnStaAsync(() =>
        {
            var state = new SelectionState { ActiveUnitId = "unit-a" };
            var comboBox = new ComboBox
            {
                ItemsSource = new[] { new UnitOption("unit-a"), new UnitOption("unit-b"), new UnitOption("unit-c") },
                SelectedValuePath = nameof(UnitOption.Id)
            };
            using var inputSource = new HwndSource(new HwndSourceParameters("ComboBoxSelectionCommandBehaviorTest")
            {
                Width = 1,
                Height = 1,
                WindowStyle = unchecked((int)0x80000000u)
            });
            inputSource.RootVisual = comboBox;
            comboBox.SetBinding(Selector.SelectedValueProperty, new Binding(nameof(SelectionState.ActiveUnitId))
            {
                Source = state,
                Mode = BindingMode.OneWay
            });
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
            var values = new List<string?>();
            var behavior = new ComboBoxSelectionCommandBehavior
            {
                Command = new RelayCommand(value =>
                {
                    values.Add(value as string);
                    if (values.Count is 1 or 3) state.ActiveUnitId = value as string;
                })
            };
            var behaviors = Interaction.GetBehaviors(comboBox);
            behaviors.Add(behavior);

            Assert.Equal("unit-a", comboBox.SelectedValue);
            state.ActiveUnitId = "unit-c";
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
            Assert.Empty(values);
            Assert.Equal("unit-c", comboBox.SelectedValue);

            comboBox.RaiseEvent(CreateKeyEventArgs(inputSource, Key.Down, Keyboard.PreviewKeyDownEvent));
            comboBox.SetCurrentValue(Selector.SelectedValueProperty, "unit-b");
            Assert.Equal(new string?[] { "unit-b" }, values);
            Assert.Equal("unit-b", state.ActiveUnitId);
            Assert.Equal("unit-b", comboBox.SelectedValue);
            comboBox.RaiseEvent(CreateKeyEventArgs(inputSource, Key.Down, Keyboard.PreviewKeyUpEvent));
            state.ActiveUnitId = "unit-a";
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
            Assert.Equal(["unit-b"], values);
            Assert.Equal("unit-a", comboBox.SelectedValue);

            comboBox.RaiseEvent(CreateKeyEventArgs(inputSource, Key.Down, Keyboard.PreviewKeyDownEvent));
            comboBox.SetCurrentValue(Selector.SelectedValueProperty, "unit-b");
            Assert.Equal(new string?[] { "unit-b", "unit-b" }, values);
            Assert.Equal("unit-a", state.ActiveUnitId);
            Assert.Equal("unit-a", comboBox.SelectedValue);
            comboBox.RaiseEvent(CreateKeyEventArgs(inputSource, Key.Down, Keyboard.PreviewKeyUpEvent));

            comboBox.RaiseEvent(CreateKeyEventArgs(inputSource, Key.Enter, Keyboard.PreviewKeyDownEvent));
            comboBox.RaiseEvent(CreateKeyEventArgs(inputSource, Key.Enter, Keyboard.PreviewKeyUpEvent));
            comboBox.SetCurrentValue(Selector.SelectedValueProperty, "unit-c");
            Assert.Equal(new string?[] { "unit-b", "unit-b" }, values);
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            Assert.Equal(new string?[] { "unit-b", "unit-b", "unit-c" }, values);
            Assert.Equal("unit-c", state.ActiveUnitId);
            Assert.Equal("unit-c", comboBox.SelectedValue);

            comboBox.RaiseEvent(CreateKeyEventArgs(inputSource, Key.Enter, Keyboard.PreviewKeyDownEvent));
            comboBox.RaiseEvent(CreateKeyEventArgs(inputSource, Key.Enter, Keyboard.PreviewKeyUpEvent));
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            Assert.Equal(new string?[] { "unit-b", "unit-b", "unit-c" }, values);

            comboBox.RaiseEvent(CreateKeyEventArgs(inputSource, Key.Enter, Keyboard.PreviewKeyDownEvent));
            comboBox.RaiseEvent(CreateKeyEventArgs(inputSource, Key.Enter, Keyboard.PreviewKeyUpEvent));
            comboBox.SetCurrentValue(Selector.SelectedValueProperty, "unit-b");
            behaviors.Remove(behavior);
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            Assert.Equal(new string?[] { "unit-b", "unit-b", "unit-c" }, values);
            comboBox.SetCurrentValue(Selector.SelectedValueProperty, "unit-a");
            return values.ToArray();
        });

        Assert.Equal(new string?[] { "unit-b", "unit-b", "unit-c" }, routedValues);
    }

    private static KeyEventArgs CreateKeyEventArgs(HwndSource inputSource, Key key, RoutedEvent routedEvent) => new(
        Keyboard.PrimaryDevice,
        inputSource,
        0,
        key)
    {
        RoutedEvent = routedEvent
    };

    private static Task<T> RunOnStaAsync<T>(Func<T> action)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                completion.SetResult(action());
            }
            catch (Exception exception)
            {
                completion.SetException(exception);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private sealed record UnitOption(string Id);

    private sealed class SelectionState : INotifyPropertyChanged
    {
        private string? _activeUnitId;

        public event PropertyChangedEventHandler? PropertyChanged;

        public string? ActiveUnitId
        {
            get => _activeUnitId;
            set
            {
                if (_activeUnitId == value) return;
                _activeUnitId = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ActiveUnitId)));
            }
        }
    }
}
