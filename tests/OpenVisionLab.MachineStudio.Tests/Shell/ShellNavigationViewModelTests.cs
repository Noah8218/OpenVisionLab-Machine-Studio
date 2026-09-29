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
    public void SelectionAndAdaptiveLayoutStateRaiseOnlyNavigationProperties()
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
        viewModel.IsNarrowLayout = true;

        Assert.Equal(
            [
                nameof(ShellNavigationViewModel.SelectedLeftToolTabIndex),
                nameof(ShellNavigationViewModel.IsEquipmentOutlineVisible),
                nameof(ShellNavigationViewModel.SelectedWorkspaceIndex),
                nameof(ShellNavigationViewModel.IsEquipmentWorkspace),
                nameof(ShellNavigationViewModel.IsSimulationWorkspace),
                nameof(ShellNavigationViewModel.IsInspectionWorkspace),
                nameof(ShellNavigationViewModel.IsResultsWorkspace),
                nameof(ShellNavigationViewModel.IsEquipmentOutlineVisible),
                nameof(ShellNavigationViewModel.SelectedExecutionTabIndex),
                nameof(ShellNavigationViewModel.SelectedDocumentTabIndex),
                nameof(ShellNavigationViewModel.SelectedDocumentContentIndex),
                nameof(ShellNavigationViewModel.SelectedLeftToolTabIndex),
                nameof(ShellNavigationViewModel.IsEquipmentOutlineVisible),
                nameof(ShellNavigationViewModel.IsCompactLayout),
                nameof(ShellNavigationViewModel.IsNarrowLayout)
            ],
            changed);
    }

    [Fact]
    public void InspectionSettingsExpandedIsSessionPresentationState()
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

        viewModel.IsInspectionSettingsExpanded = true;
        viewModel.IsInspectionSettingsExpanded = false;

        Assert.False(viewModel.IsInspectionSettingsExpanded);
        Assert.Equal(
            [nameof(ShellNavigationViewModel.IsInspectionSettingsExpanded), nameof(ShellNavigationViewModel.IsInspectionSettingsExpanded)],
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

    [Fact]
    public void R19WorkspacesRouteToExistingPagesAndPreserveSubworkspaceSelection()
    {
        using var navigation = new ShellNavigationViewModel(false, () => false, () => false,
            () => Task.CompletedTask, () => { }, exception => throw exception);

        navigation.IsSimulationWorkspace = true;
        navigation.SelectedExecutionTabIndex = 1;
        Assert.Equal(2, navigation.SelectedDocumentTabIndex);
        Assert.Equal(0, navigation.SelectedDocumentContentIndex);
        navigation.IsEquipmentWorkspace = true;
        Assert.Equal(0, navigation.SelectedExecutionTabIndex);
        navigation.IsSimulationWorkspace = true;
        Assert.Equal(1, navigation.SelectedExecutionTabIndex);
        Assert.Equal(2, navigation.SelectedDocumentTabIndex);

        navigation.IsInspectionWorkspace = true;
        navigation.SelectedInspectionTabIndex = 1;
        Assert.Equal(1, navigation.SelectedDocumentTabIndex);
        navigation.IsResultsWorkspace = true;
        Assert.Equal(3, navigation.SelectedDocumentTabIndex);
        navigation.IsInspectionWorkspace = true;
        Assert.Equal(1, navigation.SelectedInspectionTabIndex);
        Assert.Equal(1, navigation.SelectedDocumentTabIndex);

        navigation.SelectedDocumentTabIndex = 2;
        Assert.True(navigation.IsSimulationWorkspace);
        Assert.Equal(1, navigation.SelectedExecutionTabIndex);
        navigation.SelectedWorkspaceIndex = -1;
        navigation.SelectedDocumentTabIndex = 99;
        Assert.True(navigation.IsSimulationWorkspace);
        Assert.Equal(2, navigation.SelectedDocumentTabIndex);
        navigation.SelectedDocumentTabIndex = 0;
        Assert.True(navigation.IsEquipmentWorkspace);
        Assert.Equal(0, navigation.SelectedDocumentContentIndex);
    }

    [Fact]
    public void OpenComponentLibraryCommandSelectsLibraryWithoutChangingEquipmentWorkspace()
    {
        using var navigation = new ShellNavigationViewModel(false, () => false, () => false,
            () => Task.CompletedTask, () => { }, exception => throw exception);
        navigation.IsEquipmentWorkspace = true;
        navigation.SelectedLeftToolTabIndex = 0;

        navigation.OpenComponentLibraryCommand.Execute(null);

        Assert.Equal(1, navigation.SelectedLeftToolTabIndex);
        Assert.True(navigation.IsEquipmentWorkspace);
        navigation.Dispose();
        Assert.False(navigation.OpenComponentLibraryCommand.CanExecute(null));
    }

    [Fact]
    public void EquipmentOutlineAndSequenceActionsStayOnExistingNavigationOwners()
    {
        using var navigation = new ShellNavigationViewModel(false, () => false, () => false,
            () => Task.CompletedTask, () => { }, exception => throw exception);

        Assert.True(navigation.IsEquipmentOutlineVisible);
        navigation.IsSimulationWorkspace = true;

        Assert.Equal(3, navigation.SelectedLeftToolTabIndex);
        Assert.True(navigation.IsEquipmentOutlineVisible);
        navigation.SelectedLeftToolTabIndex = 1;
        Assert.False(navigation.IsEquipmentOutlineVisible);
        navigation.SelectedLeftToolTabIndex = 3;
        Assert.True(navigation.IsEquipmentOutlineVisible);
        navigation.SelectedLeftToolTabIndex = 2;
        Assert.False(navigation.IsEquipmentOutlineVisible);

        navigation.IsEquipmentWorkspace = true;

        Assert.Equal(0, navigation.SelectedLeftToolTabIndex);
        Assert.True(navigation.IsEquipmentOutlineVisible);
        navigation.OpenSequenceEditorCommand.Execute(null);
        Assert.True(navigation.IsSimulationWorkspace);
        Assert.Equal(1, navigation.SelectedExecutionTabIndex);
        Assert.Equal(2, navigation.SelectedDocumentTabIndex);

        navigation.Dispose();
        Assert.False(navigation.OpenSequenceEditorCommand.CanExecute(null));
    }

    [Fact]
    public void DisposalNotifiesStartupCommandsOfFinalAdmission()
    {
        using var viewModel = new ShellNavigationViewModel(
            isStartupChoiceVisible: true,
            canStartBlankLayout: () => true,
            canOpenBundledSample: () => true,
            openBundledSampleAsync: () => Task.CompletedTask,
            onBlankLayoutStarted: () => { },
            onCommandException: exception => throw exception);
        var startNotifications = 0;
        var openNotifications = 0;
        viewModel.StartBlankLayoutCommand.CanExecuteChanged += (_, _) => startNotifications++;
        viewModel.OpenBundledSampleCommand.CanExecuteChanged += (_, _) => openNotifications++;

        viewModel.Dispose();

        Assert.False(viewModel.StartBlankLayoutCommand.CanExecute(null));
        Assert.False(viewModel.OpenBundledSampleCommand.CanExecute(null));
        Assert.Equal(1, startNotifications);
        Assert.Equal(1, openNotifications);
    }

    [Fact]
    public void SimulationSubtabsRetainTheirSelectionAcrossWorkspaceChanges()
    {
        using var navigation = new ShellNavigationViewModel(false, () => false, () => false,
            () => Task.CompletedTask, () => { }, exception => throw exception);
        navigation.SelectedExecutionTabIndex = 1;
        Assert.Equal(0, navigation.SelectedExecutionTabIndex);
        navigation.IsSimulationWorkspace = true;
        navigation.SelectedExecutionTabIndex = 1;
        Assert.Equal(2, navigation.SelectedDocumentTabIndex);
        Assert.Equal(0, navigation.SelectedDocumentContentIndex);
        navigation.SelectedExecutionTabIndex = -1;
        navigation.SelectedExecutionTabIndex = 2;
        Assert.Equal(2, navigation.SelectedExecutionTabIndex);
        Assert.Equal(0, navigation.SelectedDocumentTabIndex);
        navigation.SelectedExecutionTabIndex = 3;
        Assert.Equal(2, navigation.SelectedExecutionTabIndex);
        navigation.IsEquipmentWorkspace = true;
        Assert.Equal(0, navigation.SelectedExecutionTabIndex);
        navigation.SelectedExecutionTabIndex = 0;
        navigation.IsResultsWorkspace = true;
        Assert.Equal(3, navigation.SelectedDocumentTabIndex);
        navigation.IsSimulationWorkspace = true;
        Assert.Equal(2, navigation.SelectedExecutionTabIndex);
        Assert.Equal(0, navigation.SelectedDocumentContentIndex);
    }

    [Fact]
    public void EvidencePanelRetainsTabAcrossCollapseAndWorkspaceChangesWithoutProjectActions()
    {
        using var navigation = new ShellNavigationViewModel(false, () => false, () => false,
            () => throw new InvalidOperationException("Navigation must not open a project."),
            () => throw new InvalidOperationException("Navigation must not create a layout."), exception => throw exception);
        navigation.IsSimulationWorkspace = true;
        navigation.SelectedExecutionTabIndex = 2;
        navigation.IsEvidenceExpanded = true;
        navigation.SelectedEvidenceTabIndex = 2;
        navigation.IsEvidenceExpanded = false;
        navigation.IsInspectionWorkspace = true;
        navigation.IsSimulationWorkspace = true;
        navigation.SelectedEvidenceTabIndex = -1;
        navigation.SelectedEvidenceTabIndex = 4;
        navigation.IsEvidenceExpanded = true;

        Assert.Equal(2, navigation.SelectedEvidenceTabIndex);
        Assert.Equal(2, navigation.SelectedExecutionTabIndex);
        Assert.True(navigation.IsEvidenceExpanded);
    }

    [Fact]
    public void InspectorAndEvidenceDrawerCommandsToggleAndResetWithWorkspaceNavigation()
    {
        using var navigation = new ShellNavigationViewModel(false, () => false, () => false,
            () => throw new InvalidOperationException("Navigation must not open a project."),
            () => throw new InvalidOperationException("Navigation must not create a layout."), exception => throw exception);

        Assert.False(navigation.IsInspectorOpen);
        Assert.True(navigation.ToggleInspectorCommand.CanExecute(null));
        navigation.ToggleInspectorCommand.Execute(null);
        Assert.True(navigation.IsInspectorOpen);
        navigation.ToggleInspectorCommand.Execute(null);
        Assert.False(navigation.IsInspectorOpen);

        navigation.ToggleEvidenceDrawerCommand.Execute(null);
        Assert.True(navigation.IsEvidenceExpanded);
        navigation.ToggleEvidenceDrawerCommand.Execute(null);
        Assert.False(navigation.IsEvidenceExpanded);

        navigation.ToggleInspectorCommand.Execute(null);
        navigation.IsSimulationWorkspace = true;
        Assert.False(navigation.IsInspectorOpen);
        Assert.True(navigation.ToggleInspectorCommand.CanExecute(null));
        navigation.ToggleInspectorCommand.Execute(null);
        Assert.True(navigation.IsInspectorOpen);

        navigation.IsInspectionWorkspace = true;
        Assert.False(navigation.IsInspectorOpen);
        Assert.True(navigation.ToggleInspectorCommand.CanExecute(null));
        navigation.ToggleInspectorCommand.Execute(null);
        Assert.True(navigation.IsInspectorOpen);

        navigation.IsResultsWorkspace = true;
        Assert.False(navigation.IsInspectorOpen);
        Assert.True(navigation.ToggleInspectorCommand.CanExecute(null));
        navigation.ToggleInspectorCommand.Execute(null);
        Assert.True(navigation.IsInspectorOpen);

        navigation.IsEquipmentWorkspace = true;
        Assert.False(navigation.IsInspectorOpen);
        Assert.True(navigation.ToggleInspectorCommand.CanExecute(null));
    }
}
