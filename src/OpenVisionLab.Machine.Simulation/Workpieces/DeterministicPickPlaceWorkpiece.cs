namespace OpenVisionLab.Machine.Simulation.Workpieces;

public enum PickPlaceWorkpieceState
{
    Available,
    Attached,
    Placed
}

public sealed record PickPlaceWorkpieceRuntimeConfiguration(
    string Id,
    string Name,
    string XAxisId,
    string YAxisId,
    string GripperSignalId,
    double PickX,
    double PickY);

public sealed record PickPlaceWorkpieceSnapshot(
    string Id,
    string Name,
    PickPlaceWorkpieceState State,
    double X,
    double Y);

public sealed record PickPlaceWorkpieceTransition(
    PickPlaceWorkpieceState PreviousState,
    PickPlaceWorkpieceState CurrentState,
    double X,
    double Y);

internal sealed class DeterministicPickPlaceWorkpiece
{
    private readonly PickPlaceWorkpieceRuntimeConfiguration _configuration;
    private PickPlaceWorkpieceState _state;
    private double _x;
    private double _y;

    public DeterministicPickPlaceWorkpiece(PickPlaceWorkpieceRuntimeConfiguration configuration)
    {
        _configuration = configuration;
        Reset();
    }

    public string XAxisId => _configuration.XAxisId;
    public string YAxisId => _configuration.YAxisId;
    public string GripperSignalId => _configuration.GripperSignalId;

    public PickPlaceWorkpieceTransition? Tick(double axisX, double axisY, bool gripperClosed)
    {
        var previous = _state;
        if (_state == PickPlaceWorkpieceState.Available &&
            gripperClosed &&
            Near(axisX, _configuration.PickX) &&
            Near(axisY, _configuration.PickY))
        {
            _state = PickPlaceWorkpieceState.Attached;
            _x = axisX;
            _y = axisY;
        }
        else if (_state == PickPlaceWorkpieceState.Attached)
        {
            _x = axisX;
            _y = axisY;
            if (!gripperClosed)
            {
                _state = PickPlaceWorkpieceState.Placed;
            }
        }

        return previous == _state
            ? null
            : new PickPlaceWorkpieceTransition(previous, _state, _x, _y);
    }

    public void Reset()
    {
        _state = PickPlaceWorkpieceState.Available;
        _x = _configuration.PickX;
        _y = _configuration.PickY;
    }

    public PickPlaceWorkpieceSnapshot CaptureSnapshot() =>
        new(_configuration.Id, _configuration.Name, _state, _x, _y);

    private static bool Near(double actual, double expected) => Math.Abs(actual - expected) <= 1e-9;
}
