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
        Assert.Equal(0, viewModel.SelectedLeftToolTabIndex);
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
                nameof(ShellNavigationViewModel.IsInspectorOpen),
                nameof(ShellNavigationViewModel.IsEquipmentInspectorOpen),
                nameof(ShellNavigationViewModel.IsDockedInspectorOpen),
                nameof(ShellNavigationViewModel.SelectedWorkspaceIndex),
                nameof(ShellNavigationViewModel.IsEquipmentWorkspace),
                nameof(ShellNavigationViewModel.IsEquipmentInspectorOpen),
                nameof(ShellNavigationViewModel.IsSequenceWorkspace),
                nameof(ShellNavigationViewModel.IsProcessWorkspace),
                nameof(ShellNavigationViewModel.IsSimulationOverviewWorkspace),
                nameof(ShellNavigationViewModel.IsDockedInspectorOpen),
                nameof(ShellNavigationViewModel.IsSimulationWorkspace),
                nameof(ShellNavigationViewModel.IsInspectionWorkspace),
                nameof(ShellNavigationViewModel.IsResultsWorkspace),
                nameof(ShellNavigationViewModel.IsEquipmentOutlineVisible),
                nameof(ShellNavigationViewModel.SelectedExecutionTabIndex),
                nameof(ShellNavigationViewModel.IsSequenceWorkspace),
                nameof(ShellNavigationViewModel.IsProcessWorkspace),
                nameof(ShellNavigationViewModel.IsSimulationOverviewWorkspace),
                nameof(ShellNavigationViewModel.IsDockedInspectorOpen),
                nameof(ShellNavigationViewModel.IsInspectorOpen),
                nameof(ShellNavigationViewModel.IsEquipmentInspectorOpen),
                nameof(ShellNavigationViewModel.IsDockedInspectorOpen),
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
        Assert.False(navigation.IsEquipmentOutlineVisible);
        Assert.Equal(1, navigation.SelectedDocumentTabIndex);
        navigation.IsResultsWorkspace = true;
        Assert.Equal(3, navigation.SelectedDocumentTabIndex);
        navigation.IsInspectionWorkspace = true;
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
        Assert.Equal(1, navigation.SelectedExecutionTabIndex);
        Assert.Equal(2, navigation.SelectedDocumentTabIndex);
        navigation.SelectedExecutionTabIndex = 3;
        Assert.Equal(1, navigation.SelectedExecutionTabIndex);
        navigation.IsEquipmentWorkspace = true;
        Assert.Equal(0, navigation.SelectedExecutionTabIndex);
        navigation.SelectedExecutionTabIndex = 0;
        navigation.IsResultsWorkspace = true;
        Assert.Equal(3, navigation.SelectedDocumentTabIndex);
        navigation.IsSimulationWorkspace = true;
        Assert.Equal(1, navigation.SelectedExecutionTabIndex);
        Assert.Equal(0, navigation.SelectedDocumentContentIndex);
    }

    [Fact]
    public void EvidencePanelRetainsTabAcrossCollapseAndWorkspaceChangesWithoutProjectActions()
    {
        using var navigation = new ShellNavigationViewModel(false, () => false, () => false,
            () => throw new InvalidOperationException("Navigation must not open a project."),
            () => throw new InvalidOperationException("Navigation must not create a layout."), exception => throw exception);
        navigation.IsSimulationWorkspace = true;
        navigation.SelectedExecutionTabIndex = 1;
        navigation.IsEvidenceExpanded = true;
        navigation.SelectedEvidenceTabIndex = 2;
        navigation.IsEvidenceExpanded = false;
        navigation.IsInspectionWorkspace = true;
        navigation.IsSimulationWorkspace = true;
        navigation.SelectedEvidenceTabIndex = -1;
        navigation.SelectedEvidenceTabIndex = 4;
        navigation.IsEvidenceExpanded = true;

        Assert.Equal(2, navigation.SelectedEvidenceTabIndex);
        Assert.Equal(1, navigation.SelectedExecutionTabIndex);
        Assert.True(navigation.IsEvidenceExpanded);
    }

    [Fact]
    public void EquipmentInspectorDockingRetainsTheDraftWhenClosingIsCancelled()
    {
        var allowTransition = false;
        using var navigation = new ShellNavigationViewModel(false, () => false, () => false,
            () => Task.CompletedTask, () => { }, exception => throw exception,
            resolvePendingPlacementDraft: () => allowTransition);
        var changed = new List<string?>();
        navigation.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        navigation.IsInspectorOpen = true;
        Assert.True(navigation.IsEquipmentInspectorOpen);
        changed.Clear();

        navigation.ToggleInspectorCommand.Execute(null);
        Assert.True(navigation.IsInspectorOpen);
        Assert.True(navigation.IsEquipmentInspectorOpen);
        Assert.DoesNotContain(nameof(navigation.IsEquipmentInspectorOpen), changed);
        navigation.IsSimulationWorkspace = true;
        Assert.True(navigation.IsEquipmentWorkspace);
        Assert.True(navigation.IsEquipmentInspectorOpen);

        allowTransition = true;
        navigation.ToggleInspectorCommand.Execute(null);
        Assert.False(navigation.IsEquipmentInspectorOpen);
        Assert.Contains(nameof(navigation.IsEquipmentInspectorOpen), changed);
        navigation.IsSimulationWorkspace = true;
        navigation.IsInspectorOpen = true;
        Assert.False(navigation.IsEquipmentInspectorOpen);
    }

    [Fact]
    public void InspectorAndEvidenceDrawerCommandsToggleAndResetWithWorkspaceNavigation()
    {
        using var navigation = new ShellNavigationViewModel(false, () => false, () => false,
            () => throw new InvalidOperationException("Navigation must not open a project."),
            () => throw new InvalidOperationException("Navigation must not create a layout."), exception => throw exception);

        Assert.True(navigation.IsInspectorOpen);
        Assert.True(navigation.IsEquipmentInspectorOpen);
        Assert.True(navigation.ToggleInspectorCommand.CanExecute(null));
        navigation.ToggleInspectorCommand.Execute(null);
        Assert.False(navigation.IsInspectorOpen);
        Assert.False(navigation.IsEquipmentInspectorOpen);
        navigation.ToggleInspectorCommand.Execute(null);
        Assert.True(navigation.IsInspectorOpen);

        navigation.ToggleEvidenceDrawerCommand.Execute(null);
        Assert.True(navigation.IsEvidenceExpanded);
        navigation.ToggleEvidenceDrawerCommand.Execute(null);
        Assert.False(navigation.IsEvidenceExpanded);

        navigation.ToggleInspectorCommand.Execute(null);
        navigation.IsSimulationWorkspace = true;
        Assert.False(navigation.IsInspectorOpen);
        Assert.False(navigation.IsEquipmentInspectorOpen);
        Assert.True(navigation.ToggleInspectorCommand.CanExecute(null));
        navigation.ToggleInspectorCommand.Execute(null);
        Assert.True(navigation.IsInspectorOpen);

        Assert.False(navigation.IsEquipmentInspectorOpen);

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
        Assert.True(navigation.IsInspectorOpen);
        Assert.True(navigation.ToggleInspectorCommand.CanExecute(null));
    }

    [Fact]
    public void EquipmentEvidenceCommandsRetainIndependentTabsAndIgnoreInvalidOrDisposedActions()
    {
        using var navigation = new ShellNavigationViewModel(false, () => false, () => false,
            () => throw new InvalidOperationException("Evidence must not open a project."),
            () => throw new InvalidOperationException("Evidence must not create a layout."), exception => throw exception);
        navigation.SelectedEvidenceTabIndex = 2;
        navigation.SelectEquipmentEvidenceTabCommand.Execute("1");
        Assert.True(navigation.IsEvidenceExpanded && navigation.IsEquipmentRequestsSelected);
        foreach (var invalid in new object?[] { null, "-1", "4", "request", "1.5" }) navigation.SelectEquipmentEvidenceTabCommand.Execute(invalid);
        Assert.Equal(1, navigation.SelectedEquipmentEvidenceTabIndex);
        Assert.True(navigation.IsEvidenceExpanded);
        navigation.SelectEquipmentEvidenceTabCommand.Execute("1");
        Assert.False(navigation.IsEvidenceExpanded || navigation.IsEquipmentRequestsSelected);
        navigation.ToggleEvidenceDrawerCommand.Execute(null);
        Assert.True(navigation.IsEquipmentRequestsSelected);
        navigation.SelectEquipmentEvidenceTabCommand.Execute("3");
        Assert.True(navigation.IsEquipmentAlarmsSelected);
        Assert.Equal(2, navigation.SelectedEvidenceTabIndex);
        navigation.Dispose();
        Assert.False(navigation.SelectEquipmentEvidenceTabCommand.CanExecute("0"));
        navigation.SelectEquipmentEvidenceTabCommand.Execute("0");
        Assert.Equal(3, navigation.SelectedEquipmentEvidenceTabIndex);
    }

    [Fact]
    public void ManualCommissioningReentryPreservesDraftCancellationAndSequenceNavigation()
    {
        var allowEntry = true;
        var allowTransition = true;
        using var navigation = new ShellNavigationViewModel(false, () => false, () => false,
            () => throw new InvalidOperationException("Navigation must not open a project."),
            () => throw new InvalidOperationException("Navigation must not create a layout."), exception => throw exception,
            resolvePendingPlacementDraft: () => allowTransition, canOpenManualCommissioning: () => allowEntry);
        navigation.OpenSequenceEditorCommand.Execute(null);
        navigation.IsEquipmentWorkspace = true;
        allowTransition = false;
        navigation.OpenManualCommissioningCommand.Execute(null);
        Assert.True(navigation.IsEquipmentWorkspace);
        Assert.True(navigation.IsInspectorOpen);
        allowTransition = true;
        navigation.OpenManualCommissioningCommand.Execute(null);
        Assert.True(navigation.IsSimulationWorkspace && navigation.IsInspectorOpen);
        Assert.Equal(0, navigation.SelectedExecutionTabIndex);
        navigation.OpenSequenceEditorCommand.Execute(null);
        navigation.IsInspectorOpen = false;
        allowTransition = false;
        navigation.OpenManualCommissioningCommand.Execute(null);
        Assert.True(navigation.IsSequenceWorkspace);
        Assert.False(navigation.IsInspectorOpen);
        allowTransition = true;
        navigation.OpenManualCommissioningCommand.Execute(null);
        navigation.OpenManualCommissioningCommand.Execute(null);
        Assert.True(navigation.IsSimulationWorkspace && navigation.IsInspectorOpen);
        Assert.Equal(0, navigation.SelectedExecutionTabIndex);
        allowEntry = false;
        navigation.IsInspectorOpen = false;
        Assert.False(navigation.OpenManualCommissioningCommand.CanExecute(null));
        navigation.OpenManualCommissioningCommand.Execute(null);
        Assert.False(navigation.IsInspectorOpen);
        navigation.Dispose();
        Assert.False(navigation.OpenManualCommissioningCommand.CanExecute(null));
        navigation.OpenManualCommissioningCommand.Execute(null);
        Assert.Equal(0, navigation.SelectedExecutionTabIndex);
    }

    [Fact]
    public void OpeningSequenceEditorReopensInspectorAfterWorkspaceReturnAndHonorsDraftCancellation()
    {
        var allowTransition = true;
        using var navigation = new ShellNavigationViewModel(false, () => false, () => false,
            () => throw new InvalidOperationException("Navigation must not open a project."),
            () => throw new InvalidOperationException("Navigation must not create a layout."), exception => throw exception,
            resolvePendingPlacementDraft: () => allowTransition);
        navigation.OpenSequenceEditorCommand.Execute(null);
        Assert.True(navigation.IsSequenceWorkspace && navigation.IsDockedInspectorOpen);
        navigation.IsEquipmentWorkspace = true;
        Assert.True(navigation.IsInspectorOpen);
        allowTransition = false;
        navigation.OpenSequenceEditorCommand.Execute(null);
        Assert.True(navigation.IsEquipmentWorkspace);
        Assert.True(navigation.IsInspectorOpen);
        allowTransition = true;
        navigation.OpenSequenceEditorCommand.Execute(null);
        Assert.True(navigation.IsSequenceWorkspace && navigation.IsDockedInspectorOpen);
        navigation.SelectedExecutionTabIndex = 0;
        navigation.IsInspectorOpen = false;
        allowTransition = false;
        navigation.OpenSequenceEditorCommand.Execute(null);
        Assert.Equal(0, navigation.SelectedExecutionTabIndex);
        Assert.False(navigation.IsDockedInspectorOpen);
        allowTransition = true;
        navigation.OpenSequenceEditorCommand.Execute(null);
        Assert.True(navigation.IsSequenceWorkspace && navigation.IsDockedInspectorOpen);
        navigation.IsInspectorOpen = false;
        navigation.OpenSequenceEditorCommand.Execute(null);
        Assert.True(navigation.IsDockedInspectorOpen);
        navigation.Dispose();
        Assert.False(navigation.OpenSequenceEditorCommand.CanExecute(null));
    }

    [Fact]
    public void RecipeConnectionsOverlayRetainsWorkspaceAndHonorsDraftAndModalAdmission()
    {
        var allowDraft = false;
        var admitted = true;
        var draftChecks = 0;
        using var navigation = new ShellNavigationViewModel(true, () => false, () => false,
            () => throw new InvalidOperationException("Must not open a project."),
            () => throw new InvalidOperationException("Must not create a layout."), exception => throw exception,
            resolvePendingPlacementDraft: () => { draftChecks++; return allowDraft; }, canOpenTestScenario: () => admitted);
        Assert.False(navigation.OpenRecipeConnectionsCommand.CanExecute(null));
        navigation.OpenRecipeConnectionsCommand.Execute(null);
        Assert.False(navigation.IsRecipeConnectionsOpen);
        Assert.Equal(0, draftChecks);
        navigation.HideStartupChoice();
        navigation.OpenRecipeConnectionsCommand.Execute(null);
        Assert.False(navigation.IsRecipeConnectionsOpen);
        Assert.Equal(1, draftChecks);
        allowDraft = true;
        navigation.OpenRecipeConnectionsCommand.Execute(null);
        Assert.True(navigation.IsRecipeConnectionsOpen && navigation.IsModalOverlayOpen);
        Assert.Equal(0, navigation.SelectedWorkspaceIndex);
        Assert.False(navigation.OpenRecipeConnectionsCommand.CanExecute(null));
        Assert.False(navigation.OpenTestScenarioCommand.CanExecute(null));
        navigation.OpenRecipeConnectionsCommand.Execute(null);
        Assert.Equal(2, draftChecks);
        navigation.CloseModalOverlayCommand.Execute(null);
        Assert.False(navigation.IsModalOverlayOpen);
        navigation.OpenTestScenarioCommand.Execute(null);
        Assert.False(navigation.OpenRecipeConnectionsCommand.CanExecute(null));
        navigation.OpenRecipeConnectionsCommand.Execute(null);
        Assert.False(navigation.IsRecipeConnectionsOpen);
        navigation.CloseModalOverlayCommand.Execute(null);
        admitted = false;
        navigation.OpenRecipeConnectionsCommand.Execute(null);
        Assert.False(navigation.IsRecipeConnectionsOpen);
        admitted = true;
        navigation.OpenRecipeConnectionsCommand.Execute(null);
        navigation.Dispose();
        Assert.False(navigation.IsRecipeConnectionsOpen || navigation.IsModalOverlayOpen);
        Assert.False(navigation.OpenRecipeConnectionsCommand.CanExecute(null));
        Assert.False(navigation.CloseModalOverlayCommand.CanExecute(null));
    }

    [Fact]
    public void TestScenarioOverlayHonorsAdmissionAndRetainsWorkspaceAndPendingDraft()
    {
        var admitted = true;
        var draftChecks = 0;
        using var navigation = new ShellNavigationViewModel(true, () => false, () => false,
            () => Task.CompletedTask, () => throw new InvalidOperationException("Must not create a layout."), exception => throw exception,
            resolvePendingPlacementDraft: () => { draftChecks++; return false; }, canOpenTestScenario: () => admitted);
        Assert.False(navigation.OpenTestScenarioCommand.CanExecute(null));
        navigation.OpenTestScenarioCommand.Execute(null);
        Assert.False(navigation.IsTestScenarioOpen);
        navigation.HideStartupChoice();
        admitted = false;
        navigation.OpenTestScenarioCommand.Execute(null);
        Assert.False(navigation.IsTestScenarioOpen);
        admitted = true;
        navigation.IsInspectorOpen = true;
        navigation.OpenTestScenarioCommand.Execute(null);
        Assert.True(navigation.IsTestScenarioOpen);
        Assert.False(navigation.OpenTestScenarioCommand.CanExecute(null));
        navigation.OpenTestScenarioCommand.Execute(null);
        Assert.Equal(0, navigation.SelectedWorkspaceIndex);
        Assert.True(navigation.IsInspectorOpen);
        Assert.Equal(0, draftChecks);
        navigation.CloseTestScenarioCommand.Execute(null);
        Assert.False(navigation.IsTestScenarioOpen);
        Assert.False(navigation.CloseTestScenarioCommand.CanExecute(null));
        navigation.OpenTestScenarioCommand.Execute(null);
        navigation.Dispose();
        Assert.False(navigation.IsTestScenarioOpen);
        Assert.False(navigation.OpenTestScenarioCommand.CanExecute(null));
        Assert.False(navigation.CloseTestScenarioCommand.CanExecute(null));
        Assert.Equal(0, draftChecks);
    }
}
