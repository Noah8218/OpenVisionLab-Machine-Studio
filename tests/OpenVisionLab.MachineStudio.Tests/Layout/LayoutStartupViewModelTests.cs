using System.Globalization;
using OpenVisionLab.Machine.Core.Axes;
using OpenVisionLab.Machine.Core.Devices;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Core.Layouts;
using OpenVisionLab.Machine.Simulation.Camera;
using OpenVisionLab.Machine.Sequence.Runtime;
using OpenVisionLab.Machine.Persistence.Projects;
using OpenVisionLab.MachineStudio.Model;
using OpenVisionLab.MachineStudio.View.Dialogs;
using OpenVisionLab.MachineStudio.ViewModel;
using OpenVisionLab.TestSupport;
using OpenVisionLab.Wpf.MessageDialogs;
using System.Windows.Threading;
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
    private static string LargeLayoutSamplePath => Path.Combine(
        AppContext.BaseDirectory,
        "Samples",
        "LargeLayoutExploration.ovmachine");
    private static string InspectionSamplePath => Path.Combine(
        AppContext.BaseDirectory,
        "Samples",
        "R19InspectionFlow.ovmachine");

    [Fact]
    public async Task PlacementDraftDecisionProtectsSelectionNavigationAndProjectTransition()
    {
        var first = new LayoutComponentDefinition
        {
            Id = "first", Name = "First", Kind = LayoutComponentKind.MachineFrame,
            Transform = new Transform2D { X = 10, Y = 20 }, Size = new Size2D { Width = 40, Height = 30 }
        };
        var second = new LayoutComponentDefinition
        {
            Id = "second", Name = "Second", Kind = LayoutComponentKind.MachineFrame,
            Transform = new Transform2D { X = 70, Y = 20 }, Size = new Size2D { Width = 40, Height = 30 }
        };
        var layout = new MachineLayoutDefinition { Id = "main", Name = "Main", Components = { first, second } };
        var project = new MachineProjectDocument { Layouts = { layout } };
        project.Simulation.ActiveLayoutId = layout.Id;
        using var viewModel = new MainViewModel(project);
        viewModel.Layout.Select(first.Id);
        var editor = Assert.IsType<LayoutComponentEditorViewModel>(viewModel.Layout.SelectedComponentEditor);
        editor.DraftXText = "35";
        var promptCount = 0;
        viewModel.PlacementDraftPrompt = () => { promptCount++; return PlacementDraftDecision.Cancel; };

        viewModel.Layout.Select(second.Id);
        Assert.Equal(first.Id, viewModel.Layout.SelectedItem?.Id);
        viewModel.Navigation.IsSimulationWorkspace = true;
        Assert.True(viewModel.Navigation.IsEquipmentWorkspace);
        viewModel.ProjectTree.SelectedNode = viewModel.ProjectTree.Roots.Single();
        Assert.Equal(first.Id, viewModel.ProjectTree.SelectedNode?.Id);
        viewModel.OpenEquipmentDriveTabCommand.Execute(null);
        Assert.False(viewModel.IsEquipmentDriveTabOpen);
        Assert.False(await viewModel.CreateNewProjectAsync());
        Assert.False(await viewModel.TryResolveUnsavedChangesAsync());
        Assert.Equal(10, first.Transform.X);
        Assert.True(editor.HasPendingPlacementDraft);
        Assert.Equal(6, promptCount);

        editor.DraftXText = "invalid";
        viewModel.PlacementDraftPrompt = () => PlacementDraftDecision.Apply;
        viewModel.Layout.Select(second.Id);
        Assert.Equal(first.Id, viewModel.Layout.SelectedItem?.Id);
        Assert.NotEmpty(editor.PlacementDraftError);
        Assert.Equal(10, first.Transform.X);

        editor.DraftXText = "35";
        viewModel.Layout.Select(second.Id);
        Assert.Equal(second.Id, viewModel.Layout.SelectedItem?.Id);
        Assert.Equal(35, first.Transform.X);

        viewModel.OpenEquipmentDriveTabCommand.Execute(null);
        Assert.True(viewModel.IsEquipmentDriveTabOpen);
        viewModel.OpenEquipmentPropertiesCommand.Execute(null);
        Assert.False(viewModel.IsEquipmentDriveTabOpen);

        var secondEditor = Assert.IsType<LayoutComponentEditorViewModel>(viewModel.Layout.SelectedComponentEditor);
        secondEditor.DraftName = "Discarded";
        viewModel.PlacementDraftPrompt = () => PlacementDraftDecision.Discard;
        viewModel.Navigation.IsSimulationWorkspace = true;
        Assert.True(viewModel.Navigation.IsSimulationWorkspace);
        Assert.Equal("Second", second.Name);
    }

    [Fact]
    public async Task AxisDriveDraftDecisionProtectsSelectionAndProjectTransition()
    {
        var axis = new VirtualAxisDefinition { Id = "axis-x", Name = "X", Kind = AxisKind.Linear, SoftLimitMin = -50, SoftLimitMax = 250, HomePosition = 0, MaxVelocity = 100 };
        var stage = new LayoutComponentDefinition
        {
            Id = "stage", Name = "Stage", Kind = LayoutComponentKind.LinearStage, BehaviorBindingId = axis.Id,
            Transform = new Transform2D(), Size = new Size2D { Width = 60, Height = 30 }
        };
        var frame = new LayoutComponentDefinition
        {
            Id = "frame", Name = "Frame", Kind = LayoutComponentKind.MachineFrame,
            Transform = new Transform2D { X = 100 }, Size = new Size2D { Width = 60, Height = 30 }
        };
        var layout = new MachineLayoutDefinition { Id = "main", Name = "Main", Components = { stage, frame } };
        var project = new MachineProjectDocument { Layouts = { layout }, Axes = { axis } };
        project.Simulation.ActiveLayoutId = layout.Id;
        using var viewModel = new MainViewModel(project);
        viewModel.Layout.Select(stage.Id);
        viewModel.OpenEquipmentDriveTabCommand.Execute(null);
        var editor = Assert.IsType<LayoutComponentEditorViewModel>(viewModel.Layout.SelectedComponentEditor);
        editor.DraftAxisSpeedText = "120";
        viewModel.PlacementDraftPrompt = () => PlacementDraftDecision.Cancel;

        viewModel.Layout.Select(frame.Id);
        Assert.Equal(stage.Id, viewModel.Layout.SelectedItem?.Id);
        viewModel.OpenEquipmentPropertiesCommand.Execute(null);
        Assert.True(viewModel.IsEquipmentDriveTabOpen);
        Assert.False(await viewModel.CreateNewProjectAsync());
        Assert.Equal(100, axis.MaxVelocity);

        viewModel.PlacementDraftPrompt = () => PlacementDraftDecision.Apply;
        viewModel.Layout.Select(frame.Id);
        Assert.Equal(frame.Id, viewModel.Layout.SelectedItem?.Id);
        Assert.Equal(120, axis.MaxVelocity);

        viewModel.Layout.Select(stage.Id);
        var newEditor = Assert.IsType<LayoutComponentEditorViewModel>(viewModel.Layout.SelectedComponentEditor);
        newEditor.DraftAxisSpeedText = "140";
        viewModel.PlacementDraftPrompt = () => PlacementDraftDecision.Discard;
        viewModel.OpenEquipmentPropertiesCommand.Execute(null);
        Assert.False(viewModel.IsEquipmentDriveTabOpen);
        Assert.Equal(120, axis.MaxVelocity);
    }

    [Fact]
    public void ApplyingBothInspectorDraftsDoesNotPartiallyChangePlacementWhenDriveIsInvalid()
    {
        var axis = new VirtualAxisDefinition { Id = "axis-x", Name = "X", Kind = AxisKind.Linear, SoftLimitMin = 0, SoftLimitMax = 100, HomePosition = 0, MaxVelocity = 50 };
        var stage = new LayoutComponentDefinition
        {
            Id = "stage", Name = "Stage", Kind = LayoutComponentKind.LinearStage, BehaviorBindingId = axis.Id,
            Transform = new Transform2D { X = 10 }, Size = new Size2D { Width = 60, Height = 30 }
        };
        var frame = new LayoutComponentDefinition
        {
            Id = "frame", Name = "Frame", Kind = LayoutComponentKind.MachineFrame,
            Transform = new Transform2D { X = 100 }, Size = new Size2D { Width = 60, Height = 30 }
        };
        var layout = new MachineLayoutDefinition { Id = "main", Name = "Main", Components = { stage, frame } };
        var project = new MachineProjectDocument { Layouts = { layout }, Axes = { axis } };
        project.Simulation.ActiveLayoutId = layout.Id;
        using var viewModel = new MainViewModel(project);
        viewModel.Layout.Select(stage.Id);
        var editor = Assert.IsType<LayoutComponentEditorViewModel>(viewModel.Layout.SelectedComponentEditor);
        editor.DraftXText = "25";
        editor.DraftAxisSpeedText = "-1";
        viewModel.PlacementDraftPrompt = () => PlacementDraftDecision.Apply;

        viewModel.Layout.Select(frame.Id);
        Assert.Equal(stage.Id, viewModel.Layout.SelectedItem?.Id);
        Assert.Equal(10, stage.Transform.X);
        Assert.Equal(50, axis.MaxVelocity);
        Assert.True(editor.HasPendingPlacementDraft);
        Assert.True(editor.HasPendingAxisDriveDraft);
        Assert.NotEmpty(editor.AxisDriveDraftError);

        editor.DraftAxisSpeedText = "75";
        viewModel.Layout.Select(frame.Id);
        Assert.Equal(frame.Id, viewModel.Layout.SelectedItem?.Id);
        Assert.Equal(25, stage.Transform.X);
        Assert.Equal(75, axis.MaxVelocity);
    }

    [Fact]
    public void InitialProjectRemainsCleanAfterMonitorInitialization()
    {
        var project = new ProjectDocumentStore().Load(File.ReadAllText(SamplePath));
        using var viewModel = new MainViewModel(project);

        Assert.False(viewModel.HasUnsavedChanges);
        Assert.DoesNotContain("*", viewModel.Title, StringComparison.Ordinal);
    }

    [Fact]
    public void DisposeClearsSelectedAxisTuningEditorBeforeRuntimeShutdown()
    {
        var project = new MachineProjectDocument { Name = "Dispose axis editor" };
        project.Axes.Add(new VirtualAxisDefinition
        {
            Id = "axis-x",
            Name = "X Axis",
            Unit = "mm",
            SoftLimitMin = 0,
            SoftLimitMax = 100,
            MaxVelocity = 50
        });
        using var viewModel = new MainViewModel(project);
        var axesNode = viewModel.ProjectTree.Roots
            .Single()
            .Children
            .Single(node => node.Kind == TreeNodeKind.Axes);
        viewModel.ProjectTree.SelectedNode = Assert.Single(axesNode.Children);

        Assert.NotNull(viewModel.AxisDriveTuningEditor);

        viewModel.Dispose();

        Assert.Null(viewModel.AxisDriveTuningEditor);
    }

    [Fact]
    public void ReplacedAxisTuningEditorCannotMutatePreviousAxis()
    {
        var firstAxis = new VirtualAxisDefinition
        {
            Id = "axis-first",
            Name = "First Axis",
            Unit = "mm",
            SoftLimitMin = 0,
            SoftLimitMax = 100,
            MaxVelocity = 50
        };
        var secondAxis = new VirtualAxisDefinition
        {
            Id = "axis-second",
            Name = "Second Axis",
            Unit = "mm",
            SoftLimitMin = 0,
            SoftLimitMax = 100,
            MaxVelocity = 60
        };
        var project = new MachineProjectDocument { Name = "Axis selection lifetime" };
        project.Axes.Add(firstAxis);
        project.Axes.Add(secondAxis);
        using var viewModel = new MainViewModel(project);

        var axesNode = viewModel.ProjectTree.Roots
            .Single()
            .Children
            .Single(node => node.Kind == TreeNodeKind.Axes);
        var firstNode = axesNode.Children.Single(node => node.Id == firstAxis.Id);
        var secondNode = axesNode.Children.Single(node => node.Id == secondAxis.Id);

        viewModel.ProjectTree.SelectedNode = firstNode;
        var replacedEditor = Assert.IsType<AxisDriveTuningEditorViewModel>(viewModel.AxisDriveTuningEditor);
        viewModel.ProjectTree.SelectedNode = secondNode;

        replacedEditor.MaxVelocity = 77;
        Assert.False(replacedEditor.ResetDriveDefaultsCommand.CanExecute(null));
        replacedEditor.ResetDriveDefaultsCommand.Execute(null);

        Assert.Equal(50, firstAxis.MaxVelocity);
        Assert.Equal(60, secondAxis.MaxVelocity);
        Assert.Equal(60, viewModel.AxisDriveTuningEditor?.MaxVelocity);
    }

    [Fact]
    public async Task ReplacingProjectPublishesTheAppliedRuntimeSnapshotWhilePaused()
    {
        await RunOnStaAsync(async () =>
        {
            var initialProject = new ProjectDocumentStore().Load(File.ReadAllText(SamplePath));
            var replacement = new MachineProjectDocument { Name = "Replacement" };
            var root = Path.Combine(
                "D:\\OpenVisionLab-TestData\\Machine",
                "pl-0370-project-runtime-snapshot",
                Guid.NewGuid().ToString("N"));
            var replacementPath = Path.Combine(root, "replacement.ovmachine");
            Directory.CreateDirectory(root);

            try
            {
                await new ProjectDocumentFileStore().SaveAsync(replacement, replacementPath);
                using var viewModel = new MainViewModel(initialProject, SamplePath);
                await WaitForAsync(() => viewModel.SceneSnapshots.Latest?.ProjectId == initialProject.Id);

                Assert.False(viewModel.IsRunning);
                Assert.True(await viewModel.OpenProjectAsync(replacementPath));

                Assert.Equal(replacement.Id, viewModel.SceneSnapshots.Latest?.ProjectId);
                Assert.Empty(viewModel.ConditionScenarioTargets);
            }
            finally
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
            return true;
        });
    }

    [Fact]
    public async Task ReplacingProjectThroughMainViewModelRetainsSessionProjectionPreference()
    {
        await RunOnStaAsync(async () =>
        {
            var initialProject = new ProjectDocumentStore().Load(File.ReadAllText(SamplePath));
            var replacement = new MachineProjectDocument { Name = "Projection replacement" };
            var root = CreateTestDirectory();
            var replacementPath = Path.Combine(root, "replacement.ovmachine");

            try
            {
                await new ProjectDocumentFileStore().SaveAsync(replacement, replacementPath);
                using var viewModel = new MainViewModel(initialProject, SamplePath);
                var layout = viewModel.Layout;
                await WaitForAsync(() => viewModel.SceneSnapshots.Latest?.ProjectId == initialProject.Id);

                viewModel.Layout.ShowTopViewCommand.Execute(null);
                Assert.False(viewModel.Layout.IsObliqueView);
                Assert.False(viewModel.HasUnsavedChanges);
                viewModel.UnsavedProjectPrompt = () => UnsavedProjectDecision.Discard;

                Assert.True(await viewModel.OpenProjectReplacingCurrentAsync(replacementPath)
                    .WaitAsync(TimeSpan.FromSeconds(10)));

                Assert.Same(layout, viewModel.Layout);
                var currentProject = Assert.IsType<MachineProjectDocument>(viewModel.ProjectTree.Roots.Single().Model);
                Assert.Equal(replacement.Id, currentProject.Id);
                Assert.False(viewModel.Layout.IsObliqueView);
                Assert.False(viewModel.HasUnsavedChanges);
            }
            finally
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }

            return true;
        });
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
    public async Task RecipeEntryWithoutBundledSampleRequiresAnExplicitNewRecipe()
    {
        await RunOnStaAsync(async () =>
        {
            using var viewModel = new MainViewModel();
            viewModel.UnsavedProjectPrompt = () => throw new InvalidOperationException("A clean entry must not prompt for unsaved changes.");
            Assert.True(viewModel.IsStartupChoiceVisible);
            Assert.False(viewModel.OpenBundledSampleCommand.CanExecute(null));
            Assert.False(viewModel.SaveProjectCommand.CanExecute(null));
            Assert.False(viewModel.SaveProjectAsCommand.CanExecute(null));
            Assert.False(viewModel.RunCommand.CanExecute(null));
            Assert.False(viewModel.StepCommand.CanExecute(null));
            Assert.False(viewModel.ResetCommand.CanExecute(null));

            viewModel.RunCommand.Execute(null);
            viewModel.StepCommand.Execute(null);
            Assert.False(viewModel.IsRunning);
            Assert.True(viewModel.IsStartupChoiceVisible);

            viewModel.NewProjectCommand.Execute(null);
            Assert.True(viewModel.IsNewProjectNameDialogOpen);
            viewModel.NewProjectNameDraft = "New recipe from entry";
            Assert.True(await viewModel.ConfirmNewProjectNameAsync().WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.False(viewModel.IsStartupChoiceVisible);
            Assert.False(viewModel.IsNewProjectNameDialogOpen);
            Assert.Equal("New recipe from entry", viewModel.ProjectTree.Roots.Single().DisplayName);
            Assert.True(viewModel.IsDesignMode);
            Assert.False(viewModel.IsRunning);
            Assert.False(viewModel.HasUnsavedChanges);
            Assert.True(viewModel.SaveProjectCommand.CanExecute(null));
            return true;
        });
    }

    [Fact]
    public async Task NewRecipeCanBeSavedAndReopenedInANewSessionWithoutRunning()
    {
        var directory = Path.Combine(
            TestStorage.RootPath,
            "r19-recipe-entry-save-reopen",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var recipePath = Path.Combine(directory, "untitled.ovmachine");
            await RunOnStaAsync(async () =>
            {
                string[] savedComponentIds;
                using (var creator = new MainViewModel())
                {
                    Assert.True(creator.IsStartupChoiceVisible);
                    Assert.True(await creator.CreateNewProjectAsync().WaitAsync(TimeSpan.FromSeconds(10)));
                    Assert.True(creator.IsDesignMode);
                    Assert.False(creator.IsRunning);
                    Assert.True(creator.TryAddLayoutComponent(LayoutComponentKind.MachineFrame));
                    Assert.True(creator.HasUnsavedChanges);

                    savedComponentIds = creator.Layout.Items
                        .Where(item => item.Component is not null)
                        .Select(item => item.Component!.Id)
                        .OrderBy(id => id, StringComparer.Ordinal)
                        .ToArray();
                    Assert.NotEmpty(savedComponentIds);

                    await creator.SaveProjectAsync(recipePath).WaitAsync(TimeSpan.FromSeconds(10));

                    Assert.True(File.Exists(recipePath));
                    Assert.Equal(Path.GetFullPath(recipePath), creator.CurrentProjectPath);
                    Assert.False(creator.HasUnsavedChanges);
                    Assert.False(creator.IsRunning);
                }

                using var reopened = new MainViewModel();
                Assert.True(reopened.IsStartupChoiceVisible);
                Assert.True(await reopened.OpenProjectReplacingCurrentAsync(recipePath).WaitAsync(TimeSpan.FromSeconds(10)));
                Assert.False(reopened.IsStartupChoiceVisible);
                Assert.Equal(Path.GetFullPath(recipePath), reopened.CurrentProjectPath);
                Assert.Equal(
                    savedComponentIds,
                    reopened.Layout.Items
                        .Where(item => item.Component is not null)
                        .Select(item => item.Component!.Id)
                        .OrderBy(id => id, StringComparer.Ordinal)
                        .ToArray());
                Assert.True(reopened.IsDesignMode);
                Assert.False(reopened.IsRunning);
                Assert.False(reopened.HasUnsavedChanges);
                return true;
            });
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task RecipeEntryOpenFailurePreservesEntryAndValidOpenRecoversWithoutRunning()
    {
        await RunOnStaAsync(async () =>
        {
            using var viewModel = new MainViewModel(startupSamplePath: SamplePath);
            var failures = 0;
            viewModel.ProjectOpenFailurePresenter = _ => failures++;
            viewModel.UnsavedProjectPrompt = () => throw new InvalidOperationException("Opening from a clean entry must not prompt for unsaved changes.");
            var originalTitle = viewModel.Title;
            var missingPath = Path.Combine("D:\\OpenVisionLab-TestData\\Machine", Guid.NewGuid().ToString("N"), "missing.ovmachine");

            Assert.False(await viewModel.OpenProjectReplacingCurrentAsync(missingPath));
            Assert.Equal(1, failures);
            Assert.True(viewModel.IsStartupChoiceVisible);
            Assert.Equal(originalTitle, viewModel.Title);
            Assert.False(viewModel.HasUnsavedChanges);

            Assert.True(await viewModel.OpenProjectReplacingCurrentAsync(SamplePath).WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.False(viewModel.IsStartupChoiceVisible);
            Assert.True(viewModel.HasAuthoredLayout);
            Assert.True(viewModel.IsDesignMode);
            Assert.False(viewModel.IsRunning);
            return true;
        });
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
        Assert.True(viewModel.IsDesignMode);
        Assert.False(viewModel.IsRunMode);
        Assert.False(viewModel.IsRunning);
        Assert.Equal(
            OpenVisionLanguageService.T("Scene.ExampleOpenedStoppedStatus"),
            viewModel.StatusMessage);
    }

    [Fact]
    public async Task InspectionExampleEntryOpensTheBundledStoppedRecipeOnce()
    {
        using var viewModel = new MainViewModel(startupSamplePath: InspectionSamplePath);
        Assert.True(viewModel.IsStartupChoiceVisible);
        Assert.True(viewModel.OpenBundledSampleCommand.CanExecute(null));

        viewModel.OpenBundledSampleCommand.Execute(null);
        viewModel.OpenBundledSampleCommand.Execute(null);

        await WaitForAsync(() => !viewModel.IsStartupChoiceVisible);
        await WaitForAsync(() => viewModel.SceneSnapshots.Latest?.Cameras.Count == 2);

        Assert.Contains("Same-workpiece 2D and Height Verdict Flow", viewModel.Title, StringComparison.Ordinal);
        Assert.Null(viewModel.CurrentProjectPath);
        Assert.False(viewModel.HasUnsavedChanges);
        Assert.False(viewModel.IsRunning);
        var cameraItems = viewModel.Layout.Items
            .Where(item => item.Kind == LayoutItemKind.Camera)
            .ToArray();
        Assert.Equal(2, cameraItems.Length);
        Assert.All(cameraItems, item => Assert.Equal("unit-inspection", item.Component?.UnitId));
        Assert.Equal(
            new[] { "device.camera-1", "device.height-verdict" },
            cameraItems.Select(item => item.BehaviorBindingId).OrderBy(id => id, StringComparer.Ordinal).ToArray());
        Assert.Contains(viewModel.Layout.LibraryItems, item => item.Kind == LayoutComponentKind.Camera);
        viewModel.Layout.Select("camera-top-2d");
        Assert.Equal(
            new[] { "device.camera-1" },
            viewModel.Layout.SelectedComponentEditor!.BehaviorBindingOptions
                .Select(option => option.Id)
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToArray());
        Assert.All(viewModel.SceneSnapshots.Latest!.Cameras, camera =>
        {
            Assert.Equal(VirtualCameraState.Idle, camera.State);
            Assert.Equal(0L, camera.AcquisitionOrdinal);
        });
        Assert.False(viewModel.OpenBundledSampleCommand.CanExecute(null));
    }

    [Fact]
    public async Task ClosingAnUneditedBundledSampleDoesNotAskToSave()
    {
        await RunOnStaAsync(async () =>
        {
            using var viewModel = new MainViewModel(startupSamplePath: InspectionSamplePath);
            var promptCount = 0;
            viewModel.UnsavedProjectPrompt = () =>
            {
                promptCount++;
                return UnsavedProjectDecision.Cancel;
            };

            viewModel.OpenBundledSampleCommand.Execute(null);
            await WaitForAsync(() => viewModel.SceneSnapshots.Latest?.Cameras.Count == 2);

            Assert.False(viewModel.HasUnsavedChanges);
            var closeResult = await viewModel.RequestCloseAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(0, promptCount);
            Assert.True(closeResult.IsApproved, $"Close outcome: {closeResult.Outcome}");
            return true;
        });
    }

    [Fact]
    public async Task CancellingCloseOfAnEditedBundledSampleKeepsChangesAndCanRetry()
    {
        await RunOnStaAsync(async () =>
        {
            using var viewModel = new MainViewModel(startupSamplePath: InspectionSamplePath);
            var promptCount = 0;
            var decision = UnsavedProjectDecision.Cancel;
            viewModel.UnsavedProjectPrompt = () =>
            {
                promptCount++;
                return decision;
            };

            viewModel.OpenBundledSampleCommand.Execute(null);
            await WaitForAsync(() => !viewModel.IsStartupChoiceVisible
                && viewModel.SceneSnapshots.Latest?.Cameras.Count == 2);
            viewModel.SimulationWorkspace.ScenarioSeed++;

            Assert.True(viewModel.HasUnsavedChanges);
            var cancelledClose = await viewModel.RequestCloseAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(SimulationSessionCloseOutcome.UnsavedChangesRejected, cancelledClose.Outcome);
            Assert.True(viewModel.HasUnsavedChanges);
            Assert.Equal(1, promptCount);

            decision = UnsavedProjectDecision.Discard;
            var retriedClose = await viewModel.RequestCloseAsync(TimeSpan.FromSeconds(10));

            Assert.True(retriedClose.IsApproved);
            Assert.Equal(2, promptCount);
            return true;
        });
    }

    [Fact]
    public async Task LargeLayoutSampleStartLoadsOnceAsAnEditableStoppedRecipe()
    {
        using var viewModel = new MainViewModel(largeLayoutSamplePath: LargeLayoutSamplePath);

        Assert.True(viewModel.OpenLargeLayoutSampleCommand.CanExecute(null));
        viewModel.OpenLargeLayoutSampleCommand.Execute(null);
        viewModel.OpenLargeLayoutSampleCommand.Execute(null);

        var timeout = DateTime.UtcNow.AddSeconds(10);
        while (viewModel.IsStartupChoiceVisible && DateTime.UtcNow < timeout)
        {
            await Task.Delay(20);
        }

        Assert.False(viewModel.IsStartupChoiceVisible);
        Assert.Equal(500, viewModel.Layout.Items.Count);
        Assert.False(viewModel.HasUnsavedChanges);
        Assert.True(viewModel.IsDesignMode);
        Assert.False(viewModel.IsRunMode);
        Assert.False(viewModel.IsRunning);
        Assert.False(viewModel.OpenLargeLayoutSampleCommand.CanExecute(null));
    }

    [Theory]
    [InlineData(PlaceholderInspectionDecision.Pass, PlaceholderInspectionDecision.Pass, true)]
    [InlineData(PlaceholderInspectionDecision.Fail, PlaceholderInspectionDecision.Pass, false)]
    [InlineData(PlaceholderInspectionDecision.Pass, PlaceholderInspectionDecision.Fail, false)]
    [InlineData(PlaceholderInspectionDecision.Fail, PlaceholderInspectionDecision.Fail, false)]
    public async Task InspectionSampleRunsBothMockVerdictsOnlyAfterExplicitRun(
        PlaceholderInspectionDecision topDecision,
        PlaceholderInspectionDecision heightDecision,
        bool expectedPass)
    {
        await RunOnStaAsync(async () =>
        {
            var store = new ProjectDocumentStore();
            var project = store.Load(File.ReadAllText(InspectionSamplePath));
            project.Devices.Single(device => device.Id == "device.camera-1")
                .Camera!.PlaceholderDecision = topDecision;
            project.Devices.Single(device => device.Id == "device.height-verdict")
                .Camera!.PlaceholderDecision = heightDecision;
            var roundTripRoot = Path.Combine(
                TestStorage.RootPath,
                "r19-inspection-file-roundtrip",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(roundTripRoot);
            var roundTripPath = Path.Combine(roundTripRoot, "inspection.ovmachine");
            var fileStore = new ProjectDocumentFileStore(store);
            try
            {
                await fileStore.SaveAsync(project, roundTripPath);
                project = await fileStore.LoadAsync(roundTripPath);
            }
            finally
            {
                Directory.Delete(roundTripRoot, recursive: true);
            }

            using var viewModel = new MainViewModel(project);
            await WaitForAsync(() => viewModel.SceneSnapshots.Latest?.Cameras.Count == 2);
            viewModel.SelectedCameraId = "device.height-verdict";

            Assert.Null(viewModel.CurrentProjectPath);
            Assert.False(viewModel.HasUnsavedChanges);
            Assert.False(viewModel.IsRunning);
            var workpieceBeforeRun = Assert.Single(
                viewModel.SceneSnapshots.Latest!.LayoutComponents,
                item => item.Id == "wafer-workpiece");
            Assert.True(workpieceBeforeRun.IsWorkpiecePresent);
            Assert.Null(workpieceBeforeRun.WorkpieceInstanceId);
            Assert.All(viewModel.SceneSnapshots.Latest!.Cameras, camera =>
            {
                Assert.Equal(VirtualCameraState.Idle, camera.State);
                Assert.Equal(0L, camera.AcquisitionOrdinal);
            });

            viewModel.IsRunMode = true;
            await WaitForAsync(() => viewModel.RunCommand.CanExecute(null));
            viewModel.RunCommand.Execute(null);
            viewModel.RunCommand.Execute(null);

            await WaitForAsync(() =>
            {
                var snapshot = viewModel.SceneSnapshots.Latest;
                return snapshot?.Cameras.Count == 2
                    && snapshot.Cameras.All(camera => camera.State == VirtualCameraState.FrameReady)
                    && snapshot.Signals.FirstOrDefault(signal => signal.Id == "do.cycle-done")?.Value == true;
            }, TimeSpan.FromSeconds(20));

            var finalSnapshot = viewModel.SceneSnapshots.Latest!;
            var ejectedWorkpiece = Assert.Single(
                finalSnapshot.LayoutComponents,
                item => item.Id == "wafer-workpiece");
            Assert.False(ejectedWorkpiece.IsWorkpiecePresent);
            Assert.Null(ejectedWorkpiece.WorkpieceInstanceId);
            Assert.Equal(topDecision, finalSnapshot.Cameras.Single(camera => camera.Id == "device.camera-1").Result!.Decision);
            Assert.Equal(heightDecision, finalSnapshot.Cameras.Single(camera => camera.Id == "device.height-verdict").Result!.Decision);
            Assert.All(finalSnapshot.Cameras, camera =>
                Assert.Equal("wafer-workpiece", camera.Result!.WorkpieceComponentId));
            var workpieceInstanceId = Assert.Single(finalSnapshot.Cameras
                .Select(camera => camera.Result!.WorkpieceInstanceId)
                .Distinct(StringComparer.Ordinal));
            Assert.False(string.IsNullOrWhiteSpace(workpieceInstanceId));
            Assert.All(finalSnapshot.Cameras, camera => Assert.Null(camera.Result!.InspectionEvidence));
            foreach (var (cameraId, decision) in new[]
            {
                ("device.camera-1", topDecision),
                ("device.height-verdict", heightDecision)
            })
            {
                viewModel.SelectedCameraId = cameraId;
                var expectedResult = OpenVisionLab.OpenVisionLanguageService.T(
                    decision == PlaceholderInspectionDecision.Pass ? "Shell.ResultPass" : "Shell.ResultFail");
                await WaitForAsync(() => viewModel.CurrentCameraResultText == expectedResult, TimeSpan.FromSeconds(10));
                Assert.Equal(expectedResult, viewModel.CurrentCameraResultText);
                Assert.Contains("wafer-workpiece", viewModel.CurrentCameraEvidenceDetailsText, StringComparison.Ordinal);
            }

            Assert.Equal(expectedPass, finalSnapshot.Signals.Single(signal => signal.Id == "do.inspection-pass").Value);
            Assert.Equal(!expectedPass, finalSnapshot.Signals.Single(signal => signal.Id == "do.inspection-fail").Value);
            Assert.All(finalSnapshot.Cameras, camera => Assert.Equal(1L, camera.AcquisitionOrdinal));
            Assert.Null(viewModel.CurrentProjectPath);
            Assert.False(viewModel.HasUnsavedChanges);
            Assert.False(File.Exists($"{InspectionSamplePath}.vision-result.json"));
            return true;
        });
    }

    [Fact]
    public void StationUnitEditorPreservesCancelAndInvalidInputThenAddsUnitAndScopesNewComponents()
    {
        var project = new MachineProjectDocument { Name = "Equipment setup" };
        using var viewModel = new MainViewModel(project);

        viewModel.OpenStationUnitEditorCommand.Execute(null);
        Assert.True(viewModel.IsStationUnitEditorOpen);
        Assert.Empty(project.Stations);

        viewModel.StationNameDraft = string.Empty;
        viewModel.UnitNameDraft = "  ";
        Assert.False(viewModel.AddStationUnitCommand.CanExecute(null));
        viewModel.AddStationUnitCommand.Execute(null);
        Assert.True(viewModel.IsStationUnitEditorOpen);
        Assert.Empty(project.Stations);
        Assert.False(viewModel.HasUnsavedChanges);

        viewModel.CancelStationUnitEditorCommand.Execute(null);
        Assert.False(viewModel.IsStationUnitEditorOpen);
        Assert.Empty(project.Stations);
        Assert.False(viewModel.HasUnsavedChanges);

        viewModel.OpenStationUnitEditorCommand.Execute(null);
        viewModel.StationNameDraft = "Inspection";
        viewModel.UnitNameDraft = "Top camera";
        Assert.True(viewModel.AddStationUnitCommand.CanExecute(null));
        viewModel.AddStationUnitCommand.Execute(null);

        var station = Assert.Single(project.Stations);
        var unit = Assert.Single(station.Units);
        Assert.Equal("Inspection", station.Name);
        Assert.Equal("Top camera", unit.Name);
        Assert.False(viewModel.IsStationUnitEditorOpen);
        Assert.True(viewModel.HasUnsavedChanges);
        Assert.Equal(TreeNodeKind.Unit, viewModel.ProjectTree.SelectedNode?.Kind);
        Assert.Equal(unit.Id, viewModel.ProjectTree.SelectedNode?.Id);

        viewModel.AddLayoutComponentCommand.Execute(LayoutComponentKind.MachineFrame);

        var component = Assert.Single(project.Layouts.SelectMany(layout => layout.Components));
        Assert.Equal(unit.Id, component.UnitId);
    }

    [Fact]
    public void SimulationEquipmentOutlineUsesExistingStationUnitEditorAndCancelKeepsProjectUnchanged()
    {
        var project = new MachineProjectDocument { Name = "Simulation equipment setup" };
        using var viewModel = new MainViewModel(project);

        viewModel.Navigation.IsSimulationWorkspace = true;

        Assert.Equal(3, viewModel.Navigation.SelectedLeftToolTabIndex);
        Assert.True(viewModel.Navigation.IsEquipmentOutlineVisible);
        Assert.True(viewModel.OpenStationUnitEditorCommand.CanExecute(null));
        viewModel.OpenStationUnitEditorCommand.Execute(null);
        Assert.True(viewModel.IsStationUnitEditorOpen);
        Assert.Empty(project.Stations);

        viewModel.CancelStationUnitEditorCommand.Execute(null);

        Assert.False(viewModel.IsStationUnitEditorOpen);
        Assert.Empty(project.Stations);
        Assert.False(viewModel.HasUnsavedChanges);
    }

    [Fact]
    public void StationUnitEditorAddsToTheSelectedExistingStation()
    {
        var station = new MachineStationDefinition
        {
            Id = "station-assembly",
            Name = "Assembly"
        };
        var project = new MachineProjectDocument
        {
            Name = "Existing station",
            Stations = [station]
        };
        using var viewModel = new MainViewModel(project);

        viewModel.OpenStationUnitEditorCommand.Execute(null);

        Assert.False(viewModel.IsCreatingNewStation);
        Assert.Equal(station.Id, viewModel.SelectedStationId);
        viewModel.UnitNameDraft = "Press";
        Assert.True(viewModel.AddStationUnitCommand.CanExecute(null));
        viewModel.AddStationUnitCommand.Execute(null);

        Assert.Same(station, Assert.Single(project.Stations));
        Assert.Equal("Press", Assert.Single(station.Units).Name);
        Assert.Equal(station.Units[0].Id, viewModel.ProjectTree.SelectedNode?.Id);
    }

    [Fact]
    public void AssignedComponentsRefuseUnitRemovalAcrossEveryLayoutWithoutChangingProjectState()
    {
        static LayoutComponentDefinition Component(string id, string unitId) => new()
        {
            Id = id,
            Name = id,
            Kind = LayoutComponentKind.MachineFrame,
            UnitId = unitId,
            Transform = new Transform2D(),
            Size = new Size2D { Width = 20, Height = 20 }
        };

        var unit = new MachineUnitDefinition { Id = "unit-camera", Name = "Camera" };
        var station = new MachineStationDefinition
        {
            Id = "station-inspection",
            Name = "Inspection",
            Units = [unit]
        };
        var project = new MachineProjectDocument
        {
            Name = "Assigned unit removal",
            Stations = [station],
            Layouts =
            [
                new MachineLayoutDefinition
                {
                    Id = "layout-main",
                    Name = "Main",
                    Components = { Component("camera-frame", unit.Id) }
                },
                new MachineLayoutDefinition
                {
                    Id = "layout-secondary",
                    Name = "Secondary",
                    Components = { Component("camera-light", unit.Id) }
                }
            ]
        };
        project.Simulation.ActiveLayoutId = "layout-main";
        using var viewModel = new MainViewModel(project);
        var store = new ProjectDocumentStore();
        var before = store.Serialize(project);
        var promptCount = 0;
        viewModel.EquipmentUnitRemovalPrompt = (_, _) =>
        {
            promptCount++;
            return true;
        };

        Assert.True(viewModel.RemoveEquipmentUnitCommand.CanExecute(unit.Id));
        viewModel.RemoveEquipmentUnitCommand.Execute(unit.Id);

        Assert.Same(unit, Assert.Single(station.Units));
        Assert.Equal(before, store.Serialize(project));
        Assert.Equal(0, promptCount);
        Assert.False(viewModel.HasUnsavedChanges);
        Assert.Contains(unit.Name, viewModel.StatusMessage, StringComparison.Ordinal);
        Assert.Contains("2", viewModel.StatusMessage, StringComparison.Ordinal);
        Assert.True(new MachineProjectLayoutValidator().Validate(project).IsValid);
    }

    [Fact]
    public void PendingInspectorDraftResolvesBeforeUnitAssignmentIsRechecked()
    {
        var unit = new MachineUnitDefinition { Id = "unit-camera", Name = "Camera" };
        var station = new MachineStationDefinition
        {
            Id = "station-inspection",
            Name = "Inspection",
            Units = [unit]
        };
        var component = new LayoutComponentDefinition
        {
            Id = "camera-frame",
            Name = "Camera frame",
            Kind = LayoutComponentKind.MachineFrame,
            UnitId = unit.Id,
            Transform = new Transform2D { X = 20 },
            Size = new Size2D { Width = 20, Height = 20 }
        };
        var layout = new MachineLayoutDefinition
        {
            Id = "layout-main",
            Name = "Main",
            Components = { component }
        };
        var project = new MachineProjectDocument
        {
            Name = "Pending draft before unit removal",
            Stations = [station],
            Layouts = [layout]
        };
        project.Simulation.ActiveLayoutId = layout.Id;
        using var viewModel = new MainViewModel(project);
        viewModel.ShowEquipmentUnitCommand.Execute(unit.Id);
        viewModel.Layout.Select(component.Id);
        var editor = Assert.IsType<LayoutComponentEditorViewModel>(viewModel.Layout.SelectedComponentEditor);
        editor.DraftXText = "42";
        viewModel.PlacementDraftPrompt = () => PlacementDraftDecision.Apply;
        var removalPromptCount = 0;
        viewModel.EquipmentUnitRemovalPrompt = (_, _) =>
        {
            removalPromptCount++;
            return true;
        };

        viewModel.RemoveEquipmentUnitCommand.Execute(unit.Id);

        Assert.Equal(42d, component.Transform.X);
        Assert.Equal(unit.Id, component.UnitId);
        Assert.Same(unit, Assert.Single(station.Units));
        Assert.Equal(0, removalPromptCount);
        Assert.Contains(unit.Name, viewModel.StatusMessage, StringComparison.Ordinal);
        Assert.True(new MachineProjectLayoutValidator().Validate(project).IsValid);
    }

    [Fact]
    public void EmptyUnitRemovalCancelPreservesScopeThenConfirmedRemovalKeepsOtherAssignments()
    {
        var target = new MachineUnitDefinition { Id = "unit-empty", Name = "Empty unit" };
        var survivor = new MachineUnitDefinition { Id = "unit-press", Name = "Press" };
        var station = new MachineStationDefinition
        {
            Id = "station-assembly",
            Name = "Assembly",
            Units = [target, survivor]
        };
        var component = new LayoutComponentDefinition
        {
            Id = "press-frame",
            Name = "Press frame",
            Kind = LayoutComponentKind.MachineFrame,
            UnitId = survivor.Id,
            Transform = new Transform2D(),
            Size = new Size2D { Width = 20, Height = 20 }
        };
        var project = new MachineProjectDocument
        {
            Name = "Empty unit removal",
            Stations = [station],
            Layouts = [new MachineLayoutDefinition { Id = "layout-main", Name = "Main", Components = { component } }]
        };
        project.Simulation.ActiveLayoutId = "layout-main";
        using var viewModel = new MainViewModel(project);
        viewModel.ShowEquipmentUnitCommand.Execute(target.Id);
        var store = new ProjectDocumentStore();
        var before = store.Serialize(project);
        var acceptRemoval = false;
        viewModel.EquipmentUnitRemovalPrompt = (_, _) => acceptRemoval;

        Assert.True(viewModel.RemoveEquipmentUnitCommand.CanExecute(target.Id));
        viewModel.RemoveEquipmentUnitCommand.Execute(target.Id);

        Assert.Equal(before, store.Serialize(project));
        Assert.False(viewModel.HasUnsavedChanges);
        Assert.Equal(target.Id, viewModel.Layout.ActiveUnitId);

        acceptRemoval = true;
        viewModel.RemoveEquipmentUnitCommand.Execute(target.Id);

        Assert.Same(survivor, Assert.Single(station.Units));
        Assert.Null(viewModel.Layout.ActiveUnitId);
        Assert.True(viewModel.HasUnsavedChanges);
        Assert.Equal(survivor.Id, component.UnitId);
        Assert.True(new MachineProjectLayoutValidator().Validate(project).IsValid);
    }

    [Fact]
    public void StationRemovalWaitsForChildUnitsAndCancelThenConfirmPreservesValidProject()
    {
        var unit = new MachineUnitDefinition { Id = "unit-inspection", Name = "Inspection" };
        var station = new MachineStationDefinition
        {
            Id = "station-main",
            Name = "Main station",
            Units = [unit]
        };
        var project = new MachineProjectDocument { Name = "Station removal", Stations = [station] };
        using var viewModel = new MainViewModel(project);
        var stationPromptCount = 0;
        viewModel.EquipmentStationRemovalPrompt = _ =>
        {
            stationPromptCount++;
            return true;
        };
        viewModel.OpenStationUnitEditorCommand.Execute(null);
        var before = new ProjectDocumentStore().Serialize(project);

        Assert.True(viewModel.HasSelectedStationChildren);
        Assert.Contains("1", viewModel.StationUnitRemovalHintText, StringComparison.Ordinal);
        Assert.False(viewModel.RemoveSelectedStationCommand.CanExecute(null));
        viewModel.RemoveSelectedStationCommand.Execute(null);
        Assert.Equal(before, new ProjectDocumentStore().Serialize(project));
        Assert.Equal(0, stationPromptCount);
        Assert.False(viewModel.HasUnsavedChanges);

        viewModel.CancelStationUnitEditorCommand.Execute(null);
        viewModel.EquipmentUnitRemovalPrompt = (_, _) => true;
        viewModel.RemoveEquipmentUnitCommand.Execute(unit.Id);
        Assert.Empty(station.Units);

        viewModel.OpenStationUnitEditorCommand.Execute(null);
        Assert.True(viewModel.RemoveSelectedStationCommand.CanExecute(null));
        var acceptStationRemoval = false;
        viewModel.EquipmentStationRemovalPrompt = _ =>
        {
            stationPromptCount++;
            return acceptStationRemoval;
        };
        var afterUnitRemoval = new ProjectDocumentStore().Serialize(project);

        viewModel.RemoveSelectedStationCommand.Execute(null);
        Assert.Equal(afterUnitRemoval, new ProjectDocumentStore().Serialize(project));
        Assert.Same(station, Assert.Single(project.Stations));
        Assert.True(viewModel.IsStationUnitEditorOpen);

        acceptStationRemoval = true;
        viewModel.RemoveSelectedStationCommand.Execute(null);

        Assert.Empty(project.Stations);
        Assert.True(viewModel.IsStationUnitEditorOpen);
        Assert.True(viewModel.IsCreatingNewStation);
        Assert.Null(viewModel.SelectedStationId);
        Assert.False(viewModel.HasSelectedStationChildren);
        Assert.True(viewModel.HasUnsavedChanges);
        Assert.True(new MachineProjectLayoutValidator().Validate(project).IsValid);
        Assert.Equal(2, stationPromptCount);
    }

    [Fact]
    public void EquipmentLibraryRequiresAUnitAndKeepsNewComponentsInTheActiveUnit()
    {
        var project = new MachineProjectDocument { Name = "Equipment target" };
        using var viewModel = new MainViewModel(project);
        var before = new ProjectDocumentStore().Serialize(project);

        viewModel.AddEquipmentLayoutComponentCommand.Execute(LayoutComponentKind.MachineFrame);

        Assert.True(viewModel.IsStationUnitEditorOpen);
        Assert.Empty(project.Layouts.SelectMany(layout => layout.Components));
        Assert.Equal(before, new ProjectDocumentStore().Serialize(project));
        Assert.False(viewModel.HasUnsavedChanges);

        viewModel.CancelStationUnitEditorCommand.Execute(null);
        viewModel.OpenStationUnitEditorCommand.Execute(null);
        viewModel.StationNameDraft = "Inspection";
        viewModel.UnitNameDraft = "Top camera";
        viewModel.AddStationUnitCommand.Execute(null);

        var unit = Assert.Single(Assert.Single(project.Stations).Units);
        viewModel.AddEquipmentLayoutComponentCommand.Execute(LayoutComponentKind.MachineFrame);
        var first = Assert.Single(project.Layouts.SelectMany(layout => layout.Components));
        Assert.Equal(unit.Id, first.UnitId);

        viewModel.Layout.Select(first.Id);
        Assert.Equal(first.Id, viewModel.ProjectTree.SelectedNode?.Id);
        Assert.Equal(unit.Id, viewModel.Layout.ActiveUnitId);

        viewModel.AddEquipmentLayoutComponentCommand.Execute(LayoutComponentKind.MachineFrame);

        Assert.Equal(2, project.Layouts.SelectMany(layout => layout.Components).Count());
        Assert.All(project.Layouts.SelectMany(layout => layout.Components), component => Assert.Equal(unit.Id, component.UnitId));
    }

    [Fact]
    public void EquipmentComponentLibraryDialogFallsBackToUnitSetupAndCancelPreservesProject()
    {
        var project = new MachineProjectDocument { Name = "Equipment component dialog" };
        using var viewModel = new MainViewModel(project);
        var store = new ProjectDocumentStore();
        var before = store.Serialize(project);

        viewModel.OpenEquipmentComponentLibraryDialogCommand.Execute(null);

        Assert.True(viewModel.IsStationUnitEditorOpen);
        Assert.False(viewModel.IsEquipmentComponentLibraryDialogOpen);
        Assert.Equal(before, store.Serialize(project));
        Assert.False(viewModel.HasUnsavedChanges);
        viewModel.CancelStationUnitEditorCommand.Execute(null);

        viewModel.OpenStationUnitEditorCommand.Execute(null);
        viewModel.StationNameDraft = "Inspection";
        viewModel.UnitNameDraft = "Top camera";
        viewModel.AddStationUnitCommand.Execute(null);
        var unit = Assert.Single(Assert.Single(project.Stations).Units);
        viewModel.ShowEquipmentUnitCommand.Execute(unit.Id);
        before = store.Serialize(project);

        viewModel.OpenEquipmentComponentLibraryDialogCommand.Execute(null);
        Assert.True(viewModel.IsEquipmentComponentLibraryDialogOpen);
        Assert.Equal(before, store.Serialize(project));
        Assert.True(viewModel.CloseEquipmentComponentLibraryDialogCommand.CanExecute(null));

        viewModel.CloseEquipmentComponentLibraryDialogCommand.Execute(null);

        Assert.False(viewModel.IsEquipmentComponentLibraryDialogOpen);
        Assert.Equal(before, store.Serialize(project));
        Assert.Equal(unit.Id, viewModel.Layout.ActiveUnitId);
        Assert.Equal(8, viewModel.Layout.LibraryItems.Count);
    }

    [Fact]
    public void EquipmentComponentLibraryDialogFiltersAndAddsOnlyTheSelectedVisibleKindOnce()
    {
        var project = new MachineProjectDocument { Name = "Equipment component selection" };
        using var viewModel = new MainViewModel(project);
        viewModel.OpenStationUnitEditorCommand.Execute(null);
        viewModel.StationNameDraft = "Inspection";
        viewModel.UnitNameDraft = "Top camera";
        viewModel.AddStationUnitCommand.Execute(null);
        var unit = Assert.Single(Assert.Single(project.Stations).Units);
        viewModel.ShowEquipmentUnitCommand.Execute(unit.Id);
        viewModel.OpenEquipmentComponentLibraryDialogCommand.Execute(null);
        viewModel.Layout.LibrarySearchText = "Camera";

        Assert.Single(viewModel.Layout.FilteredLibraryItems);
        Assert.True(viewModel.AddEquipmentComponentFromLibraryCommand.CanExecute(LayoutComponentKind.Camera));
        Assert.False(viewModel.AddEquipmentComponentFromLibraryCommand.CanExecute((LayoutComponentKind)int.MaxValue));
        var projectBeforeUnavailableCamera = new ProjectDocumentStore().Serialize(project);
        viewModel.AddEquipmentComponentFromLibraryCommand.Execute(LayoutComponentKind.Camera);
        Assert.True(viewModel.IsEquipmentComponentLibraryDialogOpen);
        Assert.Equal(OpenVisionLanguageService.T("Layout.Add.CameraDeviceRequired"), viewModel.EquipmentComponentLibraryDialogStatusText);
        Assert.Equal(projectBeforeUnavailableCamera, new ProjectDocumentStore().Serialize(project));

        viewModel.Layout.LibrarySearchText = "MachineFrame";
        Assert.Single(viewModel.Layout.FilteredLibraryItems);
        Assert.True(viewModel.AddEquipmentComponentFromLibraryCommand.CanExecute(LayoutComponentKind.MachineFrame));

        var componentCount = project.Layouts.Sum(layout => layout.Components.Count);
        viewModel.AddEquipmentComponentFromLibraryCommand.Execute(LayoutComponentKind.MachineFrame);
        viewModel.AddEquipmentComponentFromLibraryCommand.Execute(LayoutComponentKind.MachineFrame);

        var added = Assert.Single(project.Layouts.SelectMany(layout => layout.Components));
        Assert.Equal(componentCount + 1, project.Layouts.Sum(layout => layout.Components.Count));
        Assert.Equal(LayoutComponentKind.MachineFrame, added.Kind);
        Assert.Equal(unit.Id, added.UnitId);
        Assert.False(viewModel.IsEquipmentComponentLibraryDialogOpen);
        Assert.True(viewModel.HasUnsavedChanges);
    }

    [Fact]
    public void ComponentLibrarySearchMatchesR19AxisTypeIdInBothLanguages()
    {
        var originalLanguage = OpenVisionLanguageService.CurrentLanguage;
        try
        {
            foreach (var language in new[] { OpenVisionLanguage.Korean, OpenVisionLanguage.English })
            {
                OpenVisionLanguageService.SetLanguage(language, save: false);
                using var viewModel = new MainViewModel(new MachineProjectDocument { Name = "Axis search" });
                viewModel.Layout.LibrarySearchText = "axis";

                Assert.Contains(viewModel.Layout.FilteredLibraryItems, item =>
                    item.Kind == LayoutComponentKind.LinearStage);
            }
        }
        finally
        {
            OpenVisionLanguageService.SetLanguage(originalLanguage, save: false);
        }
    }

    [Fact]
    public void EquipmentComponentLibraryDialogLocalizesRejectedAddAndPreservesDialogForRetry()
    {
        var originalLanguage = OpenVisionLanguageService.CurrentLanguage;
        try
        {
            OpenVisionLanguageService.SetLanguage(OpenVisionLanguage.Korean, save: false);
            var project = new MachineProjectDocument { Name = "Equipment component rejection" };
            using var viewModel = new MainViewModel(project);
            viewModel.OpenStationUnitEditorCommand.Execute(null);
            viewModel.StationNameDraft = "Inspection";
            viewModel.UnitNameDraft = "Top camera";
            viewModel.AddStationUnitCommand.Execute(null);
            var unit = Assert.Single(Assert.Single(project.Stations).Units);
            viewModel.ShowEquipmentUnitCommand.Execute(unit.Id);
            viewModel.OpenEquipmentComponentLibraryDialogCommand.Execute(null);
            viewModel.Layout.LibrarySearchText = "Camera";

            var projectBeforeRejectedAdd = new ProjectDocumentStore().Serialize(project);
            viewModel.AddEquipmentComponentFromLibraryCommand.Execute(LayoutComponentKind.Camera);
            Assert.Equal(
                OpenVisionLanguageService.T("Layout.Add.CameraDeviceRequired"),
                viewModel.EquipmentComponentLibraryDialogStatusText);
            Assert.True(viewModel.IsEquipmentComponentLibraryDialogOpen);
            Assert.Equal(projectBeforeRejectedAdd, new ProjectDocumentStore().Serialize(project));

            OpenVisionLanguageService.SetLanguage(OpenVisionLanguage.English, save: false);
            viewModel.AddEquipmentComponentFromLibraryCommand.Execute(LayoutComponentKind.Camera);

            Assert.Equal(
                OpenVisionLanguageService.T("Layout.Add.CameraDeviceRequired"),
                viewModel.EquipmentComponentLibraryDialogStatusText);
            Assert.True(viewModel.IsEquipmentComponentLibraryDialogOpen);
            Assert.Equal(projectBeforeRejectedAdd, new ProjectDocumentStore().Serialize(project));

            viewModel.CloseEquipmentComponentLibraryDialogCommand.Execute(null);

            Assert.False(viewModel.IsEquipmentComponentLibraryDialogOpen);
            Assert.Equal(string.Empty, viewModel.EquipmentComponentLibraryDialogStatusText);
            viewModel.OpenEquipmentComponentLibraryDialogCommand.Execute(null);

            Assert.True(viewModel.IsEquipmentComponentLibraryDialogOpen);
            Assert.Equal(string.Empty, viewModel.Layout.LibrarySearchText);
            Assert.Equal(string.Empty, viewModel.EquipmentComponentLibraryDialogStatusText);
            Assert.Equal(projectBeforeRejectedAdd, new ProjectDocumentStore().Serialize(project));
        }
        finally
        {
            OpenVisionLanguageService.SetLanguage(originalLanguage, save: false);
        }
    }

    [Fact]
    public void StationAndUnitTreeSelectionScopesTheSceneAndRecovers()
    {
        static LayoutComponentDefinition Component(string id, string? unitId) => new()
        {
            Id = id,
            Name = id,
            Kind = LayoutComponentKind.MachineFrame,
            UnitId = unitId,
            Transform = new Transform2D(),
            Size = new Size2D { Width = 20, Height = 20 }
        };

        var project = new MachineProjectDocument
        {
            Name = "Station selection",
            Stations =
            [
                new MachineStationDefinition
                {
                    Id = "station-assembly",
                    Name = "Assembly",
                    Units =
                    [
                        new MachineUnitDefinition { Id = "unit-press", Name = "Press" },
                        new MachineUnitDefinition { Id = "unit-empty", Name = "Empty unit" }
                    ]
                },
                new MachineStationDefinition
                {
                    Id = "station-inspection",
                    Name = "Inspection",
                    Units = [new MachineUnitDefinition { Id = "unit-camera", Name = "Camera" }]
                }
            ],
            Layouts =
            [
                new MachineLayoutDefinition
                {
                    Id = "layout-main",
                    Name = "Main layout",
                    Components =
                    [
                        Component("press-frame", "unit-press"),
                        Component("press-stage", "unit-press"),
                        Component("camera-frame", "unit-camera"),
                        Component("unassigned-frame", null)
                    ]
                }
            ]
        };
        using var viewModel = new MainViewModel(project);
        var serializedBeforeSelection = new ProjectDocumentStore().Serialize(project);
        var projectRoot = Assert.Single(viewModel.ProjectTree.Roots);
        var stationsNode = projectRoot.Children.Single(node => node.Kind == TreeNodeKind.Stations);
        var assemblyNode = stationsNode.Children.Single(node => node.Id == "station-assembly");
        var pressNode = assemblyNode.Children.Single(node => node.Id == "unit-press");
        var emptyUnitNode = assemblyNode.Children.Single(node => node.Id == "unit-empty");
        var cameraNode = stationsNode.Children.Single(node => node.Id == "station-inspection")
            .Children.Single(node => node.Id == "unit-camera");
        var componentRoot = projectRoot.Children.Single(node => node.Kind == TreeNodeKind.Layouts)
            .Children.Single();
        var cameraFrameNode = componentRoot.Children.Single(node => node.Id == "camera-frame");
        var unassignedNode = componentRoot.Children.Single(node => node.Id == "unassigned-frame");

        Assert.Equal(4, viewModel.Layout.SceneItems.Count());

        viewModel.ProjectTree.SelectedNode = stationsNode;
        Assert.Equal(
            ["camera-frame", "press-frame", "press-stage"],
            viewModel.Layout.SelectedItems.Select(item => item.Id).Order(StringComparer.Ordinal));

        viewModel.ProjectTree.SelectedNode = assemblyNode;
        Assert.Equal(
            ["press-frame", "press-stage"],
            viewModel.Layout.SelectedItems.Select(item => item.Id).Order(StringComparer.Ordinal));

        viewModel.Navigation.IsInspectorOpen = true;
        viewModel.ProjectTree.SelectedNode = pressNode;
        Assert.Equal("unit-press", viewModel.Layout.ActiveUnitId);
        Assert.Equal("Press", viewModel.Layout.SceneTitleText);
        Assert.Equal(
            ["press-frame", "press-stage"],
            viewModel.Layout.SceneItems.Select(item => item.Id).Order(StringComparer.Ordinal));
        Assert.Empty(viewModel.Layout.SelectedItems);
        Assert.False(viewModel.Navigation.IsInspectorOpen);
        viewModel.ProjectTree.SelectedNode = pressNode;
        Assert.Equal(2, viewModel.Layout.SceneItems.Count());

        viewModel.ProjectTree.SelectedNode = cameraNode;
        Assert.Equal("unit-camera", viewModel.Layout.ActiveUnitId);
        Assert.Equal(["camera-frame"], viewModel.Layout.SceneItems.Select(item => item.Id));
        Assert.Empty(viewModel.Layout.SelectedItems);
        viewModel.ProjectTree.SelectedNode = emptyUnitNode;
        Assert.Equal("unit-empty", viewModel.Layout.ActiveUnitId);
        Assert.Equal("Empty unit", viewModel.Layout.SceneTitleText);
        Assert.Empty(viewModel.Layout.SceneItems);
        Assert.Empty(viewModel.Layout.SelectedItems);
        Assert.Null(viewModel.Layout.SelectedItem);

        viewModel.ProjectTree.SelectedNode = pressNode;
        viewModel.ProjectTree.SelectedNode = unassignedNode;
        Assert.Null(viewModel.Layout.ActiveUnitId);
        Assert.Equal(4, viewModel.Layout.SceneItems.Count());
        Assert.Equal("unassigned-frame", Assert.Single(viewModel.Layout.SelectedItems).Id);

        viewModel.Layout.Select("camera-frame");
        Assert.Same(cameraFrameNode, viewModel.ProjectTree.SelectedNode);
        Assert.Equal("unit-camera", viewModel.Layout.ActiveUnitId);

        viewModel.Layout.Select("unassigned-frame");
        Assert.Same(unassignedNode, viewModel.ProjectTree.SelectedNode);
        Assert.Null(viewModel.Layout.ActiveUnitId);

        viewModel.Layout.SelectedItem = null;
        Assert.Null(viewModel.ProjectTree.SelectedNode);
        Assert.Equal(serializedBeforeSelection, new ProjectDocumentStore().Serialize(project));
        Assert.False(viewModel.HasUnsavedChanges);
    }

    [Fact]
    public void EquipmentOverviewCommandClearsSelectionAndClosesInspectorWithoutChangingProject()
    {
        var unit = new MachineUnitDefinition { Id = "unit-press", Name = "Press" };
        var component = new LayoutComponentDefinition
        {
            Id = "press-frame",
            Name = "Press frame",
            Kind = LayoutComponentKind.MachineFrame,
            UnitId = unit.Id,
            Transform = new Transform2D(),
            Size = new Size2D { Width = 20, Height = 20 }
        };
        var project = new MachineProjectDocument
        {
            Name = "Equipment overview",
            Stations = [new MachineStationDefinition { Id = "station-press", Name = "Press", Units = [unit] }],
            Layouts =
            [
                new MachineLayoutDefinition
                {
                    Id = "layout-main",
                    Name = "Main layout",
                    Components = [component]
                }
            ]
        };
        using var viewModel = new MainViewModel(project);
        var unitNode = viewModel.ProjectTree.Roots.Single().Children
            .Single(node => node.Kind == TreeNodeKind.Stations).Children.Single().Children.Single();
        var serializedBeforeSelection = new ProjectDocumentStore().Serialize(project);
        viewModel.Navigation.IsInspectorOpen = true;
        unitNode.IsSelected = true;
        viewModel.ProjectTree.SelectedNode = unitNode;

        Assert.Empty(viewModel.Layout.SelectedItems);
        Assert.Equal(unit.Id, viewModel.Layout.ActiveUnitId);
        Assert.Equal([component.Id], viewModel.Layout.SceneItems.Select(item => item.Id));
        Assert.False(viewModel.Navigation.IsInspectorOpen);

        viewModel.Navigation.IsInspectorOpen = true;
        viewModel.ShowEquipmentUnitCommand.Execute(unit.Id);

        Assert.Equal(unit.Id, viewModel.Layout.ActiveUnitId);
        Assert.Equal([component.Id], viewModel.Layout.SceneItems.Select(item => item.Id));
        Assert.False(viewModel.Navigation.IsInspectorOpen);

        viewModel.ShowEquipmentOverviewCommand.Execute(null);
        viewModel.ShowEquipmentOverviewCommand.Execute(null);

        Assert.Null(viewModel.ProjectTree.SelectedNode);
        Assert.False(unitNode.IsSelected);
        Assert.Empty(viewModel.Layout.SelectedItems);
        Assert.Null(viewModel.Layout.SelectedItem);
        Assert.Null(viewModel.Layout.ActiveUnitId);
        Assert.Equal([component.Id], viewModel.Layout.SceneItems.Select(item => item.Id));
        Assert.False(viewModel.Navigation.IsInspectorOpen);
        Assert.Equal(serializedBeforeSelection, new ProjectDocumentStore().Serialize(project));
        Assert.False(viewModel.HasUnsavedChanges);
    }

    [Fact]
    public void EquipmentBulkMoveDialogMovesSelectionTogetherAndKeepsUndoHistory()
    {
        var first = new LayoutComponentDefinition
        {
            Id = "frame-a",
            Name = "Frame A",
            Kind = LayoutComponentKind.MachineFrame,
            Transform = new Transform2D { X = 10, Y = 20 },
            Size = new Size2D { Width = 20, Height = 20 }
        };
        var second = new LayoutComponentDefinition
        {
            Id = "frame-b",
            Name = "Frame B",
            Kind = LayoutComponentKind.MachineFrame,
            Transform = new Transform2D { X = 40, Y = 50 },
            Size = new Size2D { Width = 20, Height = 20 }
        };
        var project = new MachineProjectDocument
        {
            Name = "Bulk move",
            Layouts =
            [
                new MachineLayoutDefinition
                {
                    Id = "layout-main",
                    Name = "Main layout",
                    Components = [first, second]
                }
            ]
        };
        using var viewModel = new MainViewModel(project);
        viewModel.Layout.SelectMany([first.Id, second.Id], first.Id);

        Assert.Equal(2, viewModel.Layout.SelectionCount);
        Assert.Equal([first.Id, second.Id], viewModel.Layout.SelectedItems.Select(item => item.Id));
        Assert.Equal(first.Id, viewModel.Layout.SelectedItem?.Id);
        Assert.Equal(first.Id, viewModel.ProjectTree.SelectedNode?.Id);
        Assert.True(viewModel.OpenBulkMoveEditorCommand.CanExecute(null));
        viewModel.OpenBulkMoveEditorCommand.Execute(null);
        Assert.True(viewModel.IsBulkMoveEditorOpen);
        Assert.Equal(2, viewModel.Layout.SelectionCount);
        Assert.Equal([first.Id, second.Id], viewModel.Layout.SelectedItems.Select(item => item.Id));
        Assert.Contains("2", viewModel.BulkMoveTitleText, StringComparison.Ordinal);

        viewModel.BulkMoveXText = "invalid";
        Assert.False(viewModel.ApplyBulkMoveCommand.CanExecute(null));
        Assert.NotEmpty(viewModel.BulkMoveValidationText);
        viewModel.CancelBulkMoveCommand.Execute(null);
        Assert.False(viewModel.IsBulkMoveEditorOpen);
        Assert.Equal(10, first.Transform.X);
        Assert.Equal(20, first.Transform.Y);

        viewModel.OpenBulkMoveEditorCommand.Execute(null);
        Assert.Equal("0", viewModel.BulkMoveXText);
        Assert.Equal("0", viewModel.BulkMoveZText);

        viewModel.BulkMoveXText = 15.5.ToString(CultureInfo.CurrentCulture);
        viewModel.BulkMoveZText = (-4.25).ToString(CultureInfo.CurrentCulture);
        viewModel.ApplyBulkMoveCommand.Execute(null);

        Assert.False(viewModel.IsBulkMoveEditorOpen);
        Assert.Equal(25.5, first.Transform.X);
        Assert.Equal(15.75, first.Transform.Y);
        Assert.Equal(55.5, second.Transform.X);
        Assert.Equal(45.75, second.Transform.Y);
        Assert.True(viewModel.HasUnsavedChanges);
        Assert.True(viewModel.UndoLayoutEditCommand.CanExecute(null));

        viewModel.UndoLayoutEditCommand.Execute(null);

        var restored = Assert.IsType<MachineLayoutDefinition>(viewModel.Layout.Definition)
            .Components.ToDictionary(item => item.Id);
        Assert.Equal(10, restored[first.Id].Transform.X);
        Assert.Equal(20, restored[first.Id].Transform.Y);
        Assert.Equal(40, restored[second.Id].Transform.X);
        Assert.Equal(50, restored[second.Id].Transform.Y);
    }

    [Fact]
    public void EquipmentSinglePartCheckRequiresOneSelectionAndDoesNotChangeProject()
    {
        var project = new ProjectDocumentStore().Load(File.ReadAllText(SamplePath));
        using var viewModel = new MainViewModel(project);
        var serializedBefore = new ProjectDocumentStore().Serialize(project);
        var first = viewModel.Layout.Items[0];
        var second = viewModel.Layout.Items[1];

        Assert.False(viewModel.OpenEquipmentSinglePartCheckCommand.CanExecute(null));

        viewModel.Layout.Select(first.Id);

        Assert.True(viewModel.OpenEquipmentSinglePartCheckCommand.CanExecute(null));
        viewModel.OpenEquipmentSinglePartCheckCommand.Execute(null);
        Assert.True(viewModel.IsEquipmentSinglePartCheckOpen);
        Assert.True(viewModel.Navigation.IsInspectorOpen);

        viewModel.Navigation.IsInspectorOpen = false;

        Assert.False(viewModel.IsEquipmentSinglePartCheckOpen);
        viewModel.OpenEquipmentSinglePartCheckCommand.Execute(null);

        viewModel.Layout.SelectMany([first.Id, second.Id], first.Id);

        Assert.False(viewModel.IsEquipmentSinglePartCheckOpen);
        Assert.False(viewModel.OpenEquipmentSinglePartCheckCommand.CanExecute(null));

        viewModel.Layout.Select(first.Id);
        viewModel.OpenEquipmentSinglePartCheckCommand.Execute(null);
        viewModel.OpenEquipmentPropertiesCommand.Execute(null);

        Assert.False(viewModel.IsEquipmentSinglePartCheckOpen);
        Assert.True(viewModel.Navigation.IsInspectorOpen);
        Assert.Equal(serializedBefore, new ProjectDocumentStore().Serialize(project));
        Assert.False(viewModel.HasUnsavedChanges);
    }

    [Fact]
    public void EquipmentPaneTitleRemainsIdentifiableWhenNameIsInvalid()
    {
        var project = new ProjectDocumentStore().Load(File.ReadAllText(SamplePath));
        using var viewModel = new MainViewModel(project);
        viewModel.Layout.Select("stage-1");
        var editor = viewModel.Layout.SelectedComponentEditor;
        Assert.NotNull(editor);

        Assert.Equal(editor.Name, editor.PaneTitle);
        editor.Name = " ";
        Assert.Equal(editor.KindText, editor.PaneTitle);
        editor.Name = "Renamed stage";
        Assert.Equal("Renamed stage", editor.PaneTitle);
    }

    [Fact]
    public void AssigningSelectedComponentToExistingUnitEnablesUnitSceneScope()
    {
        var component = new LayoutComponentDefinition
        {
            Id = "camera-frame",
            Name = "Camera frame",
            Kind = LayoutComponentKind.MachineFrame,
            Transform = new Transform2D(),
            Size = new Size2D { Width = 20, Height = 20 }
        };
        var unit = new MachineUnitDefinition { Id = "unit-camera", Name = "Camera" };
        var project = new MachineProjectDocument
        {
            Name = "Unit assignment",
            Stations =
            [
                new MachineStationDefinition
                {
                    Id = "station-inspection",
                    Name = "Inspection",
                    Units = { unit }
                }
            ],
            Layouts =
            [
                new MachineLayoutDefinition
                {
                    Id = "layout-main",
                    Name = "Main layout",
                    Components = { component }
                }
            ]
        };
        using var viewModel = new MainViewModel(project);

        viewModel.Layout.Select(component.Id);
        var editor = Assert.IsType<LayoutComponentEditorViewModel>(viewModel.Layout.SelectedComponentEditor);
        Assert.Equal(string.Empty, editor.UnitId);
        Assert.Equal("Camera / unit-camera", Assert.Single(editor.UnitOptions.Where(option => option.Id == unit.Id)).DisplayName);
        Assert.DoesNotContain(viewModel.Layout.SelectedItems, item => item.Component?.UnitId == unit.Id);

        editor.UnitId = unit.Id;

        Assert.Equal(unit.Id, component.UnitId);
        Assert.True(viewModel.HasUnsavedChanges);
        var unitNode = viewModel.ProjectTree.Roots
            .SelectMany(root => root.Children)
            .SelectMany(group => group.Children)
            .SelectMany(station => station.Children)
            .Single(node => node.Id == unit.Id);
        viewModel.ProjectTree.SelectedNode = unitNode;

        Assert.Equal(unit.Id, viewModel.Layout.ActiveUnitId);
        Assert.Equal([component.Id], viewModel.Layout.SceneItems.Select(item => item.Id));
        Assert.Empty(viewModel.Layout.SelectedItems);
    }

    [Fact]
    public void LargeLayoutProjectTreeSearchFiltersAndRestoresWithoutChangingRecipeOrSelection()
    {
        var project = new ProjectDocumentStore().Load(File.ReadAllText(LargeLayoutSamplePath));
        using var viewModel = new MainViewModel(project);
        var serializedBeforeSearch = new ProjectDocumentStore().Serialize(project);
        var root = Assert.Single(viewModel.ProjectTree.Roots);
        var stations = root.Children.Single(node => node.Kind == TreeNodeKind.Stations);
        var assemblyStation = stations.Children.Single(node => node.Id == "station-assembly");
        var firstUnit = assemblyStation.Children.Single(node => node.Id == "unit-01");
        var secondUnit = assemblyStation.Children.Single(node => node.Id == "unit-02");
        var layouts = root.Children.Single(node => node.Kind == TreeNodeKind.Layouts);
        var layout = Assert.Single(layouts.Children);
        Assert.NotEmpty(layout.Children);
        var firstComponent = layout.Children.First();

        Assert.False(viewModel.ProjectTree.ClearSearchCommand.CanExecute(null));
        viewModel.ProjectTree.SelectedNode = firstUnit;
        assemblyStation.IsExpanded = false;
        viewModel.ProjectTree.SearchText = "uNiT 01";

        Assert.True(viewModel.ProjectTree.ClearSearchCommand.CanExecute(null));
        Assert.Equal(26, viewModel.ProjectTree.SearchResultCount);
        Assert.True(viewModel.ProjectTree.HasSearchMatches);
        Assert.False(viewModel.ProjectTree.HasNoSearchMatches);
        Assert.True(stations.IsVisible);
        Assert.True(assemblyStation.IsVisible);
        Assert.True(assemblyStation.IsExpanded);
        Assert.True(firstUnit.IsVisible);
        Assert.False(secondUnit.IsVisible);
        Assert.Equal(25, layout.Children.Count(node => node.IsVisible));
        Assert.Same(firstUnit, viewModel.ProjectTree.SelectedNode);

        viewModel.ProjectTree.SearchText = firstComponent.Id.ToUpperInvariant();

        Assert.Equal(1, viewModel.ProjectTree.SearchResultCount);
        Assert.True(layout.IsVisible);
        Assert.True(firstComponent.IsVisible);
        Assert.DoesNotContain(layout.Children.Skip(1), node => node.IsVisible);
        Assert.Same(firstUnit, viewModel.ProjectTree.SelectedNode);

        viewModel.ProjectTree.SearchText = "no-such-project-item";

        Assert.Equal(0, viewModel.ProjectTree.SearchResultCount);
        Assert.True(viewModel.ProjectTree.HasNoSearchMatches);
        Assert.False(viewModel.ProjectTree.HasSearchMatches);
        Assert.True(root.IsVisible);
        Assert.False(stations.IsVisible);
        Assert.False(layouts.IsVisible);
        Assert.Same(firstUnit, viewModel.ProjectTree.SelectedNode);

        viewModel.ProjectTree.ClearSearchCommand.Execute(null);
        viewModel.ProjectTree.ClearSearchCommand.Execute(null);

        Assert.False(viewModel.ProjectTree.IsSearchActive);
        Assert.False(viewModel.ProjectTree.ClearSearchCommand.CanExecute(null));
        Assert.False(viewModel.ProjectTree.HasNoSearchMatches);
        Assert.Equal(0, viewModel.ProjectTree.SearchResultCount);
        Assert.True(stations.IsVisible);
        Assert.True(assemblyStation.IsVisible);
        Assert.False(assemblyStation.IsExpanded);
        Assert.True(firstUnit.IsVisible);
        Assert.True(secondUnit.IsVisible);
        Assert.True(layout.IsVisible);
        Assert.True(layout.Children.All(node => node.IsVisible));
        Assert.Same(firstUnit, viewModel.ProjectTree.SelectedNode);
        Assert.Equal(serializedBeforeSearch, new ProjectDocumentStore().Serialize(project));
        Assert.False(viewModel.HasUnsavedChanges);
    }

    [Fact]
    public void SelectingLargeStationGroupKeepsAll500ComponentsInTheSelectionScope()
    {
        var project = new ProjectDocumentStore().Load(File.ReadAllText(LargeLayoutSamplePath));
        using var viewModel = new MainViewModel(project);
        var stationsNode = Assert.Single(viewModel.ProjectTree.Roots)
            .Children.Single(node => node.Kind == TreeNodeKind.Stations);
        var allUnitIds = project.Stations.SelectMany(station => station.Units)
            .Select(unit => unit.Id)
            .ToHashSet(StringComparer.Ordinal);
        var expectedIds = viewModel.Layout.Items
            .Where(item => item.UnitId is { } unitId && allUnitIds.Contains(unitId))
            .Select(item => item.Id)
            .Order(StringComparer.Ordinal)
            .ToArray();

        viewModel.ProjectTree.SelectedNode = stationsNode;

        Assert.Equal(500, viewModel.Layout.Items.Count);
        Assert.Equal(500, expectedIds.Length);
        Assert.Equal(expectedIds, viewModel.Layout.SelectedItems.Select(item => item.Id).Order(StringComparer.Ordinal));
        Assert.Equal(500, viewModel.Layout.SelectionCount);
        Assert.Null(viewModel.Layout.ActiveUnitId);
    }

    [Fact]
    public void ProjectTreeDoesNotInventStationGroupsForLegacyProjects()
    {
        using var viewModel = new MainViewModel(new MachineProjectDocument { Name = "Legacy project" });

        var projectRoot = Assert.Single(viewModel.ProjectTree.Roots);

        Assert.DoesNotContain(projectRoot.Children, node => node.Kind == TreeNodeKind.Stations);
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
                ["머신 프레임", "리니어 스테이지", "로터리 스테이지", "디지털 센서", "공압 실린더", "컨베이어", "워크피스", "카메라"],
                viewModel.Layout.LibraryItems.Select(item => item.Name));
            Assert.Equal("기구", viewModel.Layout.LibraryItems[0].Category);
            Assert.Equal("장비 셀의 고정 구조와 시각적 경계를 정의합니다.", viewModel.Layout.LibraryItems[0].Description);
            Assert.Equal("비전", viewModel.Layout.LibraryItems[^1].Category);
            Assert.Equal("레이아웃에서 가상 카메라의 위치를 표시합니다. 이미지를 생성하지 않습니다.", viewModel.Layout.LibraryItems[^1].Description);

            viewModel.SelectedLanguageOption = viewModel.LanguageOptions.Single(option =>
                option.Language == OpenVisionLanguage.English);

            Assert.Equal(
                ["Machine Frame", "Linear Stage", "Rotary Stage", "Digital Sensor", "Pneumatic Cylinder", "Conveyor", "Workpiece", "Camera"],
                viewModel.Layout.LibraryItems.Select(item => item.Name));
            Assert.Equal("Mechanics", viewModel.Layout.LibraryItems[0].Category);
            Assert.Equal("Static cell structure and visual boundary", viewModel.Layout.LibraryItems[0].Description);
            Assert.Equal("Vision", viewModel.Layout.LibraryItems[^1].Category);
            Assert.Equal("Shows a virtual camera position in the layout; does not generate images", viewModel.Layout.LibraryItems[^1].Description);
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
    public async Task LayoutDefinitionChangeNotifiesSelectedEquipmentStatus()
    {
        var project = new ProjectDocumentStore().Load(File.ReadAllText(SamplePath));
        using var viewModel = new MainViewModel(project);
        var selected = viewModel.Layout.Items.First(item => item.Component is not null);
        viewModel.Layout.Select(selected.Id);
        await Task.Delay(200);
        var notifications = 0;
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MainViewModel.SelectedEquipmentStatus))
            {
                notifications++;
            }
        };

        selected.CurrentName = $"{selected.Name} Renamed";

        Assert.Equal(selected.Name, viewModel.SelectedEquipmentStatus?.Name);
        Assert.Equal(1, notifications);
    }

    [Fact]
    public void RunModeKeepsProjectNavigationSelectionAndHeader()
    {
        var project = new ProjectDocumentStore().Load(File.ReadAllText(SamplePath));
        using var viewModel = new MainViewModel(project);
        var axesNode = viewModel.ProjectTree.Roots
            .SelectMany(root => root.Children)
            .Single(node => node.Kind == TreeNodeKind.Axes);
        var selectedNode = Assert.Single(axesNode.Children);
        viewModel.ProjectTree.SelectedNode = selectedNode;

        viewModel.IsRunMode = true;

        Assert.Same(selectedNode, viewModel.ProjectTree.SelectedNode);
        Assert.Equal(OpenVisionLanguageService.T("Shell.Project"), viewModel.LeftPanelHeaderText);
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
    [InlineData(false)]
    [InlineData(true)]
    public void WorkspaceNavigationDoesNotChangeModeProjectOrSelection(bool runMode)
    {
        var project = new ProjectDocumentStore().Load(File.ReadAllText(SamplePath));
        using var viewModel = new MainViewModel(project);
        viewModel.IsRunMode = runMode;
        var selected = viewModel.ProjectTree.SelectedNode;
        var layoutSelection = viewModel.Layout.SelectedItem;
        var dirty = viewModel.HasUnsavedChanges;
        var projectRoot = viewModel.ProjectTree.Roots.Single();
        foreach (var index in new[] { 1, 2, 3, 0, 3, 2, 1, 0 })
        {
            viewModel.Navigation.SelectedWorkspaceIndex = index;
            Assert.Equal(runMode, viewModel.IsRunMode);
            Assert.Equal(!runMode, viewModel.IsDesignMode);
            Assert.False(viewModel.IsRunning);
            Assert.Equal(dirty, viewModel.HasUnsavedChanges);
            Assert.Same(projectRoot, viewModel.ProjectTree.Roots.Single());
            Assert.Same(selected, viewModel.ProjectTree.SelectedNode);
            Assert.Same(layoutSelection, viewModel.Layout.SelectedItem);
        }
    }

    [Fact]
    public async Task PausedWorkspaceEntersEditingThroughResetAndPreservesSelection()
    {
        await RunOnStaAsync(async () =>
        {
            var project = new ProjectDocumentStore().Load(File.ReadAllText(SamplePath));
            using var viewModel = new MainViewModel(project);
            viewModel.Layout.SelectedItem = viewModel.Layout.Items.First();
            var selected = viewModel.Layout.SelectedItem;
            var layout = viewModel.Layout;
            viewModel.IsRunMode = true;
            viewModel.IsDesignMode = true;
            viewModel.IsDesignMode = true;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (viewModel.IsModeTransitioning)
            {
                await Task.Delay(10, timeout.Token);
            }
            Assert.True(viewModel.IsDesignMode);
            Assert.True(viewModel.IsSceneEditable);
            Assert.False(viewModel.IsRunning);
            Assert.Same(layout, viewModel.Layout);
            Assert.Same(selected, viewModel.Layout.SelectedItem);

            viewModel.IsRunMode = true;
            viewModel.UnsavedProjectPrompt = () => UnsavedProjectDecision.Discard;
            Assert.True(await viewModel.OpenProjectReplacingCurrentAsync(SamplePath).WaitAsync(timeout.Token));
            Assert.True(viewModel.IsDesignMode);
            Assert.False(viewModel.IsModeTransitioning);
            Assert.False(viewModel.IsRunning);
            return true;
        });
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
                    $"Project schema '2.0' is not supported. The latest supported schema is '{MachineProjectDocument.CurrentSchema}'.",
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
    public void LayoutRemovalDialogOptionsExplainImpactInBothLanguagesAndDefaultToCancel()
    {
        var component = new LayoutComponentDefinition { Id = "stage-1", Name = "Transfer Stage" };
        var impact = new LayoutComponentRemovalImpact(
            "cycle", "Cycle", "move", "Move stage", component.Id, LayoutComponentRemovalReferenceKind.Target);
        var originalLanguage = OpenVisionLanguageService.CurrentLanguage;
        try
        {
            OpenVisionLanguageService.SetLanguage(OpenVisionLanguage.Korean, save: false);
            var korean = MainMessageDialogHost.CreateLayoutRemovalDialogOptions([component], [impact]);
            Assert.Contains("1개", korean.Title, StringComparison.Ordinal);
            Assert.Contains("영향받는 동작 연결", korean.Message, StringComparison.Ordinal);
            Assert.Contains(component.Id, korean.Message, StringComparison.Ordinal);
            Assert.Equal("삭제", korean.PrimaryButtonText);
            Assert.Equal("취소", korean.SecondaryButtonText);
            Assert.Equal(WpfMessageDialogResult.No, korean.DefaultResult);

            OpenVisionLanguageService.SetLanguage(OpenVisionLanguage.English, save: false);
            var english = MainMessageDialogHost.CreateLayoutRemovalDialogOptions([component], [impact]);
            Assert.Equal("Delete 1 selected component?", english.Title);
            Assert.Contains("Affected sequence connections", english.Message, StringComparison.Ordinal);
            Assert.Contains(component.Id, english.Message, StringComparison.Ordinal);
            Assert.Equal("Delete", english.PrimaryButtonText);
            Assert.Equal("Cancel", english.SecondaryButtonText);
            Assert.Equal(WpfMessageDialogResult.No, english.DefaultResult);
        }
        finally
        {
            OpenVisionLanguageService.SetLanguage(originalLanguage, save: false);
        }
    }

    [Fact]
    public void EquipmentGroupRemovalPromptsNameTheTargetAndDefaultToCancelInBothLanguages()
    {
        var station = new MachineStationDefinition { Id = "station-inspection", Name = "Inspection" };
        var unit = new MachineUnitDefinition { Id = "unit-camera", Name = "Top camera" };
        var unitOptions = MainMessageDialogHost.CreateEquipmentUnitRemovalDialogOptions(station, unit);
        var stationOptions = MainMessageDialogHost.CreateEquipmentStationRemovalDialogOptions(station);

        Assert.Contains(unit.Name, unitOptions.Title, StringComparison.Ordinal);
        Assert.Contains(station.Name, unitOptions.Message, StringComparison.Ordinal);
        Assert.Equal(OpenVisionLanguageService.T("Shell.Delete", "삭제", "Delete"), unitOptions.PrimaryButtonText);
        Assert.Equal(OpenVisionLanguageService.T("Project.Cancel", "취소", "Cancel"), unitOptions.SecondaryButtonText);
        Assert.Equal(WpfMessageDialogResult.No, unitOptions.DefaultResult);
        Assert.Contains(station.Name, stationOptions.Title, StringComparison.Ordinal);
        Assert.Equal(WpfMessageDialogResult.No, stationOptions.DefaultResult);
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

    [Fact]
    public async Task WorkspaceNavigationLeavesRunningSimulationAndTickProgressIntact()
    {
        await RunOnStaAsync(async () =>
        {
            var project = new ProjectDocumentStore().Load(File.ReadAllText(SamplePath));
            using var viewModel = new MainViewModel(project);
            viewModel.IsRunMode = true;
            await WaitForAsync(() => viewModel.RunCommand.CanExecute(null));
            viewModel.RunCommand.Execute(null);
            await WaitForAsync(() => viewModel.IsRunning && viewModel.SceneSnapshots.Latest?.TickIndex > 0);
            var tick = viewModel.SceneSnapshots.Latest!.TickIndex;
            var selected = viewModel.Layout.SelectedItem;
            foreach (var index in new[] { 1, 3, 2, 0, 3, 1, 2 })
            {
                viewModel.Navigation.SelectedWorkspaceIndex = index;
                await Task.Delay(20);
                Assert.True(viewModel.IsRunning);
                Assert.True(viewModel.IsRunMode);
                Assert.False(viewModel.HasUnsavedChanges);
                Assert.Same(selected, viewModel.Layout.SelectedItem);
                Assert.True(viewModel.SceneSnapshots.Latest!.TickIndex >= tick);
                tick = viewModel.SceneSnapshots.Latest.TickIndex;
            }
            return true;
        });
    }

    private static async Task WaitForAsync(Func<bool> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        Assert.True(condition(), "The initial runtime configuration did not become observable.");
    }

    private static Task<T> RunOnStaAsync<T>(Func<Task<T>> action)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            _ = RunAsync();
            Dispatcher.Run();

            async Task RunAsync()
            {
                try
                {
                    completion.SetResult(await action());
                }
                catch (Exception exception)
                {
                    completion.SetException(exception);
                }
                finally
                {
                    dispatcher.BeginInvokeShutdown(DispatcherPriority.Normal);
                }
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
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
