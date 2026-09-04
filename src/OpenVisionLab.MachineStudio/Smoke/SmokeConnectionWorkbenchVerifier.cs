using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using OpenVisionLab;
using OpenVisionLab.Machine.Core.Axes;
using OpenVisionLab.Machine.Core.Channels;
using OpenVisionLab.Machine.Core.Layouts;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Sequence.Authoring;
using OpenVisionLab.Machine.Simulation.Compilation;
using OpenVisionLab.Machine.Simulation.Layout;
using OpenVisionLab.Machine.Simulation.Sequences;
using OpenVisionLab.MachineStudio.View.Shell;
using OpenVisionLab.MachineStudio.ViewModel;

namespace OpenVisionLab.MachineStudio;

internal sealed class SmokeConnectionWorkbenchReport
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

internal static class SmokeConnectionWorkbenchVerifier
{
    public static async Task<SmokeConnectionWorkbenchReport> VerifyAsync(
        ShellWindow window,
        MainViewModel vm,
        MachineProjectDocument? initialProject,
        string connectionWorkbenchSavePath,
        Func<DependencyObject, Func<TextBox, bool>, TextBox?> findTextBox)
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

        var bundledRecipePaths = Directory
            .EnumerateFiles(
                Path.Combine(AppContext.BaseDirectory, "Samples", "SemiconductorRecipes"),
                "*.ovmachine")
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        Check("ten-bundled-recipe-projections", bundledRecipePaths.Length == 10);
        Check("all-bundled-recipe-connections-valid", bundledRecipePaths.All(path =>
        {
            var project = new ProjectDocumentStore().Load(File.ReadAllText(path));
            var projection = new RecipeConnectionWorkbenchViewModel(
                _ => { },
                (_, _) => { },
                _ => null,
                () => null,
                (sequenceId, stepId, componentId) =>
                    new DeterministicSequenceStepPreviewRunner().RunAsync(
                        project,
                        sequenceId,
                        stepId,
                        componentId),
                sequenceId => new DeterministicRecipeDryRunRunner().RunAsync(
                    project,
                    sequenceId),
                _ => { },
                () => false,
                _ => 0,
                _ => 0,
                _ => 0,
                _ => 0,
                _ => 0,
                _ => 0,
                _ => 0,
                _ => 0,
                _ => 0,
                _ => { });
            projection.Load(project);
            return projection.HasRows && projection.Rows.All(row => row.IsValid);
        }));
        Check("all-bundled-recipes-simulation-ready", bundledRecipePaths.All(path =>
        {
            var project = new ProjectDocumentStore().Load(File.ReadAllText(path));
            var fixedStep = TimeSpan.FromMilliseconds(project.Simulation.FixedStepMilliseconds);
            return new MachineProjectRuntimeCompiler(fixedStep).Compile(project).IsSuccess;
        }));

        var previewRow = vm.RecipeConnections.Rows.FirstOrDefault(row =>
            row.Kind == LayoutComponentKind.PneumaticCylinder && row.CanPreviewSequenceStep);
        Check("preview-disabled-before-readiness",
            previewRow is not null
            && !vm.RecipeConnections.PreviewSequenceStepCommand.CanExecute(previewRow));
        Check("dry-run-disabled-before-readiness",
            !vm.RecipeConnections.RunRecipeDryRunCommand.CanExecute(null));
        var projectStore = new ProjectDocumentStore();
        var projectBeforePreview = projectStore.Serialize(initialProject!);
        var mainSnapshotBeforePreview = vm.SceneSnapshots.Latest;
        var runningBeforePreview = vm.IsRunning;
        var designModeBeforePreview = vm.IsDesignMode;
        vm.RecipeConnections.ValidateSimulationReadinessCommand.Execute(null);
        Check("preview-enabled-after-readiness",
            previewRow is not null
            && vm.RecipeConnections.PreviewSequenceStepCommand.CanExecute(previewRow));
        Check("dry-run-enabled-after-readiness",
            vm.RecipeConnections.RunRecipeDryRunCommand.CanExecute(null));
        if (previewRow is not null)
        {
            vm.RecipeConnections.PreviewSequenceStepCommand.Execute(previewRow);
            for (var attempt = 0; attempt < 100 && !previewRow.HasPreviewResult; attempt++)
            {
                await Task.Delay(20);
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            }
        }

