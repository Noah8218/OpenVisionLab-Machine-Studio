namespace OpenVisionLab.Machine.Simulation.Axis;

public sealed class ServoAxisComponent
{
    private readonly AxisConfiguration _config;
    private AxisState _state = AxisState.Idle;
    private double _position;
    private double _velocity;
    private double _commandPosition;
    private double _followingError;
    private TrapezoidalMotionProfile? _profile;
    private bool _stopsAtLimit;
    private bool _followingErrorInjected;
    private bool _driveAlarmActive;
    private bool _motionBlocked;
    private TimeSpan _profileStartTime;
    private TimeSpan _currentTime;

    public ServoAxisComponent(AxisConfiguration config)
    {
        _config = config;
        _position = config.HomePosition;
        _commandPosition = config.HomePosition;
    }

    public string Id => _config.Id;
    public string Name => _config.Name;
    public AxisState State => _state;
    public double Position => _position;
    public double Velocity => _velocity;
    public double HomePosition => _config.HomePosition;
    public double MinimumPosition => _config.MinimumPosition;
    public double MaximumPosition => _config.MaximumPosition;
    public double MaximumVelocity => _config.MaximumVelocity;
    public double CommandPosition => _commandPosition;
    public double FollowingError => _followingError;
    public double FollowingErrorLimit => _config.FollowingErrorLimit;
    public bool DriveAlarmActive => _driveAlarmActive;

    public AxisCommandResult ValidateAbsoluteMove(double targetPosition) =>
        ValidateMoveTo(targetPosition, MaximumVelocity);

    public AxisCommandResult MoveAbsolute(double targetPosition) =>
        MoveTo(targetPosition, MaximumVelocity, stopsAtLimit: false);

    public AxisCommandResult MoveRelative(double distance) =>
        MoveAbsolute(_position + distance);

    public AxisCommandResult MoveVelocity(double velocity) =>
        MoveTo(
            velocity > 0 ? MaximumPosition : MinimumPosition,
            Math.Abs(velocity),
            stopsAtLimit: true);

    public AxisCommandResult Jog(bool positive) =>
        MoveTo(
            positive ? MaximumPosition : MinimumPosition,
            MaximumVelocity,
            stopsAtLimit: true);

    private AxisCommandResult MoveTo(
        double targetPosition,
        double maximumVelocity,
        bool stopsAtLimit)
    {
        var validation = ValidateMoveTo(targetPosition, maximumVelocity);
        if (!validation.IsAccepted)
        {
            return validation;
        }

        _profile = new TrapezoidalMotionProfile(_position, targetPosition, maximumVelocity, _config.Acceleration, _config.Deceleration);
        _profileStartTime = _currentTime;
        _stopsAtLimit = stopsAtLimit;
        _state = AxisState.Moving;
        return validation;
    }

    private AxisCommandResult ValidateMoveTo(double targetPosition, double maximumVelocity)
    {
        if (_state == AxisState.Error)
        {
            return AxisCommandResult.Rejected(AxisCommandErrorCode.AxisInterlocked, targetPosition);
        }

        if (!double.IsFinite(targetPosition))
        {
            return AxisCommandResult.Rejected(AxisCommandErrorCode.InvalidTarget, targetPosition);
        }

        if (targetPosition < _config.MinimumPosition || targetPosition > _config.MaximumPosition)
        {
            return AxisCommandResult.Rejected(AxisCommandErrorCode.TargetOutOfRange, targetPosition);
        }

        if (!double.IsFinite(maximumVelocity) || maximumVelocity <= 0)
        {
            return AxisCommandResult.Rejected(AxisCommandErrorCode.InvalidVelocity, targetPosition);
        }

        if (maximumVelocity > MaximumVelocity)
        {
            return AxisCommandResult.Rejected(AxisCommandErrorCode.VelocityOutOfRange, targetPosition);
        }

        if (_state == AxisState.Moving)
        {
            return AxisCommandResult.Rejected(AxisCommandErrorCode.AxisBusy, targetPosition);
        }

        return AxisCommandResult.Accepted(targetPosition);
    }

    public void Stop()
    {
        if (_profile is null)
        {
            return;
        }

        _profile = null;
        _stopsAtLimit = false;
        _commandPosition = _position;
        _followingError = 0;
        _velocity = 0;
        _state = AxisState.Stopped;
    }

    public void SetMotionBlocked(bool isBlocked)
    {
        _motionBlocked = isBlocked;
        if (isBlocked)
        {
            _profile = null;
            _stopsAtLimit = false;
            _commandPosition = _position;
            _followingError = 0;
            _velocity = 0;
            _state = AxisState.Error;
        }
        else
        {
            _state = _driveAlarmActive ? AxisState.Error : AxisState.Stopped;
        }
    }

    public void SetFollowingErrorInjected(bool isInjected)
    {
        _followingErrorInjected = isInjected;
        if (isInjected)
        {
            return;
        }

        var wasAlarmed = _driveAlarmActive;
        _driveAlarmActive = false;
        _followingError = 0;
        _commandPosition = _position;
        if (wasAlarmed)
        {
            _state = _motionBlocked ? AxisState.Error : AxisState.Stopped;
        }
    }

    public void Reset()
    {
        _position = _config.HomePosition;
        _commandPosition = _config.HomePosition;
        _followingError = 0;
        _velocity = 0;
        _state = AxisState.Idle;
        _profile = null;
        _stopsAtLimit = false;
        _followingErrorInjected = false;
        _driveAlarmActive = false;
        _motionBlocked = false;
        _currentTime = TimeSpan.Zero;
    }

    public void Tick(TimeSpan deltaTime)
    {
        _currentTime += deltaTime;

        if (_profile is null)
        {
            _velocity = 0;
            return;
        }

        var elapsed = _currentTime - _profileStartTime;
        var (position, velocity) = _profile.Evaluate(elapsed.TotalSeconds);

        _commandPosition = position;
        if (_followingErrorInjected)
        {
            _velocity = 0;
            _followingError = _commandPosition - _position;
            if (Math.Abs(_followingError) >= FollowingErrorLimit)
            {
                _driveAlarmActive = true;
                _state = AxisState.Error;
                _profile = null;
                _stopsAtLimit = false;
            }
            return;
        }

        _position = position;
        _velocity = velocity;
        _followingError = 0;

        if (elapsed >= TimeSpan.FromSeconds(_profile.TotalTime))
        {
            _position = _profile.Evaluate(_profile.TotalTime).Position;
            _velocity = 0;
            _state = _stopsAtLimit ? AxisState.Limited : AxisState.Idle;
            _profile = null;
            _stopsAtLimit = false;
        }
    }

    public AxisSnapshot CreateSnapshot()
    {
        return new AxisSnapshot(
            Id,
            Name,
            _state,
            _position,
            _velocity,
            _commandPosition,
            _followingError,
            FollowingErrorLimit,
            _driveAlarmActive,
            _config.MaximumVelocity,
            _config.Acceleration,
            _config.Deceleration);
    }
}
