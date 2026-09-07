using OpenVisionLab.Machine.Core.Channels;
using OpenVisionLab.Machine.IO.Channels;

namespace OpenVisionLab.Machine.Simulation.Layout;

/// <summary>
/// Validates the digital signal channels required by each runtime layout device.
/// </summary>
internal sealed class MachineLayoutSignalBindingValidator
{
    private readonly DeterministicSignalHub _signalHub;

    public MachineLayoutSignalBindingValidator(DeterministicSignalHub signalHub)
    {
        _signalHub = signalHub ?? throw new ArgumentNullException(nameof(signalHub));
    }

    public void Validate(
        IReadOnlyList<DigitalSensorRuntimeState> sensors,
        IReadOnlyList<PneumaticCylinderRuntimeState> cylinders,
        IReadOnlyList<ConveyorRuntimeState> conveyors,
        IReadOnlyList<LoadLockRuntimeState> loadLocks,
        IReadOnlyList<WaferHandlerRuntimeState> waferHandlers,
        IReadOnlyList<InspectionSortRouterRuntimeState> inspectionSortRouters,
        IReadOnlyList<InspectionHandoffRuntimeState> inspectionHandoffs,
        IReadOnlyList<OhtHandoffRuntimeState> ohtHandoffs,
        IReadOnlyList<PrealignerRuntimeState> prealigners)
    {
        foreach (var sensor in sensors)
        {
            SignalReadResult read = _signalHub.ReadDigitalSignal(
                sensor.SensorConfiguration.OutputChannelId);
            if (!read.IsAccepted || read.Kind != ChannelKind.DigitalInput)
            {
                throw new ArgumentException(
                    $"Sensor '{sensor.Configuration.Id}' output '{sensor.SensorConfiguration.OutputChannelId}' " +
                    "must identify a configured DigitalInput channel.",
                    nameof(_signalHub));
            }
        }

        foreach (var cylinder in cylinders)
        {
            ValidateCylinderSignal(
                cylinder,
                cylinder.CylinderConfiguration.ExtendCommandChannelId,
                ChannelKind.DigitalOutput,
                "extend command");
            ValidateCylinderSignal(
                cylinder,
                cylinder.CylinderConfiguration.ExtendedSensorChannelId,
                ChannelKind.DigitalInput,
                "extended sensor");
            ValidateCylinderSignal(
                cylinder,
                cylinder.CylinderConfiguration.RetractedSensorChannelId,
                ChannelKind.DigitalInput,
                "retracted sensor");
        }

        foreach (var conveyor in conveyors)
        {
            ValidateConveyorSignal(
                conveyor,
                conveyor.ConveyorConfiguration.RunCommandChannelId,
                "run command");
            ValidateConveyorSignal(
                conveyor,
                conveyor.ConveyorConfiguration.ReverseCommandChannelId,
                "reverse command");
        }

        foreach (var loadLock in loadLocks)
        {
            ValidateLoadLockSignal(
                loadLock,
                loadLock.Configuration.EvacuateCommandChannelId,
                ChannelKind.DigitalOutput,
                "evacuate command");
            ValidateLoadLockSignal(
                loadLock,
                loadLock.Configuration.VentCommandChannelId,
                ChannelKind.DigitalOutput,
                "vent command");
            ValidateLoadLockSignal(
                loadLock,
                loadLock.Configuration.VacuumReadySensorChannelId,
                ChannelKind.DigitalInput,
                "vacuum-ready sensor");
            ValidateLoadLockSignal(
                loadLock,
                loadLock.Configuration.AtmosphereReadySensorChannelId,
                ChannelKind.DigitalInput,
                "atmosphere-ready sensor");
        }

        foreach (var handler in waferHandlers)
        {
            ValidateWaferHandlerSignal(handler, handler.Configuration.SourcePresentSensorChannelId, ChannelKind.DigitalInput);
            ValidateWaferHandlerSignal(handler, handler.Configuration.GateOpenSensorChannelId, ChannelKind.DigitalInput);
            ValidateWaferHandlerSignal(handler, handler.Configuration.PickCommandChannelId, ChannelKind.DigitalOutput);
            ValidateWaferHandlerSignal(handler, handler.Configuration.PlaceCommandChannelId, ChannelKind.DigitalOutput);
            ValidateWaferHandlerSignal(handler, handler.Configuration.HoldingFeedbackChannelId, ChannelKind.DigitalInput);
            ValidateWaferHandlerSignal(handler, handler.Configuration.PlacedFeedbackChannelId, ChannelKind.DigitalInput);
        }

        foreach (var sorter in inspectionSortRouters)
        {
            ValidateInspectionSortRouterSignal(sorter, sorter.Configuration.PassRunCommandChannelId, ChannelKind.DigitalOutput);
            ValidateInspectionSortRouterSignal(sorter, sorter.Configuration.NgRunCommandChannelId, ChannelKind.DigitalOutput);
            ValidateInspectionSortRouterSignal(sorter, sorter.Configuration.PassRoutedFeedbackChannelId, ChannelKind.DigitalInput);
            ValidateInspectionSortRouterSignal(sorter, sorter.Configuration.NgRoutedFeedbackChannelId, ChannelKind.DigitalInput);
        }

        foreach (var handoff in inspectionHandoffs)
        {
            ValidateInspectionHandoffSignal(handoff, handoff.Configuration.InspectionPositionSensorChannelId, ChannelKind.DigitalInput);
            ValidateInspectionHandoffSignal(handoff, handoff.Configuration.ResultAcceptedCommandChannelId, ChannelKind.DigitalOutput);
            ValidateInspectionHandoffSignal(handoff, handoff.Configuration.InspectionReadyFeedbackChannelId, ChannelKind.DigitalInput);
            ValidateInspectionHandoffSignal(handoff, handoff.Configuration.InspectionCompleteFeedbackChannelId, ChannelKind.DigitalInput);
        }

        foreach (var handoff in ohtHandoffs)
        {
            ValidateOhtHandoffSignal(handoff, handoff.Configuration.ForwardCommandChannelId, ChannelKind.DigitalOutput);
            ValidateOhtHandoffSignal(handoff, handoff.Configuration.ReverseCommandChannelId, ChannelKind.DigitalOutput);
            ValidateOhtHandoffSignal(handoff, handoff.Configuration.RouteAvailableSensorChannelId, ChannelKind.DigitalInput);
            ValidateOhtHandoffSignal(handoff, handoff.Configuration.VehicleDockedSensorChannelId, ChannelKind.DigitalInput);
            ValidateOhtHandoffSignal(handoff, handoff.Configuration.LoadPortReadySensorChannelId, ChannelKind.DigitalInput);
            ValidateOhtHandoffSignal(handoff, handoff.Configuration.CarrierReceivedSensorChannelId, ChannelKind.DigitalInput);
            ValidateOhtHandoffSignal(handoff, handoff.Configuration.HandoffReadyFeedbackChannelId, ChannelKind.DigitalInput);
            ValidateOhtHandoffSignal(handoff, handoff.Configuration.CarrierTransferredFeedbackChannelId, ChannelKind.DigitalInput);
        }

        foreach (var prealigner in prealigners)
        {
            ValidatePrealignerSignal(prealigner, prealigner.Configuration.WaferPresentSensorChannelId, ChannelKind.DigitalInput);
            ValidatePrealignerSignal(prealigner, prealigner.Configuration.AlignmentAcceptedCommandChannelId, ChannelKind.DigitalOutput);
            ValidatePrealignerSignal(prealigner, prealigner.Configuration.AlignmentReadyFeedbackChannelId, ChannelKind.DigitalInput);
            ValidatePrealignerSignal(prealigner, prealigner.Configuration.AlignmentCompleteFeedbackChannelId, ChannelKind.DigitalInput);
        }
    }

