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
    private readonly Func<bool> _isSceneEditable;
    private readonly Func<bool> _isApplyingProject;
    private readonly Action _markProjectChanged;
    private readonly Action _updateRunToolAvailability;
    private readonly Action<string?> _refreshDefinitionPresentation;
    private readonly Action<string> _setStatusMessage;
    private readonly Action<string, string> _log;

    internal LayoutAuthoringMutationWorkflow(
        MachineLayoutViewModel layout,
        LayoutComponentAuthoringService authoringService,
        LayoutAuthoringHistoryViewModel history,
        Func<MachineProjectDocument> projectAccessor,
        Func<bool> isSceneEditable,
        Func<bool> isApplyingProject,
        Action markProjectChanged,
        Action updateRunToolAvailability,
        Action<string?> refreshDefinitionPresentation,
        Action<string> setStatusMessage,
        Action<string, string> log)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(authoringService);
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(projectAccessor);
        ArgumentNullException.ThrowIfNull(isSceneEditable);
        ArgumentNullException.ThrowIfNull(isApplyingProject);
        ArgumentNullException.ThrowIfNull(markProjectChanged);
        ArgumentNullException.ThrowIfNull(updateRunToolAvailability);
        ArgumentNullException.ThrowIfNull(refreshDefinitionPresentation);
        ArgumentNullException.ThrowIfNull(setStatusMessage);
        ArgumentNullException.ThrowIfNull(log);

        _layout = layout;
        _authoringService = authoringService;
        _history = history;
        _projectAccessor = projectAccessor;
        _isSceneEditable = isSceneEditable;
        _isApplyingProject = isApplyingProject;
        _markProjectChanged = markProjectChanged;
        _updateRunToolAvailability = updateRunToolAvailability;
        _refreshDefinitionPresentation = refreshDefinitionPresentation;
        _setStatusMessage = setStatusMessage;
        _log = log;
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
            worldY);
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
        var before = _history.CaptureCurrentState();
        var component = _layout.SelectedItem?.Component;
        var layout = _layout.Definition;
        if (component is null || layout is null)
        {
            return false;
        }

        var result = _authoringService.TryRemove(_projectAccessor(), layout, component.Id);
        if (!result.IsSuccess)
        {
            switch (result.Failure)
            {
                case LayoutComponentRemovalFailureKind.SensorDependency when result.BlockingComponent is not null:
                    _setStatusMessage(
                        $"Remove sensor '{result.BlockingComponent.Name}' before removing {component.Name}");
                    break;
                case LayoutComponentRemovalFailureKind.WorkpieceDependency when result.BlockingComponent is not null:
                    _setStatusMessage(
                        $"Remove workpiece '{result.BlockingComponent.Name}' before removing {component.Name}");
                    break;
            }

            return false;
        }

        var removedComponent = result.RemovedComponent!;
        _markProjectChanged();
        _updateRunToolAvailability();
        _refreshDefinitionPresentation(null);
        _history.Commit(before);
        _setStatusMessage(removedComponent.Kind is LayoutComponentKind.DigitalSensor or LayoutComponentKind.PneumaticCylinder
            ? $"Removed {removedComponent.Name}; its device and channel definitions were retained"
            : $"Removed {removedComponent.Name}");
        _log(
            "Layout",
            $"Removed {removedComponent.Kind} '{removedComponent.Id}' without cascading into project definitions");
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
                _setStatusMessage($"Active layout '{activeLayoutId}' was not found");
                _log("Layout", "Select a valid active layout before adding components");
                break;
            case { Kind: LayoutComponentAuthoringFailureKind.ActiveLayoutRequired }:
                _setStatusMessage("Select an active layout before adding components");
                _log("Layout", "simulation.activeLayoutId is required for projects with multiple layouts");
                break;
            case { Kind: LayoutComponentAuthoringFailureKind.SensorTargetRequired }:
                _setStatusMessage("Add a Workpiece or Stage before adding a Digital Sensor");
                _log("Layout", "Digital Sensor requires a Workpiece or Stage target");
                break;
            case { Kind: LayoutComponentAuthoringFailureKind.WorkpieceCarrierRequired }:
                _setStatusMessage("Add a Conveyor before adding a Workpiece");
                _log("Layout", "Workpiece requires an explicit Conveyor carrier");
                break;
            case
            {
                Kind: LayoutComponentAuthoringFailureKind.InvalidDefinition,
                ValidationError: { } error
            }:
                _setStatusMessage("Component was not added because its definition is invalid");
                _log("Layout", $"Add rejected · {error.Code}: {error.Message}");
                _refreshDefinitionPresentation(null);
                break;
        }
    }
}
