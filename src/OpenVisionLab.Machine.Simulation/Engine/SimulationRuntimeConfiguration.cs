using OpenVisionLab.Machine.Core.Channels;
using OpenVisionLab.Machine.Sequence.Compilation;
using OpenVisionLab.Machine.Simulation.Axis;
using OpenVisionLab.Machine.Simulation.Camera;
using OpenVisionLab.Machine.Simulation.Layout;
using OpenVisionLab.Machine.Simulation.Workpieces;

namespace OpenVisionLab.Machine.Simulation.Engine;

public sealed class SimulationRuntimeConfiguration
{
    public SimulationRuntimeConfiguration(
        IEnumerable<AxisConfiguration> axes,
        IEnumerable<ChannelDefinition> channels,
        IEnumerable<CompiledSequence> sequences)
        : this(axes, channels, sequences, Array.Empty<VirtualCameraConfiguration>(), null, null, null)
    {
    }

    public SimulationRuntimeConfiguration(
        IEnumerable<AxisConfiguration> axes,
        IEnumerable<ChannelDefinition> channels,
        IEnumerable<CompiledSequence> sequences,
        IEnumerable<VirtualCameraConfiguration> cameras)
        : this(axes, channels, sequences, cameras, null, null, null)
    {
    }

    public SimulationRuntimeConfiguration(
        IEnumerable<AxisConfiguration> axes,
        IEnumerable<ChannelDefinition> channels,
        IEnumerable<CompiledSequence> sequences,
        IEnumerable<VirtualCameraConfiguration> cameras,
        AutomaticRunConfiguration? automaticRun)
        : this(axes, channels, sequences, cameras, automaticRun, null, null)
    {
    }

    public SimulationRuntimeConfiguration(
        IEnumerable<AxisConfiguration> axes,
        IEnumerable<ChannelDefinition> channels,
        IEnumerable<CompiledSequence> sequences,
        IEnumerable<VirtualCameraConfiguration> cameras,
        AutomaticRunConfiguration? automaticRun,
        MachineLayoutRuntimeConfiguration? layout)
        : this(axes, channels, sequences, cameras, automaticRun, layout, null)
    {
    }

    public SimulationRuntimeConfiguration(
        IEnumerable<AxisConfiguration> axes,
        IEnumerable<ChannelDefinition> channels,
        IEnumerable<CompiledSequence> sequences,
        IEnumerable<VirtualCameraConfiguration> cameras,
        AutomaticRunConfiguration? automaticRun,
        MachineLayoutRuntimeConfiguration? layout,
        PickPlaceWorkpieceRuntimeConfiguration? pickPlaceWorkpiece)
    {
        ArgumentNullException.ThrowIfNull(axes);
        ArgumentNullException.ThrowIfNull(channels);
        ArgumentNullException.ThrowIfNull(sequences);
        ArgumentNullException.ThrowIfNull(cameras);

        Axes = Array.AsReadOnly(axes.Select(CloneAxis).ToArray());
        Channels = Array.AsReadOnly(channels.Select(CloneChannel).ToArray());
        Sequences = Array.AsReadOnly(sequences.ToArray());
        Cameras = Array.AsReadOnly(cameras.Select(CloneCamera).ToArray());
        AutomaticRun = automaticRun is null ? null : CloneAutomaticRun(automaticRun);
        Layout = layout is null ? null : CloneLayout(layout);
        PickPlaceWorkpiece = pickPlaceWorkpiece is null
            ? null
            : new PickPlaceWorkpieceRuntimeConfiguration(
                pickPlaceWorkpiece.Id,
                pickPlaceWorkpiece.Name,
                pickPlaceWorkpiece.XAxisId,
                pickPlaceWorkpiece.YAxisId,
                pickPlaceWorkpiece.GripperSignalId,
                pickPlaceWorkpiece.PickX,
                pickPlaceWorkpiece.PickY);
    }

