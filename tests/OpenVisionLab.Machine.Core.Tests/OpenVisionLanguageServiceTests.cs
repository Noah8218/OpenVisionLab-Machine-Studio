using OpenVisionLab;
using Xunit;

namespace OpenVisionLab.Machine.Core.Tests;

public sealed class OpenVisionLanguageServiceTests
{
    private static readonly (string Key, string Korean, string English)[] ScenarioOverlayLegacyRows =
    [
        ("Simulation.ScheduledFaultRecovery", "예약 장애 및 복구", "Scheduled fault and recovery"),
        ("Simulation.EnableScheduledFault", "예약 장애 사용", "Enable scheduled fault"),
        ("Simulation.FaultKind", "장애 종류", "Fault type"),
        ("Simulation.FaultTarget", "장애 대상", "Fault target"),
        ("Simulation.RestartSequence", "장애 해제 후 시퀀스 재시작", "Restart sequence after clear"),
        ("Simulation.TestWorkspaceRunHint", "조건을 설정한 뒤 상단에서 운전 모드로 전환하여 시험을 실행하세요.", "Configure the conditions, then switch to Run mode above to execute the test."),
        ("Simulation.RequireNoActiveFaults", "마지막에 활성 장애 없음", "No active faults at finish"),
        ("Simulation.RunBatch", "반복 실행", "Run batch"),
    ];

    private const string WorkspaceDefaults =
        "Key\tKorean\tEnglish\n" +
        "Equipment.OutlineNoMatches\t검색 결과가 없습니다.\tNo search results.\n" +
        "Equipment.OutlineSearch\t이름·ID·종류·유닛 검색\tSearch name · ID · kind · unit\n" +
        "Workspace.Teaching\t시뮬레이션\tSimulation\n" +
        "Workspace.Execution\t검사 연결\tInspection connection\n" +
        "Workspace.Results\t실행 기록\tRun records\n" +
        "Integration.MmiShowInputWithSettings\t입력 넓게\tWiden input\n";

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
        Assert.Equal("빈 장비에서 스테이션과 장치를 추가합니다.", OpenVisionLanguageService.T("Project.NewRecipeEmptyHint"));
        Assert.Equal("이름을 1~60자로 입력하세요.", OpenVisionLanguageService.T("Project.NewRecipeNameRequired"));
        Assert.Equal("이름을 1~60자로 입력하세요.", OpenVisionLanguageService.T("Project.NewRecipeNameTooLong"));
        Assert.Equal("만들기", OpenVisionLanguageService.T("Project.Create"));
        Assert.Equal("아직 이미지가 없습니다", OpenVisionLanguageService.T("Camera.EmptyInput"));

