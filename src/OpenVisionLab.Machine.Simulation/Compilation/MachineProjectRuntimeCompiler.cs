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
    SubsequenceCompositionInvalid,
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
    UnexpectedFailure,
    TimeScaleInvalid
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
    private readonly TimeSpan _fixedStep;
    private readonly FixedStepDelayConverter _delayConverter;
    private readonly MachineProjectRuntimeAxisCompiler _axisCompiler;
    private readonly MachineProjectRuntimeCameraCompiler _cameraCompiler;
    private readonly MachineProjectRuntimeLayoutCompiler _layoutCompiler;
    private readonly MachineProjectRuntimeSequenceCompiler _sequenceCompiler = new();
    private readonly MachineProjectRuntimeAutomaticRunCompiler _automaticRunCompiler;
    private readonly MachineProjectRuntimePickPlaceCompiler _pickPlaceCompiler = new();

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
        _delayConverter = new FixedStepDelayConverter(fixedStep);
        _axisCompiler = new MachineProjectRuntimeAxisCompiler();
        _cameraCompiler = new MachineProjectRuntimeCameraCompiler(_delayConverter);
        _layoutCompiler = new MachineProjectRuntimeLayoutCompiler(_delayConverter);
        _automaticRunCompiler = new MachineProjectRuntimeAutomaticRunCompiler(_delayConverter);
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
        if (!double.IsFinite(simulation.DefaultTimeScale)
            || simulation.DefaultTimeScale < 0.1
            || simulation.DefaultTimeScale > 10.0)
        {
            errors.Add(Error(
                MachineProjectRuntimeCompilationErrorCode.TimeScaleInvalid,
                "simulation.defaultTimeScale",
                "Project default time scale must be finite and between 0.1 and 10.0 inclusive."));
            return Failure(errors);
        }

        IReadOnlyList<AxisConfiguration> axes = _axisCompiler.Compile(axisDefinitions!, errors);
        IReadOnlyList<VirtualCameraConfiguration> cameras = _cameraCompiler.Compile(deviceDefinitions!, errors);
        IReadOnlyDictionary<string, ChannelKind>? channelKinds = BuildSignalContract(
            channelDefinitions!,
            errors);

        IReadOnlyList<CompiledSequence> sequences = Array.Empty<CompiledSequence>();
        if (!HasErrorsInDependencies(errors))
        {
            sequences = _sequenceCompiler.Compile(
                sequenceDefinitions!,
                channelKinds!,
                axes,
                cameras,
                errors);
        }

        MachineLayoutRuntimeConfiguration? layout = _layoutCompiler.Compile(
            project,
            layoutDefinitions!,
            channelKinds,
            errors);
        AutomaticRunConfiguration? automaticRun = _automaticRunCompiler.Compile(
            simulation.AutomaticRun,
            sequences,
            channelKinds,
            errors);
        PickPlaceWorkpieceRuntimeConfiguration? pickPlaceWorkpiece = _pickPlaceCompiler.Compile(
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
                pickPlaceWorkpiece,
                simulation.DefaultTimeScale);
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
