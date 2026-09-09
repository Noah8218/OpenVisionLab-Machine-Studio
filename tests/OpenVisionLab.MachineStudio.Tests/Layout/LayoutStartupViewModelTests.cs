using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Core.Layouts;
using OpenVisionLab.MachineStudio.Model;
using OpenVisionLab.MachineStudio.View.Dialogs;
using OpenVisionLab.MachineStudio.ViewModel;
using OpenVisionLab.Wpf.MessageDialogs;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class LayoutStartupTestCollection
{
    public const string Name = "Layout startup";
}

[Collection(LayoutStartupTestCollection.Name)]
public sealed class LayoutStartupViewModelTests
{
    private static string SamplePath => Path.Combine(
        AppContext.BaseDirectory,
        "Samples",
        "AutomaticTransferCell.ovmachine");

    [Fact]
    public void InitialProjectRemainsCleanAfterMonitorInitialization()
    {
        var project = new ProjectDocumentStore().Load(File.ReadAllText(SamplePath));
        using var viewModel = new MainViewModel(project);

        Assert.False(viewModel.HasUnsavedChanges);
        Assert.DoesNotContain("*", viewModel.Title, StringComparison.Ordinal);
    }

    [Fact]
    public void BlankStartIsOneChoiceAndOpensTheLibrary()
    {
        using var viewModel = new MainViewModel(startupSamplePath: SamplePath);

        Assert.True(viewModel.IsStartupChoiceVisible);
        Assert.False(viewModel.HasUnsavedChanges);

        viewModel.StartBlankLayoutCommand.Execute(null);

        Assert.False(viewModel.IsStartupChoiceVisible);
        Assert.Equal(1, viewModel.SelectedLeftToolTabIndex);
        Assert.False(viewModel.HasUnsavedChanges);
    }

    [Fact]
    public async Task SampleStartLoadsAnEditableCleanTemplate()
    {
        using var viewModel = new MainViewModel(startupSamplePath: SamplePath);

        viewModel.OpenBundledSampleCommand.Execute(null);

        var timeout = DateTime.UtcNow.AddSeconds(5);
        while (viewModel.IsStartupChoiceVisible && DateTime.UtcNow < timeout)
        {
            await Task.Delay(20);
        }

        Assert.False(viewModel.IsStartupChoiceVisible);
        Assert.True(viewModel.HasAuthoredLayout);
        Assert.False(viewModel.HasUnsavedChanges);
        Assert.Contains("Automatic Transfer Cell", viewModel.Title, StringComparison.Ordinal);
        Assert.DoesNotContain("*", viewModel.Title, StringComparison.Ordinal);
    }

    [Fact]
    public void LibraryLocalizationRefreshPreservesAuthoredLayoutState()
    {
        var originalLanguage = OpenVisionLanguageService.CurrentLanguage;
        try
        {
            OpenVisionLanguageService.SetLanguage(OpenVisionLanguage.Korean, save: false);
            var project = new ProjectDocumentStore().Load(File.ReadAllText(SamplePath));
            using var viewModel = new MainViewModel(project);
            var selectedItem = viewModel.Layout.Items[0];
            viewModel.Layout.Select(selectedItem.Id);
            var definition = viewModel.Layout.Definition;

            Assert.Equal(
                ["머신 프레임", "리니어 스테이지", "로터리 스테이지", "디지털 센서", "공압 실린더", "컨베이어", "워크피스"],
                viewModel.Layout.LibraryItems.Select(item => item.Name));
            Assert.Equal("기구", viewModel.Layout.LibraryItems[0].Category);
            Assert.Equal("장비 셀의 고정 구조와 시각적 경계를 정의합니다.", viewModel.Layout.LibraryItems[0].Description);

            viewModel.SelectedLanguageOption = viewModel.LanguageOptions.Single(option =>
                option.Language == OpenVisionLanguage.English);

            Assert.Equal(
                ["Machine Frame", "Linear Stage", "Rotary Stage", "Digital Sensor", "Pneumatic Cylinder", "Conveyor", "Workpiece"],
                viewModel.Layout.LibraryItems.Select(item => item.Name));
            Assert.Equal("Mechanics", viewModel.Layout.LibraryItems[0].Category);
            Assert.Equal("Static cell structure and visual boundary", viewModel.Layout.LibraryItems[0].Description);
            Assert.Same(definition, viewModel.Layout.Definition);
            Assert.Same(selectedItem, viewModel.Layout.SelectedItem);
            Assert.True(selectedItem.IsSelected);
        }
        finally
        {
            OpenVisionLanguageService.SetLanguage(originalLanguage, save: false);
        }
    }