    private void ValidateCylinderSignal(
        PneumaticCylinderRuntimeState cylinder,
        string channelId,
        ChannelKind expectedKind,
        string role)
    {
        SignalReadResult read = _signalHub.ReadDigitalSignal(channelId);
        if (!read.IsAccepted || read.Kind != expectedKind)
        {
            throw new ArgumentException(
                $"Cylinder '{cylinder.Configuration.Id}' {role} '{channelId}' " +
                $"must identify a configured {expectedKind} channel.",
                nameof(_signalHub));
        }
    }

    private void ValidateConveyorSignal(
        ConveyorRuntimeState conveyor,
        string channelId,
        string role)
    {
        SignalReadResult read = _signalHub.ReadDigitalSignal(channelId);
        if (!read.IsAccepted || read.Kind != ChannelKind.DigitalOutput)
        {
            throw new ArgumentException(
                $"Conveyor '{conveyor.Configuration.Id}' {role} '{channelId}' " +
                "must identify a configured DigitalOutput channel.",
                nameof(_signalHub));
        }
    }

    private void ValidateLoadLockSignal(
        LoadLockRuntimeState loadLock,
        string channelId,
        ChannelKind expectedKind,
        string role)
    {
        SignalReadResult read = _signalHub.ReadDigitalSignal(channelId);
        if (!read.IsAccepted || read.Kind != expectedKind)
        {
            throw new ArgumentException(
                $"Load-lock '{loadLock.Configuration.Id}' {role} '{channelId}' " +
                $"must identify a configured {expectedKind} channel.",
                nameof(_signalHub));
        }


    }

