using System.Collections.ObjectModel;
using OpenVisionLab.Machine.Core.Devices;
using OpenVisionLab.Machine.Core.Layouts;

namespace OpenVisionLab.Machine.Simulation.Layout;

/// <summary>
/// Immutable two-dimensional transform copied from an authored layout.
/// X and Y identify the component center in layout world coordinates.
/// </summary>
public sealed record LayoutRuntimeTransform
{
    public LayoutRuntimeTransform(double x, double y, double rotationDegrees = 0)
    {
        if (!double.IsFinite(x))
        {
            throw new ArgumentOutOfRangeException(nameof(x), x, "Layout X must be finite.");
        }

        if (!double.IsFinite(y))
        {
            throw new ArgumentOutOfRangeException(nameof(y), y, "Layout Y must be finite.");
        }

        if (!double.IsFinite(rotationDegrees))
        {
            throw new ArgumentOutOfRangeException(
                nameof(rotationDegrees),
                rotationDegrees,
                "Layout rotation must be finite.");
        }

        X = x;
        Y = y;
        RotationDegrees = rotationDegrees;
    }

    public double X { get; }
    public double Y { get; }
    public double RotationDegrees { get; }
}

/// <summary>
/// Immutable positive component size in layout world units.
/// </summary>
public sealed record LayoutRuntimeSize
{
    public LayoutRuntimeSize(double width, double height)
    {
        if (!double.IsFinite(width) || width <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(width),
                width,
                "Layout width must be finite and positive.");
        }

        if (!double.IsFinite(height) || height <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(height),
                height,
                "Layout height must be finite and positive.");
        }

        Width = width;
        Height = height;
    }

    public double Width { get; }
    public double Height { get; }
}

/// <summary>
/// Source-neutral immutable runtime configuration for one layout component.
/// </summary>
public abstract record LayoutComponentRuntimeConfiguration
{
    protected LayoutComponentRuntimeConfiguration(
        string id,
        string name,
        LayoutComponentKind kind,
        LayoutRuntimeTransform baseTransform,
        LayoutRuntimeSize size)
    {
        Id = RequiredIdentifier(id, nameof(id));
        Name = RequiredIdentifier(name, nameof(name));
        Kind = kind;
        BaseTransform = baseTransform ?? throw new ArgumentNullException(nameof(baseTransform));
        Size = size ?? throw new ArgumentNullException(nameof(size));
    }

    public string Id { get; }
    public string Name { get; }
    public LayoutComponentKind Kind { get; }
    public LayoutRuntimeTransform BaseTransform { get; }
    public LayoutRuntimeSize Size { get; }

    internal static string RequiredIdentifier(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "A non-empty value without leading or trailing whitespace is required.",
                parameterName);
        }

        return value;
    }
}

public sealed record MachineFrameRuntimeConfiguration : LayoutComponentRuntimeConfiguration
{
    public MachineFrameRuntimeConfiguration(
        string id,
        string name,
        LayoutRuntimeTransform baseTransform,
        LayoutRuntimeSize size)
        : base(id, name, LayoutComponentKind.MachineFrame, baseTransform, size)
    {
    }
}

public abstract record AxisBoundStageRuntimeConfiguration : LayoutComponentRuntimeConfiguration
{
    protected AxisBoundStageRuntimeConfiguration(
        string id,
        string name,
        LayoutComponentKind kind,
        string axisId,
        double homePosition,
        LayoutRuntimeTransform baseTransform,
        LayoutRuntimeSize size)
        : base(id, name, kind, baseTransform, size)
    {
        AxisId = RequiredIdentifier(axisId, nameof(axisId));
        if (!double.IsFinite(homePosition))
        {
            throw new ArgumentOutOfRangeException(
                nameof(homePosition),
                homePosition,
                "Stage home position must be finite.");
        }

        HomePosition = homePosition;
    }

    public string AxisId { get; }
    public double HomePosition { get; }
}

