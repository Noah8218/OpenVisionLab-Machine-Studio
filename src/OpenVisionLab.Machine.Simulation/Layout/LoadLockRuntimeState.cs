using OpenVisionLab.Machine.Core.Channels;
using OpenVisionLab.Machine.IO.Channels;

namespace OpenVisionLab.Machine.Simulation.Layout;

internal sealed class LoadLockRuntimeState
{
    private readonly DeterministicSignalHub _signalHub;

    public LoadLockRuntimeState(
        LoadLockRuntimeConfiguration configuration,
        DeterministicSignalHub signalHub)
    {
        Configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _signalHub = signalHub ?? throw new ArgumentNullException(nameof(signalHub));
        Reset();
    }

    public LoadLockRuntimeConfiguration Configuration { get; }
    public LoadLockState State { get; private set; }
    public int RemainingTransitionTicks { get; private set; }
    public bool AllowOuterDoorExtension { get; private set; }
    public bool AllowInnerDoorExtension { get; private set; }

    public void Tick(
        PneumaticCylinderState outerDoorState,
        PneumaticCylinderState innerDoorState,
        bool outerDoorRequested,
        bool innerDoorRequested)
    {
        bool evacuateRequested = ReadCommand(Configuration.EvacuateCommandChannelId);
        bool ventRequested = ReadCommand(Configuration.VentCommandChannelId);
        bool bothDoorsClosed = outerDoorState == PneumaticCylinderState.Retracted
            && innerDoorState == PneumaticCylinderState.Retracted;

        AllowOuterDoorExtension = false;
        AllowInnerDoorExtension = false;

        if (State == LoadLockState.InterlockFault)
        {
            WriteFeedback();
            return;
        }

        if ((outerDoorRequested && innerDoorRequested) || (evacuateRequested && ventRequested))
        {
            EnterFault();
            WriteFeedback();
            return;
        }

        switch (State)
        {
            case LoadLockState.Atmosphere:
                if (innerDoorRequested || (evacuateRequested && !bothDoorsClosed))
                {
                    EnterFault();
                    break;
                }

                AllowOuterDoorExtension = outerDoorRequested;
                if (evacuateRequested)
                {
                    State = LoadLockState.PumpingDown;
                    RemainingTransitionTicks = Configuration.PumpDownDurationTicks;
                }
                break;

            case LoadLockState.PumpingDown:
                if (!evacuateRequested || ventRequested || !bothDoorsClosed
                    || outerDoorRequested || innerDoorRequested)
                {
                    EnterFault();
                    break;
                }

                if (--RemainingTransitionTicks <= 0)
                {
                    State = LoadLockState.Vacuum;
                    RemainingTransitionTicks = 0;
                }
                break;

            case LoadLockState.Vacuum:
                if (outerDoorRequested || (ventRequested && !bothDoorsClosed))
                {
                    EnterFault();
                    break;
                }

                AllowInnerDoorExtension = innerDoorRequested;
                if (ventRequested)
                {
                    State = LoadLockState.Venting;
                    RemainingTransitionTicks = Configuration.VentDurationTicks;
                    AllowInnerDoorExtension = false;
                }
                break;

            case LoadLockState.Venting:
                if (!ventRequested || evacuateRequested || !bothDoorsClosed
                    || outerDoorRequested || innerDoorRequested)
                {
                    EnterFault();
                    break;
                }

                if (--RemainingTransitionTicks <= 0)
                {
                    State = LoadLockState.Atmosphere;
                    RemainingTransitionTicks = 0;
                }
                break;
        }

        WriteFeedback();
    }

    public void Reset()
    {
        State = LoadLockState.Atmosphere;
        RemainingTransitionTicks = 0;
        AllowOuterDoorExtension = false;
        AllowInnerDoorExtension = false;
        WriteFeedback();
    }

    public LoadLockSnapshot CaptureSnapshot() => new(
        Configuration.Id,
        Configuration.Name,
        State,
        RemainingTransitionTicks,
        State == LoadLockState.Vacuum,
        State == LoadLockState.Atmosphere,
        State == LoadLockState.Atmosphere,
        State == LoadLockState.Vacuum,
        Configuration.OuterDoorComponentId,
        Configuration.InnerDoorComponentId);

    private bool ReadCommand(string channelId)
    {
        SignalReadResult read = _signalHub.ReadDigitalSignal(channelId);
        if (!read.IsAccepted || read.Kind != ChannelKind.DigitalOutput)
        {
            throw new InvalidOperationException(
                $"Load-lock '{Configuration.Id}' could not read command '{channelId}'.");
        }

        return read.Value == true;
    }

    private void WriteFeedback()
    {
        WriteFeedback(Configuration.VacuumReadySensorChannelId, State == LoadLockState.Vacuum);
        WriteFeedback(Configuration.AtmosphereReadySensorChannelId, State == LoadLockState.Atmosphere);
    }

    private void WriteFeedback(string channelId, bool value)
    {
        SignalWriteResult write = _signalHub.SetDigitalInput(
            channelId,
            value,
            SignalWriteOwner.SimulationComponent);
        if (!write.IsAccepted)
        {
            throw new InvalidOperationException(
                $"Load-lock '{Configuration.Id}' could not write feedback '{channelId}': {write.ErrorCode}.");
        }
    }

    private void EnterFault()
    {
        State = LoadLockState.InterlockFault;
        RemainingTransitionTicks = 0;
        AllowOuterDoorExtension = false;
        AllowInnerDoorExtension = false;
    }
}
