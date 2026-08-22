using OpenVisionLab.Machine.Simulation.Axis;
using Xunit;

namespace OpenVisionLab.Machine.Simulation.Tests;

public class ServoAxisComponentTests
{
    private static AxisConfiguration CreateConfig() => new()
    {
        Id = "x",
        Name = "X Axis",
        MinimumPosition = 0,
        MaximumPosition = 300,
        HomePosition = 0,
        MaximumVelocity = 200,
        Acceleration = 500,
        Deceleration = 500
    };

    [Fact]
    public void Reset_RestoresInitialState()
    {
        var axis = new ServoAxisComponent(CreateConfig());
        axis.MoveAbsolute(100);
        axis.Reset();
        Assert.Equal(0, axis.Position);
        Assert.Equal(AxisState.Idle, axis.State);
        Assert.Equal(0, axis.Velocity);
    }

    [Fact]
    public void MoveAbsolute_RejectsTargetOutsideSoftLimit()
    {
        var axis = new ServoAxisComponent(CreateConfig());
        var result = axis.MoveAbsolute(500);

        Assert.False(result.IsAccepted);
        Assert.Equal(AxisCommandErrorCode.TargetOutOfRange, result.ErrorCode);
        Assert.Equal(0, axis.Position, 6);
        Assert.Equal(AxisState.Idle, axis.State);
    }

    [Fact]
    public void MoveWhileMoving_IsRejectedWithoutChangingActiveMove()
    {
        var axis = new ServoAxisComponent(CreateConfig());
        var first = axis.MoveAbsolute(100);
        var second = axis.MoveAbsolute(200);

        Assert.True(first.IsAccepted);
        Assert.False(second.IsAccepted);
        Assert.Equal(AxisCommandErrorCode.AxisBusy, second.ErrorCode);
        Assert.Equal(AxisState.Moving, axis.State);
    }

    [Fact]
    public void ValidateAbsoluteMove_DoesNotMutateAxisState()
    {
        var axis = new ServoAxisComponent(CreateConfig());

        var result = axis.ValidateAbsoluteMove(100);

        Assert.True(result.IsAccepted);
        Assert.Equal(AxisState.Idle, axis.State);
        Assert.Equal(0, axis.Position, 10);
        Assert.Equal(0, axis.CommandPosition, 10);
    }

    [Fact]
    public void Snapshot_PublishesAuthoredDriveTuning()
    {
        var configuration = CreateConfig();
        configuration.Deceleration = 450;
        configuration.FollowingErrorLimit = 0.08;
        var snapshot = new ServoAxisComponent(configuration).CreateSnapshot();

        Assert.Equal(200, snapshot.MaximumVelocity);
        Assert.Equal(500, snapshot.Acceleration);
        Assert.Equal(450, snapshot.Deceleration);
        Assert.Equal(0.08, snapshot.FollowingErrorLimit);
    }

    [Fact]
    public void Tick_AdvancesPosition()
    {
        var axis = new ServoAxisComponent(CreateConfig());
        axis.MoveAbsolute(100);
        axis.Tick(TimeSpan.FromMilliseconds(5));
        Assert.True(axis.Position > 0);
        Assert.True(axis.Velocity > 0);
    }

    [Fact]
    public void Stop_SetsStoppedState()
    {
        var axis = new ServoAxisComponent(CreateConfig());
        axis.MoveAbsolute(100);
        axis.Tick(TimeSpan.FromMilliseconds(5));
        axis.Stop();
        Assert.Equal(AxisState.Stopped, axis.State);
        Assert.Equal(0, axis.Velocity);
    }

    [Fact]
    public void Jog_ReachesAuthoredSoftLimitAndPreservesLimitedStateOnStop()
    {
        var axis = new ServoAxisComponent(CreateConfig());

        Assert.True(axis.Jog(positive: true).IsAccepted);
        for (var tick = 0; tick < 600 && axis.State == AxisState.Moving; tick++)
        {
            axis.Tick(TimeSpan.FromMilliseconds(5));
        }

        Assert.Equal(300, axis.Position, 6);
        Assert.Equal(AxisState.Limited, axis.State);
        axis.Stop();
        Assert.Equal(AxisState.Limited, axis.State);
    }

    [Fact]
    public void MotionBlock_StopsAxisRejectsMotionAndClearLeavesSafeStoppedState()
    {
        var axis = new ServoAxisComponent(CreateConfig());
        Assert.True(axis.MoveAbsolute(100).IsAccepted);
        axis.Tick(TimeSpan.FromMilliseconds(5));

        axis.SetMotionBlocked(true);

        Assert.Equal(AxisState.Error, axis.State);
        Assert.Equal(0, axis.Velocity);
        var blocked = axis.MoveAbsolute(50);
        Assert.False(blocked.IsAccepted);
        Assert.Equal(AxisCommandErrorCode.AxisInterlocked, blocked.ErrorCode);

        axis.SetMotionBlocked(false);
        Assert.Equal(AxisState.Stopped, axis.State);
        Assert.True(axis.MoveAbsolute(50).IsAccepted);
    }

    [Fact]
    public void FollowingError_LatchesDriveAlarmAtConfiguredLimitAndClearLeavesStopped()
    {
        var configuration = CreateConfig();
        configuration.FollowingErrorLimit = 0.05;
        var axis = new ServoAxisComponent(configuration);

        Assert.True(axis.MoveVelocity(5).IsAccepted);
        axis.SetFollowingErrorInjected(true);
        axis.Tick(TimeSpan.FromMilliseconds(5));
        axis.Tick(TimeSpan.FromMilliseconds(5));
        axis.Tick(TimeSpan.FromMilliseconds(5));
        Assert.False(axis.DriveAlarmActive);

        axis.Tick(TimeSpan.FromMilliseconds(5));

        Assert.True(axis.DriveAlarmActive);
        Assert.Equal(AxisState.Error, axis.State);
        Assert.Equal(0, axis.Position, 10);
        Assert.Equal(0.075, axis.CommandPosition, 10);
        Assert.Equal(axis.CommandPosition - axis.Position, axis.FollowingError, 10);
        Assert.Equal(0.05, axis.FollowingErrorLimit, 10);
        Assert.Equal(0, axis.Velocity, 10);
        axis.SetFollowingErrorInjected(false);
        Assert.False(axis.DriveAlarmActive);
        Assert.Equal(0, axis.FollowingError, 10);
        Assert.Equal(AxisState.Stopped, axis.State);
    }
}