public sealed record LinearStageRuntimeConfiguration : AxisBoundStageRuntimeConfiguration
{
    public LinearStageRuntimeConfiguration(
        string id,
        string name,
        string axisId,
        double homePosition,
        LayoutRuntimeTransform baseTransform,
        LayoutRuntimeSize size)
        : base(
            id,
            name,
            LayoutComponentKind.LinearStage,
            axisId,
            homePosition,
            baseTransform,
            size)
    {
    }
}

public sealed record RotaryStageRuntimeConfiguration : AxisBoundStageRuntimeConfiguration
{
    public RotaryStageRuntimeConfiguration(
        string id,
        string name,
        string axisId,
        double homePosition,
        LayoutRuntimeTransform baseTransform,
        LayoutRuntimeSize size)
        : base(
            id,
            name,
            LayoutComponentKind.RotaryStage,
            axisId,
            homePosition,
            baseTransform,
            size)
    {
    }
}

public sealed record DigitalSensorRuntimeConfiguration : LayoutComponentRuntimeConfiguration
{
    public DigitalSensorRuntimeConfiguration(
        string id,
        string name,
        string outputChannelId,
        string targetComponentId,
        int onDelayTicks,
        int offDelayTicks,
        LayoutRuntimeTransform baseTransform,
        LayoutRuntimeSize size)
        : base(id, name, LayoutComponentKind.DigitalSensor, baseTransform, size)
    {
        OutputChannelId = RequiredIdentifier(outputChannelId, nameof(outputChannelId));
        TargetComponentId = RequiredIdentifier(targetComponentId, nameof(targetComponentId));

        if (onDelayTicks < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(onDelayTicks),
                onDelayTicks,
                "Sensor on-delay ticks cannot be negative.");
        }

        if (offDelayTicks < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(offDelayTicks),
                offDelayTicks,
                "Sensor off-delay ticks cannot be negative.");
        }

        OnDelayTicks = onDelayTicks;
        OffDelayTicks = offDelayTicks;
    }

    public string OutputChannelId { get; }
    public string TargetComponentId { get; }
    public int OnDelayTicks { get; }
    public int OffDelayTicks { get; }
}

public sealed record PneumaticCylinderRuntimeConfiguration : LayoutComponentRuntimeConfiguration
{
    public PneumaticCylinderRuntimeConfiguration(
        string id,
        string name,
        string extendCommandChannelId,
        string extendedSensorChannelId,
        string retractedSensorChannelId,
        int extendDurationTicks,
        int retractDurationTicks,
        int extendedSensorDelayTicks,
        int retractedSensorDelayTicks,
        double stroke,
        LayoutRuntimeTransform baseTransform,
        LayoutRuntimeSize size)
        : base(id, name, LayoutComponentKind.PneumaticCylinder, baseTransform, size)
    {
        ExtendCommandChannelId = RequiredIdentifier(
            extendCommandChannelId,
            nameof(extendCommandChannelId));
        ExtendedSensorChannelId = RequiredIdentifier(
            extendedSensorChannelId,
            nameof(extendedSensorChannelId));
        RetractedSensorChannelId = RequiredIdentifier(
            retractedSensorChannelId,
            nameof(retractedSensorChannelId));

        if (new[] { ExtendCommandChannelId, ExtendedSensorChannelId, RetractedSensorChannelId }
            .Distinct(StringComparer.Ordinal)
            .Count() != 3)
        {
            throw new ArgumentException("Cylinder command and feedback channels must be distinct.");
        }

        if (extendDurationTicks <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(extendDurationTicks));
        }

        if (retractDurationTicks <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(retractDurationTicks));
        }

        if (extendedSensorDelayTicks < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(extendedSensorDelayTicks));
        }

        if (retractedSensorDelayTicks < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(retractedSensorDelayTicks));
        }

        if (!double.IsFinite(stroke) || stroke <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(stroke));
        }

        ExtendDurationTicks = extendDurationTicks;
        RetractDurationTicks = retractDurationTicks;
        ExtendedSensorDelayTicks = extendedSensorDelayTicks;
        RetractedSensorDelayTicks = retractedSensorDelayTicks;
        Stroke = stroke;
    }

    public string ExtendCommandChannelId { get; }
    public string ExtendedSensorChannelId { get; }
    public string RetractedSensorChannelId { get; }
    public int ExtendDurationTicks { get; }
    public int RetractDurationTicks { get; }
    public int ExtendedSensorDelayTicks { get; }
    public int RetractedSensorDelayTicks { get; }
    public double Stroke { get; }
}

