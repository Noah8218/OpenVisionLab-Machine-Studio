namespace OpenVisionLab.Machine.Simulation.Axis;

public sealed class AxisConfiguration
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public double MinimumPosition { get; set; } = 0.0;
    public double MaximumPosition { get; set; } = 300.0;
    public double HomePosition { get; set; } = 0.0;
    public double MaximumVelocity { get; set; } = 200.0;
    public double Acceleration { get; set; } = 500.0;
    public double Deceleration { get; set; } = 500.0;
    public double FollowingErrorLimit { get; set; } = 0.05;
}