    [Fact]
    public void DuplicateSelectionCreatesOneUndoableOffsetCopy()
    {
        var project = new ProjectDocumentStore().Load(File.ReadAllText(SamplePath));
        using var viewModel = new MainViewModel(project);
        var originalCount = viewModel.Layout.Items.Count;
        var source = viewModel.Layout.Items.Single(item => item.Id == "stage-1");
        viewModel.Layout.Select(source.Id);

        Assert.True(viewModel.DuplicateLayoutSelectionCommand.CanExecute(null));
        viewModel.DuplicateLayoutSelectionCommand.Execute(null);

        Assert.Equal(originalCount + 1, viewModel.Layout.Items.Count);
        Assert.Equal(1, viewModel.Layout.SelectionCount);
        Assert.NotEqual(source.Id, viewModel.Layout.SelectedItem?.Id);
        Assert.NotEqual(source.CurrentX, viewModel.Layout.SelectedItem?.CurrentX);
        Assert.True(viewModel.UndoLayoutEditCommand.CanExecute(null));

        viewModel.UndoLayoutEditCommand.Execute(null);

        Assert.Equal(originalCount, viewModel.Layout.Items.Count);
        Assert.Equal(source.Id, viewModel.Layout.SelectedItem?.Id);
    }

    [Fact]
    public void ClickAddUsesNearestFreeGridPositionButExplicitDropCoordinatesWin()
    {
        var project = new ProjectDocumentStore().Load(File.ReadAllText(SamplePath));
        using var viewModel = new MainViewModel(project);

        Assert.True(viewModel.TryAddLayoutComponent(LayoutComponentKind.RotaryStage));
        var clickAdded = viewModel.Layout.SelectedItem;
        Assert.NotNull(clickAdded);
        Assert.Equal(LayoutComponentKind.RotaryStage, clickAdded.Component?.Kind);
        Assert.Equal(0, clickAdded.CurrentX % viewModel.Layout.GridSize);
        Assert.Equal(0, clickAdded.CurrentY % viewModel.Layout.GridSize);
        Assert.DoesNotContain(
            viewModel.Layout.Items.Where(item => item.Id != clickAdded.Id &&
                                                 item.Component?.Kind != LayoutComponentKind.MachineFrame),
            existing => existing.Component is not null &&
                        Overlaps(clickAdded.Component!, clickAdded.CurrentX, clickAdded.CurrentY,
                            existing.Component, existing.CurrentX, existing.CurrentY));

        Assert.True(viewModel.TryAddLayoutComponent(LayoutComponentKind.RotaryStage, 40, 180));
        var dropped = viewModel.Layout.SelectedItem;
        Assert.NotNull(dropped);
        Assert.Equal(40, dropped.CurrentX);
        Assert.Equal(180, dropped.CurrentY);
    }