        var previewResult = previewRow?.PreviewResult;
        var mainSnapshotAfterPreview = vm.SceneSnapshots.Latest;
        Check("preview-completed", previewResult?.Outcome == SequenceStepPreviewOutcome.Completed);
        Check("preview-bounded",
            previewResult is { ExecutedTicks: > 0 }
            && previewResult.ExecutedTicks < previewResult.MaximumTicks);
        Check("preview-observed-cylinder-extended",
            previewResult?.FinalSnapshot?.LayoutComponents.FirstOrDefault(component =>
                component.Id == previewRow?.ComponentId)?.CylinderState
                == PneumaticCylinderState.Extended);
        Check("preview-main-runtime-unchanged",
            mainSnapshotBeforePreview?.TickIndex == mainSnapshotAfterPreview?.TickIndex
            && mainSnapshotBeforePreview?.SimulationTime == mainSnapshotAfterPreview?.SimulationTime
            && vm.IsRunning == runningBeforePreview
            && vm.IsDesignMode == designModeBeforePreview);
        Check("preview-project-unchanged",
            projectBeforePreview == projectStore.Serialize(initialProject!));

        vm.RecipeConnections.RunRecipeDryRunCommand.Execute(null);
        for (var attempt = 0;
             attempt < 150 && !vm.RecipeConnections.HasRecipeDryRunResult;
             attempt++)
        {
            await Task.Delay(20);
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        }

        var dryRunResult = vm.RecipeConnections.RecipeDryRunResult;
        var mainSnapshotAfterDryRun = vm.SceneSnapshots.Latest;
        Check("dry-run-completed", dryRunResult?.Outcome == RecipeDryRunOutcome.Completed);
        Check("dry-run-bounded",
            dryRunResult is { ExecutedTicks: > 0 }
            && dryRunResult.ExecutedTicks < dryRunResult.MaximumTicks);
        Check(
            "dry-run-timeline-complete",
            dryRunResult?.Timeline.Count == vm.RecipeConnections.RecipeStepCount
                - (initialProject?.Devices.Any(device =>
                    device.InspectionSortRouter is not null) == true ? 3 : 0));
        Check("dry-run-no-issue", dryRunResult?.FirstIssue is null);
        Check("dry-run-final-cylinder-retracted",
            dryRunResult?.FinalSnapshot?.LayoutComponents.FirstOrDefault(component =>
                component.Kind == LayoutComponentKind.PneumaticCylinder)?.CylinderState
                == PneumaticCylinderState.Retracted);
        Check("dry-run-final-conveyor-stopped",
            dryRunResult?.FinalSnapshot?.LayoutComponents.FirstOrDefault(component =>
                component.Kind == LayoutComponentKind.Conveyor)?.ConveyorRunning == false);
        Check("dry-run-main-runtime-unchanged",
            mainSnapshotBeforePreview?.TickIndex == mainSnapshotAfterDryRun?.TickIndex
            && mainSnapshotBeforePreview?.SimulationTime == mainSnapshotAfterDryRun?.SimulationTime
            && vm.IsRunning == runningBeforePreview
            && vm.IsDesignMode == designModeBeforePreview);
        Check("dry-run-project-unchanged",
            projectBeforePreview == projectStore.Serialize(initialProject!));
        var dryRunNavigationStep = FindConnectionDryRunStep(
            vm.RecipeConnections.RecipeDryRunTimeline,
            "wait-process-position",
            "wait-station-position");
        vm.RecipeConnections.SelectedRecipeDryRunStep = dryRunNavigationStep;
        Check("dry-run-timeline-selects-connection",
            dryRunNavigationStep?.ComponentId is not null
            && vm.RecipeConnections.SelectedRow?.ComponentId == dryRunNavigationStep.ComponentId
            && vm.Layout.SelectedItem?.Id == dryRunNavigationStep.ComponentId);
        vm.SelectedDocumentTabIndex = 1;
        vm.RecipeConnections.OpenRecipeDryRunStepCommand.Execute(dryRunNavigationStep);
        Check("dry-run-timeline-opens-exact-sequence",
            vm.SelectedDocumentTabIndex == 2
            && vm.SequenceEditor.SelectedSequence?.Id == dryRunNavigationStep?.SequenceId
            && vm.SequenceEditor.SelectedStep?.Id == dryRunNavigationStep?.StepId);
        Check("dry-run-navigation-runtime-unchanged",
            mainSnapshotBeforePreview?.TickIndex == vm.SceneSnapshots.Latest?.TickIndex
            && mainSnapshotBeforePreview?.SimulationTime == vm.SceneSnapshots.Latest?.SimulationTime
            && vm.IsRunning == runningBeforePreview
            && vm.IsDesignMode == designModeBeforePreview);
        Check("dry-run-navigation-project-unchanged",
            projectBeforePreview == projectStore.Serialize(initialProject!));
        var dryRunPlaybackStep = FindConnectionDryRunStep(
            vm.RecipeConnections.RecipeDryRunTimeline,
            "wait-cylinder-extended",
            "wait-stopper-extended");
        vm.SelectedDocumentTabIndex = 1;
        vm.RecipeConnections.PlayRecipeDryRunStepCommand.Execute(dryRunPlaybackStep);
        Check("dry-run-playback-opens-read-only-layout",
            dryRunPlaybackStep is not null
            && vm.IsDryRunPlaybackActive
            && vm.SelectedDocumentTabIndex == 0
            && !vm.IsSceneEditable
            && !vm.Layout.IsEditable);
        Check("dry-run-playback-uses-isolated-boundary",
            ReferenceEquals(vm.SceneSnapshotSource.Latest, dryRunPlaybackStep?.BoundarySnapshot)
            && !ReferenceEquals(vm.SceneSnapshotSource, vm.SceneSnapshots));
        var playbackPropertyEditor = findTextBox(window, candidate =>
            string.Equals(candidate.Name, "ComponentNameTextBox", StringComparison.Ordinal));
        Check("dry-run-playback-inspector-read-only",
            playbackPropertyEditor is not null
            && !playbackPropertyEditor.IsEnabled
            && vm.SelectedEquipmentStatus?.StateText
                == OpenVisionLanguageService.T("Equipment.State.Extended"));
        var playbackIndex = vm.RecipeConnections.RecipeDryRunTimeline.IndexOf(dryRunPlaybackStep!);
        vm.NextDryRunPlaybackStepCommand.Execute(null);
        var nextPlaybackStep = vm.RecipeConnections.RecipeDryRunTimeline[playbackIndex + 1];
        Check("dry-run-playback-next-boundary",
            ReferenceEquals(vm.SceneSnapshotSource.Latest, nextPlaybackStep.BoundarySnapshot)
            && ReferenceEquals(vm.RecipeConnections.SelectedRecipeDryRunStep, nextPlaybackStep));
        vm.PreviousDryRunPlaybackStepCommand.Execute(null);
        Check("dry-run-playback-previous-boundary",
            ReferenceEquals(vm.SceneSnapshotSource.Latest, dryRunPlaybackStep?.BoundarySnapshot)
            && ReferenceEquals(vm.RecipeConnections.SelectedRecipeDryRunStep, dryRunPlaybackStep));
        Check("dry-run-playback-runtime-unchanged",
            mainSnapshotBeforePreview?.TickIndex == vm.SceneSnapshots.Latest?.TickIndex
            && mainSnapshotBeforePreview?.SimulationTime == vm.SceneSnapshots.Latest?.SimulationTime
            && vm.IsRunning == runningBeforePreview
            && vm.IsDesignMode == designModeBeforePreview);
        Check("dry-run-playback-project-unchanged",
            projectBeforePreview == projectStore.Serialize(initialProject!));
        vm.ExitDryRunPlaybackCommand.Execute(null);
        Check("dry-run-playback-exit-restores-layout",
            !vm.IsDryRunPlaybackActive
            && vm.IsSceneEditable
            && vm.Layout.IsEditable
            && ReferenceEquals(vm.SceneSnapshotSource, vm.SceneSnapshots)
            && playbackPropertyEditor?.IsEnabled == true);

