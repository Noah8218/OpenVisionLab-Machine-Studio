using OpenVisionLab.Machine.Core.Devices;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Core.Sequences;
using OpenVisionLab.MachineStudio.ViewModel;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

public sealed class CameraSelectionWorkflowTests
{
    [Fact]
    public void EnsureSelectionChoosesFirstCameraAndSortedDistinctRecipes()
    {
        var project = CreateProject();
        var events = new List<string>();
        var workflow = CreateWorkflow(project, events);

        workflow.EnsureSelectionFor(project);

        Assert.Equal("camera-1", workflow.SelectedCameraId);
        Assert.Equal("alpha", workflow.SelectedCameraRecipe);
        Assert.Equal(["alpha", "zeta"], workflow.CurrentCameraRecipes);
        Assert.Same(project.Devices[0], workflow.SelectedVirtualCamera);
        Assert.Same(project.Devices[0], workflow.GetSelectedDefinition(null));
        Assert.Equal(["property:SelectedCameraRecipe"], events);
    }

    [Fact]
    public void InvalidOrNullSelectionDoesNotTriggerCallbacksAndValidSelectionPreservesOrder()
    {
        var project = CreateProject();
        var events = new List<string>();
        var workflow = CreateWorkflow(project, events);
        workflow.EnsureSelectionFor(project);
        events.Clear();

        workflow.SelectVirtualCamera("missing");
        workflow.SelectVirtualCamera(null);

        Assert.Equal("camera-1", workflow.SelectedCameraId);
        Assert.Empty(events);

        workflow.SelectVirtualCamera("camera-2");

        Assert.Equal("camera-2", workflow.SelectedCameraId);
        Assert.Equal("beta", workflow.SelectedCameraRecipe);
        Assert.Equal(["beta"], workflow.CurrentCameraRecipes);
        Assert.Equal(
            [
                "property:SelectedCameraRecipe",
                "editor:camera-2",
                "property:SelectedCameraId",
                "property:SelectedVirtualCamera",
                "property:CurrentCameraRecipes",
                "evidence",
                "snapshot"
            ],
            events);
    }

    [Fact]
    public void RecipeSelectionRejectsBlankWhenRecipesExistAndRefreshesDependents()
    {
        var project = CreateProject();
        var events = new List<string>();
        var workflow = CreateWorkflow(project, events);
        workflow.EnsureSelectionFor(project);
        events.Clear();

        workflow.SelectCameraRecipe(string.Empty);
        workflow.SelectCameraRecipe("zeta");

        Assert.Equal("zeta", workflow.SelectedCameraRecipe);
        Assert.Equal(
            [
                "property:SelectedCameraRecipe",
                "evidence",
                "commissioning"
            ],
            events);
    }

    private static CameraSelectionWorkflow CreateWorkflow(
        MachineProjectDocument project,
        List<string> events) =>
        new(
            () => project,
            cameraId => events.Add($"editor:{cameraId}"),
            () => events.Add("evidence"),
            () => events.Add("snapshot"),
            propertyName => events.Add($"property:{propertyName}"),
            () => events.Add("commissioning"));

    private static MachineProjectDocument CreateProject()
    {
        var project = new MachineProjectDocument { Name = "Camera selection" };
        project.Devices.AddRange(
        [
            new DeviceDefinition
            {
                Id = "camera-1",
                Name = "Camera 1",
                Kind = DeviceKind.Camera
            },
            new DeviceDefinition
            {
                Id = "camera-2",
                Name = "Camera 2",
                Kind = DeviceKind.Camera
            }
        ]);
        project.Sequences.Add(
            new SequenceDefinition
            {
                Id = "sequence-1",
                Steps =
                [
                    new SequenceStepDefinition
                    {
                        Id = "trigger-zeta",
                        Action = SequenceStepAction.TriggerCamera,
                        TargetId = "camera-1",
                        Parameter = " zeta "
                    },
                    new SequenceStepDefinition
                    {
                        Id = "trigger-alpha",
                        Action = SequenceStepAction.TriggerCamera,
                        TargetId = "camera-1",
                        Parameter = "alpha"
                    },
                    new SequenceStepDefinition
                    {
                        Id = "trigger-alpha-duplicate",
                        Action = SequenceStepAction.TriggerCamera,
                        TargetId = "camera-1",
                        Parameter = "alpha"
                    },
                    new SequenceStepDefinition
                    {
                        Id = "trigger-camera-2",
                        Action = SequenceStepAction.TriggerCamera,
                        TargetId = "camera-2",
                        Parameter = "beta"
                    }
                ]
            });
        return project;
    }
}
