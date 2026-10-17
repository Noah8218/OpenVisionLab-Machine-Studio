using OpenVisionLab.Machine.Core.Devices;
using OpenVisionLab.Machine.Core.Axes;
using OpenVisionLab.Machine.Core.Layouts;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Core.Sequences;
using OpenVisionLab.MachineStudio.ViewModel;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

public sealed class CameraCaptureEditorViewModelTests
{
    [Fact]
    public void DelayConflictAndOffTickDraftDoNotMutateTheRecipeAndValidTimingRecovers()
    {
        var device = new DeviceDefinition { Id = "camera", Kind = DeviceKind.Camera, Camera = new() { ExposureDelayMilliseconds = 20, TransferDelayMilliseconds = 30 } };
        var trigger = new SequenceStepDefinition { Id = "trigger", Action = SequenceStepAction.TriggerCamera, TargetId = device.Id, Parameter = "original", NextStepId = "wait" };
        var wait = new SequenceStepDefinition { Id = "wait", Name = "검사 완료", Action = SequenceStepAction.WaitVisionResult, TargetId = device.Id, TimeoutMs = 2000 };
        var project = new MachineProjectDocument { Devices = [device], Sequences = [new() { Id = "sequence", Name = "검사 흐름", Steps = [trigger, wait] }] };
        int applied = 0;
        var editor = new CameraCaptureEditorViewModel(() => true, _ => applied++);
        editor.Load(project, device.Id);
        editor.Enabled = true;
        editor.ApplyCommand.Execute(null);
        Assert.Contains("2650 ms", editor.Status);
        Assert.Contains("2000 ms", editor.Status);
        Assert.Null(device.Camera.NumericalInput);
        Assert.Equal("original", trigger.Parameter);
        Assert.Equal(0, applied);
        editor.Parameters[8].Value = "0.001";
        editor.ApplyCommand.Execute(null);
        Assert.Contains("5 ms", editor.Status);
        Assert.Null(device.Camera.NumericalInput);
        editor.Parameters[8].Value = "1.2";
        editor.ApplyCommand.Execute(null);
        Assert.Equal(1200, device.Camera.NumericalInput!.GenerationDelayMilliseconds);
        Assert.Equal(editor.RecipeName, trigger.Parameter);
        Assert.Equal(2000, wait.TimeoutMs);
        Assert.Equal(1, applied);
        Assert.DoesNotContain("대기 제한", editor.Status);
    }

    [Fact]
    public Task SuccessfulCurrentSaveClearsAppliedSaveHintAndRetainsUnappliedValidation() => RunOnStaAsync(async () =>
    {
        var project = new MachineProjectDocument { Devices = [new() { Id = "camera", Kind = DeviceKind.Camera, Camera = new() }] };
        using var viewModel = new MainViewModel(project);
        var editor = viewModel.Camera.CaptureEditor;
        editor.Enabled = true;
        editor.ConfirmVersionCommand.Execute(null);
        editor.ApplyCommand.Execute(null);
        Assert.Contains("저장 필요", editor.Status);
        var root = Path.Combine(@"D:/OpenVisionLab-TestData/Machine/public-readiness-workflow-20261003/save-hint-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        await viewModel.SaveProjectAsync(Path.Combine(root, "recipe.ovmachine"));
        Assert.Contains("저장했습니다", editor.Status);
        Assert.DoesNotContain("저장 필요", editor.Status);
        editor.Parameters[0].Value = "NaN";
        editor.ApplyCommand.Execute(null);
        var validation = editor.Status;
        editor.NotifyProjectSaved();
        Assert.Equal(validation, editor.Status);
        Assert.Equal("NaN", editor.Parameters[0].Value);
    });

    [Fact]
    public void AddingAndUndoingAnAxisRefreshesCaptureTargetsWithoutResettingTheDraft()
    {
        var project = new MachineProjectDocument { Devices = [new() { Id = "camera", Kind = DeviceKind.Camera, Camera = new() }] };
        using var viewModel = new MainViewModel(project);
        var editor = viewModel.Camera.CaptureEditor;
        editor.Enabled = true;
        editor.RecipeName = "외관 검사";
        editor.Parameters[0].Value = "150";
        int refreshes = 0;
        editor.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(CameraCaptureEditorViewModel.Axes)) refreshes++; };

        Assert.True(viewModel.TryAddLayoutComponent(LayoutComponentKind.LinearStage));

