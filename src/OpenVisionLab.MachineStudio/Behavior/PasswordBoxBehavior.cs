using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Xaml.Behaviors;

namespace OpenVisionLab.MachineStudio.Behavior;

public sealed class PasswordBoxBehavior : Behavior<PasswordBox>
{
    public static readonly DependencyProperty PasswordChangedCommandProperty =
        DependencyProperty.Register(
            nameof(PasswordChangedCommand),
            typeof(ICommand),
            typeof(PasswordBoxBehavior),
            new PropertyMetadata(null));

    public static readonly DependencyProperty ResetTokenProperty =
        DependencyProperty.Register(
            nameof(ResetToken),
            typeof(object),
            typeof(PasswordBoxBehavior),
            new PropertyMetadata(null, OnResetTokenChanged));

    public ICommand? PasswordChangedCommand
    {
        get => (ICommand?)GetValue(PasswordChangedCommandProperty);
        set => SetValue(PasswordChangedCommandProperty, value);
    }

    public object? ResetToken
    {
        get => GetValue(ResetTokenProperty);
        set => SetValue(ResetTokenProperty, value);
    }

    private bool _isResetting;

    protected override void OnAttached()
    {
        base.OnAttached();
        AssociatedObject.PasswordChanged += OnPasswordChanged;
    }

    protected override void OnDetaching()
    {
        AssociatedObject.PasswordChanged -= OnPasswordChanged;
        base.OnDetaching();
    }

    private void OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_isResetting)
        {
            return;
        }

        var command = PasswordChangedCommand;
        var password = string.IsNullOrEmpty(AssociatedObject.Password)
            ? null
            : AssociatedObject.Password;
        if (command?.CanExecute(password) == true)
        {
            command.Execute(password);
        }
    }

    private static void OnResetTokenChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs args)
    {
        if (dependencyObject is PasswordBoxBehavior behavior
            && behavior.AssociatedObject is { } passwordBox
            && !Equals(args.OldValue, args.NewValue))
        {
            behavior._isResetting = true;
            try
            {
                passwordBox.Clear();
            }
            finally
            {
                behavior._isResetting = false;
            }
        }
    }
}
