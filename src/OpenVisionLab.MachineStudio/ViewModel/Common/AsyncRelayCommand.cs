using System.Diagnostics;
using System.Windows.Input;

namespace OpenVisionLab.MachineStudio.ViewModel;

public sealed class AsyncRelayCommand : ICommand
{
    private readonly Func<object?, Task> _execute;
    private readonly Predicate<object?>? _canExecute;
    private readonly Action<Exception>? _onException;
    private readonly bool _useCommandManagerRequery;
    private int _isExecuting;
    private event EventHandler? LocalCanExecuteChanged;

    public AsyncRelayCommand(
        Func<object?, Task> execute,
        Predicate<object?>? canExecute = null,
        Action<Exception>? onException = null,
        bool useCommandManagerRequery = true)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
        _onException = onException;
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

    public bool CanExecute(object? parameter) =>
        Volatile.Read(ref _isExecuting) == 0
        && (_canExecute?.Invoke(parameter) ?? true);

    public void RaiseCanExecuteChanged() =>
        LocalCanExecuteChanged?.Invoke(this, EventArgs.Empty);

    public async void Execute(object? parameter)
    {
        if (Interlocked.CompareExchange(ref _isExecuting, 1, 0) != 0)
        {
            return;
        }

        try
        {
            if (!(_canExecute?.Invoke(parameter) ?? true))
            {
                return;
            }

            InvalidateCanExecute();
            await _execute(parameter);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            if (_onException is null)
            {
                Trace.TraceError(exception.ToString());
            }
            else
            {
                _onException(exception);
            }
        }
        finally
        {
            Volatile.Write(ref _isExecuting, 0);
            InvalidateCanExecute();
        }
    }

    private void InvalidateCanExecute()
    {
        if (_useCommandManagerRequery)
        {
            CommandManager.InvalidateRequerySuggested();
        }
        else
        {
            RaiseCanExecuteChanged();
        }
    }
}
