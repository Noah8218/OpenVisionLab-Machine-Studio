using System.Collections.ObjectModel;
using System.Globalization;
using OpenVisionLab.Machine.Core.Axes;
using OpenVisionLab.Machine.Core.Channels;
using OpenVisionLab.Machine.Core.Devices;
using OpenVisionLab.Machine.Core.Layouts;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Core.Sequences;
using OpenVisionLab.Machine.IO.Channels;
using OpenVisionLab.Machine.Sequence.Compilation;
using OpenVisionLab.Machine.Simulation.Axis;
using OpenVisionLab.Machine.Simulation.Camera;
using OpenVisionLab.Machine.Simulation.Engine;
using OpenVisionLab.Machine.Simulation.Layout;
using OpenVisionLab.Machine.Simulation.Workpieces;

namespace OpenVisionLab.Machine.Simulation.Compilation;

public enum MachineProjectRuntimeCompilationErrorCode
{
    ProjectRequired,
    ProjectContractInvalid,
    FixedStepMismatch,
    AxisIdRequired,
    DuplicateAxisId,
    AxisConfigurationInvalid,
    SignalConfigurationInvalid,
    CameraKindMismatch,
    CameraIdRequired,
    DuplicateCameraId,
    CameraDecisionInvalid,
    CameraLegacyValueInvalid,
    CameraDelayInvalid,
    DuplicateSequenceId,
    SequenceCompilationFailed,
    LayoutValidationFailed,
    ActiveLayoutIdInvalid,
    ActiveLayoutRequired,
    ActiveLayoutNotFound,
    LayoutTargetOutsideActiveLayout,
    SensorDelayInvalid,
    CylinderTimingInvalid,
    LoadLockConfigurationInvalid,
    WaferHandlerConfigurationInvalid,
    InspectionSortRouterConfigurationInvalid,
    InspectionHandoffConfigurationInvalid,
    OhtHandoffConfigurationInvalid,
    PrealignerConfigurationInvalid,
    LayoutRuntimeInvalid,
    AutomaticSequenceIdRequired,
    AutomaticSequenceNotFound,
    AutomaticStartInputInvalid,
    AutomaticStartInputNotFound,
    AutomaticStartInputKindInvalid,
    AutomaticRepeatDelayInvalid,
    PickPlaceWorkpieceInvalid,
    RuntimeConfigurationInvalid,
    UnexpectedFailure
}

public sealed record MachineProjectRuntimeCompilationError(
    MachineProjectRuntimeCompilationErrorCode Code,
    string? TargetId,
    string Message);

/// <summary>
/// Typed, deterministically ordered result of compiling one authored machine
/// project into the UI-neutral simulation runtime contract.
/// </summary>
public sealed class MachineProjectRuntimeCompilationResult
{
    internal MachineProjectRuntimeCompilationResult(
        SimulationRuntimeConfiguration? configuration,
        IEnumerable<MachineProjectRuntimeCompilationError> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);

        Configuration = configuration;
        Errors = new ReadOnlyCollection<MachineProjectRuntimeCompilationError>(
            errors
                .Distinct()
                .OrderBy(error => error.Code)
                .ThenBy(error => error.TargetId ?? string.Empty, StringComparer.Ordinal)
                .ThenBy(error => error.Message, StringComparer.Ordinal)
                .ToArray());
    }

    public bool IsSuccess => Configuration is not null && Errors.Count == 0;
    public SimulationRuntimeConfiguration? Configuration { get; }
    public ReadOnlyCollection<MachineProjectRuntimeCompilationError> Errors { get; }
}

/// <summary>
/// Converts persisted project definitions into one validated runtime
/// configuration without referencing WPF, a ViewModel, or the running engine.
/// </summary>
public sealed class MachineProjectRuntimeCompiler
{
    private static readonly string[] LegacyDecisionKeys =
        ["placeholderDecision", "placeholderResult", "stubJudgment"];

    private readonly TimeSpan _fixedStep;

    public MachineProjectRuntimeCompiler(TimeSpan fixedStep)
    {
        if (fixedStep <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(fixedStep),
                fixedStep,
                "The runtime fixed step must be positive.");
        }

