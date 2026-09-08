using OpenVisionLab.Machine.Core.Axes;
using OpenVisionLab.Machine.Core.Channels;
using OpenVisionLab.Machine.Core.Devices;
using OpenVisionLab.Machine.Simulation.Layout;

namespace OpenVisionLab.Machine.Simulation.Compilation;

/// <summary>
/// Compiles wafer-handler device definitions into immutable runtime configurations.
/// </summary>
internal sealed class MachineLayoutWaferHandlerRuntimeCompiler
{
    internal IReadOnlyList<WaferHandlerRuntimeConfiguration> Compile(
        IEnumerable<DeviceDefinition> devices,
        IReadOnlyCollection<LayoutComponentRuntimeConfiguration> runtimeComponents,
        IReadOnlyDictionary<string, VirtualAxisDefinition> axesById,
        IReadOnlyDictionary<string, ChannelKind>? channelKinds,
        ICollection<MachineProjectRuntimeCompilationError> errors)
    {
        var workpieceIds = runtimeComponents
            .OfType<WorkpieceRuntimeConfiguration>()
            .Select(workpiece => workpiece.Id)
            .ToHashSet(StringComparer.Ordinal);
        var handlers = new List<WaferHandlerRuntimeConfiguration>();

        foreach (DeviceDefinition device in devices
                     .Where(device => device.Kind == DeviceKind.Handler)
                     .OrderBy(device => device.Id, StringComparer.Ordinal))
        {
            string targetId = string.IsNullOrWhiteSpace(device.Id) ? "devices.waferHandler" : device.Id;
            WaferHandlerDefinition? definition = device.WaferHandler;
            if (definition is null)
            {
                AddError(errors, targetId, "Wafer-handler settings are required.");
                continue;
            }

            if (!axesById.TryGetValue(definition.HorizontalAxisId, out VirtualAxisDefinition? horizontal)
                || !axesById.TryGetValue(definition.VerticalAxisId, out VirtualAxisDefinition? vertical)
                || horizontal.Kind != AxisKind.Linear
                || vertical.Kind != AxisKind.Linear
                || string.Equals(horizontal.Id, vertical.Id, StringComparison.Ordinal))
            {
                AddError(errors, targetId, "Wafer-handler axes must identify two distinct configured linear axes.");
                continue;
            }

            if (!workpieceIds.Contains(definition.WorkpieceComponentId))
            {
                AddError(errors, targetId, "Wafer-handler workpiece must identify a workpiece in the active layout.");
                continue;
            }

            if (!PositionWithin(horizontal, definition.PickHorizontalPosition)
                || !PositionWithin(vertical, definition.PickVerticalPosition)
                || !PositionWithin(horizontal, definition.PlaceHorizontalPosition)
                || !PositionWithin(vertical, definition.PlaceVerticalPosition))
            {
                AddError(errors, targetId, "Wafer-handler pick and place positions must be finite and within their axis soft limits.");
                continue;
            }

            if (channelKinds is null
                || !HasChannelKind(definition.SourcePresentSensorChannelId, ChannelKind.DigitalInput, channelKinds)
                || !HasChannelKind(definition.GateOpenSensorChannelId, ChannelKind.DigitalInput, channelKinds)
                || !HasChannelKind(definition.PickCommandChannelId, ChannelKind.DigitalOutput, channelKinds)
                || !HasChannelKind(definition.PlaceCommandChannelId, ChannelKind.DigitalOutput, channelKinds)
                || !HasChannelKind(definition.HoldingFeedbackChannelId, ChannelKind.DigitalInput, channelKinds)
                || !HasChannelKind(definition.PlacedFeedbackChannelId, ChannelKind.DigitalInput, channelKinds))
            {
                AddError(errors, targetId, "Wafer-handler conditions/feedback must be DigitalInput and pick/place commands must be DigitalOutput.");
                continue;
            }

            try
            {
                handlers.Add(new WaferHandlerRuntimeConfiguration(
                    device.Id,
                    device.Name,
                    definition.HorizontalAxisId,
                    definition.VerticalAxisId,
                    definition.WorkpieceComponentId,
                    definition.SourcePresentSensorChannelId,
                    definition.GateOpenSensorChannelId,
                    definition.PickCommandChannelId,
                    definition.PlaceCommandChannelId,
                    definition.HoldingFeedbackChannelId,
                    definition.PlacedFeedbackChannelId,
                    definition.PickHorizontalPosition,
                    definition.PickVerticalPosition,
                    definition.PlaceHorizontalPosition,
                    definition.PlaceVerticalPosition));
            }
            catch (ArgumentException exception)
            {
                AddError(errors, targetId, exception.Message);
            }
        }

        return handlers;
    }

    private static bool PositionWithin(VirtualAxisDefinition axis, double position) =>
        double.IsFinite(position)
        && axis.SoftLimitMin.HasValue
        && axis.SoftLimitMax.HasValue
        && position >= axis.SoftLimitMin.Value
        && position <= axis.SoftLimitMax.Value;

    private static bool HasChannelKind(
        string channelId,
        ChannelKind expectedKind,
        IReadOnlyDictionary<string, ChannelKind> channelKinds) =>
        !string.IsNullOrWhiteSpace(channelId)
        && channelKinds.TryGetValue(channelId, out ChannelKind kind)
        && kind == expectedKind;

    private static void AddError(
        ICollection<MachineProjectRuntimeCompilationError> errors,
        string targetId,
        string message) =>
        errors.Add(new(
            MachineProjectRuntimeCompilationErrorCode.WaferHandlerConfigurationInvalid,
            targetId,
            message));
}