public sealed record ConveyorRuntimeConfiguration : LayoutComponentRuntimeConfiguration
{
    public ConveyorRuntimeConfiguration(
        string id,
        string name,
        string runCommandChannelId,
        string reverseCommandChannelId,
        double speedUnitsPerSecond,
        double fixedStepSeconds,
        LayoutRuntimeTransform baseTransform,
        LayoutRuntimeSize size)
        : base(id, name, LayoutComponentKind.Conveyor, baseTransform, size)
    {
        RunCommandChannelId = RequiredIdentifier(runCommandChannelId, nameof(runCommandChannelId));
        ReverseCommandChannelId = RequiredIdentifier(
            reverseCommandChannelId,
            nameof(reverseCommandChannelId));
        if (string.Equals(RunCommandChannelId, ReverseCommandChannelId, StringComparison.Ordinal))
        {
            throw new ArgumentException("Conveyor run and reverse command channels must be distinct.");
        }

        if (!double.IsFinite(speedUnitsPerSecond) || speedUnitsPerSecond <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(speedUnitsPerSecond));
        }

        if (!double.IsFinite(fixedStepSeconds) || fixedStepSeconds <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(fixedStepSeconds));
        }

        SpeedUnitsPerSecond = speedUnitsPerSecond;
        FixedStepSeconds = fixedStepSeconds;
        TravelPerTick = speedUnitsPerSecond * fixedStepSeconds;
    }

    public string RunCommandChannelId { get; }
    public string ReverseCommandChannelId { get; }
    public double SpeedUnitsPerSecond { get; }
    public double FixedStepSeconds { get; }
    public double TravelPerTick { get; }
}

public sealed record WorkpieceRuntimeConfiguration : LayoutComponentRuntimeConfiguration
{
    public WorkpieceRuntimeConfiguration(
        string id,
        string name,
        string type,
        string conveyorComponentId,
        WorkpieceInspectionState inspectionState,
        LayoutRuntimeTransform baseTransform,
        LayoutRuntimeSize size)
        : base(id, name, LayoutComponentKind.Workpiece, baseTransform, size)
    {
        Type = RequiredIdentifier(type, nameof(type));
        ConveyorComponentId = RequiredIdentifier(
            conveyorComponentId,
            nameof(conveyorComponentId));
        if (!Enum.IsDefined(inspectionState))
        {
            throw new ArgumentOutOfRangeException(nameof(inspectionState));
        }

        InspectionState = inspectionState;
    }

    public string Type { get; }
    public string ConveyorComponentId { get; }
    public WorkpieceInspectionState InspectionState { get; }
}

