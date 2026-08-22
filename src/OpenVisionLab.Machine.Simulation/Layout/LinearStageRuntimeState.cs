namespace OpenVisionLab.Machine.Simulation.Layout;

internal sealed class LinearStageRuntimeState : AxisBoundStageRuntimeState
{
    public LinearStageRuntimeState(LinearStageRuntimeConfiguration configuration)
        : base(configuration)
    {
    }

    protected override void ApplyAxisDelta(double axisDelta)
    {
        double worldX = Configuration.BaseTransform.X + axisDelta;
        if (!double.IsFinite(worldX))
        {
            throw new InvalidOperationException(
                $"Layout stage '{Configuration.Id}' world X is not finite.");
        }

        X = worldX;
    }
}
