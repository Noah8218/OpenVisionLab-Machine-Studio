using OpenVisionLab.Machine.Core.Sequences;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Sequence.Authoring;
using Xunit;

namespace OpenVisionLab.Machine.Simulation.Tests;

public sealed class SequenceDefinitionEditorTests
{
    [Fact]
    public void InsertMoveDelete_RebuildsOneLinearSuccessPathDeterministically()
    {
        SequenceDefinition sequence = LinearSequence();
        var editor = new SequenceDefinitionEditor();
        var inserted = new SequenceStepDefinition
        {
            Id = "set-output",
            Name = "Set Output",
            Action = SequenceStepAction.SetSignal,
            TargetId = "do.ready",
            Parameter = "true"
        };

        SequenceEditResult insert = editor.InsertBeforeTerminal(sequence, inserted);
        Assert.True(insert.IsAccepted);
        Assert.Equal(new[] { "move", "wait", "set-output", "complete" }, Ids(sequence));
        AssertLinearLinks(sequence);

        SequenceEditResult move = editor.Move(sequence, "set-output", -2);
        Assert.True(move.IsAccepted);
        Assert.Equal(new[] { "set-output", "move", "wait", "complete" }, Ids(sequence));
        AssertLinearLinks(sequence);

        SequenceEditResult delete = editor.Delete(sequence, "move");
        Assert.True(delete.IsAccepted);
        Assert.Equal(new[] { "set-output", "wait", "complete" }, Ids(sequence));
        AssertLinearLinks(sequence);
    }

    [Fact]
    public void Move_BranchedSequence_IsRejectedWithoutMutation()
    {
        SequenceDefinition sequence = LinearSequence();
        sequence.Steps[0].ErrorStepId = "complete";
        string[] before = Ids(sequence);
        var editor = new SequenceDefinitionEditor();

        SequenceEditResult result = editor.Move(sequence, "wait", -1);

        Assert.False(result.IsAccepted);
        Assert.Equal(SequenceEditErrorCode.LinearSequenceRequired, result.ErrorCode);
        Assert.Equal(before, Ids(sequence));
        Assert.Equal("complete", sequence.Steps[0].ErrorStepId);
    }

    [Fact]
    public void Delete_TerminalComplete_IsRejectedWithoutMutation()
    {
        SequenceDefinition sequence = LinearSequence();
        var editor = new SequenceDefinitionEditor();

        SequenceEditResult result = editor.Delete(sequence, "complete");

        Assert.False(result.IsAccepted);
        Assert.Equal(SequenceEditErrorCode.TerminalStepCannotDelete, result.ErrorCode);
        Assert.Equal(new[] { "move", "wait", "complete" }, Ids(sequence));
        AssertLinearLinks(sequence);
    }

    [Fact]
    public void Insert_BranchedDraft_IsRejectedWithoutChangingLinearSequence()
    {
        SequenceDefinition sequence = LinearSequence();
        var editor = new SequenceDefinitionEditor();
        var branched = new SequenceStepDefinition
        {
            Id = "branched",
            Name = "Branched",
            Action = SequenceStepAction.WaitSignal,
            TargetId = "di.ready",
            Parameter = "true",
            ErrorStepId = "complete"
        };

        SequenceEditResult result = editor.InsertBeforeTerminal(sequence, branched);

        Assert.False(result.IsAccepted);
        Assert.Equal(SequenceEditErrorCode.LinearSequenceRequired, result.ErrorCode);
        Assert.Equal(new[] { "move", "wait", "complete" }, Ids(sequence));
        AssertLinearLinks(sequence);
    }

    [Fact]
    public void EditedSequence_SaveAndReload_PreservesOrderFieldsAndTransitions()
    {
        SequenceDefinition sequence = LinearSequence();
        var project = new MachineProjectDocument
        {
            Id = "sequence-editor-round-trip",
            Name = "Sequence Editor Round Trip",
            Sequences = { sequence }
        };
        var editor = new SequenceDefinitionEditor();
        var inserted = new SequenceStepDefinition
        {
            Id = "set-output",
            Name = "Set Ready Output",
            Action = SequenceStepAction.SetSignal,
            TargetId = "do.ready",
            Parameter = "true"
        };

        Assert.True(editor.InsertBeforeTerminal(sequence, inserted).IsAccepted);
        Assert.True(editor.Move(sequence, inserted.Id, -1).IsAccepted);
        inserted.Name = "Set Ready";
        inserted.ErrorStepId = "complete";

        var store = new ProjectDocumentStore();
        MachineProjectDocument reloaded = store.Load(store.Save(project));
        SequenceDefinition actual = Assert.Single(reloaded.Sequences);

        Assert.Equal(new[] { "move", "set-output", "wait", "complete" }, Ids(actual));
        Assert.Equal("Set Ready", actual.Steps[1].Name);
        Assert.Equal("complete", actual.Steps[1].ErrorStepId);
        Assert.Equal("wait", actual.Steps[1].NextStepId);
    }

    private static SequenceDefinition LinearSequence() =>
        new()
        {
            Id = "auto",
            Name = "Automatic",
            Steps =
            {
                new SequenceStepDefinition
                {
                    Id = "move",
                    Name = "Move",
                    Action = SequenceStepAction.MoveAxis,
                    TargetId = "x",
                    Parameter = "100",
                    NextStepId = "wait"
                },
                new SequenceStepDefinition
                {
                    Id = "wait",
                    Name = "Wait",
                    Action = SequenceStepAction.WaitAxisDone,
                    TargetId = "x",
                    TimeoutMs = 1000,
                    NextStepId = "complete"
                },
                new SequenceStepDefinition
                {
                    Id = "complete",
                    Name = "Complete",
                    Action = SequenceStepAction.Complete
                }
            }
        };

    private static string[] Ids(SequenceDefinition sequence) =>
        sequence.Steps.Select(step => step.Id).ToArray();

    private static void AssertLinearLinks(SequenceDefinition sequence)
    {
        for (var index = 0; index < sequence.Steps.Count; index++)
        {
            string? expected = index + 1 < sequence.Steps.Count
                ? sequence.Steps[index + 1].Id
                : null;
            Assert.Equal(expected, sequence.Steps[index].NextStepId);
        }
    }
}
