using System.Text.Json;

namespace OpenVisionLab.Machine.Infrastructure.Integration;

/// <summary>
/// Owns JSON serialization and path-based persistence for coordinate
/// projection contracts.
/// </summary>
internal static class MachineCoordinateProjectionPersistence
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    internal static string SerializeProfile(MachineCoordinateProjectionProfile profile)
    {
        MachineCoordinateProjectionValidator.ValidateProfile(profile);
        return JsonSerializer.Serialize(profile, JsonOptions);
    }

    internal static MachineCoordinateProjectionProfile ReadProfile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var profile = JsonSerializer.Deserialize<MachineCoordinateProjectionProfile>(
                           File.ReadAllText(Path.GetFullPath(path)),
                           JsonOptions)
                       ?? throw new InvalidDataException("Coordinate projection profile is empty.");
        MachineCoordinateProjectionValidator.ValidateProfile(profile);
        return profile;
    }

    internal static string SerializeResult(MachineCoordinateProjectionResult result)
    {
        MachineCoordinateProjectionValidator.ValidateResult(result);
        return JsonSerializer.Serialize(result, JsonOptions);
    }

    internal static MachineCoordinateProjectionResult ReadResult(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var result = JsonSerializer.Deserialize<MachineCoordinateProjectionResult>(
                         File.ReadAllText(Path.GetFullPath(path)),
                         JsonOptions)
                     ?? throw new InvalidDataException("Coordinate projection result is empty.");
        MachineCoordinateProjectionValidator.ValidateResult(result);
        return result;
    }
}
