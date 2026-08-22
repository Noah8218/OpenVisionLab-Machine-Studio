using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Core.Layouts;
using OpenVisionLab.MachineStudio.ViewModel;
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
