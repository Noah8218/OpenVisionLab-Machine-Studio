using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using OpenVisionLab.Machine.Simulation.Commands;
using OpenVisionLab.Machine.Simulation.Commissioning;
using OpenVisionLab.Machine.Simulation.Engine;
using OpenVisionLab.Machine.Simulation.Scenarios;
using OpenVisionLab.MachineStudio.View.Project;
using OpenVisionLab.MachineStudio.View.Shell;
using OpenVisionLab.MachineStudio.ViewModel;

namespace OpenVisionLab.MachineStudio;

internal static class SmokeRuntimeEvidenceVerifier
{
    private const uint MouseEventMove = 0x0001;
    private const uint MouseEventLeftDown = 0x0002;

    public static async Task VerifyCommandTraceAsync(
        ShellWindow window,
        MainViewModel viewModel,
        string? tracePath,
        string state,
        SmokeUiInteraction interaction)
    {
        var normalizedState = state.ToLowerInvariant();
        var sectionAnchor = interaction.FindTextBlock(
            window,
            candidate => string.Equals(
                candidate.Name,
                "SimulationCommandTraceSectionAnchor",
                StringComparison.Ordinal))
            ?? throw new InvalidOperationException("Simulation command trace section was not available.");
        sectionAnchor.BringIntoView();
        window.UpdateLayout();
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);

        var startButton = interaction.FindButton(
            window,
            candidate => ReferenceEquals(
                candidate.Command,
                viewModel.StartSimulationCommandTraceCaptureCommand))
            ?? throw new InvalidOperationException("Simulation command trace Start capture button was not available.");
        var exportButton = interaction.FindButton(
            window,
            candidate => ReferenceEquals(
                candidate.Command,
                viewModel.ExportSimulationCommandTraceCommand))
            ?? throw new InvalidOperationException("Simulation command trace Export button was not available.");
        var replayButton = interaction.FindButton(
            window,
            candidate => ReferenceEquals(
                candidate.Command,
                viewModel.ReplaySimulationCommandTraceCommand))
            ?? throw new InvalidOperationException("Simulation command trace Replay button was not available.");
        var buttons = new[] { startButton, exportButton, replayButton };
        if (buttons.Any(button => !button.IsVisible))
        {
            throw new InvalidOperationException("Simulation command trace controls were not visible.");
        }

        async Task WaitForAsync(Func<bool> condition, string failureMessage)
        {
            for (var attempt = 0; attempt < 120 && !condition(); attempt++)
            {
                await Task.Delay(25);
            }

            AssertSmoke(condition(), failureMessage);
        }

        async Task MovePointerToButtonAsync(Button button)
        {
            interaction.ActivateWindow();
            button.BringIntoView();
            window.UpdateLayout();
            button.UpdateLayout();
            for (var attempt = 0; attempt < 20 && !button.IsMouseOver; attempt++)
            {
                interaction.MovePointerToCenter(button);
                interaction.MouseEvent(MouseEventMove, 1, 0, 0, UIntPtr.Zero);
                await Task.Delay(50);
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            }

            AssertSmoke(button.IsMouseOver, "Command trace button did not enter hover state.");
        }

        if (normalizedState is "focus" or "hover" or "pressed")
        {
            viewModel.StartSimulationCommandTraceCaptureCommand.Execute(null);
            viewModel.ResetCommand.Execute(null);
            await WaitForAsync(
                () => viewModel.SimulationCommandTraceEntryCount == 1,
                "Command trace visual-state preparation did not capture the reset boundary.");
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        }