    [Theory]
    [InlineData("malformed", "{\"schema\":\"1.12\",\"name\":")]
    [InlineData("future-schema", "{\"schema\":\"2.0\",\"name\":\"future\"}")]
    [InlineData("null-document", "null")]
    [InlineData("missing", null)]
    [InlineData("directory", null)]
    public async Task ExpectedOpenFailureReportsAndPreservesDirtyProject(
        string caseName,
        string? rejectedContent)
    {
        var originalLanguage = OpenVisionLanguageService.CurrentLanguage;
        var directory = CreateTestDirectory();
        try
        {
            OpenVisionLanguageService.SetLanguage(OpenVisionLanguage.English, save: false);
            var project = new ProjectDocumentStore().Load(File.ReadAllText(SamplePath));
            using var viewModel = new MainViewModel(project, initialProjectPath: SamplePath);
            await WaitForAsync(() => viewModel.SceneSnapshots.Latest?.Axes.Count > 0);
            Assert.True(viewModel.TryAddLayoutComponent(LayoutComponentKind.MachineFrame));

            var rejectedPath = Path.Combine(directory, $"{caseName}.ovmachine");
            if (caseName == "directory")
            {
                Directory.CreateDirectory(rejectedPath);
            }
            else if (rejectedContent is not null)
            {
                await File.WriteAllTextAsync(rejectedPath, rejectedContent);
            }
            var rejectedBytes = rejectedContent is null
                ? null
                : await File.ReadAllBytesAsync(rejectedPath);

            var title = viewModel.Title;
            var projectStatus = viewModel.ProjectStatusText;
            var currentProjectPath = viewModel.CurrentProjectPath;
            var projectModel = Assert.IsType<MachineProjectDocument>(
                viewModel.ProjectTree.Roots.Single().Model);
            var projectEvidence = new ProjectDocumentStore().SerializeForEvidence(projectModel);
            var layoutDefinition = viewModel.Layout.Definition;
            var selectedItem = viewModel.Layout.SelectedItem;
            var layoutCount = viewModel.LayoutComponentCountText;
            var snapshot = viewModel.SceneSnapshots.Latest;
            var designMode = viewModel.IsDesignMode;
            var running = viewModel.IsRunning;
            var promptCount = 0;
            var presentationCount = 0;
            string? presentedDetails = null;
            viewModel.UnsavedProjectPrompt = () =>
            {
                promptCount++;
                return UnsavedProjectDecision.Cancel;
            };
            viewModel.ProjectOpenFailurePresenter = details =>
            {
                presentationCount++;
                presentedDetails = details;
            };

            var opened = await viewModel.OpenProjectReplacingCurrentAsync(rejectedPath);

            Assert.False(opened);
            Assert.Equal(0, promptCount);
            Assert.Equal(1, presentationCount);
            Assert.NotNull(presentedDetails);
            var presentedOptions = MainMessageDialogHost.CreateProjectOpenFailureDialogOptions(presentedDetails!);
            Assert.Equal("Project open failed", presentedOptions.Title);
            Assert.StartsWith(
                "The project file could not be opened. The current project remains unchanged.",
                presentedOptions.Message,
                StringComparison.Ordinal);
            var expectedDetail = caseName switch
            {
                "malformed" or "null-document" =>
                    "The file content is not a valid Machine Studio project.",
                "future-schema" =>
                    "Project schema '2.0' is not supported. The latest supported schema is '1.12'.",
                "missing" => "The project file could not be found.",
                "directory" => "The project file cannot be read with the current permissions.",
                _ => throw new ArgumentOutOfRangeException(nameof(caseName))
            };
            Assert.Contains(expectedDetail, presentedOptions.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(
                "Unsupported machine project schema",
                presentedOptions.Message,
                StringComparison.Ordinal);
            Assert.Equal(WpfMessageDialogKind.Warning, presentedOptions.Kind);
            Assert.Equal(WpfMessageDialogResult.OK, presentedOptions.DefaultResult);
            Assert.Equal("OK", presentedOptions.PrimaryButtonText);
            Assert.Equal("The project could not be opened", viewModel.StatusMessage);
            Assert.Equal(title, viewModel.Title);
            Assert.Equal(projectStatus, viewModel.ProjectStatusText);
            Assert.Equal(currentProjectPath, viewModel.CurrentProjectPath);
            Assert.Same(projectModel, viewModel.ProjectTree.Roots.Single().Model);
            Assert.Equal(
                projectEvidence,
                new ProjectDocumentStore().SerializeForEvidence(projectModel));
            Assert.Same(layoutDefinition, viewModel.Layout.Definition);
            Assert.Same(selectedItem, viewModel.Layout.SelectedItem);
            Assert.Equal(layoutCount, viewModel.LayoutComponentCountText);
            Assert.Same(snapshot, viewModel.SceneSnapshots.Latest);
            Assert.Equal(designMode, viewModel.IsDesignMode);
            Assert.Equal(running, viewModel.IsRunning);
            Assert.True(viewModel.HasUnsavedChanges);
            Assert.EndsWith(" *", viewModel.Title, StringComparison.Ordinal);
            if (rejectedContent is not null)
            {
                Assert.NotNull(rejectedBytes);
                Assert.Equal(
                    rejectedBytes,
                    await File.ReadAllBytesAsync(rejectedPath));
            }
        }
        finally
        {
            OpenVisionLanguageService.SetLanguage(originalLanguage, save: false);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ProjectOpenFailureDialogOptionsAreLocalizedAndDefaultToAcknowledgement()
    {
        var originalLanguage = OpenVisionLanguageService.CurrentLanguage;
        try
        {
            OpenVisionLanguageService.SetLanguage(OpenVisionLanguage.Korean, save: false);
            var korean = MainMessageDialogHost.CreateProjectOpenFailureDialogOptions("상세 원인");
            Assert.Equal("프로젝트 열기 실패", korean.Title);
            Assert.Contains("현재 프로젝트는 그대로 유지됩니다.", korean.Message, StringComparison.Ordinal);
            Assert.Contains("상세 원인", korean.Message, StringComparison.Ordinal);
            Assert.Equal("확인", korean.PrimaryButtonText);

            OpenVisionLanguageService.SetLanguage(OpenVisionLanguage.English, save: false);
            var english = MainMessageDialogHost.CreateProjectOpenFailureDialogOptions("failure details");
            Assert.Equal("Project open failed", english.Title);
            Assert.Contains("The current project remains unchanged.", english.Message, StringComparison.Ordinal);
            Assert.Contains("failure details", english.Message, StringComparison.Ordinal);
            Assert.Equal("OK", english.PrimaryButtonText);
            Assert.Equal(WpfMessageDialogKind.Warning, english.Kind);
            Assert.Equal(WpfMessageDialogResult.OK, english.DefaultResult);
        }
        finally
        {
            OpenVisionLanguageService.SetLanguage(originalLanguage, save: false);
        }
    }

    [Fact]
    public void ProjectMessageDialogHostKeepsDecisionAndFailureDefaults()
    {
        var originalLanguage = OpenVisionLanguageService.CurrentLanguage;
        try
        {
            OpenVisionLanguageService.SetLanguage(OpenVisionLanguage.English, save: false);

            var unsaved = MainMessageDialogHost.CreateUnsavedProjectDialogOptions();
            Assert.Equal(WpfMessageDialogKind.Question, unsaved.Kind);
            Assert.Equal(WpfMessageDialogResult.Yes, unsaved.DefaultResult);
            Assert.Equal("Save", unsaved.PrimaryButtonText);
            Assert.Equal("Don't save", unsaved.SecondaryButtonText);
            Assert.Equal("Cancel", unsaved.TertiaryButtonText);

            var saveFailure = MainMessageDialogHost.CreateProjectSaveFailureDialogOptions("failure details");
            Assert.Equal(WpfMessageDialogKind.Warning, saveFailure.Kind);
            Assert.Equal(WpfMessageDialogResult.OK, saveFailure.DefaultResult);
            Assert.Contains("failure details", saveFailure.Message, StringComparison.Ordinal);
            Assert.Equal("OK", saveFailure.PrimaryButtonText);
        }
        finally
        {
            OpenVisionLanguageService.SetLanguage(originalLanguage, save: false);
        }
    }

    private static string CreateTestDirectory()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"ovl-project-open-failure-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var timeout = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < timeout)
        {
            await Task.Delay(20);
        }

        Assert.True(condition(), "The initial runtime configuration did not become observable.");
    }

    private static bool Overlaps(
        LayoutComponentDefinition first,
        double firstX,
        double firstY,
        LayoutComponentDefinition second,
        double secondX,
        double secondY)
    {
        return Math.Abs(firstX - secondX) <
                   HorizontalHalfExtent(first) + HorizontalHalfExtent(second) &&
               Math.Abs(firstY - secondY) <
                   VerticalHalfExtent(first) + VerticalHalfExtent(second);
    }

    private static double HorizontalHalfExtent(LayoutComponentDefinition component)
    {
        var radians = component.Transform.RotationDegrees * Math.PI / 180d;
        return (Math.Abs(Math.Cos(radians)) * component.Size.Width / 2d) +
               (Math.Abs(Math.Sin(radians)) * component.Size.Height / 2d);
    }

    private static double VerticalHalfExtent(LayoutComponentDefinition component)
    {
        var radians = component.Transform.RotationDegrees * Math.PI / 180d;
        return (Math.Abs(Math.Sin(radians)) * component.Size.Width / 2d) +
               (Math.Abs(Math.Cos(radians)) * component.Size.Height / 2d);
    }
}