public sealed record LoadLockRuntimeConfiguration
{
    public LoadLockRuntimeConfiguration(
        string id,
        string name,
        string outerDoorComponentId,
        string innerDoorComponentId,
        string evacuateCommandChannelId,
        string ventCommandChannelId,
        string vacuumReadySensorChannelId,
        string atmosphereReadySensorChannelId,
        int pumpDownDurationTicks,
        int ventDurationTicks)
    {
        Id = LayoutComponentRuntimeConfiguration.RequiredIdentifier(id, nameof(id));
        Name = LayoutComponentRuntimeConfiguration.RequiredIdentifier(name, nameof(name));
        OuterDoorComponentId = LayoutComponentRuntimeConfiguration.RequiredIdentifier(
            outerDoorComponentId,
            nameof(outerDoorComponentId));
        InnerDoorComponentId = LayoutComponentRuntimeConfiguration.RequiredIdentifier(
            innerDoorComponentId,
            nameof(innerDoorComponentId));
        EvacuateCommandChannelId = LayoutComponentRuntimeConfiguration.RequiredIdentifier(
            evacuateCommandChannelId,
            nameof(evacuateCommandChannelId));
        VentCommandChannelId = LayoutComponentRuntimeConfiguration.RequiredIdentifier(
            ventCommandChannelId,
            nameof(ventCommandChannelId));
        VacuumReadySensorChannelId = LayoutComponentRuntimeConfiguration.RequiredIdentifier(
            vacuumReadySensorChannelId,
            nameof(vacuumReadySensorChannelId));
        AtmosphereReadySensorChannelId = LayoutComponentRuntimeConfiguration.RequiredIdentifier(
            atmosphereReadySensorChannelId,
            nameof(atmosphereReadySensorChannelId));

        if (string.Equals(OuterDoorComponentId, InnerDoorComponentId, StringComparison.Ordinal))
        {
            throw new ArgumentException("Load-lock outer and inner doors must be distinct.");
        }

        if (new[]
            {
                EvacuateCommandChannelId,
                VentCommandChannelId,
                VacuumReadySensorChannelId,
                AtmosphereReadySensorChannelId
            }.Distinct(StringComparer.Ordinal).Count() != 4)
        {
            throw new ArgumentException("Load-lock command and feedback channels must be distinct.");
        }

        if (pumpDownDurationTicks <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pumpDownDurationTicks));
        }

        if (ventDurationTicks <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ventDurationTicks));
        }

        PumpDownDurationTicks = pumpDownDurationTicks;
        VentDurationTicks = ventDurationTicks;
    }

    public string Id { get; }
    public string Name { get; }
    public string OuterDoorComponentId { get; }
    public string InnerDoorComponentId { get; }
    public string EvacuateCommandChannelId { get; }
    public string VentCommandChannelId { get; }
    public string VacuumReadySensorChannelId { get; }
    public string AtmosphereReadySensorChannelId { get; }
    public int PumpDownDurationTicks { get; }
    public int VentDurationTicks { get; }
}

public sealed record WaferHandlerRuntimeConfiguration
{
    public WaferHandlerRuntimeConfiguration(
        string id,
        string name,
        string horizontalAxisId,
        string verticalAxisId,
        string workpieceComponentId,
        string sourcePresentSensorChannelId,
        string gateOpenSensorChannelId,
        string pickCommandChannelId,
        string placeCommandChannelId,
        string holdingFeedbackChannelId,
        string placedFeedbackChannelId,
        double pickHorizontalPosition,
        double pickVerticalPosition,
        double placeHorizontalPosition,
        double placeVerticalPosition)
    {
        Id = LayoutComponentRuntimeConfiguration.RequiredIdentifier(id, nameof(id));
        Name = LayoutComponentRuntimeConfiguration.RequiredIdentifier(name, nameof(name));
        HorizontalAxisId = LayoutComponentRuntimeConfiguration.RequiredIdentifier(horizontalAxisId, nameof(horizontalAxisId));
        VerticalAxisId = LayoutComponentRuntimeConfiguration.RequiredIdentifier(verticalAxisId, nameof(verticalAxisId));
        WorkpieceComponentId = LayoutComponentRuntimeConfiguration.RequiredIdentifier(workpieceComponentId, nameof(workpieceComponentId));
        SourcePresentSensorChannelId = LayoutComponentRuntimeConfiguration.RequiredIdentifier(sourcePresentSensorChannelId, nameof(sourcePresentSensorChannelId));
        GateOpenSensorChannelId = LayoutComponentRuntimeConfiguration.RequiredIdentifier(gateOpenSensorChannelId, nameof(gateOpenSensorChannelId));
        PickCommandChannelId = LayoutComponentRuntimeConfiguration.RequiredIdentifier(pickCommandChannelId, nameof(pickCommandChannelId));
        PlaceCommandChannelId = LayoutComponentRuntimeConfiguration.RequiredIdentifier(placeCommandChannelId, nameof(placeCommandChannelId));
        HoldingFeedbackChannelId = LayoutComponentRuntimeConfiguration.RequiredIdentifier(holdingFeedbackChannelId, nameof(holdingFeedbackChannelId));
        PlacedFeedbackChannelId = LayoutComponentRuntimeConfiguration.RequiredIdentifier(placedFeedbackChannelId, nameof(placedFeedbackChannelId));

        if (string.Equals(HorizontalAxisId, VerticalAxisId, StringComparison.Ordinal))
        {
            throw new ArgumentException("Wafer-handler axes must be distinct.");
        }

        if (new[] { SourcePresentSensorChannelId, GateOpenSensorChannelId, PickCommandChannelId, PlaceCommandChannelId, HoldingFeedbackChannelId, PlacedFeedbackChannelId }
            .Distinct(StringComparer.Ordinal).Count() != 6)
        {
            throw new ArgumentException("Wafer-handler command, condition, and feedback channels must be distinct.");
        }

        if (!double.IsFinite(pickHorizontalPosition) || !double.IsFinite(pickVerticalPosition)
            || !double.IsFinite(placeHorizontalPosition) || !double.IsFinite(placeVerticalPosition))
        {
            throw new ArgumentException("Wafer-handler pick and place positions must be finite.");
        }

        PickHorizontalPosition = pickHorizontalPosition;
        PickVerticalPosition = pickVerticalPosition;
        PlaceHorizontalPosition = placeHorizontalPosition;
        PlaceVerticalPosition = placeVerticalPosition;
    }

