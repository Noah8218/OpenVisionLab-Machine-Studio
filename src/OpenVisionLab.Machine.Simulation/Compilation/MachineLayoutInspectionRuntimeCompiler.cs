using OpenVisionLab.Machine.Core.Channels;
using OpenVisionLab.Machine.Core.Devices;
using OpenVisionLab.Machine.Simulation.Layout;

namespace OpenVisionLab.Machine.Simulation.Compilation;

/// <summary>
/// Compiles inspection sorting and inspection handoff device definitions into
/// immutable runtime configurations.
/// </summary>
internal sealed class MachineLayoutInspectionRuntimeCompiler
{
    internal IReadOnlyList<InspectionSortRouterRuntimeConfiguration> BuildSortRouters(
        IEnumerable<DeviceDefinition> devices,
        IReadOnlyCollection<LayoutComponentRuntimeConfiguration> runtimeComponents,
        IReadOnlyDictionary<string, ChannelKind>? channelKinds,
        ICollection<MachineProjectRuntimeCompilationError> errors)
    {
        DeviceDefinition[] deviceArray = devices.ToArray();
        var cameraIds = deviceArray
            .Where(device => device is { Kind: DeviceKind.Camera, Camera: not null })
            .Select(device => device.Id)
            .ToHashSet(StringComparer.Ordinal);
        var conveyorsById = runtimeComponents
            .OfType<ConveyorRuntimeConfiguration>()
            .ToDictionary(conveyor => conveyor.Id, StringComparer.Ordinal);
        var sorters = new List<InspectionSortRouterRuntimeConfiguration>();

        foreach (DeviceDefinition device in deviceArray
                     .Where(device => device.Kind == DeviceKind.Sorter)
                     .OrderBy(device => device.Id, StringComparer.Ordinal))
        {
            string targetId = string.IsNullOrWhiteSpace(device.Id) ? "devices.inspectionSorter" : device.Id;
            InspectionSortRouterDefinition? definition = device.InspectionSortRouter;
            if (definition is null)
            {
                AddSortRouterError(errors, targetId, "Inspection-sorter settings are required.");
                continue;
            }

            if (!cameraIds.Contains(definition.CameraId))
            {
                AddSortRouterError(errors, targetId, "Inspection sorter camera must identify a configured virtual camera.");
                continue;
            }

            if (!conveyorsById.TryGetValue(definition.PassConveyorComponentId, out var passConveyor)
                || !conveyorsById.TryGetValue(definition.NgConveyorComponentId, out var ngConveyor)
                || string.Equals(passConveyor.Id, ngConveyor.Id, StringComparison.Ordinal))
            {
                AddSortRouterError(errors, targetId, "Inspection sorter routes must identify two distinct conveyors in the active layout.");
                continue;
            }

            if (channelKinds is null
                || !HasChannelKind(definition.PassRoutedFeedbackChannelId, ChannelKind.DigitalInput, channelKinds)
                || !HasChannelKind(definition.NgRoutedFeedbackChannelId, ChannelKind.DigitalInput, channelKinds)
                || string.Equals(
                    definition.PassRoutedFeedbackChannelId,
                    definition.NgRoutedFeedbackChannelId,
                    StringComparison.Ordinal))
            {
                AddSortRouterError(errors, targetId, "Inspection sorter route feedback channels must be two distinct DigitalInput channels.");
                continue;
            }

            try
            {
                sorters.Add(new InspectionSortRouterRuntimeConfiguration(
                    device.Id,
                    device.Name,
                    definition.CameraId,
                    passConveyor.Id,
                    ngConveyor.Id,
                    passConveyor.RunCommandChannelId,
                    ngConveyor.RunCommandChannelId,
                    definition.PassRoutedFeedbackChannelId,
                    definition.NgRoutedFeedbackChannelId));
            }
            catch (ArgumentException exception)
            {
                AddSortRouterError(errors, targetId, exception.Message);
            }
        }

        return sorters;
    }