    public IReadOnlyList<AxisConfiguration> Axes { get; }
    public IReadOnlyList<ChannelDefinition> Channels { get; }
    public IReadOnlyList<CompiledSequence> Sequences { get; }
    public IReadOnlyList<VirtualCameraConfiguration> Cameras { get; }
    public AutomaticRunConfiguration? AutomaticRun { get; }
    public MachineLayoutRuntimeConfiguration? Layout { get; }
    public PickPlaceWorkpieceRuntimeConfiguration? PickPlaceWorkpiece { get; }

    private static AxisConfiguration CloneAxis(AxisConfiguration source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return new AxisConfiguration
        {
            Id = source.Id,
            Name = source.Name,
            MinimumPosition = source.MinimumPosition,
            MaximumPosition = source.MaximumPosition,
            HomePosition = source.HomePosition,
            MaximumVelocity = source.MaximumVelocity,
            Acceleration = source.Acceleration,
            Deceleration = source.Deceleration,
            FollowingErrorLimit = source.FollowingErrorLimit
        };
    }

    private static ChannelDefinition CloneChannel(ChannelDefinition source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return new ChannelDefinition
        {
            Id = source.Id,
            Name = source.Name,
            Kind = source.Kind,
            InitialValue = source.InitialValue,
            InterlockIds = source.InterlockIds?.ToList() ?? new List<string>()
        };
    }

    private static VirtualCameraConfiguration CloneCamera(VirtualCameraConfiguration source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return new VirtualCameraConfiguration(
            source.Id,
            source.Name,
            source.ExposureTicks,
            source.TransferTicks,
            source.PlaceholderDecision);
    }

    private static AutomaticRunConfiguration CloneAutomaticRun(AutomaticRunConfiguration source) =>
        new(
            source.SequenceId,
            source.StartInputId,
            source.StartInputValue,
            source.Repeat,
            source.RepeatDelayMilliseconds);

    private static MachineLayoutRuntimeConfiguration CloneLayout(
        MachineLayoutRuntimeConfiguration source) =>
        new(
            source.Id,
            source.Name,
            source.Components.Select(CloneLayoutComponent),
            source.LoadLocks.Select(CloneLoadLock),
            source.WaferHandlers.Select(CloneWaferHandler),
            source.InspectionSortRouters.Select(CloneInspectionSortRouter),
            source.InspectionHandoffs.Select(CloneInspectionHandoff),
            source.OhtHandoffs.Select(CloneOhtHandoff),
            source.Prealigners.Select(ClonePrealigner));

    private static PrealignerRuntimeConfiguration ClonePrealigner(
        PrealignerRuntimeConfiguration source) =>
        new(
            source.Id,
            source.Name,
            source.RotaryStageComponentId,
            source.RotaryAxisId,
            source.ClampCylinderComponentId,
            source.WaferPresentSensorChannelId,
            source.AlignmentAcceptedCommandChannelId,
            source.AlignmentReadyFeedbackChannelId,
            source.AlignmentCompleteFeedbackChannelId,
            source.AlignmentTargetDegrees,
            source.AlignmentToleranceDegrees);

    private static InspectionHandoffRuntimeConfiguration CloneInspectionHandoff(
        InspectionHandoffRuntimeConfiguration source) =>
        new(
            source.Id,
            source.Name,
            source.CameraId,
            source.InspectionPositionSensorChannelId,
            source.ResultAcceptedCommandChannelId,
            source.InspectionReadyFeedbackChannelId,
            source.InspectionCompleteFeedbackChannelId);

    private static OhtHandoffRuntimeConfiguration CloneOhtHandoff(
        OhtHandoffRuntimeConfiguration source) =>
        new(
            source.Id,
            source.Name,
            source.TransportConveyorComponentId,
            source.ForwardCommandChannelId,
            source.ReverseCommandChannelId,
            source.RouteAvailableSensorChannelId,
            source.VehicleDockedSensorChannelId,
            source.LoadPortReadySensorChannelId,
            source.CarrierReceivedSensorChannelId,
            source.HandoffReadyFeedbackChannelId,
            source.CarrierTransferredFeedbackChannelId);

