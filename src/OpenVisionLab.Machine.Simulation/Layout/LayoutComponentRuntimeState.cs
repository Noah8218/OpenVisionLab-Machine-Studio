namespace OpenVisionLab.Machine.Simulation.Layout;

internal abstract class LayoutComponentRuntimeState
{
    protected LayoutComponentRuntimeState(LayoutComponentRuntimeConfiguration configuration)
    {
        Configuration = configuration;
        X = configuration.BaseTransform.X;
        Y = configuration.BaseTransform.Y;
        RotationDegrees = configuration.BaseTransform.RotationDegrees;
    }

    public LayoutComponentRuntimeConfiguration Configuration { get; }
    public double X { get; set; }
    public double Y { get; set; }
    public double RotationDegrees { get; set; }

    public virtual void Reset()
    {
        X = Configuration.BaseTransform.X;
        Y = Configuration.BaseTransform.Y;
        RotationDegrees = Configuration.BaseTransform.RotationDegrees;
    }

    public virtual LayoutComponentSnapshot CaptureSnapshot() =>
        new(
            Configuration.Id,
            Configuration.Name,
            Configuration.Kind,
            X,
            Y,
            RotationDegrees,
            Configuration.Size.Width,
            Configuration.Size.Height,
            null,
            null);
}
