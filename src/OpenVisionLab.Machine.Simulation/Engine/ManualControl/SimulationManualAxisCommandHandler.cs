using OpenVisionLab.Machine.Simulation.Axis;
using OpenVisionLab.Machine.Simulation.Commands;

namespace OpenVisionLab.Machine.Simulation.Engine;

internal sealed class SimulationManualAxisCommandHandler
{
    internal SimulationManualControlOutcome Apply(
        SimulationCommand command,
        SimulationManualControlContext context)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        return command switch
        {
            MoveAbsoluteCommand move => ApplyManualMove(command, move, context),
            MoveAxesAbsoluteCommand move => ApplyManualGroupMove(command, move, context),
            MoveRelativeCommand move => ApplyManualRelativeMove(command, move, context),
            MoveVelocityCommand move => ApplyManualVelocityMove(command, move, context),
            HomeAxisCommand home => ApplyManualHome(command, home, context),
            JogAxisCommand jog => ApplyManualJog(command, jog, context),
            StopAxisCommand stop => ApplyManualStop(command, stop, context),
            StopAxesCommand stop => ApplyManualGroupStop(command, stop, context),
            _ => SimulationManualControlCommandHandler.Reject(
                command,
                context,
                SimulationCommandErrorCode.UnsupportedCommand,
                $"Command '{command.GetType().Name}' is not supported by the manual axis handler.")
        };
    }

    private static SimulationManualControlOutcome ApplyManualMove(
        SimulationCommand command,
        MoveAbsoluteCommand move,
        SimulationManualControlContext context)
    {
        if (context.ControlOwner != SimulationControlOwner.Manual)
        {
            return SimulationManualControlCommandHandler.Reject(
                command,
                context,
                SimulationCommandErrorCode.ControlOwnerNotAllowed,
                $"Manual axis motion is unavailable while owner is {context.ControlOwner}.");
        }

        var axis = context.Axes.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, move.AxisId, StringComparison.Ordinal));
        if (axis is null)
        {
            return SimulationManualControlCommandHandler.Reject(
                command,
                context,
                SimulationCommandErrorCode.AxisNotFound,
                $"Axis '{move.AxisId}' was not found.");
        }

        var moveResult = axis.MoveAbsolute(move.TargetPosition);
        if (!moveResult.IsAccepted)
        {
            return SimulationManualControlCommandHandler.Reject(
                command,
                context,
                MapAxisError(moveResult.ErrorCode),
                $"Axis move rejected: {moveResult.ErrorCode}.");
        }

        return SimulationManualControlCommandHandler.Accept(
            command,
            context,
            $"Axis '{move.AxisId}' move accepted.",
            new SimulationManualControlEvent(
                "Motion",
                "AxisMoveAccepted",
                $"{move.AxisId} target = {move.TargetPosition:F3}."));
    }

    private static SimulationManualControlOutcome ApplyManualRelativeMove(
        SimulationCommand command,
        MoveRelativeCommand move,
        SimulationManualControlContext context)
    {
        if (context.ControlOwner != SimulationControlOwner.Manual)
        {
            return SimulationManualControlCommandHandler.Reject(
                command,
                context,
                SimulationCommandErrorCode.ControlOwnerNotAllowed,
                $"Manual axis motion is unavailable while owner is {context.ControlOwner}.");
        }

        var axis = context.Axes.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, move.AxisId, StringComparison.Ordinal));
        if (axis is null)
        {
            return SimulationManualControlCommandHandler.Reject(
                command,
                context,
                SimulationCommandErrorCode.AxisNotFound,
                $"Axis '{move.AxisId}' was not found.");
        }

        var moveResult = axis.MoveRelative(move.Distance);
        if (!moveResult.IsAccepted)
        {
            return SimulationManualControlCommandHandler.Reject(
                command,
                context,
                MapAxisError(moveResult.ErrorCode),
                $"Axis relative move rejected: {moveResult.ErrorCode}.");
        }

        return SimulationManualControlCommandHandler.Accept(
            command,
            context,
            $"Axis '{move.AxisId}' relative move accepted.",
            new SimulationManualControlEvent(
                "Motion",
                "AxisRelativeMoveAccepted",
                $"{move.AxisId} distance = {move.Distance:F3}, target = {moveResult.RequestedTarget:F3}."));
    }

    private static SimulationManualControlOutcome ApplyManualGroupMove(
        SimulationCommand command,
        MoveAxesAbsoluteCommand move,
        SimulationManualControlContext context)
    {
        if (context.ControlOwner != SimulationControlOwner.Manual)
        {
            return SimulationManualControlCommandHandler.Reject(
                command,
                context,
                SimulationCommandErrorCode.ControlOwnerNotAllowed,
                $"Manual axis motion is unavailable while owner is {context.ControlOwner}.");
        }

        if (!TryResolveDistinctAxes(
                move.Targets.Select(target => target.AxisId),
                context.Axes,
                out var axes,
                out var error))
        {
            return SimulationManualControlCommandHandler.Reject(command, context, error.ErrorCode, error.Detail);
        }

        for (var index = 0; index < axes.Count; index++)
        {
            var validation = axes[index].ValidateAbsoluteMove(move.Targets[index].TargetPosition);
            if (!validation.IsAccepted)
            {
                return SimulationManualControlCommandHandler.Reject(
                    command,
                    context,
                    MapAxisError(validation.ErrorCode),
                    $"Axis '{axes[index].Id}' group move rejected: {validation.ErrorCode}.");
            }
        }

        for (var index = 0; index < axes.Count; index++)
        {
            axes[index].MoveAbsolute(move.Targets[index].TargetPosition);
        }

        var targets = string.Join(
            ", ",
            move.Targets.Select(target => FormattableString.Invariant(
                $"{target.AxisId} = {target.TargetPosition:F3}")));
        return SimulationManualControlCommandHandler.Accept(
            command,
            context,
            $"Coordinated move for {axes.Count} axes accepted.",
            new SimulationManualControlEvent(
                "Motion",
                "AxisGroupMoveAccepted",
                $"Targets: {targets}."));
    }

    private static SimulationManualControlOutcome ApplyManualVelocityMove(
        SimulationCommand command,
        MoveVelocityCommand move,
        SimulationManualControlContext context)
    {
        if (context.ControlOwner != SimulationControlOwner.Manual)
        {
            return SimulationManualControlCommandHandler.Reject(
                command,
                context,
                SimulationCommandErrorCode.ControlOwnerNotAllowed,
                $"Manual axis motion is unavailable while owner is {context.ControlOwner}.");
        }

        var axis = context.Axes.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, move.AxisId, StringComparison.Ordinal));
        if (axis is null)
        {
            return SimulationManualControlCommandHandler.Reject(
                command,
                context,
                SimulationCommandErrorCode.AxisNotFound,
                $"Axis '{move.AxisId}' was not found.");
        }

        var moveResult = axis.MoveVelocity(move.Velocity);
        if (!moveResult.IsAccepted)
        {
            return SimulationManualControlCommandHandler.Reject(
                command,
                context,
                MapAxisError(moveResult.ErrorCode),
                $"Axis velocity move rejected: {moveResult.ErrorCode}.");
        }

        return SimulationManualControlCommandHandler.Accept(
            command,
            context,
            $"Axis '{move.AxisId}' velocity move accepted.",
            new SimulationManualControlEvent(
                "Motion",
                "AxisVelocityMoveAccepted",
                $"{move.AxisId} velocity = {move.Velocity:F3}, limit = {moveResult.RequestedTarget:F3}."));
    }

    private static SimulationManualControlOutcome ApplyManualHome(
        SimulationCommand command,
        HomeAxisCommand home,
        SimulationManualControlContext context)
    {
        if (context.ControlOwner != SimulationControlOwner.Manual)
        {
            return SimulationManualControlCommandHandler.Reject(
                command,
                context,
                SimulationCommandErrorCode.ControlOwnerNotAllowed,
                $"Manual axis homing is unavailable while owner is {context.ControlOwner}.");
        }

        var axis = context.Axes.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, home.AxisId, StringComparison.Ordinal));
        if (axis is null)
        {
            return SimulationManualControlCommandHandler.Reject(
                command,
                context,
                SimulationCommandErrorCode.AxisNotFound,
                $"Axis '{home.AxisId}' was not found.");
        }

        var homeResult = axis.MoveAbsolute(axis.HomePosition);
        if (!homeResult.IsAccepted)
        {
            return SimulationManualControlCommandHandler.Reject(
                command,
                context,
                MapAxisError(homeResult.ErrorCode),
                $"Axis home rejected: {homeResult.ErrorCode}.");
        }

        return SimulationManualControlCommandHandler.Accept(
            command,
            context,
            $"Axis '{home.AxisId}' home accepted.",
            new SimulationManualControlEvent(
                "Motion",
                "AxisHomeAccepted",
                $"{home.AxisId} home = {axis.HomePosition:F3}."));
    }

    private static SimulationManualControlOutcome ApplyManualJog(
        SimulationCommand command,
        JogAxisCommand jog,
        SimulationManualControlContext context)
    {
        if (context.ControlOwner != SimulationControlOwner.Manual)
        {
            return SimulationManualControlCommandHandler.Reject(
                command,
                context,
                SimulationCommandErrorCode.ControlOwnerNotAllowed,
                $"Manual axis jog is unavailable while owner is {context.ControlOwner}.");
        }

        if (!Enum.IsDefined(jog.Direction))
        {
            return SimulationManualControlCommandHandler.Reject(
                command,
                context,
                SimulationCommandErrorCode.AxisTargetInvalid,
                "Axis jog direction is invalid.");
        }

        var axis = context.Axes.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, jog.AxisId, StringComparison.Ordinal));
        if (axis is null)
        {
            return SimulationManualControlCommandHandler.Reject(
                command,
                context,
                SimulationCommandErrorCode.AxisNotFound,
                $"Axis '{jog.AxisId}' was not found.");
        }

        var positive = jog.Direction == AxisJogDirection.Positive;
        var jogResult = axis.Jog(positive);
        if (!jogResult.IsAccepted)
        {
            return SimulationManualControlCommandHandler.Reject(
                command,
                context,
                MapAxisError(jogResult.ErrorCode),
                $"Axis jog rejected: {jogResult.ErrorCode}.");
        }

        return SimulationManualControlCommandHandler.Accept(
            command,
            context,
            $"Axis '{jog.AxisId}' jog {jog.Direction} accepted.",
            new SimulationManualControlEvent(
                "Motion",
                "AxisJogAccepted",
                $"{jog.AxisId} jog {jog.Direction} toward {jogResult.RequestedTarget:F3}."));
    }

    private static SimulationManualControlOutcome ApplyManualStop(
        SimulationCommand command,
        StopAxisCommand stop,
        SimulationManualControlContext context)
    {
        if (context.ControlOwner != SimulationControlOwner.Manual)
        {
            return SimulationManualControlCommandHandler.Reject(
                command,
                context,
                SimulationCommandErrorCode.ControlOwnerNotAllowed,
                $"Manual axis stop is unavailable while owner is {context.ControlOwner}.");
        }

        var axis = context.Axes.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, stop.AxisId, StringComparison.Ordinal));
        if (axis is null)
        {
            return SimulationManualControlCommandHandler.Reject(
                command,
                context,
                SimulationCommandErrorCode.AxisNotFound,
                $"Axis '{stop.AxisId}' was not found.");
        }

        axis.Stop();
        return SimulationManualControlCommandHandler.Accept(
            command,
            context,
            $"Axis '{stop.AxisId}' stopped.",
            new SimulationManualControlEvent(
                "Motion",
                "AxisStopAccepted",
                $"{stop.AxisId} stopped at {axis.Position:F3}."));
    }

    private static SimulationManualControlOutcome ApplyManualGroupStop(
        SimulationCommand command,
        StopAxesCommand stop,
        SimulationManualControlContext context)
    {
        if (context.ControlOwner != SimulationControlOwner.Manual)
        {
            return SimulationManualControlCommandHandler.Reject(
                command,
                context,
                SimulationCommandErrorCode.ControlOwnerNotAllowed,
                $"Manual axis stop is unavailable while owner is {context.ControlOwner}.");
        }

        if (!TryResolveDistinctAxes(stop.AxisIds, context.Axes, out var axes, out var error))
        {
            return SimulationManualControlCommandHandler.Reject(command, context, error.ErrorCode, error.Detail);
        }

        foreach (var axis in axes)
        {
            axis.Stop();
        }

        var positions = string.Join(
            ", ",
            axes.Select(axis => FormattableString.Invariant($"{axis.Id} = {axis.Position:F3}")));
        return SimulationManualControlCommandHandler.Accept(
            command,
            context,
            $"Coordinated stop for {axes.Count} axes accepted.",
            new SimulationManualControlEvent(
                "Motion",
                "AxisGroupStopAccepted",
                $"Stopped: {positions}."));
    }

    private static bool TryResolveDistinctAxes(
        IEnumerable<string> axisIds,
        IReadOnlyList<ServoAxisComponent> availableAxes,
        out IReadOnlyList<ServoAxisComponent> axes,
        out (SimulationCommandErrorCode ErrorCode, string Detail) error)
    {
        var candidates = new List<ServoAxisComponent>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var axisId in axisIds)
        {
            if (string.IsNullOrWhiteSpace(axisId))
            {
                axes = Array.Empty<ServoAxisComponent>();
                error = (SimulationCommandErrorCode.AxisGroupInvalid, "Every coordinated axis requires an id.");
                return false;
            }

            if (!ids.Add(axisId))
            {
                axes = Array.Empty<ServoAxisComponent>();
                error = (SimulationCommandErrorCode.AxisGroupInvalid, $"Axis id '{axisId}' is duplicated.");
                return false;
            }

            var axis = availableAxes.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, axisId, StringComparison.Ordinal));
            if (axis is null)
            {
                axes = Array.Empty<ServoAxisComponent>();
                error = (SimulationCommandErrorCode.AxisNotFound, $"Axis '{axisId}' was not found.");
                return false;
            }

            candidates.Add(axis);
        }

        if (candidates.Count == 0)
        {
            axes = Array.Empty<ServoAxisComponent>();
            error = (SimulationCommandErrorCode.AxisGroupInvalid, "At least one coordinated axis is required.");
            return false;
        }

        axes = candidates;
        error = (SimulationCommandErrorCode.None, string.Empty);
        return true;
    }

    private static SimulationCommandErrorCode MapAxisError(AxisCommandErrorCode errorCode) =>
        errorCode switch
        {
            AxisCommandErrorCode.InvalidTarget => SimulationCommandErrorCode.AxisTargetInvalid,
            AxisCommandErrorCode.TargetOutOfRange => SimulationCommandErrorCode.AxisTargetOutOfRange,
            AxisCommandErrorCode.InvalidVelocity => SimulationCommandErrorCode.AxisVelocityInvalid,
            AxisCommandErrorCode.VelocityOutOfRange => SimulationCommandErrorCode.AxisVelocityOutOfRange,
            AxisCommandErrorCode.AxisBusy => SimulationCommandErrorCode.AxisBusy,
            AxisCommandErrorCode.AxisInterlocked => SimulationCommandErrorCode.AxisInterlocked,
            _ => SimulationCommandErrorCode.AxisTargetInvalid
        };
}
