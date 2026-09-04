using OpenVisionLab.Machine.Core.Axes;
using OpenVisionLab.Machine.Core.Channels;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Simulation.Axis;
using OpenVisionLab.Machine.Simulation.Workpieces;

namespace OpenVisionLab.Machine.Simulation.Compilation;

internal sealed class MachineProjectRuntimePickPlaceCompiler
{
    internal PickPlaceWorkpieceRuntimeConfiguration? Compile(
        PickPlaceWorkpieceDefinition? definition,
        IReadOnlyList<AxisConfiguration> axes,
        IReadOnlyDictionary<string, ChannelKind>? channelKinds,
        ICollection<MachineProjectRuntimeCompilationError> errors)
    {
        if (definition is null)
        {
            return null;
        }

        AxisConfiguration? xAxis = axes.FirstOrDefault(axis =>
            string.Equals(axis.Id, definition.XAxisId, StringComparison.Ordinal));
        AxisConfiguration? yAxis = axes.FirstOrDefault(axis =>
            string.Equals(axis.Id, definition.YAxisId, StringComparison.Ordinal));
        var valid = !string.IsNullOrWhiteSpace(definition.Id) &&
            !string.IsNullOrWhiteSpace(definition.Name) &&
            xAxis is not null &&
            yAxis is not null &&
            !string.Equals(definition.XAxisId, definition.YAxisId, StringComparison.Ordinal) &&
            channelKinds is not null &&
            channelKinds.TryGetValue(definition.GripperSignalId, out var gripperKind) &&
            gripperKind == ChannelKind.DigitalOutput &&
            double.IsFinite(definition.PickX) &&
            double.IsFinite(definition.PickY) &&
            definition.PickX >= xAxis.MinimumPosition &&
            definition.PickX <= xAxis.MaximumPosition &&
            definition.PickY >= yAxis.MinimumPosition &&
            definition.PickY <= yAxis.MaximumPosition;
        if (!valid)
        {
            errors.Add(Error(
                MachineProjectRuntimeCompilationErrorCode.PickPlaceWorkpieceInvalid,
                string.IsNullOrWhiteSpace(definition.Id) ? "simulation.pickPlaceWorkpiece" : definition.Id,
                "Pick-and-Place workpiece requires an id, name, distinct configured X/Y axes, " +
                "a digital-output gripper signal, and a finite Pick position within both axis limits."));
            return null;
        }

        return new PickPlaceWorkpieceRuntimeConfiguration(
            definition.Id,
            definition.Name,
            definition.XAxisId,
            definition.YAxisId,
            definition.GripperSignalId,
            definition.PickX,
            definition.PickY);
    }

    private static MachineProjectRuntimeCompilationError Error(
        MachineProjectRuntimeCompilationErrorCode code,
        string? targetId,
        string message) =>
        new(code, targetId, message);
}
