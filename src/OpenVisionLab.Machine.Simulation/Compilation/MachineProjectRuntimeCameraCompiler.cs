using System.Globalization;
using OpenVisionLab.Machine.Core.Devices;
using OpenVisionLab.Machine.Simulation.Camera;

namespace OpenVisionLab.Machine.Simulation.Compilation;

internal sealed class MachineProjectRuntimeCameraCompiler
{
    private static readonly string[] LegacyDecisionKeys =
        ["placeholderDecision", "placeholderResult", "stubJudgment"];

    private readonly FixedStepDelayConverter _delayConverter;

    internal MachineProjectRuntimeCameraCompiler(FixedStepDelayConverter delayConverter)
    {
        ArgumentNullException.ThrowIfNull(delayConverter);
        _delayConverter = delayConverter;
    }

    internal IReadOnlyList<VirtualCameraConfiguration> Compile(
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

            if (!_delayConverter.TryConvertDelayToTicks(
                    authored.ExposureDelayMilliseconds,
                    allowZero: false,
                    out int exposureTicks) ||
                !_delayConverter.TryConvertDelayToTicks(
                    authored.TransferDelayMilliseconds,
                    allowZero: false,
                    out int transferTicks))
            {
                errors.Add(Error(
                    MachineProjectRuntimeCompilationErrorCode.CameraDelayInvalid,
                    device.Id,
                    $"Virtual camera '{device.Id}' exposure and transfer delays must be positive exact multiples of {_delayConverter.FixedStep.TotalMilliseconds.ToString("0.###", CultureInfo.InvariantCulture)} ms."));
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

    private static MachineProjectRuntimeCompilationError Error(
        MachineProjectRuntimeCompilationErrorCode code,
        string? targetId,
        string message) =>
        new(code, targetId, message);
}
