using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows.Controls;
using OpenVisionLab.MachineStudio.View.Shell;
using OpenVisionLab.MachineStudio.ViewModel;
using static OpenVisionLab.MachineStudio.SmokeProjectTreeQuery;
using static OpenVisionLab.MachineStudio.SmokeRoundTripScenario;
using static OpenVisionLab.MachineStudio.SmokeVisualTreeQuery;

namespace OpenVisionLab.MachineStudio;

internal sealed class SmokeProjectRoundTripReport
{
    public string Schema { get; init; } = "1.3";
    public DateTimeOffset CapturedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public required string Phase { get; init; }
    public required string ProjectPath { get; init; }
    public required double ExpectedStageX { get; init; }
    public required double ActualStageX { get; init; }
    public required string ExpectedStepName { get; init; }
    public required string ActualStepName { get; init; }
    public required string ExpectedStepCheckpointTargetId { get; init; }
    public required string ActualStepCheckpointTargetId { get; init; }
    public required string ExpectedStepCheckpointState { get; init; }
    public required string ActualStepCheckpointState { get; init; }
    public required string ExpectedComponentName { get; init; }
    public required string ActualComponentName { get; init; }
    public required double ExpectedComponentRotation { get; init; }
    public required double ActualComponentRotation { get; init; }
    public required double ExpectedComponentWidth { get; init; }
    public required double ActualComponentWidth { get; init; }
    public required double ExpectedComponentHeight { get; init; }
    public required double ActualComponentHeight { get; init; }
    public required int ExpectedCylinderExtendDuration { get; init; }
    public required int ActualCylinderExtendDuration { get; init; }
    public required double ExpectedCylinderStroke { get; init; }
    public required double ActualCylinderStroke { get; init; }
    public required double ExpectedAxisMaxVelocity { get; init; }
    public required double ActualAxisMaxVelocity { get; init; }
    public required double ExpectedAxisMaxAcceleration { get; init; }
    public required double ActualAxisMaxAcceleration { get; init; }
    public required double ExpectedAxisMaxDeceleration { get; init; }
    public required double ActualAxisMaxDeceleration { get; init; }
    public required double ExpectedAxisFollowingErrorLimit { get; init; }
    public required double ActualAxisFollowingErrorLimit { get; init; }
    public required double ExpectedAlignedComponentX { get; init; }
    public required double ActualAlignedComponentX { get; init; }
    public required bool IsDesignMode { get; init; }
    public required bool IsRunning { get; init; }
    public required string SimulationStatus { get; init; }
    public required string AxisState { get; init; }
    public required bool HasVirtualCamera { get; init; }
    public required string CameraState { get; init; }
    public required string SequenceState { get; init; }
    public required int ActiveFaultCount { get; init; }
    public required SmokeMonitorEvidence Monitor { get; init; }
    public required IReadOnlyList<string> Failures { get; init; }
    public bool IsValid => Failures.Count == 0;

    public void Save(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        };
        File.WriteAllText(fullPath, JsonSerializer.Serialize(this, options));
    }
}

