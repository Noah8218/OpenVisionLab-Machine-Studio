using OpenVisionLab;
using Xunit;

namespace OpenVisionLab.Machine.Core.Tests;

public sealed class OpenVisionLanguageServiceTests
{
    private const string WorkspaceDefaults =
        "Key\tKorean\tEnglish\n" +
        "Equipment.OutlineNoMatches\t검색 결과가 없습니다.\tNo search results.\n" +
        "Equipment.OutlineSearch\t이름·ID·종류·유닛 검색\tSearch name · ID · kind · unit\n" +
        "Workspace.Teaching\t시뮬레이션\tSimulation\n" +
        "Workspace.Execution\t검사 연결\tInspection connection\n" +
        "Workspace.Results\t실행 기록\tRun records\n";

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
        Assert.Equal("새 레시피", OpenVisionLanguageService.T("Project.NewRecipeTitle"));
        Assert.Equal("빈 장비에서 스테이션과 부품을 추가합니다.", OpenVisionLanguageService.T("Project.NewRecipeEmptyHint"));
        Assert.Equal("레시피 이름을 입력하세요.", OpenVisionLanguageService.T("Project.NewRecipeNameRequired"));
        Assert.Equal("레시피 이름은 60자 이하여야 합니다.", OpenVisionLanguageService.T("Project.NewRecipeNameTooLong"));
        Assert.Equal("만들기", OpenVisionLanguageService.T("Project.Create"));

        OpenVisionLanguageService.SetLanguage(OpenVisionLanguage.English, save: false);
        Assert.Equal("File", OpenVisionLanguageService.T("Shell.File"));
        Assert.Equal("NORMAL", OpenVisionLanguageService.T("Equipment.Normal"));
        Assert.Equal("New recipe", OpenVisionLanguageService.T("Project.NewRecipeTitle"));
        Assert.Equal("Start from an empty machine and add stations and components.", OpenVisionLanguageService.T("Project.NewRecipeEmptyHint"));
        Assert.Equal("Enter a recipe name.", OpenVisionLanguageService.T("Project.NewRecipeNameRequired"));
        Assert.Equal("Recipe name must be 60 characters or fewer.", OpenVisionLanguageService.T("Project.NewRecipeNameTooLong"));
        Assert.Equal("Create", OpenVisionLanguageService.T("Project.Create"));
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

    [Fact]
    public void CatalogMigrationUpdatesOnlyUnmodifiedLegacyWorkspaceDefaults()
    {
        var path = CreateTestCatalogPath();
        File.WriteAllText(path,
            "Key\tKorean\tEnglish\n" +
            "Equipment.OutlineNoMatches\t일치하는 부품이 없습니다.\tNo matching parts\n" +
            "Equipment.OutlineSearch\t이름 · ID · 종류 · 유닛 검색\tSearch name · ID · kind · unit\n" +
            "Workspace.Teaching\t동작·검사 티칭\tMotion & inspection teaching\n" +
            "Workspace.Execution\t사용자 검사 이름\tRun & validate\n" +
            "Workspace.Results\t결과·비교\tResults & comparison\n" +
            "User.Custom\t사용자 값\tCustom value\n");

        OpenVisionLanguageService.EnsureCatalogFile(path, WorkspaceDefaults);

        var catalog = File.ReadAllText(path);
        Assert.Contains("Equipment.OutlineNoMatches\t검색 결과가 없습니다.\tNo search results.", catalog);
        Assert.Contains("Equipment.OutlineSearch\t이름·ID·종류·유닛 검색\tSearch name · ID · kind · unit", catalog);
        Assert.Contains("Workspace.Teaching\t시뮬레이션\tSimulation", catalog);
        Assert.Contains("Workspace.Execution\t사용자 검사 이름\tRun & validate", catalog);
        Assert.Contains("Workspace.Results\t실행 기록\tRun records", catalog);
        Assert.Contains("User.Custom\t사용자 값\tCustom value", catalog);
    }

    [Fact]
    public void CatalogMigrationPreservesCustomizedNoMatchCopy()
    {
        var path = CreateTestCatalogPath();
        File.WriteAllText(path,
            "Key\tKorean\tEnglish\n" +
            "Equipment.OutlineNoMatches\t사용자 검색 안내\tCustom search guidance\n" +
            "Equipment.OutlineSearch\t내 장비 검색 문구\tMy equipment search hint\n");

        OpenVisionLanguageService.EnsureCatalogFile(path, WorkspaceDefaults);

        var catalog = File.ReadAllText(path);
        Assert.Contains("Equipment.OutlineNoMatches\t사용자 검색 안내\tCustom search guidance", catalog);
        Assert.Contains("Equipment.OutlineSearch\t내 장비 검색 문구\tMy equipment search hint", catalog);
    }

    [Fact]
    public void NewCatalogUsesCurrentWorkspaceDefaults()
    {
        var path = CreateTestCatalogPath();

        OpenVisionLanguageService.EnsureCatalogFile(path, WorkspaceDefaults);

        var catalog = File.ReadAllText(path);
        Assert.Contains("Equipment.OutlineNoMatches\t검색 결과가 없습니다.\tNo search results.", catalog);
        Assert.Contains("Equipment.OutlineSearch\t이름·ID·종류·유닛 검색\tSearch name · ID · kind · unit", catalog);
        Assert.Contains("Workspace.Teaching\t시뮬레이션\tSimulation", catalog);
        Assert.Contains("Workspace.Execution\t검사 연결\tInspection connection", catalog);
        Assert.Contains("Workspace.Results\t실행 기록\tRun records", catalog);
    }

    private static string CreateTestCatalogPath()
    {
        var testRoot = Environment.GetEnvironmentVariable("OPENVISIONLAB_TEST_DATA_ROOT")
            ?? Path.Combine(Path.GetTempPath(), "OpenVisionLab-Localization.Tests");
        var directory = Path.Combine(
            testRoot,
            "localization-catalog",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, "localization_catalog.tsv");
    }
}
