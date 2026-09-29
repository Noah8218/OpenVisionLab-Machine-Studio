using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using OpenVisionLab;
using OpenVisionLab.Machine.Core.Devices;
using OpenVisionLab.Machine.Sequence.Runtime;
using OpenVisionLab.Machine.Simulation.Camera;
using OpenVisionLab.Machine.Simulation.Engine;
using OpenVisionLab.MachineStudio.View.Inspector;
using OpenVisionLab.MachineStudio.ViewModel;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class CameraResultInspectorViewAutomationTestCollection
{
    public const string Name = "Camera result inspector WPF automation";
}

[Collection(CameraResultInspectorViewAutomationTestCollection.Name)]
public sealed class CameraResultInspectorViewAutomationTests
{
    [Fact]
    public async Task SelectingEachCameraBindsItsOwnResultAndSharedWorkpieceAssociation()
    {
        var result = await RunOnStaAsync(() =>
        {
            if (Application.Current is null) new App().InitializeComponent();
            OpenVisionLanguageService.Load();
            var originalLanguage = OpenVisionLanguageService.CurrentLanguage;
            try
            {
                OpenVisionLanguageService.SetLanguage(OpenVisionLanguage.Korean, save: false);
                var project = CameraCommissioningViewModelTests.CreateProject();
                var projections = new Dictionary<string, CameraCommissioningProjection>(StringComparer.Ordinal)
                {
                    ["camera-1"] = CreateProjection(CreateCameraSnapshot(
                        "camera-1",
                        "Camera 1",
                        "acquisition-1",
                        "inspection-1",
                        PlaceholderInspectionDecision.Pass,
                        "workpiece-1")),
                    ["camera-2"] = CreateProjection(CreateCameraSnapshot(
                        "camera-2",
                        "Camera 2",
                        "acquisition-2",
                        "inspection-2",
                        PlaceholderInspectionDecision.Fail,
                        "workpiece-1"))
                };
                CameraCommissioningViewModel? camera = null;
                camera = CameraCommissioningViewModelTests.CreateViewModel(
                    project,
                    projections["camera-1"],
                    () => projections[camera?.SelectedCameraId ?? "camera-1"]);
                using (camera)
                {
                    camera.LoadProject(project, null);
                    var view = new RightToolRegionView
                    {
                        DataContext = new CameraInspectorContext(camera)
                    };
                    var window = new Window
                    {
                        Content = view,
                        Height = 900,
                        Left = -2000,
                        ShowActivated = false,
                        ShowInTaskbar = false,
                        Top = -2000,
                        Width = 540,
                        WindowStyle = WindowStyle.None
                    };

                    window.Show();
                    try
                    {
                        window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(window.UpdateLayout));
                        var cameraSelector = Assert.IsType<ComboBox>(view.FindName("CameraSelectionComboBox"));
                        var evidenceDetails = Assert.IsType<TextBlock>(view.FindName("CameraExecutionEvidenceDetailsTextBlock"));
                        var resultText = Descendants<TextBlock>(view).Single(text =>
                            text.GetBindingExpression(TextBlock.TextProperty)?.ParentBinding.Path?.Path
                                == nameof(CameraCommissioningViewModel.CurrentCameraResultText));
                        var cameraOne = CaptureProjection(cameraSelector, resultText, evidenceDetails);

                        cameraSelector.SelectedValue = "camera-2";
                        window.Dispatcher.Invoke(DispatcherPriority.DataBind, new Action(() => { }));
                        window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(window.UpdateLayout));
                        var cameraTwo = CaptureProjection(cameraSelector, resultText, evidenceDetails);

                        return new CameraSwitchResult(
                            cameraOne,
                            cameraTwo,
                            OpenVisionLanguageService.T("Shell.ResultPass"),
                            OpenVisionLanguageService.T("Shell.ResultFail"),
                            OpenVisionLanguageService.T("Camera.ResultSourceMock"));
                    }
                    finally
                    {
                        window.Close();
                    }
                }
            }
            finally
            {
                OpenVisionLanguageService.SetLanguage(originalLanguage, save: false);
            }
        });

        Assert.Equal("camera-1", result.First.SelectedCameraId);
        Assert.Equal("camera-2", result.Second.SelectedCameraId);
        Assert.Equal(result.PassText, result.First.ResultText);
        Assert.Equal(result.FailText, result.Second.ResultText);
        Assert.Contains("workpiece-1", result.First.EvidenceDetails, StringComparison.Ordinal);
        Assert.Contains("workpiece-1", result.Second.EvidenceDetails, StringComparison.Ordinal);
        Assert.Contains("inspection-1", result.First.EvidenceDetails, StringComparison.Ordinal);
        Assert.Contains("inspection-2", result.Second.EvidenceDetails, StringComparison.Ordinal);
        Assert.Contains(result.MockSourceText, result.First.EvidenceDetails, StringComparison.Ordinal);
        Assert.Contains(result.MockSourceText, result.Second.EvidenceDetails, StringComparison.Ordinal);
        Assert.NotEqual(result.First.EvidenceDetails, result.Second.EvidenceDetails);
    }

    private static CameraViewProjection CaptureProjection(
        ComboBox selector,
        TextBlock resultText,
        TextBlock evidenceDetails) => new(
        selector.SelectedValue?.ToString() ?? string.Empty,
        resultText.Text,
        evidenceDetails.Text);

    private static CameraCommissioningProjection CreateProjection(VirtualCameraSnapshot snapshot) => new(
        snapshot,
        HasCameraDefinition: true,
        FallbackCameraName: snapshot.Name,
        ImageSource: new VirtualSingleImageSourceDefinition
        {
            SourceRelativePath = "images/part.pgm",
            Width = 2,
            Height = 2,
            PixelFormat = "Mono8"
        },
        ProjectPath: @"D:\OpenVisionLab-TestData\Machine\r19-priority-25-inspector-view\machine.ovmachine",
        SelectedCameraRecipe: "recipe-1",
        SimulationFixedStep: TimeSpan.FromMilliseconds(5),
        RuntimeRunMode: SimulationRunMode.Paused,
        IsRunMode: true,
        IsApplyingProject: false,
        IsValidationBusy: false,
        IsRuntimeDefinitionDirty: false,
        IsRunning: false,
        ControlOwner: SimulationControlOwner.EmbeddedSequence,
        IsAutomaticRunActive: false,
        ActiveSequenceStatus: SequenceExecutionStatus.Completed);

    private static VirtualCameraSnapshot CreateCameraSnapshot(
        string cameraId,
        string cameraName,
        string acquisitionId,
        string inspectionId,
        PlaceholderInspectionDecision decision,
        string workpieceComponentId)
    {
        var frame = new VirtualCameraFrameEvidence(
            $"frame-{cameraId}",
            "images/part.pgm",
            new string('A', 64),
            4,
            2,
            2,
            "Mono8");
        var inspection = new VirtualCameraInspectionEvidence(
            inspectionId,
            acquisitionId,
            cameraId,
            "recipe-1",
            frame.FrameId,
            decision,
            decision == PlaceholderInspectionDecision.Pass ? "Pass result" : "Fail result",
            new Dictionary<string, double> { ["score"] = decision == PlaceholderInspectionDecision.Pass ? 0.9 : 0.2 });
        var result = new VirtualCameraAcquisitionResult(
            acquisitionId,
            cameraId,
            "recipe-1",
            1,
            decision,
            frame,
            inspection,
            WorkpieceComponentId: workpieceComponentId,
            WorkpieceInstanceId: "workpiece-instance-1");
        return new VirtualCameraSnapshot(
            cameraId,
            cameraName,
            VirtualCameraState.FrameReady,
            1,
            acquisitionId,
            "recipe-1",
            0,
            0,
            result,
            frame);
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }

    private static Task<T> RunOnStaAsync<T>(Func<T> action)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { completion.SetResult(action()); }
            catch (Exception exception) { completion.SetException(exception); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private sealed record CameraInspectorContext(CameraCommissioningViewModel Camera);

    private sealed record CameraViewProjection(string SelectedCameraId, string ResultText, string EvidenceDetails);

    private sealed record CameraSwitchResult(
        CameraViewProjection First,
        CameraViewProjection Second,
        string PassText,
        string FailText,
        string MockSourceText);
}