    public string Id { get; }
    public string Name { get; }
    public string HorizontalAxisId { get; }
    public string VerticalAxisId { get; }
    public string WorkpieceComponentId { get; }
    public string SourcePresentSensorChannelId { get; }
    public string GateOpenSensorChannelId { get; }
    public string PickCommandChannelId { get; }
    public string PlaceCommandChannelId { get; }
    public string HoldingFeedbackChannelId { get; }
    public string PlacedFeedbackChannelId { get; }
    public double PickHorizontalPosition { get; }
    public double PickVerticalPosition { get; }
    public double PlaceHorizontalPosition { get; }
    public double PlaceVerticalPosition { get; }
}

public sealed record InspectionSortRouterRuntimeConfiguration
{
    public InspectionSortRouterRuntimeConfiguration(
        string id,
        string name,
        string cameraId,
        string passConveyorComponentId,
        string ngConveyorComponentId,
        string passRunCommandChannelId,
        string ngRunCommandChannelId,
        string passRoutedFeedbackChannelId,
        string ngRoutedFeedbackChannelId)
    {
        Id = LayoutComponentRuntimeConfiguration.RequiredIdentifier(id, nameof(id));
        Name = LayoutComponentRuntimeConfiguration.RequiredIdentifier(name, nameof(name));
        CameraId = LayoutComponentRuntimeConfiguration.RequiredIdentifier(cameraId, nameof(cameraId));
        PassConveyorComponentId = LayoutComponentRuntimeConfiguration.RequiredIdentifier(passConveyorComponentId, nameof(passConveyorComponentId));
        NgConveyorComponentId = LayoutComponentRuntimeConfiguration.RequiredIdentifier(ngConveyorComponentId, nameof(ngConveyorComponentId));
        PassRunCommandChannelId = LayoutComponentRuntimeConfiguration.RequiredIdentifier(passRunCommandChannelId, nameof(passRunCommandChannelId));
        NgRunCommandChannelId = LayoutComponentRuntimeConfiguration.RequiredIdentifier(ngRunCommandChannelId, nameof(ngRunCommandChannelId));
        PassRoutedFeedbackChannelId = LayoutComponentRuntimeConfiguration.RequiredIdentifier(passRoutedFeedbackChannelId, nameof(passRoutedFeedbackChannelId));
        NgRoutedFeedbackChannelId = LayoutComponentRuntimeConfiguration.RequiredIdentifier(ngRoutedFeedbackChannelId, nameof(ngRoutedFeedbackChannelId));

        if (string.Equals(PassConveyorComponentId, NgConveyorComponentId, StringComparison.Ordinal)
            || new[] { PassRunCommandChannelId, NgRunCommandChannelId, PassRoutedFeedbackChannelId, NgRoutedFeedbackChannelId }
                .Distinct(StringComparer.Ordinal).Count() != 4)
        {
            throw new ArgumentException("Inspection sorter routes and their command/feedback channels must be distinct.");
        }
    }