        switch (normalizedState)
        {
            case "normal":
                AssertSmoke(
                    startButton.IsEnabled,
                    "Start capture was not enabled in the paused run state.");
                AssertSmoke(
                    !exportButton.IsEnabled,
                    "Export trace was enabled before an explicit capture boundary.");
                AssertSmoke(
                    replayButton.IsEnabled,
                    "Replay trace was not enabled in the paused run state.");
                break;
            case "focus":
                foreach (var button in buttons)
                {
                    interaction.ActivateWindow();
                    button.Focus();
                    Keyboard.Focus(button);
                    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    AssertSmoke(button.IsKeyboardFocused, "Command trace button did not receive keyboard focus.");
                }
                break;
            case "hover":
                foreach (var button in buttons)
                {
                    await MovePointerToButtonAsync(button);
                }
                interaction.SetCursorPosition(0, 0);
                Mouse.Synchronize();
                await Task.Delay(50);
                AssertSmoke(!replayButton.IsMouseOver, "Command trace button did not recover after mouse leave.");
                break;
            case "pressed":
                window.Activate();
                var outsidePoint = window.PointToScreen(new Point(8, 8));
                foreach (var button in buttons)
                {
                    button.Focus();
                    await MovePointerToButtonAsync(button);
                    interaction.MouseEvent(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
                    interaction.MarkSmokePointerHeld();
                    for (var attempt = 0; attempt < 10 && !button.IsPressed; attempt++)
                    {
                        await Task.Delay(50);
                        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    }
                    AssertSmoke(button.IsPressed, "Command trace button did not enter pointer-down state.");
                    interaction.SetCursorPosition((int)Math.Round(outsidePoint.X), (int)Math.Round(outsidePoint.Y));
                    Mouse.Synchronize();
                    interaction.ReleaseSmokePointer();
                    Mouse.Capture(null);
                }
                break;
            case "disabled":
                AssertSmoke(viewModel.RunCommand.CanExecute(null), "Run command was unavailable for disabled-state smoke.");
                viewModel.RunCommand.Execute(null);
                await WaitForAsync(() => viewModel.IsRunning, "The runtime did not enter the disabled-state path.");
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                foreach (var button in buttons)
                {
                    AssertSmoke(!button.IsEnabled, "Command trace button remained enabled while simulation was running.");
                }
                viewModel.PauseCommand.Execute(null);
                await WaitForAsync(() => !viewModel.IsRunning, "The runtime did not leave the disabled-state path.");
                replayButton.BringIntoView();
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                Console.WriteLine("Simulation command trace visual state passed: disabled while running.");
                return;
            default:
                throw new ArgumentException(
                    $"Unsupported --smoke-command-trace-state '{state}'. " +
                    "Expected normal, focus, hover, pressed, or disabled.");
        }

        if (!normalizedState.Equals("normal", StringComparison.OrdinalIgnoreCase))
        {
            replayButton.BringIntoView();
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Console.WriteLine($"Simulation command trace visual state passed: {state}.");
            return;
        }

        var fullTracePath = Path.GetFullPath(
            tracePath ?? throw new ArgumentException("Normal command-trace smoke requires a trace path."));
        Directory.CreateDirectory(Path.GetDirectoryName(fullTracePath)!);
        var beforeDirty = viewModel.HasUnsavedChanges;
        viewModel.StartSimulationCommandTraceCaptureCommand.Execute(null);
        AssertSmoke(
            viewModel.SimulationCommandTraceEntryCount == 0,
            "Start capture did not clear the prior in-memory trace.");
        viewModel.ResetCommand.Execute(null);
        await WaitForAsync(
            () => viewModel.SimulationCommandTraceEntryCount == 1,
            "Explicit command trace capture did not record the reset boundary.");
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        AssertSmoke(
            viewModel.ExportSimulationCommandTraceCommand.CanExecute(fullTracePath),
            "Export trace did not become enabled after capture.");
        viewModel.ExportSimulationCommandTraceCommand.Execute(fullTracePath);
        AssertSmoke(File.Exists(fullTracePath), "Command trace export did not create its file.");

        var package = DeterministicSimulationCommandTracePackage.LoadFromJson(fullTracePath)
            ?? throw new InvalidOperationException("Exported command trace could not be loaded.");
        AssertSmoke(package.HasValidTraceHash(), "Exported command trace hash was invalid.");
        AssertSmoke(package.CanReplay, "Exported command trace was not replayable.");
        AssertSmoke(package.Entries.Length == 1, "Exported command trace contained an unexpected boundary count.");
        AssertSmoke(
            package.Entries[0].CommandType == nameof(ResetCommand),
            "Exported command trace did not retain the explicit reset boundary.");

        viewModel.ReplaySimulationCommandTraceCommand.Execute(fullTracePath);
        await WaitForAsync(
            () => viewModel.LastSimulationCommandTraceReplaySucceeded,
            "Command trace replay did not complete successfully.");
        AssertSmoke(!viewModel.IsRunning, "Command trace replay changed the runtime to a running state.");
        AssertSmoke(
            viewModel.HasUnsavedChanges == beforeDirty,
            "Command trace capture/replay changed authored project dirty state.");
        replayButton.BringIntoView();
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Console.WriteLine(
            $"Simulation command trace smoke passed: capture/export/replay, " +
            $"{package.Entries.Length} boundary, hash {package.TraceHash[..8]}, no implicit run or project mutation.");
    }

