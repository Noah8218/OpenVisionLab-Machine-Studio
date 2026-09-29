using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
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

    public static readonly DependencyProperty SaveCommandProperty =
        DependencyProperty.Register(
            nameof(SaveCommand),
            typeof(ICommand),
            typeof(ShellKeyboardCommandBehavior),
            new PropertyMetadata(null));

    public static readonly DependencyProperty SaveAsCommandProperty =
        DependencyProperty.Register(
            nameof(SaveAsCommand),
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

    public ICommand? SaveCommand
    {
        get => (ICommand?)GetValue(SaveCommandProperty);
        set => SetValue(SaveCommandProperty, value);
    }

    public ICommand? SaveAsCommand
    {
        get => (ICommand?)GetValue(SaveAsCommandProperty);
        set => SetValue(SaveAsCommandProperty, value);
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
        if (e.Handled)
        {
            return;
        }

        var saveCommand = ResolveSaveCommand(e.Key, Keyboard.Modifiers, SaveCommand, SaveAsCommand);
        if (saveCommand?.CanExecute(null) == true)
        {
            CommitFocusedNumberBoxEdit();
            saveCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (IsTextEditingFocus())
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

    internal static ICommand? ResolveSaveCommand(
        Key key,
        ModifierKeys modifiers,
        ICommand? saveCommand,
        ICommand? saveAsCommand) => (key, modifiers) switch
    {
        (Key.S, ModifierKeys.Control) => saveCommand,
        (Key.S, ModifierKeys.Control | ModifierKeys.Shift) => saveAsCommand,
        _ => null
    };

    private void CommitFocusedNumberBoxEdit()
    {
        if (Keyboard.FocusedElement is not DependencyObject focusedObject ||
            FindNumberBoxAncestor(focusedObject) is not { } numberBox)
        {
            return;
        }

        var originalFocus = Keyboard.FocusedElement;
        var next = new TraversalRequest(FocusNavigationDirection.Next);
        while (ReferenceEquals(FindNumberBoxAncestor(Keyboard.FocusedElement as DependencyObject), numberBox))
        {
            var previousFocus = Keyboard.FocusedElement;
            if (!AssociatedObject.MoveFocus(next) || ReferenceEquals(previousFocus, Keyboard.FocusedElement))
            {
                Keyboard.ClearFocus();
                break;
            }
        }

        if (originalFocus is not null)
        {
            Keyboard.Focus(originalFocus);
        }
    }

    private static global::Wpf.Ui.Controls.NumberBox? FindNumberBoxAncestor(DependencyObject? element)
    {
        for (DependencyObject? current = element; current is not null; current = GetParent(current))
        {
            if (current is global::Wpf.Ui.Controls.NumberBox numberBox)
            {
                return numberBox;
            }
        }

        return null;
    }

    private static DependencyObject? GetParent(DependencyObject element) => element switch
    {
        Visual or Visual3D => VisualTreeHelper.GetParent(element),
        FrameworkContentElement contentElement => contentElement.Parent,
        _ => LogicalTreeHelper.GetParent(element)
    };

    private static bool IsTextEditingFocus() => Keyboard.FocusedElement is
        TextBoxBase or
        PasswordBox or
        ComboBox { IsEditable: true };
}
