using System.IO;
using OpenVisionLab;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Simulation.Sequences;
using OpenVisionLab.MachineStudio.ViewModel;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

[Collection(LayoutStartupTestCollection.Name)]
public sealed class RecipeConnectionSimulationWorkflowTests
{
    [Fact]
    public void ReadinessSuccessUsesCurrentProjectAndPresentsStatusAndLog()
    {
        OpenVisionLanguageService.Load();
        var project = LoadRecipe();
        MachineProjectDocument? validatedProject = null;
        string? status = null;
        var logs = new List<(string Category, string Message)>();
        var workflow = CreateWorkflow(
            project,
            validated => validatedProject = validated,
            value => status = value,
            (category, message) => logs.Add((category, message)));

        var detail = workflow.ValidateSimulationReadiness();

        Assert.Null(detail);
        Assert.Same(project, validatedProject);
        Assert.Equal(OpenVisionLanguageService.T("Connections.ReadinessPassedStatus"), status);
        var log = Assert.Single(logs);
        Assert.Equal("Project", log.Category);
        Assert.Contains("passed without applying or running", log.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadinessFailureReturnsDetailAndPresentsFailure()
    {
        OpenVisionLanguageService.Load();
        string? status = null;
        var logs = new List<(string Category, string Message)>();
        var workflow = CreateWorkflow(
            LoadRecipe(),
            _ => throw new InvalidDataException("invalid runtime configuration"),
            value => status = value,
            (category, message) => logs.Add((category, message)));

        var detail = workflow.ValidateSimulationReadiness();

        Assert.Equal("invalid runtime configuration", detail);
        Assert.Equal(OpenVisionLanguageService.T("Connections.ReadinessFailedStatus"), status);
        var log = Assert.Single(logs);
        Assert.Equal("Project", log.Category);
        Assert.Contains("invalid runtime configuration", log.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PreviewAndDryRunUseIsolatedRunnersAndKeepProjectUnchanged()
    {
        OpenVisionLanguageService.Load();
        var project = LoadRecipe();
        var store = new ProjectDocumentStore();
        var before = store.Serialize(project);
        string? status = null;
        var logs = new List<(string Category, string Message)>();
        var workflow = CreateWorkflow(
            project,
            _ => { },
            value => status = value,
            (category, message) => logs.Add((category, message)));

        var preview = await workflow.RunSequenceStepPreviewAsync(
            "automatic-cycle",
            "extend-cylinder",
            "process-cylinder");
        var dryRun = await workflow.RunRecipeDryRunAsync("automatic-cycle");

        Assert.True(preview.IsCompleted, preview.Detail);
        Assert.True(dryRun.IsCompleted, dryRun.Detail);
        Assert.Equal(
            OpenVisionLanguageService.T("Connections.DryRunCompletedStatus"),
            status);
        Assert.Equal(before, store.Serialize(project));
        Assert.Contains(logs, log =>
            log.Category == "Simulation"
            && log.Message.Contains("Isolated connection step preview", StringComparison.Ordinal));
        Assert.Contains(logs, log =>
            log.Category == "Simulation"
            && log.Message.Contains("Isolated recipe dry run", StringComparison.Ordinal));
    }

    private static RecipeConnectionSimulationWorkflow CreateWorkflow(
        MachineProjectDocument project,
        Action<MachineProjectDocument> validateRuntimeConfiguration,
        Action<string> setStatus,
        Action<string, string> log) =>
        new(
            () => project,
            validateRuntimeConfiguration,
            setStatus,
            log);

    private static MachineProjectDocument LoadRecipe() =>
        new ProjectDocumentStore().Load(File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "SemiconductorRecipes",
            "01-FoupLoadPort.ovmachine")));
}
