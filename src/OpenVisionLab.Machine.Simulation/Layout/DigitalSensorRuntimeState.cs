namespace OpenVisionLab.Machine.Simulation.Layout;

internal sealed class DigitalSensorRuntimeState : LayoutComponentRuntimeState
{
    private int _pendingOnTicks;
    private int _pendingOffTicks;

    public DigitalSensorRuntimeState(DigitalSensorRuntimeConfiguration configuration)
        : base(configuration)
    {
        SensorConfiguration = configuration;
    }

    public DigitalSensorRuntimeConfiguration SensorConfiguration { get; }
    public bool IsDetected { get; private set; }

    public void ApplyRawDetection(bool rawDetected)
    {
        if (rawDetected == IsDetected)
        {
            _pendingOnTicks = 0;
            _pendingOffTicks = 0;
            return;
        }

        if (rawDetected)
        {
            _pendingOffTicks = 0;
            _pendingOnTicks++;
            if (SensorConfiguration.OnDelayTicks == 0 ||
                _pendingOnTicks >= SensorConfiguration.OnDelayTicks)
            {
                IsDetected = true;
                _pendingOnTicks = 0;
            }

            return;
        }

        _pendingOnTicks = 0;
        _pendingOffTicks++;
        if (SensorConfiguration.OffDelayTicks == 0 ||
            _pendingOffTicks >= SensorConfiguration.OffDelayTicks)
        {
            IsDetected = false;
            _pendingOffTicks = 0;
        }
    }

    public override void Reset()
    {
        base.Reset();
        IsDetected = false;
        _pendingOnTicks = 0;
        _pendingOffTicks = 0;
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
            IsDetected,
            IsDetected ? _pendingOffTicks : _pendingOnTicks,
            SensorOutputChannelId: SensorConfiguration.OutputChannelId);
}
