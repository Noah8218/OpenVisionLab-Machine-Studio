using System.ComponentModel;
using OpenVisionLab.Machine.Core.Axes;
using OpenVisionLab.Machine.Core.Channels;
using OpenVisionLab.Machine.Core.Devices;
using OpenVisionLab.Machine.Core.Layouts;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Core.Sequences;
using OpenVisionLab.Machine.Simulation.Snapshots;
using OpenVisionLab.MachineStudio.Model;

namespace OpenVisionLab.MachineStudio.ViewModel;

/// <summary>
/// Owns the selection contract between the project tree, scene, property
/// panel, authoring editors, and camera/sequence views. The shell supplies
/// runtime and public-property notifications; this type owns the event
/// subscriptions and selection state transitions.
/// </summary>
internal sealed class ProjectSelectionSynchronizationWorkflow : ViewModelBase, IDisposable
{
    private readonly ProjectTreeViewModel _projectTree;
    private readonly MachineLayoutViewModel _layout;
    private readonly PropertiesViewModel _properties;
    private readonly RecipeConnectionWorkbenchViewModel _recipeConnections;
    private readonly SequenceEditorViewModel _sequenceEditor;
    private readonly CameraSelectionWorkflow _cameraSelection;
    private readonly Func<MachineProjectDocument> _getProject;
    private readonly Func<SimulationSnapshot> _getSnapshot;
    private readonly Action<SimulationSnapshot> _updateSelectedAxisProjection;
    private readonly Action<bool> _notifyTreeSelectionChanged;
    private readonly Action _notifyLayoutSelectionChanged;
    private readonly Action _onAxisDefinitionChanged;
    private readonly Func<bool>? _canEditAxisDefinition;
    private readonly Action _onAnalogChannelDefinitionChanged;
    private readonly Action<string> _setStatus;
    private AxisDriveTuningEditorViewModel? _axisDriveTuningEditor;
    private AnalogIoAuthoringViewModel? _analogIoAuthoring;
    private bool _isApplyingProjectTreeGroupSelection;
    private bool _isApplyingLayoutSelection;
    private bool _isApplyingSequenceStepSelection;
    private bool _isClearingProjectTreeGroupSelection;
    private int _disposed;

    internal ProjectSelectionSynchronizationWorkflow(
        ProjectTreeViewModel projectTree,
        MachineLayoutViewModel layout,
        PropertiesViewModel properties,
        RecipeConnectionWorkbenchViewModel recipeConnections,
        SequenceEditorViewModel sequenceEditor,
        CameraSelectionWorkflow cameraSelection,
        Func<MachineProjectDocument> getProject,
        Func<SimulationSnapshot> getSnapshot,
        Action<SimulationSnapshot> updateSelectedAxisProjection,
        Action<bool> notifyTreeSelectionChanged,
        Action notifyLayoutSelectionChanged,
        Action onAxisDefinitionChanged,
        Action onAnalogChannelDefinitionChanged,
        Action<string> setStatus,
        Func<bool>? canEditAxisDefinition = null)
    {
        _projectTree = projectTree ?? throw new ArgumentNullException(nameof(projectTree));
        _layout = layout ?? throw new ArgumentNullException(nameof(layout));
        _properties = properties ?? throw new ArgumentNullException(nameof(properties));
        _recipeConnections = recipeConnections
            ?? throw new ArgumentNullException(nameof(recipeConnections));
        _sequenceEditor = sequenceEditor ?? throw new ArgumentNullException(nameof(sequenceEditor));
        _cameraSelection = cameraSelection ?? throw new ArgumentNullException(nameof(cameraSelection));
        _getProject = getProject ?? throw new ArgumentNullException(nameof(getProject));
        _getSnapshot = getSnapshot ?? throw new ArgumentNullException(nameof(getSnapshot));
        _updateSelectedAxisProjection = updateSelectedAxisProjection
            ?? throw new ArgumentNullException(nameof(updateSelectedAxisProjection));
        _notifyTreeSelectionChanged = notifyTreeSelectionChanged
            ?? throw new ArgumentNullException(nameof(notifyTreeSelectionChanged));
        _notifyLayoutSelectionChanged = notifyLayoutSelectionChanged
            ?? throw new ArgumentNullException(nameof(notifyLayoutSelectionChanged));
        _onAxisDefinitionChanged = onAxisDefinitionChanged
            ?? throw new ArgumentNullException(nameof(onAxisDefinitionChanged));
        _onAnalogChannelDefinitionChanged = onAnalogChannelDefinitionChanged
            ?? throw new ArgumentNullException(nameof(onAnalogChannelDefinitionChanged));
        _setStatus = setStatus ?? throw new ArgumentNullException(nameof(setStatus));
        _canEditAxisDefinition = canEditAxisDefinition;

        _projectTree.PropertyChanged += OnProjectTreePropertyChanged;
        _layout.PropertyChanged += OnLayoutPropertyChanged;
    }