    public string Id { get; }
    public string Name { get; }
    public string CameraId { get; }
    public string PassConveyorComponentId { get; }
    public string NgConveyorComponentId { get; }
    public string PassRunCommandChannelId { get; }
    public string NgRunCommandChannelId { get; }
    public string PassRoutedFeedbackChannelId { get; }
    public string NgRoutedFeedbackChannelId { get; }
}

public sealed record OhtHandoffRuntimeConfiguration
{
    public OhtHandoffRuntimeConfiguration(
        string id,
        string name,
        string transportConveyorComponentId,
        string forwardCommandChannelId,
        string reverseCommandChannelId,
        string routeAvailableSensorChannelId,
        string vehicleDockedSensorChannelId,
        string loadPortReadySensorChannelId,
        string carrierReceivedSensorChannelId,
        string handoffReadyFeedbackChannelId,
        string carrierTransferredFeedbackChannelId)
    {
        Id = LayoutComponentRuntimeConfiguration.RequiredIdentifier(id, nameof(id));
        Name = LayoutComponentRuntimeConfiguration.RequiredIdentifier(name, nameof(name));
        TransportConveyorComponentId = LayoutComponentRuntimeConfiguration.RequiredIdentifier(transportConveyorComponentId, nameof(transportConveyorComponentId));
        ForwardCommandChannelId = LayoutComponentRuntimeConfiguration.RequiredIdentifier(forwardCommandChannelId, nameof(forwardCommandChannelId));
        ReverseCommandChannelId = LayoutComponentRuntimeConfiguration.RequiredIdentifier(reverseCommandChannelId, nameof(reverseCommandChannelId));
        RouteAvailableSensorChannelId = LayoutComponentRuntimeConfiguration.RequiredIdentifier(routeAvailableSensorChannelId, nameof(routeAvailableSensorChannelId));
        VehicleDockedSensorChannelId = LayoutComponentRuntimeConfiguration.RequiredIdentifier(vehicleDockedSensorChannelId, nameof(vehicleDockedSensorChannelId));
        LoadPortReadySensorChannelId = LayoutComponentRuntimeConfiguration.RequiredIdentifier(loadPortReadySensorChannelId, nameof(loadPortReadySensorChannelId));
        CarrierReceivedSensorChannelId = LayoutComponentRuntimeConfiguration.RequiredIdentifier(carrierReceivedSensorChannelId, nameof(carrierReceivedSensorChannelId));
        HandoffReadyFeedbackChannelId = LayoutComponentRuntimeConfiguration.RequiredIdentifier(handoffReadyFeedbackChannelId, nameof(handoffReadyFeedbackChannelId));
        CarrierTransferredFeedbackChannelId = LayoutComponentRuntimeConfiguration.RequiredIdentifier(carrierTransferredFeedbackChannelId, nameof(carrierTransferredFeedbackChannelId));

        if (new[]
            {
                RouteAvailableSensorChannelId,
                VehicleDockedSensorChannelId,
                LoadPortReadySensorChannelId,
                CarrierReceivedSensorChannelId,
                HandoffReadyFeedbackChannelId,
                CarrierTransferredFeedbackChannelId
            }.Distinct(StringComparer.Ordinal).Count() != 6)
        {
            throw new ArgumentException("OHT handoff condition and feedback channels must be distinct.");
        }
    }

    public string Id { get; }
    public string Name { get; }
    public string TransportConveyorComponentId { get; }
    public string ForwardCommandChannelId { get; }
    public string ReverseCommandChannelId { get; }
    public string RouteAvailableSensorChannelId { get; }
    public string VehicleDockedSensorChannelId { get; }
    public string LoadPortReadySensorChannelId { get; }
    public string CarrierReceivedSensorChannelId { get; }
    public string HandoffReadyFeedbackChannelId { get; }
    public string CarrierTransferredFeedbackChannelId { get; }
}

