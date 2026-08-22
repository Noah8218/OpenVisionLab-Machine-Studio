namespace OpenVisionLab.Machine.Simulation.Layout;

internal sealed class ConveyorRuntimeState : LayoutComponentRuntimeState
{
    public ConveyorRuntimeState(ConveyorRuntimeConfiguration configuration)
        : base(configuration)
    {
        ConveyorConfiguration = configuration;
        Reset();
    }

    public ConveyorRuntimeConfiguration ConveyorConfiguration { get; }
    public bool IsRunning { get; private set; }
    public ConveyorDirection Direction { get; private set; }

    public void Tick(bool runCommand, bool reverseCommand)
    {
        IsRunning = runCommand;
        Direction = reverseCommand ? ConveyorDirection.Reverse : ConveyorDirection.Forward;
    }

    public override void Reset()
    {
        base.Reset();
        IsRunning = false;
        Direction = ConveyorDirection.Forward;
    }

    public override LayoutComponentSnapshot CaptureSnapshot() =>
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
            null,
            ConveyorRunning: IsRunning,
            ConveyorDirection: Direction,
            ConveyorSpeedUnitsPerSecond: ConveyorConfiguration.SpeedUnitsPerSecond);
}
