using OpenVisionLab;
using OpenVisionLab.Machine.Core.Axes;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Simulation.Axis;
using OpenVisionLab.Machine.Simulation.Commissioning;
using OpenVisionLab.Machine.Simulation.Compilation;
using OpenVisionLab.Machine.Simulation.Engine;
using OpenVisionLab.MachineStudio.ViewModel;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

public sealed class MultiAxisCommissioningViewModelTests
{
    [Fact]
    public void ChangingTargetAxisRefreshesUnitDependentPresentation()
    {
        var axes = new[]
        {
            new VirtualAxisDefinition
            {
                Id = "linear",
                Name = "Linear",
                Unit = "mm",
                SoftLimitMin = 0,
                SoftLimitMax = 300
            },
            new VirtualAxisDefinition
            {
                Id = "rotary",
                Name = "Rotary",
                Unit = "deg",
                SoftLimitMin = 0,
                SoftLimitMax = 360
            }
        };
        var target = new MultiAxisCommissioningTargetDefinition
        {
            AxisId = "linear",
            TargetPosition = 10
        };
        var editor = new MultiAxisCommissioningTargetEditorViewModel(
            target,
            axes,
            () => { },
            _ => { },
            _ => { },
            _ => { });
        editor.ApplySnapshot(new AxisSnapshot("linear", "Linear", AxisState.Idle, 12.5, 0));
        var changedProperties = new List<string?>();
        editor.PropertyChanged += (_, args) => changedProperties.Add(args.PropertyName);

        editor.AxisId = "rotary";

        Assert.Equal("10.000 deg", editor.TargetText);
        Assert.Equal("12.500 deg", editor.CurrentPositionText);
        Assert.Contains(nameof(MultiAxisCommissioningTargetEditorViewModel.TargetText), changedProperties);
        Assert.Contains(nameof(MultiAxisCommissioningTargetEditorViewModel.CurrentPositionText), changedProperties);
    }

    [Fact]
    public void ValidationCommandUsesParentRuntimeGate()
    {
        OpenVisionLanguageService.Load();
        var project = CreateProject();
        var recipe = new MultiAxisCommissioningRecipeEditorViewModel(() => { });
        recipe.Load(project);
        var allowed = true;
        using var viewModel = CreateViewModel(project, recipe, () => allowed);

        Assert.True(viewModel.CanValidate);
        Assert.True(viewModel.ValidateCommand.CanExecute(null));

        allowed = false;
        viewModel.InvalidateCommands();

        Assert.False(viewModel.CanValidate);
        Assert.False(viewModel.ValidateCommand.CanExecute(null));
    }

    [Fact]
    public void DisposeDisablesCommandsAndNotifiesFinalAdmission()
    {
        OpenVisionLanguageService.Load();
        var project = CreateProject();
        var recipe = new MultiAxisCommissioningRecipeEditorViewModel(() => { });
        recipe.Load(project);
        using var viewModel = CreateViewModel(project, recipe, () => true);

        var validateNotifications = 0;
        var acceptNotifications = 0;
        var clearNotifications = 0;
        var navigateNotifications = 0;
        viewModel.ValidateCommand.CanExecuteChanged += (_, _) => validateNotifications++;
        viewModel.AcceptBaselineCommand.CanExecuteChanged += (_, _) => acceptNotifications++;
        viewModel.ClearBaselineCommand.CanExecuteChanged += (_, _) => clearNotifications++;
        viewModel.NavigateToMismatchCommand.CanExecuteChanged += (_, _) => navigateNotifications++;

        Assert.True(viewModel.ValidateCommand.CanExecute(null));

        viewModel.Dispose();

        Assert.False(viewModel.ValidateCommand.CanExecute(null));
        Assert.False(viewModel.AcceptBaselineCommand.CanExecute(null));
        Assert.False(viewModel.ClearBaselineCommand.CanExecute(null));
        Assert.False(viewModel.NavigateToMismatchCommand.CanExecute(null));
        Assert.Equal(1, validateNotifications);
        Assert.Equal(1, acceptNotifications);
        Assert.Equal(1, clearNotifications);
        Assert.Equal(1, navigateNotifications);
    }

