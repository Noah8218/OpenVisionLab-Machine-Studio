using System.Collections.ObjectModel;
using OpenVisionLab.Machine.Core.Channels;
using OpenVisionLab.Machine.Core.Layouts;
using OpenVisionLab.Machine.IO.Channels;
using OpenVisionLab.Machine.Simulation.Axis;
using OpenVisionLab.Machine.Simulation.Camera;

namespace OpenVisionLab.Machine.Simulation.Layout;

/// <summary>
/// Evaluates axis-linked poses, command-driven actuators and conveyors,
/// transported workpieces, and digital-sensor geometry once per fixed
/// simulation tick. This type is intended to be owned by the single simulation
/// thread.
/// </summary>
public sealed class DeterministicMachineLayout
{
    private static readonly IReadOnlySet<string> NoBlockedCylinders =
        new HashSet<string>(StringComparer.Ordinal);
    private static readonly IReadOnlyDictionary<string, VirtualCameraSnapshot> NoCameraSnapshots =
        new Dictionary<string, VirtualCameraSnapshot>(StringComparer.Ordinal);
    private readonly MachineLayoutRuntimeConfiguration _configuration;
    private readonly DeterministicSignalHub _signalHub;
    private readonly Dictionary<string, LayoutComponentRuntimeState> _componentsById;
    private readonly LayoutComponentRuntimeState[] _orderedComponents;
    private readonly DigitalSensorRuntimeState[] _orderedSensors;
    private readonly PneumaticCylinderRuntimeState[] _orderedCylinders;
    private readonly ConveyorRuntimeState[] _orderedConveyors;
    private readonly WorkpieceRuntimeState[] _orderedWorkpieces;
    private readonly LoadLockRuntimeState[] _orderedLoadLocks;
    private readonly WaferHandlerRuntimeState[] _orderedWaferHandlers;
    private readonly InspectionSortRouterRuntimeState[] _orderedInspectionSortRouters;
    private readonly InspectionHandoffRuntimeState[] _orderedInspectionHandoffs;
    private readonly OhtHandoffRuntimeState[] _orderedOhtHandoffs;
    private readonly PrealignerRuntimeState[] _orderedPrealigners;
    private readonly Dictionary<string, LoadLockRuntimeState> _loadLocksByDoorId;
    private readonly Dictionary<string, OhtHandoffRuntimeState> _ohtHandoffsByConveyorId;

    public DeterministicMachineLayout(
        MachineLayoutRuntimeConfiguration configuration,
        DeterministicSignalHub signalHub)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _signalHub = signalHub ?? throw new ArgumentNullException(nameof(signalHub));

