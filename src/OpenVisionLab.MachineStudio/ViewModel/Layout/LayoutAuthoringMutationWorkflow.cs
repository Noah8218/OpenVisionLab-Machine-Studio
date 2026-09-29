using OpenVisionLab.Machine.Core.Layouts;
using OpenVisionLab.Machine.Core.Projects;

namespace OpenVisionLab.MachineStudio.ViewModel;

/// <summary>
/// Coordinates add/remove layout mutations around the Core authoring service.
/// History, project mutation, Scene event forwarding, and WPF remain owned by
/// their existing owners; this class maps typed mutation results to shell hooks.
/// </summary>
internal sealed class LayoutAuthoringMutationWorkflow
{
    private readonly MachineLayoutViewModel _layout;
    private readonly LayoutComponentAuthoringService _authoringService;
    private readonly LayoutAuthoringHistoryViewModel _history;
    private readonly Func<MachineProjectDocument> _projectAccessor;
    private readonly Func<string?> _selectedUnitIdProvider;
    private readonly Func<bool> _isSceneEditable;
    private readonly Func<bool> _isApplyingProject;
    private readonly Action _markProjectChanged;
    private readonly Action _updateRunToolAvailability;
    private readonly Action<string?> _refreshDefinitionPresentation;
    private readonly Action<string> _setStatusMessage;
    private readonly Action<string, string> _log;
    private readonly Func<IReadOnlyList<LayoutComponentDefinition>, IReadOnlyList<LayoutComponentRemovalImpact>, bool> _confirmRemoval;
    private bool _isConfirmingRemoval;

    internal LayoutAuthoringMutationWorkflow(
        MachineLayoutViewModel layout,
        LayoutComponentAuthoringService authoringService,
        LayoutAuthoringHistoryViewModel history,
        Func<MachineProjectDocument> projectAccessor,
        Func<string?> selectedUnitIdProvider,
        Func<bool> isSceneEditable,
        Func<bool> isApplyingProject,
        Action markProjectChanged,
        Action updateRunToolAvailability,
        Action<string?> refreshDefinitionPresentation,
        Action<string> setStatusMessage,
        Action<string, string> log,
        Func<IReadOnlyList<LayoutComponentDefinition>, IReadOnlyList<LayoutComponentRemovalImpact>, bool> confirmRemoval)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(authoringService);
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(projectAccessor);
        ArgumentNullException.ThrowIfNull(selectedUnitIdProvider);
        ArgumentNullException.ThrowIfNull(isSceneEditable);
        ArgumentNullException.ThrowIfNull(isApplyingProject);
        ArgumentNullException.ThrowIfNull(markProjectChanged);
        ArgumentNullException.ThrowIfNull(updateRunToolAvailability);
        ArgumentNullException.ThrowIfNull(refreshDefinitionPresentation);
        ArgumentNullException.ThrowIfNull(setStatusMessage);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(confirmRemoval);