    public static async Task VerifyScenarioEvidenceExchangeAsync(
        ShellWindow window,
        MainViewModel viewModel,
        string exchangePath,
        string state,
        string? projectPath,
        SmokeUiInteraction interaction)
    {
        var normalizedState = state.ToLowerInvariant();
        var sectionAnchor = interaction.FindTextBlock(
            window,
            candidate => string.Equals(
                candidate.Name,
                "RepeatValidationSectionAnchor",
                StringComparison.Ordinal))
            ?? throw new InvalidOperationException("Repeat validation section was not available.");
        sectionAnchor.BringIntoView();
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);

        var exportButton = interaction.FindButton(
            window,
            candidate => ReferenceEquals(candidate.Command, viewModel.ExportSimulationEvidenceCommand))
            ?? throw new InvalidOperationException("Simulation evidence Export button was not available.");
        var importButton = interaction.FindButton(
            window,
            candidate => ReferenceEquals(candidate.Command, viewModel.ImportSimulationEvidenceCommand))
            ?? throw new InvalidOperationException("Simulation evidence Import button was not available.");
        exportButton.BringIntoView();
        importButton.BringIntoView();
        exportButton.UpdateLayout();
        importButton.UpdateLayout();
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);

        if (normalizedState == "disabled")
        {
            viewModel.SimulationWorkspace.BatchRepetitionCount = 3;
            viewModel.SimulationWorkspace.ScenarioDurationCycles = 100_000;
            for (var attempt = 0; attempt < 40 && !viewModel.RunScenarioBatchCommand.CanExecute(null); attempt++)
            {
                await Task.Delay(50);
            }

            if (!viewModel.RunScenarioBatchCommand.CanExecute(null))
            {
                throw new InvalidOperationException("A batch was unavailable for evidence button disabled-state smoke.");
            }

            viewModel.RunScenarioBatchCommand.Execute(null);
            for (var attempt = 0; attempt < 40 && !viewModel.IsBatchRunning; attempt++)
            {
                await Task.Delay(25);
            }

            if (!viewModel.IsBatchRunning)
            {
                throw new InvalidOperationException("Evidence button disabled-state batch did not start.");
            }

            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            AssertSmoke(!exportButton.IsEnabled, "Export evidence remained enabled while batch validation was running.");
            AssertSmoke(!importButton.IsEnabled, "Import evidence remained enabled while batch validation was running.");
            viewModel.CancelScenarioBatchCommand.Execute(null);
            for (var attempt = 0; attempt < 80 && viewModel.IsBatchRunning; attempt++)
            {
                await Task.Delay(25);
            }

            if (viewModel.IsBatchRunning)
            {
                throw new InvalidOperationException("Evidence button disabled-state batch did not cancel.");
            }

            viewModel.SimulationWorkspace.BatchRepetitionCount = 2;
            viewModel.SimulationWorkspace.ScenarioDurationCycles = 1200;
            for (var attempt = 0; attempt < 40 && !viewModel.RunScenarioBatchCommand.CanExecute(null); attempt++)
            {
                await Task.Delay(50);
            }

            viewModel.RunScenarioBatchCommand.Execute(null);
            for (var attempt = 0;
                 attempt < 120
                 && (viewModel.IsBatchRunning
                     || viewModel.LatestBatchResult is not { IsComplete: true, IsSuccess: true, CompletedRuns: 2 });
                 attempt++)
            {
                await Task.Delay(25);
            }

            if (viewModel.LatestBatchResult is not { IsComplete: true, IsSuccess: true, CompletedRuns: 2 })
            {
                throw new InvalidOperationException(
                    "Evidence button disabled-state smoke did not restore the deterministic batch result.");
            }

            Console.WriteLine("Scenario evidence exchange visual state passed: disabled during batch.");
            return;
        }

        if (!exportButton.IsVisible || !importButton.IsVisible)
        {
            throw new InvalidOperationException("Simulation evidence controls were not visible.");
        }

        async Task MovePointerToButtonAsync(Button button)
        {
            interaction.ActivateWindow();
            button.BringIntoView();
            button.UpdateLayout();
            for (var attempt = 0; attempt < 20 && !button.IsMouseOver; attempt++)
            {
                interaction.MovePointerToCenter(button);
                interaction.MouseEvent(MouseEventMove, 1, 0, 0, UIntPtr.Zero);
                await Task.Delay(50);
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                if (interaction.CheckPointerOwnership(window).IsOwned && button.IsMouseOver)
                {
                    break;
                }
            }

            var pointerOwnership = interaction.CheckPointerOwnership(window);
            AssertSmoke(
                pointerOwnership.IsOwned,
                $"Simulation evidence pointer was not owned by the target Machine Studio window: " +
                pointerOwnership.Diagnostic);
            AssertSmoke(
                button.IsMouseOver,
                $"Simulation evidence button did not enter hover state: Name={button.Name}, " +
                $"Content={button.Content}, Visible={button.IsVisible}, Enabled={button.IsEnabled}, " +
                $"Actual={button.ActualWidth:F1}x{button.ActualHeight:F1}.");
        }

        switch (normalizedState)
        {
            case "normal":
                AssertSmoke(
                    exportButton.IsEnabled && importButton.IsEnabled,
                    $"Simulation evidence controls were not enabled in normal state. " +
                    $"ExportButton={exportButton.IsEnabled}, ImportButton={importButton.IsEnabled}, " +
                    $"CanExport={viewModel.CanExportSimulationEvidence}, CanImport={viewModel.CanImportSimulationEvidence}, " +
                    $"IsRunMode={viewModel.IsRunMode}, IsRunning={viewModel.IsRunning}, " +
                    $"IsBatchRunning={viewModel.IsBatchRunning}, Target={viewModel.SimulationWorkspace.ScenarioTargetId ?? "<null>"}.");
                break;
            case "focus":
                window.Activate();
                exportButton.Focus();
                Keyboard.Focus(exportButton);
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                AssertSmoke(exportButton.IsKeyboardFocused, "Export evidence button did not receive keyboard focus.");
                importButton.Focus();
                Keyboard.Focus(importButton);
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                AssertSmoke(importButton.IsKeyboardFocused, "Import evidence button did not receive keyboard focus.");
                break;
            case "hover":
                await MovePointerToButtonAsync(exportButton);
                await MovePointerToButtonAsync(importButton);
                interaction.SetCursorPosition(0, 0);
                Mouse.Synchronize();
                await Task.Delay(50);
                AssertSmoke(!importButton.IsMouseOver, "Import evidence button did not recover after mouse leave.");
                break;
            case "pressed":
                window.Activate();
                exportButton.Focus();
                interaction.MovePointerToCenter(exportButton);
                await Task.Delay(100);
                interaction.MouseEvent(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
                interaction.MarkSmokePointerHeld();
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                AssertSmoke(exportButton.IsPressed, "Export evidence button did not enter pointer-down state.");
                var outsidePoint = window.PointToScreen(new Point(8, 8));
                interaction.SetCursorPosition((int)Math.Round(outsidePoint.X), (int)Math.Round(outsidePoint.Y));
                Mouse.Synchronize();
                interaction.ReleaseSmokePointer();
                Mouse.Capture(null);
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                interaction.ActivateWindow();
                importButton.Focus();
                importButton.BringIntoView();
                importButton.UpdateLayout();
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                interaction.MovePointerToCenter(importButton);
                await Task.Delay(100);
                interaction.MouseEvent(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
                interaction.MarkSmokePointerHeld();
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                AssertSmoke(importButton.IsPressed, "Import evidence button did not enter pointer-down state.");
                interaction.SetCursorPosition((int)Math.Round(outsidePoint.X), (int)Math.Round(outsidePoint.Y));
                Mouse.Synchronize();
                interaction.ReleaseSmokePointer();
                Mouse.Capture(null);
                break;
            default:
                throw new ArgumentException(
                    $"Unsupported --smoke-scenario-evidence-state '{state}'. " +
                    "Expected normal, focus, hover, pressed, or disabled.");
        }

        if (!normalizedState.Equals("normal", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine($"Scenario evidence exchange visual state passed: {state}.");
            return;
        }

        var fullExchangePath = Path.GetFullPath(exchangePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullExchangePath)!);
        var beforeTick = viewModel.SceneSnapshots.Latest?.TickIndex ?? -1;
        var beforeDirty = viewModel.HasUnsavedChanges;
        var beforeAlarmHistoryCount = viewModel.RuntimeDebugger.AlarmHistory.Count;
        var exportedEvidenceHash = viewModel.LatestBatchResult?.EvidenceHash
            ?? throw new InvalidOperationException("No completed batch evidence was available for export.");

        viewModel.ExportSimulationEvidenceCommand.Execute(fullExchangePath);
        if (!File.Exists(fullExchangePath))
        {
            throw new InvalidOperationException("Simulation evidence export did not create its exchange file.");
        }

        var exportedJson = File.ReadAllText(fullExchangePath);
        var sourceFileName = Path.GetFileName(projectPath ?? string.Empty);
        if (!string.IsNullOrWhiteSpace(sourceFileName))
        {
            AssertSmoke(
                !exportedJson.Contains(sourceFileName, StringComparison.Ordinal),
                "Simulation evidence exchange unexpectedly contained the source project filename.");
        }
        AssertSmoke(
            !exportedJson.Contains("RuntimeDebugger", StringComparison.Ordinal)
            && !exportedJson.Contains("acknowledg", StringComparison.OrdinalIgnoreCase),
            "Simulation evidence exchange unexpectedly contained debugger session acknowledgement state.");

        viewModel.ClearBatchBaselineCommand.Execute(null);
        if (viewModel.HasAcceptedBatchBaseline)
        {
            throw new InvalidOperationException("The baseline did not clear before the import check.");
        }

        var baselineSidecarPath = string.IsNullOrWhiteSpace(projectPath)
            ? null
            : $"{Path.GetFullPath(projectPath)}.batch-baseline.json";
        viewModel.ImportSimulationEvidenceCommand.Execute(fullExchangePath);
        if (!viewModel.HasAcceptedBatchBaseline
            || viewModel.LatestBatchResult?.EvidenceHash != exportedEvidenceHash
            || viewModel.IsRunning
            || viewModel.HasUnsavedChanges != beforeDirty
            || viewModel.SceneSnapshots.Latest?.TickIndex != beforeTick
            || viewModel.RuntimeDebugger.AlarmHistory.Count != beforeAlarmHistoryCount
            || (baselineSidecarPath is not null && File.Exists(baselineSidecarPath)))
        {
            throw new InvalidOperationException(
                "Simulation evidence import changed runtime, authored dirty state, debugger session state, or sidecar state.");
        }

        Console.WriteLine(
            $"Scenario evidence exchange smoke passed: export/import, hash {exportedEvidenceHash[..8]}, " +
            "no execution, acknowledgement, or sidecar persistence.");
    }

    public static async Task VerifyUnifiedCommissioningEvidenceAsync(
        ShellWindow window,
        MainViewModel viewModel,
        string bundlePath,
        string state,
        string? projectPath,
        SmokeUiInteraction interaction)
    {
        var normalizedState = state.ToLowerInvariant();
        var sectionAnchor = interaction.FindTextBlock(
            window,
            candidate => string.Equals(
                candidate.Name,
                "UnifiedCommissioningEvidenceSectionAnchor",
                StringComparison.Ordinal))
            ?? throw new InvalidOperationException("Unified commissioning evidence section was not available.");
        sectionAnchor.BringIntoView();
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);

        var exportButton = interaction.FindButton(
            window,
            candidate => ReferenceEquals(
                candidate.Command,
                viewModel.ExportUnifiedCommissioningEvidenceCommand))
            ?? throw new InvalidOperationException("Unified commissioning evidence Export button was not available.");
        var importButton = interaction.FindButton(
            window,
            candidate => ReferenceEquals(
                candidate.Command,
                viewModel.ImportUnifiedCommissioningEvidenceCommand))
            ?? throw new InvalidOperationException("Unified commissioning evidence Import button was not available.");
        var buttons = new[] { exportButton, importButton };
        if (buttons.Any(button => !button.IsVisible))
        {
            throw new InvalidOperationException("Unified commissioning evidence controls were not visible.");
        }

        async Task WaitForAsync(Func<bool> condition, string failureMessage)
        {
            for (var attempt = 0; attempt < 120 && !condition(); attempt++)
            {
                await Task.Delay(25);
            }

            AssertSmoke(condition(), failureMessage);
        }

        async Task MovePointerToButtonAsync(Button button)
        {
            interaction.ActivateWindow();
            button.BringIntoView();
            button.UpdateLayout();
            for (var attempt = 0; attempt < 20 && !button.IsMouseOver; attempt++)
            {
                interaction.MovePointerToCenter(button);
                interaction.MouseEvent(MouseEventMove, 1, 0, 0, UIntPtr.Zero);
                await Task.Delay(50);
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                if (interaction.CheckPointerOwnership(window).IsOwned && button.IsMouseOver)
                {
                    break;
                }
            }

            var pointerOwnership = interaction.CheckPointerOwnership(window);
            AssertSmoke(
                pointerOwnership.IsOwned,
                $"Unified commissioning evidence pointer was not owned by the target Machine Studio window: " +
                pointerOwnership.Diagnostic);
            AssertSmoke(
                button.IsMouseOver,
                $"Unified commissioning evidence button did not enter hover state: " +
                $"Name={button.Name}, Content={button.Content}, Visible={button.IsVisible}, " +
                $"Enabled={button.IsEnabled}, Actual={button.ActualWidth:F1}x{button.ActualHeight:F1}, " +
                $"Screen={button.PointToScreen(new Point(button.ActualWidth / 2, button.ActualHeight / 2))}.");
        }

        if (normalizedState is "focus" or "hover" or "pressed" or "normal")
        {
            viewModel.StartSimulationCommandTraceCaptureCommand.Execute(null);
            viewModel.ResetCommand.Execute(null);
            await WaitForAsync(
                () => viewModel.SimulationCommandTraceEntryCount == 1,
                "Unified commissioning evidence preparation did not capture the reset boundary.");
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        }

        switch (normalizedState)
        {
            case "normal":
                AssertSmoke(
                    exportButton.IsEnabled && importButton.IsEnabled,
                    $"Unified commissioning evidence controls were not enabled in normal state. " +
                    $"ExportButton={exportButton.IsEnabled}, ImportButton={importButton.IsEnabled}, " +
                    $"CanExport={viewModel.CanExportUnifiedCommissioningEvidence}, " +
                    $"CanImport={viewModel.CanImportUnifiedCommissioningEvidence}.");
                break;
            case "focus":
                foreach (var button in buttons)
                {
                    window.Activate();
                    button.Focus();
                    Keyboard.Focus(button);
                    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    AssertSmoke(
                        button.IsKeyboardFocused,
                        "Unified commissioning evidence button did not receive keyboard focus.");
                }
                break;
            case "hover":
                foreach (var button in buttons)
                {
                    await MovePointerToButtonAsync(button);
                }
                interaction.SetCursorPosition(0, 0);
                Mouse.Synchronize();
                await Task.Delay(50);
                AssertSmoke(!importButton.IsMouseOver, "Unified commissioning evidence button did not recover after mouse leave.");
                break;
            case "pressed":
                window.Activate();
                var outsidePoint = window.PointToScreen(new Point(8, 8));
                foreach (var button in buttons)
                {
                    button.Focus();
                    await MovePointerToButtonAsync(button);
                    interaction.MouseEvent(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
                    interaction.MarkSmokePointerHeld();
                    for (var attempt = 0; attempt < 10 && !button.IsPressed; attempt++)
                    {
                        await Task.Delay(50);
                        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    }
                    AssertSmoke(button.IsPressed, "Unified commissioning evidence button did not enter pointer-down state.");
                    interaction.SetCursorPosition((int)Math.Round(outsidePoint.X), (int)Math.Round(outsidePoint.Y));
                    Mouse.Synchronize();
                    interaction.ReleaseSmokePointer();
                    Mouse.Capture(null);
                }
                break;
            case "disabled":
                AssertSmoke(viewModel.RunCommand.CanExecute(null), "Run command was unavailable for unified evidence disabled-state smoke.");
                viewModel.RunCommand.Execute(null);
                await WaitForAsync(() => viewModel.IsRunning, "The runtime did not enter the unified evidence disabled-state path.");
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                foreach (var button in buttons)
                {
                    AssertSmoke(!button.IsEnabled, "Unified commissioning evidence button remained enabled while simulation was running.");
                }
                viewModel.PauseCommand.Execute(null);
                await WaitForAsync(() => !viewModel.IsRunning, "The runtime did not leave the unified evidence disabled-state path.");
                sectionAnchor.BringIntoView();
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                Console.WriteLine("Unified commissioning evidence visual state passed: disabled while running.");
                return;
            default:
                throw new ArgumentException(
                    $"Unsupported --smoke-unified-evidence-state '{state}'. " +
                    "Expected normal, focus, hover, pressed, or disabled.");
        }

        if (!normalizedState.Equals("normal", StringComparison.OrdinalIgnoreCase))
        {
            sectionAnchor.BringIntoView();
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Console.WriteLine($"Unified commissioning evidence visual state passed: {state}.");
            return;
        }

        var fullBundlePath = Path.GetFullPath(bundlePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullBundlePath)!);
        var beforeTick = viewModel.SceneSnapshots.Latest?.TickIndex ?? -1;
        var beforeDirty = viewModel.HasUnsavedChanges;
        var beforeAlarmHistoryCount = viewModel.RuntimeDebugger.AlarmHistory.Count;
        var beforeProjectSidecars = projectPath is null
            ? []
            : new[]
            {
                $"{Path.GetFullPath(projectPath)}.batch-result.json",
                $"{Path.GetFullPath(projectPath)}.batch-baseline.json",
                $"{Path.GetFullPath(projectPath)}.vision-result.json"
            };

        viewModel.ExportUnifiedCommissioningEvidenceCommand.Execute(fullBundlePath);
        AssertSmoke(File.Exists(fullBundlePath), "Unified commissioning evidence export did not create its file.");
        var exportedJson = File.ReadAllText(fullBundlePath);
        var sourceFileName = Path.GetFileName(projectPath ?? string.Empty);
        if (!string.IsNullOrWhiteSpace(sourceFileName))
        {
            AssertSmoke(
                !exportedJson.Contains(sourceFileName, StringComparison.Ordinal),
                "Unified commissioning evidence unexpectedly contained the source project filename.");
        }
        AssertSmoke(
            !exportedJson.Contains("RuntimeDebugger", StringComparison.Ordinal)
                && !exportedJson.Contains("acknowledg", StringComparison.OrdinalIgnoreCase),
            "Unified commissioning evidence unexpectedly contained debugger acknowledgement state.");

        var package = DeterministicUnifiedCommissioningEvidencePackage.LoadFromJson(fullBundlePath)
            ?? throw new InvalidOperationException("Exported unified commissioning evidence could not be loaded.");
        AssertSmoke(package.HasValidEvidenceHash(), "Exported unified commissioning evidence hash was invalid.");
        AssertSmoke(package.CommandTrace.Entries.Length == 1, "Unified commissioning evidence trace count was unexpected.");
        AssertSmoke(!package.ContainsNonReplayableVisionEvidence, "Unexpected Vision evidence was included in the sample bundle.");

        viewModel.ClearBatchBaselineCommand.Execute(null);
        AssertSmoke(!viewModel.HasAcceptedBatchBaseline, "The batch baseline did not clear before unified import.");
        viewModel.ImportUnifiedCommissioningEvidenceCommand.Execute(fullBundlePath);
        AssertSmoke(
            viewModel.HasAcceptedBatchBaseline
                && viewModel.LatestUnifiedCommissioningEvidence?.EvidenceHash == package.EvidenceHash
                && viewModel.LatestUnifiedCommissioningEvidence.CommandTrace.TraceHash == package.CommandTrace.TraceHash
                && !viewModel.LastSimulationCommandTraceReplaySucceeded
                && !viewModel.IsRunning
                && viewModel.HasUnsavedChanges == beforeDirty
                && viewModel.SceneSnapshots.Latest?.TickIndex == beforeTick
                && viewModel.RuntimeDebugger.AlarmHistory.Count == beforeAlarmHistoryCount,
            "Unified commissioning evidence import executed, dirtied, or changed runtime/debugger state.");
        AssertSmoke(
            beforeProjectSidecars.All(path => !File.Exists(path)),
            "Unified commissioning evidence import created a project-linked sidecar.");
        sectionAnchor.BringIntoView();
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Console.WriteLine(
            $"Unified commissioning evidence smoke passed: export/import, hash {package.EvidenceHash[..8]}, " +
            "trace retained without replay, no run/acquire/save/dirty/acknowledgement side effect.");
    }


    private static void AssertSmoke(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
