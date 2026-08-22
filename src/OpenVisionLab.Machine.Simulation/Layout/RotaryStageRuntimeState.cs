namespace OpenVisionLab.Machine.Simulation.Layout;

internal sealed class RotaryStageRuntimeState : AxisBoundStageRuntimeState
{
    public RotaryStageRuntimeState(RotaryStageRuntimeConfiguration configuration)
        : base(configuration)
    {
    }

    protected override void ApplyAxisDelta(double axisDelta)
    {
        double rotationDegrees = Configuration.BaseTransform.RotationDegrees + axisDelta;
        if (!double.IsFinite(rotationDegrees))
        {
            throw new InvalidOperationException(
                $"Layout stage '{Configuration.Id}' rotation is not finite.");
        }

        RotationDegrees = rotationDegrees;
    }
}