        _layout = layout;
        _authoringService = authoringService;
        _history = history;
        _projectAccessor = projectAccessor;
        _selectedUnitIdProvider = selectedUnitIdProvider;
        _isSceneEditable = isSceneEditable;
        _isApplyingProject = isApplyingProject;
        _markProjectChanged = markProjectChanged;
        _updateRunToolAvailability = updateRunToolAvailability;
        _refreshDefinitionPresentation = refreshDefinitionPresentation;
        _setStatusMessage = setStatusMessage;
        _log = log;
        _confirmRemoval = confirmRemoval;
    }

    internal bool TryAdd(LayoutComponentKind kind, double? worldX = null, double? worldY = null)
    {
        if (!_isSceneEditable()
            || _isApplyingProject()
            || worldX.HasValue != worldY.HasValue
            || worldX is { } x && !double.IsFinite(x)
            || worldY is { } y && !double.IsFinite(y))
        {
            return false;
        }

        var before = _history.CaptureCurrentState();
        var result = _authoringService.TryAdd(
            _projectAccessor(),
            kind,
            _layout.SelectedItem?.Id,
            worldX,
            worldY,
            _selectedUnitIdProvider());
        if (!result.IsSuccess)
        {
            HandleAddFailure(result.Failure);
            return false;
        }

        var component = result.Component!;
        _markProjectChanged();
        _updateRunToolAvailability();
        _refreshDefinitionPresentation(component.Id);
        _history.Commit(before);
        _setStatusMessage($"Added {component.Name}");
        _log("Layout", $"Added {component.Kind} '{component.Id}'");
        return true;
    }

    internal bool TryRemoveSelected()
    {
        if (!_isSceneEditable() || _isApplyingProject() || _isConfirmingRemoval)
        {
            return false;
        }

        var components = _layout.SelectedItems.Select(item => item.Component).OfType<LayoutComponentDefinition>().ToArray();
        var layout = _layout.Definition;
        if (components.Length == 0 || components.Length != _layout.SelectionCount || layout is null)
        {
            return false;
        }

        var project = _projectAccessor();
        var ids = components.Select(component => component.Id).ToArray();
        var impacts = _authoringService.GetRemovalImpacts(project, ids);
        _isConfirmingRemoval = true;
        try
        {
            if (!_confirmRemoval(components, impacts))
            {
                return false;
            }
        }
        finally
        {
            _isConfirmingRemoval = false;
        }

        if (!_isSceneEditable() || _isApplyingProject() || !ReferenceEquals(_projectAccessor(), project)
            || !ReferenceEquals(_layout.Definition, layout)
            || !_layout.SelectedItems.Select(item => item.Id).ToHashSet(StringComparer.Ordinal).SetEquals(ids))
        {
            return false;
        }

        var before = _history.CaptureCurrentState();
        var result = _authoringService.TryRemove(project, layout, ids);
        if (!result.IsSuccess)
        {
            switch (result.Failure)
            {
                case LayoutComponentRemovalFailureKind.SensorDependency when result.BlockingComponent is not null:
                    _setStatusMessage(
                        $"Remove sensor '{result.BlockingComponent.Name}' before removing {components[0].Name}");
                    break;
                case LayoutComponentRemovalFailureKind.WorkpieceDependency when result.BlockingComponent is not null:
                    _setStatusMessage(
                        $"Remove workpiece '{result.BlockingComponent.Name}' before removing {components[0].Name}");
                    break;
            }

            return false;
        }

        _markProjectChanged();
        _updateRunToolAvailability();
        _refreshDefinitionPresentation(null);
        _history.Commit(before);
        _setStatusMessage(result.RemovedComponents.Count == 1
            ? result.RemovedComponents[0].Kind is LayoutComponentKind.DigitalSensor or LayoutComponentKind.PneumaticCylinder
                ? $"Removed {result.RemovedComponents[0].Name}; its device and channel definitions were retained"
                : $"Removed {result.RemovedComponents[0].Name}"
            : $"Removed {result.RemovedComponents.Count} components; device and sequence definitions were retained");
        _log("Layout", $"Removed {string.Join(", ", ids)} without cascading into project definitions");
        return true;
    }

    private void HandleAddFailure(LayoutComponentAuthoringFailure? failure)
    {
        switch (failure)
        {
            case
            {
                Kind: LayoutComponentAuthoringFailureKind.ActiveLayoutNotFound,
                ActiveLayoutId: { } activeLayoutId
            }:
                _setStatusMessage(OpenVisionLanguageService.T("Layout.Add.ActiveLayoutNotFoundFormat")
                    .Replace("{0}", activeLayoutId, StringComparison.Ordinal));
                _log("Layout", "Select a valid active layout before adding components");
                break;
            case { Kind: LayoutComponentAuthoringFailureKind.ActiveLayoutRequired }:
                _setStatusMessage(OpenVisionLanguageService.T("Layout.Add.ActiveLayoutRequired"));
                _log("Layout", "simulation.activeLayoutId is required for projects with multiple layouts");
                break;
            case { Kind: LayoutComponentAuthoringFailureKind.SensorTargetRequired }:
                _setStatusMessage(OpenVisionLanguageService.T("Layout.Add.SensorTargetRequired"));
                _log("Layout", "Digital Sensor requires a Workpiece or Stage target");
                break;
            case { Kind: LayoutComponentAuthoringFailureKind.WorkpieceCarrierRequired }:
                _setStatusMessage(OpenVisionLanguageService.T("Layout.Add.WorkpieceCarrierRequired"));
                _log("Layout", "Workpiece requires an explicit Conveyor carrier");
                break;
            case { Kind: LayoutComponentAuthoringFailureKind.CameraDeviceRequired }:
                _setStatusMessage(OpenVisionLanguageService.T("Layout.Add.CameraDeviceRequired"));
                _log("Layout", "Camera placement requires an unplaced virtual-camera device");
                break;
            case
            {
                Kind: LayoutComponentAuthoringFailureKind.InvalidDefinition,
                ValidationError: { } error
            }:
                _setStatusMessage(OpenVisionLanguageService.T("Layout.Add.InvalidDefinition"));
                _log("Layout", $"Add rejected · {error.Code}: {error.Message}");
                _refreshDefinitionPresentation(null);
                break;
        }
    }
}