    internal IReadOnlyList<InspectionHandoffRuntimeConfiguration> BuildHandoffs(
        IEnumerable<DeviceDefinition> devices,
        IReadOnlyDictionary<string, ChannelKind>? channelKinds,
        ICollection<MachineProjectRuntimeCompilationError> errors)
    {
        DeviceDefinition[] deviceArray = devices.ToArray();
        var cameraIds = deviceArray
            .Where(device => device is { Kind: DeviceKind.Camera, Camera: not null })
            .Select(device => device.Id)
            .ToHashSet(StringComparer.Ordinal);
        var handoffs = new List<InspectionHandoffRuntimeConfiguration>();

        foreach (DeviceDefinition device in deviceArray
                     .Where(device => device.Kind == DeviceKind.Inspection)
                     .OrderBy(device => device.Id, StringComparer.Ordinal))
        {
            string targetId = string.IsNullOrWhiteSpace(device.Id) ? "devices.inspectionHandoff" : device.Id;
            InspectionHandoffDefinition? definition = device.InspectionHandoff;
            if (definition is null)
            {
                AddHandoffError(errors, targetId, "Inspection-handoff settings are required.");
                continue;
            }

            if (!cameraIds.Contains(definition.CameraId))
            {
                AddHandoffError(errors, targetId, "Inspection handoff camera must identify a configured virtual camera.");
                continue;
            }

            string[] inputIds =
            {
                definition.InspectionPositionSensorChannelId,
                definition.InspectionReadyFeedbackChannelId,
                definition.InspectionCompleteFeedbackChannelId
            };
            if (channelKinds is null
                || inputIds.Any(channelId => !HasChannelKind(channelId, ChannelKind.DigitalInput, channelKinds))
                || !HasChannelKind(definition.ResultAcceptedCommandChannelId, ChannelKind.DigitalOutput, channelKinds)
                || inputIds.Append(definition.ResultAcceptedCommandChannelId).Distinct(StringComparer.Ordinal).Count() != 4)
            {
                AddHandoffError(errors, targetId, "Inspection handoff requires three distinct DigitalInput channels and one distinct DigitalOutput result-accepted command.");
                continue;
            }

            try
            {
                handoffs.Add(new InspectionHandoffRuntimeConfiguration(
                    device.Id,
                    device.Name,
                    definition.CameraId,
                    definition.InspectionPositionSensorChannelId,
                    definition.ResultAcceptedCommandChannelId,
                    definition.InspectionReadyFeedbackChannelId,
                    definition.InspectionCompleteFeedbackChannelId));
            }
            catch (ArgumentException exception)
            {
                AddHandoffError(errors, targetId, exception.Message);
            }
        }

        return handoffs;
    }

    private static void AddSortRouterError(
        ICollection<MachineProjectRuntimeCompilationError> errors,
        string targetId,
        string message) =>
        errors.Add(Error(
            MachineProjectRuntimeCompilationErrorCode.InspectionSortRouterConfigurationInvalid,
            targetId,
            message));

    private static void AddHandoffError(
        ICollection<MachineProjectRuntimeCompilationError> errors,
        string targetId,
        string message) =>
        errors.Add(Error(
            MachineProjectRuntimeCompilationErrorCode.InspectionHandoffConfigurationInvalid,
            targetId,
            message));

    private static bool HasChannelKind(
        string channelId,
        ChannelKind expectedKind,
        IReadOnlyDictionary<string, ChannelKind> channelKinds) =>
        !string.IsNullOrWhiteSpace(channelId)
        && channelKinds.TryGetValue(channelId, out ChannelKind kind)
        && kind == expectedKind;

    private static MachineProjectRuntimeCompilationError Error(
        MachineProjectRuntimeCompilationErrorCode code,
        string? targetId,
        string message) =>
        new(code, targetId, message);
}
