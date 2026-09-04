using OpenVisionLab.MachineStudio.ViewModel;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

[Collection(LayoutStartupTestCollection.Name)]
public sealed class SemiconductorRecipeGalleryViewModelTests
{
    [Fact]
    public async Task CompatibilityCommandsUseInjectedSelectorsAndPreserveCancellation()
    {
        var root = Path.Combine(
            "D:\\OpenVisionLab-TestData\\OpenVisionLab-Machine-Studio",
            "semiconductor-recipe-gallery-viewmodel-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var savePath = Path.Combine(root, "saved-report.json");
            var baselinePath = Path.Combine(root, "baseline-report.json");
            var currentPath = Path.Combine(root, "current-report.json");
            var saveSelectorCalls = 0;
            var baselineSelectorCalls = 0;
            var currentSelectorCalls = 0;
            var viewModel = new SemiconductorRecipeGalleryViewModel(
                (_, _) => Task.FromResult(false),
                () =>
                {
                    saveSelectorCalls++;
                    return savePath;
                },
                () =>
                {
                    baselineSelectorCalls++;
                    return baselinePath;
                },
                () =>
                {
                    currentSelectorCalls++;
                    return currentPath;
                });

            Assert.True(viewModel.HasItems);
            await viewModel.ValidateAllForSmokeAsync();
            Assert.True(viewModel.SaveCompatibilityReportCommand.CanExecute(null));

            viewModel.SaveCompatibilityReportCommand.Execute(null);

            Assert.Equal(1, saveSelectorCalls);
            Assert.True(File.Exists(savePath));
            File.Copy(savePath, baselinePath);
            File.Copy(savePath, currentPath);

            viewModel.CompareCompatibilityReportsCommand.Execute(null);

            Assert.Equal(1, baselineSelectorCalls);
            Assert.Equal(1, currentSelectorCalls);
            Assert.True(viewModel.IsComparisonOpen);
            Assert.Equal(viewModel.Items.Count, viewModel.ComparisonItems.Count);

            var cancelledViewModel = new SemiconductorRecipeGalleryViewModel(
                (_, _) => Task.FromResult(false),
                () => null,
                () => null,
                () => throw new InvalidOperationException("Current selector must not run after cancellation."));
            cancelledViewModel.CompareCompatibilityReportsCommand.Execute(null);

            Assert.False(cancelledViewModel.IsComparisonOpen);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
