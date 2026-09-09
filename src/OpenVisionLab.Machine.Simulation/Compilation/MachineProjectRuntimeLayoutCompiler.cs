using OpenVisionLab.Machine.Core.Axes;
using OpenVisionLab.Machine.Core.Channels;
using OpenVisionLab.Machine.Core.Devices;
using OpenVisionLab.Machine.Core.Layouts;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Simulation.Layout;

namespace OpenVisionLab.Machine.Simulation.Compilation;

internal sealed class MachineProjectRuntimeLayoutCompiler
{
    private readonly MachineLayoutDeviceRuntimeCompiler _layoutDeviceRuntimeCompiler;
    private readonly MachineProjectRuntimeComponentCompiler _componentCompiler;

    internal MachineProjectRuntimeLayoutCompiler(FixedStepDelayConverter delayConverter)
    {
        ArgumentNullException.ThrowIfNull(delayConverter);
        _layoutDeviceRuntimeCompiler = new MachineLayoutDeviceRuntimeCompiler(delayConverter);
        _componentCompiler = new MachineProjectRuntimeComponentCompiler(delayConverter);
    }

    internal MachineLayoutRuntimeConfiguration? Compile(
        MachineProjectDocument project,
        IReadOnlyList<MachineLayoutDefinition> layouts,
        IReadOnlyDictionary<string, ChannelKind>? channelKinds,
        ICollection<MachineProjectRuntimeCompilationError> errors)
    {
        MachineProjectLayoutValidationResult validation =
            new MachineProjectLayoutValidator().Validate(project);
        foreach (MachineProjectLayoutValidationError error in validation.Errors)
        {
            errors.Add(Error(
                MachineProjectRuntimeCompilationErrorCode.LayoutValidationFailed,
                error.ComponentId ?? error.LayoutId,
                $"{error.Code}: {error.Message}"));
        }

        if (!validation.IsValid)
        {
            return null;
        }

        string? activeLayoutId = project.Simulation.ActiveLayoutId;
        if (layouts.Count == 0)
        {
            if (activeLayoutId is not null && !string.IsNullOrWhiteSpace(activeLayoutId))
            {
                errors.Add(Error(
                    MachineProjectRuntimeCompilationErrorCode.ActiveLayoutNotFound,
                    "simulation.activeLayoutId",
                    $"Active layout '{activeLayoutId}' was not found because the project has no layouts."));
            }
            else if (activeLayoutId is not null)
            {
                errors.Add(Error(
                    MachineProjectRuntimeCompilationErrorCode.ActiveLayoutIdInvalid,
                    "simulation.activeLayoutId",
                    "Active layout id cannot be blank."));
            }

            return null;
        }

        MachineLayoutDefinition? activeLayout;
        if (activeLayoutId is null)
        {
            if (layouts.Count != 1)
            {
                errors.Add(Error(
                    MachineProjectRuntimeCompilationErrorCode.ActiveLayoutRequired,
                    "simulation.activeLayoutId",
                    "simulation.activeLayoutId is required when a project contains more than one layout."));
                return null;
            }

            activeLayout = layouts[0];
        }
        else if (string.IsNullOrWhiteSpace(activeLayoutId) ||
                 !string.Equals(activeLayoutId, activeLayoutId.Trim(), StringComparison.Ordinal))
        {
            errors.Add(Error(
                MachineProjectRuntimeCompilationErrorCode.ActiveLayoutIdInvalid,
                "simulation.activeLayoutId",
                "Active layout id cannot be blank or contain leading/trailing whitespace."));
            return null;
        }
        else
        {
            activeLayout = layouts.FirstOrDefault(layout =>
                string.Equals(layout.Id, activeLayoutId, StringComparison.Ordinal));
            if (activeLayout is null)
            {
                errors.Add(Error(
                    MachineProjectRuntimeCompilationErrorCode.ActiveLayoutNotFound,
                    "simulation.activeLayoutId",
                    $"Active layout '{activeLayoutId}' was not found."));
                return null;
            }
        }

        var axesById = project.Axes
            .GroupBy(axis => axis.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var devicesById = project.Devices
            .GroupBy(device => device.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        IReadOnlyList<LayoutComponentRuntimeConfiguration> runtimeComponents = _componentCompiler.Compile(
            activeLayout,
            axesById,
            devicesById,
            errors);

        if (errors.Any(error => error.Code is
                MachineProjectRuntimeCompilationErrorCode.LayoutTargetOutsideActiveLayout or
                MachineProjectRuntimeCompilationErrorCode.SensorDelayInvalid or
                MachineProjectRuntimeCompilationErrorCode.CylinderTimingInvalid))
        {
            return null;
        }

        return _layoutDeviceRuntimeCompiler.Compile(
            layoutId: activeLayout.Id,
            layoutName: activeLayout.Name,
            devices: project.Devices,
            runtimeComponents,
            axesById,
            channelKinds,
            errors);
    }

    private static MachineProjectRuntimeCompilationError Error(
        MachineProjectRuntimeCompilationErrorCode code,
        string? targetId,
        string message) =>
        new(code, targetId, message);
}
