namespace OpenVisionLab.Machine.Simulation.Axis;

public sealed class TrapezoidalMotionProfile
{
    private readonly double _start;
    private readonly double _target;
    private readonly double _maxVelocity;
    private readonly double _acceleration;
    private readonly double _deceleration;
    private readonly double _totalTime;
    private readonly double _accelTime;
    private readonly double _decelTime;
    private readonly double _cruiseTime;
    private readonly double _distance;
    private readonly int _direction;

    public TrapezoidalMotionProfile(double start, double target, double maxVelocity, double acceleration, double deceleration)
    {
        _start = start;
        _target = target;
        _maxVelocity = Math.Abs(maxVelocity);
        _acceleration = Math.Abs(acceleration);
        _deceleration = Math.Abs(deceleration);
        _distance = Math.Abs(target - start);
        _direction = Math.Sign(target - start);

        if (_distance < 1e-9)
        {
            _totalTime = 0;
            _accelTime = 0;
            _decelTime = 0;
            _cruiseTime = 0;
            return;
        }

        // Compute peak velocity needed
        var accelDist = _maxVelocity * _maxVelocity / (2 * _acceleration);
        var decelDist = _maxVelocity * _maxVelocity / (2 * _deceleration);
        var peakVelocity = _maxVelocity;

        if (accelDist + decelDist > _distance)
        {
            // Triangular profile
            peakVelocity = Math.Sqrt((2 * _distance * _acceleration * _deceleration) / (_acceleration + _deceleration));
        }

        _accelTime = peakVelocity / _acceleration;
        _decelTime = peakVelocity / _deceleration;
        var accelTimeDist = 0.5 * _acceleration * _accelTime * _accelTime;
        var decelTimeDist = 0.5 * _deceleration * _decelTime * _decelTime;
        var cruiseDist = _distance - accelTimeDist - decelTimeDist;
        _cruiseTime = cruiseDist > 0 ? cruiseDist / peakVelocity : 0;
        _totalTime = _accelTime + _cruiseTime + _decelTime;
    }

    public double TotalTime => _totalTime;

    public (double Position, double Velocity) Evaluate(double time)
    {
        if (time < 0) time = 0;
        if (time > _totalTime) time = _totalTime;

        double position;
        double velocity;

        if (time < _accelTime)
        {
            velocity = _acceleration * time;
            position = 0.5 * _acceleration * time * time;
        }
        else if (time < _accelTime + _cruiseTime)
        {
            velocity = _acceleration * _accelTime;
            var t = time - _accelTime;
            position = 0.5 * _acceleration * _accelTime * _accelTime + velocity * t;
        }
        else
        {
            var t = time - _accelTime - _cruiseTime;
            velocity = _acceleration * _accelTime - _deceleration * t;
            var cruiseEnd = 0.5 * _acceleration * _accelTime * _accelTime + _acceleration * _accelTime * _cruiseTime;
            position = cruiseEnd + _acceleration * _accelTime * t - 0.5 * _deceleration * t * t;
        }

        if (_direction < 0)
        {
            position = -position;
            velocity = -velocity;
        }

        return (_start + position, velocity);
    }
}