        var componentCountBefore = vm.Layout.Items.Count(item => item.Component is not null);
        var axisCountBefore = vm.RecipeConnections.Rows.Count(row =>
            row.Kind == LayoutComponentKind.LinearStage);
        var rotaryAxisCountBefore = vm.RecipeConnections.Rows.Count(row =>
            row.Kind == LayoutComponentKind.RotaryStage);
        var runningBefore = vm.IsRunning;
        var designModeBefore = vm.IsDesignMode;
        var stepCountBefore = vm.SequenceEditor.Steps.Count;

        Check("add-axis-stage", vm.TryAddLayoutComponent(LayoutComponentKind.LinearStage));
        var stageId = vm.Layout.SelectedItem?.Id;
        Check("stage-selected", !string.IsNullOrWhiteSpace(stageId));
        Check("add-targeted-sensor", vm.TryAddLayoutComponent(LayoutComponentKind.DigitalSensor));
        var sensorId = vm.Layout.SelectedItem?.Id;
        Check("add-rotary-axis-stage", vm.TryAddLayoutComponent(LayoutComponentKind.RotaryStage));
        var rotaryStageId = vm.Layout.SelectedItem?.Id;
        Check("add-connected-cylinder", vm.TryAddLayoutComponent(LayoutComponentKind.PneumaticCylinder));
        var cylinderId = vm.Layout.SelectedItem?.Id;
        Check("add-connected-conveyor", vm.TryAddLayoutComponent(LayoutComponentKind.Conveyor));
        var conveyorId = vm.Layout.SelectedItem?.Id;
        Check("add-connected-workpiece", vm.TryAddLayoutComponent(LayoutComponentKind.Workpiece));
        var workpieceId = vm.Layout.SelectedItem?.Id;

