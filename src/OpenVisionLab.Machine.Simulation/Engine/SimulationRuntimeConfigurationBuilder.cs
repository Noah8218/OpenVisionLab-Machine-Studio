using OpenVisionLab.Machine.Core.Channels;
using OpenVisionLab.Machine.Sequence.Compilation;
using OpenVisionLab.Machine.Sequence.Runtime;
using OpenVisionLab.Machine.Simulation.Axis;
using OpenVisionLab.Machine.Simulation.Camera;
using OpenVisionLab.Machine.Simulation.Layout;
using OpenVisionLab.Machine.Simulation.Workpieces;
using OpenVisionLab.Machine.IO.Channels;

namespace OpenVisionLab.Machine.Simulation.Engine;

internal sealed record SimulationRuntimeConfigurationBuildResult(
    IReadOnlyList<ServoAxisComponent> Axes,
    IReadOnlyList<DeterministicVirtualCamera> Cameras,
    DeterministicSignalHub SignalHub,
    IReadOnlyDictionary<string, CompiledSequence> CompiledSequences,
    IReadOnlyDictionary<string, DeterministicSequenceExecutor> SequenceExecutors,
    DeterministicMachineLayout? MachineLayout,
    DeterministicPickPlaceWorkpiece? PickPlaceWorkpiece,
    int AutomaticRunRepeatDelayTicks);

internal sealed class SimulationRuntimeConfigurationBuilder
{
    private readonly TimeSpan _fixedStep;

    public SimulationRuntimeConfigurationBuilder(TimeSpan fixedStep)
    {
        if (fixedStep <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(fixedStep));
        }