    internal AxisDriveTuningEditorViewModel? AxisDriveTuningEditor => _axisDriveTuningEditor;

    internal AnalogIoAuthoringViewModel? AnalogIoAuthoring => _analogIoAuthoring;

    internal void ClearEditors()
    {
        SetAxisDriveTuningEditor(null);
        SetAnalogIoAuthoring(null);
    }

    internal void ClearSelection()
    {
        if (_projectTree.SelectedNode is { } selectedNode)
        {
            selectedNode.IsSelected = false;
        }

        _projectTree.SelectedNode = null;
        _layout.SelectedItem = null;
    }

    internal LayoutItem? FindStepAxisSettingsTarget()
    {
        var project = _getProject();
        var sequence = _sequenceEditor.SelectedSequence;
        var step = _sequenceEditor.SelectedStep;
        var draft = _sequenceEditor.SelectedStepDraft;
        if (_disposed != 0 || sequence is null || !project.Sequences.Contains(sequence) || step is null
            || sequence.Steps.Count(candidate => candidate.Id == step.Id) != 1 || draft is null || draft.Id != step.Id
            || draft.Action != SequenceStepAction.MoveAxis || string.IsNullOrWhiteSpace(draft.TargetId)
            || project.Axes.Count(axis => axis.Id == draft.TargetId) != 1) return null;
        var layout = _layout.Definition;
        if (layout is null || !project.Layouts.Contains(layout)
            || (!string.IsNullOrWhiteSpace(project.Simulation.ActiveLayoutId) ? layout.Id != project.Simulation.ActiveLayoutId : project.Layouts.Count != 1)) return null;
        var matches = layout.Components.Where(component => component.Kind is LayoutComponentKind.LinearStage or LayoutComponentKind.RotaryStage
            && component.BehaviorBindingId == draft.TargetId).Take(2).ToArray();
        if (matches.Length != 1 || string.IsNullOrWhiteSpace(matches[0].Id) || layout.Components.Count(component => component.Id == matches[0].Id) != 1) return null;
        return _layout.Items.SingleOrDefault(item => ReferenceEquals(item.Component, matches[0]));
    }

    internal void SelectSequenceStepTarget()
    {
        if (_disposed != 0 || _sequenceEditor.SelectedStep is not { } step || string.IsNullOrEmpty(step.TargetId)) return;
        string? signalDeviceId = null;
        if (step.Action is SequenceStepAction.SetSignal or SequenceStepAction.WaitSignal)
        {
            var owners = _getProject().Devices.Where(device => device.ChannelIds.Contains(step.TargetId, StringComparer.Ordinal)).Take(2).ToArray();
            if (owners.Length != 1 || string.IsNullOrEmpty(owners[0].Id)) return;
            signalDeviceId = owners[0].Id;
        }
        var matches = _layout.Items.Where(item => step.Action switch
        {
            SequenceStepAction.MoveAxis or SequenceStepAction.WaitAxisDone =>
                item.Component?.Kind is LayoutComponentKind.LinearStage or LayoutComponentKind.RotaryStage
                    ? string.Equals(item.BehaviorBindingId, step.TargetId, StringComparison.Ordinal)
                    : item.Component is null && item.Kind == LayoutItemKind.Axis && string.Equals(item.Id, step.TargetId, StringComparison.Ordinal),
            SequenceStepAction.TriggerCamera or SequenceStepAction.WaitVisionResult =>
                item.Component?.Kind == LayoutComponentKind.Camera
                    ? string.Equals(item.BehaviorBindingId, step.TargetId, StringComparison.Ordinal)
                    : item.Component is null && item.Kind == LayoutItemKind.Camera && string.Equals(item.Id, step.TargetId, StringComparison.Ordinal),
            SequenceStepAction.SetSignal or SequenceStepAction.WaitSignal =>
                item.Component is not null && string.Equals(item.BehaviorBindingId, signalDeviceId, StringComparison.Ordinal),
            _ => false
        }).Take(2).ToArray();
        if (matches.Length != 1) return;
        _isApplyingSequenceStepSelection = true;
        try
        {
            _layout.Select(matches[0].Id);
        }
        finally
        {
            _isApplyingSequenceStepSelection = false;
        }
    }

