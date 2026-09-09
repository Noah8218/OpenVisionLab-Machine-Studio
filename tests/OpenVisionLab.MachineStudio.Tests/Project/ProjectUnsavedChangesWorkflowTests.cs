using OpenVisionLab.MachineStudio.Model;
using OpenVisionLab.MachineStudio.ViewModel;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

public sealed class ProjectUnsavedChangesWorkflowTests
{
    [Fact]
    public async Task NoChangesCompletesWithoutPromptOrSave()
    {
        var events = new List<string>();
        var workflow = CreateWorkflow(events, hasUnsavedChanges: false);

        var result = await workflow.ResolveAsync();

        Assert.Equal(ProjectUnsavedChangesOutcome.NoChanges, result.Outcome);
        Assert.True(result.IsAccepted);
        Assert.Equal(["commit", "prepare", "refresh-dirty", "has-unsaved"], events);
    }

    [Fact]
    public async Task DiscardCompletesAfterDirtyResolution()
    {
        var events = new List<string>();
        var workflow = CreateWorkflow(
            events,
            hasUnsavedChanges: true,
            decision: UnsavedProjectDecision.Discard);

        var result = await workflow.ResolveAsync();

        Assert.Equal(ProjectUnsavedChangesOutcome.Discarded, result.Outcome);
        Assert.True(result.IsAccepted);
        Assert.Equal(
            ["commit", "prepare", "refresh-dirty", "has-unsaved", "decision"],
            events);
    }

    [Fact]
    public async Task SaveCompletesOnlyWhenSaveOperationIsAccepted()
    {
        var events = new List<string>();
        var workflow = CreateWorkflow(
            events,
            hasUnsavedChanges: true,
            decision: UnsavedProjectDecision.Save,
            saveAccepted: true);

        var result = await workflow.ResolveAsync();

        Assert.Equal(ProjectUnsavedChangesOutcome.Saved, result.Outcome);
        Assert.True(result.IsAccepted);
        Assert.Equal(
            ["commit", "prepare", "refresh-dirty", "has-unsaved", "decision", "save"],
            events);
    }

    [Fact]
    public async Task SaveRejectionBlocksTheDocumentOperation()
    {
        var events = new List<string>();
        var workflow = CreateWorkflow(
            events,
            hasUnsavedChanges: true,
            decision: UnsavedProjectDecision.Save,
            saveAccepted: false);

        var result = await workflow.ResolveAsync();

        Assert.Equal(ProjectUnsavedChangesOutcome.SaveRejected, result.Outcome);
        Assert.False(result.IsAccepted);
        Assert.Equal(
            ["commit", "prepare", "refresh-dirty", "has-unsaved", "decision", "save"],
            events);
    }

    [Fact]
    public async Task CancelStopsTheDocumentOperationWithoutSaving()
    {
        var events = new List<string>();
        var workflow = CreateWorkflow(
            events,
            hasUnsavedChanges: true,
            decision: UnsavedProjectDecision.Cancel);

        var result = await workflow.ResolveAsync();

        Assert.Equal(ProjectUnsavedChangesOutcome.Cancelled, result.Outcome);
        Assert.False(result.IsAccepted);
        Assert.Equal(
            ["commit", "prepare", "refresh-dirty", "has-unsaved", "decision"],
            events);
    }

    private static ProjectUnsavedChangesWorkflow CreateWorkflow(
        List<string> events,
        bool hasUnsavedChanges,
        UnsavedProjectDecision decision = UnsavedProjectDecision.Cancel,
        bool saveAccepted = false) =>
        new(
            () =>
            {
                events.Add("commit");
                return Task.CompletedTask;
            },
            () => events.Add("prepare"),
            () =>
            {
                events.Add("has-unsaved");
                return hasUnsavedChanges;
            },
            () => events.Add("refresh-dirty"),
            () =>
            {
                events.Add("decision");
                return decision;
            },
            () =>
            {
                events.Add("save");
                return Task.FromResult(saveAccepted);
            });
}
