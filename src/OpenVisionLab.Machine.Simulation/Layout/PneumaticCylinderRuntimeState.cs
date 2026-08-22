namespace OpenVisionLab.Machine.Simulation.Layout;

internal sealed class PneumaticCylinderRuntimeState : LayoutComponentRuntimeState
{
    private int _pendingExtendedSensorTicks;
    private int _pendingRetractedSensorTicks;

    public PneumaticCylinderRuntimeState(PneumaticCylinderRuntimeConfiguration configuration)
        : base(configuration)
    {
        CylinderConfiguration = configuration;
        Reset();
    }

    public PneumaticCylinderRuntimeConfiguration CylinderConfiguration { get; }
    public PneumaticCylinderState State { get; private set; }
    public double MotionProgress { get; private set; }
    public bool IsExtendedFeedback { get; private set; }
    public bool IsRetractedFeedback { get; private set; }

    public void Tick(bool extendCommand, bool travelBlocked)
    {
        if (travelBlocked)
        {
            State = PneumaticCylinderState.Fault;
            _pendingExtendedSensorTicks = 0;
            _pendingRetractedSensorTicks = 0;
            return;
        }

        if (extendCommand)
        {
            MotionProgress = Math.Min(
                1d,
                MotionProgress + (1d / CylinderConfiguration.ExtendDurationTicks));
            State = MotionProgress >= 1d
                ? PneumaticCylinderState.Extended
                : PneumaticCylinderState.Extending;
        }
        else
        {
            MotionProgress = Math.Max(
                0d,
                MotionProgress - (1d / CylinderConfiguration.RetractDurationTicks));
            State = MotionProgress <= 0d
                ? PneumaticCylinderState.Retracted
                : PneumaticCylinderState.Retracting;
        }

        IsExtendedFeedback = ApplyOnDelay(
            State == PneumaticCylinderState.Extended,
            IsExtendedFeedback,
            CylinderConfiguration.ExtendedSensorDelayTicks,
            ref _pendingExtendedSensorTicks);
        IsRetractedFeedback = ApplyOnDelay(
            State == PneumaticCylinderState.Retracted,
            IsRetractedFeedback,
            CylinderConfiguration.RetractedSensorDelayTicks,
            ref _pendingRetractedSensorTicks);
    }

    public override void Reset()
    {
        base.Reset();
        State = PneumaticCylinderState.Retracted;
        MotionProgress = 0d;
        IsExtendedFeedback = false;
        IsRetractedFeedback = true;
        _pendingExtendedSensorTicks = 0;
        _pendingRetractedSensorTicks = 0;
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
            Math.Max(_pendingExtendedSensorTicks, _pendingRetractedSensorTicks),
            State,
            MotionProgress);

    private static bool ApplyOnDelay(
        bool rawValue,
        bool currentValue,
        int delayTicks,
        ref int pendingTicks)
    {
        if (!rawValue)
        {
            pendingTicks = 0;
            return false;
        }

        if (currentValue)
        {
            pendingTicks = 0;
            return true;
        }

        pendingTicks++;
        if (delayTicks == 0 || pendingTicks >= delayTicks)
        {
            pendingTicks = 0;
            return true;
        }

        return false;
    }
}
