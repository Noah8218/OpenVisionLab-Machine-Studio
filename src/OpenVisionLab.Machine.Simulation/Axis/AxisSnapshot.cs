namespace OpenVisionLab.Machine.Simulation.Axis;

public sealed class AxisSnapshot
{
    public string Id { get; }
    public string Name { get; }
    public AxisState State { get; }
    public double Position { get; }
    public double Velocity { get; }
    public double CommandPosition { get; }
    public double FollowingError { get; }
    public double FollowingErrorLimit { get; }
    public bool DriveAlarmActive { get; }
    public double MaximumVelocity { get; }
    public double Acceleration { get; }
    public double Deceleration { get; }

    public AxisSnapshot(string id, string name, AxisState state, double position, double velocity)
        : this(id, name, state, position, velocity, position, 0, 0, false, 0, 0, 0)
    {
    }

    public AxisSnapshot(
        string id,
        string name,
        AxisState state,
        double position,
        double velocity,
        double commandPosition,
        double followingError,
        double followingErrorLimit,
        bool driveAlarmActive,
        double maximumVelocity,
        double acceleration,
        double deceleration)
    {
        Id = id;
        Name = name;
        State = state;
        Position = position;
        Velocity = velocity;
        CommandPosition = commandPosition;
        FollowingError = followingError;
        FollowingErrorLimit = followingErrorLimit;
        DriveAlarmActive = driveAlarmActive;
        MaximumVelocity = maximumVelocity;
        Acceleration = acceleration;
        Deceleration = deceleration;
    }
}