        OpenVisionLanguageService.SetLanguage(OpenVisionLanguage.English, save: false);
        Assert.Equal("File", OpenVisionLanguageService.T("Shell.File"));
        Assert.Equal("NORMAL", OpenVisionLanguageService.T("Equipment.Normal"));
        Assert.Equal("New recipe", OpenVisionLanguageService.T("Project.NewRecipeTitle"));
        Assert.Equal("Start from an empty machine and add stations and components.", OpenVisionLanguageService.T("Project.NewRecipeEmptyHint"));
        Assert.Equal("Enter a recipe name.", OpenVisionLanguageService.T("Project.NewRecipeNameRequired"));
        Assert.Equal("Recipe name must be 60 characters or fewer.", OpenVisionLanguageService.T("Project.NewRecipeNameTooLong"));
        Assert.Equal("Create", OpenVisionLanguageService.T("Project.Create"));
        Assert.Equal("No image yet", OpenVisionLanguageService.T("Camera.EmptyInput"));
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
            "Integration.MmiShowInputWithSettings\t입력과 함께 보기\tShow input with settings\n" +
            "User.Custom\t사용자 값\tCustom value\n");

        OpenVisionLanguageService.EnsureCatalogFile(path, WorkspaceDefaults);

        var catalog = File.ReadAllText(path);
        Assert.Contains("Equipment.OutlineNoMatches\t검색 결과가 없습니다.\tNo search results.", catalog);
        Assert.Contains("Equipment.OutlineSearch\t이름·ID·종류·유닛 검색\tSearch name · ID · kind · unit", catalog);
        Assert.Contains("Workspace.Teaching\t시뮬레이션\tSimulation", catalog);
        Assert.Contains("Workspace.Execution\t사용자 검사 이름\tRun & validate", catalog);
        Assert.Contains("Workspace.Results\t실행 기록\tRun records", catalog);
        Assert.Contains("User.Custom\t사용자 값\tCustom value", catalog);
        Assert.Contains("Integration.MmiShowInputWithSettings\t입력 넓게\tWiden input", catalog);
    }

    [Fact]
    public void CatalogMigrationPreservesCustomizedNoMatchCopy()
    {
        var path = CreateTestCatalogPath();
        File.WriteAllText(path,
            "Key\tKorean\tEnglish\n" +
            "Equipment.OutlineNoMatches\t사용자 검색 안내\tCustom search guidance\n" +
            "Equipment.OutlineSearch\t내 장비 검색 문구\tMy equipment search hint\n" +
            "Integration.MmiShowInputWithSettings\t사용자 입력 제목\tCustom input heading\n");

        OpenVisionLanguageService.EnsureCatalogFile(path, WorkspaceDefaults);

        var catalog = File.ReadAllText(path);
        Assert.Contains("Equipment.OutlineNoMatches\t사용자 검색 안내\tCustom search guidance", catalog);
        Assert.Contains("Equipment.OutlineSearch\t내 장비 검색 문구\tMy equipment search hint", catalog);
        Assert.Contains("Integration.MmiShowInputWithSettings\t사용자 입력 제목\tCustom input heading", catalog);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NewRecipeNameCopyMigratesExactLegacyPairsAndPreservesCustomizedMessagesOnReopen(bool customized)
    {
        const string defaults = "Key\tKorean\tEnglish\n" +
            "Project.NewRecipeNameRequired\t이름을 1~60자로 입력하세요.\tEnter a recipe name.\n" +
            "Project.NewRecipeNameTooLong\t이름을 1~60자로 입력하세요.\tRecipe name must be 60 characters or fewer.\n";
        var required = customized ? "사용자 이름 안내" : "레시피 이름을 입력하세요.";
        var tooLongEnglish = customized ? "Custom length guidance" : "Recipe name must be 60 characters or fewer.";
        var path = CreateTestCatalogPath();
        File.WriteAllText(path, "Key\tKorean\tEnglish\n" +
            $"Project.NewRecipeNameRequired\t{required}\tEnter a recipe name.\n" +
            $"Project.NewRecipeNameTooLong\t레시피 이름은 60자 이하여야 합니다.\t{tooLongEnglish}\n");

        OpenVisionLanguageService.EnsureCatalogFile(path, defaults);
        var migrated = File.ReadAllText(path);
        Assert.Contains($"Project.NewRecipeNameRequired\t{(customized ? required : "이름을 1~60자로 입력하세요.")}\tEnter a recipe name.", migrated);
        Assert.Contains($"Project.NewRecipeNameTooLong\t{(customized ? "레시피 이름은 60자 이하여야 합니다." : "이름을 1~60자로 입력하세요.")}\t{tooLongEnglish}", migrated);
        OpenVisionLanguageService.EnsureCatalogFile(path, defaults);
        Assert.Equal(migrated, File.ReadAllText(path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StartupCopyMigratesOnlyExactBuiltInPairsAndPreservesCustomTextOnReopen(bool customized)
    {
        var entries = new[]
        {
            (Key: "RecipeEntry.Title", OldKorean: "레시피로 시작하기", OldEnglish: "Start with a recipe", Korean: "레시피를 열어 시작하세요", English: "Open a recipe to start"),
            (Key: "RecipeEntry.New", OldKorean: "새 레시피 만들기", OldEnglish: "Create a new recipe", Korean: "새 레시피", English: "New recipe"),
            (Key: "RecipeEntry.NewDescription", OldKorean: "빈 레시피에서 가상 장비 구성을 시작합니다.", OldEnglish: "Start a virtual machine layout from a blank recipe.", Korean: "빈 레시피에서 유닛과 장치를 추가합니다.", English: "Add units and devices to a blank recipe."),
            (Key: "RecipeEntry.Open", OldKorean: "레시피 열기", OldEnglish: "Open a recipe", Korean: "레시피 열기…", English: "Open recipe…")
        };
        string defaults = "Key\tKorean\tEnglish\n" + string.Join("\n", entries.Select(entry => $"{entry.Key}\t{entry.Korean}\t{entry.English}"));
        string oldCatalog = "Key\tKorean\tEnglish\n" + string.Join("\n", entries.Select(entry => $"{entry.Key}\t{(customized ? "사용자 " + entry.OldKorean : entry.OldKorean)}\t{entry.OldEnglish}"));
        var path = CreateTestCatalogPath();
        File.WriteAllText(path, oldCatalog);
        OpenVisionLanguageService.EnsureCatalogFile(path, defaults);
        string migrated = File.ReadAllText(path);
        foreach (var entry in entries)
            Assert.Contains($"{entry.Key}\t{(customized ? "사용자 " + entry.OldKorean : entry.Korean)}\t{(customized ? entry.OldEnglish : entry.English)}", migrated);
        OpenVisionLanguageService.EnsureCatalogFile(path, defaults);
        Assert.Equal(migrated, File.ReadAllText(path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EmptyInputTitleMigratesOnlyBuiltInPairAndPreservesCustomTextOnReopen(bool customized)
    {
        const string defaults = "Key\tKorean\tEnglish\nCamera.EmptyInput\t아직 이미지가 없습니다\tNo image yet\n";
        string korean = customized ? "사용자 입력 없음" : "조건을 적용한 뒤 입력을 생성하세요.";
        var path = CreateTestCatalogPath();
        File.WriteAllText(path, $"Key\tKorean\tEnglish\nCamera.EmptyInput\t{korean}\tApply the conditions, then generate an input.\n");
        OpenVisionLanguageService.EnsureCatalogFile(path, defaults);
        string migrated = File.ReadAllText(path);
        Assert.Contains(customized ? $"Camera.EmptyInput\t{korean}\tApply the conditions, then generate an input." : "Camera.EmptyInput\t아직 이미지가 없습니다\tNo image yet", migrated);
        OpenVisionLanguageService.EnsureCatalogFile(path, defaults);
        Assert.Equal(migrated, File.ReadAllText(path));
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

    [Fact]
    public void TerminologyMigrationPreservesCustomPairsAndSurvivesReopen()
    {
        const string defaults = "Key\tKorean\tEnglish\n" +
            "Workspace.Teaching\t시퀀스·시운전\tSimulation\n" +
            "Workspace.Results\t운전 이력\tRun records\n" +
            "Shell.Running\t운전 중\tRUNNING\n" +
            "Shell.ContinueRun\t운전 재개\tContinue run\n" +
            "Shell.Step\t1틱 진행\tStep\n";
        var path = CreateTestCatalogPath();
        File.WriteAllText(path, "Key\tKorean\tEnglish\n" +
            "Workspace.Teaching\t시뮬레이션\tSimulation\n" +
            "Workspace.Results\t실행 기록\tRun records\n" +
            "Shell.Running\t실행 중\tRUNNING\n" +
            "Shell.ContinueRun\t계속 운전\tMy resume label\n" +
            "Shell.Step\t내 틱 명령\tStep\n");

        OpenVisionLanguageService.EnsureCatalogFile(path, defaults);
        var migrated = File.ReadAllText(path);
        Assert.Contains("Workspace.Teaching\t시퀀스·시운전\tSimulation", migrated);
        Assert.Contains("Workspace.Results\t운전 이력\tRun records", migrated);
        Assert.Contains("Shell.Running\t운전 중\tRUNNING", migrated);
        Assert.Contains("Shell.ContinueRun\t계속 운전\tMy resume label", migrated);
        Assert.Contains("Shell.Step\t내 틱 명령\tStep", migrated);

        OpenVisionLanguageService.EnsureCatalogFile(path, defaults);
        Assert.Equal(migrated, File.ReadAllText(path));
    }

    [Fact]
    public void CoordinateLabelsUseConveyingWidthHeightWithoutChangingEnglish()
    {
        OpenVisionLanguageService.Load();
        var previousLanguage = OpenVisionLanguageService.CurrentLanguage;
        try
        {
            OpenVisionLanguageService.SetLanguage(OpenVisionLanguage.Korean, save: false);
            Assert.Equal("X (이송, LU)", OpenVisionLanguageService.T("Equipment.CoordinateX"));
            Assert.Equal("Y (폭, LU)", OpenVisionLanguageService.T("Equipment.CoordinateDepthZ"));
            Assert.Equal("Z (높이, LU)", OpenVisionLanguageService.T("Equipment.CoordinateHeightY"));
            Assert.Equal("길이 (X)", OpenVisionLanguageService.T("Properties.Width"));
            Assert.Equal("폭 (Y)", OpenVisionLanguageService.T("Properties.Depth"));
            Assert.Equal("높이 (Z, LU)", OpenVisionLanguageService.T("Properties.VerticalHeight"));
            Assert.DoesNotContain("X/Z", OpenVisionLanguageService.T("Inspector.VerticalEnvelopeHint"));

            OpenVisionLanguageService.SetLanguage(OpenVisionLanguage.English, save: false);
            Assert.Equal("X (LU)", OpenVisionLanguageService.T("Equipment.CoordinateX"));
            Assert.Equal("Depth Z (LU)", OpenVisionLanguageService.T("Equipment.CoordinateDepthZ"));
            Assert.Equal("Height Y (LU)", OpenVisionLanguageService.T("Equipment.CoordinateHeightY"));
            Assert.Equal("Width", OpenVisionLanguageService.T("Properties.Width"));
            Assert.Equal("Depth (Z)", OpenVisionLanguageService.T("Properties.Depth"));
            Assert.Equal("Vertical size (LU)", OpenVisionLanguageService.T("Properties.VerticalHeight"));
        }
        finally { OpenVisionLanguageService.SetLanguage(previousLanguage, save: false); }
    }

    [Fact]
    public void CoordinateCatalogMigrationKeepsCustomPairsAndIsStableOnReopen()
    {
        var assembly = typeof(OpenVisionLanguageService).Assembly;
        var resourceName = assembly.GetManifestResourceNames().Single(name => name.EndsWith("LocalizationCatalog.tsv", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resourceName)!;
        using var reader = new StreamReader(stream);
        var defaults = reader.ReadToEnd();
        var path = CreateTestCatalogPath();
        File.WriteAllText(path, "Key\tKorean\tEnglish\n" +
            "Equipment.CoordinateX\tX (LU)\tX (LU)\n" +
            "Equipment.CoordinateHeightY\t높이 Y (LU)\tHeight Y (LU)\n" +
            "Equipment.CoordinateDepthZ\t깊이 Z (LU)\tDepth Z (LU)\n" +
            "Properties.Width\t너비\tWidth\n" +
            "Properties.Depth\t깊이 (Z)\tDepth (Z)\n" +
            "Properties.VerticalHeight\t수직 크기 (LU)\tVertical size (LU)\n" +
            "Inspector.VerticalEnvelopeHint\t사선 장면의 개략 체적입니다. 기존 X/Y 바닥 크기는 X/Z에 대응하며 고도와 수직 크기는 LU입니다. 실측 mm 치수가 아닙니다.\tSchematic volume for the oblique view. Existing X/Y footprint maps to X/Z; elevation and vertical size use LU. These are not measured millimetres.\n");

        OpenVisionLanguageService.EnsureCatalogFile(path, defaults);
        var migrated = File.ReadAllText(path);
        Assert.Contains("Equipment.CoordinateX\tX (이송, LU)\tX (LU)", migrated);
        Assert.Contains("Equipment.CoordinateHeightY\tZ (높이, LU)\tHeight Y (LU)", migrated);
        Assert.Contains("Equipment.CoordinateDepthZ\tY (폭, LU)\tDepth Z (LU)", migrated);
        Assert.Contains("Properties.Width\t길이 (X)\tWidth", migrated);
        Assert.Contains("Properties.Depth\t폭 (Y)\tDepth (Z)", migrated);
        Assert.Contains("Properties.VerticalHeight\t높이 (Z, LU)\tVertical size (LU)", migrated);
        Assert.DoesNotContain("기존 X/Y 바닥 크기는 X/Z", migrated);
        OpenVisionLanguageService.EnsureCatalogFile(path, defaults);
        Assert.Equal(migrated, File.ReadAllText(path));

        var customPath = CreateTestCatalogPath();
        File.WriteAllText(customPath, "Key\tKorean\tEnglish\n" +
            "Equipment.CoordinateHeightY\t내 높이 표시\tHeight Y (LU)\n" +
            "Equipment.CoordinateDepthZ\t깊이 Z (LU)\tMy width label\n" +
            "User.Custom\t사용자 값\tCustom value\n");
        OpenVisionLanguageService.EnsureCatalogFile(customPath, defaults);
        var customized = File.ReadAllText(customPath);
        Assert.Contains("Equipment.CoordinateHeightY\t내 높이 표시\tHeight Y (LU)", customized);
        Assert.Contains("Equipment.CoordinateDepthZ\t깊이 Z (LU)\tMy width label", customized);
        Assert.Contains("User.Custom\t사용자 값\tCustom value", customized);
        OpenVisionLanguageService.EnsureCatalogFile(customPath, defaults);
        Assert.Equal(customized, File.ReadAllText(customPath));
    }

    [Fact]
    public void EquipmentDeviceTermsPreserveEnglishAndUseConfirmedKoreanNames()
    {
        OpenVisionLanguageService.Load();
        var previousLanguage = OpenVisionLanguageService.CurrentLanguage;
        try
        {
            OpenVisionLanguageService.SetLanguage(OpenVisionLanguage.Korean, save: false);
            Assert.Equal("장치 추가", OpenVisionLanguageService.T("Equipment.LibraryDialogTitle"));
            Assert.Equal("+ 장치", OpenVisionLanguageService.T("Equipment.AddPartButton"));
            Assert.Equal("장치 종류 검색", OpenVisionLanguageService.T("Equipment.LibrarySearchLabel"));
            Assert.Equal("제품", OpenVisionLanguageService.T("Layout.Library.Workpiece.Name"));
            Assert.Equal("제품", OpenVisionLanguageService.T("Properties.Value.Workpiece"));
            Assert.Equal("리니어 축", OpenVisionLanguageService.T("Layout.Library.LinearStage.Name"));
            Assert.Equal("수동 조작", OpenVisionLanguageService.T("Equipment.SinglePartCheck"));
            Assert.Equal("제품을 추가하기 전에 컨베이어를 추가하세요", OpenVisionLanguageService.T("Layout.Add.WorkpieceCarrierRequired"));
            Assert.Equal("선택 장치 {0}개를 삭제할까요?", OpenVisionLanguageService.T("Equipment.RemovalTitle"));
            Assert.Equal("영향받는 시퀀스 연결", OpenVisionLanguageService.T("Equipment.RemovalImpacts"));

            OpenVisionLanguageService.SetLanguage(OpenVisionLanguage.English, save: false);
            Assert.Equal("Add component", OpenVisionLanguageService.T("Equipment.LibraryDialogTitle"));
            Assert.Equal("Workpiece", OpenVisionLanguageService.T("Layout.Library.Workpiece.Name"));
            Assert.Equal("Linear Stage", OpenVisionLanguageService.T("Layout.Library.LinearStage.Name"));
            Assert.Equal("Single-part check", OpenVisionLanguageService.T("Equipment.SinglePartCheck"));
            Assert.Equal("Removed {0}", OpenVisionLanguageService.T("Layout.RemovedStatus"));
            Assert.Equal("Affected sequence connections", OpenVisionLanguageService.T("Equipment.RemovalImpacts"));
        }
        finally { OpenVisionLanguageService.SetLanguage(previousLanguage, save: false); }
    }

    [Fact]
    public void EquipmentDeviceCatalogMigrationPreservesCustomPairsAndFallbackEntriesOnReopen()
    {
        var assembly = typeof(OpenVisionLanguageService).Assembly;
        var resourceName = assembly.GetManifestResourceNames().Single(name => name.EndsWith("LocalizationCatalog.tsv", StringComparison.Ordinal));
        using var reader = new StreamReader(assembly.GetManifestResourceStream(resourceName)!);
        var defaults = reader.ReadToEnd();
        var path = CreateTestCatalogPath();
        File.WriteAllText(path, "Key\tKorean\tEnglish\n" +
            "Equipment.LibraryDialogTitle\t부품 추가\tAdd component\n" +
            "Layout.Library.Workpiece.Name\t워크피스\tWorkpiece\n" +
            "Equipment.RemovalWorkpiece\t작업물\tWorkpiece\n" +
            "Equipment.RemovalImpacts\t영향받는 동작 연결\tAffected sequence connections\n" +
            "Equipment.LibrarySearchLabel\t내 장치 검색\tSearch component types\n" +
            "Layout.Library.LinearStage.Name\t리니어 스테이지\tMy linear axis\n" +
            "User.Custom\t사용자 값\tCustom value\n");

        OpenVisionLanguageService.EnsureCatalogFile(path, defaults);
        var migrated = File.ReadAllText(path);
        Assert.Contains("Equipment.LibraryDialogTitle\t장치 추가\tAdd component", migrated);
        Assert.Contains("Layout.Library.Workpiece.Name\t제품\tWorkpiece", migrated);
        Assert.Contains("Equipment.RemovalWorkpiece\t제품\tWorkpiece", migrated);
        Assert.Contains("Equipment.RemovalImpacts\t영향받는 시퀀스 연결\tAffected sequence connections", migrated);
        Assert.Contains("Equipment.LibrarySearchLabel\t내 장치 검색\tSearch component types", migrated);
        Assert.Contains("Layout.Library.LinearStage.Name\t리니어 스테이지\tMy linear axis", migrated);
        Assert.Contains("User.Custom\t사용자 값\tCustom value", migrated);
        OpenVisionLanguageService.EnsureCatalogFile(path, defaults);
        Assert.Equal(migrated, File.ReadAllText(path));
    }

    [Fact]
    public void ManualAxisTermsUseConfirmedMotionNamesAndPreserveEnglish()
    {
        OpenVisionLanguageService.Load();
        var previousLanguage = OpenVisionLanguageService.CurrentLanguage;
        try
        {
            OpenVisionLanguageService.SetLanguage(OpenVisionLanguage.Korean, save: false);
            Assert.Equal("JOG −", OpenVisionLanguageService.T("Axis.JogNegative"));
            Assert.Equal("JOG +", OpenVisionLanguageService.T("Axis.JogPositive"));
            Assert.Equal("원점 위치 이동", OpenVisionLanguageService.T("Axis.Home"));
            Assert.Equal("목표 위치로 이동", OpenVisionLanguageService.T("Axis.Move"));
            Assert.Equal("소프트 리밋 (−)", OpenVisionLanguageService.T("Axis.SoftLimitMin"));
            Assert.Equal("소프트 리밋 (+)", OpenVisionLanguageService.T("Axis.SoftLimitMax"));
            Assert.Equal("알람", OpenVisionLanguageService.T("Equipment.State.Error"));
            Assert.Equal("전진 완료 센서", OpenVisionLanguageService.T("Properties.ExtendedSensor"));
            Assert.Equal("후진 완료 센서", OpenVisionLanguageService.T("Properties.RetractedSensor"));
            Assert.Equal("후진 완료", OpenVisionLanguageService.T("Equipment.State.Retracted"));
            Assert.Equal("후진", OpenVisionLanguageService.T("Cylinder.Retract"));
            Assert.Equal("수동 조작", OpenVisionLanguageService.T("Equipment.ManualCommissioning"));
            Assert.Contains("선택 장치", OpenVisionLanguageService.T("Equipment.ManualHint.Ready"));
            Assert.Contains("운전을 종료", OpenVisionLanguageService.T("Equipment.ManualHint.Paused"));
            OpenVisionLanguageService.SetLanguage(OpenVisionLanguage.English, save: false);
            Assert.Equal("Home", OpenVisionLanguageService.T("Axis.Home"));
            Assert.Equal("Move", OpenVisionLanguageService.T("Axis.Move"));
            Assert.Equal("Minimum soft limit", OpenVisionLanguageService.T("Axis.SoftLimitMin"));
            Assert.Equal("Retract", OpenVisionLanguageService.T("Cylinder.Retract"));
            Assert.Equal("RETRACTED", OpenVisionLanguageService.T("Equipment.State.Retracted"));
            Assert.Equal("ERROR", OpenVisionLanguageService.T("Equipment.State.Error"));
        }
        finally { OpenVisionLanguageService.SetLanguage(previousLanguage, save: false); }
    }

    [Fact]
    public void ManualAxisDefaultMigrationIsIdempotentAndPreservesCustomLanguagePairs()
    {
        var assembly = typeof(OpenVisionLanguageService).Assembly;
        var resourceName = assembly.GetManifestResourceNames().Single(name => name.EndsWith("LocalizationCatalog.tsv", StringComparison.Ordinal));
        using var reader = new StreamReader(assembly.GetManifestResourceStream(resourceName)!);
        var defaults = reader.ReadToEnd();
        var path = CreateTestCatalogPath();
        File.WriteAllText(path, "Key\tKorean\tEnglish\n" +
            "Axis.Home\t원점\tHome\n" +
            "Axis.Move\t이동\tMove\n" +
            "Axis.SoftLimitMin\t최소 소프트 한계\tMinimum soft limit\n" +
            "Axis.JogPositive\t조그 +\tJog +\n" +
            "Cylinder.Retract\t후퇴\tRetract\n" +
            "Properties.RetractedSensor\t후퇴 센서\tRetracted sensor\n" +
            "Equipment.State.Error\t오류\tERROR\n" +
            "Equipment.ManualCommissioning\t수동 커미셔닝\tMANUAL COMMISSIONING\n" +
            "Cylinder.Commissioning\t실린더 커미셔닝\tCYLINDER COMMISSIONING\n" +
            "Sensor.Commissioning\t센서 커미셔닝\tSENSOR COMMISSIONING\n" +
            "Conveyor.Commissioning\t컨베이어 커미셔닝\tCONVEYOR COMMISSIONING\n" +
            "Axis.JogNegative\t내 음방향 조작\tJog −\n" +
            "Axis.SoftLimitMax\t최대 소프트 한계\tMy upper limit\n" +
            "User.Custom\t내 값\tMy value\n");
        OpenVisionLanguageService.EnsureCatalogFile(path, defaults);
        var migrated = File.ReadAllText(path);
        Assert.Contains("Axis.Home\t원점 위치 이동\tHome", migrated);
        Assert.Contains("Axis.Move\t목표 위치로 이동\tMove", migrated);
        Assert.Contains("Axis.SoftLimitMin\t소프트 리밋 (−)\tMinimum soft limit", migrated);
        Assert.Contains("Axis.JogPositive\tJOG +\tJog +", migrated);
        Assert.Contains("Cylinder.Retract\t후진\tRetract", migrated);
        Assert.Contains("Properties.RetractedSensor\t후진 완료 센서\tRetracted sensor", migrated);
        Assert.Contains("Equipment.State.Error\t알람\tERROR", migrated);
        Assert.Contains("Equipment.ManualCommissioning\t수동 조작\tManual commissioning", migrated);
        Assert.Contains("Cylinder.Commissioning\t실린더 수동 조작\tCylinder commissioning", migrated);
        Assert.Contains("Sensor.Commissioning\t센서 수동 조작\tSensor commissioning", migrated);
        Assert.Contains("Conveyor.Commissioning\t컨베이어 수동 조작\tConveyor commissioning", migrated);
        Assert.Contains("Axis.JogNegative\t내 음방향 조작\tJog −", migrated);
        Assert.Contains("Axis.SoftLimitMax\t최대 소프트 한계\tMy upper limit", migrated);
        Assert.Contains("User.Custom\t내 값\tMy value", migrated);
        OpenVisionLanguageService.EnsureCatalogFile(path, defaults);
        Assert.Equal(migrated, File.ReadAllText(path));
    }

    [Fact]
    public void ProductInspectorTermsPreserveEnglishAndUseConfirmedVerdicts()
    {
        OpenVisionLanguageService.Load();
        var previousLanguage = OpenVisionLanguageService.CurrentLanguage;
        try
        {
            OpenVisionLanguageService.SetLanguage(OpenVisionLanguage.Korean, save: false);
            Assert.Equal("제품", OpenVisionLanguageService.T("Properties.Workpiece"));
            Assert.Equal("제품 모델", OpenVisionLanguageService.T("Properties.WorkpieceType"));
            Assert.Equal("검사 대기", OpenVisionLanguageService.T("Properties.Value.Pending"));
            Assert.Equal("OK", OpenVisionLanguageService.T("Properties.Value.Passed"));
            Assert.Equal("NG", OpenVisionLanguageService.T("Properties.Value.Failed"));
            Assert.Equal("건너뜀", OpenVisionLanguageService.T("Properties.Value.Skipped"));
            Assert.Contains("디지털 입력", OpenVisionLanguageService.T("LayoutValidation.SensorOutputChannelMustBeDigitalInput"));
            Assert.Contains("디지털 출력", OpenVisionLanguageService.T("LayoutValidation.ConveyorCommandMustBeDigitalOutput"));
            Assert.Contains("같은 레이아웃", OpenVisionLanguageService.T("LayoutValidation.WorkpieceCarrierMustBeConveyor"));
            OpenVisionLanguageService.SetLanguage(OpenVisionLanguage.English, save: false);
            Assert.Equal("Workpiece", OpenVisionLanguageService.T("Properties.Workpiece"));
            Assert.Equal("Type", OpenVisionLanguageService.T("Properties.WorkpieceType"));
            Assert.Equal("Pending", OpenVisionLanguageService.T("Properties.Value.Pending"));
            Assert.Equal("Passed", OpenVisionLanguageService.T("Properties.Value.Passed"));
            Assert.Equal("Failed", OpenVisionLanguageService.T("Properties.Value.Failed"));
            Assert.Equal("Skipped", OpenVisionLanguageService.T("Properties.Value.Skipped"));
        }
        finally { OpenVisionLanguageService.SetLanguage(previousLanguage, save: false); }
    }

    [Fact]
    public void ProductInspectorCatalogMigrationPreservesCustomTranslationsOnReopen()
    {
        var assembly = typeof(OpenVisionLanguageService).Assembly;
        var resourceName = assembly.GetManifestResourceNames().Single(name => name.EndsWith("LocalizationCatalog.tsv", StringComparison.Ordinal));
        using var reader = new StreamReader(assembly.GetManifestResourceStream(resourceName)!);
        var defaults = reader.ReadToEnd();
        var path = CreateTestCatalogPath();
        File.WriteAllText(path, "Key\tKorean\tEnglish\n" +
            "Properties.Workpiece\t워크피스\tWorkpiece\n" +
            "Properties.Value.Passed\t통과\tPassed\n" +
            "Properties.Value.Failed\t실패\tFailed\n");
        OpenVisionLanguageService.EnsureCatalogFile(path, defaults);
        var migrated = File.ReadAllText(path);
        Assert.Contains("Properties.Workpiece\t제품\tWorkpiece", migrated);
        Assert.Contains("Properties.Value.Passed\tOK\tPassed", migrated);
        Assert.Contains("Properties.Value.Failed\tNG\tFailed", migrated);
        Assert.Contains("Properties.Value.Pending\t검사 대기\tPending", migrated);
        Assert.Contains("LayoutValidation.WorkpieceTypeRequired\t제품 모델을 입력하세요.", migrated);
        OpenVisionLanguageService.EnsureCatalogFile(path, defaults);
        Assert.Equal(migrated, File.ReadAllText(path));
        var customPath = CreateTestCatalogPath();
        File.WriteAllText(customPath, "Key\tKorean\tEnglish\n" +
            "Properties.Workpiece\t내 제품 표시\tWorkpiece\n" +
            "Properties.Value.Passed\t통과\tMy accepted verdict\n" +
            "Properties.Value.Failed\t내 NG 표시\tFailed\n" +
            "Properties.Value.Pending\t내 대기 표시\tPending\n" +
            "LayoutValidation.SensorOutputChannelMustBeDigitalInput\t내 센서 안내\tMy sensor hint\n" +
            "User.Custom\t사용자 값\tCustom value\n");
        OpenVisionLanguageService.EnsureCatalogFile(customPath, defaults);
        var custom = File.ReadAllText(customPath);
        Assert.Contains("Properties.Workpiece\t내 제품 표시\tWorkpiece", custom);
        Assert.Contains("Properties.Value.Passed\t통과\tMy accepted verdict", custom);
        Assert.Contains("Properties.Value.Failed\t내 NG 표시\tFailed", custom);
        Assert.Contains("Properties.Value.Pending\t내 대기 표시\tPending", custom);
        Assert.Contains("LayoutValidation.SensorOutputChannelMustBeDigitalInput\t내 센서 안내\tMy sensor hint", custom);
        Assert.Contains("User.Custom\t사용자 값\tCustom value", custom);
        OpenVisionLanguageService.EnsureCatalogFile(customPath, defaults);
        Assert.Equal(custom, File.ReadAllText(customPath));
    }

    [Fact]
    public void SensorFaultCatalogUsesConfirmedKoreanTermsAndPreservesEnglish()
    {
        var assembly = typeof(OpenVisionLanguageService).Assembly;
        var resourceName = assembly.GetManifestResourceNames().Single(name => name.EndsWith("LocalizationCatalog.tsv", StringComparison.Ordinal));
        using var reader = new StreamReader(assembly.GetManifestResourceStream(resourceName)!);
        var catalog = reader.ReadToEnd().Split('\n').Select(line => line.TrimEnd('\r').Split('\t'))
            .Where(row => row.Length == 3).ToDictionary(row => row[0], StringComparer.Ordinal);
        Assert.Equal("센서 상시 ON/OFF", catalog["Sensor.FaultOverride"][1]);
        Assert.Equal("Fault override", catalog["Sensor.FaultOverride"][2]);
        Assert.Equal("고장 시뮬레이션에서 센서 상시 ON/OFF를 해제하거나 초기화한 뒤 수동 조작하세요.", catalog["Sensor.ClearFaultHint"][1]);
        Assert.Equal("Clear the stuck-input fault or Reset before using manual force.", catalog["Sensor.ClearFaultHint"][2]);
    }

    [Fact]
    public void SensorFaultDefaultMigrationPreservesCustomPairsAndIsStableOnReopen()
    {
        var assembly = typeof(OpenVisionLanguageService).Assembly;
        var resourceName = assembly.GetManifestResourceNames().Single(name => name.EndsWith("LocalizationCatalog.tsv", StringComparison.Ordinal));
        using var reader = new StreamReader(assembly.GetManifestResourceStream(resourceName)!);
        var defaults = reader.ReadToEnd();
        var path = CreateTestCatalogPath();
        File.WriteAllText(path, "Key\tKorean\tEnglish\n" +
            "Sensor.FaultOverride\t고장 Override\tFault override\n" +
            "Sensor.ClearFaultHint\tStuck-input 고장을 해제하거나 초기화한 뒤 수동 Force를 사용하세요.\tClear the stuck-input fault or Reset before using manual force.\n");
        OpenVisionLanguageService.EnsureCatalogFile(path, defaults);
        var migrated = File.ReadAllText(path);
        Assert.Contains("Sensor.FaultOverride\t센서 상시 ON/OFF\tFault override", migrated);
        Assert.Contains("Sensor.ClearFaultHint\t고장 시뮬레이션에서 센서 상시 ON/OFF를 해제하거나 초기화한 뒤 수동 조작하세요.\tClear the stuck-input fault or Reset before using manual force.", migrated);
        OpenVisionLanguageService.EnsureCatalogFile(path, defaults);
        Assert.Equal(migrated, File.ReadAllText(path));
        var customPath = CreateTestCatalogPath();
        File.WriteAllText(customPath, "Key\tKorean\tEnglish\n" +
            "Sensor.FaultOverride\t고장 Override\tMy sensor fault\n" +
            "Sensor.ClearFaultHint\t내 센서 복구 안내\tClear the stuck-input fault or Reset before using manual force.\n" +
            "User.Custom\t사용자 값\tCustom value\n");
        OpenVisionLanguageService.EnsureCatalogFile(customPath, defaults);
        var custom = File.ReadAllText(customPath);
        Assert.Contains("Sensor.FaultOverride\t고장 Override\tMy sensor fault", custom);
        Assert.Contains("Sensor.ClearFaultHint\t내 센서 복구 안내\tClear the stuck-input fault or Reset before using manual force.", custom);
        Assert.Contains("User.Custom\t사용자 값\tCustom value", custom);
        OpenVisionLanguageService.EnsureCatalogFile(customPath, defaults);
        Assert.Equal(custom, File.ReadAllText(customPath));
    }

    [Fact]
    public void StationUnitCatalogMatchesR24UnitCreationAndKeepsEightyCharacterContract()
    {
        var assembly = typeof(OpenVisionLanguageService).Assembly;
        var resourceName = assembly.GetManifestResourceNames().Single(name => name.EndsWith("LocalizationCatalog.tsv", StringComparison.Ordinal));
        using var reader = new StreamReader(assembly.GetManifestResourceStream(resourceName)!);
        var catalog = reader.ReadToEnd().Split('\n').Select(line => line.TrimEnd('\r').Split('\t'))
            .Where(row => row.Length == 3).ToDictionary(row => row[0], StringComparer.Ordinal);
        foreach (var key in new[] { "Equipment.StationUnitAddButton", "Equipment.StationUnitDialogTitle" })
        {
            Assert.Equal("유닛 추가", catalog[key][1]);
            Assert.Equal("Add unit", catalog[key][2]);
        }
        Assert.Contains("80", catalog["Equipment.StationUnitUnitNameRequired"][1]);
        Assert.Contains("80", catalog["Equipment.StationUnitStationNameRequired"][2]);
    }

    [Fact]
    public void StationUnitDefaultMigrationPreservesCustomPairsAndIsStableOnReopen()
    {
        var assembly = typeof(OpenVisionLanguageService).Assembly;
        var resourceName = assembly.GetManifestResourceNames().Single(name => name.EndsWith("LocalizationCatalog.tsv", StringComparison.Ordinal));
        using var reader = new StreamReader(assembly.GetManifestResourceStream(resourceName)!);
        var defaults = reader.ReadToEnd();
        var path = CreateTestCatalogPath();
        File.WriteAllText(path, "Key\tKorean\tEnglish\n" +
            "Equipment.StationUnitAddButton\t스테이션·유닛 추가\tAdd station / unit\n" +
            "Equipment.StationUnitDialogTitle\t스테이션·유닛 설정\tStation and unit setup\n");
        OpenVisionLanguageService.EnsureCatalogFile(path, defaults);
        var migrated = File.ReadAllText(path);
        Assert.Contains("Equipment.StationUnitAddButton\t유닛 추가\tAdd unit", migrated);
        Assert.Contains("Equipment.StationUnitDialogTitle\t유닛 추가\tAdd unit", migrated);
        OpenVisionLanguageService.EnsureCatalogFile(path, defaults);
        Assert.Equal(migrated, File.ReadAllText(path));
        var customPath = CreateTestCatalogPath();
        File.WriteAllText(customPath, "Key\tKorean\tEnglish\n" +
            "Equipment.StationUnitAddButton\t내 유닛 추가\tAdd station / unit\n" +
            "Equipment.StationUnitDialogTitle\t스테이션·유닛 설정\tMy station dialog\n" +
            "User.Custom\t사용자 값\tCustom value\n");
        OpenVisionLanguageService.EnsureCatalogFile(customPath, defaults);
        var customized = File.ReadAllText(customPath);
        Assert.Contains("Equipment.StationUnitAddButton\t내 유닛 추가\tAdd station / unit", customized);
        Assert.Contains("Equipment.StationUnitDialogTitle\t스테이션·유닛 설정\tMy station dialog", customized);
        Assert.Contains("User.Custom\t사용자 값\tCustom value", customized);
        OpenVisionLanguageService.EnsureCatalogFile(customPath, defaults);
        Assert.Equal(customized, File.ReadAllText(customPath));
    }


    [Fact]
    public void InspectorSettingsHintMigratesOnlyExactDefaultAndAddsManualScopeWithoutChangingCustomPairs()
    {
        var assembly = typeof(OpenVisionLanguageService).Assembly;
        var resourceName = assembly.GetManifestResourceNames().Single(name => name.EndsWith("LocalizationCatalog.tsv", StringComparison.Ordinal));
        using var reader = new StreamReader(assembly.GetManifestResourceStream(resourceName)!);
        var defaults = reader.ReadToEnd();
        const string oldKorean = "구동·입출력 값은 변경 즉시 반영됩니다. 표시할 항목이 없으면 별도 구동 설정이 없는 부품입니다.";
        const string english = "Drive and I/O changes take effect immediately. If no fields appear, this component has no separate drive settings.";
        var path = CreateTestCatalogPath();
        File.WriteAllText(path, "Key\tKorean\tEnglish\nEquipment.DriveTabHint\t" + oldKorean + "\t" + english + "\n");
        OpenVisionLanguageService.EnsureCatalogFile(path, defaults);
        var migrated = File.ReadAllText(path);
        Assert.Contains("별도 구동 설정이 없는 장치입니다.\t" + english, migrated);
        Assert.Contains("Equipment.ManualControlScopeHint\t", migrated);
        OpenVisionLanguageService.EnsureCatalogFile(path, defaults);
        Assert.Equal(migrated, File.ReadAllText(path));
        var customPath = CreateTestCatalogPath();
        File.WriteAllText(customPath, "Key\tKorean\tEnglish\nEquipment.DriveTabHint\t내 연결 안내\t" + english + "\n" +
            "Equipment.ManualControlScopeHint\t내 수동 범위\tMy manual scope\nUser.Custom\t사용자 값\tCustom value\n");
        OpenVisionLanguageService.EnsureCatalogFile(customPath, defaults);
        var customized = File.ReadAllText(customPath);
        Assert.Contains("Equipment.DriveTabHint\t내 연결 안내\t" + english, customized);
        Assert.Contains("Equipment.ManualControlScopeHint\t내 수동 범위\tMy manual scope", customized);
        Assert.Contains("User.Custom\t사용자 값\tCustom value", customized);
        OpenVisionLanguageService.EnsureCatalogFile(customPath, defaults);
        Assert.Equal(customized, File.ReadAllText(customPath));
    }


    [Fact]
    public void SequenceInspectorTerminologyMigratesOnlyExactDefaults()
    {
        var assembly = typeof(OpenVisionLanguageService).Assembly;
        var resourceName = assembly.GetManifestResourceNames().Single(name => name.EndsWith("LocalizationCatalog.tsv", StringComparison.Ordinal));
        using var reader = new StreamReader(assembly.GetManifestResourceStream(resourceName)!);
        var defaults = reader.ReadToEnd();
        (string Key, string OldKorean, string English, string Korean)[] pairs =
        [
            ("Sequence.GraphView", "동작 흐름", "Action flow", "스텝 흐름"),
            ("Sequence.TableView", "단계 표", "Step table", "스텝 표"),
            ("Sequence.NewStep", "새 단계", "New step", "새 스텝"),
            ("Sequence.AddTooltip", "완료 단계 앞에 새 단계를 추가합니다.", "Insert a new step before the final Complete step.", "완료 스텝 앞에 새 스텝을 추가합니다."),
            ("Sequence.DeleteTooltip", "선택한 종료 단계가 아닌 단계를 삭제합니다.", "Delete the selected non-terminal step.", "종료 스텝을 제외한 선택 스텝을 삭제합니다."),
            ("Sequence.Step", "단계", "Step", "스텝"),
            ("Sequence.Action", "동작", "Action", "종류"),
            ("Sequence.WorkpieceTarget", "촬영 대상 작업물", "Target workpiece", "촬영 대상 제품"),
            ("Sequence.NoWorkpieceAssociation", "작업물 연결 안 함", "No workpiece association", "제품 연결 안 함"),
            ("Sequence.Timeout", "시간 제한", "Timeout", "타임아웃 (ms)"),
            ("Sequence.StepId", "단계 ID", "Step ID", "스텝 ID"),
            ("Sequence.ValidationIssueLocationFormat", "시퀀스 {0} · 단계 {1} · 대상 {2} · 속성 {3}", "Sequence {0} · step {1} · target {2} · property {3}", "시퀀스 {0} · 스텝 {1} · 대상 {2} · 속성 {3}"),
            ("Sequence.ExpectedCheckpointTooltip", "이 단계가 끝날 때 선택한 장비 상태를 검증합니다.", "Validate the selected equipment state when this step ends.", "이 스텝이 끝날 때 선택한 장비 상태를 검증합니다."),
            ("Sequence.SelectSequenceHint", "시퀀스를 선택해 단계를 편집하세요.", "Select a sequence to edit steps.", "시퀀스를 선택해 스텝을 편집하세요."),
            ("Sequence.ValidSummary", "유효 · {0}개 단계", "VALID · {0} steps", "유효 · 스텝 {0}개"),
            ("Sequence.Action.MoveAxis", "축 이동", "Move axis", "축 이동 명령"),
            ("Sequence.Action.TriggerCamera", "카메라 트리거", "Trigger camera", "촬영(트리거)"),
            ("Sequence.Action.WaitAxisDone", "축 완료 대기", "Wait for axis", "축 이동 완료 대기"),
            ("Sequence.Action.FeedWorkpiece", "작업물 투입", "Feed workpiece", "제품 투입"),
            ("Sequence.Action.EjectWorkpiece", "작업물 배출", "Eject workpiece", "제품 배출"),
            ("Sequence.Action.Delay", "시간 지연", "Delay", "딜레이"),
            ("Sequence.Action.FeedUnitWorkpiece", "유닛 작업물 투입", "Feed unit workpiece", "유닛 제품 투입"),
            ("Sequence.Action.ReceiveUnitWorkpiece", "유닛 작업물 수신", "Receive unit workpiece", "유닛 제품 수신"),
            ("Sequence.Template.move-axis-home", "축 원점 이동", "Move axis to home", "원점 위치 이동"),
            ("Sequence.Template.wait-axis-done", "축 완료 대기", "Wait for axis done", "축 이동 완료 대기"),
            ("Sequence.Template.trigger-camera", "가상 카메라 트리거", "Trigger virtual camera", "촬영(트리거)"),
        ];
        var path = CreateTestCatalogPath();
        File.WriteAllText(path, "Key\tKorean\tEnglish\n" + string.Join("\n", pairs.Select(pair => pair.Key + "\t" + pair.OldKorean + "\t" + pair.English)));
        OpenVisionLanguageService.EnsureCatalogFile(path, defaults);
        var migrated = File.ReadAllText(path);
        foreach (var pair in pairs) Assert.Contains(pair.Key + "\t" + pair.Korean + "\t" + pair.English, migrated);
        OpenVisionLanguageService.EnsureCatalogFile(path, defaults);
        Assert.Equal(migrated, File.ReadAllText(path));
        var customPath = CreateTestCatalogPath();
        var customRows = pairs.Select((pair, index) => pair.Key + "\t" + (index % 2 == 0 ? "사용자 스텝 " + index : pair.OldKorean) + "\t" + (index % 2 == 0 ? pair.English : "Custom step " + index)).ToArray();
        File.WriteAllText(customPath, "Key\tKorean\tEnglish\n" + string.Join("\n", customRows) + "\nUser.Custom\t사용자 값\tCustom value\n");
        OpenVisionLanguageService.EnsureCatalogFile(customPath, defaults);
        var customized = File.ReadAllText(customPath);
        foreach (var row in customRows) Assert.Contains(row, customized);
        Assert.Contains("User.Custom\t사용자 값\tCustom value", customized);
        OpenVisionLanguageService.EnsureCatalogFile(customPath, defaults);
        Assert.Equal(customized, File.ReadAllText(customPath));
    }

    [Fact]
    public void SequenceInspectorAddsBilingualDraftAndIsolatedTestCopy()
    {
        var assembly = typeof(OpenVisionLanguageService).Assembly;
        var resourceName = assembly.GetManifestResourceNames().Single(name => name.EndsWith("LocalizationCatalog.tsv", StringComparison.Ordinal));
        using var reader = new StreamReader(assembly.GetManifestResourceStream(resourceName)!);
        var defaults = reader.ReadToEnd();
        var path = CreateTestCatalogPath();
        File.WriteAllText(path, "Key\tKorean\tEnglish\nUser.Custom\t사용자 값\tCustom value\n");
        OpenVisionLanguageService.EnsureCatalogFile(path, defaults);
        var catalog = File.ReadAllText(path);
        foreach (string key in new[] { "Sequence.StepSettings", "Sequence.ApplyStep", "Sequence.DiscardStep", "Sequence.StepDraftPending", "Sequence.StepDraftApplied", "Sequence.StepDraftIntegerError", "Sequence.StepDraftStale", "Sequence.StepDraftInvalid", "Sequence.TestSelectedStep", "Sequence.StepTestScope", "Sequence.StepTestBusy", "Sequence.StepTestComplete", "Sequence.StepTestIncomplete", "Sequence.StepTestUnsupported", "Sequence.WaitForAxisCompletion", "Sequence.EjectionDelay", "Sequence.DestinationUnit", "Sequence.SelectStepSettingsHint", "Sequence.StepTimeout" })
        {
            var columns = catalog.Split('\n').Select(line => line.TrimEnd('\r').Split('\t')).Single(row => row[0] == key);
            Assert.Equal(3, columns.Length);
            Assert.False(string.IsNullOrWhiteSpace(columns[1]));
            Assert.False(string.IsNullOrWhiteSpace(columns[2]));
        }
        Assert.Contains("User.Custom\t사용자 값\tCustom value", catalog);
        OpenVisionLanguageService.EnsureCatalogFile(path, defaults);
        Assert.Equal(catalog, File.ReadAllText(path));
    }


    [Fact]
    public void SequenceUnitSettingsAddsBilingualCopyWithoutReplacingCustomizedRows()
    {
        var assembly = typeof(OpenVisionLanguageService).Assembly;
        var resourceName = assembly.GetManifestResourceNames().Single(name => name.EndsWith("LocalizationCatalog.tsv", StringComparison.Ordinal));
        using var reader = new StreamReader(assembly.GetManifestResourceStream(resourceName)!);
        var defaults = reader.ReadToEnd();
        var path = CreateTestCatalogPath();
        File.WriteAllText(path, "Key\tKorean\tEnglish\nUser.Custom\t사용자 값\tCustom value\nSequence.OwnerUnit\t사용자 소유 유닛\tCustom owner unit\n");
        OpenVisionLanguageService.EnsureCatalogFile(path, defaults);
        var catalog = File.ReadAllText(path);
        foreach (string key in new[] { "Sequence.UnitSettings", "Sequence.OwnerUnit", "Sequence.CommonSequence", "Sequence.AddUnitSequence", "Sequence.AddUnitSequenceHint", "Sequence.UnitRepeat", "Sequence.UnitRepeatRange", "Sequence.ApplyUnitRepeat", "Sequence.UnitSettingsScope", "Sequence.UnitAlreadyHasSequence", "Sequence.UnitRepeatError", "Sequence.UnitRepeatApplied", "Sequence.UnitSequenceName" })
        {
            var columns = catalog.Split('\n').Select(line => line.TrimEnd('\r').Split('\t')).Single(row => row[0] == key);
            Assert.Equal(3, columns.Length);
            Assert.False(string.IsNullOrWhiteSpace(columns[1]) || string.IsNullOrWhiteSpace(columns[2]));
        }
        Assert.Contains("User.Custom\t사용자 값\tCustom value", catalog);
        Assert.Contains("Sequence.OwnerUnit\t사용자 소유 유닛\tCustom owner unit", catalog);
        Assert.Contains("Sequence.UnitRepeatError\t유닛 반복 횟수는 1–100 범위의 정수여야 합니다.\tUnit repeat count must be an integer from 1–100.", catalog);
        OpenVisionLanguageService.EnsureCatalogFile(path, defaults);
        Assert.Equal(catalog, File.ReadAllText(path));
    }

    [Fact]
    public void IntegrationSummaryAddsBilingualStatesAndPreservesCustomizedRows()
    {
        var assembly = typeof(OpenVisionLanguageService).Assembly;
        var resourceName = assembly.GetManifestResourceNames().Single(name => name.EndsWith("LocalizationCatalog.tsv", StringComparison.Ordinal));
        using var reader = new StreamReader(assembly.GetManifestResourceStream(resourceName)!);
        var defaults = reader.ReadToEnd();
        var path = CreateTestCatalogPath();
        File.WriteAllText(path, "Key\tKorean\tEnglish\nUser.Custom\t사용자 값\tCustom value\nIntegration.RequestCaption\t내 요청\tMy request\n");
        OpenVisionLanguageService.EnsureCatalogFile(path, defaults);
        var catalog = File.ReadAllText(path);
        foreach (string key in new[] { "Integration.RequestCaption", "Integration.ReceiptCaption", "Integration.ResultCaption", "Integration.ExchangeDetails", "Integration.RequestNone", "Integration.ReceiptNone", "Integration.ResultNone" })
        {
            var columns = catalog.Split('\n').Select(line => line.TrimEnd('\r').Split('\t')).Single(row => row[0] == key);
            Assert.Equal(3, columns.Length);
            Assert.False(string.IsNullOrWhiteSpace(columns[1]) || string.IsNullOrWhiteSpace(columns[2]));
        }
        Assert.Contains("User.Custom\t사용자 값\tCustom value", catalog);
        Assert.Contains("Integration.RequestCaption\t내 요청\tMy request", catalog);
        Assert.Contains("Integration.ReceiptNone\t현재 프로젝트의 수신 확인이 없습니다.\tNo Acknowledgement exists for the current project.", catalog);
        OpenVisionLanguageService.EnsureCatalogFile(path, defaults);
        Assert.Equal(catalog, File.ReadAllText(path));
    }

    [Fact]
    public void IntegrationReadinessAndObservedStatesMigrateExactDefaultsAndKeepCustomRows()
    {
        var assembly = typeof(OpenVisionLanguageService).Assembly;
        var resourceName = assembly.GetManifestResourceNames().Single(name => name.EndsWith("LocalizationCatalog.tsv", StringComparison.Ordinal));
        using var reader = new StreamReader(assembly.GetManifestResourceStream(resourceName)!);
        var defaults = reader.ReadToEnd();
        var expected = new (string Key, string OldKorean, string NewKorean, string English)[]
        {
            ("Integration.SetupStepConsumer", "3/6 2D 소비자 버전과 40자리 source commit을 입력하세요.", "3/6 2D 검사 프로그램 버전과 40자리 source commit을 입력하세요.", "3/6 Enter the 2D consumer version and 40-character source commit."),
            ("Integration.SetupStepSave", "6/6 변경 사항을 저장한 뒤 명시적 연결 또는 Handoff를 실행하세요.", "6/6 변경 사항을 저장한 뒤 명시적 연결 또는 검사 요청을 실행하세요.", "6/6 Save the changes before an explicit connection or Handoff."),
            ("Integration.SetupReady", "설정 준비 완료. 수신 시작, 상대 Ping 또는 명시적 Handoff 중 하나를 선택하세요.", "설정 준비 완료. 수신 시작, 상대 Ping 또는 명시적 검사 요청 중 하나를 선택하세요.", "Setup ready. Choose Start listening, Ping peer, or an explicit Handoff."),
            ("Integration.ConsumerVersion", "2D 소비자 버전", "2D 검사 프로그램 버전", "2D consumer version"),
            ("Integration.ConsumerCommit", "2D 소비자 source commit", "2D 검사 프로그램 source commit", "2D consumer source commit"),
            ("Integration.ConsumerCommitTooltip", "2D 소비자 clean build의 40자리 source commit을 입력합니다.", "2D 검사 프로그램의 clean build에 해당하는 40자리 source commit을 입력합니다.", "Enter the 40-character source commit from the 2D consumer clean build."),
            ("Integration.ThreeDConsumerVersion", "3D consumer 버전", "3D 검사 프로그램 버전", "3D consumer version"),
            ("Integration.ThreeDConsumerCommit", "3D consumer source commit", "3D 검사 프로그램 source commit", "3D consumer source commit"),
            ("Integration.TransactionHistoryFilterPendingReview", "검토 대기", "수신 확인 대기", "Pending review"),
            ("Integration.TransactionHistoryFilterReviewed", "검토됨", "수신 확인됨", "Reviewed"),
            ("Integration.TransactionHistoryFilterRejected", "거절됨", "거부됨", "Rejected"),
            ("Integration.TransactionHistoryFilterPublished", "결과 게시됨", "결과 수신", "Result published"),
            ("Integration.MmiApplyInspectionResult", "검사 결과 적용", "검사 결과 반영", "Apply inspection result"),
            ("Integration.MmiApplyInspectionResultTooltip", "현재 프로세스가 만든 Handoff와 일치하는 검증된 Result만 일시정지된 시뮬레이션에 적용합니다.", "현재 프로세스의 검사 요청과 일치하는 검증된 결과만 일시정지된 시뮬레이션에 반영합니다.", "Apply only a validated Result matching a Handoff created by this process to the paused Simulation."),
        };
        var original = "Key\tKorean\tEnglish\n" + string.Join("\n", expected.Select(item => $"{item.Key}\t{item.OldKorean}\t{item.English}")) + "\nUser.Custom\t사용자 값\tCustom value\n";
        var path = CreateTestCatalogPath();
        File.WriteAllText(path, original);
        OpenVisionLanguageService.EnsureCatalogFile(path, defaults);
        var catalog = File.ReadAllText(path);
        foreach (var item in expected) Assert.Contains($"{item.Key}\t{item.NewKorean}\t{item.English}", catalog);
        foreach (var key in new[] { "Integration.OpenSetup", "Integration.SetupStepThreeD", "Integration.RequestStatusFormat", "Integration.ReceiptStatusFormat", "Integration.ReceiptReadFailed", "Integration.ReceiptPendingValidation", "Integration.ResultStatusFormat", "Integration.ResultReadFailed", "Integration.ResultPendingValidation", "Integration.ReceiptAccepted", "Integration.ReceiptRejected", "Integration.OutcomePass", "Integration.OutcomeNg", "Integration.OutcomeNotMeasured", "Integration.OutcomeIndeterminate", "Integration.OutcomeCorrelationMismatch", "Integration.OutcomeTampered", "Integration.OutcomeExecutionError", "Integration.ResultCompleted", "Integration.ResultFailed", "Integration.ResultCancelled" })
        {
            var row = catalog.Split('\n').Select(line => line.TrimEnd('\r').Split('\t')).Single(columns => columns[0] == key);
            Assert.Equal(3, row.Length);
            Assert.False(string.IsNullOrWhiteSpace(row[1]) || string.IsNullOrWhiteSpace(row[2]));
        }
        OpenVisionLanguageService.EnsureCatalogFile(path, defaults);
        Assert.Equal(catalog, File.ReadAllText(path));
        var customPath = CreateTestCatalogPath();
        var customRows = "Key\tKorean\tEnglish\nUser.Custom\t사용자 값\tCustom value\n" + string.Join("\n", expected.Select((item, index) => $"{item.Key}\t{(index % 2 == 0 ? "사용자 " + item.OldKorean : item.OldKorean)}\t{(index % 2 == 1 ? "Custom " + item.English : item.English)}"));
        File.WriteAllText(customPath, customRows);
        OpenVisionLanguageService.EnsureCatalogFile(customPath, defaults);
        var customized = File.ReadAllText(customPath);
        foreach (var row in customRows.Split('\n').Skip(1)) Assert.Contains(row, customized);
        Assert.Contains("Integration.OutcomePass\tOK\tPass", catalog);
        Assert.Contains("Integration.OutcomeNg\tNG\tNg", catalog);
        Assert.Contains("Integration.ResultStatusFormat\t최근 검사 결과: {0} · {1} · 운전 {2}\tLatest Result: {0} · {1} · Run {2}", catalog);
        OpenVisionLanguageService.EnsureCatalogFile(customPath, defaults);
        Assert.Equal(customized, File.ReadAllText(customPath));
    }

    [Fact]
    public void R24ApprovedTermsMigrateSavedDefaultsPreserveCustomPairsAndReopen()
    {
        var assembly = typeof(OpenVisionLanguageService).Assembly;
        var resourceName = assembly.GetManifestResourceNames().Single(name => name.EndsWith("LocalizationCatalog.tsv", StringComparison.Ordinal));
        using var reader = new StreamReader(assembly.GetManifestResourceStream(resourceName)!);
        var defaults = reader.ReadToEnd();
        var expected = defaults.Split('\n').Select(line => line.TrimEnd('\r').Split('\t'))
            .Where(row => row.Length == 3).ToDictionary(row => row[0], StringComparer.Ordinal);
        (string Key, string Korean, string English)[] previous =
        [
            ("Connections.InputOverview", "촬영 입력 정보", "Capture input information"),
            ("Connections.ConnectionOverview", "검사 연결 요약", "Inspection connection summary"),
            ("Scene.LegendCamera", "▣ 촬영부", "▣ Camera"),
            ("Camera.SourceWidth", "폭 (px)", "Width (px)"),
            ("Camera.SourceHeight", "높이 (px)", "Height (px)"),
            ("Camera.ApplySource", "설정 적용", "Apply settings"),
            ("Camera.ApplySourceTooltip", "검증된 설정을 프로젝트에 적용합니다. 획득은 자동으로 시작하지 않습니다.", "Apply validated settings to the project without starting acquisition."),
            ("Camera.SourceDraftReady", "유효한 변경입니다. 설정 적용을 선택하세요.", "Valid changes. Select Apply settings."),
            ("Camera.WorkpieceAssociation", "연결 작업물 ID", "Associated workpiece ID"),
            ("Camera.WorkpieceInstanceId", "실행 작업물 ID", "Workpiece instance ID"),
            ("Integration.Hint", "교환 설정을 저장한 뒤 Handoff 게시, Result 새로고침, 시뮬레이션 적용을 각각 명시적으로 실행합니다.", "Save the exchange setup, then explicitly publish the Handoff, refresh Result, and apply it to Simulation."),
            ("Integration.WaitForExternalResult", "카메라 전송 후 외부 Result 대기", "Wait for external Result after camera transfer"),
            ("Integration.WaitForExternalResultCompactLabel", "Result 대기", "Wait for Result"),
            ("Integration.PublishTwoD", "2D/Image 게시", "Publish 2D/Image"),
            ("Integration.PublishTwoDShort", "2D/Image 게시", "Publish 2D/Image"),
            ("Integration.RefreshResults", "Result 새로고침", "Refresh Result"),
            ("Integration.MmiShowInputWithSettings", "입력 넓게", "Widen input"),
            ("Sequence.MoveUp", "위로 이동", "Move up"),
            ("Sequence.MoveDown", "아래로 이동", "Move down"),
            ("Equipment.InspectorDraftLeaveMessage", "설정 변경을 적용하거나 버린 뒤 계속하세요. 취소하면 현재 화면에서 편집을 계속합니다.", "Apply or discard settings changes before continuing. Cancel keeps this editor open."),
            ("Equipment.PlacementDraftDiscard", "변경 버리기", "Discard changes"),
            ("Equipment.ManualHint.CameraReady", "수동 제어가 준비됐습니다. 선택 촬영부의 촬영 명령을 실행하세요. 편집 복귀 시 운전을 종료·초기화합니다.", "Manual control is ready. Trigger the selected camera. Returning to editing ends and resets the run."),
            ("Camera.CurrentInputs", "현재 입력", "Current inputs"),
            ("Camera.InputRecordedStep", "기록된 촬영 단계: {0}", "Recorded capture step: {0}"),
            ("Camera.InputUnrecordedStep", "촬영 단계 미기록", "Capture step not recorded"),
            ("Camera.InputContext", "작업물: {0} · 레시피: {1}", "Workpiece: {0} · recipe: {1}"),
            ("Camera.CaptureStation", "촬영부", "Capture station"),
            ("Camera.InspectionConnection", "검사 연결", "Inspection connection"),
            ("Camera.CaptureWidth", "촬영 폭 (LU)", "Capture width (LU)"),
            ("Camera.CaptureScopeHint", "선택한 촬영부에 저장합니다. 운전 입력에는 당시 작업물·촬영 단계를 함께 남깁니다.", "Saved per capture station. Run inputs retain their workpiece and capture step."),
            ("Camera.UseNumericalInput", "수치 모의 입력 사용", "Use numerical mock input"),
            ("Camera.MockDefect", "모의 불량 입력", "Mock defect input"),
            ("Camera.ApplySettings", "설정 적용", "Apply settings"),
            ("Camera.CaptureInput", "촬영 입력", "Capture input"),
            ("Camera.GenerationExecutionHint", "설정 적용 후 선택한 실행 대상의 레시피를 시작해 촬영 입력을 생성합니다.", "Apply the settings, then start the selected run target’s recipe to generate camera inputs."),
            ("Camera.Capture.Parameter.Tilt", "기울기", "Tilt"),
            ("Camera.Capture.Parameter.Shake", "흔들림", "Shake"),
            ("Camera.Capture.Parameter.Light", "조명", "Light"),
            ("Camera.Capture.Loaded", "촬영 설정을 불러왔습니다. 실행은 별도 버튼입니다.", "Capture settings loaded. Start acquisition separately."),
            ("Camera.Capture.AppliedNeedsSave", "촬영 설정 적용 완료 · 프로젝트 저장 필요 · 실행은 별도 버튼입니다.", "Capture settings applied · save the project · start acquisition separately."),
            ("Camera.Capture.AppliedSaved", "적용한 촬영 설정을 프로젝트에 저장했습니다. 실행은 별도 버튼입니다.", "Capture settings saved to the project. Start acquisition separately."),
            ("Shell.ProjectRevision", "설정 r{0}", "Settings r{0}"),
        ];
        var path = CreateTestCatalogPath();
        File.WriteAllText(path, "Key\tKorean\tEnglish\n" + string.Join("\n", previous.Select(row => $"{row.Key}\t{row.Korean}\t{row.English}")) + "\nUser.Custom\t사용자 값\tCustom value\n");
        OpenVisionLanguageService.EnsureCatalogFile(path, defaults);
        var migrated = File.ReadAllText(path);
        foreach (var row in previous)
        {
            Assert.Equal(row.English, expected[row.Key][2]);
            Assert.Contains($"{row.Key}\t{expected[row.Key][1]}\t{row.English}", migrated);
        }
        Assert.Contains("Equipment.InspectorDraftApplyAndContinue\t적용 후 계속\tApply changes", migrated);
        Assert.Contains("User.Custom\t사용자 값\tCustom value", migrated);
        OpenVisionLanguageService.EnsureCatalogFile(path, defaults);
        Assert.Equal(migrated, File.ReadAllText(path));
        foreach (bool customEnglish in new[] { false, true })
        {
            var customPath = CreateTestCatalogPath();
            var customizedRows = previous.Select(row => $"{row.Key}\t{(customEnglish ? row.Korean : "사용자 " + row.Korean)}\t{(customEnglish ? "Custom " + row.English : row.English)}").ToArray();
            File.WriteAllText(customPath, "Key\tKorean\tEnglish\n" + string.Join("\n", customizedRows));
            OpenVisionLanguageService.EnsureCatalogFile(customPath, defaults);
            var preserved = File.ReadAllText(customPath);
            foreach (var row in customizedRows) Assert.Contains(row, preserved);
            OpenVisionLanguageService.EnsureCatalogFile(customPath, defaults);
            Assert.Equal(preserved, File.ReadAllText(customPath));
        }
    }

    [Theory]
    [InlineData("set-output-on", "출력 ON 설정", "Set output ON", "출력 ON")]
    [InlineData("set-output-off", "출력 OFF 설정", "Set output OFF", "출력 OFF")]
    [InlineData("move-axis-home", "원점 위치 이동", "Move axis to home", "축 이동 명령")]
    [InlineData("move-axis-home", "축 원점 이동", "Move axis to home", "축 이동 명령")]
    [InlineData("feed-workpiece", "작업물 투입", "Feed workpiece", "제품 투입")]
    [InlineData("eject-workpiece", "작업물 배출", "Eject workpiece", "제품 배출")]
    [InlineData("delay", "시간 지연 (초)", "Delay (seconds)", "딜레이")]
    [InlineData("feed-unit", "유닛 작업물 투입", "Feed unit workpiece", "유닛 제품 투입")]
    [InlineData("receive-unit", "유닛 작업물 수신", "Receive unit workpiece", "유닛 제품 수신")]
    public void TemplateCaptionMigrationUpdatesKnownDefaultsAndPreservesEitherCustomizedLanguage(string id, string oldKorean, string english, string korean)
    {
        var assembly = typeof(OpenVisionLanguageService).Assembly;
        string resource = assembly.GetManifestResourceNames().Single(name => name.EndsWith("LocalizationCatalog.tsv", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resource)!;
        using var reader = new StreamReader(stream);
        string defaults = reader.ReadToEnd();
        string key = "Sequence.Template." + id;
        string expected = $"{key}\t{korean}\t{english}";
        Assert.Contains(expected, defaults);
        var path = CreateTestCatalogPath();
        File.WriteAllText(path, $"Key\tKorean\tEnglish\n{key}\t{oldKorean}\t{english}\nUser.Custom\t사용자 값\tCustom value\n");
        OpenVisionLanguageService.EnsureCatalogFile(path, defaults);
        var migrated = File.ReadAllBytes(path);
        Assert.Contains(expected, File.ReadAllText(path));
        Assert.Contains("User.Custom\t사용자 값\tCustom value", File.ReadAllText(path));
        OpenVisionLanguageService.EnsureCatalogFile(path, defaults);
        Assert.Equal(migrated, File.ReadAllBytes(path));
        foreach (bool customEnglish in new[] { false, true })
        {
            var customPath = CreateTestCatalogPath();
            string custom = $"{key}\t{(customEnglish ? oldKorean : "사용자 " + oldKorean)}\t{(customEnglish ? "Custom " + english : english)}";
            File.WriteAllText(customPath, "Key\tKorean\tEnglish\n" + custom + "\n");
            OpenVisionLanguageService.EnsureCatalogFile(customPath, defaults);
            Assert.Contains(custom, File.ReadAllText(customPath));
            var preserved = File.ReadAllBytes(customPath);
            OpenVisionLanguageService.EnsureCatalogFile(customPath, defaults);
            Assert.Equal(preserved, File.ReadAllBytes(customPath));
        }
    }

    [Fact]
    public void ScenarioOverlaySavedDefaultsMigrateAllEightRowsAndReopen()
    {
        var assembly = typeof(OpenVisionLanguageService).Assembly;
        var resource = assembly.GetManifestResourceNames().Single(name => name.EndsWith("LocalizationCatalog.tsv", StringComparison.Ordinal));
        using var reader = new StreamReader(assembly.GetManifestResourceStream(resource)!);
        string defaults = reader.ReadToEnd();
        var expected = defaults.Split('\n').Select(line => line.TrimEnd('\r').Split('\t'))
            .Where(row => row.Length == 3).ToDictionary(row => row[0], StringComparer.Ordinal);
        Assert.Equal("After configuring conditions, close this dialog and switch to Run mode. Reopen it from View to start the scenario.", expected["Simulation.TestWorkspaceRunHint"][2]);
        var path = CreateTestCatalogPath();
        File.WriteAllText(path, "Key\tKorean\tEnglish\n" + string.Join("\n", ScenarioOverlayLegacyRows.Select(row => $"{row.Key}\t{row.Korean}\t{row.English}")) + "\nUser.Custom\t사용자 값\tCustom value\n");
        OpenVisionLanguageService.EnsureCatalogFile(path, defaults);
        string migrated = File.ReadAllText(path);
        foreach (var row in ScenarioOverlayLegacyRows)
            Assert.Contains($"{row.Key}\t{expected[row.Key][1]}\t{expected[row.Key][2]}", migrated);
        Assert.Contains("User.Custom\t사용자 값\tCustom value", migrated);
        var bytes = File.ReadAllBytes(path);
        OpenVisionLanguageService.EnsureCatalogFile(path, defaults);
        Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ScenarioOverlayMigrationPreservesCustomizedLanguageAndReopen(bool customEnglish)
    {
        var assembly = typeof(OpenVisionLanguageService).Assembly;
        var resource = assembly.GetManifestResourceNames().Single(name => name.EndsWith("LocalizationCatalog.tsv", StringComparison.Ordinal));
        using var reader = new StreamReader(assembly.GetManifestResourceStream(resource)!);
        string defaults = reader.ReadToEnd();
        var customized = ScenarioOverlayLegacyRows.Select(row => $"{row.Key}\t{(customEnglish ? row.Korean : "사용자 " + row.Korean)}\t{(customEnglish ? "Custom " + row.English : row.English)}").ToArray();
        var path = CreateTestCatalogPath();
        File.WriteAllText(path, "Key\tKorean\tEnglish\n" + string.Join("\n", customized));
        OpenVisionLanguageService.EnsureCatalogFile(path, defaults);
        string preserved = File.ReadAllText(path);
        foreach (string row in customized) Assert.Contains(row, preserved);
        var bytes = File.ReadAllBytes(path);
        OpenVisionLanguageService.EnsureCatalogFile(path, defaults);
        Assert.Equal(bytes, File.ReadAllBytes(path));
    }


    private static readonly (string Key, string Korean, string English)[] RecipeConnectionsLegacyRows =
    [
        ("Connections.CheckReadinessTooltip", "현재 프로젝트 전체를 컴파일해 실행 가능 여부를 확인하며 시뮬레이션은 시작하지 않습니다.", "Compile the current project to check whether it can run without starting simulation."),
        ("Connections.ReadinessPassedDetail", "현재 연결과 시퀀스가 컴파일되었습니다. 이 확인은 시뮬레이션을 시작하지 않습니다.", "The current connections and sequence compile successfully. This check does not start simulation."),
        ("Connections.ReadinessStale", "만료됨", "Stale"),
        ("Connections.ReadinessStaleDetail", "프로젝트 revision이 {0}에서 {1}(으)로 변경되어 준비 확인이 만료되었습니다. 다시 준비 확인을 실행하십시오.", "Readiness is stale because the project revision changed from {0} to {1}. Run readiness again."),
        ("Connections.ReadinessComparisonInitial", "이번 준비 확인 결과를 기준으로 삼았습니다.", "This readiness result is the baseline."),
        ("Connections.ReadinessComparisonUnchanged", "이전 준비 확인과 동일합니다.", "Readiness is unchanged from the previous check."),
        ("Connections.ReadinessComparisonImproved", "이전 준비 확인보다 개선되었습니다.", "Readiness improved from the previous check."),
        ("Connections.ReadinessComparisonRegressed", "이전 준비 확인보다 악화되었습니다.", "Readiness regressed from the previous check."),
        ("Connections.ReadinessComparisonChanged", "준비 확인 결과가 변경되었습니다.", "Readiness result changed."),
        ("Connections.ReadinessComparisonRerunRequired", "편집 후 준비 확인을 다시 실행해야 합니다.", "Run readiness again after editing."),

        ("Connections.AddSensorTooltip", "선택한 스테이지 또는 워크피스를 감지하는 DI 센서를 추가합니다.", "Add a DI sensor that detects the selected stage or workpiece."),
        ("Connections.AddWorkpiece", "워크피스", "Workpiece"),
        ("Connections.AddWorkpieceTooltip", "프로젝트의 컨베이어에 연결된 워크피스를 추가합니다.", "Add a workpiece connected to a project conveyor."),
        ("Connections.Behavior", "동작 바인딩", "Behavior binding"),
        ("Connections.CheckReadiness", "시뮬레이션 준비 확인", "Check simulation readiness"),
        ("Connections.NoSequenceUse", "직접 사용하는 단계 없음", "No directly linked step"),
        ("Connections.OpenStep", "단계 열기", "Open step"),
        ("Connections.OpenStepRejectedStatus", "연결된 시퀀스 단계를 찾을 수 없습니다.", "The linked sequence step could not be found."),
        ("Connections.OpenStepStatus", "시퀀스 단계 '{0}'을(를) 열었습니다.", "Opened sequence step '{0}'."),
        ("Connections.OpenStepTooltip", "이 연결을 사용하는 첫 번째 시퀀스 단계를 선택합니다.", "Select the first sequence step that uses this connection."),
        ("Connections.QuickAddHint", "기존 프로젝트 모델에 축·장치·I/O가 함께 생성됩니다. 센서는 스테이지 또는 워크피스 카드를 먼저 선택하면 해당 대상을 연결하며, 상세 연결은 오른쪽 속성에서 편집합니다.", "Axis, device, and I/O definitions are created together in the existing project model. For a sensor, first select its stage or workpiece target; edit detailed links in Properties."),
        ("Connections.QuickAddTitle", "구성요소 추가 및 자동 연결", "Add and connect equipment"),
        ("Connections.ReadinessFailedStatus", "시뮬레이션 준비 확인에서 오류가 발견되었습니다.", "Simulation readiness found an error."),
        ("Connections.ReadinessNotCheckedDetail", "운전 전에 시뮬레이션 준비 확인을 실행하십시오.", "Check simulation readiness before Run."),
        ("Connections.ReadinessPassedStatus", "시뮬레이션 준비 확인을 통과했습니다. 시뮬레이션은 시작되지 않았습니다.", "Simulation readiness passed. Simulation was not started."),
        ("Connections.ReturnToProcessPlan", "공정 계획으로 돌아가기", "Return to process plan"),
        ("Connections.SequenceUse", "시퀀스 사용", "Sequence use"),
        ("Connections.ValidationSequenceLocationFormat", "시퀀스 {0} · 단계 {1}", "sequence {0} · step {1}"),
    ];

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    public void RecipeConnectionsSavedDefaultsMigrateAndPreserveCustomLanguageOnReopen(int customizedLanguage)
    {
        var assembly = typeof(OpenVisionLanguageService).Assembly;
        var resource = assembly.GetManifestResourceNames().Single(name => name.EndsWith("LocalizationCatalog.tsv", StringComparison.Ordinal));
        using var reader = new StreamReader(assembly.GetManifestResourceStream(resource)!);
        string defaults = reader.ReadToEnd();
        var expected = defaults.Split('\n').Select(line => line.TrimEnd('\r').Split('\t'))
            .Where(row => row.Length == 3).ToDictionary(row => row[0], StringComparer.Ordinal);
        var rows = RecipeConnectionsLegacyRows.Select(row => $"{row.Key}\t{(customizedLanguage == 0 ? "사용자 " + row.Korean : row.Korean)}\t{(customizedLanguage == 1 ? "Custom " + row.English : row.English)}").ToArray();
        var path = CreateTestCatalogPath();
        File.WriteAllText(path, "Key\tKorean\tEnglish\n" + string.Join("\n", rows));
        OpenVisionLanguageService.EnsureCatalogFile(path, defaults);
        string actual = File.ReadAllText(path);
        if (customizedLanguage < 0)
        {
            foreach (var row in RecipeConnectionsLegacyRows) Assert.Contains($"{row.Key}\t{expected[row.Key][1]}\t{expected[row.Key][2]}", actual);
        }
        else foreach (string row in rows) Assert.Contains(row, actual);
        Assert.Contains("Connections.StationEntry\t스테이션\tStation", actual);
        var bytes = File.ReadAllBytes(path);
        OpenVisionLanguageService.EnsureCatalogFile(path, defaults);
        Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    public void ExampleGallerySavedDefaultsMigrateAndPreserveCustomLanguageOnReopen(int customizedLanguage)
    {
        var assembly = typeof(OpenVisionLanguageService).Assembly;
        var resource = assembly.GetManifestResourceNames().Single(name => name.EndsWith("LocalizationCatalog.tsv", StringComparison.Ordinal));
        using var reader = new StreamReader(assembly.GetManifestResourceStream(resource)!);
        string defaults = reader.ReadToEnd();
        var expected = defaults.Split('\n').Select(line => line.TrimEnd('\r').Split('\t')).Where(row => row.Length == 3).ToDictionary(row => row[0], StringComparer.Ordinal);
        (string Key, string Korean, string English)[] legacy =
        [
            ("Gallery.EquipmentFocus", "고유 장비", "Distinctive equipment"),
            ("Gallery.ProjectSchema", "프로젝트 스키마", "Project schema"),
            ("Gallery.ValidationPending", "대기", "Pending"),
            ("Gallery.Open", "반도체 레시피 갤러리...", "Semiconductor recipe gallery..."),
            ("Gallery.Title", "반도체 레시피 갤러리", "Semiconductor recipe gallery"),
            ("Gallery.Subtitle", "장비 구성을 한눈에 비교하고 복사본을 만들어 편집하십시오.", "Compare equipment configurations at a glance, then create an editable copy."),
            ("Gallery.CopyHint", "원본 템플릿은 변경되지 않습니다.", "The original template remains unchanged."),
            ("Gallery.SafePreview", "레시피를 선택해도 시뮬레이션은 시작되지 않습니다. 복사본은 설계 모드로 열립니다.", "Selecting a recipe does not start simulation. The copy opens in Design mode."),
            ("Gallery.ValidateAll", "10개 모두 검증", "Validate all 10"),
            ("Gallery.ValidateAllTooltip", "내장 레시피 10개를 순서대로 컴파일하고 격리 드라이런합니다.", "Compile and isolated-dry-run all 10 bundled recipes in order."),
            ("Gallery.SaveCompatibilityReport", "호환성 보고서 저장", "Save compatibility report"),
        ];
        var rows = legacy.Select(row => $"{row.Key}\t{(customizedLanguage == 0 ? "사용자 " + row.Korean : row.Korean)}\t{(customizedLanguage == 1 ? "Custom " + row.English : row.English)}").ToArray();
        var path = CreateTestCatalogPath();
        File.WriteAllText(path, "Key\tKorean\tEnglish\n" + string.Join("\n", rows));
        OpenVisionLanguageService.EnsureCatalogFile(path, defaults);
        string actual = File.ReadAllText(path);
        if (customizedLanguage < 0)
            foreach (var row in legacy) Assert.Contains($"{row.Key}\t{expected[row.Key][1]}\t{expected[row.Key][2]}", actual);
        else foreach (string row in rows) Assert.Contains(row, actual);
        Assert.Contains("Gallery.Empty\t사용할 수 있는 예제가 없습니다.\tNo examples are available.", actual);
        var bytes = File.ReadAllBytes(path);
        OpenVisionLanguageService.EnsureCatalogFile(path, defaults);
        Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    [Fact]
    public void StepTemplateDescriptionsContainExactlyTheSixteenRoleContracts()
    {
        var assembly = typeof(OpenVisionLanguageService).Assembly;
        string resource = assembly.GetManifestResourceNames().Single(name => name.EndsWith("LocalizationCatalog.tsv", StringComparison.Ordinal));
        using var reader = new StreamReader(assembly.GetManifestResourceStream(resource)!);
        var descriptions = reader.ReadToEnd().Split('\n').Select(line => line.TrimEnd('\r').Split('\t'))
            .Where(row => row.Length == 3 && row[0].StartsWith("R24.StepTemplateDescription.", StringComparison.Ordinal))
            .ToDictionary(row => row[0]["R24.StepTemplateDescription.".Length..], row => row, StringComparer.Ordinal);
        (string Id, string English)[] expected =
        [
            ("set-output-on", "Set the output signal ON. Check completion in a separate wait step."),
            ("set-output-off", "Set the output signal OFF."),
            ("wait-input-on", "Wait for the input signal to turn ON within the timeout."),
            ("wait-input-off", "Wait for the input signal to turn OFF within the timeout."),
            ("move-axis-home", "Command the axis to its home position. Wait for completion in a separate step."),
            ("wait-axis-done", "Wait for the previously commanded axis movement to finish within the timeout."),
            ("trigger-camera", "Request a virtual camera capture using its configured image source."),
            ("feed-workpiece", "Feed a product at the selected product position. Select the position after adding."),
            ("eject-workpiece", "Eject the product at the selected product position. Select the position after adding."),
            ("delay", "Wait for the specified simulation time."),
            ("feed-unit", "Feed a new product into the current unit. Check the product flow settings."),
            ("receive-unit", "Wait for the current unit to receive a product from the previous unit."),
            ("transfer-unit", "Transfer the product to the next unit by conveyor. Check the transfer time."),
            ("eject-unit", "Route the current unit product to an exit according to its inspection result."),
            ("branch-inspection", "Branch to the OK or NG step according to the current unit inspection result."),
            ("call-subsequence", "Call another sequence. The current sequence cannot be selected."),
        ];
        Assert.Equal(expected.Select(row => row.Id).Order(), descriptions.Keys.Order());
        foreach (var row in expected)
        {
            Assert.False(string.IsNullOrWhiteSpace(descriptions[row.Id][1]));
            Assert.Equal(row.English, descriptions[row.Id][2]);
        }
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