        _orderedComponents = configuration.Components
            .Select(CreateState)
            .OrderBy(component => component.Configuration.Id, StringComparer.Ordinal)
            .ToArray();
        _componentsById = _orderedComponents.ToDictionary(
            component => component.Configuration.Id,
            StringComparer.Ordinal);
        _orderedSensors = _orderedComponents
            .OfType<DigitalSensorRuntimeState>()
            .OrderBy(sensor => sensor.Configuration.Id, StringComparer.Ordinal)
            .ToArray();
        _orderedCylinders = _orderedComponents
            .OfType<PneumaticCylinderRuntimeState>()
            .OrderBy(cylinder => cylinder.Configuration.Id, StringComparer.Ordinal)
            .ToArray();
        _orderedConveyors = _orderedComponents
            .OfType<ConveyorRuntimeState>()
            .OrderBy(conveyor => conveyor.Configuration.Id, StringComparer.Ordinal)
            .ToArray();
        _orderedWorkpieces = _orderedComponents
            .OfType<WorkpieceRuntimeState>()
            .OrderBy(workpiece => workpiece.Configuration.Id, StringComparer.Ordinal)
            .ToArray();
        _orderedLoadLocks = configuration.LoadLocks
            .Select(loadLock => new LoadLockRuntimeState(loadLock, signalHub))
            .OrderBy(loadLock => loadLock.Configuration.Id, StringComparer.Ordinal)
            .ToArray();
        _orderedWaferHandlers = configuration.WaferHandlers
            .Select(handler => new WaferHandlerRuntimeState(
                handler,
                signalHub,
                (WorkpieceRuntimeState)_componentsById[handler.WorkpieceComponentId]))
            .OrderBy(handler => handler.Configuration.Id, StringComparer.Ordinal)
            .ToArray();
        _orderedInspectionSortRouters = configuration.InspectionSortRouters
            .Select(sorter => new InspectionSortRouterRuntimeState(sorter, signalHub))
            .OrderBy(sorter => sorter.Configuration.Id, StringComparer.Ordinal)
            .ToArray();
        _orderedInspectionHandoffs = configuration.InspectionHandoffs
            .Select(handoff => new InspectionHandoffRuntimeState(handoff, signalHub))
            .OrderBy(handoff => handoff.Configuration.Id, StringComparer.Ordinal)
            .ToArray();
        _orderedOhtHandoffs = configuration.OhtHandoffs
            .Select(handoff => new OhtHandoffRuntimeState(handoff, signalHub))
            .OrderBy(handoff => handoff.Configuration.Id, StringComparer.Ordinal)
            .ToArray();
        _orderedPrealigners = configuration.Prealigners
            .Select(prealigner => new PrealignerRuntimeState(prealigner, signalHub))
            .OrderBy(prealigner => prealigner.Configuration.Id, StringComparer.Ordinal)
            .ToArray();
        _loadLocksByDoorId = _orderedLoadLocks
            .SelectMany(loadLock => new[]
            {
                (loadLock.Configuration.OuterDoorComponentId, LoadLock: loadLock),
                (loadLock.Configuration.InnerDoorComponentId, LoadLock: loadLock)
            })
            .ToDictionary(item => item.Item1, item => item.LoadLock, StringComparer.Ordinal);
        _ohtHandoffsByConveyorId = _orderedOhtHandoffs.ToDictionary(
            handoff => handoff.Configuration.TransportConveyorComponentId,
            StringComparer.Ordinal);

