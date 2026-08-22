using OpenVisionLab.Machine.Core.Channels;
using OpenVisionLab.Machine.IO.Channels;
using OpenVisionLab.Machine.Simulation.Axis;

namespace OpenVisionLab.Machine.Simulation.Layout;

internal sealed class PrealignerRuntimeState
{
    private readonly DeterministicSignalHub _signalHub;
    private bool _previousAlignmentAccepted;
    private PrealignerSnapshot? _snapshot;

    public PrealignerRuntimeState(
        PrealignerRuntimeConfiguration configuration,
        DeterministicSignalHub signalHub)
    {
        Configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _signalHub = signalHub ?? throw new ArgumentNullException(nameof(signalHub));
        Reset();
    }

    public PrealignerRuntimeConfiguration Configuration { get; }
    public PrealignerState State { get; private set; }

    public void Tick(AxisSnapshot rotaryAxis, PneumaticCylinderState clampState)
    {
        bool waferPresent = ReadSignal(Configuration.WaferPresentSensorChannelId, ChannelKind.DigitalInput);
        bool alignmentAccepted = ReadSignal(Configuration.AlignmentAcceptedCommandChannelId, ChannelKind.DigitalOutput);

        if (State != PrealignerState.InterlockFault)
        {
            if (rotaryAxis.State is AxisState.Error or AxisState.Limited or AxisState.Stopped)
            {
                EnterFault();
            }
            else
            {
                Advance(rotaryAxis, clampState, waferPresent, alignmentAccepted);
            }
        }

        _previousAlignmentAccepted = alignmentAccepted;
        WriteFeedback();
        _snapshot = new PrealignerSnapshot(
            Configuration.Id,
            Configuration.Name,
            State,
            Configuration.RotaryStageComponentId,
            Configuration.RotaryAxisId,
            Configuration.ClampCylinderComponentId,
            Configuration.AlignmentTargetDegrees,
            Configuration.AlignmentToleranceDegrees,
            rotaryAxis.Position,
            waferPresent,
            clampState,
            alignmentAccepted,
            State == PrealignerState.Ready,
            State is PrealignerState.Aligned or PrealignerState.Released);
    }

    public void Reset()
    {
        State = PrealignerState.AwaitingWafer;
        _previousAlignmentAccepted = false;
        _snapshot = null;
        WriteFeedback();
    }

    public PrealignerSnapshot CaptureSnapshot() => _snapshot ?? new(
        Configuration.Id,
        Configuration.Name,
        State,
        Configuration.RotaryStageComponentId,
        Configuration.RotaryAxisId,
        Configuration.ClampCylinderComponentId,
        Configuration.AlignmentTargetDegrees,
        Configuration.AlignmentToleranceDegrees,
        0,
        false,
        PneumaticCylinderState.Retracted,
        false,
        false,
        false);

    private void Advance(
        AxisSnapshot rotaryAxis,
        PneumaticCylinderState clampState,
        bool waferPresent,
        bool alignmentAccepted)
    {
        switch (State)
        {
            case PrealignerState.AwaitingWafer:
                if (alignmentAccepted || rotaryAxis.State == AxisState.Moving)
                {
                    EnterFault();
                }
                else if (waferPresent)
                {
                    State = clampState == PneumaticCylinderState.Extended
                        ? PrealignerState.Ready
                        : PrealignerState.AwaitingClamp;
                }
                break;

            case PrealignerState.AwaitingClamp:
                if (alignmentAccepted || rotaryAxis.State == AxisState.Moving)
                {
                    EnterFault();
                }
                else if (!waferPresent)
                {
                    State = PrealignerState.AwaitingWafer;
                }
                else if (clampState == PneumaticCylinderState.Extended)
                {
                    State = PrealignerState.Ready;
                }
                else if (clampState == PneumaticCylinderState.Fault)
                {
                    EnterFault();
                }
                break;

            case PrealignerState.Ready:
                if (alignmentAccepted || !waferPresent || clampState != PneumaticCylinderState.Extended)
                {
                    EnterFault();
                }
                else if (rotaryAxis.State == AxisState.Moving)
                {
                    State = PrealignerState.Aligning;
                }
                break;

            case PrealignerState.Aligning:
                if (!waferPresent || clampState != PneumaticCylinderState.Extended)
                {
                    EnterFault();
                }
                else if (alignmentAccepted && !_previousAlignmentAccepted)
                {
                    State = IsAtTarget(rotaryAxis)
                        ? PrealignerState.Aligned
                        : PrealignerState.InterlockFault;
                }
                else if (rotaryAxis.State == AxisState.Idle && !IsAtTarget(rotaryAxis))
                {
                    EnterFault();
                }
                break;

            case PrealignerState.Aligned:
                if (rotaryAxis.State == AxisState.Moving
                    || (!waferPresent && clampState != PneumaticCylinderState.Retracted)
                    || clampState == PneumaticCylinderState.Fault)
                {
                    EnterFault();
                }
                else if (clampState == PneumaticCylinderState.Retracted)
                {
                    State = PrealignerState.Released;
                }
                break;

            case PrealignerState.Released:
                if (rotaryAxis.State == AxisState.Moving)
                {
                    EnterFault();
                }
                break;
        }
    }

    private bool IsAtTarget(AxisSnapshot axis) =>
        axis.State == AxisState.Idle
        && Math.Abs(axis.Position - Configuration.AlignmentTargetDegrees)
            <= Configuration.AlignmentToleranceDegrees;

    private bool ReadSignal(string channelId, ChannelKind expectedKind)
    {
        SignalReadResult read = _signalHub.ReadDigitalSignal(channelId);
        if (!read.IsAccepted || read.Kind != expectedKind)
        {
            throw new InvalidOperationException(
                $"Pre-aligner '{Configuration.Id}' could not read {expectedKind} channel '{channelId}'.");
        }
        return read.Value == true;
    }

    private void WriteFeedback()
    {
        bool healthy = State != PrealignerState.InterlockFault;
        WriteFeedback(Configuration.AlignmentReadyFeedbackChannelId, healthy && State == PrealignerState.Ready);
        WriteFeedback(
            Configuration.AlignmentCompleteFeedbackChannelId,
            healthy && State is PrealignerState.Aligned or PrealignerState.Released);
    }

    private void WriteFeedback(string channelId, bool value)
    {
        SignalWriteResult write = _signalHub.SetDigitalInput(channelId, value, SignalWriteOwner.SimulationComponent);
        if (!write.IsAccepted)
        {
            throw new InvalidOperationException(
                $"Pre-aligner '{Configuration.Id}' could not write feedback '{channelId}': {write.ErrorCode}.");
        }
    }

    private void EnterFault() => State = PrealignerState.InterlockFault;
}