        _fixedStep = fixedStep;
    }

    public bool TryBuild(
        SimulationRuntimeConfiguration configuration,
        out SimulationRuntimeConfigurationBuildResult? result,
        out string error)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        result = null;
        error = string.Empty;

        if (!TryCreateAxes(configuration.Axes, out var axes, out var axisError))
        {
            error = axisError;
            return false;
        }

        if (!TryCreateCameras(configuration.Cameras, out var cameras, out var cameraError))
        {
            error = cameraError;
            return false;
        }

        var hubResult = DeterministicSignalHub.Create(configuration.Channels);
        if (!hubResult.IsAccepted || hubResult.Hub is null)
        {
            error = $"Signal configuration failed: {hubResult.ErrorCode} ({hubResult.ChannelId ?? "n/a"}).";
            return false;
        }

        DeterministicSignalHub signalHub = hubResult.Hub;
        var compiled = new Dictionary<string, CompiledSequence>(StringComparer.Ordinal);
        foreach (var sequence in configuration.Sequences)
        {
            if (sequence is null || string.IsNullOrWhiteSpace(sequence.Id))
            {
                error = "Every compiled sequence requires an id.";
                return false;
            }

            if (!compiled.TryAdd(sequence.Id, sequence))
            {
                error = $"Sequence id '{sequence.Id}' is duplicated.";
                return false;
            }
        }

        var compositionErrors = SequenceCompiler.ValidateComposition(compiled.Values);
        if (compositionErrors.Count != 0)
        {
            var compositionError = compositionErrors[0];
            error = $"{compositionError.Code}: {compositionError.Message}";
            return false;
        }

        var executors = new Dictionary<string, DeterministicSequenceExecutor>(StringComparer.Ordinal);
        foreach (var sequence in compiled.Values)
        {
            executors.Add(sequence.Id, new DeterministicSequenceExecutor(sequence, compiled));
        }

        if (!TryValidateAutomaticRun(
                configuration.AutomaticRun,
                compiled,
                signalHub,
                out var repeatDelayTicks,
                out var automaticRunError))
        {
            error = automaticRunError;
            return false;
        }

        if (!TryCreateMachineLayout(
                configuration.Layout,
                axes,
                cameras,
                signalHub,
                out var machineLayout,
                out var layoutError))
        {
            error = layoutError;
            return false;
        }

        if (!TryCreatePickPlaceWorkpiece(
                configuration.PickPlaceWorkpiece,
                axes,
                signalHub,
                out var pickPlaceWorkpiece,
                out var workpieceError))
        {
            error = workpieceError;
            return false;
        }

        result = new SimulationRuntimeConfigurationBuildResult(
            axes,
            cameras,
            signalHub,
            compiled,
            executors,
            machineLayout,
            pickPlaceWorkpiece,
            repeatDelayTicks);
        return true;
    }

    public bool TryCreateAxes(
        IEnumerable<AxisConfiguration> configurations,
        out IReadOnlyList<ServoAxisComponent> axes,
        out string error)
    {
        var candidates = new List<ServoAxisComponent>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var configuration in configurations)
        {
            if (configuration is null || string.IsNullOrWhiteSpace(configuration.Id))
            {
                axes = Array.Empty<ServoAxisComponent>();
                error = "Every axis requires an id.";
                return false;
            }

            if (!ids.Add(configuration.Id))
            {
                axes = Array.Empty<ServoAxisComponent>();
                error = $"Axis id '{configuration.Id}' is duplicated.";
                return false;
            }

            if (!double.IsFinite(configuration.MinimumPosition)
                || !double.IsFinite(configuration.MaximumPosition)
                || !double.IsFinite(configuration.HomePosition)
                || configuration.MinimumPosition > configuration.MaximumPosition
                || configuration.HomePosition < configuration.MinimumPosition
                || configuration.HomePosition > configuration.MaximumPosition
                || !double.IsFinite(configuration.MaximumVelocity)
                || configuration.MaximumVelocity <= 0
                || !double.IsFinite(configuration.Acceleration)
                || configuration.Acceleration <= 0
                || !double.IsFinite(configuration.Deceleration)
                || configuration.Deceleration <= 0)
            {
                axes = Array.Empty<ServoAxisComponent>();
                error = $"Axis '{configuration.Id}' has invalid limits or motion parameters.";
                return false;
            }

            candidates.Add(new ServoAxisComponent(CloneAxis(configuration)));
        }

        axes = candidates;
        error = string.Empty;
        return true;
    }

    private static bool TryCreateMachineLayout(
        MachineLayoutRuntimeConfiguration? configuration,
        IReadOnlyList<ServoAxisComponent> axes,
        IReadOnlyList<DeterministicVirtualCamera> cameras,
        DeterministicSignalHub signalHub,
        out DeterministicMachineLayout? machineLayout,
        out string error)
    {
        machineLayout = null;
        error = string.Empty;
        if (configuration is null)
        {
            return true;
        }

        var axisIds = axes.Select(axis => axis.Id).ToHashSet(StringComparer.Ordinal);
        var missingStageAxis = configuration.Components
            .OfType<AxisBoundStageRuntimeConfiguration>()
            .FirstOrDefault(stage => !axisIds.Contains(stage.AxisId));
        if (missingStageAxis is not null)
        {
            error = $"Layout stage '{missingStageAxis.Id}' axis '{missingStageAxis.AxisId}' was not configured.";
            return false;
        }

        var missingHandlerAxis = configuration.WaferHandlers.FirstOrDefault(handler =>
            !axisIds.Contains(handler.HorizontalAxisId) || !axisIds.Contains(handler.VerticalAxisId));
        if (missingHandlerAxis is not null)
        {
            error = $"Wafer-handler '{missingHandlerAxis.Id}' references an axis that was not configured.";
            return false;
        }

        var missingPrealignerAxis = configuration.Prealigners.FirstOrDefault(prealigner =>
            !axisIds.Contains(prealigner.RotaryAxisId));
        if (missingPrealignerAxis is not null)
        {
            error = $"Pre-aligner '{missingPrealignerAxis.Id}' rotary axis '{missingPrealignerAxis.RotaryAxisId}' was not configured.";
            return false;
        }

        var cameraIds = cameras.Select(camera => camera.Id).ToHashSet(StringComparer.Ordinal);
        var missingSorterCamera = configuration.InspectionSortRouters.FirstOrDefault(sorter =>
            !cameraIds.Contains(sorter.CameraId));
        if (missingSorterCamera is not null)
        {
            error = $"Inspection sorter '{missingSorterCamera.Id}' camera '{missingSorterCamera.CameraId}' was not configured.";
            return false;
        }

        var missingHandoffCamera = configuration.InspectionHandoffs.FirstOrDefault(handoff =>
            !cameraIds.Contains(handoff.CameraId));
        if (missingHandoffCamera is not null)
        {
            error = $"Inspection handoff '{missingHandoffCamera.Id}' camera '{missingHandoffCamera.CameraId}' was not configured.";
            return false;
        }

        try
        {
            machineLayout = new DeterministicMachineLayout(configuration, signalHub);
            machineLayout.Reset();
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            error = $"Layout configuration failed: {exception.Message}";
            machineLayout = null;
            return false;
        }
    }

    private static bool TryCreatePickPlaceWorkpiece(
        PickPlaceWorkpieceRuntimeConfiguration? configuration,
        IReadOnlyList<ServoAxisComponent> axes,
        DeterministicSignalHub signalHub,
        out DeterministicPickPlaceWorkpiece? workpiece,
        out string error)
    {
        workpiece = null;
        error = string.Empty;
        if (configuration is null)
        {
            return true;
        }

        ServoAxisComponent? xAxis = axes.FirstOrDefault(axis =>
            string.Equals(axis.Id, configuration.XAxisId, StringComparison.Ordinal));
        ServoAxisComponent? yAxis = axes.FirstOrDefault(axis =>
            string.Equals(axis.Id, configuration.YAxisId, StringComparison.Ordinal));
        SignalReadResult gripper = signalHub.ReadDigitalSignal(configuration.GripperSignalId);
        if (string.IsNullOrWhiteSpace(configuration.Id) ||
            string.IsNullOrWhiteSpace(configuration.Name) ||
            xAxis is null ||
            yAxis is null ||
            ReferenceEquals(xAxis, yAxis) ||
            !gripper.IsAccepted ||
            gripper.Kind != ChannelKind.DigitalOutput ||
            !double.IsFinite(configuration.PickX) ||
            !double.IsFinite(configuration.PickY) ||
            configuration.PickX < xAxis.MinimumPosition ||
            configuration.PickX > xAxis.MaximumPosition ||
            configuration.PickY < yAxis.MinimumPosition ||
            configuration.PickY > yAxis.MaximumPosition)
        {
            error = "Pick-and-Place workpiece configuration is invalid.";
            return false;
        }

        workpiece = new DeterministicPickPlaceWorkpiece(configuration);
        return true;
    }

    private bool TryValidateAutomaticRun(
        AutomaticRunConfiguration? configuration,
        IReadOnlyDictionary<string, CompiledSequence> compiledSequences,
        DeterministicSignalHub signalHub,
        out int repeatDelayTicks,
        out string error)
    {
        repeatDelayTicks = 0;
        error = string.Empty;
        if (configuration is null)
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(configuration.SequenceId))
        {
            error = "Automatic run requires a sequence id.";
            return false;
        }

        if (!compiledSequences.ContainsKey(configuration.SequenceId))
        {
            error = $"Automatic sequence '{configuration.SequenceId}' is not configured.";
            return false;
        }

        if (configuration.StartInputId is not null)
        {
            if (string.IsNullOrWhiteSpace(configuration.StartInputId))
            {
                error = "Automatic start input id cannot be blank.";
                return false;
            }

            var input = signalHub.ReadDigitalSignal(configuration.StartInputId);
            if (!input.IsAccepted)
            {
                error = $"Automatic start input '{configuration.StartInputId}' is not configured.";
                return false;
            }

            if (input.Kind != ChannelKind.DigitalInput)
            {
                error = $"Automatic start input '{configuration.StartInputId}' must be a digital input.";
                return false;
            }
        }

        if (configuration.RepeatDelayMilliseconds < 0)
        {
            error = "Automatic repeat delay cannot be negative.";
            return false;
        }

        if (!configuration.Repeat && configuration.RepeatDelayMilliseconds != 0)
        {
            error = "Automatic repeat delay must be zero when repeat is disabled.";
            return false;
        }

        var repeatDelay = TimeSpan.FromMilliseconds(configuration.RepeatDelayMilliseconds);
        if (repeatDelay.Ticks % _fixedStep.Ticks != 0)
        {
            error = "Automatic repeat delay must be an exact multiple of the simulation fixed step.";
            return false;
        }

        var tickCount = repeatDelay.Ticks / _fixedStep.Ticks;
        if (tickCount > int.MaxValue)
        {
            error = "Automatic repeat delay exceeds the supported fixed-tick range.";
            return false;
        }

        repeatDelayTicks = (int)tickCount;
        return true;
    }

    private static bool TryCreateCameras(
        IEnumerable<VirtualCameraConfiguration> configurations,
        out IReadOnlyList<DeterministicVirtualCamera> cameras,
        out string error)
    {
        var candidates = new List<DeterministicVirtualCamera>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var configuration in configurations)
        {
            if (configuration is null || string.IsNullOrWhiteSpace(configuration.Id))
            {
                cameras = Array.Empty<DeterministicVirtualCamera>();
                error = "Every virtual camera requires an id.";
                return false;
            }

            if (!ids.Add(configuration.Id))
            {
                cameras = Array.Empty<DeterministicVirtualCamera>();
                error = $"Virtual camera id '{configuration.Id}' is duplicated.";
                return false;
            }

            candidates.Add(new DeterministicVirtualCamera(configuration));
        }

        cameras = candidates;
        error = string.Empty;
        return true;
    }

    private static AxisConfiguration CloneAxis(AxisConfiguration source) =>
        new()
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
