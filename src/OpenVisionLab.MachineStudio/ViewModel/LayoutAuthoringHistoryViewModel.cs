using System.ComponentModel;
using System.Windows.Input;
using OpenVisionLab.Machine.Core.Layouts;
using OpenVisionLab.Machine.Core.Projects;

namespace OpenVisionLab.MachineStudio.ViewModel;

/// <summary>
/// Owns the transactional state around layout authoring history and clipboard
/// commands. The shell supplies cross-workflow presentation and runtime hooks.
/// </summary>
internal sealed class LayoutAuthoringHistoryViewModel : IDisposable
{
    private readonly MachineLayoutViewModel _layout;
    private readonly Func<MachineProjectDocument> _projectProvider;
    private readonly Func<bool> _isEditable;
    private readonly Func<bool> _isApplyingProject;
    private readonly Action _markProjectChanged;
    private readonly Action _updateRunToolAvailability;
    private readonly Action<string?> _refreshDefinitionPresentation;
    private readonly Action _notifyHostCommandsChanged;
    private readonly Action<string> _setStatusMessage;
    private readonly Action<string, string> _log;
    private readonly Action _onDefinitionChanged;
    private readonly LayoutEditHistory _history = new();
    private readonly LayoutComponentClipboard _clipboard = new();
    private LayoutAuthoringState? _currentState;
    private bool _isRestoring;
    private bool _disposed;
    private ICommand? _undoCommand;
    private ICommand? _redoCommand;
    private ICommand? _copyCommand;
    private ICommand? _duplicateCommand;
    private ICommand? _pasteCommand;

    public LayoutAuthoringHistoryViewModel(
        MachineLayoutViewModel layout,
        Func<MachineProjectDocument> projectProvider,
        Func<bool> isEditable,
        Func<bool> isApplyingProject,
        Action markProjectChanged,
        Action updateRunToolAvailability,
        Action<string?> refreshDefinitionPresentation,
        Action notifyHostCommandsChanged,
        Action<string> setStatusMessage,
        Action<string, string> log,
        Action onDefinitionChanged)
    {
        _layout = layout ?? throw new ArgumentNullException(nameof(layout));
        _projectProvider = projectProvider ?? throw new ArgumentNullException(nameof(projectProvider));
        _isEditable = isEditable ?? throw new ArgumentNullException(nameof(isEditable));
        _isApplyingProject = isApplyingProject ?? throw new ArgumentNullException(nameof(isApplyingProject));
        _markProjectChanged = markProjectChanged ?? throw new ArgumentNullException(nameof(markProjectChanged));
        _updateRunToolAvailability = updateRunToolAvailability ?? throw new ArgumentNullException(nameof(updateRunToolAvailability));
        _refreshDefinitionPresentation = refreshDefinitionPresentation ?? throw new ArgumentNullException(nameof(refreshDefinitionPresentation));
        _notifyHostCommandsChanged = notifyHostCommandsChanged ?? throw new ArgumentNullException(nameof(notifyHostCommandsChanged));
        _setStatusMessage = setStatusMessage ?? throw new ArgumentNullException(nameof(setStatusMessage));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _onDefinitionChanged = onDefinitionChanged ?? throw new ArgumentNullException(nameof(onDefinitionChanged));

        _layout.PropertyChanged += OnLayoutPropertyChanged;
        _layout.DefinitionChanged += OnLayoutDefinitionChanged;
    }

    public ICommand UndoCommand => _undoCommand ??= new RelayCommand(
        _ => Undo(),
        _ => CanEdit && _history.CanUndo,
        useCommandManagerRequery: false);

    public ICommand RedoCommand => _redoCommand ??= new RelayCommand(
        _ => Redo(),
        _ => CanEdit && _history.CanRedo,
        useCommandManagerRequery: false);

    public ICommand CopyCommand => _copyCommand ??= new RelayCommand(
        _ => CopySelection(),
        _ => CanEdit && _layout.HasSelection && _layout.Definition is not null,
        useCommandManagerRequery: false);

    public ICommand DuplicateCommand => _duplicateCommand ??= new RelayCommand(
        _ => DuplicateSelection(),
        _ => CanEdit && _layout.HasSelection && _layout.Definition is not null,
        useCommandManagerRequery: false);

    public ICommand PasteCommand => _pasteCommand ??= new RelayCommand(
        _ => PasteSelection(),
        _ => CanEdit && _clipboard.HasContent && _layout.Definition is not null,
        useCommandManagerRequery: false);

    private bool CanEdit => _isEditable() && !_isApplyingProject();

    public LayoutAuthoringState CaptureCurrentState() =>
        _currentState ??= CaptureState();

    public void Reset()
    {
        _history.Clear();
        _clipboard.Clear();
        _currentState = CaptureState();
    }