internal static class SmokeProjectRoundTripVerifier
{
    public static SmokeProjectRoundTripReport CreateReport(
        string phase,
        string projectPath,
        ShellWindow window,
        MainViewModel viewModel)
    {
        var failures = new List<string>();
        var stage = viewModel.Layout.Items.FirstOrDefault(item =>
            string.Equals(item.Id, RoundTripStageId, StringComparison.Ordinal));
        var alignedItem = viewModel.Layout.Items.FirstOrDefault(item =>
            string.Equals(item.Id, RoundTripAlignedComponentId, StringComparison.Ordinal));
        var step = viewModel.SequenceEditor.Steps.FirstOrDefault(item =>
            string.Equals(item.Id, RoundTripStepId, StringComparison.Ordinal));
        SelectNode(viewModel.ProjectTree, "x");
        var axisEditor = viewModel.AxisDriveTuningEditor;
        window.UpdateLayout();
        if (axisEditor is null)
        {
            failures.Add("Axis drive tuning editor was not restored.");
        }
        else
        {
            CheckValue("Axis maximum velocity", axisEditor.MaxVelocity, RoundTripAxisMaxVelocity);
            CheckValue("Axis maximum acceleration", axisEditor.MaxAcceleration, RoundTripAxisMaxAcceleration);
            CheckValue("Axis maximum deceleration", axisEditor.MaxDeceleration, RoundTripAxisMaxDeceleration);
            CheckValue("Axis following-error limit", axisEditor.FollowingErrorLimit, RoundTripAxisFollowingErrorLimit);
            if (axisEditor.HasValidationErrors)
            {
                failures.Add($"Restored axis tuning was invalid: {axisEditor.ValidationMessage}");
            }

            var tuningPanel = FindVisualDescendant<Border>(
                window,
                element => string.Equals(element.Name, "AxisDriveTuningPanel", StringComparison.Ordinal));
            var visibleValues = tuningPanel is null
                ? Array.Empty<TextBox>()
                : FindVisualDescendants<TextBox>(tuningPanel)
                    .Where(textBox => textBox.IsVisible && !string.IsNullOrWhiteSpace(textBox.Text))
                    .ToArray();
            if (tuningPanel is null || !tuningPanel.IsVisible || visibleValues.Length < 4)
            {
                failures.Add("Axis drive tuning inputs did not render representative non-empty values.");
            }
        }

        foreach (var item in viewModel.Layout.Items.Where(item => item.Component is not null))
        {
            viewModel.Layout.Select(item.Id);
            var editor = viewModel.Layout.SelectedComponentEditor;
            if (editor is null)
            {
                failures.Add($"Layout property editor was unavailable for '{item.Id}'.");
                continue;
            }

            if (editor.HasValidationErrors)
            {
                failures.Add($"Layout property editor for '{item.Id}' was invalid: {editor.ValidationMessage}");
            }

            if (item.Component!.Kind != OpenVisionLab.Machine.Core.Layouts.LayoutComponentKind.MachineFrame &&
                editor.BehaviorBindingOptions.Count == 0)
            {
                failures.Add($"Layout property editor for '{item.Id}' had no compatible behavior binding.");
            }

            if (viewModel.Properties.Items.Any(property =>
                    property.Value.Contains("OpenVisionLab.", StringComparison.Ordinal)))
            {
                failures.Add($"Layout properties for '{item.Id}' exposed a CLR type name.");
            }
        }

        viewModel.Layout.Select(RoundTripCylinderId);
        var cylinderItem = viewModel.Layout.SelectedItem;
        var cylinderEditor = viewModel.Layout.SelectedComponentEditor;

        if (stage is null)
        {
            failures.Add($"Layout item '{RoundTripStageId}' was not restored.");
        }
        else if (Math.Abs(stage.CurrentX - RoundTripStageX) > 0.001)
        {
            failures.Add(
                $"Layout X was {stage.CurrentX:F3}; expected {RoundTripStageX:F3}.");
        }

        if (alignedItem is null)
        {
            failures.Add($"Layout item '{RoundTripAlignedComponentId}' was not restored.");
        }
        else if (Math.Abs(alignedItem.CurrentX - RoundTripAlignedComponentX) > 0.001)
        {
            failures.Add(
                $"Aligned component X was {alignedItem.CurrentX:F3}; " +
                $"expected {RoundTripAlignedComponentX:F3}.");
        }
        if (step is null)
        {
            failures.Add($"Sequence step '{RoundTripStepId}' was not restored.");
        }
        else if (!string.Equals(step.Name, RoundTripStepName, StringComparison.Ordinal))
        {
            failures.Add(
                $"Sequence step name was '{step.Name}'; expected '{RoundTripStepName}'.");
        }
        else
        {
            if (!string.Equals(
                    step.ExpectedTargetId,
                    RoundTripStepCheckpointTargetId,
                    StringComparison.Ordinal))
            {
                failures.Add(
                    $"Sequence checkpoint target was '{step.ExpectedTargetId}'; " +
                    $"expected '{RoundTripStepCheckpointTargetId}'.");
            }
            if (!string.Equals(
                    step.ExpectedState,
                    RoundTripStepCheckpointState,
                    StringComparison.Ordinal))
            {
                failures.Add(
                    $"Sequence checkpoint state was '{step.ExpectedState}'; " +
                    $"expected '{RoundTripStepCheckpointState}'.");
            }
        }

        if (cylinderItem is null || cylinderEditor is null)
        {
            failures.Add($"Layout item '{RoundTripCylinderId}' was not restored with an editor.");
        }
        else
        {
            if (!string.Equals(cylinderEditor.Name, RoundTripCylinderName, StringComparison.Ordinal))
            {
                failures.Add($"Component name was '{cylinderEditor.Name}'; expected '{RoundTripCylinderName}'.");
            }
            if (Math.Abs(cylinderEditor.RotationDegrees - RoundTripCylinderRotation) > 0.001)
            {
                failures.Add($"Component rotation was {cylinderEditor.RotationDegrees:F3}; expected {RoundTripCylinderRotation:F3}.");
            }
            if (Math.Abs(cylinderEditor.Width - RoundTripCylinderWidth) > 0.001 ||
                Math.Abs(cylinderEditor.Height - RoundTripCylinderHeight) > 0.001)
            {
                failures.Add(
                    $"Component size was {cylinderEditor.Width:F3} x {cylinderEditor.Height:F3}; " +
                    $"expected {RoundTripCylinderWidth:F3} x {RoundTripCylinderHeight:F3}.");
            }
            if (Math.Abs(cylinderEditor.CylinderExtendDurationMilliseconds - RoundTripCylinderExtendDuration) > 0.001)
            {
                failures.Add(
                    $"Cylinder extend duration was {cylinderEditor.CylinderExtendDurationMilliseconds:F0}; " +
                    $"expected {RoundTripCylinderExtendDuration}.");
            }
            if (Math.Abs(cylinderEditor.CylinderStroke - RoundTripCylinderStroke) > 0.001)
            {
                failures.Add(
                    $"Cylinder stroke was {cylinderEditor.CylinderStroke:F3}; expected {RoundTripCylinderStroke:F3}.");
            }
        }
        if (!viewModel.IsDesignMode)
        {
            failures.Add("Project restore changed the application out of Design mode.");
        }

        if (viewModel.IsRunning)
        {
            failures.Add("Project restore started the simulation.");
        }

        if (!viewModel.SimulationStatusText.EndsWith(
                "00:00:00.000",
                StringComparison.Ordinal))
        {
            failures.Add(
                $"Simulation time advanced during restore: {viewModel.SimulationStatusText}.");
        }

        if (!string.Equals(
                viewModel.CurrentAxisStateText,
                OpenVisionLanguageService.T("Equipment.State.Idle", "Idle", "Idle"),
                StringComparison.Ordinal))
        {
            failures.Add($"Axis state after restore was {viewModel.CurrentAxisStateText}.");
        }

        if (viewModel.HasVirtualCamera &&
            !string.Equals(
                viewModel.CurrentCameraStateText,
                OpenVisionLanguageService.T("Equipment.State.Idle", "Idle", "Idle"),
                StringComparison.Ordinal))
        {
            failures.Add($"Camera state after restore was {viewModel.CurrentCameraStateText}.");
        }

        if (!string.Equals(
                viewModel.CurrentSequenceStateText,
                OpenVisionLanguageService.T("Equipment.State.Ready", "Ready", "Ready"),
                StringComparison.Ordinal))
        {
            failures.Add($"Sequence state after restore was {viewModel.CurrentSequenceStateText}.");
        }

        if (viewModel.FaultManager.ActiveFaults.Count != 0)
        {
            failures.Add(
                $"Project restore retained {viewModel.FaultManager.ActiveFaults.Count} active fault(s).");
        }

        if (string.Equals(phase, "SaveReload", StringComparison.Ordinal))
        {
            if (!string.Equals(
                    viewModel.SimulationWorkspace.SelectedScenarioProfile.ProfileId,
                    RoundTripScenarioProfileId,
                    StringComparison.Ordinal))
            {
                failures.Add("Test Scenario profile was not restored.");
            }

            if (viewModel.SimulationWorkspace.ScenarioSeed != RoundTripScenarioSeed)
            {
                failures.Add(
                    $"Test Scenario seed was {viewModel.SimulationWorkspace.ScenarioSeed}; " +
                    $"expected {RoundTripScenarioSeed}.");
            }

            if (viewModel.SimulationWorkspace.ScenarioDurationCycles != RoundTripScenarioDuration)
            {
                failures.Add(
                    $"Test Scenario duration was {viewModel.SimulationWorkspace.ScenarioDurationCycles}; " +
                    $"expected {RoundTripScenarioDuration}.");
            }

            if (!string.Equals(
                    viewModel.SimulationWorkspace.ScenarioTargetId,
                    RoundTripScenarioTargetId,
                    StringComparison.Ordinal))
            {
                failures.Add("Test Scenario target was not restored.");
            }

            if (viewModel.ConditionScenario.IsConfigured || viewModel.ConditionScenario.IsActive)
            {
                failures.Add("Project restore configured or started a runtime Test Scenario.");
            }
        }

        return new SmokeProjectRoundTripReport
        {
            Phase = phase,
            ProjectPath = Path.GetFullPath(projectPath),
            ExpectedStageX = RoundTripStageX,
            ActualStageX = stage?.CurrentX ?? double.NaN,
            ExpectedStepName = RoundTripStepName,
            ActualStepName = step?.Name ?? string.Empty,
            ExpectedStepCheckpointTargetId = RoundTripStepCheckpointTargetId,
            ActualStepCheckpointTargetId = step?.ExpectedTargetId ?? string.Empty,
            ExpectedStepCheckpointState = RoundTripStepCheckpointState,
            ActualStepCheckpointState = step?.ExpectedState ?? string.Empty,
            ExpectedComponentName = RoundTripCylinderName,
            ActualComponentName = cylinderEditor?.Name ?? string.Empty,
            ExpectedComponentRotation = RoundTripCylinderRotation,
            ActualComponentRotation = cylinderEditor?.RotationDegrees ?? double.NaN,
            ExpectedComponentWidth = RoundTripCylinderWidth,
            ActualComponentWidth = cylinderEditor?.Width ?? double.NaN,
            ExpectedComponentHeight = RoundTripCylinderHeight,
            ActualComponentHeight = cylinderEditor?.Height ?? double.NaN,
            ExpectedCylinderExtendDuration = RoundTripCylinderExtendDuration,
            ActualCylinderExtendDuration = cylinderEditor is null
                ? int.MinValue
                : Convert.ToInt32(cylinderEditor.CylinderExtendDurationMilliseconds),
            ExpectedCylinderStroke = RoundTripCylinderStroke,
            ActualCylinderStroke = cylinderEditor?.CylinderStroke ?? double.NaN,
            ExpectedAxisMaxVelocity = RoundTripAxisMaxVelocity,
            ActualAxisMaxVelocity = axisEditor?.MaxVelocity ?? double.NaN,
            ExpectedAxisMaxAcceleration = RoundTripAxisMaxAcceleration,
            ActualAxisMaxAcceleration = axisEditor?.MaxAcceleration ?? double.NaN,
            ExpectedAxisMaxDeceleration = RoundTripAxisMaxDeceleration,
            ActualAxisMaxDeceleration = axisEditor?.MaxDeceleration ?? double.NaN,
            ExpectedAxisFollowingErrorLimit = RoundTripAxisFollowingErrorLimit,
            ActualAxisFollowingErrorLimit = axisEditor?.FollowingErrorLimit ?? double.NaN,
            ExpectedAlignedComponentX = RoundTripAlignedComponentX,
            ActualAlignedComponentX = alignedItem?.CurrentX ?? double.NaN,
            IsDesignMode = viewModel.IsDesignMode,
            IsRunning = viewModel.IsRunning,
            SimulationStatus = viewModel.SimulationStatusText,
            AxisState = viewModel.CurrentAxisStateText,
            HasVirtualCamera = viewModel.HasVirtualCamera,
            CameraState = viewModel.CurrentCameraStateText,
            SequenceState = viewModel.CurrentSequenceStateText,
            ActiveFaultCount = viewModel.FaultManager.ActiveFaults.Count,
            Monitor = SmokeDpiTestHook.CaptureMonitorEvidence(window),
            Failures = failures
        };

        void CheckValue(string name, double actual, double expected)
        {
            if (Math.Abs(actual - expected) > 0.000001)
            {
                failures.Add($"{name} was {actual:G6}; expected {expected:G6}.");
            }
        }
    }
}
