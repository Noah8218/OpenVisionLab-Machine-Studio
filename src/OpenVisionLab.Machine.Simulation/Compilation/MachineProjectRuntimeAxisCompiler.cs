using OpenVisionLab.Machine.Core.Axes;
using OpenVisionLab.Machine.Simulation.Axis;

namespace OpenVisionLab.Machine.Simulation.Compilation;

internal sealed class MachineProjectRuntimeAxisCompiler
{
    internal IReadOnlyList<AxisConfiguration> Compile(
        IEnumerable<VirtualAxisDefinition> definitions,
        ICollection<MachineProjectRuntimeCompilationError> errors)
    {
        var axes = new List<AxisConfiguration>();
        var ids = new HashSet<string>(StringComparer.Ordinal);

        foreach (VirtualAxisDefinition definition in definitions)
        {
            if (string.IsNullOrWhiteSpace(definition.Id))
            {
                errors.Add(Error(
                    MachineProjectRuntimeCompilationErrorCode.AxisIdRequired,
                    definition.Id,
                    "Every axis requires an id."));
                continue;
            }

            if (!ids.Add(definition.Id))
            {
                errors.Add(Error(
                    MachineProjectRuntimeCompilationErrorCode.DuplicateAxisId,
                    definition.Id,
                    $"Axis id '{definition.Id}' is duplicated."));
                continue;
            }

            var axis = new AxisConfiguration
            {
                Id = definition.Id,
                Name = definition.Name,
                MinimumPosition = definition.SoftLimitMin ?? 0,
                MaximumPosition = definition.SoftLimitMax ?? 300,
                HomePosition = definition.HomePosition,
                MaximumVelocity = definition.MaxVelocity,
                Acceleration = definition.MaxAcceleration,
                Deceleration = definition.MaxDeceleration ?? definition.MaxAcceleration,
                FollowingErrorLimit = definition.FollowingErrorLimit ??
                    VirtualAxisDefinition.DefaultFollowingErrorLimit
            };

            if (!IsValidAxis(axis))
            {
                errors.Add(Error(
                    MachineProjectRuntimeCompilationErrorCode.AxisConfigurationInvalid,
                    definition.Id,
                    $"Axis '{definition.Id}' has invalid limits or motion parameters."));
                continue;
            }

            axes.Add(axis);
        }

        return axes;
    }

    private static bool IsValidAxis(AxisConfiguration axis) =>
        double.IsFinite(axis.MinimumPosition) &&
        double.IsFinite(axis.MaximumPosition) &&
        double.IsFinite(axis.HomePosition) &&
        axis.MinimumPosition <= axis.MaximumPosition &&
        axis.HomePosition >= axis.MinimumPosition &&
        axis.HomePosition <= axis.MaximumPosition &&
        double.IsFinite(axis.MaximumVelocity) &&
        axis.MaximumVelocity > 0 &&
        double.IsFinite(axis.Acceleration) &&
        axis.Acceleration > 0 &&
        double.IsFinite(axis.Deceleration) &&
        axis.Deceleration > 0 &&
        double.IsFinite(axis.FollowingErrorLimit) &&
        axis.FollowingErrorLimit > 0;

    private static MachineProjectRuntimeCompilationError Error(
        MachineProjectRuntimeCompilationErrorCode code,
        string? targetId,
        string message) =>
        new(code, targetId, message);
}