    internal void ClearAnalogEditor() => SetAnalogIoAuthoring(null);

    private void OnProjectTreePropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(ProjectTreeViewModel.SelectedNode)
            || _isClearingProjectTreeGroupSelection)
        {
            return;
        }

        var node = _projectTree.SelectedNode;
        if (node?.Kind == TreeNodeKind.Device
            && _getProject().Devices.Any(device =>
                device.Kind == DeviceKind.Camera
                && string.Equals(device.Id, node.Id, StringComparison.Ordinal)))
        {
            _cameraSelection.SelectVirtualCamera(node.Id);
        }

        if (TryGetSelectedUnitIds(node, out var unitIds))
        {
            var unitIdSet = unitIds.ToHashSet(StringComparer.Ordinal);
            var componentIds = node is { Kind: TreeNodeKind.Unit, Model: MachineUnitDefinition unit }
                ? Array.Empty<string>()
                : _layout.Items
                    .Where(item => item.Component?.UnitId is { } unitId && unitIdSet.Contains(unitId))
                    .Select(item => item.Id)
                    .ToArray();
            if (node is { Kind: TreeNodeKind.Unit, Model: MachineUnitDefinition selectedUnit })
            {
                _layout.ShowUnit(selectedUnit.Id);
            }
            else
            {
                _layout.ShowOverview();
            }
            _isApplyingProjectTreeGroupSelection = true;
            try
            {
                _layout.SelectMany(componentIds);
            }
            finally
            {
                _isApplyingProjectTreeGroupSelection = false;
            }
        }
        else if (node?.Kind == TreeNodeKind.LayoutComponent)
        {
            if (node.Model is LayoutComponentDefinition { UnitId: { } unitId })
            {
                _layout.ShowUnit(unitId);
            }
            else
            {
                _layout.ShowOverview();
            }
            if (!_isApplyingLayoutSelection)
            {
                _layout.Select(node.Id);
            }
        }
        else if (_layout.SelectedItem is not null)
        {
            _layout.SelectedItem = null;
        }

        if (node?.Model is SequenceDefinition sequence)
        {
            _sequenceEditor.SelectSequence(sequence.Id);
        }
        else if (node?.Model is SequenceStepDefinition step)
        {
            _sequenceEditor.SelectStep(step.Id);
        }

        _properties.ShowNode(node);
        SetAxisDriveTuningEditor(node?.Model is VirtualAxisDefinition axis
            ? new AxisDriveTuningEditorViewModel(axis, _onAxisDefinitionChanged, _canEditAxisDefinition)
            : null);
        SetAnalogIoAuthoring(node?.Model is ChannelDefinition channel
            && channel.Kind is (ChannelKind.AnalogInput or ChannelKind.AnalogOutput)
            ? new AnalogIoAuthoringViewModel(channel, _onAnalogChannelDefinitionChanged)
            : null);
        _setStatus(node is null ? "Ready" : $"Selected {node.DisplayName}");

        _notifyTreeSelectionChanged(node?.Kind == TreeNodeKind.Axis);
    }

    private void OnLayoutPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is not nameof(MachineLayoutViewModel.SelectedItem) and
            not nameof(MachineLayoutViewModel.SelectionCount) and
            not nameof(MachineLayoutViewModel.SelectedItems))
        {
            return;
        }

        if (!_isApplyingProjectTreeGroupSelection && !_isApplyingSequenceStepSelection && IsProjectTreeGroupSelection(_projectTree.SelectedNode))
        {
            var selectedNode = _projectTree.SelectedNode!;
            _isClearingProjectTreeGroupSelection = true;
            try
            {
                selectedNode.IsSelected = false;
                _projectTree.SelectedNode = null;
            }
            finally
            {
                _isClearingProjectTreeGroupSelection = false;
            }
            _notifyTreeSelectionChanged(false);
        }

        if (!_isApplyingProjectTreeGroupSelection && !_isApplyingSequenceStepSelection)
        {
            var layoutComponentNode = _layout.SelectedItem is { } selectedItem
                ? FindLayoutComponentNode(selectedItem.Id)
                : null;
            if (layoutComponentNode is not null || _projectTree.SelectedNode?.Kind == TreeNodeKind.LayoutComponent)
            {
                _isApplyingLayoutSelection = true;
                try
                {
                    _projectTree.SelectedNode = layoutComponentNode;
                }
                finally
                {
                    _isApplyingLayoutSelection = false;
                }
            }
        }

        _properties.Show(_layout.SelectedItem?.Component);
        if (_layout.SelectedItem is not null)
        {
            SetAxisDriveTuningEditor(null);
        }

        _setStatus(_layout.SelectedItem is null
            ? "Ready"
            : _layout.SelectionCount > 1
                ? $"Selected {_layout.SelectionCount} components; reference {_layout.SelectedItem.Name}"
                : $"Selected {_layout.SelectedItem.Name}");
        _recipeConnections.SynchronizeSelection(_layout.SelectedItem?.Id);
        var snapshot = _getSnapshot();
        if (_layout.SelectedItem?.Component?.Kind is
            LayoutComponentKind.LinearStage or LayoutComponentKind.RotaryStage)
        {
            _updateSelectedAxisProjection(snapshot);
        }

        _notifyLayoutSelectionChanged();
    }

    private bool TryGetSelectedUnitIds(TreeNodeViewModel? node, out IEnumerable<string> unitIds)
    {
        unitIds = node switch
        {
            { Kind: TreeNodeKind.Stations } => _getProject().Stations.SelectMany(station => station.Units).Select(unit => unit.Id),
            { Kind: TreeNodeKind.Station, Model: MachineStationDefinition station } => station.Units.Select(unit => unit.Id),
            { Kind: TreeNodeKind.Unit, Model: MachineUnitDefinition unit } => [unit.Id],
            _ => Array.Empty<string>()
        };
        return node?.Kind is TreeNodeKind.Stations or TreeNodeKind.Station or TreeNodeKind.Unit;
    }

    private TreeNodeViewModel? FindLayoutComponentNode(string componentId) =>
        _projectTree.Roots
            .SelectMany(EnumerateNodes)
            .FirstOrDefault(node => node.Kind == TreeNodeKind.LayoutComponent
                && string.Equals(node.Id, componentId, StringComparison.Ordinal));

    private static IEnumerable<TreeNodeViewModel> EnumerateNodes(TreeNodeViewModel node)
    {
        yield return node;
        foreach (var child in node.Children)
        {
            foreach (var descendant in EnumerateNodes(child))
            {
                yield return descendant;
            }
        }
    }

    private static bool IsProjectTreeGroupSelection(TreeNodeViewModel? node) =>
        node?.Kind is TreeNodeKind.Stations or TreeNodeKind.Station or TreeNodeKind.Unit;

    private void SetAxisDriveTuningEditor(AxisDriveTuningEditorViewModel? value)
    {
        if (ReferenceEquals(_axisDriveTuningEditor, value))
        {
            return;
        }

        _axisDriveTuningEditor?.Dispose();
        _axisDriveTuningEditor = value;
        OnPropertyChanged(nameof(AxisDriveTuningEditor));
    }

    private void SetAnalogIoAuthoring(AnalogIoAuthoringViewModel? value)
    {
        if (ReferenceEquals(_analogIoAuthoring, value))
        {
            return;
        }

        _analogIoAuthoring?.Dispose();
        _analogIoAuthoring = value;
        OnPropertyChanged(nameof(AnalogIoAuthoring));
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        ClearEditors();
        _projectTree.PropertyChanged -= OnProjectTreePropertyChanged;
        _layout.PropertyChanged -= OnLayoutPropertyChanged;
    }
}
