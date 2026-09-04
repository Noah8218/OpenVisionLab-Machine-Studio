using System.Windows.Input;
using OpenVisionLab.Machine.Core.Layouts;
using OpenVisionLab.Machine.Core.Sequences;
using OpenVisionLab.Machine.Simulation.Sequences;
using OpenVisionLab.MachineStudio.ViewModel;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

[Collection(LayoutStartupTestCollection.Name)]
public sealed class RecipeSequenceStepPreviewViewModelTests
{
    [Fact]
    public async Task CommandOwnsReadinessAndEditabilityGatesAndProjectsResult()
    {
        var row = CreateRow();
        var isReady = false;
        var isEditable = true;
        var calls = 0;
        var result = new SequenceStepPreviewResult(
            SequenceStepPreviewOutcome.Completed,
            SequenceStepAction.MoveAxis,
            "axis-x",
            2,
            10,
            null,
            "axis reached");
        var viewModel = CreateViewModel(
            () => isEditable,
            () => isReady,
            (_, _, _) =>
            {
                calls++;
                return Task.FromResult(result);
            });

        Assert.False(viewModel.PreviewSequenceStepCommand.CanExecute(row));

        isReady = true;
        viewModel.RefreshCanExecute();
        Assert.True(viewModel.PreviewSequenceStepCommand.CanExecute(row));

        viewModel.PreviewSequenceStepCommand.Execute(row);
        await WaitForAsync(() => row.HasPreviewResult);

        Assert.Equal(1, calls);
        Assert.Same(result, row.PreviewResult);
        Assert.True(row.IsPreviewSuccessful);

        isEditable = false;
        viewModel.RefreshCanExecute();
        Assert.False(viewModel.PreviewSequenceStepCommand.CanExecute(row));
    }

    [Fact]
    public async Task InFlightResultIsIgnoredWhenRowOrReadinessBecomesStale()
    {
        var row = CreateRow();
        var isReady = true;
        var isCurrentRow = true;
        var resultSource = new TaskCompletionSource<SequenceStepPreviewResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var result = new SequenceStepPreviewResult(
            SequenceStepPreviewOutcome.LimitReached,
            SequenceStepAction.Wait,
            "signal-x",
            10,
            10,
            null,
            "preview limit");
        var viewModel = CreateViewModel(
            () => true,
            () => isReady,
            (_, _, _) => resultSource.Task,
            _ => isCurrentRow && isReady);

        viewModel.PreviewSequenceStepCommand.Execute(row);
        isCurrentRow = false;
        isReady = false;
        resultSource.SetResult(result);
        await WaitForAsync(() => resultSource.Task.IsCompletedSuccessfully);
        await Task.Yield();

        Assert.False(row.HasPreviewResult);
    }

    [Fact]
    public async Task AsyncCommandRejectsRepeatedExecutionWhilePreviewIsInFlight()
    {
        var row = CreateRow();
        var isReady = true;
        var calls = 0;
        var resultSource = new TaskCompletionSource<SequenceStepPreviewResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var result = new SequenceStepPreviewResult(
            SequenceStepPreviewOutcome.Completed,
            SequenceStepAction.Wait,
            "signal-x",
            1,
            10,
            null,
            "preview complete");
        var viewModel = CreateViewModel(
            () => true,
            () => isReady,
            (_, _, _) =>
            {
                calls++;
                return resultSource.Task;
            });

        viewModel.PreviewSequenceStepCommand.Execute(row);
        Assert.False(viewModel.PreviewSequenceStepCommand.CanExecute(row));
        viewModel.PreviewSequenceStepCommand.Execute(row);
        Assert.Equal(1, calls);

        resultSource.SetResult(result);
        await WaitForAsync(() => row.HasPreviewResult);
        Assert.True(isReady);
    }

    private static RecipeSequenceStepPreviewViewModel CreateViewModel(
        Func<bool> isEditable,
        Func<bool> isReady,
        Func<string, string, string, Task<SequenceStepPreviewResult>> preview,
        Func<RecipeConnectionRowViewModel, bool>? isCurrentRow = null) =>
        new(
            preview,
            isEditable,
            isReady,
            isCurrentRow ?? (_ => true));

    private static RecipeConnectionRowViewModel CreateRow() => new()
    {
        ComponentId = "component-1",
        Name = "Component 1",
        Kind = LayoutComponentKind.PneumaticCylinder,
        KindText = "Cylinder",
        BehaviorText = "Cylinder",
        ConnectionText = "channel-x",
        SequenceText = "Step 1",
        SequenceUseCount = 1,
        FirstSequenceId = "sequence-1",
        FirstSequenceStepId = "step-1",
        FirstSequenceAction = SequenceStepAction.Wait,
        SequenceTargetId = "signal-x",
        CanAddSequenceStep = false,
        RelatedTargetIds = new HashSet<string>(StringComparer.Ordinal) { "component-1", "signal-x" },
        IsConnected = true,
        IsValid = true,
        ValidationText = "Valid"
    };

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 100 && !condition(); attempt++)
        {
            await Task.Delay(1);
        }

        Assert.True(condition());
    }
}