        _fixedStep = fixedStep;
    }

    public MachineProjectRuntimeCompilationResult Compile(MachineProjectDocument? project)
    {
        if (project is null)
        {
            return Failure(Error(
                MachineProjectRuntimeCompilationErrorCode.ProjectRequired,
                null,
                "A machine project is required."));
        }

        try
        {
            return CompileCore(project);
        }
        catch (Exception exception)
        {
            return Failure(Error(
                MachineProjectRuntimeCompilationErrorCode.UnexpectedFailure,
                string.IsNullOrWhiteSpace(project.Id) ? null : project.Id,
                $"Project runtime compilation failed: {exception.Message}"));
        }
    }

    private MachineProjectRuntimeCompilationResult CompileCore(MachineProjectDocument project)
    {
        var errors = new List<MachineProjectRuntimeCompilationError>();
        if (!TryGetProjectCollections(
                project,
                errors,
                out SimulationDefinition? simulation,
                out IReadOnlyList<VirtualAxisDefinition>? axisDefinitions,
                out IReadOnlyList<DeviceDefinition>? deviceDefinitions,
                out IReadOnlyList<ChannelDefinition>? channelDefinitions,
                out IReadOnlyList<SequenceDefinition>? sequenceDefinitions,
                out IReadOnlyList<MachineLayoutDefinition>? layoutDefinitions))
        {
            return Failure(errors);
        }

        TimeSpan projectFixedStep = TimeSpan.FromMilliseconds(simulation!.FixedStepMilliseconds);
        if (projectFixedStep != _fixedStep)
        {
            errors.Add(Error(
                MachineProjectRuntimeCompilationErrorCode.FixedStepMismatch,
                "simulation.fixedStepMilliseconds",
                $"Project fixed step {simulation.FixedStepMilliseconds} ms does not match runtime fixed step {_fixedStep.TotalMilliseconds.ToString("0.###", CultureInfo.InvariantCulture)} ms."));
            return Failure(errors);
        }

        IReadOnlyList<AxisConfiguration> axes = BuildAxes(axisDefinitions!, errors);
        IReadOnlyList<VirtualCameraConfiguration> cameras = BuildCameras(deviceDefinitions!, errors);
        IReadOnlyDictionary<string, ChannelKind>? channelKinds = BuildSignalContract(
            channelDefinitions!,
            errors);

        IReadOnlyList<CompiledSequence> sequences = Array.Empty<CompiledSequence>();
        if (!HasErrorsInDependencies(errors))
        {
            sequences = BuildSequences(
                sequenceDefinitions!,
                channelKinds!,
                axes,
                cameras,
                errors);
        }

        MachineLayoutRuntimeConfiguration? layout = BuildLayout(
            project,
            layoutDefinitions!,
            channelKinds,
            errors);
        AutomaticRunConfiguration? automaticRun = BuildAutomaticRun(
            simulation.AutomaticRun,
            sequences,
            channelKinds,
            errors);
        PickPlaceWorkpieceRuntimeConfiguration? pickPlaceWorkpiece = BuildPickPlaceWorkpiece(
            simulation.PickPlaceWorkpiece,
            axes,
            channelKinds,
            errors);

        if (errors.Count > 0)
        {
            return Failure(errors);
        }

        try
        {
            var configuration = new SimulationRuntimeConfiguration(
                axes,
                channelDefinitions!,
                sequences,
                cameras,
                automaticRun,
                layout,
                pickPlaceWorkpiece);
            return new MachineProjectRuntimeCompilationResult(configuration, Array.Empty<MachineProjectRuntimeCompilationError>());
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return Failure(Error(
                MachineProjectRuntimeCompilationErrorCode.RuntimeConfigurationInvalid,
                string.IsNullOrWhiteSpace(project.Id) ? null : project.Id,
                $"Runtime configuration is invalid: {exception.Message}"));
        }
    }

    private static bool TryGetProjectCollections(
        MachineProjectDocument project,
        ICollection<MachineProjectRuntimeCompilationError> errors,
        out SimulationDefinition? simulation,
        out IReadOnlyList<VirtualAxisDefinition>? axes,
        out IReadOnlyList<DeviceDefinition>? devices,
        out IReadOnlyList<ChannelDefinition>? channels,
        out IReadOnlyList<SequenceDefinition>? sequences,
        out IReadOnlyList<MachineLayoutDefinition>? layouts)
    {
        simulation = project.Simulation;
        axes = project.Axes;
        devices = project.Devices;
        channels = project.Channels;
        sequences = project.Sequences;
        layouts = project.Layouts;

        if (simulation is null)
        {
            errors.Add(Error(
                MachineProjectRuntimeCompilationErrorCode.ProjectContractInvalid,
                "simulation",
                "Project simulation settings are required."));
        }

        AddCollectionError(errors, axes, "axes");
        AddCollectionError(errors, devices, "devices");
        AddCollectionError(errors, channels, "channels");
        AddCollectionError(errors, sequences, "sequences");
        AddCollectionError(errors, layouts, "layouts");

        return errors.Count == 0;
    }

    private static void AddCollectionError<T>(
        ICollection<MachineProjectRuntimeCompilationError> errors,
        IReadOnlyList<T>? items,
        string targetId)
        where T : class
    {
        if (items is null || items.Any(item => item is null))
        {
            errors.Add(Error(
                MachineProjectRuntimeCompilationErrorCode.ProjectContractInvalid,
                targetId,
                $"Project {targetId} must be a non-null collection without null entries."));
        }
    }

    private static PickPlaceWorkpieceRuntimeConfiguration? BuildPickPlaceWorkpiece(
        PickPlaceWorkpieceDefinition? definition,
        IReadOnlyList<AxisConfiguration> axes,
        IReadOnlyDictionary<string, ChannelKind>? channelKinds,
        ICollection<MachineProjectRuntimeCompilationError> errors)
    {
        if (definition is null)
        {
            return null;
        }

        AxisConfiguration? xAxis = axes.FirstOrDefault(axis =>
            string.Equals(axis.Id, definition.XAxisId, StringComparison.Ordinal));
        AxisConfiguration? yAxis = axes.FirstOrDefault(axis =>
            string.Equals(axis.Id, definition.YAxisId, StringComparison.Ordinal));
        var valid = !string.IsNullOrWhiteSpace(definition.Id) &&
            !string.IsNullOrWhiteSpace(definition.Name) &&
            xAxis is not null &&
            yAxis is not null &&
            !string.Equals(definition.XAxisId, definition.YAxisId, StringComparison.Ordinal) &&
            channelKinds is not null &&
            channelKinds.TryGetValue(definition.GripperSignalId, out var gripperKind) &&
            gripperKind == ChannelKind.DigitalOutput &&
            double.IsFinite(definition.PickX) &&
            double.IsFinite(definition.PickY) &&
            definition.PickX >= xAxis.MinimumPosition &&
            definition.PickX <= xAxis.MaximumPosition &&
            definition.PickY >= yAxis.MinimumPosition &&
            definition.PickY <= yAxis.MaximumPosition;
        if (!valid)
        {
            errors.Add(Error(
                MachineProjectRuntimeCompilationErrorCode.PickPlaceWorkpieceInvalid,
                string.IsNullOrWhiteSpace(definition.Id) ? "simulation.pickPlaceWorkpiece" : definition.Id,
                "Pick-and-Place workpiece requires an id, name, distinct configured X/Y axes, " +
                "a digital-output gripper signal, and a finite Pick position within both axis limits."));
            return null;
        }

        return new PickPlaceWorkpieceRuntimeConfiguration(
            definition.Id,
            definition.Name,
            definition.XAxisId,
            definition.YAxisId,
            definition.GripperSignalId,
            definition.PickX,
            definition.PickY);
    }

    private static IReadOnlyList<AxisConfiguration> BuildAxes(
        IEnumerable<VirtualAxisDefinition> definitions,
        ICollection<MachineProjectRuntimeCompilationError> errors)
    {
        var axes = new List<AxisConfiguration>();
        var ids = new HashSet<string>(StringComparer.Ordinal);

        foreach (VirtualAxisDefinition definition in definitions)
        {
            if (string.IsNullOrWhiteSpace(definition.Id))
            {
                errors.Add(Error(
                    MachineProjectRuntimeCompilationErrorCode.AxisIdRequired,
                    definition.Id,
                    "Every axis requires an id."));
                continue;
            }

            if (!ids.Add(definition.Id))
            {
                errors.Add(Error(
                    MachineProjectRuntimeCompilationErrorCode.DuplicateAxisId,
                    definition.Id,
                    $"Axis id '{definition.Id}' is duplicated."));
                continue;
            }

            var axis = new AxisConfiguration
            {
                Id = definition.Id,
                Name = definition.Name,
                MinimumPosition = definition.SoftLimitMin ?? 0,
                MaximumPosition = definition.SoftLimitMax ?? 300,
                HomePosition = definition.HomePosition,
                MaximumVelocity = definition.MaxVelocity,
                Acceleration = definition.MaxAcceleration,
                Deceleration = definition.MaxDeceleration ?? definition.MaxAcceleration,
                FollowingErrorLimit = definition.FollowingErrorLimit ??
                    VirtualAxisDefinition.DefaultFollowingErrorLimit
            };

            if (!IsValidAxis(axis))
            {
                errors.Add(Error(
                    MachineProjectRuntimeCompilationErrorCode.AxisConfigurationInvalid,
                    definition.Id,
                    $"Axis '{definition.Id}' has invalid limits or motion parameters."));
                continue;
            }

            axes.Add(axis);
        }

        return axes;
    }

    private static bool IsValidAxis(AxisConfiguration axis) =>
        double.IsFinite(axis.MinimumPosition) &&
        double.IsFinite(axis.MaximumPosition) &&
        double.IsFinite(axis.HomePosition) &&
        axis.MinimumPosition <= axis.MaximumPosition &&
        axis.HomePosition >= axis.MinimumPosition &&
        axis.HomePosition <= axis.MaximumPosition &&
        double.IsFinite(axis.MaximumVelocity) &&
        axis.MaximumVelocity > 0 &&
        double.IsFinite(axis.Acceleration) &&
        axis.Acceleration > 0 &&
        double.IsFinite(axis.Deceleration) &&
        axis.Deceleration > 0 &&
        double.IsFinite(axis.FollowingErrorLimit) &&
        axis.FollowingErrorLimit > 0;

    private IReadOnlyList<VirtualCameraConfiguration> BuildCameras(
        IEnumerable<DeviceDefinition> devices,
        ICollection<MachineProjectRuntimeCompilationError> errors)
    {
        var cameras = new List<VirtualCameraConfiguration>();
        var ids = new HashSet<string>(StringComparer.Ordinal);

        foreach (DeviceDefinition device in devices)
        {
            if (device.Camera is not null && device.Kind != DeviceKind.Camera)
            {
                errors.Add(Error(
                    MachineProjectRuntimeCompilationErrorCode.CameraKindMismatch,
                    device.Id,
                    $"Device '{device.Id}' declares camera timing but its kind is {device.Kind}."));
                continue;
            }

            if (device.Kind != DeviceKind.Camera)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(device.Id))
            {
                errors.Add(Error(
                    MachineProjectRuntimeCompilationErrorCode.CameraIdRequired,
                    device.Id,
                    "Every virtual camera requires an id."));
                continue;
            }

            if (!ids.Add(device.Id))
            {
                errors.Add(Error(
                    MachineProjectRuntimeCompilationErrorCode.DuplicateCameraId,
                    device.Id,
                    $"Virtual camera id '{device.Id}' is duplicated."));
                continue;
            }

            if (!TryResolveCameraDefinition(device, errors, out VirtualCameraDefinition authored))
            {
                continue;
            }

            if (!Enum.IsDefined(authored.PlaceholderDecision))
            {
                errors.Add(Error(
                    MachineProjectRuntimeCompilationErrorCode.CameraDecisionInvalid,
                    device.Id,
                    $"Virtual camera '{device.Id}' has an undefined placeholder decision."));
                continue;
            }

            if (!TryConvertDelayToTicks(
                    authored.ExposureDelayMilliseconds,
                    allowZero: false,
                    out int exposureTicks) ||
                !TryConvertDelayToTicks(
                    authored.TransferDelayMilliseconds,
                    allowZero: false,
                    out int transferTicks))
            {
                errors.Add(Error(
                    MachineProjectRuntimeCompilationErrorCode.CameraDelayInvalid,
                    device.Id,
                    $"Virtual camera '{device.Id}' exposure and transfer delays must be positive exact multiples of {_fixedStep.TotalMilliseconds.ToString("0.###", CultureInfo.InvariantCulture)} ms."));
                continue;
            }

            try
            {
                cameras.Add(new VirtualCameraConfiguration(
                    device.Id,
                    string.IsNullOrWhiteSpace(device.Name) ? device.Id : device.Name,
                    exposureTicks,
                    transferTicks,
                    authored.PlaceholderDecision));
            }
            catch (ArgumentException exception)
            {
                errors.Add(Error(
                    MachineProjectRuntimeCompilationErrorCode.CameraDecisionInvalid,
                    device.Id,
                    $"Virtual camera '{device.Id}' is invalid: {exception.Message}"));
            }
        }

        return cameras;
    }

    private static bool TryResolveCameraDefinition(
        DeviceDefinition device,
        ICollection<MachineProjectRuntimeCompilationError> errors,
        out VirtualCameraDefinition definition)
    {
        if (device.Camera is not null)
        {
            definition = device.Camera;
            return true;
        }

        definition = new VirtualCameraDefinition();
        IReadOnlyDictionary<string, string>? properties = device.Properties;
        if (properties is null)
        {
            return true;
        }

        if (!TryReadLegacyDelay(
                device.Id,
                properties,
                "exposureDelayMs",
                definition.ExposureDelayMilliseconds,
                errors,
                out int exposure) ||
            !TryReadLegacyDelay(
                device.Id,
                properties,
                "transferDelayMs",
                definition.TransferDelayMilliseconds,
                errors,
                out int transfer))
        {
            return false;
        }

        definition.ExposureDelayMilliseconds = exposure;
        definition.TransferDelayMilliseconds = transfer;
        string? decisionText = LegacyDecisionKeys
            .Select(key => properties.TryGetValue(key, out string? value) ? value : null)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

        PlaceholderInspectionDecision decision = definition.PlaceholderDecision;
        if (decisionText is not null &&
            (!Enum.TryParse(decisionText, true, out decision) ||
             !Enum.IsDefined(decision)))
        {
            errors.Add(Error(
                MachineProjectRuntimeCompilationErrorCode.CameraLegacyValueInvalid,
                device.Id,
                $"Virtual camera '{device.Id}' has invalid placeholder decision '{decisionText}'."));
            return false;
        }

        if (decisionText is not null)
        {
            definition.PlaceholderDecision = decision;
        }

        return true;
    }

    private static bool TryReadLegacyDelay(
        string cameraId,
        IReadOnlyDictionary<string, string> properties,
        string key,
        int defaultValue,
        ICollection<MachineProjectRuntimeCompilationError> errors,
        out int milliseconds)
    {
        milliseconds = defaultValue;
        if (!properties.TryGetValue(key, out string? text))
        {
            return true;
        }

        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out milliseconds))
        {
            return true;
        }

        errors.Add(Error(
            MachineProjectRuntimeCompilationErrorCode.CameraLegacyValueInvalid,
            cameraId,
            $"Virtual camera '{cameraId}' has invalid {key} value '{text}'."));
        return false;
    }

    private static IReadOnlyDictionary<string, ChannelKind>? BuildSignalContract(
        IReadOnlyList<ChannelDefinition> definitions,
        ICollection<MachineProjectRuntimeCompilationError> errors)
    {
        SignalHubCreationResult result = DeterministicSignalHub.Create(definitions);
        if (!result.IsAccepted)
        {
            errors.Add(Error(
                MachineProjectRuntimeCompilationErrorCode.SignalConfigurationInvalid,
                result.ChannelId,
                $"Signal configuration failed: {result.ErrorCode}."));
            return null;
        }

        return definitions.ToDictionary(
            definition => definition.Id,
            definition => definition.Kind,
            StringComparer.Ordinal);
    }

    private static IReadOnlyList<CompiledSequence> BuildSequences(
        IEnumerable<SequenceDefinition> definitions,
        IReadOnlyDictionary<string, ChannelKind> channelKinds,
        IEnumerable<AxisConfiguration> axes,
        IEnumerable<VirtualCameraConfiguration> cameras,
        ICollection<MachineProjectRuntimeCompilationError> errors)
    {
        var targets = new SequenceCompilationTargets(
            channelKinds,
            axes.Select(axis => axis.Id),
            cameras.Select(camera => camera.Id));
        var compiler = new SequenceCompiler();
        var compiled = new List<CompiledSequence>();
        var ids = new HashSet<string>(StringComparer.Ordinal);

        foreach (SequenceDefinition definition in definitions)
        {
            if (!string.IsNullOrWhiteSpace(definition.Id) && !ids.Add(definition.Id))
            {
                errors.Add(Error(
                    MachineProjectRuntimeCompilationErrorCode.DuplicateSequenceId,
                    definition.Id,
                    $"Sequence id '{definition.Id}' is duplicated."));
                continue;
            }

            SequenceCompilationResult result = compiler.Compile(definition, targets);
            if (!result.IsSuccess)
            {
                foreach (SequenceCompilationError error in result.Errors)
                {
                    errors.Add(Error(
                        MachineProjectRuntimeCompilationErrorCode.SequenceCompilationFailed,
                        error.StepId ?? definition.Id,
                        $"{error.Code}: {error.Message}"));
                }
                continue;
            }

            compiled.Add(result.Sequence!);
        }

        return compiled;
    }

    private MachineLayoutRuntimeConfiguration? BuildLayout(
        MachineProjectDocument project,
        IReadOnlyList<MachineLayoutDefinition> layouts,
        IReadOnlyDictionary<string, ChannelKind>? channelKinds,
        ICollection<MachineProjectRuntimeCompilationError> errors)
    {
        MachineProjectLayoutValidationResult validation =
            new MachineProjectLayoutValidator().Validate(project);
        foreach (MachineProjectLayoutValidationError error in validation.Errors)
        {
            errors.Add(Error(
                MachineProjectRuntimeCompilationErrorCode.LayoutValidationFailed,
                error.ComponentId ?? error.LayoutId,
                $"{error.Code}: {error.Message}"));
        }

        if (!validation.IsValid)
        {
            return null;
        }

        string? activeLayoutId = project.Simulation.ActiveLayoutId;
        if (layouts.Count == 0)
        {
            if (activeLayoutId is not null && !string.IsNullOrWhiteSpace(activeLayoutId))
            {
                errors.Add(Error(
                    MachineProjectRuntimeCompilationErrorCode.ActiveLayoutNotFound,
                    "simulation.activeLayoutId",
                    $"Active layout '{activeLayoutId}' was not found because the project has no layouts."));
            }
            else if (activeLayoutId is not null)
            {
                errors.Add(Error(
                    MachineProjectRuntimeCompilationErrorCode.ActiveLayoutIdInvalid,
                    "simulation.activeLayoutId",
                    "Active layout id cannot be blank."));
            }

            return null;
        }

        MachineLayoutDefinition? activeLayout;
        if (activeLayoutId is null)
        {
            if (layouts.Count != 1)
            {
                errors.Add(Error(
                    MachineProjectRuntimeCompilationErrorCode.ActiveLayoutRequired,
                    "simulation.activeLayoutId",
                    "simulation.activeLayoutId is required when a project contains more than one layout."));
                return null;
            }

            activeLayout = layouts[0];
        }
        else if (string.IsNullOrWhiteSpace(activeLayoutId) ||
                 !string.Equals(activeLayoutId, activeLayoutId.Trim(), StringComparison.Ordinal))
        {
            errors.Add(Error(
                MachineProjectRuntimeCompilationErrorCode.ActiveLayoutIdInvalid,
                "simulation.activeLayoutId",
                "Active layout id cannot be blank or contain leading/trailing whitespace."));
            return null;
        }
        else
        {
            activeLayout = layouts.FirstOrDefault(layout =>
                string.Equals(layout.Id, activeLayoutId, StringComparison.Ordinal));
            if (activeLayout is null)
            {
                errors.Add(Error(
                    MachineProjectRuntimeCompilationErrorCode.ActiveLayoutNotFound,
                    "simulation.activeLayoutId",
                    $"Active layout '{activeLayoutId}' was not found."));
                return null;
            }
        }

        var axesById = project.Axes
            .GroupBy(axis => axis.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var devicesById = project.Devices
            .GroupBy(device => device.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var activeComponentIds = activeLayout.Components
            .Select(component => component.Id)
            .ToHashSet(StringComparer.Ordinal);
        var runtimeComponents = new List<LayoutComponentRuntimeConfiguration>();

        foreach (LayoutComponentDefinition component in activeLayout.Components)
        {
            var transform = new LayoutRuntimeTransform(
                component.Transform.X,
                component.Transform.Y,
                component.Transform.RotationDegrees);
            var size = new LayoutRuntimeSize(component.Size.Width, component.Size.Height);

            switch (component.Kind)
            {
                case LayoutComponentKind.MachineFrame:
                    runtimeComponents.Add(new MachineFrameRuntimeConfiguration(
                        component.Id,
                        component.Name,
                        transform,
                        size));
                    break;

                case LayoutComponentKind.LinearStage:
                    VirtualAxisDefinition axis = axesById[component.BehaviorBindingId!];
                    runtimeComponents.Add(new LinearStageRuntimeConfiguration(
                        component.Id,
                        component.Name,
                        axis.Id,
                        axis.HomePosition,
                        transform,
                        size));
                    break;

                case LayoutComponentKind.RotaryStage:
                    VirtualAxisDefinition rotaryAxis = axesById[component.BehaviorBindingId!];
                    runtimeComponents.Add(new RotaryStageRuntimeConfiguration(
                        component.Id,
                        component.Name,
                        rotaryAxis.Id,
                        rotaryAxis.HomePosition,
                        transform,
                        size));
                    break;

                case LayoutComponentKind.DigitalSensor:
                    DeviceDefinition device = devicesById[component.BehaviorBindingId!];
                    DigitalSensorDefinition sensor = device.Sensor!;
                    if (!activeComponentIds.Contains(sensor.TargetComponentId))
                    {
                        errors.Add(Error(
                            MachineProjectRuntimeCompilationErrorCode.LayoutTargetOutsideActiveLayout,
                            component.Id,
                            $"Sensor '{component.Id}' target '{sensor.TargetComponentId}' is not part of active layout '{activeLayout.Id}'."));
                        break;
                    }

                    bool onValid = TryConvertDelayToTicks(
                        sensor.OnDelayMilliseconds,
                        allowZero: true,
                        out int onDelayTicks);
                    bool offValid = TryConvertDelayToTicks(
                        sensor.OffDelayMilliseconds,
                        allowZero: true,
                        out int offDelayTicks);
                    if (!onValid || !offValid)
                    {
                        errors.Add(Error(
                            MachineProjectRuntimeCompilationErrorCode.SensorDelayInvalid,
                            component.Id,
                            $"Digital sensor '{component.Id}' on/off delays must be zero or exact multiples of {_fixedStep.TotalMilliseconds.ToString("0.###", CultureInfo.InvariantCulture)} ms."));
                        break;
                    }

                    runtimeComponents.Add(new DigitalSensorRuntimeConfiguration(
                        component.Id,
                        component.Name,
                        sensor.OutputChannelId,
                        sensor.TargetComponentId,
                        onDelayTicks,
                        offDelayTicks,
                        transform,
                        size));
                    break;

                case LayoutComponentKind.PneumaticCylinder:
                    DeviceDefinition cylinderDevice = devicesById[component.BehaviorBindingId!];
                    PneumaticCylinderDefinition cylinder = cylinderDevice.Cylinder!;
                    bool extendValid = TryConvertDelayToTicks(
                        cylinder.ExtendDurationMilliseconds,
                        allowZero: false,
                        out int extendDurationTicks);
                    bool retractValid = TryConvertDelayToTicks(
                        cylinder.RetractDurationMilliseconds,
                        allowZero: false,
                        out int retractDurationTicks);
                    bool extendedDelayValid = TryConvertDelayToTicks(
                        cylinder.ExtendedSensorDelayMilliseconds,
                        allowZero: true,
                        out int extendedSensorDelayTicks);
                    bool retractedDelayValid = TryConvertDelayToTicks(
                        cylinder.RetractedSensorDelayMilliseconds,
                        allowZero: true,
                        out int retractedSensorDelayTicks);
                    if (!extendValid || !retractValid || !extendedDelayValid || !retractedDelayValid)
                    {
                        errors.Add(Error(
                            MachineProjectRuntimeCompilationErrorCode.CylinderTimingInvalid,
                            component.Id,
                            $"Pneumatic cylinder '{component.Id}' durations must be positive and " +
                            $"sensor delays must be non-negative exact multiples of " +
                            $"{_fixedStep.TotalMilliseconds.ToString("0.###", CultureInfo.InvariantCulture)} ms."));
                        break;
                    }

                    runtimeComponents.Add(new PneumaticCylinderRuntimeConfiguration(
                        component.Id,
                        component.Name,
                        cylinder.ExtendCommandChannelId,
                        cylinder.ExtendedSensorChannelId,
                        cylinder.RetractedSensorChannelId,
                        extendDurationTicks,
                        retractDurationTicks,
                        extendedSensorDelayTicks,
                        retractedSensorDelayTicks,
                        cylinder.Stroke,
                        transform,
                        size));
                    break;

                case LayoutComponentKind.Conveyor:
                    DeviceDefinition conveyorDevice = devicesById[component.BehaviorBindingId!];
                    ConveyorDefinition conveyor = conveyorDevice.Conveyor!;
                    runtimeComponents.Add(new ConveyorRuntimeConfiguration(
                        component.Id,
                        component.Name,
                        conveyor.RunCommandChannelId,
                        conveyor.ReverseCommandChannelId,
                        conveyor.SpeedUnitsPerSecond,
                        _fixedStep.TotalSeconds,
                        transform,
                        size));
                    break;

                case LayoutComponentKind.Workpiece:
                    DeviceDefinition workpieceDevice = devicesById[component.BehaviorBindingId!];
                    WorkpieceDefinition workpiece = workpieceDevice.Workpiece!;
                    runtimeComponents.Add(new WorkpieceRuntimeConfiguration(
                        component.Id,
                        component.Name,
                        workpiece.Type,
                        workpiece.ConveyorComponentId,
                        workpiece.InspectionState,
                        transform,
                        size));
                    break;
            }
        }

        if (errors.Any(error => error.Code is
                MachineProjectRuntimeCompilationErrorCode.LayoutTargetOutsideActiveLayout or
                MachineProjectRuntimeCompilationErrorCode.SensorDelayInvalid or
                MachineProjectRuntimeCompilationErrorCode.CylinderTimingInvalid))
        {
            return null;
        }

        IReadOnlyList<LoadLockRuntimeConfiguration> loadLocks = BuildLoadLocks(
            project.Devices,
            runtimeComponents,
            channelKinds,
            errors);
        if (errors.Any(error =>
                error.Code == MachineProjectRuntimeCompilationErrorCode.LoadLockConfigurationInvalid))
        {
            return null;
        }

        IReadOnlyList<WaferHandlerRuntimeConfiguration> waferHandlers = BuildWaferHandlers(
            project.Devices,
            runtimeComponents,
            axesById,
            channelKinds,
            errors);
        if (errors.Any(error =>
                error.Code == MachineProjectRuntimeCompilationErrorCode.WaferHandlerConfigurationInvalid))
        {
            return null;
        }

        IReadOnlyList<InspectionSortRouterRuntimeConfiguration> inspectionSortRouters =
            BuildInspectionSortRouters(
                project.Devices,
                runtimeComponents,
                channelKinds,
                errors);
        if (errors.Any(error =>
                error.Code == MachineProjectRuntimeCompilationErrorCode.InspectionSortRouterConfigurationInvalid))
        {
            return null;
        }

        IReadOnlyList<InspectionHandoffRuntimeConfiguration> inspectionHandoffs =
            BuildInspectionHandoffs(
                project.Devices,
                channelKinds,
                errors);
        if (errors.Any(error =>
                error.Code == MachineProjectRuntimeCompilationErrorCode.InspectionHandoffConfigurationInvalid))
        {
            return null;
        }

        IReadOnlyList<OhtHandoffRuntimeConfiguration> ohtHandoffs = BuildOhtHandoffs(
            project.Devices,
            runtimeComponents,
            channelKinds,
            errors);
        if (errors.Any(error =>
                error.Code == MachineProjectRuntimeCompilationErrorCode.OhtHandoffConfigurationInvalid))
        {
            return null;
        }

        IReadOnlyList<PrealignerRuntimeConfiguration> prealigners = BuildPrealigners(
            project.Devices,
            runtimeComponents,
            axesById,
            channelKinds,
            errors);
        if (errors.Any(error =>
                error.Code == MachineProjectRuntimeCompilationErrorCode.PrealignerConfigurationInvalid))
        {
            return null;
        }

        try
        {
            return new MachineLayoutRuntimeConfiguration(
                activeLayout.Id,
                activeLayout.Name,
                runtimeComponents,
                loadLocks,
                waferHandlers,
                inspectionSortRouters,
                inspectionHandoffs,
                ohtHandoffs,
                prealigners);
        }
        catch (ArgumentException exception)
        {
            errors.Add(Error(
                MachineProjectRuntimeCompilationErrorCode.LayoutRuntimeInvalid,
                activeLayout.Id,
                $"Active layout runtime configuration is invalid: {exception.Message}"));
            return null;
        }
    }

    private IReadOnlyList<LoadLockRuntimeConfiguration> BuildLoadLocks(
        IEnumerable<DeviceDefinition> devices,
        IReadOnlyCollection<LayoutComponentRuntimeConfiguration> runtimeComponents,
        IReadOnlyDictionary<string, ChannelKind>? channelKinds,
        ICollection<MachineProjectRuntimeCompilationError> errors)
    {
        var componentsById = runtimeComponents.ToDictionary(
            component => component.Id,
            StringComparer.Ordinal);
        var loadLocks = new List<LoadLockRuntimeConfiguration>();

        foreach (DeviceDefinition device in devices
                     .Where(device => device.Kind == DeviceKind.LoadLock)
                     .OrderBy(device => device.Id, StringComparer.Ordinal))
        {
            LoadLockDefinition? definition = device.LoadLock;
            string targetId = string.IsNullOrWhiteSpace(device.Id) ? "devices.loadLock" : device.Id;
            if (definition is null)
            {
                AddLoadLockError(errors, targetId, "Load-lock settings are required.");
                continue;
            }

            if (!IsCylinder(definition.OuterDoorComponentId, componentsById)
                || !IsCylinder(definition.InnerDoorComponentId, componentsById)
                || string.Equals(
                    definition.OuterDoorComponentId,
                    definition.InnerDoorComponentId,
                    StringComparison.Ordinal))
            {
                AddLoadLockError(
                    errors,
                    targetId,
                    "Load-lock outer and inner door ids must identify two distinct pneumatic cylinders in the active layout.");
                continue;
            }

            if (channelKinds is null
                || !HasChannelKind(
                    definition.EvacuateCommandChannelId,
                    ChannelKind.DigitalOutput,
                    channelKinds)
                || !HasChannelKind(
                    definition.VentCommandChannelId,
                    ChannelKind.DigitalOutput,
                    channelKinds)
                || !HasChannelKind(
                    definition.VacuumReadySensorChannelId,
                    ChannelKind.DigitalInput,
                    channelKinds)
                || !HasChannelKind(
                    definition.AtmosphereReadySensorChannelId,
                    ChannelKind.DigitalInput,
                    channelKinds))
            {
                AddLoadLockError(
                    errors,
                    targetId,
                    "Load-lock evacuate/vent channels must be DigitalOutput and vacuum/atmosphere feedback channels must be DigitalInput.");
                continue;
            }

            bool pumpDownValid = TryConvertDelayToTicks(
                definition.PumpDownDurationMilliseconds,
                allowZero: false,
                out int pumpDownDurationTicks);
            bool ventValid = TryConvertDelayToTicks(
                definition.VentDurationMilliseconds,
                allowZero: false,
                out int ventDurationTicks);
            if (!pumpDownValid || !ventValid)
            {
                AddLoadLockError(
                    errors,
                    targetId,
                    $"Load-lock pump-down and vent durations must be positive exact multiples of {_fixedStep.TotalMilliseconds.ToString("0.###", CultureInfo.InvariantCulture)} ms.");
                continue;
            }

            try
            {
                loadLocks.Add(new LoadLockRuntimeConfiguration(
                    device.Id,
                    device.Name,
                    definition.OuterDoorComponentId,
                    definition.InnerDoorComponentId,
                    definition.EvacuateCommandChannelId,
                    definition.VentCommandChannelId,
                    definition.VacuumReadySensorChannelId,
                    definition.AtmosphereReadySensorChannelId,
                    pumpDownDurationTicks,
                    ventDurationTicks));
            }
            catch (ArgumentException exception)
            {
                AddLoadLockError(errors, targetId, exception.Message);
            }
        }

        return loadLocks;
    }

    private static bool IsCylinder(
        string componentId,
        IReadOnlyDictionary<string, LayoutComponentRuntimeConfiguration> componentsById) =>
        !string.IsNullOrWhiteSpace(componentId)
        && componentsById.TryGetValue(componentId, out var component)
        && component is PneumaticCylinderRuntimeConfiguration;

    private static IReadOnlyList<WaferHandlerRuntimeConfiguration> BuildWaferHandlers(
        IEnumerable<DeviceDefinition> devices,
        IReadOnlyCollection<LayoutComponentRuntimeConfiguration> runtimeComponents,
        IReadOnlyDictionary<string, VirtualAxisDefinition> axesById,
        IReadOnlyDictionary<string, ChannelKind>? channelKinds,
        ICollection<MachineProjectRuntimeCompilationError> errors)
    {
        var workpieceIds = runtimeComponents
            .OfType<WorkpieceRuntimeConfiguration>()
            .Select(workpiece => workpiece.Id)
            .ToHashSet(StringComparer.Ordinal);
        var handlers = new List<WaferHandlerRuntimeConfiguration>();

        foreach (DeviceDefinition device in devices
                     .Where(device => device.Kind == DeviceKind.Handler)
                     .OrderBy(device => device.Id, StringComparer.Ordinal))
        {
            string targetId = string.IsNullOrWhiteSpace(device.Id) ? "devices.waferHandler" : device.Id;
            WaferHandlerDefinition? definition = device.WaferHandler;
            if (definition is null)
            {
                AddWaferHandlerError(errors, targetId, "Wafer-handler settings are required.");
                continue;
            }

            if (!axesById.TryGetValue(definition.HorizontalAxisId, out VirtualAxisDefinition? horizontal)
                || !axesById.TryGetValue(definition.VerticalAxisId, out VirtualAxisDefinition? vertical)
                || horizontal.Kind != AxisKind.Linear
                || vertical.Kind != AxisKind.Linear
                || string.Equals(horizontal.Id, vertical.Id, StringComparison.Ordinal))
            {
                AddWaferHandlerError(errors, targetId, "Wafer-handler axes must identify two distinct configured linear axes.");
                continue;
            }

            if (!workpieceIds.Contains(definition.WorkpieceComponentId))
            {
                AddWaferHandlerError(errors, targetId, "Wafer-handler workpiece must identify a workpiece in the active layout.");
                continue;
            }

            if (!PositionWithin(horizontal, definition.PickHorizontalPosition)
                || !PositionWithin(vertical, definition.PickVerticalPosition)
                || !PositionWithin(horizontal, definition.PlaceHorizontalPosition)
                || !PositionWithin(vertical, definition.PlaceVerticalPosition))
            {
                AddWaferHandlerError(errors, targetId, "Wafer-handler pick and place positions must be finite and within their axis soft limits.");
                continue;
            }

            if (channelKinds is null
                || !HasChannelKind(definition.SourcePresentSensorChannelId, ChannelKind.DigitalInput, channelKinds)
                || !HasChannelKind(definition.GateOpenSensorChannelId, ChannelKind.DigitalInput, channelKinds)
                || !HasChannelKind(definition.PickCommandChannelId, ChannelKind.DigitalOutput, channelKinds)
                || !HasChannelKind(definition.PlaceCommandChannelId, ChannelKind.DigitalOutput, channelKinds)
                || !HasChannelKind(definition.HoldingFeedbackChannelId, ChannelKind.DigitalInput, channelKinds)
                || !HasChannelKind(definition.PlacedFeedbackChannelId, ChannelKind.DigitalInput, channelKinds))
            {
                AddWaferHandlerError(errors, targetId, "Wafer-handler conditions/feedback must be DigitalInput and pick/place commands must be DigitalOutput.");
                continue;
            }

            try
            {
                handlers.Add(new WaferHandlerRuntimeConfiguration(
                    device.Id,
                    device.Name,
                    definition.HorizontalAxisId,
                    definition.VerticalAxisId,
                    definition.WorkpieceComponentId,
                    definition.SourcePresentSensorChannelId,
                    definition.GateOpenSensorChannelId,
                    definition.PickCommandChannelId,
                    definition.PlaceCommandChannelId,
                    definition.HoldingFeedbackChannelId,
                    definition.PlacedFeedbackChannelId,
                    definition.PickHorizontalPosition,
                    definition.PickVerticalPosition,
                    definition.PlaceHorizontalPosition,
                    definition.PlaceVerticalPosition));
            }
            catch (ArgumentException exception)
            {
                AddWaferHandlerError(errors, targetId, exception.Message);
            }
        }

        return handlers;
    }

    private static bool PositionWithin(VirtualAxisDefinition axis, double position) =>
        double.IsFinite(position)
        && axis.SoftLimitMin.HasValue
        && axis.SoftLimitMax.HasValue
        && position >= axis.SoftLimitMin.Value
        && position <= axis.SoftLimitMax.Value;

    private static IReadOnlyList<InspectionSortRouterRuntimeConfiguration> BuildInspectionSortRouters(
        IEnumerable<DeviceDefinition> devices,
        IReadOnlyCollection<LayoutComponentRuntimeConfiguration> runtimeComponents,
        IReadOnlyDictionary<string, ChannelKind>? channelKinds,
        ICollection<MachineProjectRuntimeCompilationError> errors)
    {
        DeviceDefinition[] deviceArray = devices.ToArray();
        var cameraIds = deviceArray
            .Where(device => device is { Kind: DeviceKind.Camera, Camera: not null })
            .Select(device => device.Id)
            .ToHashSet(StringComparer.Ordinal);
        var conveyorsById = runtimeComponents
            .OfType<ConveyorRuntimeConfiguration>()
            .ToDictionary(conveyor => conveyor.Id, StringComparer.Ordinal);
        var sorters = new List<InspectionSortRouterRuntimeConfiguration>();

        foreach (DeviceDefinition device in deviceArray
                     .Where(device => device.Kind == DeviceKind.Sorter)
                     .OrderBy(device => device.Id, StringComparer.Ordinal))
        {
            string targetId = string.IsNullOrWhiteSpace(device.Id) ? "devices.inspectionSorter" : device.Id;
            InspectionSortRouterDefinition? definition = device.InspectionSortRouter;
            if (definition is null)
            {
                AddInspectionSortRouterError(errors, targetId, "Inspection-sorter settings are required.");
                continue;
            }

            if (!cameraIds.Contains(definition.CameraId))
            {
                AddInspectionSortRouterError(errors, targetId, "Inspection sorter camera must identify a configured virtual camera.");
                continue;
            }

            if (!conveyorsById.TryGetValue(definition.PassConveyorComponentId, out var passConveyor)
                || !conveyorsById.TryGetValue(definition.NgConveyorComponentId, out var ngConveyor)
                || string.Equals(passConveyor.Id, ngConveyor.Id, StringComparison.Ordinal))
            {
                AddInspectionSortRouterError(errors, targetId, "Inspection sorter routes must identify two distinct conveyors in the active layout.");
                continue;
            }

            if (channelKinds is null
                || !HasChannelKind(definition.PassRoutedFeedbackChannelId, ChannelKind.DigitalInput, channelKinds)
                || !HasChannelKind(definition.NgRoutedFeedbackChannelId, ChannelKind.DigitalInput, channelKinds)
                || string.Equals(
                    definition.PassRoutedFeedbackChannelId,
                    definition.NgRoutedFeedbackChannelId,
                    StringComparison.Ordinal))
            {
                AddInspectionSortRouterError(errors, targetId, "Inspection sorter route feedback channels must be two distinct DigitalInput channels.");
                continue;
            }

            try
            {
                sorters.Add(new InspectionSortRouterRuntimeConfiguration(
                    device.Id,
                    device.Name,
                    definition.CameraId,
                    passConveyor.Id,
                    ngConveyor.Id,
                    passConveyor.RunCommandChannelId,
                    ngConveyor.RunCommandChannelId,
                    definition.PassRoutedFeedbackChannelId,
                    definition.NgRoutedFeedbackChannelId));
            }
            catch (ArgumentException exception)
            {
                AddInspectionSortRouterError(errors, targetId, exception.Message);
            }
        }

        return sorters;
    }

    private static void AddInspectionSortRouterError(
        ICollection<MachineProjectRuntimeCompilationError> errors,
        string targetId,
        string message) =>
        errors.Add(Error(
            MachineProjectRuntimeCompilationErrorCode.InspectionSortRouterConfigurationInvalid,
            targetId,
            message));

    private static IReadOnlyList<InspectionHandoffRuntimeConfiguration> BuildInspectionHandoffs(
        IEnumerable<DeviceDefinition> devices,
        IReadOnlyDictionary<string, ChannelKind>? channelKinds,
        ICollection<MachineProjectRuntimeCompilationError> errors)
    {
        DeviceDefinition[] deviceArray = devices.ToArray();
        var cameraIds = deviceArray
            .Where(device => device is { Kind: DeviceKind.Camera, Camera: not null })
            .Select(device => device.Id)
            .ToHashSet(StringComparer.Ordinal);
        var handoffs = new List<InspectionHandoffRuntimeConfiguration>();

        foreach (DeviceDefinition device in deviceArray
                     .Where(device => device.Kind == DeviceKind.Inspection)
                     .OrderBy(device => device.Id, StringComparer.Ordinal))
        {
            string targetId = string.IsNullOrWhiteSpace(device.Id) ? "devices.inspectionHandoff" : device.Id;
            InspectionHandoffDefinition? definition = device.InspectionHandoff;
            if (definition is null)
            {
                AddInspectionHandoffError(errors, targetId, "Inspection-handoff settings are required.");
                continue;
            }

            if (!cameraIds.Contains(definition.CameraId))
            {
                AddInspectionHandoffError(errors, targetId, "Inspection handoff camera must identify a configured virtual camera.");
                continue;
            }

            string[] inputIds =
            {
                definition.InspectionPositionSensorChannelId,
                definition.InspectionReadyFeedbackChannelId,
                definition.InspectionCompleteFeedbackChannelId
            };
            if (channelKinds is null
                || inputIds.Any(channelId => !HasChannelKind(channelId, ChannelKind.DigitalInput, channelKinds))
                || !HasChannelKind(definition.ResultAcceptedCommandChannelId, ChannelKind.DigitalOutput, channelKinds)
                || inputIds.Append(definition.ResultAcceptedCommandChannelId).Distinct(StringComparer.Ordinal).Count() != 4)
            {
                AddInspectionHandoffError(errors, targetId, "Inspection handoff requires three distinct DigitalInput channels and one distinct DigitalOutput result-accepted command.");
                continue;
            }

            try
            {
                handoffs.Add(new InspectionHandoffRuntimeConfiguration(
                    device.Id,
                    device.Name,
                    definition.CameraId,
                    definition.InspectionPositionSensorChannelId,
                    definition.ResultAcceptedCommandChannelId,
                    definition.InspectionReadyFeedbackChannelId,
                    definition.InspectionCompleteFeedbackChannelId));
            }
            catch (ArgumentException exception)
            {
                AddInspectionHandoffError(errors, targetId, exception.Message);
            }
        }

        return handoffs;
    }

    private static void AddInspectionHandoffError(
        ICollection<MachineProjectRuntimeCompilationError> errors,
        string targetId,
        string message) =>
        errors.Add(Error(
            MachineProjectRuntimeCompilationErrorCode.InspectionHandoffConfigurationInvalid,
            targetId,
            message));

    private static IReadOnlyList<PrealignerRuntimeConfiguration> BuildPrealigners(
        IEnumerable<DeviceDefinition> devices,
        IReadOnlyCollection<LayoutComponentRuntimeConfiguration> runtimeComponents,
        IReadOnlyDictionary<string, VirtualAxisDefinition> axesById,
        IReadOnlyDictionary<string, ChannelKind>? channelKinds,
        ICollection<MachineProjectRuntimeCompilationError> errors)
    {
        var componentsById = runtimeComponents.ToDictionary(component => component.Id, StringComparer.Ordinal);
        var prealigners = new List<PrealignerRuntimeConfiguration>();

        foreach (DeviceDefinition device in devices
                     .Where(device => device.Kind == DeviceKind.Prealigner)
                     .OrderBy(device => device.Id, StringComparer.Ordinal))
        {
            string targetId = string.IsNullOrWhiteSpace(device.Id) ? "devices.prealigner" : device.Id;
            PrealignerDefinition? definition = device.Prealigner;
            if (definition is null)
            {
                AddPrealignerError(errors, targetId, "Pre-aligner settings are required.");
                continue;
            }

            if (!componentsById.TryGetValue(definition.RotaryStageComponentId, out var stageComponent)
                || stageComponent is not RotaryStageRuntimeConfiguration rotaryStage
                || !axesById.TryGetValue(rotaryStage.AxisId, out VirtualAxisDefinition? rotaryAxis)
                || rotaryAxis.Kind != AxisKind.Rotary)
            {
                AddPrealignerError(errors, targetId, "Pre-aligner stage must identify an active rotary stage bound to a Rotary axis.");
                continue;
            }

            if (!componentsById.TryGetValue(definition.ClampCylinderComponentId, out var clamp)
                || clamp is not PneumaticCylinderRuntimeConfiguration)
            {
                AddPrealignerError(errors, targetId, "Pre-aligner clamp must identify an active pneumatic cylinder.");
                continue;
            }

            if (!double.IsFinite(definition.AlignmentTargetDegrees)
                || definition.AlignmentTargetDegrees < rotaryAxis.SoftLimitMin
                || definition.AlignmentTargetDegrees > rotaryAxis.SoftLimitMax
                || !double.IsFinite(definition.AlignmentToleranceDegrees)
                || definition.AlignmentToleranceDegrees <= 0)
            {
                AddPrealignerError(errors, targetId, "Pre-aligner target must be finite and within rotary-axis limits, with a positive finite tolerance.");
                continue;
            }

            string[] inputIds =
            {
                definition.WaferPresentSensorChannelId,
                definition.AlignmentReadyFeedbackChannelId,
                definition.AlignmentCompleteFeedbackChannelId
            };
            if (channelKinds is null
                || inputIds.Any(channelId => !HasChannelKind(channelId, ChannelKind.DigitalInput, channelKinds))
                || !HasChannelKind(definition.AlignmentAcceptedCommandChannelId, ChannelKind.DigitalOutput, channelKinds)
                || inputIds.Append(definition.AlignmentAcceptedCommandChannelId).Distinct(StringComparer.Ordinal).Count() != 4)
            {
                AddPrealignerError(errors, targetId, "Pre-aligner requires three distinct DigitalInput channels and one distinct DigitalOutput accept command.");
                continue;
            }

            try
            {
                prealigners.Add(new PrealignerRuntimeConfiguration(
                    device.Id,
                    device.Name,
                    rotaryStage.Id,
                    rotaryStage.AxisId,
                    definition.ClampCylinderComponentId,
                    definition.WaferPresentSensorChannelId,
                    definition.AlignmentAcceptedCommandChannelId,
                    definition.AlignmentReadyFeedbackChannelId,
                    definition.AlignmentCompleteFeedbackChannelId,
                    definition.AlignmentTargetDegrees,
                    definition.AlignmentToleranceDegrees));
            }
            catch (ArgumentException exception)
            {
                AddPrealignerError(errors, targetId, exception.Message);
            }
        }

        return prealigners;
    }

    private static void AddPrealignerError(
        ICollection<MachineProjectRuntimeCompilationError> errors,
        string targetId,
        string message) =>
        errors.Add(Error(
            MachineProjectRuntimeCompilationErrorCode.PrealignerConfigurationInvalid,
            targetId,
            message));

    private static IReadOnlyList<OhtHandoffRuntimeConfiguration> BuildOhtHandoffs(
        IEnumerable<DeviceDefinition> devices,
        IReadOnlyCollection<LayoutComponentRuntimeConfiguration> runtimeComponents,
        IReadOnlyDictionary<string, ChannelKind>? channelKinds,
        ICollection<MachineProjectRuntimeCompilationError> errors)
    {
        var conveyorsById = runtimeComponents
            .OfType<ConveyorRuntimeConfiguration>()
            .ToDictionary(conveyor => conveyor.Id, StringComparer.Ordinal);
        var handoffs = new List<OhtHandoffRuntimeConfiguration>();

        foreach (DeviceDefinition device in devices
                     .Where(device => device.Kind == DeviceKind.Oht)
                     .OrderBy(device => device.Id, StringComparer.Ordinal))
        {
            string targetId = string.IsNullOrWhiteSpace(device.Id) ? "devices.ohtHandoff" : device.Id;
            OhtHandoffDefinition? definition = device.OhtHandoff;
            if (definition is null)
            {
                AddOhtHandoffError(errors, targetId, "OHT handoff settings are required.");
                continue;
            }

            if (!conveyorsById.TryGetValue(definition.TransportConveyorComponentId, out var conveyor))
            {
                AddOhtHandoffError(errors, targetId, "OHT handoff transport must identify a conveyor in the active layout.");
                continue;
            }

            string[] inputIds =
            {
                definition.RouteAvailableSensorChannelId,
                definition.VehicleDockedSensorChannelId,
                definition.LoadPortReadySensorChannelId,
                definition.CarrierReceivedSensorChannelId,
                definition.HandoffReadyFeedbackChannelId,
                definition.CarrierTransferredFeedbackChannelId
            };
            if (channelKinds is null
                || inputIds.Any(channelId => !HasChannelKind(channelId, ChannelKind.DigitalInput, channelKinds))
                || inputIds.Distinct(StringComparer.Ordinal).Count() != inputIds.Length)
            {
                AddOhtHandoffError(errors, targetId, "OHT handoff conditions and feedback must be six distinct DigitalInput channels.");
                continue;
            }

            try
            {
                handoffs.Add(new OhtHandoffRuntimeConfiguration(
                    device.Id,
                    device.Name,
                    conveyor.Id,
                    conveyor.RunCommandChannelId,
                    conveyor.ReverseCommandChannelId,
                    definition.RouteAvailableSensorChannelId,
                    definition.VehicleDockedSensorChannelId,
                    definition.LoadPortReadySensorChannelId,
                    definition.CarrierReceivedSensorChannelId,
                    definition.HandoffReadyFeedbackChannelId,
                    definition.CarrierTransferredFeedbackChannelId));
            }
            catch (ArgumentException exception)
            {
                AddOhtHandoffError(errors, targetId, exception.Message);
            }
        }

        return handoffs;
    }

    private static void AddOhtHandoffError(
        ICollection<MachineProjectRuntimeCompilationError> errors,
        string targetId,
        string message) =>
        errors.Add(Error(
            MachineProjectRuntimeCompilationErrorCode.OhtHandoffConfigurationInvalid,
            targetId,
            message));

    private static void AddWaferHandlerError(
        ICollection<MachineProjectRuntimeCompilationError> errors,
        string targetId,
        string message) =>
        errors.Add(Error(
            MachineProjectRuntimeCompilationErrorCode.WaferHandlerConfigurationInvalid,
            targetId,
            message));

    private static bool HasChannelKind(
        string channelId,
        ChannelKind expectedKind,
        IReadOnlyDictionary<string, ChannelKind> channelKinds) =>
        !string.IsNullOrWhiteSpace(channelId)
        && channelKinds.TryGetValue(channelId, out ChannelKind kind)
        && kind == expectedKind;

    private static void AddLoadLockError(
        ICollection<MachineProjectRuntimeCompilationError> errors,
        string targetId,
        string message) =>
        errors.Add(Error(
            MachineProjectRuntimeCompilationErrorCode.LoadLockConfigurationInvalid,
            targetId,
            message));

    private AutomaticRunConfiguration? BuildAutomaticRun(
        AutomaticRunDefinition? definition,
        IReadOnlyList<CompiledSequence> sequences,
        IReadOnlyDictionary<string, ChannelKind>? channelKinds,
        ICollection<MachineProjectRuntimeCompilationError> errors)
    {
        if (definition is null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(definition.SequenceId) ||
            !string.Equals(definition.SequenceId, definition.SequenceId.Trim(), StringComparison.Ordinal))
        {
            errors.Add(Error(
                MachineProjectRuntimeCompilationErrorCode.AutomaticSequenceIdRequired,
                "simulation.automaticRun.sequenceId",
                "Automatic run requires a non-blank sequence id without surrounding whitespace."));
        }
        else if (!sequences.Any(sequence =>
                     string.Equals(sequence.Id, definition.SequenceId, StringComparison.Ordinal)))
        {
            errors.Add(Error(
                MachineProjectRuntimeCompilationErrorCode.AutomaticSequenceNotFound,
                definition.SequenceId,
                $"Automatic sequence '{definition.SequenceId}' is not configured."));
        }

        if (definition.StartInputId is not null)
        {
            if (string.IsNullOrWhiteSpace(definition.StartInputId) ||
                !string.Equals(
                    definition.StartInputId,
                    definition.StartInputId.Trim(),
                    StringComparison.Ordinal))
            {
                errors.Add(Error(
                    MachineProjectRuntimeCompilationErrorCode.AutomaticStartInputInvalid,
                    "simulation.automaticRun.startInputId",
                    "Automatic start input id cannot be blank or contain surrounding whitespace."));
            }
            else if (channelKinds is null ||
                     !channelKinds.TryGetValue(definition.StartInputId, out ChannelKind kind))
            {
                errors.Add(Error(
                    MachineProjectRuntimeCompilationErrorCode.AutomaticStartInputNotFound,
                    definition.StartInputId,
                    $"Automatic start input '{definition.StartInputId}' is not configured."));
            }
            else if (kind != ChannelKind.DigitalInput)
            {
                errors.Add(Error(
                    MachineProjectRuntimeCompilationErrorCode.AutomaticStartInputKindInvalid,
                    definition.StartInputId,
                    $"Automatic start input '{definition.StartInputId}' must be a DigitalInput."));
            }
        }

        bool repeatDelayValid = definition.RepeatDelayMilliseconds >= 0 &&
            (definition.Repeat || definition.RepeatDelayMilliseconds == 0) &&
            TryConvertDelayToTicks(
                definition.RepeatDelayMilliseconds,
                allowZero: true,
                out _);
        if (!repeatDelayValid)
        {
            errors.Add(Error(
                MachineProjectRuntimeCompilationErrorCode.AutomaticRepeatDelayInvalid,
                "simulation.automaticRun.repeatDelayMilliseconds",
                "Automatic repeat delay must be non-negative, zero when repeat is disabled, and an exact fixed-step multiple."));
        }

        if (errors.Any(error => error.Code is
                MachineProjectRuntimeCompilationErrorCode.AutomaticSequenceIdRequired or
                MachineProjectRuntimeCompilationErrorCode.AutomaticSequenceNotFound or
                MachineProjectRuntimeCompilationErrorCode.AutomaticStartInputInvalid or
                MachineProjectRuntimeCompilationErrorCode.AutomaticStartInputNotFound or
                MachineProjectRuntimeCompilationErrorCode.AutomaticStartInputKindInvalid or
                MachineProjectRuntimeCompilationErrorCode.AutomaticRepeatDelayInvalid))
        {
            return null;
        }

        return new AutomaticRunConfiguration(
            definition.SequenceId,
            definition.StartInputId,
            definition.StartInputValue,
            definition.Repeat,
            definition.RepeatDelayMilliseconds);
    }

    private bool TryConvertDelayToTicks(
        int milliseconds,
        bool allowZero,
        out int tickCount)
    {
        tickCount = 0;
        if (milliseconds < 0 || (!allowZero && milliseconds == 0))
        {
            return false;
        }

        long delayTicks = TimeSpan.FromMilliseconds(milliseconds).Ticks;
        if (delayTicks % _fixedStep.Ticks != 0)
        {
            return false;
        }

        long candidate = delayTicks / _fixedStep.Ticks;
        if (candidate > int.MaxValue)
        {
            return false;
        }

        tickCount = (int)candidate;
        return allowZero || tickCount > 0;
    }

    private static bool HasErrorsInDependencies(
        IEnumerable<MachineProjectRuntimeCompilationError> errors) =>
        errors.Any(error => error.Code is
            MachineProjectRuntimeCompilationErrorCode.AxisIdRequired or
            MachineProjectRuntimeCompilationErrorCode.DuplicateAxisId or
            MachineProjectRuntimeCompilationErrorCode.AxisConfigurationInvalid or
            MachineProjectRuntimeCompilationErrorCode.SignalConfigurationInvalid or
            MachineProjectRuntimeCompilationErrorCode.CameraKindMismatch or
            MachineProjectRuntimeCompilationErrorCode.CameraIdRequired or
            MachineProjectRuntimeCompilationErrorCode.DuplicateCameraId or
            MachineProjectRuntimeCompilationErrorCode.CameraDecisionInvalid or
            MachineProjectRuntimeCompilationErrorCode.CameraLegacyValueInvalid or
            MachineProjectRuntimeCompilationErrorCode.CameraDelayInvalid);

    private static MachineProjectRuntimeCompilationResult Failure(
        params MachineProjectRuntimeCompilationError[] errors) =>
        new(null, errors);

    private static MachineProjectRuntimeCompilationResult Failure(
        IEnumerable<MachineProjectRuntimeCompilationError> errors) =>
        new(null, errors);

    private static MachineProjectRuntimeCompilationError Error(
        MachineProjectRuntimeCompilationErrorCode code,
        string? targetId,
        string message) =>
        new(code, targetId, message);
}
