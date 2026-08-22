using OpenVisionLab.Machine.Core.Channels;
using OpenVisionLab.Machine.Core.Devices;
using OpenVisionLab.Machine.IO.Channels;
using OpenVisionLab.Machine.Simulation.Camera;

namespace OpenVisionLab.Machine.Simulation.Layout;

internal sealed class InspectionHandoffRuntimeState
{
    private readonly DeterministicSignalHub _signalHub;
    private bool _previousResultAccepted;
    private long _baselineAcquisitionOrdinal;
    private long? _activeAcquisitionOrdinal;
    private PlaceholderInspectionDecision? _decision;
    private InspectionHandoffSnapshot? _snapshot;

    public InspectionHandoffRuntimeState(
        InspectionHandoffRuntimeConfiguration configuration,
        DeterministicSignalHub signalHub)
    {
        Configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _signalHub = signalHub ?? throw new ArgumentNullException(nameof(signalHub));
        Reset();
    }

    public InspectionHandoffRuntimeConfiguration Configuration { get; }
    public InspectionHandoffState State { get; private set; }

    public void Tick(VirtualCameraSnapshot camera)
    {
        bool materialPresent = ReadSignal(Configuration.InspectionPositionSensorChannelId, ChannelKind.DigitalInput);
        bool resultAccepted = ReadSignal(Configuration.ResultAcceptedCommandChannelId, ChannelKind.DigitalOutput);

        if (State != InspectionHandoffState.InterlockFault)
        {
            if (camera.State == VirtualCameraState.Faulted)
            {
                EnterFault();
            }
            else
            {
                Advance(camera, materialPresent, resultAccepted);
            }
        }

        _previousResultAccepted = resultAccepted;
        WriteFeedback();
        _snapshot = new InspectionHandoffSnapshot(
            Configuration.Id,
            Configuration.Name,
            State,
            Configuration.CameraId,
            _decision,
            camera.AcquisitionOrdinal,
            materialPresent,
            resultAccepted,
            State == InspectionHandoffState.Ready,
            State == InspectionHandoffState.Complete);
    }

    public void Reset()
    {
        State = InspectionHandoffState.AwaitingMaterial;
        _previousResultAccepted = false;
        _baselineAcquisitionOrdinal = 0;
        _activeAcquisitionOrdinal = null;
        _decision = null;
        _snapshot = null;
        WriteFeedback();
    }

    public InspectionHandoffSnapshot CaptureSnapshot() => _snapshot ?? new(
        Configuration.Id,
        Configuration.Name,
        State,
        Configuration.CameraId,
        _decision,
        _baselineAcquisitionOrdinal,
        false,
        false,
        false,
        false);

    private void Advance(
        VirtualCameraSnapshot camera,
        bool materialPresent,
        bool resultAccepted)
    {
        switch (State)
        {
            case InspectionHandoffState.AwaitingMaterial:
                if (resultAccepted
                    || camera.State is VirtualCameraState.Exposing or VirtualCameraState.Transferring
                    || camera.AcquisitionOrdinal > _baselineAcquisitionOrdinal)
                {
                    EnterFault();
                }
                else if (materialPresent)
                {
                    _baselineAcquisitionOrdinal = camera.AcquisitionOrdinal;
                    State = InspectionHandoffState.Ready;
                }
                break;

            case InspectionHandoffState.Ready:
                if (resultAccepted)
                {
                    EnterFault();
                }
                else if (!materialPresent)
                {
                    _baselineAcquisitionOrdinal = camera.AcquisitionOrdinal;
                    State = InspectionHandoffState.AwaitingMaterial;
                }
                else if (camera.AcquisitionOrdinal > _baselineAcquisitionOrdinal)
                {
                    _activeAcquisitionOrdinal = camera.AcquisitionOrdinal;
                    if (camera.Result is { } result
                        && result.AcquisitionOrdinal == _activeAcquisitionOrdinal)
                    {
                        _decision = result.Decision;
                        State = InspectionHandoffState.ResultAvailable;
                    }
                    else if (camera.State is VirtualCameraState.Exposing or VirtualCameraState.Transferring)
                    {
                        State = InspectionHandoffState.Inspecting;
                    }
                    else
                    {
                        EnterFault();
                    }
                }
                break;

            case InspectionHandoffState.Inspecting:
                if (resultAccepted
                    || !materialPresent
                    || camera.AcquisitionOrdinal != _activeAcquisitionOrdinal)
                {
                    EnterFault();
                }
                else if (camera.Result is { } result
                         && result.AcquisitionOrdinal == _activeAcquisitionOrdinal)
                {
                    _decision = result.Decision;
                    State = InspectionHandoffState.ResultAvailable;
                }
                else if (camera.State is not (VirtualCameraState.Exposing or VirtualCameraState.Transferring))
                {
                    EnterFault();
                }
                break;

            case InspectionHandoffState.ResultAvailable:
                if (!materialPresent
                    || camera.AcquisitionOrdinal != _activeAcquisitionOrdinal)
                {
                    EnterFault();
                }
                else if (resultAccepted && !_previousResultAccepted)
                {
                    State = InspectionHandoffState.Complete;
                }
                break;

            case InspectionHandoffState.Complete:
                if (camera.AcquisitionOrdinal != _activeAcquisitionOrdinal)
                {
                    EnterFault();
                }
                else if (!materialPresent)
                {
                    _baselineAcquisitionOrdinal = camera.AcquisitionOrdinal;
                    _activeAcquisitionOrdinal = null;
                    _decision = null;
                    State = InspectionHandoffState.AwaitingMaterial;
                }
                break;
        }
    }

    private bool ReadSignal(string channelId, ChannelKind expectedKind)
    {
        SignalReadResult read = _signalHub.ReadDigitalSignal(channelId);
        if (!read.IsAccepted || read.Kind != expectedKind)
        {
            throw new InvalidOperationException(
                $"Inspection handoff '{Configuration.Id}' could not read {expectedKind} channel '{channelId}'.");
        }
        return read.Value == true;
    }

    private void WriteFeedback()
    {
        bool healthy = State != InspectionHandoffState.InterlockFault;
        WriteFeedback(Configuration.InspectionReadyFeedbackChannelId, healthy && State == InspectionHandoffState.Ready);
        WriteFeedback(Configuration.InspectionCompleteFeedbackChannelId, healthy && State == InspectionHandoffState.Complete);
    }

    private void WriteFeedback(string channelId, bool value)
    {
        SignalWriteResult write = _signalHub.SetDigitalInput(channelId, value, SignalWriteOwner.SimulationComponent);
        if (!write.IsAccepted)
        {
            throw new InvalidOperationException(
                $"Inspection handoff '{Configuration.Id}' could not write feedback '{channelId}': {write.ErrorCode}.");
        }
    }

    private void EnterFault()
    {
        State = InspectionHandoffState.InterlockFault;
    }
}
