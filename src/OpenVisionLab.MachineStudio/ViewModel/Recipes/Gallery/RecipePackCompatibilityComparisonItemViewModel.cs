using System.Globalization;
using OpenVisionLab;
using OpenVisionLab.MachineStudio.Model;

namespace OpenVisionLab.MachineStudio.ViewModel;

public sealed class RecipePackCompatibilityComparisonItemViewModel : ViewModelBase
{
    private readonly RecipePackCompatibilityComparisonItem _comparison;

    internal RecipePackCompatibilityComparisonItemViewModel(
        RecipePackCompatibilityComparisonItem comparison)
    {
        _comparison = comparison;
    }

    public string FileName => _comparison.FileName;
    public string DisplayName => _comparison.DisplayName;
    public bool IsNewlyFailed => _comparison.ChangeKind == RecipePackCompatibilityChangeKind.NewlyFailed;
    public bool IsRecovered => _comparison.ChangeKind == RecipePackCompatibilityChangeKind.Recovered;
    public bool IsAdded => _comparison.ChangeKind == RecipePackCompatibilityChangeKind.Added;
    public bool IsRemoved => _comparison.ChangeKind == RecipePackCompatibilityChangeKind.Removed;
    public bool IsMetadataChanged => _comparison.ChangeKind == RecipePackCompatibilityChangeKind.MetadataChanged;
    public bool HasProjectSchemaChange => _comparison.ProjectSchemaChanged;
    public bool HasBuildChange => _comparison.BuildChanged;
    public string ChangeStatusText => OpenVisionLanguageService.T(
        $"Gallery.Comparison.{_comparison.ChangeKind}");
    public string OutcomeChangeText => string.Format(
        CultureInfo.CurrentCulture,
        OpenVisionLanguageService.T("Gallery.Comparison.OutcomeFormat"),
        LocalizedOutcome(_comparison.Baseline),
        LocalizedOutcome(_comparison.Current));
    public string ProjectSchemaChangeText => string.Format(
        CultureInfo.CurrentCulture,
        OpenVisionLanguageService.T("Gallery.Comparison.SchemaFormat"),
        _comparison.Baseline?.ProjectSchema ?? OpenVisionLanguageService.T("Gallery.Comparison.NotPresent"),
        _comparison.Current?.ProjectSchema ?? OpenVisionLanguageService.T("Gallery.Comparison.NotPresent"));
    public string BuildChangeText => string.Format(
        CultureInfo.CurrentCulture,
        OpenVisionLanguageService.T("Gallery.Comparison.BuildFormat"),
        CompactBuild(_comparison.Baseline),
        CompactBuild(_comparison.Current));
    public string BuildChangeDetailText => string.Format(
        CultureInfo.CurrentCulture,
        OpenVisionLanguageService.T("Gallery.Comparison.BuildFormat"),
        _comparison.Baseline?.BuildIdentity ?? OpenVisionLanguageService.T("Gallery.Comparison.NotPresent"),
        _comparison.Current?.BuildIdentity ?? OpenVisionLanguageService.T("Gallery.Comparison.NotPresent"));

    internal void RefreshLocalization()
    {
        OnPropertyChanged(nameof(ChangeStatusText));
        OnPropertyChanged(nameof(OutcomeChangeText));
        OnPropertyChanged(nameof(ProjectSchemaChangeText));
        OnPropertyChanged(nameof(BuildChangeText));
        OnPropertyChanged(nameof(BuildChangeDetailText));
    }

    private static string LocalizedOutcome(RecipePackCompatibilityResult? result) =>
        result is null
            ? OpenVisionLanguageService.T("Gallery.Comparison.NotPresent")
            : OpenVisionLanguageService.T(
                result.Outcome == "passed"
                    ? "Gallery.ValidationPassed"
                    : "Gallery.ValidationFailed");

    private static string CompactBuild(RecipePackCompatibilityResult? result)
    {
        if (result is null)
        {
            return OpenVisionLanguageService.T("Gallery.Comparison.NotPresent");
        }

        var commit = result.SourceCommit[..Math.Min(7, result.SourceCommit.Length)];
        return $"{commit} ({result.SourceState})";
    }
}
