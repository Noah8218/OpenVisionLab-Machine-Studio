using OpenVisionLab;
using OpenVisionLab.Machine.Core.Sequences;
using OpenVisionLab.MachineStudio.ViewModel;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

public sealed class ProcessPlanReviewViewModelTests
{
    [Fact]
    public void OpenMoveAndReturn_PreservesReviewOrderAndSelection()
    {
        OpenVisionLanguageService.Load();
        var items = new List<SemiconductorProcessBlockItemPresentation>
        {
            CreateItem("sequence-1", "step-1", "Load"),
            CreateItem("sequence-1", "step-2", "Align"),
            CreateItem("sequence-1", "step-3", "Inspect")
        };
        var opened = new List<string>();
        var selectedStepId = string.Empty;
        var selectedTab = -1;
        var status = string.Empty;
        var viewModel = CreateViewModel(
            items,
            opened,
            stepId =>
            {
                selectedStepId = stepId;
                return items.FirstOrDefault(item => item.StepId == stepId)?.StepText;
            },
            tabIndex => selectedTab = tabIndex,
            value => status = value);

        viewModel.OpenProcessBlockSequenceStep("sequence-1", "step-2");

        Assert.True(viewModel.HasReturnContext);
        Assert.Equal("step-2", viewModel.ReturnStepId);
        Assert.Contains("2/3", viewModel.ReviewPositionText, StringComparison.Ordinal);
        Assert.True(viewModel.PreviousStepCommand.CanExecute(null));
        Assert.True(viewModel.NextStepCommand.CanExecute(null));
        Assert.True(viewModel.ReturnToProcessPlanCommand.CanExecute(null));

        viewModel.NextStepCommand.Execute(null);

        Assert.Equal(["sequence-1/step-2", "sequence-1/step-3"], opened);
        Assert.Equal("step-3", viewModel.ReturnStepId);
        Assert.Contains("3/3", viewModel.ReviewPositionText, StringComparison.Ordinal);
        Assert.False(viewModel.NextStepCommand.CanExecute(null));
        Assert.True(viewModel.PreviousStepCommand.CanExecute(null));

        viewModel.ReturnToProcessPlanCommand.Execute(null);

        Assert.Equal(1, selectedTab);
        Assert.Equal("step-3", selectedStepId);
        Assert.Contains("Inspect", status, StringComparison.Ordinal);
        Assert.True(viewModel.HasReturnContext);
    }

    [Fact]
    public void OpenUnknownStep_UsesSingleItemFallbackAndClearRemovesContext()
    {
        OpenVisionLanguageService.Load();
        var items = new List<SemiconductorProcessBlockItemPresentation>
        {
            CreateItem("sequence-1", "step-1", "Load")
        };
        var opened = new List<string>();
        var viewModel = CreateViewModel(
            items,
            opened,
            stepId =>
            {
                opened.Add($"selected/{stepId}");
                return "Unknown step";
            },
            _ => { },
            _ => { },
            (_, _) => "Unknown step");

        viewModel.OpenProcessBlockSequenceStep("sequence-2", "step-9");

        Assert.True(viewModel.HasReturnContext);
        Assert.Equal("step-9", viewModel.ReturnStepId);
        Assert.Contains("1/1", viewModel.ReviewPositionText, StringComparison.Ordinal);
        Assert.False(viewModel.PreviousStepCommand.CanExecute(null));
        Assert.False(viewModel.NextStepCommand.CanExecute(null));

        viewModel.Clear();

        Assert.False(viewModel.HasReturnContext);
        Assert.Null(viewModel.ReturnStepId);
        Assert.Equal(string.Empty, viewModel.ReviewPositionText);
        Assert.False(viewModel.ReturnToProcessPlanCommand.CanExecute(null));
    }

    [Fact]
    public void FailedOpenDoesNotCreateContextAndEditabilityGuardsNavigation()
    {
        OpenVisionLanguageService.Load();
        var items = new List<SemiconductorProcessBlockItemPresentation>
        {
            CreateItem("sequence-1", "step-1", "Load"),
            CreateItem("sequence-1", "step-2", "Align")
        };
        var editable = true;
        var previewVisible = true;
        var viewModel = new ProcessPlanReviewViewModel(
            () => editable,
            () => previewVisible,
            () => items,
            () => items,
            (_, _) => null,
            stepId => items.FirstOrDefault(item => item.StepId == stepId)?.StepText,
            _ => { },
            _ => { });

        viewModel.OpenProcessBlockSequenceStep("sequence-1", "step-1");

        Assert.False(viewModel.HasReturnContext);
        Assert.False(viewModel.ReturnToProcessPlanCommand.CanExecute(null));

        viewModel = new ProcessPlanReviewViewModel(
            () => editable,
            () => previewVisible,
            () => items,
            () => items,
            (_, _) => "Load",
            stepId => items.FirstOrDefault(item => item.StepId == stepId)?.StepText,
            _ => { },
            _ => { });
        viewModel.OpenProcessBlockSequenceStep("sequence-1", "step-1");
        editable = false;
        previewVisible = false;
        viewModel.InvalidateCommands();

        Assert.False(viewModel.ReturnToProcessPlanCommand.CanExecute(null));
        Assert.False(viewModel.NextStepCommand.CanExecute(null));
    }

    private static ProcessPlanReviewViewModel CreateViewModel(
        IReadOnlyList<SemiconductorProcessBlockItemPresentation> items,
        List<string> opened,
        Func<string, string?> selectProcessBlockStep,
        Action<int> selectDocumentTab,
        Action<string> setStatus,
        Func<string, string, string?>? tryOpenSequenceStep = null)
    {
        return new ProcessPlanReviewViewModel(
            () => true,
            () => true,
            () => items,
            () => items,
            tryOpenSequenceStep ?? ((sequenceId, stepId) =>
            {
                opened.Add($"{sequenceId}/{stepId}");
                return items.FirstOrDefault(item =>
                    item.SequenceId == sequenceId && item.StepId == stepId)?.StepText;
            }),
            selectProcessBlockStep,
            selectDocumentTab,
            setStatus);
    }

    private static SemiconductorProcessBlockItemPresentation CreateItem(
        string sequenceId,
        string stepId,
        string stepText) =>
        new(
            sequenceId,
            stepId,
            stepText,
            stepText,
            SequenceStepAction.WaitSignal,
            100,
            IsProposed: false,
            IsAlreadyConfigured: true,
            IsCustomized: false,
            IsProposedRemoval: false,
            IsUnavailable: false);
}
