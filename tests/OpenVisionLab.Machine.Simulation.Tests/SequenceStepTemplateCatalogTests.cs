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
        SequenceStepDraftResult unknown =
            _catalog.CreateDraft("not-a-template", "step-10", Targets());

        Assert.False(missingTarget.IsCreated);
        Assert.Null(missingTarget.Step);
        Assert.Contains("no compatible authored target", missingTarget.Message, StringComparison.Ordinal);
        Assert.False(unknown.IsCreated);
        Assert.Null(unknown.Step);
    }

    private static SequenceAuthoringTarget[] Targets() =>
    [
        new("di.ready", "Ready Sensor", SequenceAuthoringTargetKind.DigitalInput),
        new("do.run", "Run Output", SequenceAuthoringTargetKind.DigitalOutput),
        new("axis.x", "Transfer Axis", SequenceAuthoringTargetKind.Axis, "12.5")
    ];
}
