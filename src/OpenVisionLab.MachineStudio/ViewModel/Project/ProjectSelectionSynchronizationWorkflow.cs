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
    private readonly Action _onAnalogChannelDefinitionChanged;
    private readonly Action<string> _setStatus;
    private AxisDriveTuningEditorViewModel? _axisDriveTuningEditor;
    private AnalogIoAuthoringViewModel? _analogIoAuthoring;
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
        Action<string> setStatus)
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

    internal void ClearAnalogEditor() => SetAnalogIoAuthoring(null);

    private void OnProjectTreePropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(ProjectTreeViewModel.SelectedNode))
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

        if (node?.Kind == TreeNodeKind.LayoutComponent)
        {
            _layout.Select(node.Id);
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
            ? new AxisDriveTuningEditorViewModel(axis, _onAxisDefinitionChanged)
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
            not nameof(MachineLayoutViewModel.SelectionCount))
        {
            return;
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

    private void SetAxisDriveTuningEditor(AxisDriveTuningEditorViewModel? value)
    {
        if (ReferenceEquals(_axisDriveTuningEditor, value))
        {
            return;
        }

        _axisDriveTuningEditor = value;
        OnPropertyChanged(nameof(AxisDriveTuningEditor));
    }

    private void SetAnalogIoAuthoring(AnalogIoAuthoringViewModel? value)
    {
        if (ReferenceEquals(_analogIoAuthoring, value))
        {
            return;
        }

        _analogIoAuthoring = value;
        OnPropertyChanged(nameof(AnalogIoAuthoring));
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _projectTree.PropertyChanged -= OnProjectTreePropertyChanged;
        _layout.PropertyChanged -= OnLayoutPropertyChanged;
    }
}
