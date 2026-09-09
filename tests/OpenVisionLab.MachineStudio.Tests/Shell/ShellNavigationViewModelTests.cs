using OpenVisionLab;
using OpenVisionLab.MachineStudio.ViewModel;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

[Collection(LayoutStartupTestCollection.Name)]
public sealed class ShellNavigationViewModelTests
{
    [Fact]
    public async Task StartupCommandsOwnStateAndDelegateProjectActions()
    {
        var blankStarted = 0;
        var sampleOpened = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var viewModel = new ShellNavigationViewModel(
            isStartupChoiceVisible: true,
            canStartBlankLayout: () => true,
            canOpenBundledSample: () => true,
            openBundledSampleAsync: () =>
            {
                sampleOpened.TrySetResult(true);
                return Task.CompletedTask;
            },
            onBlankLayoutStarted: () => blankStarted++,
            onCommandException: exception => throw exception);

        viewModel.StartBlankLayoutCommand.Execute(null);

        Assert.False(viewModel.IsStartupChoiceVisible);
        Assert.Equal(1, viewModel.SelectedLeftToolTabIndex);
        Assert.Equal(1, blankStarted);
        Assert.False(viewModel.StartBlankLayoutCommand.CanExecute(null));

        viewModel.HideStartupChoice();
        Assert.False(viewModel.OpenBundledSampleCommand.CanExecute(null));

        using var sampleViewModel = new ShellNavigationViewModel(
            isStartupChoiceVisible: true,
            canStartBlankLayout: () => false,
            canOpenBundledSample: () => true,
            openBundledSampleAsync: () =>
            {
                sampleOpened.TrySetResult(true);
                return Task.CompletedTask;
            },
            onBlankLayoutStarted: () => { },
            onCommandException: exception => throw exception);

        sampleViewModel.OpenBundledSampleCommand.Execute(null);

        await sampleOpened.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(sampleViewModel.IsStartupChoiceVisible);
    }

    [Fact]
    public void LanguageSelectionSynchronizesWithServiceAndRaisesOwnerEvent()
    {
        OpenVisionLanguageService.Load();
        var originalLanguage = OpenVisionLanguageService.CurrentLanguage;
        try
        {
            using var viewModel = new ShellNavigationViewModel(
                isStartupChoiceVisible: false,
                canStartBlankLayout: () => false,
                canOpenBundledSample: () => false,
                openBundledSampleAsync: () => Task.CompletedTask,
                onBlankLayoutStarted: () => { },
                onCommandException: exception => throw exception);
            var changeCount = 0;
            viewModel.LanguageChanged += (_, _) => changeCount++;
            var alternate = viewModel.LanguageOptions.Single(option =>
                option.Language != originalLanguage);

            viewModel.SelectedLanguageOption = alternate;

            Assert.Equal(alternate.Language, OpenVisionLanguageService.CurrentLanguage);
            Assert.Same(alternate, viewModel.SelectedLanguageOption);
            Assert.Equal(1, changeCount);
        }
        finally
        {
            OpenVisionLanguageService.SetLanguage(originalLanguage, save: false);
        }
    }

    [Fact]
    public void SelectionAndCompactStateRaiseOnlyNavigationProperties()
    {
        using var viewModel = new ShellNavigationViewModel(
            isStartupChoiceVisible: false,
            canStartBlankLayout: () => false,
            canOpenBundledSample: () => false,
            openBundledSampleAsync: () => Task.CompletedTask,
            onBlankLayoutStarted: () => { },
            onCommandException: exception => throw exception);
        var changed = new List<string>();
        viewModel.PropertyChanged += (_, args) => changed.Add(args.PropertyName ?? string.Empty);

        viewModel.SelectedDocumentTabIndex = 2;
        viewModel.SelectedLeftToolTabIndex = 1;
        viewModel.IsCompactLayout = true;

        Assert.Equal(
            [
                nameof(ShellNavigationViewModel.SelectedDocumentTabIndex),
                nameof(ShellNavigationViewModel.SelectedLeftToolTabIndex),
                nameof(ShellNavigationViewModel.IsCompactLayout)
            ],
            changed);
    }

    [Fact]
    public void DisposalStopsStartupCommandsAndLanguageSubscription()
    {
        OpenVisionLanguageService.Load();
        var originalLanguage = OpenVisionLanguageService.CurrentLanguage;
        try
        {
            using var viewModel = new ShellNavigationViewModel(
                isStartupChoiceVisible: true,
                canStartBlankLayout: () => true,
                canOpenBundledSample: () => true,
                openBundledSampleAsync: () => Task.CompletedTask,
                onBlankLayoutStarted: () => { },
                onCommandException: exception => throw exception);

            viewModel.Dispose();

            Assert.False(viewModel.StartBlankLayoutCommand.CanExecute(null));
            Assert.False(viewModel.OpenBundledSampleCommand.CanExecute(null));
            OpenVisionLanguageService.SetLanguage(
                originalLanguage == OpenVisionLanguage.Korean
                    ? OpenVisionLanguage.English
                    : OpenVisionLanguage.Korean,
                save: false);
        }
        finally
        {
            OpenVisionLanguageService.SetLanguage(originalLanguage, save: false);
        }
    }
}
