using System.IO;
using System.Text.Json;

namespace OpenVisionLab.Machine.Infrastructure.Integration;

/// <summary>
/// Owns the shared path normalization and current file-system readiness checks
/// used by Machine Studio integration entry points. It does not own setup
/// persistence, handoff eligibility, transaction data, or watcher lifetime.
/// </summary>
public sealed class MachineIntegrationPathReadinessPolicy
{
    public MachineIntegrationPathReadinessSnapshot Evaluate(
        string? exchangeRoot,
        string? inspectionRecipePath)
    {
        var normalizedExchangeRoot = Normalize(exchangeRoot);
        var normalizedInspectionRecipePath = Normalize(inspectionRecipePath);
        return new(
            normalizedExchangeRoot,
            normalizedInspectionRecipePath,
            IsExchangeRootAvailable(normalizedExchangeRoot),
            IsInspectionRecipeAvailable(normalizedInspectionRecipePath),
            IsInspectionRecipeSupportedByTwoD(normalizedInspectionRecipePath));
    }

    public bool IsExchangeRootAvailable(string? exchangeRoot) =>
        Directory.Exists(Normalize(exchangeRoot));

    public bool IsInspectionRecipeAvailable(string? inspectionRecipePath) =>
        File.Exists(Normalize(inspectionRecipePath));

    public bool IsInspectionRecipeSupportedByTwoD(string? inspectionRecipePath)
    {
        var path = Normalize(inspectionRecipePath);
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (document.RootElement.TryGetProperty("recipeType", out var recipeType)
                && recipeType.ValueKind == JsonValueKind.String)
            {
                return !recipeType.GetString()!.StartsWith("c3d", StringComparison.OrdinalIgnoreCase);
            }
        }
        catch (JsonException)
        {
            // Non-JSON 2D recipe formats remain eligible; their own consumer owns validation.
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }

        return true;
    }

    public bool IsInspectionRecipeSupportedByThreeD(string? inspectionRecipePath)
    {
        var path = Normalize(inspectionRecipePath);
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.TryGetProperty("recipeType", out var recipeType)
                && recipeType.ValueKind == JsonValueKind.String
                && recipeType.GetString()!.StartsWith("c3d", StringComparison.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    public string Normalize(string? path) => path?.Trim() ?? string.Empty;

    public string NormalizeFullPath(string? path) =>
        string.IsNullOrWhiteSpace(path)
            ? string.Empty
            : Path.GetFullPath(Normalize(path));
}

public sealed record MachineIntegrationPathReadinessSnapshot(
    string ExchangeRoot,
    string InspectionRecipePath,
    bool IsExchangeRootAvailable,
    bool IsInspectionRecipeAvailable,
    bool IsInspectionRecipeSupportedByTwoD)
{
    public bool CanRefreshResults => IsExchangeRootAvailable;

    public bool CanPublishHandoff =>
        IsExchangeRootAvailable
        && IsInspectionRecipeAvailable
        && IsInspectionRecipeSupportedByTwoD;
}
