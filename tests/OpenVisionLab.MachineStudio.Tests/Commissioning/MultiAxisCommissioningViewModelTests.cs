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

    private static MultiAxisCommissioningViewModel CreateViewModel(
        MachineProjectDocument project,
        MultiAxisCommissioningRecipeEditorViewModel recipe,
        Func<bool> canValidate,
        Action<bool>? notifyParent = null) =>
        new(
            recipe,
            canValidate,
            () => false,
            () => project,
            () => null,
            () => "{}",
            () => throw new InvalidOperationException("The validation runner is not part of this test."),
            TimeSpan.FromMilliseconds(5),
            action =>
            {
                action();
                return Task.CompletedTask;
            },
            _ => { },
            _ => { },
            _ => { },
            notifyParent ?? (_ => { }),
            _ => { });

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
