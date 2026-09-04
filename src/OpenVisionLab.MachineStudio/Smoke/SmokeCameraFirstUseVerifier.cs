using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using OpenVisionLab;
using OpenVisionLab.Machine.Core.Devices;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Core.Sequences;
using OpenVisionLab.Machine.Simulation.Camera;
using OpenVisionLab.Machine.Simulation.Compilation;
using OpenVisionLab.Machine.Simulation.Commissioning;
using OpenVisionLab.Machine.Simulation.Engine;
using OpenVisionLab.Machine.Simulation.Snapshots;
using OpenVisionLab.MachineStudio.View.Project;
using OpenVisionLab.MachineStudio.View.Shell;
using OpenVisionLab.MachineStudio.ViewModel;

namespace OpenVisionLab.MachineStudio;

internal sealed class SmokeCameraFirstUseReport
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

internal static class SmokeCameraFirstUseVerifier
{
    private const uint MouseEventMove = 0x0001;
    private const uint MouseEventLeftDown = 0x0002;

    public static async Task<SmokeCameraFirstUseReport> VerifyAsync(
        ShellWindow window,
        MainViewModel viewModel,
        string state,
        string? savePath,
        Func<DependencyObject, RecipeConnectionWorkbenchView?> findWorkbench,
        Func<DependencyObject, Func<Button, bool>, Button?> findButton,
        Func<DependencyObject, Func<Border, bool>, Border?> findBorder,
        Action activateWindow,
        Action<FrameworkElement> movePointerToCenter,
        Action<uint, uint, uint, uint, UIntPtr> mouseEvent,
        Action markSmokePointerHeld,
        Action<FrameworkElement?> setSmokePopupContent,
        Action releaseSmokePointer)
    {
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

        static MachineProjectDocument CurrentProject(MainViewModel owner) =>
            owner.ProjectTree.Roots.Single().Model as MachineProjectDocument
            ?? throw new InvalidOperationException("The current machine project was unavailable.");

        static bool HasNoAcquisition(SimulationSnapshot snapshot) =>
            snapshot.Cameras.All(camera =>
                camera.State == VirtualCameraState.Idle
                && camera.AcquisitionOrdinal == 0
                && camera.CurrentAcquisitionId is null
                && camera.CurrentRecipeId is null
                && camera.Result is null
                && camera.FrameEvidence is null);

        static bool HasExactInspectionGraph(MachineProjectDocument project)
        {
            var cameras = project.Devices
                .Where(device => device.Kind == DeviceKind.Camera)
                .ToArray();
            if (cameras.Length != 1 || project.Sequences.Count != 1)
            {
                return false;
            }

            var camera = cameras[0];
            var sequence = project.Sequences[0];
            if (sequence.Steps.Count != 4)
            {
                return false;
            }

            var triggerSteps = sequence.Steps
                .Where(step => step.Action == SequenceStepAction.TriggerCamera)
                .ToArray();
            var waitSteps = sequence.Steps
                .Where(step => step.Action == SequenceStepAction.WaitVisionResult)
                .ToArray();
            var terminalSteps = sequence.Steps
                .Where(step => step.Action == SequenceStepAction.Complete)
                .ToArray();
            if (triggerSteps.Length != 1 || waitSteps.Length != 1 || terminalSteps.Length != 2)
            {
                return false;
            }

            var trigger = triggerSteps[0];
            var wait = waitSteps[0];
            var pass = terminalSteps.FirstOrDefault(step =>
                string.Equals(step.Id, wait.NextStepId, StringComparison.Ordinal));
            var fail = terminalSteps.FirstOrDefault(step =>
                string.Equals(step.Id, wait.FailureStepId, StringComparison.Ordinal));
            return camera.Camera is
                {
                    ExposureDelayMilliseconds: 20,
                    TransferDelayMilliseconds: 30,
                    PlaceholderDecision: PlaceholderInspectionDecision.Pass,
                    SingleImageSource: null
                }
                && string.Equals(trigger.TargetId, camera.Id, StringComparison.Ordinal)
                && string.Equals(trigger.Parameter, "default", StringComparison.Ordinal)
                && trigger.TimeoutMs == 0
                && string.Equals(trigger.NextStepId, wait.Id, StringComparison.Ordinal)
                && string.Equals(trigger.ErrorStepId, fail?.Id, StringComparison.Ordinal)
                && trigger.FailureStepId is null
                && string.Equals(wait.TargetId, camera.Id, StringComparison.Ordinal)
                && string.IsNullOrEmpty(wait.Parameter)
                && wait.TimeoutMs == 1000
                && pass is not null
                && fail is not null
                && !ReferenceEquals(pass, fail)
                && string.Equals(wait.ErrorStepId, fail.Id, StringComparison.Ordinal)
                && terminalSteps.All(step =>
                    step.NextStepId is null
                    && step.ErrorStepId is null
                    && step.FailureStepId is null);
        }

        viewModel.SelectedDocumentTabIndex = 1;
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        await Task.Delay(100);

        var workbench = findWorkbench(window)
            ?? throw new InvalidOperationException("The connection workbench was unavailable.");
        var button = findButton(workbench, candidate =>
                string.Equals(
                    candidate.Name,
                    "CreateVirtualCameraWorkflowButton",
                    StringComparison.Ordinal))
            ?? throw new InvalidOperationException(
                "The virtual-camera first-use command was unavailable.");
        var buttonBorder = findBorder(button, candidate =>
                string.Equals(candidate.Name, "ButtonBorder", StringComparison.Ordinal))
            ?? throw new InvalidOperationException(
                "The virtual-camera first-use command template border was unavailable.");
        var toolTip = button.ToolTip as ToolTip
            ?? throw new InvalidOperationException(
                "The virtual-camera first-use command tooltip was unavailable.");
        var toolTipText = toolTip.Content as TextBlock
            ?? throw new InvalidOperationException(
                "The virtual-camera first-use tooltip text was unavailable.");
        var normalBackground = (buttonBorder.Background as SolidColorBrush)?.Color;
        var normalBorderThickness = buttonBorder.BorderThickness;
        var blankProject = CurrentProject(viewModel);
        var store = new ProjectDocumentStore();
        var blankEvidence = store.SerializeForEvidence(blankProject);
        var snapshotBefore = viewModel.SceneSnapshots.Latest
            ?? throw new InvalidOperationException("The initial runtime snapshot was unavailable.");

        Check("blank-project-has-no-camera", blankProject.Devices.All(device =>
            device.Kind != DeviceKind.Camera));
        Check("blank-project-has-no-sequence", blankProject.Sequences.Count == 0);
        Check("first-use-command-visible", button.IsVisible && button.ActualWidth > 0 && button.ActualHeight > 0);
        Check("first-use-command-enabled", button.IsEnabled
            && viewModel.RecipeConnections.CanCreateVirtualCameraWorkflow
            && viewModel.RecipeConnections.CreateVirtualCameraWorkflowCommand.CanExecute(null));
        Check("first-use-automation-name-localized", string.Equals(
            System.Windows.Automation.AutomationProperties.GetName(button),
            OpenVisionLanguageService.T("Connections.CreateVirtualCameraWorkflow"),
            StringComparison.Ordinal));
        Check("first-use-tooltip-localized", string.Equals(
            toolTipText.Text,
            OpenVisionLanguageService.T("Connections.CreateVirtualCameraWorkflowTooltip"),
            StringComparison.Ordinal));
        Check("first-use-starts-in-design-stopped", viewModel.IsDesignMode
            && !viewModel.IsRunning
            && snapshotBefore.RunMode == SimulationRunMode.Paused);
        Check("first-use-starts-without-acquisition", HasNoAcquisition(snapshotBefore));

        var appliesWorkflow = state.Equals("applied", StringComparison.OrdinalIgnoreCase)
            || state.Equals("keyboard-space", StringComparison.OrdinalIgnoreCase);
        switch (state.ToLowerInvariant())
        {
            case "normal":
                Check("normal-state-not-pressed", !button.IsPressed);
                break;
            case "focus":
                activateWindow();
                button.Focus();
                Keyboard.Focus(button);
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                Check("focus-state-keyboard-focus", button.IsKeyboardFocused);
                Check("focus-state-visible-cue",
                    buttonBorder.BorderThickness.Left > normalBorderThickness.Left);
                break;
            case "hover":
                activateWindow();
                movePointerToCenter(button);
                for (var attempt = 0; attempt < 20 && !toolTip.IsOpen; attempt++)
                {
                    await Task.Delay(100);
                    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                }
                Check("hover-state-pointer-over", button.IsMouseOver);
                Check("hover-state-visible-cue",
                    (buttonBorder.Background as SolidColorBrush)?.Color != normalBackground
                    && buttonBorder.BorderThickness.Left > normalBorderThickness.Left);
                Check("hover-state-tooltip-open", toolTip.IsOpen);
                var smokePopupContent = PresentationSource.FromVisual(toolTip)?.RootVisual
                    as FrameworkElement;
                setSmokePopupContent(smokePopupContent);
                Check("hover-state-tooltip-renderable", smokePopupContent is
                {
                    IsVisible: true,
                    ActualWidth: > 0,
                    ActualHeight: > 0
                });
                break;
            case "pressed":
                activateWindow();
                button.Focus();
                movePointerToCenter(button);
                for (var attempt = 0; attempt < 20 && !button.IsMouseOver; attempt++)
                {
                    await Task.Delay(50);
                    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                }
                mouseEvent(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
                markSmokePointerHeld();
                for (var attempt = 0; attempt < 10 && !button.IsPressed; attempt++)
                {
                    await Task.Delay(50);
                    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                }
                Check("pressed-state-pointer-over", button.IsMouseOver);
                Check("pressed-state-pointer-down", button.IsPressed);
                Check("pressed-state-visible-cue",
                    button.Opacity < 1
                    && buttonBorder.BorderThickness.Left > normalBorderThickness.Left);
                break;
            case "mouse-leave":
                var cancelTarget = findButton(workbench, candidate =>
                        string.Equals(candidate.Name, "AddConnectionStageButton", StringComparison.Ordinal))
                    ?? throw new InvalidOperationException(
                        "The mouse-leave cancellation target was unavailable.");
                window.Topmost = true;
                activateWindow();
                button.BringIntoView();
                button.UpdateLayout();
                for (var attempt = 0; attempt < 20 && !button.IsMouseOver; attempt++)
                {
                    movePointerToCenter(button);
                    mouseEvent(MouseEventMove, 1, 0, 0, UIntPtr.Zero);
                    await Task.Delay(50);
                    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                }
                Check("mouse-leave-entered-hover", button.IsMouseOver);
                for (var attempt = 0; attempt < 20 && button.IsMouseOver; attempt++)
                {
                    movePointerToCenter(cancelTarget);
                    mouseEvent(MouseEventMove, 1, 0, 0, UIntPtr.Zero);
                    await Task.Delay(50);
                    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                }
                Check("mouse-leave-exited-hit-region", !button.IsMouseOver);
                Check("mouse-leave-recovered", !button.IsPressed
                    && viewModel.RecipeConnections.CreateVirtualCameraWorkflowCommand.CanExecute(null)
                    && button.Opacity == 1
                    && buttonBorder.BorderThickness == normalBorderThickness
                    && (buttonBorder.Background as SolidColorBrush)?.Color == normalBackground);
                break;
            case "applied":
                window.Topmost = true;
                activateWindow();
                button.Focus();
                button.BringIntoView();
                button.UpdateLayout();
                for (var attempt = 0; attempt < 20 && !button.IsMouseOver; attempt++)
                {
                    movePointerToCenter(button);
                    mouseEvent(MouseEventMove, 1, 0, 0, UIntPtr.Zero);
                    await Task.Delay(50);
                    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                }
                Check("apply-pointer-over", button.IsMouseOver);
                mouseEvent(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
                markSmokePointerHeld();
                for (var attempt = 0; attempt < 10 && !button.IsPressed; attempt++)
                {
                    await Task.Delay(50);
                    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                }
                Check("apply-pointer-down", button.IsPressed);
                releaseSmokePointer();
                break;
            case "keyboard-space":
                activateWindow();
                button.Focus();
                Keyboard.Focus(button);
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                Check("keyboard-space-focused", button.IsKeyboardFocused);
                var inputSource = PresentationSource.FromVisual(button)
                    ?? throw new InvalidOperationException(
                        "The virtual-camera first-use command had no presentation source.");
                button.RaiseEvent(new KeyEventArgs(
                    Keyboard.PrimaryDevice,
                    inputSource,
                    Environment.TickCount,
                    Key.Space)
                {
                    RoutedEvent = Keyboard.KeyDownEvent
                });
                button.RaiseEvent(new KeyEventArgs(
                    Keyboard.PrimaryDevice,
                    inputSource,
                    Environment.TickCount,
                    Key.Space)
                {
                    RoutedEvent = Keyboard.KeyUpEvent
                });
                break;
            default:
                throw new ArgumentException(
                    $"Unsupported --smoke-camera-first-use-state '{state}'. " +
                    "Expected normal, focus, hover, pressed, mouse-leave, applied, or keyboard-space.");
        }

        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        await Task.Delay(appliesWorkflow ? 250 : 100);

        if (appliesWorkflow)
        {
            for (var attempt = 0;
                 attempt < 50
                 && CurrentProject(viewModel).Devices.All(device => device.Kind != DeviceKind.Camera);
                 attempt++)
            {
                await Task.Delay(100);
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            }
        }

        if (!appliesWorkflow)
        {
            var snapshotAfterVisualState = viewModel.SceneSnapshots.Latest
                ?? throw new InvalidOperationException("The visual-state runtime snapshot was unavailable.");
            Check("visual-state-project-unchanged",
                blankEvidence == store.SerializeForEvidence(CurrentProject(viewModel)));
            Check("visual-state-runtime-unchanged",
                snapshotAfterVisualState.TickIndex == snapshotBefore.TickIndex
                && snapshotAfterVisualState.SimulationTime == snapshotBefore.SimulationTime
                && snapshotAfterVisualState.RunMode == snapshotBefore.RunMode
                && HasNoAcquisition(snapshotAfterVisualState)
                && !viewModel.IsRunning
                && viewModel.IsDesignMode);
            return new SmokeCameraFirstUseReport
            {
                Checks = checks,
                Failures = failures
            };
        }

        var authoredProject = CurrentProject(viewModel);
        var authoredCameras = authoredProject.Devices
            .Where(device => device.Kind == DeviceKind.Camera)
            .ToArray();
        var authoredSequence = authoredProject.Sequences.SingleOrDefault();
        var authoredTrigger = authoredSequence?.Steps.SingleOrDefault(step =>
            step.Action == SequenceStepAction.TriggerCamera);
        var snapshotAfterApply = viewModel.SceneSnapshots.Latest
            ?? throw new InvalidOperationException("The post-authoring runtime snapshot was unavailable.");
        Check("one-camera-authored", authoredCameras.Length == 1);
        Check("default-recipe-authored", string.Equals(
            authoredTrigger?.Parameter,
            "default",
            StringComparison.Ordinal));
        Check("exact-four-step-graph-authored", HasExactInspectionGraph(authoredProject));
        Check("authored-project-compiles", new MachineProjectRuntimeCompiler(
                TimeSpan.FromMilliseconds(authoredProject.Simulation.FixedStepMilliseconds))
            .Compile(authoredProject).IsSuccess);
        Check("camera-and-recipe-selected", authoredCameras.Length == 1
            && string.Equals(viewModel.SelectedCameraId, authoredCameras[0].Id, StringComparison.Ordinal)
            && string.Equals(viewModel.SelectedCameraRecipe, "default", StringComparison.Ordinal)
            && viewModel.CurrentCameraRecipes.SequenceEqual(["default"], StringComparer.Ordinal));
        Check("trigger-step-opened", authoredSequence is not null
            && authoredTrigger is not null
            && viewModel.SelectedDocumentTabIndex == 2
            && string.Equals(
                viewModel.SequenceEditor.SelectedSequence?.Id,
                authoredSequence.Id,
                StringComparison.Ordinal)
            && string.Equals(
                viewModel.SequenceEditor.SelectedStep?.Id,
                authoredTrigger.Id,
                StringComparison.Ordinal));
        Check("runtime-debugger-catalog-refreshed", authoredSequence is not null
            && viewModel.RuntimeDebugger.Breakpoints
                .Select(item => (item.SequenceId, item.StepId))
                .SequenceEqual(
                    authoredSequence.Steps.Select(step => (authoredSequence.Id, step.Id))));
        Check("authored-project-dirty", viewModel.HasUnsavedChanges);
        Check("authoring-keeps-design-stopped", viewModel.IsDesignMode && !viewModel.IsRunning);
        Check("authoring-runtime-unchanged",
            snapshotAfterApply.TickIndex == snapshotBefore.TickIndex
            && snapshotAfterApply.SimulationTime == snapshotBefore.SimulationTime
            && snapshotAfterApply.RunMode == snapshotBefore.RunMode
            && HasNoAcquisition(snapshotAfterApply));
        Check("second-invocation-unavailable",
            !viewModel.RecipeConnections.CanCreateVirtualCameraWorkflow
            && !viewModel.RecipeConnections.CreateVirtualCameraWorkflowCommand.CanExecute(null)
            && !button.IsEnabled
            && !button.IsVisible);

        var authoredEvidence = store.SerializeForEvidence(authoredProject);
        var cameraCountBeforeSecondInvocation = authoredProject.Devices.Count(device =>
            device.Kind == DeviceKind.Camera);
        var sequenceCountBeforeSecondInvocation = authoredProject.Sequences.Count;
        if (HasExactInspectionGraph(authoredProject))
        {
            viewModel.RecipeConnections.CreateVirtualCameraWorkflowCommand.Execute(null);
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var projectAfterSecondInvocation = CurrentProject(viewModel);
            Check("second-invocation-no-duplicate",
                projectAfterSecondInvocation.Devices.Count(device => device.Kind == DeviceKind.Camera)
                    == cameraCountBeforeSecondInvocation
                && projectAfterSecondInvocation.Sequences.Count == sequenceCountBeforeSecondInvocation
                && authoredEvidence == store.SerializeForEvidence(projectAfterSecondInvocation));
        }
        else
        {
            Check("second-invocation-no-duplicate", false);
        }

        var fullSavePath = Path.GetFullPath(savePath!);
        Directory.CreateDirectory(Path.GetDirectoryName(fullSavePath)!);
        await viewModel.SaveProjectAsync(fullSavePath);
        Check("first-use-project-saved", File.Exists(fullSavePath) && !viewModel.HasUnsavedChanges);
        var savedProject = await store.LoadAsync(fullSavePath);
        Check("saved-state-exact", HasExactInspectionGraph(savedProject)
            && authoredEvidence == store.SerializeForEvidence(savedProject));
        Check("saved-project-compiles", new MachineProjectRuntimeCompiler(
                TimeSpan.FromMilliseconds(savedProject.Simulation.FixedStepMilliseconds))
            .Compile(savedProject).IsSuccess);

        Check("saved-project-reopens", await viewModel.OpenProjectAsync(fullSavePath));
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        await Task.Delay(100);
        var reopenedProject = CurrentProject(viewModel);
        var reopenedCamera = reopenedProject.Devices.SingleOrDefault(device =>
            device.Kind == DeviceKind.Camera);
        var reopenedSequence = reopenedProject.Sequences.SingleOrDefault();
        var reopenedTrigger = reopenedSequence?.Steps.SingleOrDefault(step =>
            step.Action == SequenceStepAction.TriggerCamera);
        var reopenedSnapshot = viewModel.SceneSnapshots.Latest
            ?? throw new InvalidOperationException("The reopened runtime snapshot was unavailable.");
        Check("reopen-restores-authored-state", HasExactInspectionGraph(reopenedProject)
            && authoredEvidence == store.SerializeForEvidence(reopenedProject));
        Check("reopen-restores-selection", reopenedCamera is not null
            && reopenedSequence is not null
            && reopenedTrigger is not null
            && string.Equals(viewModel.SelectedCameraId, reopenedCamera.Id, StringComparison.Ordinal)
            && string.Equals(viewModel.SelectedCameraRecipe, "default", StringComparison.Ordinal)
            && string.Equals(
                viewModel.SequenceEditor.SelectedSequence?.Id,
                reopenedSequence.Id,
                StringComparison.Ordinal)
            && string.Equals(
                viewModel.SequenceEditor.SelectedStep?.Id,
                reopenedTrigger.Id,
                StringComparison.Ordinal));
        Check("reopen-causes-no-execution", viewModel.IsDesignMode
            && !viewModel.IsRunning
            && reopenedSnapshot.RunMode == SimulationRunMode.Paused
            && reopenedSnapshot.TickIndex == snapshotBefore.TickIndex
            && reopenedSnapshot.SimulationTime == snapshotBefore.SimulationTime
            && reopenedSnapshot.Cameras.Count == 1
            && HasNoAcquisition(reopenedSnapshot));
        Check("reopen-keeps-second-invocation-unavailable",
            !viewModel.RecipeConnections.CanCreateVirtualCameraWorkflow
            && !viewModel.RecipeConnections.CreateVirtualCameraWorkflowCommand.CanExecute(null));

        return new SmokeCameraFirstUseReport
        {
            Checks = checks,
            Failures = failures
        };
    }
}
