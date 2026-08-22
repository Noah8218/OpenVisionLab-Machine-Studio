using OpenVisionLab;
using Xunit;

namespace OpenVisionLab.Machine.Core.Tests;

public sealed class OpenVisionLanguageServiceTests
{
    [Fact]
    public void CatalogStorageIsUserScopedInsteadOfInstallScoped()
    {
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OpenVisionLab",
            "MachineStudio",
            "CONFIG",
            "localization_catalog.tsv");

        Assert.Equal(expected, OpenVisionLanguageService.CatalogPath);
        Assert.False(
            OpenVisionLanguageService.CatalogPath.StartsWith(
                AppContext.BaseDirectory,
                StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CatalogDefaultsToKoreanAndSupportsEnglish()
    {
        OpenVisionLanguageService.Load();
        OpenVisionLanguageService.SetLanguage(OpenVisionLanguage.Korean, save: false);

        Assert.Equal("파일", OpenVisionLanguageService.T("Shell.File"));
        Assert.Equal("정상", OpenVisionLanguageService.T("Equipment.Normal"));
        Assert.Contains("원자적으로", OpenVisionLanguageService.T("Runtime.ConfigurationApplied"));

        OpenVisionLanguageService.SetLanguage(OpenVisionLanguage.English, save: false);
        Assert.Equal("File", OpenVisionLanguageService.T("Shell.File"));
        Assert.Equal("NORMAL", OpenVisionLanguageService.T("Equipment.Normal"));
        Assert.Equal("Configured", OpenVisionLanguageService.T("Runtime.ConfiguredPrefix"));
        Assert.Equal("and", OpenVisionLanguageService.T("Runtime.And"));
        Assert.Equal("Runtime configuration applied atomically.", OpenVisionLanguageService.T("Runtime.ConfigurationApplied"));
        Assert.Equal(
            "Automatic Transfer Cycle",
            OpenVisionLanguageService.TUserText(
                "sequence",
                "auto-transfer-cycle.name",
                "authored fallback"));
        Assert.Equal(
            "authored fallback",
            OpenVisionLanguageService.TUserText(
                "sequence",
                "missing-sequence.name",
                "authored fallback"));

        OpenVisionLanguageService.SetLanguage(OpenVisionLanguage.Korean, save: false);
    }
}