    private static InspectionSortRouterRuntimeConfiguration CloneInspectionSortRouter(
        InspectionSortRouterRuntimeConfiguration source) =>
        new(
            source.Id,
            source.Name,
            source.CameraId,
            source.PassConveyorComponentId,
            source.NgConveyorComponentId,
            source.PassRunCommandChannelId,
            source.NgRunCommandChannelId,
            source.PassRoutedFeedbackChannelId,
            source.NgRoutedFeedbackChannelId);

    private static WaferHandlerRuntimeConfiguration CloneWaferHandler(
        WaferHandlerRuntimeConfiguration source) =>
        new(
            source.Id,
            source.Name,
            source.HorizontalAxisId,
            source.VerticalAxisId,
            source.WorkpieceComponentId,
            source.SourcePresentSensorChannelId,
            source.GateOpenSensorChannelId,
            source.PickCommandChannelId,
            source.PlaceCommandChannelId,
            source.HoldingFeedbackChannelId,
            source.PlacedFeedbackChannelId,
            source.PickHorizontalPosition,
            source.PickVerticalPosition,
            source.PlaceHorizontalPosition,
            source.PlaceVerticalPosition);

    private static LoadLockRuntimeConfiguration CloneLoadLock(
        LoadLockRuntimeConfiguration source) =>
        new(
            source.Id,
            source.Name,
            source.OuterDoorComponentId,
            source.InnerDoorComponentId,
            source.EvacuateCommandChannelId,
            source.VentCommandChannelId,
            source.VacuumReadySensorChannelId,
            source.AtmosphereReadySensorChannelId,
            source.PumpDownDurationTicks,
            source.VentDurationTicks);

    private static LayoutComponentRuntimeConfiguration CloneLayoutComponent(
        LayoutComponentRuntimeConfiguration source)
    {
        var transform = new LayoutRuntimeTransform(
            source.BaseTransform.X,
            source.BaseTransform.Y,
            source.BaseTransform.RotationDegrees);
        var size = new LayoutRuntimeSize(source.Size.Width, source.Size.Height);
        return source switch
        {
            MachineFrameRuntimeConfiguration frame => new MachineFrameRuntimeConfiguration(
                frame.Id,
                frame.Name,
                transform,
                size),
            LinearStageRuntimeConfiguration stage => new LinearStageRuntimeConfiguration(
                stage.Id,
                stage.Name,
                stage.AxisId,
                stage.HomePosition,
                transform,
                size),
            RotaryStageRuntimeConfiguration stage => new RotaryStageRuntimeConfiguration(
                stage.Id,
                stage.Name,
                stage.AxisId,
                stage.HomePosition,
                transform,
                size),
            DigitalSensorRuntimeConfiguration sensor => new DigitalSensorRuntimeConfiguration(
                sensor.Id,
                sensor.Name,
                sensor.OutputChannelId,
                sensor.TargetComponentId,
                sensor.OnDelayTicks,
                sensor.OffDelayTicks,
                transform,
                size),
            PneumaticCylinderRuntimeConfiguration cylinder => new PneumaticCylinderRuntimeConfiguration(
                cylinder.Id,
                cylinder.Name,
                cylinder.ExtendCommandChannelId,
                cylinder.ExtendedSensorChannelId,
                cylinder.RetractedSensorChannelId,
                cylinder.ExtendDurationTicks,
                cylinder.RetractDurationTicks,
                cylinder.ExtendedSensorDelayTicks,
                cylinder.RetractedSensorDelayTicks,
                cylinder.Stroke,
                transform,
                size),
            ConveyorRuntimeConfiguration conveyor => new ConveyorRuntimeConfiguration(
                conveyor.Id,
                conveyor.Name,
                conveyor.RunCommandChannelId,
                conveyor.ReverseCommandChannelId,
                conveyor.SpeedUnitsPerSecond,
                conveyor.FixedStepSeconds,
                transform,
                size),
            WorkpieceRuntimeConfiguration workpiece => new WorkpieceRuntimeConfiguration(
                workpiece.Id,
                workpiece.Name,
                workpiece.Type,
                workpiece.ConveyorComponentId,
                workpiece.InspectionState,
                transform,
                size),
            _ => throw new ArgumentOutOfRangeException(
                nameof(source),
                source.Kind,
                "Unsupported layout runtime component.")
        };
    }
}