    public void Commit(LayoutAuthoringState before)
    {
        ArgumentNullException.ThrowIfNull(before);
        var after = CaptureState();
        _history.Record(before, after);
        _currentState = after;
        _notifyHostCommandsChanged();
    }

    public void InvalidateCommands()
    {
        RaiseCanExecuteChanged(_undoCommand);
        RaiseCanExecuteChanged(_redoCommand);
        RaiseCanExecuteChanged(_copyCommand);
        RaiseCanExecuteChanged(_duplicateCommand);
        RaiseCanExecuteChanged(_pasteCommand);
    }

    private void OnLayoutPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is not nameof(MachineLayoutViewModel.SelectedItem) and
            not nameof(MachineLayoutViewModel.SelectionCount))
        {
            return;
        }

        if (_currentState is not null && !_isRestoring)
        {
            _currentState = _currentState with
            {
                SelectedComponentIds = _layout.SelectedItems.Select(item => item.Id).ToArray(),
                PrimaryComponentId = _layout.SelectedItem?.Id
            };
        }
    }

    private void OnLayoutDefinitionChanged(object? sender, EventArgs args)
    {
        if (!_isRestoring)
        {
            CommitWithoutNotification(CaptureCurrentState());
        }

        _markProjectChanged();
        _updateRunToolAvailability();
        _onDefinitionChanged();
        _notifyHostCommandsChanged();
    }

    private void CommitWithoutNotification(LayoutAuthoringState before)
    {
        var after = CaptureState();
        _history.Record(before, after);
        _currentState = after;
    }

    private void Undo()
    {
        if (_history.TryUndo(out var state) && state is not null)
        {
            Restore(state, "Undid layout edit");
        }
    }

    private void Redo()
    {
        if (_history.TryRedo(out var state) && state is not null)
        {
            Restore(state, "Redid layout edit");
        }
    }

    private void CopySelection()
    {
        var definition = _layout.Definition;
        var componentIds = _layout.SelectedItems
            .Where(item => item.Component is not null)
            .Select(item => item.Id)
            .ToArray();
        if (definition is null || componentIds.Length == 0)
        {
            return;
        }

        var copiedCount = _clipboard.Copy(_projectProvider(), definition, componentIds);
        _setStatusMessage($"Copied {copiedCount} layout component(s)");
        _notifyHostCommandsChanged();
    }

    private void DuplicateSelection()
    {
        CopySelection();
        PasteSelection();
    }

    private void PasteSelection()
    {
        if (!_clipboard.HasContent || _layout.Definition is not { } targetLayout)
        {
            return;
        }

        var before = CaptureCurrentState();
        var project = _projectProvider();
        var result = _clipboard.Paste(project, targetLayout);
        if (!result.IsSuccess)
        {
            _history.Restore(project, before);
            _refreshDefinitionPresentation(null);
            _layout.SelectMany(before.SelectedComponentIds, before.PrimaryComponentId);
            _currentState = before;
            _setStatusMessage("Copied components were not pasted because their definitions were invalid");
            if (result.Error is { } error)
            {
                _log("Layout", $"Paste rejected · {error.Code}: {error.Message}");
            }
            return;
        }

        _markProjectChanged();
        _updateRunToolAvailability();
        _refreshDefinitionPresentation(null);
        _layout.SelectMany(result.ComponentIds, result.ComponentIds[^1]);
        Commit(before);
        _setStatusMessage($"Pasted {result.ComponentIds.Count} layout component(s)");
        _log("Layout", $"Pasted {result.ComponentIds.Count} component(s) with cloned behavior bindings");
    }

    private void Restore(LayoutAuthoringState state, string status)
    {
        _isRestoring = true;
        try
        {
            var project = _projectProvider();
            _history.Restore(project, state);
            _refreshDefinitionPresentation(null);
            _layout.SelectMany(state.SelectedComponentIds, state.PrimaryComponentId);
            _currentState = state;
            _markProjectChanged();
            _updateRunToolAvailability();
            _setStatusMessage(status);
            _log("Layout", status);
        }
        finally
        {
            _isRestoring = false;
            _notifyHostCommandsChanged();
        }
    }

    private LayoutAuthoringState CaptureState() =>
        _history.Capture(
            _projectProvider(),
            _layout.SelectedItems.Select(item => item.Id),
            _layout.SelectedItem?.Id);

    private static void RaiseCanExecuteChanged(ICommand? command)
    {
        if (command is RelayCommand relayCommand)
        {
            relayCommand.RaiseCanExecuteChanged();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _layout.PropertyChanged -= OnLayoutPropertyChanged;
        _layout.DefinitionChanged -= OnLayoutDefinitionChanged;
    }
}