        Assert.Equal(1, refreshes);
        Assert.Equal(Assert.Single(project.Axes).Id, editor.Axes[1].Id);
        Assert.Equal("외관 검사", editor.RecipeName);
        Assert.Equal("150", editor.Parameters[0].Value);
        Assert.True(editor.Enabled);
        Assert.Null(project.Devices[0].Camera!.NumericalInput);
        viewModel.UndoLayoutEditCommand.Execute(null);
        Assert.Equal(2, refreshes);
        Assert.Single(editor.Axes);
        Assert.Equal("150", editor.Parameters[0].Value);
    }

    [Fact]
    public void RemovedAxisIsRejectedAndReselectionRecoversWithoutAutomaticApply()
    {
        var project = new MachineProjectDocument { Axes = [new VirtualAxisDefinition { Id = "axis", Name = "Axis" }], Devices = [new() { Id = "camera", Kind = DeviceKind.Camera, Camera = new() }] };
        int changes = 0;
        var editor = new CameraCaptureEditorViewModel(() => true, _ => changes++);
        editor.Load(project, "camera");
        editor.Enabled = true;
        editor.AxisId = "axis";
        editor.ConfirmVersionCommand.Execute(null);
        project.Axes.Clear();
        editor.RefreshAuthoringTargets();
        editor.ApplyCommand.Execute(null);
        Assert.Contains("연결 축이 삭제", editor.Status);
        Assert.Null(project.Devices[0].Camera!.NumericalInput);
        Assert.Equal(0, changes);
        editor.AxisId = "";
        editor.ApplyCommand.Execute(null);
        Assert.Equal(1, changes);
        Assert.Null(project.Devices[0].Camera!.NumericalInput!.AxisId);
    }

    [Fact]
    public void RevertResetAndCameraSelectionClearValidationWithoutApplying()
    {
        var project = new MachineProjectDocument();
        project.Devices.AddRange(new[] { new DeviceDefinition { Id = "a", Kind = DeviceKind.Camera, Camera = new() { NumericalInput = new() { FieldOfView = 120 } } }, new DeviceDefinition { Id = "b", Kind = DeviceKind.Camera, Camera = new() } });
        int changes = 0;
        var editor = new CameraCaptureEditorViewModel(() => true, _ => changes++);
        editor.Load(project, "a");
        editor.Parameters[0].Value = "0";
        editor.ApplyCommand.Execute(null);
        Assert.Contains("10–10000", editor.Status);
        editor.RevertCommand.Execute(null);
        Assert.Equal("120", editor.Parameters[0].Value);
        Assert.DoesNotContain("10–10000", editor.Status);
        editor.Parameters[0].Value = "0";
        editor.ApplyCommand.Execute(null);
        editor.ResetCommand.Execute(null);
        Assert.DoesNotContain("10–10000", editor.Status);
        editor.Enabled = true;
        editor.Parameters[0].Value = "0";
        editor.ApplyCommand.Execute(null);
        editor.SelectCamera("b");
        Assert.DoesNotContain("10–10000", editor.Status);
        Assert.Equal(120, project.Devices[0].Camera!.NumericalInput!.FieldOfView);
        Assert.Equal(0, changes);
    }

    [Fact]
    public void DraftsAreCameraScopedAndInvalidOrUnconfirmedEditsLeaveTheRecipeIntact()
    {
        var project = new MachineProjectDocument();
        project.Devices.AddRange(new[] { new DeviceDefinition { Id = "a", Kind = DeviceKind.Camera, Camera = new() }, new DeviceDefinition { Id = "b", Kind = DeviceKind.Camera, Camera = new() } });
        int changes = 0;
        bool editable = true;
        var editor = new CameraCaptureEditorViewModel(() => editable, _ => changes++);
        editor.Load(project, "a");
        editor.Enabled = true;
        editor.InputKind = NumericalCameraInputKind.HeightMap3D;
        editor.RecipeName = "높이 검사";
        editor.Version = "2";
        editor.Parameters[0].Value = "NaN";
        editor.ApplyCommand.Execute(null);
        Assert.Null(project.Devices[0].Camera!.NumericalInput);
        editor.SelectCamera("b");
        Assert.False(editor.Enabled);
        editor.SelectCamera("a");
        Assert.Equal("NaN", editor.Parameters[0].Value);
        editor.Parameters[0].Value = "120";
        editor.ApplyCommand.Execute(null);
        Assert.Null(project.Devices[0].Camera!.NumericalInput);
        editor.ConfirmVersionCommand.Execute(null);
        editor.ApplyCommand.Execute(null);
        Assert.Equal(1, changes);
        var store = new ProjectDocumentStore();
        var reopened = store.Load(store.Save(project));
        Assert.Equal(project.Devices[0].Camera!.NumericalInput, reopened.Devices[0].Camera!.NumericalInput);
        editor.Load(reopened, "a");
        Assert.Equal("2", editor.Version);
        Assert.Contains("2", editor.ConfirmationText);
        editable = false;
        editor.Enabled = false;
        editor.ApplyCommand.Execute(null);
        Assert.NotNull(reopened.Devices[0].Camera!.NumericalInput);
        Assert.Equal(1, changes);
    }
    private static Task RunOnStaAsync(Func<Task> action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
            dispatcher.InvokeAsync(async () =>
            {
                try { await action(); completion.SetResult(); }
                catch (Exception exception) { completion.SetException(exception); }
                finally { dispatcher.BeginInvokeShutdown(System.Windows.Threading.DispatcherPriority.Background); }
            });
            System.Windows.Threading.Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

}