public sealed record InspectionHandoffRuntimeConfiguration
{
    public InspectionHandoffRuntimeConfiguration(
        string id,
        string name,
        string cameraId,
        string inspectionPositionSensorChannelId,
        string resultAcceptedCommandChannelId,
        string inspectionReadyFeedbackChannelId,
        string inspectionCompleteFeedbackChannelId)
    {
        Id = LayoutComponentRuntimeConfiguration.RequiredIdentifier(id, nameof(id));
        Name = LayoutComponentRuntimeConfiguration.RequiredIdentifier(name, nameof(name));
        CameraId = LayoutComponentRuntimeConfiguration.RequiredIdentifier(cameraId, nameof(cameraId));
        InspectionPositionSensorChannelId = LayoutComponentRuntimeConfiguration.RequiredIdentifier(inspectionPositionSensorChannelId, nameof(inspectionPositionSensorChannelId));
        ResultAcceptedCommandChannelId = LayoutComponentRuntimeConfiguration.RequiredIdentifier(resultAcceptedCommandChannelId, nameof(resultAcceptedCommandChannelId));
        InspectionReadyFeedbackChannelId = LayoutComponentRuntimeConfiguration.RequiredIdentifier(inspectionReadyFeedbackChannelId, nameof(inspectionReadyFeedbackChannelId));
        InspectionCompleteFeedbackChannelId = LayoutComponentRuntimeConfiguration.RequiredIdentifier(inspectionCompleteFeedbackChannelId, nameof(inspectionCompleteFeedbackChannelId));

        if (new[]
            {
                InspectionPositionSensorChannelId,
                ResultAcceptedCommandChannelId,
                InspectionReadyFeedbackChannelId,
                InspectionCompleteFeedbackChannelId
            }.Distinct(StringComparer.Ordinal).Count() != 4)
        {
            throw new ArgumentException("Inspection handoff condition, command, and feedback channels must be distinct.");
        }
    }

    public string Id { get; }
    public string Name { get; }
    public string CameraId { get; }
    public string InspectionPositionSensorChannelId { get; }
    public string ResultAcceptedCommandChannelId { get; }
    public string InspectionReadyFeedbackChannelId { get; }
    public string InspectionCompleteFeedbackChannelId { get; }
}

public sealed record PrealignerRuntimeConfiguration
{
    public PrealignerRuntimeConfiguration(
        string id,
        string name,
        string rotaryStageComponentId,
        string rotaryAxisId,
        string clampCylinderComponentId,
        string waferPresentSensorChannelId,
        string alignmentAcceptedCommandChannelId,
        string alignmentReadyFeedbackChannelId,
        string alignmentCompleteFeedbackChannelId,
        double alignmentTargetDegrees,
        double alignmentToleranceDegrees)
    {
        Id = LayoutComponentRuntimeConfiguration.RequiredIdentifier(id, nameof(id));
        Name = LayoutComponentRuntimeConfiguration.RequiredIdentifier(name, nameof(name));
        RotaryStageComponentId = LayoutComponentRuntimeConfiguration.RequiredIdentifier(rotaryStageComponentId, nameof(rotaryStageComponentId));
        RotaryAxisId = LayoutComponentRuntimeConfiguration.RequiredIdentifier(rotaryAxisId, nameof(rotaryAxisId));
        ClampCylinderComponentId = LayoutComponentRuntimeConfiguration.RequiredIdentifier(clampCylinderComponentId, nameof(clampCylinderComponentId));
        WaferPresentSensorChannelId = LayoutComponentRuntimeConfiguration.RequiredIdentifier(waferPresentSensorChannelId, nameof(waferPresentSensorChannelId));
        AlignmentAcceptedCommandChannelId = LayoutComponentRuntimeConfiguration.RequiredIdentifier(alignmentAcceptedCommandChannelId, nameof(alignmentAcceptedCommandChannelId));
        AlignmentReadyFeedbackChannelId = LayoutComponentRuntimeConfiguration.RequiredIdentifier(alignmentReadyFeedbackChannelId, nameof(alignmentReadyFeedbackChannelId));
        AlignmentCompleteFeedbackChannelId = LayoutComponentRuntimeConfiguration.RequiredIdentifier(alignmentCompleteFeedbackChannelId, nameof(alignmentCompleteFeedbackChannelId));

        if (new[]
            {
                WaferPresentSensorChannelId,
                AlignmentAcceptedCommandChannelId,
                AlignmentReadyFeedbackChannelId,
                AlignmentCompleteFeedbackChannelId
            }.Distinct(StringComparer.Ordinal).Count() != 4)
        {
            throw new ArgumentException("Pre-aligner condition, command, and feedback channels must be distinct.");
        }

        if (!double.IsFinite(alignmentTargetDegrees))
        {
            throw new ArgumentOutOfRangeException(nameof(alignmentTargetDegrees));
        }

        if (!double.IsFinite(alignmentToleranceDegrees) || alignmentToleranceDegrees <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(alignmentToleranceDegrees));
        }

