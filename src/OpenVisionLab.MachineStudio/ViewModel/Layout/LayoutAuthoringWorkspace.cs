using System.Windows.Input;
using OpenVisionLab.Machine.Core.Layouts;
using OpenVisionLab.Machine.Core.Projects;

namespace OpenVisionLab.MachineStudio.ViewModel;

/// <summary>
/// Owns layout editing commands, history, clipboard, and scene request routing.
/// MachineLayoutViewModel owns the selected items and gestures; the shell supplies
/// project/session facts and refreshes the other workspaces after an edit.
/// </summary>
public sealed class LayoutAuthoringWorkspace : IDisposable
{
    private readonly MachineLayoutViewModel _layout;
    private readonly Func<bool> _isSceneEditable;
    private readonly Func<bool> _isApplyingProject;
    private readonly Func<bool> _canExecuteSessionCommand;
    private readonly LayoutAuthoringHistoryViewModel _history;
    private readonly LayoutAuthoringMutationWorkflow _mutations;
    private readonly LayoutSelectionCommandWorkflow _selectionCommands;
    private readonly SceneViewportInteractionWorkflow _sceneInteraction;
    private RelayCommand? _addLayoutComponentCommand;
    private RelayCommand? _deleteLayoutComponentCommand;
    private RelayCommand? _sceneSelectionRequestedCommand;
    private RelayCommand? _sceneMoveRequestedCommand;
    private RelayCommand? _sceneMarqueeSelectionRequestedCommand;
    private RelayCommand? _sceneTransformRequestedCommand;
    private RelayCommand? _sceneLibraryComponentDropRequestedCommand;
    private RelayCommand? _nudgeLayoutComponentCommand;
    private RelayCommand? _alignLayoutSelectionCommand;
    private RelayCommand? _changeLayoutLayerOrderCommand;

    internal LayoutAuthoringWorkspace(
        MachineLayoutViewModel layout,
        Func<MachineProjectDocument> projectProvider,
        Func<bool> isSceneEditable,
        Func<bool> isApplyingProject,
        Func<bool> canExecuteSessionCommand,
        Action markProjectChanged,
        Action updateRunToolAvailability,
        Action<string?> refreshDefinitionPresentation,
        Action notifyHostCommandsChanged,
        Action<string> setStatusMessage,
        Action<string, string> log,
        Action onDefinitionChanged)
    {
        _layout = layout ?? throw new ArgumentNullException(nameof(layout));
        _isSceneEditable = isSceneEditable ?? throw new ArgumentNullException(nameof(isSceneEditable));
        _isApplyingProject = isApplyingProject ?? throw new ArgumentNullException(nameof(isApplyingProject));
        _canExecuteSessionCommand = canExecuteSessionCommand ?? throw new ArgumentNullException(nameof(canExecuteSessionCommand));
        _history = new LayoutAuthoringHistoryViewModel(
            layout,
            projectProvider,
            isSceneEditable,
            isApplyingProject,
            markProjectChanged,
            updateRunToolAvailability,
            refreshDefinitionPresentation,
            notifyHostCommandsChanged,
            setStatusMessage,
            log,
            onDefinitionChanged);
        _mutations = new LayoutAuthoringMutationWorkflow(
            layout,
            new LayoutComponentAuthoringService(),
            _history,
            projectProvider,
            isSceneEditable,
            isApplyingProject,
            markProjectChanged,
            updateRunToolAvailability,
            refreshDefinitionPresentation,
            setStatusMessage,
            log);
        _selectionCommands = new LayoutSelectionCommandWorkflow(layout, setStatusMessage);
        _sceneInteraction = new SceneViewportInteractionWorkflow(layout, _mutations.TryAdd);
    }

    public ICommand AddLayoutComponentCommand => _addLayoutComponentCommand ??=
        CreateCommand(AddLayoutComponent, _ => CanEdit);

    public ICommand DeleteLayoutComponentCommand => _deleteLayoutComponentCommand ??=
        CreateCommand(
            _ => _mutations.TryRemoveSelected(),
            _ => CanEdit && _layout.SelectionCount == 1 && _layout.SelectedItem?.Component is not null);

    public ICommand SceneSelectionRequestedCommand => _sceneSelectionRequestedCommand ??=
        CreateCommand(_sceneInteraction.HandleSelection);

    public ICommand SceneMoveRequestedCommand => _sceneMoveRequestedCommand ??=
        CreateCommand(_sceneInteraction.HandleMove);

    public ICommand SceneMarqueeSelectionRequestedCommand => _sceneMarqueeSelectionRequestedCommand ??=
        CreateCommand(_sceneInteraction.HandleMarqueeSelection);

    public ICommand SceneTransformRequestedCommand => _sceneTransformRequestedCommand ??=
        CreateCommand(_sceneInteraction.HandleTransform);

    public ICommand SceneLibraryComponentDropRequestedCommand => _sceneLibraryComponentDropRequestedCommand ??=
        CreateCommand(_sceneInteraction.HandleLibraryComponentDrop);

    public ICommand NudgeLayoutComponentCommand => _nudgeLayoutComponentCommand ??=
        CreateCommand(_selectionCommands.Nudge, _ => CanEdit && _layout.SelectedItem?.Component is not null);

    public ICommand AlignLayoutSelectionCommand => _alignLayoutSelectionCommand ??=
        CreateCommand(_selectionCommands.Align, _ => CanEdit && _layout.HasMultipleSelection);

    public ICommand ChangeLayoutLayerOrderCommand => _changeLayoutLayerOrderCommand ??=
        CreateCommand(
            _selectionCommands.ChangeLayerOrder,
            parameter => CanEdit &&
                parameter is string value &&
                Enum.TryParse(value, out LayoutLayerOrder order) &&
                _layout.CanChangeSelectionLayerOrder(order));

    // History retains its existing editable/project-application admission policy.
    public ICommand UndoLayoutEditCommand => _history.UndoCommand;
    public ICommand RedoLayoutEditCommand => _history.RedoCommand;
    public ICommand CopyLayoutSelectionCommand => _history.CopyCommand;
    public ICommand DuplicateLayoutSelectionCommand => _history.DuplicateCommand;
    public ICommand PasteLayoutSelectionCommand => _history.PasteCommand;

    private bool CanEdit => _isSceneEditable() && !_isApplyingProject();

    public bool TryAddComponent(LayoutComponentKind kind, double? worldX = null, double? worldY = null) =>
        _mutations.TryAdd(kind, worldX, worldY);

    public void Reset()
    {
        _history.Reset();
        InvalidateCommands();
    }

    public void InvalidateCommands()
    {
        _addLayoutComponentCommand?.RaiseCanExecuteChanged();
        _deleteLayoutComponentCommand?.RaiseCanExecuteChanged();
        _nudgeLayoutComponentCommand?.RaiseCanExecuteChanged();
        _alignLayoutSelectionCommand?.RaiseCanExecuteChanged();
        _changeLayoutLayerOrderCommand?.RaiseCanExecuteChanged();
        _history.InvalidateCommands();
    }

    private void AddLayoutComponent(object? parameter)
    {
        if (parameter is LayoutComponentKind kind)
        {
            TryAddComponent(kind);
        }
    }

    private RelayCommand CreateCommand(Action<object?> execute, Predicate<object?>? canExecute = null) => new(
        execute,
        parameter => _canExecuteSessionCommand() && (canExecute?.Invoke(parameter) ?? true),
        useCommandManagerRequery: false);

    public void Dispose() => _history.Dispose();
}
