using OpenVisionLab.Machine.Core.Channels;
using OpenVisionLab.Machine.IO.Channels;
using OpenVisionLab.Machine.Simulation.Axis;

namespace OpenVisionLab.Machine.Simulation.Layout;

internal sealed class WaferHandlerRuntimeState
{
    private const double PositionTolerance = 0.001;
    private readonly DeterministicSignalHub _signalHub;
    private readonly WorkpieceRuntimeState _workpiece;
    private bool _previousPickCommand;
    private bool _previousPlaceCommand;
    private WaferHandlerSnapshot? _snapshot;

    public WaferHandlerRuntimeState(
        WaferHandlerRuntimeConfiguration configuration,
        DeterministicSignalHub signalHub,
        WorkpieceRuntimeState workpiece)
    {
        Configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _signalHub = signalHub ?? throw new ArgumentNullException(nameof(signalHub));
        _workpiece = workpiece ?? throw new ArgumentNullException(nameof(workpiece));
        _workpiece.AttachTransferOwner(Configuration.Id);
        Reset();
    }

    public WaferHandlerRuntimeConfiguration Configuration { get; }
    public WaferHandlerOwnershipState State =>
        _workpiece.TransferOwnershipState
        ?? throw new InvalidOperationException(
            $"Wafer-handler '{Configuration.Id}' workpiece has no ownership state.");

    public void Tick(IReadOnlyDictionary<string, AxisSnapshot> axes)
    {
        AxisSnapshot horizontal = axes[Configuration.HorizontalAxisId];
        AxisSnapshot vertical = axes[Configuration.VerticalAxisId];
        bool sourcePresent = Read(Configuration.SourcePresentSensorChannelId, ChannelKind.DigitalInput);
        bool gateOpen = Read(Configuration.GateOpenSensorChannelId, ChannelKind.DigitalInput);
        bool pick = Read(Configuration.PickCommandChannelId, ChannelKind.DigitalOutput);
        bool place = Read(Configuration.PlaceCommandChannelId, ChannelKind.DigitalOutput);
        bool pickAtPosition = At(horizontal.Position, Configuration.PickHorizontalPosition)
            && At(vertical.Position, Configuration.PickVerticalPosition);
        bool placeAtPosition = At(horizontal.Position, Configuration.PlaceHorizontalPosition)
            && At(vertical.Position, Configuration.PlaceVerticalPosition);
        bool pickPermitted = State == WaferHandlerOwnershipState.Source && sourcePresent && !gateOpen && pickAtPosition;
        bool placePermitted = State == WaferHandlerOwnershipState.Handler && gateOpen && placeAtPosition;

        if (State != WaferHandlerOwnershipState.InterlockFault)
        {
            bool pickRising = pick && !_previousPickCommand;
            bool placeRising = place && !_previousPlaceCommand;
            if ((pick && place) || (pickRising && !pickPermitted) || (placeRising && !placePermitted))
            {
                _workpiece.ApplyTransferTransition(WaferHandlerOwnershipState.InterlockFault);
            }
            else if (pickRising)
            {
                _workpiece.ApplyTransferTransition(WaferHandlerOwnershipState.Handler);
            }
            else if (placeRising)
            {
                _workpiece.ApplyTransferTransition(WaferHandlerOwnershipState.Destination);
            }
        }

        _previousPickCommand = pick;
        _previousPlaceCommand = place;
        WriteFeedback();
        _snapshot = new WaferHandlerSnapshot(
            Configuration.Id,
            Configuration.Name,
            State,
            Configuration.HorizontalAxisId,
            Configuration.VerticalAxisId,
            Configuration.WorkpieceComponentId,
            horizontal.Position,
            vertical.Position,
            sourcePresent,
            gateOpen,
            pickPermitted,
            placePermitted);
    }

    public void Reset()
    {
        _previousPickCommand = false;
        _previousPlaceCommand = false;
        _snapshot = null;
        WriteFeedback();
    }

    public WaferHandlerSnapshot CaptureSnapshot() => _snapshot ?? new(
        Configuration.Id,
        Configuration.Name,
        State,
        Configuration.HorizontalAxisId,
        Configuration.VerticalAxisId,
        Configuration.WorkpieceComponentId,
        0,
        0,
        false,
        false,
        false,
        false);

    private bool Read(string channelId, ChannelKind kind)
    {
        SignalReadResult read = _signalHub.ReadDigitalSignal(channelId);
        if (!read.IsAccepted || read.Kind != kind)
        {
            throw new InvalidOperationException($"Wafer-handler '{Configuration.Id}' could not read {kind} '{channelId}'.");
        }
        return read.Value == true;
    }

    private void WriteFeedback()
    {
        bool healthy = State != WaferHandlerOwnershipState.InterlockFault;
        Write(Configuration.HoldingFeedbackChannelId, healthy && State == WaferHandlerOwnershipState.Handler);
        Write(Configuration.PlacedFeedbackChannelId, healthy && State == WaferHandlerOwnershipState.Destination);
    }

    private void Write(string channelId, bool value)
    {
        SignalWriteResult write = _signalHub.SetDigitalInput(channelId, value, SignalWriteOwner.SimulationComponent);
        if (!write.IsAccepted)
        {
            throw new InvalidOperationException($"Wafer-handler '{Configuration.Id}' could not write feedback '{channelId}': {write.ErrorCode}.");
        }
    }

    private static bool At(double actual, double expected) => Math.Abs(actual - expected) <= PositionTolerance;
}