        Check("six-components-added",
            vm.Layout.Items.Count(item => item.Component is not null) == componentCountBefore + 6);
        Check("workbench-row-count", vm.RecipeConnections.Rows.Count == componentCountBefore + 6);
        Check("all-workbench-rows-valid", vm.RecipeConnections.Rows.All(row => row.IsValid));
        Check("sequence-links-visible", vm.RecipeConnections.SequenceUseCount > 0);

        var linkedRow = vm.RecipeConnections.Rows.FirstOrDefault(row => row.HasSequenceUse);
        vm.SelectedDocumentTabIndex = 1;
        if (linkedRow is not null)
        {
            vm.RecipeConnections.OpenSequenceStepCommand.Execute(linkedRow);
        }
        Check("linked-step-opens-sequence-tab",
            linkedRow is not null && vm.SelectedDocumentTabIndex == 2);
        Check("linked-step-selected",
            linkedRow is not null
            && vm.SequenceEditor.SelectedStep?.Id == linkedRow.FirstSequenceStepId);

        var stageRow = vm.RecipeConnections.Rows.FirstOrDefault(row => row.ComponentId == stageId);
        vm.RecipeConnections.SelectedRow = stageRow;
        Check("row-selection-selects-layout", vm.Layout.SelectedItem?.Id == stageId);
        Check("row-selection-opens-binding-editor",
            vm.Layout.SelectedComponentEditor?.BehaviorBindingId is not null);
        var sequenceStructureIsEditable = SequenceDefinitionEditor.IsStrictLinear(
            vm.SequenceEditor.SelectedSequence);
        var canAddTargetStep = stageRow?.CanAddSequenceStep == true;
        Check("unused-connection-target-step-availability",
            stageRow is not null
            && canAddTargetStep == (!stageRow.HasSequenceUse
                && stageRow.SequenceTargetId is not null
                && sequenceStructureIsEditable));
        Check("connection-add-command-gate",
            stageRow is not null
            && vm.RecipeConnections.AddSequenceStepCommand.CanExecute(stageRow) == canAddTargetStep);
        vm.SelectedDocumentTabIndex = 1;
        if (canAddTargetStep && stageRow is not null)
        {
            vm.RecipeConnections.AddSequenceStepCommand.Execute(stageRow);
        }
        if (canAddTargetStep)
        {
            Check("target-step-added", vm.SequenceEditor.Steps.Count == stepCountBefore + 1);
            Check("added-target-step-selected",
                vm.SelectedDocumentTabIndex == 2
                && vm.SequenceEditor.SelectedStep?.TargetId == stageRow?.SequenceTargetId);
            Check("added-target-step-visible-in-connections",
                vm.RecipeConnections.Rows.FirstOrDefault(row => row.ComponentId == stageId)
                    is { HasSequenceUse: true });
        }
        else
        {
            Check("unavailable-target-step-command-locked",
                stageRow is not null
                && !vm.RecipeConnections.AddSequenceStepCommand.CanExecute(stageRow));
            Check("unavailable-target-step-does-not-mutate",
                vm.SequenceEditor.Steps.Count == stepCountBefore
                && stageRow?.HasSequenceUse == false);
        }

        vm.RecipeConnections.ValidateSimulationReadinessCommand.Execute(null);
        Check("explicit-readiness-passed", vm.RecipeConnections.ReadinessPassed == true);
        Check("authoring-does-not-run", vm.IsRunning == runningBefore);
        Check("authoring-keeps-design-mode", vm.IsDesignMode == designModeBefore);

