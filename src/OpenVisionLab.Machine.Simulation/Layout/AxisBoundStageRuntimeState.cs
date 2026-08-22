using OpenVisionLab.Machine.Simulation.Axis;

namespace OpenVisionLab.Machine.Simulation.Layout;

internal abstract class AxisBoundStageRuntimeState : LayoutComponentRuntimeState
{
    protected AxisBoundStageRuntimeState(AxisBoundStageRuntimeConfiguration configuration)
        : base(configuration)
    {
        StageConfiguration = configuration;
    }

    public AxisBoundStageRuntimeConfiguration StageConfiguration { get; }

    public void ApplyAxisSnapshot(AxisSnapshot axis)
    {
        ArgumentNullException.ThrowIfNull(axis);
        if (!string.Equals(axis.Id, StageConfiguration.AxisId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Layout stage '{Configuration.Id}' received axis '{axis.Id}' instead of '{StageConfiguration.AxisId}'.");
        }

        if (!double.IsFinite(axis.Position))
        {
            throw new InvalidOperationException(
                $"Layout stage '{Configuration.Id}' axis position must be finite.");
        }

        ApplyAxisDelta(axis.Position - StageConfiguration.HomePosition);
    }

    protected abstract void ApplyAxisDelta(double axisDelta);
}
