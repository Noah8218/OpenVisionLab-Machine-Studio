using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Microsoft.Xaml.Behaviors;

namespace OpenVisionLab.MachineStudio.Behavior;

public sealed class ShellKeyboardCommandBehavior : Behavior<Window>
{
    public static readonly DependencyProperty DeleteCommandProperty =
        DependencyProperty.Register(
            nameof(DeleteCommand),
            typeof(ICommand),
            typeof(ShellKeyboardCommandBehavior),
            new PropertyMetadata(null));

    public static readonly DependencyProperty DuplicateCommandProperty =
        DependencyProperty.Register(
            nameof(DuplicateCommand),
            typeof(ICommand),
            typeof(ShellKeyboardCommandBehavior),
            new PropertyMetadata(null));

    public ICommand? DeleteCommand
    {
        get => (ICommand?)GetValue(DeleteCommandProperty);
        set => SetValue(DeleteCommandProperty, value);
    }

    public ICommand? DuplicateCommand
    {
        get => (ICommand?)GetValue(DuplicateCommandProperty);
        set => SetValue(DuplicateCommandProperty, value);
    }

    protected override void OnAttached()
    {
        base.OnAttached();
        AssociatedObject.PreviewKeyDown += OnPreviewKeyDown;
    }

    protected override void OnDetaching()
    {
        AssociatedObject.PreviewKeyDown -= OnPreviewKeyDown;
        base.OnDetaching();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Handled || IsTextEditingFocus())
        {
            return;
        }

        var command = (e.Key, Keyboard.Modifiers) switch
        {
            (Key.Delete, ModifierKeys.None) => DeleteCommand,
            (Key.D, ModifierKeys.Control) => DuplicateCommand,
            _ => null
        };

        if (command?.CanExecute(null) != true)
        {
            return;
        }

        command.Execute(null);
        e.Handled = true;
    }

    private static bool IsTextEditingFocus() => Keyboard.FocusedElement is
        TextBoxBase or
        PasswordBox or
        ComboBox { IsEditable: true };
}
