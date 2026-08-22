using OpenVisionLab.Machine.Core.Channels;
using OpenVisionLab.Machine.Core.Devices;
using OpenVisionLab.Machine.IO.Channels;
using OpenVisionLab.Machine.Simulation.Camera;

namespace OpenVisionLab.Machine.Simulation.Layout;

internal sealed class InspectionSortRouterRuntimeState
{
    private readonly DeterministicSignalHub _signalHub;
    private bool _previousPassRun;
    private bool _previousNgRun;
    private PlaceholderInspectionDecision? _decision;
    private InspectionSortRouterSnapshot? _snapshot;

    public InspectionSortRouterRuntimeState(
        InspectionSortRouterRuntimeConfiguration configuration,
        DeterministicSignalHub signalHub)
    {
        Configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _signalHub = signalHub ?? throw new ArgumentNullException(nameof(signalHub));
        Reset();
    }

    public InspectionSortRouterRuntimeConfiguration Configuration { get; }
    public InspectionSortRouteState State { get; private set; }

    public void Tick(VirtualCameraSnapshot camera)
    {
        bool passRun = ReadCommand(Configuration.PassRunCommandChannelId);
        bool ngRun = ReadCommand(Configuration.NgRunCommandChannelId);

        if (State != InspectionSortRouteState.InterlockFault)
        {
            if (State == InspectionSortRouteState.AwaitingDecision && camera.Result is { } result)
            {
                _decision = result.Decision;
                if (passRun || ngRun)
                {
                    EnterFault();
                }
                else
                {
                    State = _decision == PlaceholderInspectionDecision.Pass
                        ? InspectionSortRouteState.PassReady
                        : InspectionSortRouteState.NgReady;
                }
            }
            else if (State is InspectionSortRouteState.PassReady or InspectionSortRouteState.NgReady)
            {
                bool passRising = passRun && !_previousPassRun;
                bool ngRising = ngRun && !_previousNgRun;
                if ((passRun && ngRun)
                    || (State == InspectionSortRouteState.PassReady && ngRun)
                    || (State == InspectionSortRouteState.NgReady && passRun))
                {
                    EnterFault();
                }
                else if (passRising)
                {
                    State = InspectionSortRouteState.PassRouted;
                }
                else if (ngRising)
                {
                    State = InspectionSortRouteState.NgRouted;
                }
            }
            else if ((State == InspectionSortRouteState.PassRouted && ngRun)
                     || (State == InspectionSortRouteState.NgRouted && passRun)
                     || (passRun && ngRun))
            {
                EnterFault();
            }
        }

        _previousPassRun = passRun;
        _previousNgRun = ngRun;
        WriteFeedback();
        _snapshot = new InspectionSortRouterSnapshot(
            Configuration.Id,
            Configuration.Name,
            State,
            Configuration.CameraId,
            _decision,
            Configuration.PassConveyorComponentId,
            Configuration.NgConveyorComponentId,
            passRun,
            ngRun,
            State == InspectionSortRouteState.PassReady,
            State == InspectionSortRouteState.NgReady);
    }

    public void Reset()
    {
        State = InspectionSortRouteState.AwaitingDecision;
        _decision = null;
        _previousPassRun = false;
        _previousNgRun = false;
        _snapshot = null;
        WriteFeedback();
    }

    public InspectionSortRouterSnapshot CaptureSnapshot() => _snapshot ?? new(
        Configuration.Id,
        Configuration.Name,
        State,
        Configuration.CameraId,
        _decision,
        Configuration.PassConveyorComponentId,
        Configuration.NgConveyorComponentId,
        false,
        false,
        false,
        false);

    private bool ReadCommand(string channelId)
    {
        SignalReadResult read = _signalHub.ReadDigitalSignal(channelId);
        if (!read.IsAccepted || read.Kind != ChannelKind.DigitalOutput)
        {
            throw new InvalidOperationException($"Inspection sorter '{Configuration.Id}' could not read route command '{channelId}'.");
        }
        return read.Value == true;
    }

    private void WriteFeedback()
    {
        bool healthy = State != InspectionSortRouteState.InterlockFault;
        WriteFeedback(Configuration.PassRoutedFeedbackChannelId, healthy && State == InspectionSortRouteState.PassRouted);
        WriteFeedback(Configuration.NgRoutedFeedbackChannelId, healthy && State == InspectionSortRouteState.NgRouted);
    }

    private void WriteFeedback(string channelId, bool value)
    {
        SignalWriteResult write = _signalHub.SetDigitalInput(channelId, value, SignalWriteOwner.SimulationComponent);
        if (!write.IsAccepted)
        {
            throw new InvalidOperationException($"Inspection sorter '{Configuration.Id}' could not write feedback '{channelId}': {write.ErrorCode}.");
        }
    }

    private void EnterFault()
    {
        State = InspectionSortRouteState.InterlockFault;
    }
}
