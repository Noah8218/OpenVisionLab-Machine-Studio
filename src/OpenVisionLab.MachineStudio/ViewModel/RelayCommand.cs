using System.Windows.Input;

namespace OpenVisionLab.MachineStudio.ViewModel;

public sealed class RelayCommand : ICommand
{
    private readonly Action<object?> _execute;
    private readonly Predicate<object?>? _canExecute;
    private readonly bool _useCommandManagerRequery;
    private event EventHandler? LocalCanExecuteChanged;

    public RelayCommand(
        Action<object?> execute,
        Predicate<object?>? canExecute = null,
        bool useCommandManagerRequery = true)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
        _useCommandManagerRequery = useCommandManagerRequery;
    }

    public event EventHandler? CanExecuteChanged
    {
        add
        {
            LocalCanExecuteChanged += value;
            if (_useCommandManagerRequery)
            {
                CommandManager.RequerySuggested += value;
            }
        }
        remove
        {
            LocalCanExecuteChanged -= value;
            if (_useCommandManagerRequery)
            {
                CommandManager.RequerySuggested -= value;
            }
        }
    }

    public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;

    public void Execute(object? parameter) => _execute(parameter);

    public void RaiseCanExecuteChanged() =>
        LocalCanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