    private void ValidateWaferHandlerSignal(
        WaferHandlerRuntimeState handler,
        string channelId,
        ChannelKind expectedKind)
    {
        SignalReadResult read = _signalHub.ReadDigitalSignal(channelId);
        if (!read.IsAccepted || read.Kind != expectedKind)
        {
            throw new ArgumentException(
                $"Wafer-handler '{handler.Configuration.Id}' signal '{channelId}' must identify a configured {expectedKind} channel.",
                nameof(_signalHub));
        }
    }

    private void ValidateInspectionSortRouterSignal(
        InspectionSortRouterRuntimeState sorter,
        string channelId,
        ChannelKind expectedKind)
    {
        SignalReadResult read = _signalHub.ReadDigitalSignal(channelId);
        if (!read.IsAccepted || read.Kind != expectedKind)
        {
            throw new ArgumentException(
                $"Inspection sorter '{sorter.Configuration.Id}' signal '{channelId}' must identify a configured {expectedKind} channel.",
                nameof(_signalHub));
        }
    }

    private void ValidateOhtHandoffSignal(
        OhtHandoffRuntimeState handoff,
        string channelId,
        ChannelKind expectedKind)
    {
        SignalReadResult read = _signalHub.ReadDigitalSignal(channelId);
        if (!read.IsAccepted || read.Kind != expectedKind)
        {
            throw new ArgumentException(
                $"OHT handoff '{handoff.Configuration.Id}' signal '{channelId}' must identify a configured {expectedKind} channel.",
                nameof(_signalHub));
        }
    }

    private void ValidateInspectionHandoffSignal(
        InspectionHandoffRuntimeState handoff,
        string channelId,
        ChannelKind expectedKind)
    {
        SignalReadResult read = _signalHub.ReadDigitalSignal(channelId);
        if (!read.IsAccepted || read.Kind != expectedKind)
        {
            throw new ArgumentException(
                $"Inspection handoff '{handoff.Configuration.Id}' signal '{channelId}' must identify a configured {expectedKind} channel.",
                nameof(_signalHub));
        }
    }

    private void ValidatePrealignerSignal(
        PrealignerRuntimeState prealigner,
        string channelId,
        ChannelKind expectedKind)
    {
        SignalReadResult read = _signalHub.ReadDigitalSignal(channelId);
        if (!read.IsAccepted || read.Kind != expectedKind)
        {
            throw new ArgumentException(
                $"Pre-aligner '{prealigner.Configuration.Id}' signal '{channelId}' must identify a configured {expectedKind} channel.",
                nameof(_signalHub));
        }
    }
}
