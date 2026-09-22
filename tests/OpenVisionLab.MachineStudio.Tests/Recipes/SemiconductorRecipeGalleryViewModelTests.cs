using OpenVisionLab.MachineStudio.ViewModel;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

[Collection(LayoutStartupTestCollection.Name)]
public sealed class SemiconductorRecipeGalleryViewModelTests
{
    [Fact]
    public async Task DisposeSuppressesLateCopyAndDisablesCommands()
    {
        var copyStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCopy = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var viewModel = new SemiconductorRecipeGalleryViewModel(
            (_, _) =>
            {
                copyStarted.SetResult(true);
                return releaseCopy.Task;
            },
            () => null,
            () => null,
            () => null);

        Assert.True(viewModel.HasItems);
        Task<bool> copyTask = viewModel.CreateCopyToAsync("ignored.ovmachine");
        await copyStarted.Task;
        Assert.True(viewModel.IsBusy);

        viewModel.Dispose();
        viewModel.Dispose();
        releaseCopy.SetResult(true);

        Assert.False(await copyTask);
        Assert.False(viewModel.IsBusy);
        Assert.False(viewModel.OpenCommand.CanExecute(null));
        Assert.False(viewModel.CloseCommand.CanExecute(null));
        Assert.False(viewModel.CreateCopyCommand.CanExecute(null));
        Assert.False(viewModel.ValidateAllCommand.CanExecute(null));
        Assert.False(viewModel.SaveCompatibilityReportCommand.CanExecute(null));
        Assert.False(viewModel.CompareCompatibilityReportsCommand.CanExecute(null));
        Assert.False(viewModel.CloseCompatibilityComparisonCommand.CanExecute(null));
    }

    [Fact]
    public void DisposeNotifiesCommandsOfFinalAdmission()
    {
        var viewModel = new SemiconductorRecipeGalleryViewModel(
            (_, _) => Task.FromResult(false),
            () => null,
            () => null,
            () => null);
        var commands = new[]
        {
            viewModel.OpenCommand,
            viewModel.CloseCommand,
            viewModel.CreateCopyCommand,
            viewModel.ValidateAllCommand,
            viewModel.SaveCompatibilityReportCommand,
            viewModel.CompareCompatibilityReportsCommand,
            viewModel.CloseCompatibilityComparisonCommand
        };
        var notifications = new int[commands.Length];
        for (var index = 0; index < commands.Length; index++)
        {
            var commandIndex = index;
            commands[commandIndex].CanExecuteChanged += (_, _) => notifications[commandIndex]++;
        }

        viewModel.Dispose();

        Assert.All(commands, command => Assert.False(command.CanExecute(null)));
        Assert.All(notifications, count => Assert.Equal(1, count));
    }

    [Fact]
    public async Task BusyGalleryRejectsOpenUntilCurrentOperationCompletes()
    {
        var copyStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCopy = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var viewModel = new SemiconductorRecipeGalleryViewModel(
            (_, _) =>
            {
                copyStarted.SetResult(true);
                return releaseCopy.Task;
            },
            () => null,
            () => null,
            () => null);
        var canExecuteChangedCount = 0;
        viewModel.OpenCommand.CanExecuteChanged += (_, _) => canExecuteChangedCount++;

        Task<bool> copyTask = viewModel.CreateCopyToAsync("ignored.ovmachine");
        await copyStarted.Task;

        Assert.True(viewModel.IsBusy);
        Assert.False(viewModel.OpenCommand.CanExecute(null));
        viewModel.OpenCommand.Execute(null);
        viewModel.Open();
        Assert.False(viewModel.IsOpen);
        Assert.True(canExecuteChangedCount > 0);

        releaseCopy.SetResult(false);
        Assert.False(await copyTask);
        Assert.False(viewModel.IsBusy);
        Assert.True(viewModel.OpenCommand.CanExecute(null));
    }

    [Fact]
    public async Task BusyGalleryDefersLocalizationReloadUntilCurrentOperationCompletes()
    {
        var copyStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCopy = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var viewModel = new SemiconductorRecipeGalleryViewModel(
            (_, _) =>
            {
                copyStarted.SetResult(true);
                return releaseCopy.Task;
            },
            () => null,
            () => null,
            () => null);

        viewModel.Open();
        var selectedItemBeforeRefresh = viewModel.SelectedItem;
        Assert.NotNull(selectedItemBeforeRefresh);

        Task<bool> copyTask = viewModel.CreateCopyToAsync("ignored.ovmachine");
        await copyStarted.Task;

        viewModel.RefreshLocalization();

        Assert.Same(selectedItemBeforeRefresh, viewModel.SelectedItem);
        Assert.Same(selectedItemBeforeRefresh, viewModel.Items[0]);

        releaseCopy.SetResult(false);
        Assert.False(await copyTask);
        Assert.False(viewModel.IsBusy);
        Assert.NotSame(selectedItemBeforeRefresh, viewModel.SelectedItem);
    }

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