        AlignmentTargetDegrees = alignmentTargetDegrees;
        AlignmentToleranceDegrees = alignmentToleranceDegrees;
    }

    public string Id { get; }
    public string Name { get; }
    public string RotaryStageComponentId { get; }
    public string RotaryAxisId { get; }
    public string ClampCylinderComponentId { get; }
    public string WaferPresentSensorChannelId { get; }
    public string AlignmentAcceptedCommandChannelId { get; }
    public string AlignmentReadyFeedbackChannelId { get; }
    public string AlignmentCompleteFeedbackChannelId { get; }
    public double AlignmentTargetDegrees { get; }
    public double AlignmentToleranceDegrees { get; }
}

/// <summary>
/// Deep-copied, ordinally ordered layout configuration consumed by one
/// deterministic simulation component.
/// </summary>
public sealed class MachineLayoutRuntimeConfiguration
{
    public MachineLayoutRuntimeConfiguration(
        string id,
        string name,
        IEnumerable<LayoutComponentRuntimeConfiguration> components,
        IEnumerable<LoadLockRuntimeConfiguration>? loadLocks = null,
        IEnumerable<WaferHandlerRuntimeConfiguration>? waferHandlers = null,
        IEnumerable<InspectionSortRouterRuntimeConfiguration>? inspectionSortRouters = null,
        IEnumerable<InspectionHandoffRuntimeConfiguration>? inspectionHandoffs = null,
        IEnumerable<OhtHandoffRuntimeConfiguration>? ohtHandoffs = null,
        IEnumerable<PrealignerRuntimeConfiguration>? prealigners = null)
    {
        Id = LayoutComponentRuntimeConfiguration.RequiredIdentifier(id, nameof(id));
        Name = LayoutComponentRuntimeConfiguration.RequiredIdentifier(name, nameof(name));
        var validation = MachineLayoutRuntimeConfigurationValidator.Validate(
            components,
            loadLocks,
            waferHandlers,
            inspectionSortRouters,
            inspectionHandoffs,
            ohtHandoffs,
            prealigners);
        Components = validation.Components;
        LoadLocks = validation.LoadLocks;
        WaferHandlers = validation.WaferHandlers;
        InspectionSortRouters = validation.InspectionSortRouters;
        InspectionHandoffs = validation.InspectionHandoffs;
        OhtHandoffs = validation.OhtHandoffs;
        Prealigners = validation.Prealigners;
    }

    public string Id { get; }
    public string Name { get; }
    public ReadOnlyCollection<LayoutComponentRuntimeConfiguration> Components { get; }
    public ReadOnlyCollection<LoadLockRuntimeConfiguration> LoadLocks { get; }
    public ReadOnlyCollection<WaferHandlerRuntimeConfiguration> WaferHandlers { get; }
    public ReadOnlyCollection<InspectionSortRouterRuntimeConfiguration> InspectionSortRouters { get; }
    public ReadOnlyCollection<InspectionHandoffRuntimeConfiguration> InspectionHandoffs { get; }
    public ReadOnlyCollection<OhtHandoffRuntimeConfiguration> OhtHandoffs { get; }
    public ReadOnlyCollection<PrealignerRuntimeConfiguration> Prealigners { get; }
}