    [Fact]
    public void DisposeRejectsDirectResetAndRuntimeNotifications()
    {
        OpenVisionLanguageService.Load();
        var project = CreateProject();
        var recipe = new MultiAxisCommissioningRecipeEditorViewModel(() => { });
        recipe.Load(project);
        var parentNotifications = 0;
        using var viewModel = CreateViewModel(
            project,
            recipe,
            () => true,
            _ => parentNotifications++);
        var propertyNotifications = 0;
        viewModel.PropertyChanged += (_, _) => propertyNotifications++;

        viewModel.Dispose();
        var parentNotificationsAfterDispose = parentNotifications;
        var propertyNotificationsAfterDispose = propertyNotifications;

        viewModel.Reset();
        viewModel.NotifyRuntimeChanged();

        Assert.Equal(parentNotificationsAfterDispose, parentNotifications);
        Assert.Equal(propertyNotificationsAfterDispose, propertyNotifications);
    }

    [Fact]
    public void DisposeClosesOwnedRecipeEditorCommands()
    {
        OpenVisionLanguageService.Load();
        var project = CreateProject();
        var recipe = new MultiAxisCommissioningRecipeEditorViewModel(() => { });
        recipe.Load(project);
        using var viewModel = CreateViewModel(project, recipe, () => true);
        var deleteNotifications = 0;
        recipe.DeleteRecipeCommand.CanExecuteChanged += (_, _) => deleteNotifications++;

        Assert.True(recipe.DeleteRecipeCommand.CanExecute(null));

        viewModel.Dispose();

        Assert.False(recipe.DeleteRecipeCommand.CanExecute(null));
        Assert.Equal(1, deleteNotifications);
        recipe.DeleteRecipeCommand.Execute(null);
        Assert.NotNull(project.MultiAxisCommissioningRecipe);
    }

    [Fact]
    public void ResetClearsOwnedEvidenceStateAndNotifiesParent()
    {
        OpenVisionLanguageService.Load();
        var project = CreateProject();
        var recipe = new MultiAxisCommissioningRecipeEditorViewModel(() => { });
        recipe.Load(project);
        var parentNotifications = 0;
        using var viewModel = CreateViewModel(
            project,
            recipe,
            () => true,
            _ => parentNotifications++);

        viewModel.Reset();

        Assert.Empty(viewModel.ResultHistoryEntries);
        Assert.Null(viewModel.LatestResult);
        Assert.Null(viewModel.AcceptedBaseline);
        Assert.Null(viewModel.BaselineComparison);
        Assert.False(viewModel.IsValidationRunning);
        Assert.True(parentNotifications > 0);
    }

    [Fact]
    public void RecipeChangeInvalidatesOnlyTheOwnedValidationContext()
    {
        OpenVisionLanguageService.Load();
        var project = CreateProject();
        var recipe = new MultiAxisCommissioningRecipeEditorViewModel(() => { });
        recipe.Load(project);
        using var viewModel = CreateViewModel(project, recipe, () => true);

        viewModel.NotifyRecipeChanged(invalidateCommands: false);

        Assert.False(viewModel.RejectedStaleResult);
        Assert.Null(viewModel.BaselineComparison);
        Assert.True(viewModel.CanValidate);
    }

    [Fact]
    public async Task ValidationCompletionUsesTheUiDispatchBoundary()
    {
        OpenVisionLanguageService.Load();
        var project = CreateProject();
        project.MultiAxisCommissioningRecipe!.ValidationRepetitions = 2;
        var recipe = new MultiAxisCommissioningRecipeEditorViewModel(() => { });
        recipe.Load(project);
        var dispatchScope = false;
        var statusCount = 0;
        var completionStatusWasDispatched = false;
        var completionLogWasDispatched = false;
        using var viewModel = new MultiAxisCommissioningViewModel(
            recipe,
            () => true,
            () => false,
            () => project,
            () => null,
            () => "{}",
            () => new SimulationRuntimeConfiguration(
                [
                    new AxisConfiguration
                    {
                        Id = "x",
                        Name = "X",
                        MinimumPosition = 0,
                        MaximumPosition = 300,
                        HomePosition = 0,
                        MaximumVelocity = 100,
                        Acceleration = 100,
                        Deceleration = 100,
                        FollowingErrorLimit = 10
                    },
                    new AxisConfiguration
                    {
                        Id = "y",
                        Name = "Y",
                        MinimumPosition = 0,
                        MaximumPosition = 300,
                        HomePosition = 0,
                        MaximumVelocity = 100,
                        Acceleration = 100,
                        Deceleration = 100,
                        FollowingErrorLimit = 10
                    }
                ],
                [],
                []),
            TimeSpan.FromMilliseconds(5),
            action =>
            {
                var previousScope = dispatchScope;
                dispatchScope = true;
                try
                {
                    action();
                }
                finally
                {
                    dispatchScope = previousScope;
                }

                return Task.CompletedTask;
            },
            _ =>
            {
                statusCount++;
                if (statusCount >= 2)
                {
                    completionStatusWasDispatched |= dispatchScope;
                }
            },
            _ =>
            {
                if (statusCount >= 2)
                {
                    completionLogWasDispatched |= dispatchScope;
                }
            },
            _ => { },
            _ => { },
            _ => { });

        viewModel.ValidateCommand.Execute(null);
        var validationTask = viewModel.ValidationTask;
        Assert.NotNull(validationTask);
        await validationTask!.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.NotNull(viewModel.LatestResult);
        Assert.True(completionStatusWasDispatched);
        Assert.True(completionLogWasDispatched);
        Assert.False(viewModel.IsValidationRunning);
    }