        InitializeWorkpieceCarrierPositions();
        ValidateSignalBindings();
    }

    public string Id => _configuration.Id;
    public string Name => _configuration.Name;
    public bool ContainsCylinder(string? componentId) =>
        !string.IsNullOrWhiteSpace(componentId)
        && _componentsById.TryGetValue(componentId, out LayoutComponentRuntimeState? component)
        && component is PneumaticCylinderRuntimeState;

    public bool TryGetDigitalSensorOutputChannelId(string? componentId, out string? channelId)
    {
        if (!string.IsNullOrWhiteSpace(componentId)
            && _componentsById.TryGetValue(componentId, out LayoutComponentRuntimeState? component)
            && component is DigitalSensorRuntimeState sensor)
        {
            channelId = sensor.SensorConfiguration.OutputChannelId;
            return true;
        }

        channelId = null;
        return false;
    }

    public bool TryGetCylinderCommandChannelId(string? componentId, out string? channelId)
    {
        if (!string.IsNullOrWhiteSpace(componentId)
            && _componentsById.TryGetValue(componentId, out LayoutComponentRuntimeState? component)
            && component is PneumaticCylinderRuntimeState cylinder)
        {
            channelId = cylinder.CylinderConfiguration.ExtendCommandChannelId;
            return true;
        }

        channelId = null;
        return false;
    }

    public bool TryGetConveyorCommandChannelIds(
        string? componentId,
        out string? runChannelId,
        out string? reverseChannelId)
    {
        if (!string.IsNullOrWhiteSpace(componentId)
            && _componentsById.TryGetValue(componentId, out LayoutComponentRuntimeState? component)
            && component is ConveyorRuntimeState conveyor)
        {
            runChannelId = conveyor.ConveyorConfiguration.RunCommandChannelId;
            reverseChannelId = conveyor.ConveyorConfiguration.ReverseCommandChannelId;
            return true;
        }

        runChannelId = null;
        reverseChannelId = null;
        return false;
    }

    /// <summary>
    /// Advances layout state by one fixed tick. Axis lookups and all returned
    /// component/transition ordering use exact ordinal identifiers.
    /// </summary>
    public MachineLayoutTickResult Tick(
        IReadOnlyDictionary<string, AxisSnapshot> axisSnapshots) =>
        Tick(axisSnapshots, NoBlockedCylinders, NoCameraSnapshots);

    public MachineLayoutTickResult Tick(
        IReadOnlyDictionary<string, AxisSnapshot> axisSnapshots,
        IReadOnlySet<string> blockedCylinderIds) =>
        Tick(axisSnapshots, blockedCylinderIds, NoCameraSnapshots);

    public MachineLayoutTickResult Tick(
        IReadOnlyDictionary<string, AxisSnapshot> axisSnapshots,
        IReadOnlySet<string> blockedCylinderIds,
        IReadOnlyDictionary<string, VirtualCameraSnapshot> cameraSnapshots)
    {
        ArgumentNullException.ThrowIfNull(axisSnapshots);
        ArgumentNullException.ThrowIfNull(blockedCylinderIds);
        ArgumentNullException.ThrowIfNull(cameraSnapshots);

        UpdateStagePositions(axisSnapshots);
        EvaluateLoadLocks();
        var cylinderTransitions = EvaluateCylinders(blockedCylinderIds);
        EvaluateOhtHandoffs();
        var conveyorTransitions = EvaluateConveyorsAndWorkpieces();
        var sensorTransitions = EvaluateSensors();
        EvaluateWaferHandlers(axisSnapshots);
        EvaluateInspectionSortRouters(cameraSnapshots);
        EvaluateInspectionHandoffs(cameraSnapshots);
        EvaluatePrealigners(axisSnapshots);
        return new MachineLayoutTickResult(
            CaptureSnapshotsCore(),
            sensorTransitions,
            cylinderTransitions.StateTransitions,
            cylinderTransitions.FeedbackTransitions,
            conveyorTransitions,
            CaptureLoadLockSnapshotsCore(),
            CaptureWaferHandlerSnapshotsCore(),
            CaptureInspectionSortRouterSnapshotsCore(),
            CaptureInspectionHandoffSnapshotsCore(),
            CaptureOhtHandoffSnapshotsCore(),
            CapturePrealignerSnapshotsCore());
    }

    /// <summary>
    /// Restores component poses and actuator state, clears sensor delay
    /// history, and restores simulation-owned feedback to reset values.
    /// </summary>
    public void Reset()
    {
        foreach (var component in _orderedComponents)
        {
            component.Reset();
        }
        InitializeWorkpieceCarrierPositions();

        foreach (var sensor in _orderedSensors)
        {
            SignalWriteResult write = _signalHub.SetDigitalInput(
                sensor.SensorConfiguration.OutputChannelId,
                false,
                SignalWriteOwner.SimulationComponent);
            EnsureAcceptedSensorWrite(sensor, write);
        }

        foreach (var cylinder in _orderedCylinders)
        {
            EnsureAcceptedCylinderWrite(
                cylinder,
                cylinder.CylinderConfiguration.ExtendedSensorChannelId,
                _signalHub.SetDigitalInput(
                    cylinder.CylinderConfiguration.ExtendedSensorChannelId,
                    false,
                    SignalWriteOwner.SimulationComponent));
            EnsureAcceptedCylinderWrite(
                cylinder,
                cylinder.CylinderConfiguration.RetractedSensorChannelId,
                _signalHub.SetDigitalInput(
                    cylinder.CylinderConfiguration.RetractedSensorChannelId,
                    true,
                    SignalWriteOwner.SimulationComponent));
        }

        foreach (var loadLock in _orderedLoadLocks)
        {
            loadLock.Reset();
        }

        foreach (var handler in _orderedWaferHandlers)
        {
            handler.Reset();
        }

        foreach (var sorter in _orderedInspectionSortRouters)
        {
            sorter.Reset();
        }

        foreach (var handoff in _orderedInspectionHandoffs)
        {
            handoff.Reset();
        }

        foreach (var handoff in _orderedOhtHandoffs)
        {
            handoff.Reset();
        }

        foreach (var prealigner in _orderedPrealigners)
        {
            prealigner.Reset();
        }
    }

    public ReadOnlyCollection<LayoutComponentSnapshot> CaptureSnapshots() =>
        new(CaptureSnapshotsCore());

    public ReadOnlyCollection<LoadLockSnapshot> CaptureLoadLockSnapshots() =>
        new(CaptureLoadLockSnapshotsCore());

    public ReadOnlyCollection<WaferHandlerSnapshot> CaptureWaferHandlerSnapshots() =>
        new(CaptureWaferHandlerSnapshotsCore());

    public ReadOnlyCollection<InspectionSortRouterSnapshot> CaptureInspectionSortRouterSnapshots() =>
        new(CaptureInspectionSortRouterSnapshotsCore());

    public ReadOnlyCollection<InspectionHandoffSnapshot> CaptureInspectionHandoffSnapshots() =>
        new(CaptureInspectionHandoffSnapshotsCore());

    public ReadOnlyCollection<OhtHandoffSnapshot> CaptureOhtHandoffSnapshots() =>
        new(CaptureOhtHandoffSnapshotsCore());

    public ReadOnlyCollection<PrealignerSnapshot> CapturePrealignerSnapshots() =>
        new(CapturePrealignerSnapshotsCore());

    private void InitializeWorkpieceCarrierPositions()
    {
        foreach (var workpiece in _orderedWorkpieces)
        {
            var conveyor = (ConveyorRuntimeState)_componentsById[
                workpiece.WorkpieceConfiguration.ConveyorComponentId];
            workpiece.UpdateCarrierPosition(conveyor);
        }
    }

    private void ValidateSignalBindings()
    {
        foreach (var sensor in _orderedSensors)
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

        foreach (var cylinder in _orderedCylinders)
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

        foreach (var conveyor in _orderedConveyors)
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

        foreach (var loadLock in _orderedLoadLocks)
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

        foreach (var handler in _orderedWaferHandlers)
        {
            ValidateWaferHandlerSignal(handler, handler.Configuration.SourcePresentSensorChannelId, ChannelKind.DigitalInput);
            ValidateWaferHandlerSignal(handler, handler.Configuration.GateOpenSensorChannelId, ChannelKind.DigitalInput);
            ValidateWaferHandlerSignal(handler, handler.Configuration.PickCommandChannelId, ChannelKind.DigitalOutput);
            ValidateWaferHandlerSignal(handler, handler.Configuration.PlaceCommandChannelId, ChannelKind.DigitalOutput);
            ValidateWaferHandlerSignal(handler, handler.Configuration.HoldingFeedbackChannelId, ChannelKind.DigitalInput);
            ValidateWaferHandlerSignal(handler, handler.Configuration.PlacedFeedbackChannelId, ChannelKind.DigitalInput);
        }

        foreach (var sorter in _orderedInspectionSortRouters)
        {
            ValidateInspectionSortRouterSignal(sorter, sorter.Configuration.PassRunCommandChannelId, ChannelKind.DigitalOutput);
            ValidateInspectionSortRouterSignal(sorter, sorter.Configuration.NgRunCommandChannelId, ChannelKind.DigitalOutput);
            ValidateInspectionSortRouterSignal(sorter, sorter.Configuration.PassRoutedFeedbackChannelId, ChannelKind.DigitalInput);
            ValidateInspectionSortRouterSignal(sorter, sorter.Configuration.NgRoutedFeedbackChannelId, ChannelKind.DigitalInput);
        }

        foreach (var handoff in _orderedInspectionHandoffs)
        {
            ValidateInspectionHandoffSignal(handoff, handoff.Configuration.InspectionPositionSensorChannelId, ChannelKind.DigitalInput);
            ValidateInspectionHandoffSignal(handoff, handoff.Configuration.ResultAcceptedCommandChannelId, ChannelKind.DigitalOutput);
            ValidateInspectionHandoffSignal(handoff, handoff.Configuration.InspectionReadyFeedbackChannelId, ChannelKind.DigitalInput);
            ValidateInspectionHandoffSignal(handoff, handoff.Configuration.InspectionCompleteFeedbackChannelId, ChannelKind.DigitalInput);
        }

        foreach (var handoff in _orderedOhtHandoffs)
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

        foreach (var prealigner in _orderedPrealigners)
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

    private void EvaluateLoadLocks()
    {
        foreach (var loadLock in _orderedLoadLocks)
        {
            var outerDoor = (PneumaticCylinderRuntimeState)_componentsById[
                loadLock.Configuration.OuterDoorComponentId];
            var innerDoor = (PneumaticCylinderRuntimeState)_componentsById[
                loadLock.Configuration.InnerDoorComponentId];
            loadLock.Tick(
                outerDoor.State,
                innerDoor.State,
                ReadCylinderCommand(outerDoor),
                ReadCylinderCommand(innerDoor));
        }
    }

    private (
        IReadOnlyList<PneumaticCylinderStateTransition> StateTransitions,
        IReadOnlyList<PneumaticCylinderFeedbackTransition> FeedbackTransitions)
        EvaluateCylinders(IReadOnlySet<string> blockedCylinderIds)
    {
        var stateTransitions = new List<PneumaticCylinderStateTransition>();
        var feedbackTransitions = new List<PneumaticCylinderFeedbackTransition>();

        foreach (var cylinder in _orderedCylinders)
        {
            SignalReadResult command = _signalHub.ReadDigitalSignal(
                cylinder.CylinderConfiguration.ExtendCommandChannelId);
            if (!command.IsAccepted || command.Kind != ChannelKind.DigitalOutput)
            {
                throw new InvalidOperationException(
                    $"Cylinder '{cylinder.Configuration.Id}' could not read extend command " +
                    $"'{cylinder.CylinderConfiguration.ExtendCommandChannelId}'.");
            }

            PneumaticCylinderState previousState = cylinder.State;
            bool extensionRequested = command.Value == true;
            if (_loadLocksByDoorId.TryGetValue(cylinder.Configuration.Id, out var loadLock))
            {
                extensionRequested = string.Equals(
                    cylinder.Configuration.Id,
                    loadLock.Configuration.OuterDoorComponentId,
                    StringComparison.Ordinal)
                    ? loadLock.AllowOuterDoorExtension
                    : loadLock.AllowInnerDoorExtension;
            }

            cylinder.Tick(
                extensionRequested,
                blockedCylinderIds.Contains(cylinder.Configuration.Id));

            SignalWriteResult extendedWrite = _signalHub.SetDigitalInput(
                cylinder.CylinderConfiguration.ExtendedSensorChannelId,
                cylinder.IsExtendedFeedback,
                SignalWriteOwner.SimulationComponent);
            EnsureAcceptedCylinderWrite(
                cylinder,
                cylinder.CylinderConfiguration.ExtendedSensorChannelId,
                extendedWrite);
            SignalWriteResult retractedWrite = _signalHub.SetDigitalInput(
                cylinder.CylinderConfiguration.RetractedSensorChannelId,
                cylinder.IsRetractedFeedback,
                SignalWriteOwner.SimulationComponent);
            EnsureAcceptedCylinderWrite(
                cylinder,
                cylinder.CylinderConfiguration.RetractedSensorChannelId,
                retractedWrite);

            if (previousState != cylinder.State)
            {
                stateTransitions.Add(new PneumaticCylinderStateTransition(
                    cylinder.Configuration.Id,
                    previousState,
                    cylinder.State,
                    cylinder.MotionProgress));
            }

            if (extendedWrite.StateChanged)
            {
                feedbackTransitions.Add(new PneumaticCylinderFeedbackTransition(
                    cylinder.Configuration.Id,
                    cylinder.CylinderConfiguration.ExtendedSensorChannelId,
                    extendedWrite.PreviousValue!.Value,
                    extendedWrite.CurrentValue!.Value,
                    extendedWrite.Revision));
            }

            if (retractedWrite.StateChanged)
            {
                feedbackTransitions.Add(new PneumaticCylinderFeedbackTransition(
                    cylinder.Configuration.Id,
                    cylinder.CylinderConfiguration.RetractedSensorChannelId,
                    retractedWrite.PreviousValue!.Value,
                    retractedWrite.CurrentValue!.Value,
                    retractedWrite.Revision));
            }
        }

        return (stateTransitions, feedbackTransitions);
    }

    private IReadOnlyList<ConveyorStateTransition> EvaluateConveyorsAndWorkpieces()
    {
        var transitions = new List<ConveyorStateTransition>();
        foreach (var conveyor in _orderedConveyors)
        {
            SignalReadResult run = _signalHub.ReadDigitalSignal(
                conveyor.ConveyorConfiguration.RunCommandChannelId);
            SignalReadResult reverse = _signalHub.ReadDigitalSignal(
                conveyor.ConveyorConfiguration.ReverseCommandChannelId);
            if (!run.IsAccepted || run.Kind != ChannelKind.DigitalOutput
                || !reverse.IsAccepted || reverse.Kind != ChannelKind.DigitalOutput)
            {
                throw new InvalidOperationException(
                    $"Conveyor '{conveyor.Configuration.Id}' could not read its command channels.");
            }

            bool previousRunning = conveyor.IsRunning;
            ConveyorDirection previousDirection = conveyor.Direction;
            bool forwardRequested = run.Value == true;
            bool reverseRequested = reverse.Value == true;
            if (_ohtHandoffsByConveyorId.TryGetValue(conveyor.Configuration.Id, out var handoff))
            {
                forwardRequested &= handoff.AllowForwardMotion;
                reverseRequested = false;
            }
            conveyor.Tick(forwardRequested, reverseRequested);
            if (previousRunning != conveyor.IsRunning || previousDirection != conveyor.Direction)
            {
                transitions.Add(new ConveyorStateTransition(
                    conveyor.Configuration.Id,
                    previousRunning,
                    conveyor.IsRunning,
                    previousDirection,
                    conveyor.Direction,
                    conveyor.ConveyorConfiguration.SpeedUnitsPerSecond));
            }
        }

        foreach (var workpiece in _orderedWorkpieces)
        {
            var conveyor = (ConveyorRuntimeState)_componentsById[
                workpiece.WorkpieceConfiguration.ConveyorComponentId];
            workpiece.Tick(conveyor);
        }

        return transitions;
    }

    private void UpdateStagePositions(
        IReadOnlyDictionary<string, AxisSnapshot> axisSnapshots)
    {
        foreach (var stage in _orderedComponents.OfType<AxisBoundStageRuntimeState>())
        {
            string axisId = stage.StageConfiguration.AxisId;
            if (!axisSnapshots.TryGetValue(axisId, out AxisSnapshot? axis) || axis is null)
            {
                throw new InvalidOperationException(
                    $"Layout stage '{stage.Configuration.Id}' axis snapshot '{axisId}' was not found.");
            }

            stage.ApplyAxisSnapshot(axis);
        }
    }

    private IReadOnlyList<MachineLayoutTransition> EvaluateSensors()
    {
        var transitions = new List<MachineLayoutTransition>();

        foreach (var sensor in _orderedSensors)
        {
            LayoutComponentRuntimeState target = _componentsById[sensor.SensorConfiguration.TargetComponentId];
            bool rawDetected = CreateBounds(sensor).IntersectsInclusive(CreateBounds(target));
            sensor.ApplyRawDetection(rawDetected);

            SignalWriteResult write = _signalHub.SetDigitalInput(
                sensor.SensorConfiguration.OutputChannelId,
                sensor.IsDetected,
                SignalWriteOwner.SimulationComponent);
            EnsureAcceptedSensorWrite(sensor, write);

            if (write.StateChanged)
            {
                transitions.Add(new MachineLayoutTransition(
                    sensor.Configuration.Id,
                    sensor.SensorConfiguration.OutputChannelId,
                    write.CurrentValue == true
                        ? MachineLayoutTransitionKind.SensorActivated
                        : MachineLayoutTransitionKind.SensorDeactivated,
                    write.PreviousValue!.Value,
                    write.CurrentValue!.Value,
                    write.Revision));
            }
        }

        return transitions;
    }

    private void EvaluateWaferHandlers(IReadOnlyDictionary<string, AxisSnapshot> axisSnapshots)
    {
        foreach (var handler in _orderedWaferHandlers)
        {
            handler.Tick(axisSnapshots);
        }
    }

    private void EvaluateOhtHandoffs()
    {
        foreach (var handoff in _orderedOhtHandoffs)
        {
            handoff.Tick();
        }
    }

    private void EvaluateInspectionSortRouters(
        IReadOnlyDictionary<string, VirtualCameraSnapshot> cameraSnapshots)
    {
        foreach (var sorter in _orderedInspectionSortRouters)
        {
            if (!cameraSnapshots.TryGetValue(sorter.Configuration.CameraId, out var camera))
            {
                throw new InvalidOperationException(
                    $"Inspection sorter '{sorter.Configuration.Id}' camera snapshot '{sorter.Configuration.CameraId}' was not found.");
            }

            sorter.Tick(camera);
        }
    }

    private void EvaluateInspectionHandoffs(
        IReadOnlyDictionary<string, VirtualCameraSnapshot> cameraSnapshots)
    {
        foreach (var handoff in _orderedInspectionHandoffs)
        {
            if (!cameraSnapshots.TryGetValue(handoff.Configuration.CameraId, out var camera))
            {
                throw new InvalidOperationException(
                    $"Inspection handoff '{handoff.Configuration.Id}' camera snapshot '{handoff.Configuration.CameraId}' was not found.");
            }

            handoff.Tick(camera);
        }
    }

    private void EvaluatePrealigners(IReadOnlyDictionary<string, AxisSnapshot> axisSnapshots)
    {
        foreach (var prealigner in _orderedPrealigners)
        {
            if (!axisSnapshots.TryGetValue(prealigner.Configuration.RotaryAxisId, out var rotaryAxis))
            {
                throw new InvalidOperationException(
                    $"Pre-aligner '{prealigner.Configuration.Id}' rotary-axis snapshot '{prealigner.Configuration.RotaryAxisId}' was not found.");
            }

            var clamp = (PneumaticCylinderRuntimeState)_componentsById[
                prealigner.Configuration.ClampCylinderComponentId];
            prealigner.Tick(rotaryAxis, clamp.State);
        }
    }

    private LayoutComponentSnapshot[] CaptureSnapshotsCore() =>
        _orderedComponents
            .Select(component => component.CaptureSnapshot())
            .ToArray();

    private LoadLockSnapshot[] CaptureLoadLockSnapshotsCore() =>
        _orderedLoadLocks
            .Select(loadLock => loadLock.CaptureSnapshot())
            .ToArray();

    private WaferHandlerSnapshot[] CaptureWaferHandlerSnapshotsCore() =>
        _orderedWaferHandlers
            .Select(handler => handler.CaptureSnapshot())
            .ToArray();

    private InspectionSortRouterSnapshot[] CaptureInspectionSortRouterSnapshotsCore() =>
        _orderedInspectionSortRouters
            .Select(sorter => sorter.CaptureSnapshot())
            .ToArray();

    private InspectionHandoffSnapshot[] CaptureInspectionHandoffSnapshotsCore() =>
        _orderedInspectionHandoffs
            .Select(handoff => handoff.CaptureSnapshot())
            .ToArray();

    private OhtHandoffSnapshot[] CaptureOhtHandoffSnapshotsCore() =>
        _orderedOhtHandoffs
            .Select(handoff => handoff.CaptureSnapshot())
            .ToArray();

    private PrealignerSnapshot[] CapturePrealignerSnapshotsCore() =>
        _orderedPrealigners
            .Select(prealigner => prealigner.CaptureSnapshot())
            .ToArray();

    private bool ReadCylinderCommand(PneumaticCylinderRuntimeState cylinder)
    {
        SignalReadResult command = _signalHub.ReadDigitalSignal(
            cylinder.CylinderConfiguration.ExtendCommandChannelId);
        if (!command.IsAccepted || command.Kind != ChannelKind.DigitalOutput)
        {
            throw new InvalidOperationException(
                $"Cylinder '{cylinder.Configuration.Id}' could not read extend command " +
                $"'{cylinder.CylinderConfiguration.ExtendCommandChannelId}'.");
        }

        return command.Value == true;
    }

    private static LayoutComponentRuntimeState CreateState(
        LayoutComponentRuntimeConfiguration configuration) =>
        configuration switch
        {
            MachineFrameRuntimeConfiguration frame => new PassiveLayoutComponentRuntimeState(frame),
            LinearStageRuntimeConfiguration stage => new LinearStageRuntimeState(stage),
            RotaryStageRuntimeConfiguration stage => new RotaryStageRuntimeState(stage),
            DigitalSensorRuntimeConfiguration sensor => new DigitalSensorRuntimeState(sensor),
            PneumaticCylinderRuntimeConfiguration cylinder => new PneumaticCylinderRuntimeState(cylinder),
            ConveyorRuntimeConfiguration conveyor => new ConveyorRuntimeState(conveyor),
            WorkpieceRuntimeConfiguration workpiece => new WorkpieceRuntimeState(workpiece),
            _ => throw new ArgumentOutOfRangeException(
                nameof(configuration),
                configuration.Kind,
                "The layout component kind is not supported by the deterministic runtime.")
        };

    private static LayoutAabb CreateBounds(LayoutComponentRuntimeState component)
    {
        double radians = component.RotationDegrees * Math.PI / 180d;
        double cosine = Math.Abs(Math.Cos(radians));
        double sine = Math.Abs(Math.Sin(radians));
        double halfWidth = ((component.Configuration.Size.Width * cosine) +
                            (component.Configuration.Size.Height * sine)) / 2d;
        double halfHeight = ((component.Configuration.Size.Width * sine) +
                             (component.Configuration.Size.Height * cosine)) / 2d;

        return new LayoutAabb(
            component.X - halfWidth,
            component.X + halfWidth,
            component.Y - halfHeight,
            component.Y + halfHeight);
    }

    private static void EnsureAcceptedSensorWrite(
        DigitalSensorRuntimeState sensor,
        SignalWriteResult write)
    {
        if (!write.IsAccepted)
        {
            throw new InvalidOperationException(
                $"Sensor '{sensor.Configuration.Id}' could not write digital input " +
                $"'{sensor.SensorConfiguration.OutputChannelId}': {write.ErrorCode}.");
        }
    }

    private static void EnsureAcceptedCylinderWrite(
        PneumaticCylinderRuntimeState cylinder,
        string channelId,
        SignalWriteResult write)
    {
        if (!write.IsAccepted)
        {
            throw new InvalidOperationException(
                $"Cylinder '{cylinder.Configuration.Id}' could not write digital input " +
                $"'{channelId}': {write.ErrorCode}.");
        }
    }

}
