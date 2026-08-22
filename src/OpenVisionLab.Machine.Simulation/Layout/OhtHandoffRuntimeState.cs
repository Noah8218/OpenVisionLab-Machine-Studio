using OpenVisionLab.Machine.Core.Channels;
using OpenVisionLab.Machine.IO.Channels;

namespace OpenVisionLab.Machine.Simulation.Layout;

internal sealed class OhtHandoffRuntimeState
{
    private readonly DeterministicSignalHub _signalHub;
    private bool _previousForwardCommand;
    private OhtHandoffSnapshot? _snapshot;

    public OhtHandoffRuntimeState(
        OhtHandoffRuntimeConfiguration configuration,
        DeterministicSignalHub signalHub)
    {
        Configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _signalHub = signalHub ?? throw new ArgumentNullException(nameof(signalHub));
        Reset();
    }

    public OhtHandoffRuntimeConfiguration Configuration { get; }
    public OhtHandoffOwnershipState State { get; private set; }
    public bool AllowForwardMotion => State is OhtHandoffOwnershipState.Transferring or OhtHandoffOwnershipState.LoadPort;

    public void Tick()
    {
        bool routeAvailable = Read(Configuration.RouteAvailableSensorChannelId, ChannelKind.DigitalInput);
        bool vehicleDocked = Read(Configuration.VehicleDockedSensorChannelId, ChannelKind.DigitalInput);
        bool loadPortReady = Read(Configuration.LoadPortReadySensorChannelId, ChannelKind.DigitalInput);
        bool carrierReceived = Read(Configuration.CarrierReceivedSensorChannelId, ChannelKind.DigitalInput);
        bool forward = Read(Configuration.ForwardCommandChannelId, ChannelKind.DigitalOutput);
        bool reverse = Read(Configuration.ReverseCommandChannelId, ChannelKind.DigitalOutput);
        bool forwardRising = forward && !_previousForwardCommand;
        bool conditionsReady = routeAvailable && vehicleDocked && loadPortReady;

        if (State != OhtHandoffOwnershipState.InterlockFault)
        {
            switch (State)
            {
                case OhtHandoffOwnershipState.Vehicle:
                    if (forward || reverse)
                    {
                        EnterFault();
                    }
                    else if (conditionsReady)
                    {
                        State = OhtHandoffOwnershipState.Ready;
                    }
                    break;
                case OhtHandoffOwnershipState.Ready:
                    if (reverse || (forward && !conditionsReady))
                    {
                        EnterFault();
                    }
                    else if (!conditionsReady)
                    {
                        State = OhtHandoffOwnershipState.Vehicle;
                    }
                    else if (forwardRising)
                    {
                        State = OhtHandoffOwnershipState.Transferring;
                    }
                    break;
                case OhtHandoffOwnershipState.Transferring:
                    if (reverse || !conditionsReady)
                    {
                        EnterFault();
                    }
                    else if (carrierReceived)
                    {
                        State = OhtHandoffOwnershipState.LoadPort;
                    }
                    else if (!forward)
                    {
                        EnterFault();
                    }
                    break;
                case OhtHandoffOwnershipState.LoadPort:
                    if (reverse)
                    {
                        EnterFault();
                    }
                    break;
            }
        }

        _previousForwardCommand = forward;
        WriteFeedback();
        _snapshot = new OhtHandoffSnapshot(
            Configuration.Id,
            Configuration.Name,
            State,
            Configuration.TransportConveyorComponentId,
            routeAvailable,
            vehicleDocked,
            loadPortReady,
            carrierReceived,
            forward,
            reverse,
            State == OhtHandoffOwnershipState.Ready);
    }

    public void Reset()
    {
        State = OhtHandoffOwnershipState.Vehicle;
        _previousForwardCommand = false;
        _snapshot = null;
        WriteFeedback();
    }

    public OhtHandoffSnapshot CaptureSnapshot() => _snapshot ?? new(
        Configuration.Id,
        Configuration.Name,
        State,
        Configuration.TransportConveyorComponentId,
        false,
        false,
        false,
        false,
        false,
        false,
        false);

    private bool Read(string channelId, ChannelKind expectedKind)
    {
        SignalReadResult read = _signalHub.ReadDigitalSignal(channelId);
        if (!read.IsAccepted || read.Kind != expectedKind)
        {
            throw new InvalidOperationException(
                $"OHT handoff '{Configuration.Id}' could not read {expectedKind} '{channelId}'.");
        }
        return read.Value == true;
    }

    private void WriteFeedback()
    {
        bool healthy = State != OhtHandoffOwnershipState.InterlockFault;
        Write(Configuration.HandoffReadyFeedbackChannelId, healthy && State == OhtHandoffOwnershipState.Ready);
        Write(Configuration.CarrierTransferredFeedbackChannelId, healthy && State == OhtHandoffOwnershipState.LoadPort);
    }

    private void Write(string channelId, bool value)
    {
        SignalWriteResult write = _signalHub.SetDigitalInput(channelId, value, SignalWriteOwner.SimulationComponent);
        if (!write.IsAccepted)
        {
            throw new InvalidOperationException(
                $"OHT handoff '{Configuration.Id}' could not write feedback '{channelId}': {write.ErrorCode}.");
        }
    }

    private void EnterFault() => State = OhtHandoffOwnershipState.InterlockFault;
}