        var fullSavePath = Path.GetFullPath(connectionWorkbenchSavePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullSavePath)!);
        await vm.SaveProjectAsync(fullSavePath);
        Check("saved-project-reopens", await vm.OpenProjectAsync(fullSavePath));

        var saved = await new ProjectDocumentStore().LoadAsync(fullSavePath);
        var savedLayout = saved.Layouts.First(layout =>
            string.Equals(layout.Id, saved.Simulation.ActiveLayoutId, StringComparison.Ordinal));
        var savedStage = savedLayout.Components.Single(component => component.Id == stageId);
        var savedRotaryStage = savedLayout.Components.Single(component => component.Id == rotaryStageId);
        var savedSensor = savedLayout.Components.Single(component => component.Id == sensorId);
        var savedCylinder = savedLayout.Components.Single(component => component.Id == cylinderId);
        var savedConveyor = savedLayout.Components.Single(component => component.Id == conveyorId);
        var savedWorkpiece = savedLayout.Components.Single(component => component.Id == workpieceId);
        var sensorDevice = saved.Devices.Single(device => device.Id == savedSensor.BehaviorBindingId);
        var cylinderDevice = saved.Devices.Single(device => device.Id == savedCylinder.BehaviorBindingId);
        var conveyorDevice = saved.Devices.Single(device => device.Id == savedConveyor.BehaviorBindingId);
        var workpieceDevice = saved.Devices.Single(device => device.Id == savedWorkpiece.BehaviorBindingId);

        Check("axis-binding-persisted", saved.Axes.Any(axis => axis.Id == savedStage.BehaviorBindingId));
        Check("rotary-axis-binding-persisted", saved.Axes.Any(axis =>
            axis.Id == savedRotaryStage.BehaviorBindingId
            && axis.Kind == AxisKind.Rotary
            && axis.Unit == "deg"));
        Check("rotary-stage-kind-persisted", savedRotaryStage.Kind == LayoutComponentKind.RotaryStage);
        Check("target-step-persisted",
            saved.Sequences.Any(sequence => sequence.Steps.Any(step =>
                step.TargetId == savedStage.BehaviorBindingId)) == canAddTargetStep);
        Check("sensor-target-persisted", sensorDevice.Sensor?.TargetComponentId == stageId);
        Check("sensor-di-persisted", saved.Channels.Any(channel =>
            channel.Id == sensorDevice.Sensor?.OutputChannelId && channel.Kind == ChannelKind.DigitalInput));
        Check("cylinder-io-persisted", cylinderDevice.Cylinder is { } cylinder
            && saved.Channels.Any(channel => channel.Id == cylinder.ExtendCommandChannelId && channel.Kind == ChannelKind.DigitalOutput)
            && saved.Channels.Any(channel => channel.Id == cylinder.ExtendedSensorChannelId && channel.Kind == ChannelKind.DigitalInput)
            && saved.Channels.Any(channel => channel.Id == cylinder.RetractedSensorChannelId && channel.Kind == ChannelKind.DigitalInput));
        Check("conveyor-io-persisted", conveyorDevice.Conveyor is { } conveyor
            && saved.Channels.Any(channel => channel.Id == conveyor.RunCommandChannelId && channel.Kind == ChannelKind.DigitalOutput)
            && saved.Channels.Any(channel => channel.Id == conveyor.ReverseCommandChannelId && channel.Kind == ChannelKind.DigitalOutput));
        Check("workpiece-carrier-persisted", workpieceDevice.Workpiece is { } workpiece
            && savedLayout.Components.Any(component =>
                component.Id == workpiece.ConveyorComponentId
                && component.Kind == LayoutComponentKind.Conveyor));
        Check("reopen-stays-stopped", !vm.IsRunning && vm.IsDesignMode);
        Check("added-stage-count-visible", vm.RecipeConnections.Rows.Count(row =>
            row.Kind == LayoutComponentKind.LinearStage) == axisCountBefore + 1);
        Check("added-rotary-stage-count-visible", vm.RecipeConnections.Rows.Count(row =>
            row.Kind == LayoutComponentKind.RotaryStage) == rotaryAxisCountBefore + 1);

        return new SmokeConnectionWorkbenchReport
        {
            Checks = checks,
            Failures = failures
        };
    }

    private static RecipeDryRunStepPresentation? FindConnectionDryRunStep(
        IEnumerable<RecipeDryRunStepPresentation> timeline,
        params string[] preferredStepIds)
    {
        var steps = timeline.ToArray();
        foreach (var preferredStepId in preferredStepIds)
        {
            var preferred = steps.FirstOrDefault(step =>
                string.Equals(step.StepId, preferredStepId, StringComparison.Ordinal));
            if (preferred is not null)
            {
                return preferred;
            }
        }

        return steps.FirstOrDefault(step =>
                   step.ComponentId is not null
                   && step.StepId.Contains("extended", StringComparison.OrdinalIgnoreCase))
               ?? steps.FirstOrDefault(step => step.ComponentId is not null)
               ?? steps.FirstOrDefault();
    }
}