    [Fact]
    public async Task ResetSuppressesQueuedValidationCompletionFromObsoleteGeneration()
    {
        OpenVisionLanguageService.Load();
        var project = CreateProject();
        project.MultiAxisCommissioningRecipe!.ValidationRepetitions = 2;
        var recipe = new MultiAxisCommissioningRecipeEditorViewModel(() => { });
        recipe.Load(project);
        var queuedPresentation = new List<Action>();
        using var viewModel = CreateViewModel(
            project,
            recipe,
            () => true,
            buildRuntime: CreateRuntime,
            dispatchToUi: action =>
            {
                queuedPresentation.Add(action);
                return Task.CompletedTask;
            });

        viewModel.ValidateCommand.Execute(null);
        var validationTask = viewModel.ValidationTask;
        Assert.NotNull(validationTask);
        await validationTask!.WaitAsync(TimeSpan.FromSeconds(10));

        viewModel.Reset();
        foreach (var action in queuedPresentation)
        {
            action();
        }

        Assert.False(viewModel.IsValidationRunning);
        Assert.Null(viewModel.LatestResult);
        Assert.Empty(viewModel.ResultHistoryEntries);
        Assert.Null(viewModel.BaselineComparison);
    }

    private static MultiAxisCommissioningViewModel CreateViewModel(
        MachineProjectDocument project,
        MultiAxisCommissioningRecipeEditorViewModel recipe,
        Func<bool> canValidate,
        Action<bool>? notifyParent = null,
        Func<SimulationRuntimeConfiguration>? buildRuntime = null,
        Func<Action, Task>? dispatchToUi = null) =>
        new(
            recipe,
            canValidate,
            () => false,
            () => project,
            () => null,
            () => "{}",
            buildRuntime ?? (() => throw new InvalidOperationException("The validation runner is not part of this test.")),
            TimeSpan.FromMilliseconds(5),
            dispatchToUi ?? (action =>
            {
                action();
                return Task.CompletedTask;
            }),
            _ => { },
            _ => { },
            _ => { },
            notifyParent ?? (_ => { }),
            _ => { });

    private static SimulationRuntimeConfiguration CreateRuntime() => new(
        [
            new AxisConfiguration
            {
                Id = "x",
                Name = "X",
                MinimumPosition = 0,
                MaximumPosition = 300,
                HomePosition = 0,
                MaximumVelocity = 100,
                Acceleration = 100,
                Deceleration = 100,
                FollowingErrorLimit = 10
            },
            new AxisConfiguration
            {
                Id = "y",
                Name = "Y",
                MinimumPosition = 0,
                MaximumPosition = 300,
                HomePosition = 0,
                MaximumVelocity = 100,
                Acceleration = 100,
                Deceleration = 100,
                FollowingErrorLimit = 10
            }
        ],
        [],
        []);

    private static MachineProjectDocument CreateProject() => new()
    {
        Name = "Test project",
        Axes =
        [
            new VirtualAxisDefinition
            {
                Id = "x",
                Name = "X",
                SoftLimitMin = 0,
                SoftLimitMax = 300,
                MaxVelocity = 100
            },
            new VirtualAxisDefinition
            {
                Id = "y",
                Name = "Y",
                SoftLimitMin = 0,
                SoftLimitMax = 300,
                MaxVelocity = 100
            }
        ],
        MultiAxisCommissioningRecipe = new MultiAxisCommissioningRecipeDefinition
        {
            Targets =
            [
                new MultiAxisCommissioningTargetDefinition
                {
                    AxisId = "x",
                    TargetPosition = 10
                },
                new MultiAxisCommissioningTargetDefinition
                {
                    AxisId = "y",
                    TargetPosition = 20
                }
            ]
        }
    };
}
