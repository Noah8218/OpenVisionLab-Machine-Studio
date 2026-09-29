using OpenVisionLab.Machine.Core.Sequences;
using OpenVisionLab.Machine.Sequence.Authoring;
using Xunit;

namespace OpenVisionLab.Machine.Simulation.Tests;

public sealed class SequenceStepTemplateCatalogTests
{
    private readonly SequenceStepTemplateCatalog _catalog = new();

    [Fact]
    public void GetTargets_FiltersByActionAndPreservesAuthoredOrder()
    {
        SequenceAuthoringTarget[] targets = Targets();

        Assert.Equal(
            new[] { "di.ready", "do.run" },
            _catalog.GetTargets(SequenceStepAction.WaitSignal, targets).Select(target => target.Id));
        Assert.Equal(
            new[] { "do.run" },
            _catalog.GetTargets(SequenceStepAction.SetSignal, targets).Select(target => target.Id));
        Assert.Equal(
            new[] { "axis.x" },
            _catalog.GetTargets(SequenceStepAction.MoveAxis, targets).Select(target => target.Id));
        Assert.Equal(
            new[] { "workpiece.1" },
            _catalog.GetTargets(
                SequenceStepAction.FeedWorkpiece,
                targets.Append(new SequenceAuthoringTarget(
                    "workpiece.1",
                    "Wafer position",
                    SequenceAuthoringTargetKind.Workpiece))).Select(target => target.Id));
        Assert.Equal(
            new[] { "workpiece.1" },
            _catalog.GetTargets(
                SequenceStepAction.EjectWorkpiece,
                targets.Append(new SequenceAuthoringTarget(
                    "workpiece.1",
                    "Wafer position",
                    SequenceAuthoringTargetKind.Workpiece))).Select(target => target.Id));
        Assert.Equal(
            new[] { "sequence.child" },
            _catalog.GetTargets(
                SequenceStepAction.CallSubsequence,
                targets.Append(new SequenceAuthoringTarget(
                    "sequence.child",
                    "Child sequence",
                    SequenceAuthoringTargetKind.Subsequence))).Select(target => target.Id));
        Assert.Empty(_catalog.GetTargets(SequenceStepAction.Complete, targets));
    }

    [Fact]
    public void GetAvailableTemplates_ExcludesTemplatesWithoutCompatibleTarget()
    {
        IReadOnlyList<SequenceStepTemplateDefinition> templates =
            _catalog.GetAvailableTemplates(Targets());

        Assert.Equal(6, templates.Count);
        Assert.DoesNotContain(templates, template => template.Id == "trigger-camera");
        Assert.Contains(templates, template => template.Id == "set-output-on");
        Assert.Contains(templates, template => template.Id == "wait-input-on");
        Assert.Contains(templates, template => template.Id == "move-axis-home");

        IReadOnlyList<SequenceStepTemplateDefinition> workpieceTemplates =
            _catalog.GetAvailableTemplates(Targets().Append(new SequenceAuthoringTarget(
                "workpiece.1",
                "Wafer position",
                SequenceAuthoringTargetKind.Workpiece)));
        Assert.Contains(workpieceTemplates, template => template.Id == "feed-workpiece");
        Assert.Contains(workpieceTemplates, template => template.Id == "eject-workpiece");
        Assert.DoesNotContain(
            _catalog.GetAvailableTemplates([new SequenceAuthoringTarget(
                string.Empty,
                "No workpiece association",
                SequenceAuthoringTargetKind.Workpiece)]),
            template => template.Id is "feed-workpiece" or "eject-workpiece");
    }

    [Fact]
    public void CreateDraft_MoveAxisHome_UsesTypedTargetAndAuthoredHomeValue()
    {
        SequenceStepDraftResult result =
            _catalog.CreateDraft("move-axis-home", "step-8", Targets());

        Assert.True(result.IsCreated);
        SequenceStepDefinition step = Assert.IsType<SequenceStepDefinition>(result.Step);
        Assert.Equal("step-8", step.Id);
        Assert.Equal(SequenceStepAction.MoveAxis, step.Action);
        Assert.Equal("axis.x", step.TargetId);
        Assert.Equal("12.5", step.Parameter);
        Assert.Equal(0, step.TimeoutMs);
    }

    [Fact]
    public void CreateDraft_MissingTargetOrUnknownTemplate_FailsClosed()
    {
        SequenceStepDraftResult missingTarget =
            _catalog.CreateDraft("trigger-camera", "step-9", Targets());
        SequenceStepDraftResult missingWorkpiece =
            _catalog.CreateDraft("feed-workpiece", "step-10", Targets());
        SequenceStepDraftResult unknown =
            _catalog.CreateDraft("not-a-template", "step-11", Targets());

        Assert.False(missingTarget.IsCreated);
        Assert.Null(missingTarget.Step);
        Assert.Contains("no compatible authored target", missingTarget.Message, StringComparison.Ordinal);
        Assert.False(missingWorkpiece.IsCreated);
        Assert.Null(missingWorkpiece.Step);
        Assert.False(unknown.IsCreated);
        Assert.Null(unknown.Step);
    }

    [Fact]
    public void CreateDraft_WorkpieceActionsRequireAnExplicitPositionSelection()
    {
        SequenceAuthoringTarget[] targets = Targets().Append(new SequenceAuthoringTarget(
            "workpiece.1",
            "Wafer position",
            SequenceAuthoringTargetKind.Workpiece)).ToArray();

        SequenceStepDraftResult feed = _catalog.CreateDraft("feed-workpiece", "step-1", targets);
        SequenceStepDraftResult eject = _catalog.CreateDraft("eject-workpiece", "step-2", targets);

        AssertWorkpieceDraft(feed, SequenceStepAction.FeedWorkpiece);
        AssertWorkpieceDraft(eject, SequenceStepAction.EjectWorkpiece);
    }

    private static SequenceAuthoringTarget[] Targets() =>
    [
        new("di.ready", "Ready Sensor", SequenceAuthoringTargetKind.DigitalInput),
        new("do.run", "Run Output", SequenceAuthoringTargetKind.DigitalOutput),
        new("axis.x", "Transfer Axis", SequenceAuthoringTargetKind.Axis, "12.5")
    ];

    private static void AssertWorkpieceDraft(
        SequenceStepDraftResult result,
        SequenceStepAction action)
    {
        Assert.True(result.IsCreated);
        SequenceStepDefinition step = Assert.IsType<SequenceStepDefinition>(result.Step);
        Assert.Equal(action, step.Action);
        Assert.Equal(string.Empty, step.TargetId);
        Assert.Equal(string.Empty, step.Parameter);
        Assert.Null(step.WorkpieceComponentId);
        Assert.Equal(0, step.TimeoutMs);
    }
}
