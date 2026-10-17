using System.IO;
using System.Text.Json;
using System.Security.Cryptography;
using OpenVisionLab.Machine.Sequence.Runtime;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using OpenVisionLab;
using OpenVisionLab.Machine.Core.Devices;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Persistence.Projects;
using OpenVisionLab.MachineStudio.View.Shell;
using OpenVisionLab.MachineStudio.ViewModel;
using OpenVisionLab.TestSupport;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

[Collection(StudioUiTestCollection.Name)]
public sealed class GeneratedInspectionTeachingWorkflowTests(StudioUiTestHost ui)
{
    [Fact]
    public async Task AppliedTeachingSurvivesReopenAndRealSequenceGeneratesBothInputsBeforeReset()
    {
        await ui.InvokeTaskAsync(async () =>
        {
            var previousLanguage = OpenVisionLanguageService.CurrentLanguage;
            OpenVisionLanguageService.SetLanguage(OpenVisionLanguage.Korean, save: false);
            var project = new ProjectDocumentStore().Load(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Samples", "R19InspectionFlow.ovmachine")));
            project.Name = "공개 준비 흐름 검사 · 수치 모의";
            var root = Path.Combine(TestStorage.RootPath, "generated-teaching-workflow", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var path = Path.Combine(root, "teaching.ovmachine");
            using var main = new MainViewModel(project, path, integrationSettingsPath: Path.Combine(root, "integration.json"));
            var window = new ShellWindow { DataContext = main, Width = 1920, Height = 1032, Left = -3000, Top = -3000, ShowActivated = false, ShowInTaskbar = false };
            window.Show();
            var frames = new List<object>();
            var evidence = Environment.GetEnvironmentVariable("OPENVISIONLAB_GENERATED_WORKFLOW_EVIDENCE");
            try
            {
                await WaitUntilAsync(() => main.SceneSnapshots.Latest?.ProjectId == project.Id);
                Capture("01-layout-before", "R19 레시피를 연 뒤 배치 편집 전");
                main.Layout.Select("camera-height-verdict");
                double originalX = main.Layout.SelectedItem!.CurrentX;
                Assert.True(main.Layout.MoveSelectionBy(40, 20));
                Assert.Equal(originalX + 40, main.Layout.SelectedItem!.CurrentX);
                Capture("02-layout-after", "높이 촬영부 배치를 X +40, Y +20으로 티칭");
                main.Navigation.IsInspectionWorkspace = true;
                foreach (var (id, kind) in new[] { ("device.camera-1", NumericalCameraInputKind.Image2D), ("device.height-verdict", NumericalCameraInputKind.HeightMap3D) })
                {
                    main.Camera.SelectedCameraId = id;
                    var editor = main.Camera.CaptureEditor;
                    editor.Enabled = true;
                    editor.InputKind = kind;
                    editor.RecipeName = kind == NumericalCameraInputKind.Image2D ? "상면 외관 · 수치 모의" : "높이 분포 · LU 수치 모의";
                    editor.Parameters[0].Value = "150";
                    editor.Parameters[1].Value = "5";
                    editor.ConfirmVersionCommand.Execute(null);
                    Assert.True(editor.ApplyCommand.CanExecute(null));
                    editor.ApplyCommand.Execute(null);
                    Assert.Contains("대기 제한", editor.Status);
                    Assert.Null(project.Devices.Single(device => device.Id == id).Camera!.NumericalInput);
                    editor.Parameters[8].Value = "1.2";
                    editor.ApplyCommand.Execute(null);
                    Assert.Contains("저장 필요", editor.Status);
                    main.Navigation.SelectedInspectionSettingsTabIndex = 2;
                    Capture(kind == NumericalCameraInputKind.Image2D ? "03-teach-2d" : "04-teach-height", "촬영 종류·폭 150·X 편차 5를 확인하고 적용; 실행 전");
                }
                await main.SaveProjectAsync(path).WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Contains("저장했습니다", main.Camera.CaptureEditor.Status);
                Capture("05-saved", "저장 성공 후 저장 필요 안내 해소");
                Assert.True(await main.OpenProjectReplacingCurrentAsync(path).WaitAsync(TimeSpan.FromSeconds(10)));
                Assert.False(main.HasUnsavedChanges || main.IsRunning);
                Assert.All(main.SceneSnapshots.Latest!.Cameras, camera => Assert.Equal(0, camera.AcquisitionOrdinal));
                Assert.All(new ProjectDocumentStore().Load(File.ReadAllText(path)).Devices.Where(device => device.Kind == DeviceKind.Camera), device =>
                {
                    Assert.Equal(150, device.Camera!.NumericalInput!.FieldOfView);
                    Assert.Equal(5, device.Camera.NumericalInput.OffsetX);
                    Assert.Equal(device.Camera.NumericalInput.Version, device.Camera.NumericalInput.ConfirmedVersion);
                });
                main.Navigation.IsInspectionWorkspace = true;
                main.Camera.SelectedCameraId = "device.camera-1";
                main.Navigation.SelectedInspectionSettingsTabIndex = 0;
                Capture("06-reopened", "저장 레시피 재열기; 입력 생성과 실행 없음");
                main.IsRunMode = true;
                await WaitUntilAsync(() => main.RunCommand.CanExecute(null));
                main.RunCommand.Execute(null);
                await WaitUntilAsync(() => main.SceneSnapshots.Latest?.Cameras.Any(camera => camera.Id == "device.camera-1" && camera.Result?.NumericalInput is not null) == true, 40);
                Assert.True(main.Camera.HasCurrentNumericalInput && main.Camera.HasCurrentCameraImage);
                Capture("07-2d-generated", "실제 시뮬레이션 시퀀스에서 상면 수치 모의 입력 생성");
                await WaitUntilAsync(() => main.SceneSnapshots.Latest?.Cameras.Any(camera => camera.Id == "device.height-verdict" && camera.Result?.NumericalInput is not null) == true, 40);
                main.Camera.SelectedCameraId = "device.height-verdict";
                Assert.True(main.Camera.HasCurrentNumericalInput);
                main.Camera.ShowInputOutputCommand.Execute("height");
                Assert.True(main.Camera.IsHeightDataView);
                Capture("08-height-generated", "같은 시뮬레이션 시퀀스에서 수치 높이 입력 표시; 외부 검사 아님");
                await WaitUntilAsync(() => main.SceneSnapshots.Latest?.Sequences.Any(sequence => sequence.Status == SequenceExecutionStatus.Completed) == true, 20);
                var firstInstance = main.SceneSnapshots.Latest!.Cameras.Single(camera => camera.Id == "device.height-verdict").Result!.WorkpieceInstanceId;
                Assert.NotNull(firstInstance);
                Capture("09-recipe-completed", "2D·높이 촬영 뒤 레시피 종료까지 완료; 엔진 운전 상태와 구분");
                Assert.True(main.ResetCommand.CanExecute(null));
                main.ResetCommand.Execute(null);
                await WaitUntilAsync(() => main.SceneSnapshots.Latest?.Cameras.All(camera => camera.AcquisitionOrdinal == 0) == true);
                Assert.False(main.Camera.HasCurrentNumericalInput || main.Camera.HasCurrentCameraImage);
                Capture("10-reset", "Reset 후 지난 입력·판정 제거");
                await WaitUntilAsync(() => main.RunCommand.CanExecute(null));
                main.RunCommand.Execute(null);
                await WaitUntilAsync(() => main.SceneSnapshots.Latest?.Sequences.Any(sequence => sequence.Status == SequenceExecutionStatus.Completed) == true, 30);
                Assert.NotEqual(firstInstance, main.SceneSnapshots.Latest!.Cameras.Single(camera => camera.Id == "device.height-verdict").Result!.WorkpieceInstanceId);
                Assert.True(main.Camera.HasCurrentNumericalInput);
                main.Integration.Setup.UseThreeDHeightMap = true;
                main.Camera.ShowInputOutputCommand.Execute("height");
                Assert.False(main.Integration.CanPublishInspectionHandoff);
                Capture("11-rerun-completed", "동일 레시피 재실행 완료; 3D 요청 모드 선택, 검증된 빌드 필요 상태 유지");
                if (!string.IsNullOrWhiteSpace(evidence))
                    File.WriteAllText(Path.Combine(evidence, "frames.json"), JsonSerializer.Serialize(new { kind = "in-process WPF bound-command verification", nativeComputerUse = false, externalConsumerRun = false, width = 1920, height = 1032, projectPath = path, recipeSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))), applicationAssembly = typeof(MainViewModel).Assembly.Location, applicationSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(MainViewModel).Assembly.Location))), version = typeof(MainViewModel).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false).OfType<System.Reflection.AssemblyInformationalVersionAttribute>().Single().InformationalVersion, frames }, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception exception)
            {
                Capture("failed-last-state", "필수 높이 촬영 상태에 도달하지 못한 실패 화면");
                if (!string.IsNullOrWhiteSpace(evidence))
                    File.WriteAllText(Path.Combine(evidence, "failure-state.json"), JsonSerializer.Serialize(new { error = exception.ToString(), status = main.StatusMessage, snapshot = main.SceneSnapshots.Latest }, new JsonSerializerOptions { WriteIndented = true }));
                throw;
            }
            finally
            {
                window.DataContext = null;
                window.Close();
                Assert.DoesNotContain(window, Application.Current.Windows.Cast<Window>());
                OpenVisionLanguageService.SetLanguage(previousLanguage, save: false);
            }

            void Capture(string name, string caption)
            {
                window.Dispatcher.Invoke(DispatcherPriority.DataBind, new Action(() => { }));
                window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(window.UpdateLayout));
                if (string.IsNullOrWhiteSpace(evidence)) return;
                Directory.CreateDirectory(evidence);
                var bitmap = new RenderTargetBitmap(1920, 1032, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(window);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var output = File.Create(Path.Combine(evidence, name + ".png"));
                encoder.Save(output);
                frames.Add(new { name, caption, capturedAt = DateTimeOffset.UtcNow, tick = main.SceneSnapshots.Latest?.TickIndex, camera = main.Camera.SelectedCameraId, hasInput = main.Camera.HasCurrentNumericalInput, running = main.IsRunning });
            }
        });
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, int seconds = 10)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!predicate() && DateTime.UtcNow < deadline) await Task.Delay(10);
        Assert.True(predicate(), "The required workflow state did not arrive before its deadline.");
    }
}
