using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using OpenVisionLab;
using OpenVisionLab.Machine.Core.Devices;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Simulation.Camera;
using OpenVisionLab.Machine.Simulation.Engine;
using OpenVisionLab.MachineStudio.View.Inspector;
using OpenVisionLab.MachineStudio.View.Shell;
using OpenVisionLab.MachineStudio.ViewModel;

namespace OpenVisionLab.MachineStudio;

internal sealed class SmokeCameraCommissioningReport
{
    public string Schema { get; init; } = "1.0";
    public DateTimeOffset CapturedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public required IReadOnlyDictionary<string, bool> Checks { get; init; }
    public required IReadOnlyList<string> Failures { get; init; }
    public SmokeMonitorEvidence? Monitor { get; init; }
    public bool IsValid => Failures.Count == 0 && Checks.Values.All(value => value);

    public void Save(string path)
    {
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(
            fullPath,
            JsonSerializer.Serialize(this, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = true
            }));
    }
}

internal static class SmokeCameraCommissioningVerifier
{
    private const uint MouseEventLeftDown = 0x0002;
    public static async Task<SmokeCameraCommissioningReport> VerifyAsync(
        ShellWindow window,
        MainViewModel viewModel,
        string? projectPath,
        bool editImageSource,
        Func<Task> scrollIntoView)
    {
        if (!viewModel.IsRunMode || string.IsNullOrWhiteSpace(projectPath))
        {
            throw new ArgumentException(
                "--smoke-camera-commissioning-report requires --smoke-run-layout and --smoke-project.");
        }

        var checks = new Dictionary<string, bool>(StringComparer.Ordinal);
        var failures = new List<string>();
        void Check(string name, bool passed)
        {
            checks[name] = passed;
            if (!passed)
            {
                failures.Add(name);
            }
        }

        async Task WaitForAsync(Func<bool> condition, string failureMessage)
        {
            for (var attempt = 0; attempt < 120; attempt++)
            {
                if (condition())
                {
                    return;
                }
                await Task.Delay(50);
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            }
            throw new InvalidOperationException(failureMessage);
        }

        async Task StepAsync()
        {
            var beforeTick = viewModel.SceneSnapshots.Latest?.TickIndex
                ?? throw new InvalidOperationException("Camera snapshot was unavailable before Step.");
            viewModel.StepCommand.Execute(null);
            await WaitForAsync(
                () => viewModel.SceneSnapshots.Latest?.TickIndex == beforeTick + 1,
                "Camera commissioning Step did not advance exactly one tick.");
        }

        await WaitForAsync(
            () => viewModel.SceneSnapshots.Latest?.Cameras.Count > 0,
            "Virtual camera snapshot was unavailable.");
        viewModel.ResetCommand.Execute(null);
        await WaitForAsync(
            () => !viewModel.IsRunning
                && viewModel.SceneSnapshots.Latest is
                {
                    TickIndex: 0,
                    ControlOwner: SimulationControlOwner.Definition
                }
                && viewModel.SceneSnapshots.Latest.Cameras[0].State == VirtualCameraState.Idle,
            "Camera runtime did not reset before commissioning.");

        if (editImageSource)
        {
            var editor = viewModel.CameraImageSourceEditor;
            Check("sourceEditorRestoresProjectValues",
                editor.PathText == "assets/presence-check.pgm"
                && editor.Width == 16
                && editor.Height == 12
                && editor.PixelFormatText == "Mono8"
                && !editor.IsDirty
                && !editor.ApplyCommand.CanExecute(null));

            editor.PathText = "../outside.pgm";
            Check("projectExternalDraftDoesNotMutateProject",
                editor.HasError
                && !editor.ApplyCommand.CanExecute(null)
                && viewModel.CurrentCameraSourceText == "assets/presence-check.pgm");
            editor.RevertCommand.Execute(null);

            editor.PixelFormatText = string.Empty;
            Check("invalidDraftDoesNotMutateProject",
                editor.HasError
                && !editor.ApplyCommand.CanExecute(null)
                && viewModel.CurrentCameraSourceText == "assets/presence-check.pgm");
            editor.RevertCommand.Execute(null);
            Check("revertRestoresAppliedDefinition",
                editor.PixelFormatText == "Mono8"
                && !editor.IsDirty
                && !editor.HasError);

            var beforeApply = viewModel.SceneSnapshots.Latest!;
            editor.Width = 32;
            editor.Height = 24;
            Check("validDraftEnablesApply",
                editor.IsDirty
                && !editor.HasError
                && editor.ApplyCommand.CanExecute(null));
            editor.ApplyCommand.Execute(null);
            var afterApply = viewModel.SceneSnapshots.Latest!;
            Check("applyDoesNotStartAcquisition",
                afterApply.TickIndex == beforeApply.TickIndex
                && afterApply.SimulationTime == beforeApply.SimulationTime
                && afterApply.Cameras[0].State == VirtualCameraState.Idle
                && afterApply.Cameras[0].AcquisitionOrdinal == 0
                && afterApply.Cameras[0].FrameEvidence is null
                && !editor.IsDirty);

            await viewModel.SaveProjectAsync(projectPath);
            if (!await viewModel.OpenProjectAsync(projectPath))
            {
                throw new InvalidOperationException("Edited camera project could not be reopened.");
            }
            await WaitForAsync(
                () => viewModel.SceneSnapshots.Latest?.Cameras[0] is
                {
                    State: VirtualCameraState.Idle,
                    AcquisitionOrdinal: 0,
                    FrameEvidence: null
                },
                "Edited camera project reopen restored an acquisition.");
            Check("saveReopenRestoresSourceSettings",
                editor.PathText == "assets/presence-check.pgm"
                && editor.Width == 32
                && editor.Height == 24
                && editor.PixelFormatText == "Mono8"
                && !editor.IsDirty);
            Check("saveReopenDoesNotAutoAcquire", true);
            viewModel.IsRunMode = true;
        }

        Check("authoredSelectionRestored",
            viewModel.SelectedVirtualCamera is not null
            && viewModel.SelectedCameraRecipe == "presence-check"
            && viewModel.CurrentCameraSourceText == "assets/presence-check.pgm");
        Check("manualStartAvailableAfterReset",
            viewModel.StartManualCameraControlCommand.CanExecute(null)
            && !viewModel.TriggerCameraCommand.CanExecute(null));

        viewModel.StartManualCameraControlCommand.Execute(null);
        await WaitForAsync(
            () => viewModel.IsRunning
                && viewModel.SceneSnapshots.Latest?.ControlOwner == SimulationControlOwner.Manual,
            "Manual camera control did not start.");
        viewModel.PauseCommand.Execute(null);
        await WaitForAsync(() => !viewModel.IsRunning, "Manual camera control did not pause.");
        Check("pausedManualEnablesTrigger", viewModel.TriggerCameraCommand.CanExecute(null));

        var paused = viewModel.SceneSnapshots.Latest!;
        viewModel.TriggerCameraCommand.Execute(null);
        await WaitForAsync(
            () => viewModel.SceneSnapshots.Latest?.Cameras[0] is
            {
                State: VirtualCameraState.Exposing,
                FrameEvidence: not null
            },
            "Camera trigger did not publish immutable frame evidence.");
        var triggered = viewModel.SceneSnapshots.Latest!;
        var camera = triggered.Cameras[0];
        var evidence = camera.FrameEvidence!;
        Check("triggerDoesNotAdvancePausedTick",
            triggered.TickIndex == paused.TickIndex
            && triggered.SimulationTime == paused.SimulationTime
            && camera.ExposureTicksRemaining == 4);

        var sourcePath = Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(projectPath))!,
            viewModel.CurrentCameraSourceText.Replace('/', Path.DirectorySeparatorChar));
        await using (var stream = File.OpenRead(sourcePath))
        {
            var expectedSha256 = Convert.ToHexString(await SHA256.HashDataAsync(stream));
            Check("frameHashMatchesProjectAsset",
                evidence.ContentSha256 == expectedSha256
                && evidence.FrameId == "cam1/frame/00000001"
                && evidence.SourceRelativePath == "assets/presence-check.pgm"
                && (!editImageSource || evidence is { Width: 32, Height: 24, PixelFormat: "Mono8" }));
        }

        await Task.Delay(150);
        var frozen = viewModel.SceneSnapshots.Latest!;
        Check("pauseFreezesAcquisition",
            frozen.TickIndex == triggered.TickIndex
            && frozen.Cameras[0].ExposureTicksRemaining == 4);

        for (var index = 0; index < 3; index++)
        {
            await StepAsync();
        }
        Check("exposureHoldsUntilFourthTick",
            viewModel.SceneSnapshots.Latest!.Cameras[0].State == VirtualCameraState.Exposing
            && viewModel.SceneSnapshots.Latest.Cameras[0].ExposureTicksRemaining == 1);
        await StepAsync();
        Check("fourthTickStartsTransfer",
            viewModel.SceneSnapshots.Latest!.Cameras[0].State == VirtualCameraState.Transferring
            && viewModel.SceneSnapshots.Latest.Cameras[0].TransferTicksRemaining == 6);

        for (var index = 0; index < 5; index++)
        {
            await StepAsync();
        }
        Check("transferHoldsUntilSixthTick",
            viewModel.SceneSnapshots.Latest!.Cameras[0].State == VirtualCameraState.Transferring
            && viewModel.SceneSnapshots.Latest.Cameras[0].TransferTicksRemaining == 1);
        await StepAsync();
        var ready = viewModel.SceneSnapshots.Latest!.Cameras[0];
        var firstInspection = ready.Result?.InspectionEvidence;
        Check("sixthTransferTickPublishesResult",
            ready.State == VirtualCameraState.FrameReady
            && ready.Result?.FrameEvidence == evidence
            && ready.Result.Decision == PlaceholderInspectionDecision.Pass
            && viewModel.CurrentCameraFrameHashText == evidence.ContentSha256);
        Check("deterministicRunnerPublishesCorrelatedEvidence",
            firstInspection is not null
            && firstInspection.AcquisitionId == evidence.FrameId
            && firstInspection.CameraId == ready.Id
            && firstInspection.RecipeId == "presence-check"
            && firstInspection.FrameId == evidence.FrameId
            && firstInspection.Decision == PlaceholderInspectionDecision.Pass
            && firstInspection.Metrics.SequenceEqual(new Dictionary<string, double>
            {
                ["ContentLengthBytes"] = evidence.ContentLength,
                ["PixelCount"] = (double)evidence.Width * evidence.Height,
                ["SimulationTick"] = triggered.TickIndex
            }));
        await WaitForAsync(
            () => viewModel.LogMessages.Any(line =>
                line.Contains(firstInspection!.InspectionId, StringComparison.Ordinal)
                && line.Contains("PixelCount=", StringComparison.Ordinal)),
            "Inspection identity and metrics did not reach the existing Event Journal.");
        Check("existingEventJournalContainsInspectionEvidence", true);
        await WaitForAsync(
            () => viewModel.LatestVisionEvidence is not null,
            "Project-linked Vision execution evidence was not completed.");
        var firstPackage = viewModel.LatestVisionEvidence!;
        var evidencePath = $"{Path.GetFullPath(projectPath)}.vision-result.json";
        Check("executionEvidenceCorrelatesProjectBuildFrameAndInspection",
            firstPackage.HasValidEvidenceHash()
            && firstPackage.ProjectId == new ProjectDocumentStore()
                .Load(File.ReadAllText(projectPath)).Id
            && firstPackage.BuildIdentity == BuildIdentity.Current
            && firstPackage.CameraId == ready.Id
            && firstPackage.RecipeId == "presence-check"
            && firstPackage.FrameHash == evidence.ContentSha256
            && firstPackage.InspectionId == firstInspection!.InspectionId
            && firstPackage.Events.Any(item => item.Code == "CameraTriggered")
            && firstPackage.Events.Any(item => item.Code == "CameraFrameReady")
            && firstPackage.Events.Any(item => item.Code == "VisionResultReady"));
        Check("executionEvidenceUsesInformationalBuildIdentity",
            BuildIdentity.Current != "0.1.0.0"
            && BuildIdentity.Current.Contains('+', StringComparison.Ordinal));
        Check("executionEvidencePersistsProjectSidecar",
            File.Exists(evidencePath)
            && viewModel.CurrentVisionEvidenceHashText == firstPackage.ShortEvidenceHash
            && viewModel.CurrentCameraInspectionIdText == firstInspection!.InspectionId
            && viewModel.CurrentCameraInspectionMessageText == firstInspection.Message
            && viewModel.CurrentCameraInspectionMetricsText.Contains("PixelCount=", StringComparison.Ordinal));

        viewModel.ResetCommand.Execute(null);
        await WaitForAsync(
            () => viewModel.SceneSnapshots.Latest?.Cameras[0] is
            {
                State: VirtualCameraState.Idle,
                AcquisitionOrdinal: 0,
                FrameEvidence: null
            },
            "Reset did not clear camera acquisition evidence.");
        Check("resetClearsInspectionEvidence", true);

        if (!await viewModel.OpenProjectAsync(projectPath))
        {
            throw new InvalidOperationException("Camera commissioning project could not be reopened.");
        }
        await WaitForAsync(
            () => !viewModel.IsRunning
                && viewModel.SceneSnapshots.Latest?.ControlOwner == SimulationControlOwner.Definition
                && viewModel.SceneSnapshots.Latest.Cameras[0].State == VirtualCameraState.Idle
                && viewModel.SceneSnapshots.Latest.Cameras[0].FrameEvidence is null,
            "Project reopen restored a runtime camera acquisition.");
        Check("reopenDoesNotRestoreAcquisition", true);
        Check("reopenRestoresRecipe",
            viewModel.SelectedCameraRecipe == "presence-check");
        Check("reopenRestoresImageSource",
            viewModel.CurrentCameraSourceText == "assets/presence-check.pgm");
        Check("reopenRestoresMatchingExecutionEvidence",
            viewModel.LatestVisionEvidence?.EvidenceHash == firstPackage.EvidenceHash
            && viewModel.CurrentVisionEvidenceHashText == firstPackage.ShortEvidenceHash);

        viewModel.IsRunMode = true;
        viewModel.ResetCommand.Execute(null);
        await WaitForAsync(
            () => viewModel.SceneSnapshots.Latest is
            {
                TickIndex: 0,
                ControlOwner: SimulationControlOwner.Definition
            },
            "Repeat evidence comparison did not reset to the original runtime origin.");
        viewModel.StartManualCameraControlCommand.Execute(null);
        await WaitForAsync(
            () => viewModel.IsRunning
                && viewModel.SceneSnapshots.Latest?.ControlOwner == SimulationControlOwner.Manual,
            "Manual camera control did not restart for evidence comparison.");
        viewModel.PauseCommand.Execute(null);
        await WaitForAsync(() => !viewModel.IsRunning, "Repeat camera control did not pause.");
        viewModel.TriggerCameraCommand.Execute(null);
        await WaitForAsync(
            () => viewModel.SceneSnapshots.Latest?.Cameras[0].State == VirtualCameraState.Exposing,
            "Repeat camera trigger did not start.");
        for (var index = 0; index < 10; index++)
        {
            await StepAsync();
        }
        await WaitForAsync(
            () => viewModel.VisionEvidenceComparison is not null,
            "Repeat Vision execution was not compared with restored evidence.");
        var repeatedPackage = viewModel.LatestVisionEvidence!;
        Check("repeatExecutionBuildMatches", repeatedPackage.BuildIdentity == firstPackage.BuildIdentity);
        Check("repeatExecutionProjectMatches", repeatedPackage.ProjectHash == firstPackage.ProjectHash);
        Check("repeatExecutionFrameMatches", repeatedPackage.FrameHash == firstPackage.FrameHash);
        Check("repeatExecutionReportsFirstMismatch",
            viewModel.VisionEvidenceComparison is
            {
                IsMatch: false,
                MismatchCode: "InspectionMismatch"
            }
            && repeatedPackage.InspectionId != firstPackage.InspectionId
            && repeatedPackage.EvidenceHash != firstPackage.EvidenceHash);

        var appliedWidth = viewModel.CameraImageSourceEditor.Width;
        viewModel.CameraImageSourceEditor.Width = appliedWidth + 1;
        viewModel.CameraImageSourceEditor.ApplyCommand.Execute(null);
        Check("projectChangeMarksExecutionEvidenceStale",
            viewModel.VisionEvidenceStatusText == OpenVisionLanguageService.T("Camera.EvidenceStale"));
        viewModel.CameraImageSourceEditor.Width = appliedWidth;
        viewModel.CameraImageSourceEditor.ApplyCommand.Execute(null);
        Check("restoredProjectContextRevalidatesExecutionEvidence",
            viewModel.VisionEvidenceStatusText == OpenVisionLanguageService.T("Camera.EvidenceSaved"));

        var originalLanguage = OpenVisionLanguageService.CurrentLanguage;
        var alternateLanguage = originalLanguage == OpenVisionLanguage.Korean
            ? OpenVisionLanguage.English
            : OpenVisionLanguage.Korean;
        var originalHint = viewModel.CameraCommissioningHintText;
        OpenVisionLanguageService.SetLanguage(alternateLanguage, save: false);
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Check("languageSwitchRefreshesCameraHint",
            viewModel.CameraCommissioningHintText != originalHint);
        OpenVisionLanguageService.SetLanguage(originalLanguage, save: false);

        await scrollIntoView();
        return new SmokeCameraCommissioningReport
        {
            Checks = checks,
            Failures = failures
        };
    }

    public static async Task ApplyStateAsync(
        ShellWindow window,
        MainViewModel viewModel,
        string state,
        SmokeUiInteraction interaction)
    {
        if (!viewModel.IsRunMode)
        {
            throw new ArgumentException(
                "--smoke-camera-commissioning-state requires --smoke-run-layout.");
        }

        async Task WaitForAsync(Func<bool> condition, string failureMessage)
        {
            for (var attempt = 0; attempt < 120; attempt++)
            {
                if (condition())
                {
                    return;
                }
                await Task.Delay(50);
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            }
            throw new InvalidOperationException(failureMessage);
        }

        async Task StepAsync()
        {
            var before = viewModel.SceneSnapshots.Latest!.TickIndex;
            viewModel.StepCommand.Execute(null);
            await WaitForAsync(
                () => viewModel.SceneSnapshots.Latest?.TickIndex == before + 1,
                "Camera smoke Step did not advance one tick.");
        }

        await WaitForAsync(
            () => viewModel.SceneSnapshots.Latest?.Cameras.Count > 0,
            "No virtual camera was published for the smoke state.");
        viewModel.ResetCommand.Execute(null);
        await WaitForAsync(
            () => !viewModel.IsRunning
                && viewModel.SceneSnapshots.Latest?.Cameras[0].State == VirtualCameraState.Idle,
            "Camera smoke state could not reset the runtime.");

        if (state == "source-invalid")
        {
            viewModel.CameraImageSourceEditor.PixelFormatText = string.Empty;
        }
        else if (state is "source-focus" or "source-hover-apply" or "source-pressed-apply")
        {
            viewModel.CameraImageSourceEditor.PixelFormatText = "Mono8";
            viewModel.CameraImageSourceEditor.Width += 1;
        }

        bool needsManual = state is "manual" or "exposing" or "transferring" or "frame-ready"
            or "focus-trigger" or "hover-trigger" or "pressed-trigger";
        if (needsManual)
        {
            viewModel.StartManualCameraControlCommand.Execute(null);
            await WaitForAsync(
                () => viewModel.IsRunning
                    && viewModel.SceneSnapshots.Latest?.ControlOwner == SimulationControlOwner.Manual,
                "Manual camera control did not start for the smoke state.");
            viewModel.PauseCommand.Execute(null);
            await WaitForAsync(() => !viewModel.IsRunning, "Manual camera control did not pause.");
        }

        int requestedSteps = state switch
        {
            "transferring" => 4,
            "frame-ready" => 10,
            _ => 0
        };
        if (state is "exposing" or "transferring" or "frame-ready")
        {
            viewModel.TriggerCameraCommand.Execute(null);
            await WaitForAsync(
                () => viewModel.SceneSnapshots.Latest?.Cameras[0].State == VirtualCameraState.Exposing,
                "Camera did not enter Exposing for the smoke state.");
            for (var index = 0; index < requestedSteps; index++)
            {
                await StepAsync();
            }
        }

        await ScrollIntoViewAsync(window);
        var inspector = SmokeVisualTreeQuery.FindVisualDescendant<RightToolRegionView>(window)
            ?? throw new InvalidOperationException("Run inspector was unavailable.");
        if (state == "frame-ready")
        {
            inspector.CameraExecutionEvidenceDetailsTextBlock.BringIntoView();
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            await Task.Delay(100);
        }
        if (state == "focus-start")
        {
            inspector.StartCameraManualControlButton.Focus();
        }
        else if (state is "hover-start" or "pressed-start")
        {
            interaction.MovePointerToCenter(inspector.StartCameraManualControlButton);
            if (state == "pressed-start")
            {
                interaction.MouseEvent(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
                interaction.MarkSmokePointerHeld();
            }
        }
        else if (state == "focus-trigger")
        {
            inspector.TriggerCameraButton.Focus();
        }
        else if (state is "hover-trigger" or "pressed-trigger")
        {
            interaction.MovePointerToCenter(inspector.TriggerCameraButton);
            if (state == "pressed-trigger")
            {
                interaction.MouseEvent(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
                interaction.MarkSmokePointerHeld();
            }
        }
        else if (state is "popup-camera" or "popup-recipe")
        {
            var comboBox = state == "popup-camera"
                ? inspector.CameraSelectionComboBox
                : inspector.CameraRecipeComboBox;
            interaction.ActivateWindow();
            comboBox.Focus();
            comboBox.ApplyTemplate();
            comboBox.IsDropDownOpen = true;
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            if (!comboBox.IsDropDownOpen)
            {
                throw new InvalidOperationException("Camera commissioning popup did not open.");
            }
            var windowRoot = PresentationSource.FromVisual(window)?.RootVisual;
            interaction.SetPopupContent(PresentationSource.CurrentSources
                .Cast<PresentationSource>()
                .Select(source => source.RootVisual)
                .OfType<FrameworkElement>()
                .FirstOrDefault(root =>
                    !ReferenceEquals(root, windowRoot)
                    && root.IsVisible
                    && root.ActualWidth > 0
                    && root.ActualHeight > 0)
                ?? throw new InvalidOperationException(
                    "Camera commissioning popup content was unavailable."));
        }
        else if (state is "source-focus" or "source-invalid")
        {
            interaction.ActivateWindow();
            inspector.CameraSourcePixelFormatTextBox.Focus();
        }
        else if (state is "source-hover-browse" or "source-pressed-browse")
        {
            interaction.MovePointerToCenter(inspector.BrowseCameraSourceButton);
            if (state == "source-pressed-browse")
            {
                interaction.MouseEvent(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
                interaction.MarkSmokePointerHeld();
            }
        }
        else if (state is "source-hover-apply" or "source-pressed-apply")
        {
            interaction.MovePointerToCenter(inspector.ApplyCameraSourceButton);
            if (state == "source-pressed-apply")
            {
                interaction.MouseEvent(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
                interaction.MarkSmokePointerHeld();
            }
        }
        else if (state is not "ready" and not "manual" and not "exposing"
                 and not "transferring" and not "frame-ready"
                 and not "source-hover-browse" and not "source-pressed-browse")
        {
            throw new ArgumentException(
                $"Unsupported --smoke-camera-commissioning-state '{state}'.");
        }

        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        await Task.Delay(150);
    }

    public static async Task ScrollIntoViewAsync(ShellWindow window)
    {
        var inspector = SmokeVisualTreeQuery.FindVisualDescendant<RightToolRegionView>(window)
            ?? throw new InvalidOperationException("Run inspector was unavailable.");
        var scrollViewer = inspector.RunInspectorScrollViewer;
        var targetPosition = inspector.CameraSectionAnchor.TranslatePoint(new Point(), scrollViewer);
        scrollViewer.ScrollToVerticalOffset(
            scrollViewer.VerticalOffset + targetPosition.Y - 8);
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
    }
}
