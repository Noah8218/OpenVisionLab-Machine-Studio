using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Xaml.Behaviors;

namespace OpenVisionLab.MachineStudio.Behavior;

public sealed class ComboBoxSelectionCommandBehavior : Behavior<ComboBox>
{
    public static readonly DependencyProperty CommandProperty = DependencyProperty.Register(
        nameof(Command), typeof(ICommand), typeof(ComboBoxSelectionCommandBehavior), new PropertyMetadata(null));

    private bool _isKeyboardSelection;
    private bool _isRefreshingSelection;
    private bool _hasPendingKeyboardCommit;
    private object? _selectionBeforeKeyboardCommit;
    private DispatcherOperation? _keyboardCommitOperation;

    public ICommand? Command
    {
        get => (ICommand?)GetValue(CommandProperty);
        set => SetValue(CommandProperty, value);
    }

    protected override void OnAttached()
    {
        base.OnAttached();
        AssociatedObject.PreviewKeyDown += OnPreviewKeyDown;
        AssociatedObject.PreviewKeyUp += OnPreviewKeyUp;
        AssociatedObject.SelectionChanged += OnSelectionChanged;
    }

    protected override void OnDetaching()
    {
        AssociatedObject.PreviewKeyDown -= OnPreviewKeyDown;
        AssociatedObject.PreviewKeyUp -= OnPreviewKeyUp;
        AssociatedObject.SelectionChanged -= OnSelectionChanged;
        ClearPendingKeyboardCommit();
        base.OnDetaching();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Up or Key.Down or Key.Home or Key.End or Key.PageUp or Key.PageDown or Key.Enter)
        {
            _isKeyboardSelection = true;
            if (e.Key == Key.Enter)
            {
                ClearPendingKeyboardCommit();
                _selectionBeforeKeyboardCommit = AssociatedObject.SelectedValue;
                _hasPendingKeyboardCommit = true;
            }
        }
    }

    private void OnPreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Up or Key.Down or Key.Home or Key.End or Key.PageUp or Key.PageDown or Key.Enter)
        {
            _isKeyboardSelection = false;
            if (e.Key == Key.Enter && _hasPendingKeyboardCommit)
            {
                _keyboardCommitOperation = AssociatedObject.Dispatcher.BeginInvoke(
                    DispatcherPriority.ContextIdle,
                    new Action(CommitPendingKeyboardSelection));
            }
        }
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isRefreshingSelection || (!AssociatedObject.IsDropDownOpen && !_isKeyboardSelection))
        {
            return;
        }

        if (e.AddedItems.Count == 0 || Command is not { } command)
        {
            RefreshSelectedValue();
            return;
        }

        var selectedValue = AssociatedObject.SelectedValue;
        try
        {
            if (_hasPendingKeyboardCommit && !Equals(selectedValue, _selectionBeforeKeyboardCommit))
            {
                ClearPendingKeyboardCommit();
            }

            if (command.CanExecute(selectedValue)) command.Execute(selectedValue);
        }
        finally
        {
            RefreshSelectedValue();
        }
    }

    private void CommitPendingKeyboardSelection()
    {
        _keyboardCommitOperation = null;
        if (!_hasPendingKeyboardCommit) return;

        var selectedValue = AssociatedObject.SelectedValue;
        var selectionChanged = !Equals(selectedValue, _selectionBeforeKeyboardCommit);
        ClearPendingKeyboardCommit();
        if (!selectionChanged) return;

        try
        {
            if (Command is { } command && command.CanExecute(selectedValue)) command.Execute(selectedValue);
        }
        finally
        {
            RefreshSelectedValue();
        }
    }

    private void ClearPendingKeyboardCommit()
    {
        if (_keyboardCommitOperation?.Status == DispatcherOperationStatus.Pending)
        {
            _keyboardCommitOperation.Abort();
        }

        _keyboardCommitOperation = null;
        _hasPendingKeyboardCommit = false;
        _selectionBeforeKeyboardCommit = null;
    }

    private void RefreshSelectedValue()
    {
        _isRefreshingSelection = true;
        try
        {
            AssociatedObject.GetBindingExpression(Selector.SelectedValueProperty)?.UpdateTarget();
        }
        finally
        {
            _isRefreshingSelection = false;
        }
    }
}
