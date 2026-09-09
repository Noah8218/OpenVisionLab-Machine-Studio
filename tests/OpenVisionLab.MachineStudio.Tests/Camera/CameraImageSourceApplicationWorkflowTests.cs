using Xunit;
using OpenVisionLab.MachineStudio.ViewModel;

namespace OpenVisionLab.MachineStudio.Tests;

public sealed class CameraImageSourceApplicationWorkflowTests
{
    [Fact]
    public void AppliedResultMarksProjectBeforeRefreshingCameraState()
    {
        var events = new List<string>();
        var workflow = CreateWorkflow(events);

        workflow.Apply(new(
            CameraImageSourceApplicationOutcome.Applied,
            "camera-1",
            "images/inspection.png"));

        Assert.Equal(
            [
                "mark-project-changed",
                "status:Camera.SourceAppliedSave",
                "log:Camera:Image source applied · camera-1 · images/inspection.png",
                "refresh-vision-evidence",
                "notify-camera-commissioning"
            ],
            events);
    }

    [Fact]
    public void RejectedResultDoesNotMutateProjectOrRefreshCameraState()
    {
        var events = new List<string>();
        var workflow = CreateWorkflow(events);

        workflow.Apply(new(
            CameraImageSourceApplicationOutcome.Rejected,
            null,
            "outside-project"));

        Assert.Equal(
            [
                "status:Camera.SourceMustBeProjectOwned",
                "log:Camera:Image source selection rejected · outside-project"
            ],
            events);
    }

    [Fact]
    public void AppliedResultWithoutCameraIdIsRejected()
    {
        var events = new List<string>();
        var workflow = CreateWorkflow(events);

        workflow.Apply(new(
            CameraImageSourceApplicationOutcome.Applied,
            " ",
            "images/inspection.png"));

        Assert.DoesNotContain("mark-project-changed", events);
        Assert.DoesNotContain("refresh-vision-evidence", events);
        Assert.DoesNotContain("notify-camera-commissioning", events);
        Assert.Equal("status:Camera.SourceMustBeProjectOwned", events[0]);
    }

    private static CameraImageSourceApplicationWorkflow CreateWorkflow(List<string> events) =>
        new(
            () => events.Add("mark-project-changed"),
            status => events.Add($"status:{status}"),
            (category, message) => events.Add($"log:{category}:{message}"),
            () => events.Add("refresh-vision-evidence"),
            () => events.Add("notify-camera-commissioning"),
            key => key);
}
