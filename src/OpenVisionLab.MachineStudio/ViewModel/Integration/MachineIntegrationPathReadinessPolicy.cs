using System.IO;

namespace OpenVisionLab.MachineStudio.ViewModel;

/// <summary>
/// Owns the shared path normalization and current file-system readiness checks
/// used by Machine Studio integration entry points. It does not own setup
/// persistence, handoff eligibility, transaction data, or watcher lifetime.
/// </summary>
internal sealed class MachineIntegrationPathReadinessPolicy
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
            IsInspectionRecipeAvailable(normalizedInspectionRecipePath));
    }

    public bool IsExchangeRootAvailable(string? exchangeRoot) =>
        Directory.Exists(Normalize(exchangeRoot));

    public bool IsInspectionRecipeAvailable(string? inspectionRecipePath) =>
        File.Exists(Normalize(inspectionRecipePath));

    public string Normalize(string? path) => path?.Trim() ?? string.Empty;

    public string NormalizeFullPath(string? path) =>
        string.IsNullOrWhiteSpace(path)
            ? string.Empty
            : Path.GetFullPath(Normalize(path));
}

internal sealed record MachineIntegrationPathReadinessSnapshot(
    string ExchangeRoot,
    string InspectionRecipePath,
    bool IsExchangeRootAvailable,
    bool IsInspectionRecipeAvailable)
{
    public bool CanRefreshResults => IsExchangeRootAvailable;

    public bool CanPublishHandoff =>
        IsExchangeRootAvailable && IsInspectionRecipeAvailable;
}
