using OpenVisionLab.Machine.Core.Axes;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Core.Sequences;
using OpenVisionLab.MachineStudio.ViewModel;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

public sealed class SequenceEditorViewModelTests
{
    [Fact]
    public void TargetStepCanBeAddedToStrictSequenceAndRoundTrips()
    {
        var project = new MachineProjectDocument
        {
            Id = "target-step-project",
            Name = "Target step project",
            Axes =
            [
                new VirtualAxisDefinition
                {
                    Id = "axis-1",
                    Name = "Axis 1"
                }
            ],
            Sequences =
            [
                CompleteSequence("sequence-1")
            ]
        };

        var editor = new SequenceEditorViewModel();
        editor.Load(project);

        var stepId = editor.TryAddStepForTarget("axis-1");

        Assert.NotNull(stepId);
        var addedStep = Assert.Single(project.Sequences[0].Steps, step => step.Id == stepId);
        Assert.Equal(SequenceStepAction.MoveAxis, addedStep.Action);
        Assert.Equal("axis-1", addedStep.TargetId);
        Assert.Equal("complete", addedStep.NextStepId);
        Assert.Equal(stepId, editor.SelectedStep?.Id);

        var reopened = new ProjectDocumentStore().Load(new ProjectDocumentStore().Serialize(project));
        var reopenedStep = Assert.Single(reopened.Sequences[0].Steps, step => step.Id == stepId);
        Assert.Equal(SequenceStepAction.MoveAxis, reopenedStep.Action);
        Assert.Equal("axis-1", reopenedStep.TargetId);
        Assert.Equal("complete", reopenedStep.NextStepId);
    }

    [Fact]
    public void CallSubsequenceTargetsExcludeTheSelectedSequenceAndSurviveReopen()
    {
        var project = new MachineProjectDocument
        {
            Id = "composition-project",
            Name = "Composition project",
            Sequences =
            [
                SequenceWithCall("parent", "child"),
                SequenceWithCall("child", "parent"),
                CompleteSequence("other")
            ]
        };

        var editor = new SequenceEditorViewModel();
        editor.Load(project);

        var parentCall = Assert.Single(editor.Steps.Where(step => step.Id == "call-child"));
        Assert.Equal(SequenceStepAction.CallSubsequence, parentCall.Action);
        Assert.Equal(new[] { "child", "other" }, parentCall.AvailableTargets.Select(target => target.Id));
        Assert.DoesNotContain(parentCall.AvailableTargets, target => target.Id == "parent");
        Assert.Contains(parentCall.AvailableActions, action => action == SequenceStepAction.CallSubsequence);
        Assert.Empty(parentCall.AvailableParameterOptions);
        Assert.Equal(0, parentCall.TimeoutMs);

        editor.SelectSequence("child");
        var childCall = Assert.Single(editor.Steps.Where(step => step.Id == "call-parent"));
        Assert.Equal(new[] { "parent", "other" }, childCall.AvailableTargets.Select(target => target.Id));
        Assert.DoesNotContain(childCall.AvailableTargets, target => target.Id == "child");

        var reopened = new ProjectDocumentStore().Load(new ProjectDocumentStore().Serialize(project));
        var reopenedEditor = new SequenceEditorViewModel();
        reopenedEditor.Load(reopened);

        Assert.Equal("parent", reopenedEditor.SelectedSequence?.Id);
        Assert.Contains(reopenedEditor.Templates, template => template.Id == "call-subsequence");
        Assert.Equal(
            new[] { "child", "other" },
            Assert.Single(reopenedEditor.Steps.Where(step => step.Id == "call-child"))
                .AvailableTargets.Select(target => target.Id));
    }

    private static SequenceDefinition SequenceWithCall(string id, string targetId) => new()
    {
        Id = id,
        Name = id,
        Steps =
        [
            new SequenceStepDefinition
            {
                Id = $"call-{targetId}",
                Name = $"Call {targetId}",
                Action = SequenceStepAction.CallSubsequence,
                TargetId = targetId,
                NextStepId = "complete"
            },
            new SequenceStepDefinition
            {
                Id = "complete",
                Name = "Complete",
                Action = SequenceStepAction.Complete
            }
        ]
    };

    private static SequenceDefinition CompleteSequence(string id) => new()
    {
        Id = id,
        Name = id,
        Steps =
        [
            new SequenceStepDefinition
            {
                Id = "complete",
                Name = "Complete",
                Action = SequenceStepAction.Complete
            }
        ]
    };
}
