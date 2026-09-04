using System.IO;
using System.Text.Json;
using System.Windows.Input;
using OpenVisionLab.Machine.Core.Layouts;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.MachineStudio.ViewModel;
using static OpenVisionLab.MachineStudio.SmokeRoundTripScenario;

namespace OpenVisionLab.MachineStudio;

internal sealed class SmokeLayoutHistoryReport
{
    public string Schema { get; init; } = "1.0";
    public DateTimeOffset CapturedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public required IReadOnlyDictionary<string, bool> Checks { get; init; }
    public required IReadOnlyList<string> PastedComponentIds { get; init; }
    public required IReadOnlyList<string> Failures { get; init; }
    public bool IsValid => Failures.Count == 0 && Checks.Values.All(value => value);

    public void Save(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(
            fullPath,
            JsonSerializer.Serialize(this, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = true
            }));
    }
}


internal static class SmokeLayoutHistoryVerifier
{
    public static async Task<SmokeLayoutHistoryReport> VerifyAsync(
        MainViewModel viewModel,
        string reportPath)
    {
        var checks = new Dictionary<string, bool>(StringComparer.Ordinal);
        var failures = new List<string>();
        var pastedComponentIds = Array.Empty<string>();

        void Check(string name, bool passed)
        {
            checks[name] = passed;
            if (!passed)
            {
                failures.Add(name);
            }
        }

        static void Execute(ICommand command, object? parameter = null)
        {
            if (!command.CanExecute(parameter))
            {
                throw new InvalidOperationException("Expected layout edit command was disabled.");
            }
            command.Execute(parameter);
        }

        viewModel.Layout.Select(RoundTripCylinderId);
        var originalEditor = viewModel.Layout.SelectedComponentEditor
            ?? throw new InvalidOperationException("Cylinder property editor was not available.");
        var originalName = originalEditor.Name;
        var originalRotation = originalEditor.RotationDegrees;
        var originalWidth = originalEditor.Width;
        var originalHeight = originalEditor.Height;
        var originalStroke = originalEditor.CylinderStroke;

        originalEditor.Name = "History Cylinder";
        originalEditor.RotationDegrees = originalRotation + 15;
        originalEditor.Width = originalWidth + 10;
        originalEditor.Height = originalHeight + 8;
        originalEditor.CylinderStroke = originalStroke + 5;
        for (var index = 0; index < 5; index++)
        {
            Execute(viewModel.UndoLayoutEditCommand);
        }

        var undoneEditor = viewModel.Layout.SelectedComponentEditor
            ?? throw new InvalidOperationException("Undo did not restore the property editor.");
        Check(
            "propertyUndo",
            undoneEditor.Name == originalName &&
            undoneEditor.RotationDegrees == originalRotation &&
            undoneEditor.Width == originalWidth &&
            undoneEditor.Height == originalHeight &&
            undoneEditor.CylinderStroke == originalStroke);

        for (var index = 0; index < 5; index++)
        {
            Execute(viewModel.RedoLayoutEditCommand);
        }
        var redoneEditor = viewModel.Layout.SelectedComponentEditor
            ?? throw new InvalidOperationException("Redo did not restore the property editor.");
        Check(
            "propertyRedo",
            redoneEditor.Name == "History Cylinder" &&
            redoneEditor.RotationDegrees == originalRotation + 15 &&
            redoneEditor.Width == originalWidth + 10 &&
            redoneEditor.Height == originalHeight + 8 &&
            redoneEditor.CylinderStroke == originalStroke + 5);

        Execute(viewModel.UndoLayoutEditCommand);
        var branchEditor = viewModel.Layout.SelectedComponentEditor
            ?? throw new InvalidOperationException("Undo did not restore the branch editor.");
        branchEditor.CylinderStroke = originalStroke + 7;
        Check("newEditClearsRedo", !viewModel.RedoLayoutEditCommand.CanExecute(null));

        var moveIds = new[] { RoundTripStageId, RoundTripCylinderId };
        viewModel.Layout.SelectMany(moveIds, RoundTripCylinderId);
        var beforeMove = viewModel.Layout.SelectedItems.ToDictionary(
            item => item.Id,
            item => (item.CurrentX, item.CurrentY),
            StringComparer.Ordinal);
        Execute(viewModel.NudgeLayoutComponentCommand, "Right");
        var step = viewModel.Layout.GridSize;
        Check(
            "groupMoveApplied",
            viewModel.Layout.SelectedItems.All(item =>
                item.CurrentX == beforeMove[item.Id].CurrentX + step &&
                item.CurrentY == beforeMove[item.Id].CurrentY));
        Execute(viewModel.UndoLayoutEditCommand);
        Check(
            "groupMoveUndo",
            viewModel.Layout.SelectedItems.All(item =>
                item.CurrentX == beforeMove[item.Id].CurrentX &&
                item.CurrentY == beforeMove[item.Id].CurrentY));
        Execute(viewModel.RedoLayoutEditCommand);
        Check(
            "groupMoveRedo",
            viewModel.Layout.SelectedItems.All(item =>
                item.CurrentX == beforeMove[item.Id].CurrentX + step &&
                item.CurrentY == beforeMove[item.Id].CurrentY));

        var alignIds = new[] { RoundTripAlignedComponentId, RoundTripCylinderId };
        viewModel.Layout.SelectMany(alignIds, RoundTripCylinderId);
        var beforeAlignX = viewModel.Layout.Items.Single(item => item.Id == RoundTripAlignedComponentId).CurrentX;
        Execute(viewModel.AlignLayoutSelectionCommand, nameof(LayoutSelectionAlignment.HorizontalCenter));
        var alignedX = viewModel.Layout.Items.Single(item => item.Id == RoundTripAlignedComponentId).CurrentX;
        Check("alignmentApplied", alignedX != beforeAlignX);
        Execute(viewModel.UndoLayoutEditCommand);
        Check(
            "alignmentUndo",
            viewModel.Layout.Items.Single(item => item.Id == RoundTripAlignedComponentId).CurrentX == beforeAlignX);
        Execute(viewModel.RedoLayoutEditCommand);
        Check(
            "alignmentRedo",
            viewModel.Layout.Items.Single(item => item.Id == RoundTripAlignedComponentId).CurrentX == alignedX);

        var initialComponentCount = viewModel.Layout.Items.Count;
        Execute(viewModel.AddLayoutComponentCommand, LayoutComponentKind.MachineFrame);
        var addedFrameId = viewModel.Layout.SelectedItem?.Id;
        Check("addApplied", viewModel.Layout.Items.Count == initialComponentCount + 1 && addedFrameId is not null);
        Execute(viewModel.UndoLayoutEditCommand);
        Check("addUndo", viewModel.Layout.Items.Count == initialComponentCount);
        Execute(viewModel.RedoLayoutEditCommand);
        Check("addRedo", viewModel.Layout.Items.Count == initialComponentCount + 1);

        viewModel.Layout.Select(addedFrameId!);
        Execute(viewModel.DeleteLayoutComponentCommand);
        Check("deleteApplied", viewModel.Layout.Items.Count == initialComponentCount);
        Execute(viewModel.UndoLayoutEditCommand);
        Check("deleteUndo", viewModel.Layout.Items.Count == initialComponentCount + 1);
        Execute(viewModel.RedoLayoutEditCommand);
        Check("deleteRedo", viewModel.Layout.Items.Count == initialComponentCount);

        var copyIds = new[]
        {
            "stage-1",
            "sensor-1",
            "sensor-home",
            "cylinder-1",
            "conveyor-1",
            "workpiece-1"
        };
        viewModel.Layout.SelectMany(copyIds, "workpiece-1");
        Execute(viewModel.CopyLayoutSelectionCommand);
        Execute(viewModel.PasteLayoutSelectionCommand);
        pastedComponentIds = viewModel.Layout.SelectedItems.Select(item => item.Id).ToArray();
        var pastedCount = viewModel.Layout.Items.Count;
        Check("multiPasteApplied", pastedComponentIds.Length == copyIds.Length && pastedCount == initialComponentCount + copyIds.Length);

        var reportDirectory = Path.GetDirectoryName(Path.GetFullPath(reportPath))!;
        Directory.CreateDirectory(reportDirectory);
        var projectPath = Path.Combine(reportDirectory, "layout-history-roundtrip.ovmachine");
        await viewModel.SaveProjectAsync(projectPath);
        var savedProject = await new ProjectDocumentStore().LoadAsync(projectPath);
        var validation = new MachineProjectLayoutValidator().Validate(savedProject);
        Check("pastedProjectValid", validation.IsValid);
        var projectStore = new ProjectDocumentStore();
        var invalidPasteTarget = projectStore.Load(projectStore.Serialize(savedProject));
        var invalidPasteLayout = invalidPasteTarget.Layouts.Single(layout =>
            layout.Id == invalidPasteTarget.Simulation.ActiveLayoutId);
        invalidPasteLayout.Components[1].Id = invalidPasteLayout.Components[0].Id;
        var invalidPasteCounts = (
            invalidPasteLayout.Components.Count,
            invalidPasteTarget.Axes.Count,
            invalidPasteTarget.Devices.Count,
            invalidPasteTarget.Channels.Count);
        var atomicClipboard = new LayoutComponentClipboard();
        atomicClipboard.Copy(
            savedProject,
            savedProject.Layouts.Single(layout => layout.Id == savedProject.Simulation.ActiveLayoutId),
            new[] { copyIds[0] });
        var failedPaste = atomicClipboard.Paste(invalidPasteTarget, invalidPasteLayout);
        Check(
            "failedPasteIsAtomic",
            !failedPaste.IsSuccess &&
            invalidPasteCounts == (
                invalidPasteLayout.Components.Count,
                invalidPasteTarget.Axes.Count,
                invalidPasteTarget.Devices.Count,
                invalidPasteTarget.Channels.Count));
        Check(
            "uniqueDefinitionIds",
            savedProject.Layouts.SelectMany(layout => layout.Components).Select(component => component.Id).Distinct(StringComparer.Ordinal).Count() ==
                savedProject.Layouts.SelectMany(layout => layout.Components).Count() &&
            savedProject.Axes.Select(axis => axis.Id).Distinct(StringComparer.Ordinal).Count() == savedProject.Axes.Count &&
            savedProject.Devices.Select(device => device.Id).Distinct(StringComparer.Ordinal).Count() == savedProject.Devices.Count &&
            savedProject.Channels.Select(channel => channel.Id).Distinct(StringComparer.Ordinal).Count() == savedProject.Channels.Count);

        var pastedComponents = savedProject.Layouts
            .SelectMany(layout => layout.Components)
            .Where(component => pastedComponentIds.Contains(component.Id, StringComparer.Ordinal))
            .ToArray();
        var pastedConveyor = pastedComponents.Single(component => component.Kind == LayoutComponentKind.Conveyor);
        var pastedWorkpiece = pastedComponents.Single(component => component.Kind == LayoutComponentKind.Workpiece);
        var pastedWorkpieceDevice = savedProject.Devices.Single(device => device.Id == pastedWorkpiece.BehaviorBindingId);
        var pastedSensors = pastedComponents.Where(component => component.Kind == LayoutComponentKind.DigitalSensor).ToArray();
        Check(
            "internalBindingGraphRemapped",
            pastedWorkpieceDevice.Workpiece?.ConveyorComponentId == pastedConveyor.Id &&
            pastedSensors.All(component =>
                savedProject.Devices.Single(device => device.Id == component.BehaviorBindingId)
                    .Sensor?.TargetComponentId == pastedWorkpiece.Id));

        var pastedCylinderBindingId = pastedComponents
            .Single(component => component.Kind == LayoutComponentKind.PneumaticCylinder)
            .BehaviorBindingId;
        viewModel.Layout.Select("cylinder-1");
        var bindingEditor = viewModel.Layout.SelectedComponentEditor!;
        var originalBindingId = bindingEditor.BehaviorBindingId;
        bindingEditor.BehaviorBindingId = pastedCylinderBindingId;
        Execute(viewModel.UndoLayoutEditCommand);
        Check("behaviorBindingUndo", viewModel.Layout.SelectedComponentEditor?.BehaviorBindingId == originalBindingId);
        Execute(viewModel.RedoLayoutEditCommand);
        Check("behaviorBindingRedo", viewModel.Layout.SelectedComponentEditor?.BehaviorBindingId == pastedCylinderBindingId);
        Execute(viewModel.UndoLayoutEditCommand);

        viewModel.Layout.Select("sensor-1");
        var sensorEditor = viewModel.Layout.SelectedComponentEditor!;
        var originalSensorDelay = sensorEditor.SensorOnDelayMilliseconds;
        sensorEditor.SensorOnDelayMilliseconds = originalSensorDelay + 3;
        Execute(viewModel.UndoLayoutEditCommand);
        Check("sensorPropertyUndo", viewModel.Layout.SelectedComponentEditor?.SensorOnDelayMilliseconds == originalSensorDelay);
        Execute(viewModel.RedoLayoutEditCommand);
        Check("sensorPropertyRedo", viewModel.Layout.SelectedComponentEditor?.SensorOnDelayMilliseconds == originalSensorDelay + 3);
        Execute(viewModel.UndoLayoutEditCommand);

        viewModel.Layout.Select("conveyor-1");
        var conveyorEditor = viewModel.Layout.SelectedComponentEditor!;
        var originalConveyorSpeed = conveyorEditor.ConveyorSpeedUnitsPerSecond;
        conveyorEditor.ConveyorSpeedUnitsPerSecond = originalConveyorSpeed + 10;
        Execute(viewModel.UndoLayoutEditCommand);
        Check("conveyorPropertyUndo", viewModel.Layout.SelectedComponentEditor?.ConveyorSpeedUnitsPerSecond == originalConveyorSpeed);
        Execute(viewModel.RedoLayoutEditCommand);
        Check("conveyorPropertyRedo", viewModel.Layout.SelectedComponentEditor?.ConveyorSpeedUnitsPerSecond == originalConveyorSpeed + 10);
        Execute(viewModel.UndoLayoutEditCommand);

        viewModel.Layout.Select("workpiece-1");
        var workpieceEditor = viewModel.Layout.SelectedComponentEditor!;
        var originalWorkpieceType = workpieceEditor.WorkpieceType;
        workpieceEditor.WorkpieceType = "History Part";
        Execute(viewModel.UndoLayoutEditCommand);
        Check("workpiecePropertyUndo", viewModel.Layout.SelectedComponentEditor?.WorkpieceType == originalWorkpieceType);
        Execute(viewModel.RedoLayoutEditCommand);
        Check("workpiecePropertyRedo", viewModel.Layout.SelectedComponentEditor?.WorkpieceType == "History Part");
        Execute(viewModel.UndoLayoutEditCommand);

        Execute(viewModel.UndoLayoutEditCommand);
        Check("pasteUndo", viewModel.Layout.Items.Count == initialComponentCount);
        Execute(viewModel.RedoLayoutEditCommand);
        Check("pasteRedo", viewModel.Layout.Items.Count == pastedCount);

        await viewModel.SaveProjectAsync(projectPath);
        Check("roundTripOpen", await viewModel.OpenProjectAsync(projectPath));
        Check(
            "historyAndClipboardNotPersisted",
            !viewModel.UndoLayoutEditCommand.CanExecute(null) &&
            !viewModel.RedoLayoutEditCommand.CanExecute(null) &&
            !viewModel.PasteLayoutSelectionCommand.CanExecute(null));
        Check("reopenDoesNotRun", viewModel.IsDesignMode && !viewModel.IsRunning);

        return new SmokeLayoutHistoryReport
        {
            Checks = checks,
            PastedComponentIds = pastedComponentIds,
            Failures = failures
        };
    }
}
