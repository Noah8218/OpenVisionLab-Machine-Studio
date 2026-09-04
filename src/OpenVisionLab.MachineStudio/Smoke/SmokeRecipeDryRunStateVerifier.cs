using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using OpenVisionLab;
using OpenVisionLab.Machine.Core.Axes;
using OpenVisionLab.Machine.Core.Channels;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Core.Sequences;
using OpenVisionLab.Machine.Core.Layouts;
using OpenVisionLab.Machine.Core.Devices;
using OpenVisionLab.Machine.Sequence.Authoring;
using OpenVisionLab.Machine.Simulation.Axis;
using OpenVisionLab.Machine.Simulation.Camera;
using OpenVisionLab.Machine.Simulation.Commands;
using OpenVisionLab.Machine.Simulation.Compilation;
using OpenVisionLab.Machine.Simulation.Commissioning;
using OpenVisionLab.Machine.Simulation.Engine;
using OpenVisionLab.Machine.Simulation.FaultScenarios;
using OpenVisionLab.Machine.Simulation.Faults;
using OpenVisionLab.Machine.Simulation.Layout;
using OpenVisionLab.Machine.Simulation.Scenarios;
using OpenVisionLab.Machine.Simulation.Sequences;
using OpenVisionLab.Machine.Simulation.Snapshots;
using OpenVisionLab.Machine.Simulation.Workpieces;
using OpenVisionLab.MachineStudio.Model;
using OpenVisionLab.MachineStudio.View;
using OpenVisionLab.MachineStudio.View.Inspector;
using OpenVisionLab.MachineStudio.View.Project;
using OpenVisionLab.MachineStudio.View.Scene;
using OpenVisionLab.MachineStudio.View.Shell;
using OpenVisionLab.MachineStudio.ViewModel;
using static OpenVisionLab.MachineStudio.SmokeVisualTreeQuery;

namespace OpenVisionLab.MachineStudio;

internal static class SmokeRecipeDryRunStateVerifier
{
    private const uint MouseEventLeftDown = 0x0002;

    public static bool IsSupportedState(string? state) => state?.ToLowerInvariant() is
        "dry-run"
        or "dry-run-fault"
        or "dry-run-checkpoint-mismatch"
        or "dry-run-checkpoint-playback"
        or "dry-run-load-lock-fault"
        or "dry-run-load-lock-fault-playback"
        or "dry-run-oht-fault-playback"
        or "dry-run-wafer-handler-fault"
        or "dry-run-wafer-handler-fault-playback"
        or "dry-run-inspection-sort-pass-playback"
        or "dry-run-inspection-sort-fault-playback"
        or "dry-run-inspection-handoff-fault-playback"
        or "dry-run-prealigner-fault-playback"
        or "dry-run-open-step"
        or "dry-run-playback"
        or "dry-run-playback-first"
        or "dry-run-playback-last"
        or "dry-run-playback-control-focus"
        or "dry-run-playback-control-hover"
        or "dry-run-playback-control-pressed"
        or "dry-run-playback-entry-focus"
        or "dry-run-playback-entry-hover"
        or "dry-run-playback-entry-pressed"
        or "dry-run-playback-entry-disabled"
        or "dry-run-timeline-focus"
        or "dry-run-timeline-hover"
        or "dry-run-timeline-pressed"
        or "dry-run-timeline-disabled"
        or "dry-run-focus"
        or "dry-run-hover"
        or "dry-run-pressed";

    public static async Task ApplyAsync(
        ShellWindow window,
        MainViewModel vm,
        MachineProjectDocument? initialProject,
        RecipeConnectionWorkbenchView workbench,
        Button dryRunButton,
        string state,
        string? savePath,
        SmokeUiInteraction interaction)
    {
        switch (state.ToLowerInvariant())
        {
            case "dry-run":
                AssertSmoke(!dryRunButton.IsEnabled, "Recipe dry run was enabled before readiness passed.");
                vm.RecipeConnections.ValidateSimulationReadinessCommand.Execute(null);
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                AssertSmoke(dryRunButton.IsEnabled, "Recipe dry run was not enabled after readiness passed.");
                vm.RecipeConnections.RunRecipeDryRunCommand.Execute(null);
                for (var attempt = 0;
                     attempt < 150 && !vm.RecipeConnections.HasRecipeDryRunResult;
                     attempt++)
                {
                    await Task.Delay(20);
                    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                }
                AssertSmoke(
                    vm.RecipeConnections.RecipeDryRunResult?.Outcome == RecipeDryRunOutcome.Completed,
                    "The isolated recipe dry run did not complete.");
                AssertSmoke(
                    vm.RecipeConnections.RecipeDryRunTimeline.Count
                        == vm.RecipeConnections.RecipeStepCount
                            - (initialProject?.Devices.Any(device =>
                                device.InspectionSortRouter is not null) == true ? 3 : 0),
                    "The recipe dry-run timeline was incomplete.");
                AssertSmoke(
                    vm.RecipeConnections.RecipeDryRunTimeline.Count(step => step.HasCheckpoint)
                        == vm.RecipeConnections.CheckpointStepCount
                    && !vm.RecipeConnections.RecipeDryRunTimeline.Any(step =>
                        step.HasCheckpointMismatch),
                    "The authored recipe checkpoints did not all pass.");
                AssertSmoke(
                    FindVisualDescendant<Border>(workbench, candidate =>
                        string.Equals(candidate.Name, "RecipeDryRunResult", StringComparison.Ordinal)
                        && candidate.IsVisible) is not null,
                    "The recipe dry-run result panel was not visible.");
                if (vm.RecipeConnections.RecipeDryRunResult?.FinalSnapshot?.LoadLocks is [var finalLoadLock])
                {
                    var loadLockPresentation = vm.RecipeConnections.RecipeDryRunFinalStates.Single(state =>
                        state.IsLoadLock);
                    AssertSmoke(
                        finalLoadLock.State == LoadLockState.Atmosphere
                        && !finalLoadLock.IsVacuumReady
                        && finalLoadLock.IsAtmosphereReady
                        && finalLoadLock.IsOuterDoorPermitted
                        && !finalLoadLock.IsInnerDoorPermitted
                        && !loadLockPresentation.IsFault
                        && loadLockPresentation.Text.Contains(
                            OpenVisionLanguageService.T("Connections.LoadLockState.Atmosphere"),
                            StringComparison.CurrentCulture)
                        && loadLockPresentation.Text.Contains(
                            OpenVisionLanguageService.T("Connections.LoadLockDoorAllowed"),
                            StringComparison.CurrentCulture)
                        && loadLockPresentation.Text.Contains(
                            OpenVisionLanguageService.T("Connections.LoadLockDoorBlocked"),
                            StringComparison.CurrentCulture),
                        "The normal dry-run result did not expose load-lock pressure readiness and door permissions.");
                }
                if (vm.RecipeConnections.RecipeDryRunResult?.FinalSnapshot?.WaferHandlers is [var finalHandler])
                {
                    var handlerPresentation = vm.RecipeConnections.RecipeDryRunFinalStates.Single(state =>
                        state.IsWaferHandler);
                    AssertSmoke(
                        finalHandler.State == WaferHandlerOwnershipState.Destination
                        && vm.RecipeConnections.RecipeDryRunResult!.FinalSnapshot.LayoutComponents.Single(component =>
                            component.Id == finalHandler.WorkpieceComponentId).TransferOwnershipState
                            == WaferHandlerOwnershipState.Destination
                        && !handlerPresentation.IsFault
                        && handlerPresentation.Text.Contains(
                            OpenVisionLanguageService.T("Connections.WaferHandlerState.Destination"),
                            StringComparison.CurrentCulture),
                        "The normal dry-run result did not expose destination wafer ownership.");
                }
                if (vm.RecipeConnections.RecipeDryRunResult?.FinalSnapshot?.InspectionSortRouters is [var finalSorter])
                {
                    var sorterPresentation = vm.RecipeConnections.RecipeDryRunFinalStates.Single(state =>
                        state.IsInspectionSorter);
                    AssertSmoke(
                        finalSorter.State == InspectionSortRouteState.NgRouted
                        && finalSorter.Decision == PlaceholderInspectionDecision.Fail
                        && !sorterPresentation.IsFault
                        && sorterPresentation.Text.Contains(
                            OpenVisionLanguageService.T("Connections.InspectionSortState.NgRouted"),
                            StringComparison.CurrentCulture),
                        "The normal dry-run result did not expose the NG inspection route.");
                }
                if (vm.RecipeConnections.RecipeDryRunResult?.FinalSnapshot?.InspectionHandoffs is [var finalInspectionHandoff])
                {
                    var inspectionPresentation = vm.RecipeConnections.RecipeDryRunFinalStates.Single(state =>
                        state.IsInspectionHandoff);
                    AssertSmoke(
                        finalInspectionHandoff.State == InspectionHandoffState.Complete
                        && finalInspectionHandoff.Decision == PlaceholderInspectionDecision.Pass
                        && finalInspectionHandoff.IsMaterialPresent
                        && !inspectionPresentation.IsFault
                        && inspectionPresentation.Text.Contains(
                            OpenVisionLanguageService.T("Connections.InspectionHandoffState.Complete"),
                            StringComparison.CurrentCulture),
                        "The normal dry-run result did not expose the completed inspection handoff.");
                }
                if (vm.RecipeConnections.RecipeDryRunResult?.FinalSnapshot?.OhtHandoffs is [var finalHandoff])
                {
                    var handoffPresentation = vm.RecipeConnections.RecipeDryRunFinalStates.Single(state =>
                        state.IsOhtHandoff);
                    AssertSmoke(
                        finalHandoff.State == OhtHandoffOwnershipState.LoadPort
                        && finalHandoff.IsCarrierReceived
                        && !handoffPresentation.IsFault
                        && handoffPresentation.Text.Contains(
                            OpenVisionLanguageService.T("Connections.OhtHandoffState.LoadPort"),
                            StringComparison.CurrentCulture),
                        "The normal dry-run result did not expose load-port carrier ownership.");
                }
                if (vm.RecipeConnections.RecipeDryRunResult?.FinalSnapshot?.Prealigners is [var finalPrealigner])
                {
                    var prealignerPresentation = vm.RecipeConnections.RecipeDryRunFinalStates.Single(state =>
                        state.IsPrealigner);
                    AssertSmoke(
                        finalPrealigner.State == PrealignerState.Released
                        && finalPrealigner.IsAlignmentComplete
                        && Math.Abs(finalPrealigner.RotaryPositionDegrees - 180) <= finalPrealigner.AlignmentToleranceDegrees
                        && !prealignerPresentation.IsFault
                        && prealignerPresentation.Text.Contains(
                            OpenVisionLanguageService.T("Connections.PrealignerState.Released"),
                            StringComparison.CurrentCulture),
                        "The normal dry-run result did not expose completed pre-alignment and release.");
                }
                if (!string.IsNullOrWhiteSpace(savePath)
                    && initialProject?.Devices.Any(device => device.WaferHandler is not null) == true)
                {
                    await vm.SaveProjectAsync(savePath);
                    AssertSmoke(
                        await vm.OpenProjectAsync(savePath),
                        "The saved wafer-handler recipe could not be reopened.");
                    var reopened = await new ProjectDocumentStore().LoadAsync(savePath);
                    var reopenedHandler = reopened.Devices.Single(device =>
                        device.Kind == DeviceKind.Handler && device.WaferHandler is not null);
                    AssertSmoke(
                        reopened.Schema == MachineProjectDocument.CurrentSchema
                        && reopenedHandler.WaferHandler!.HorizontalAxisId == "axis.robot-reach"
                        && reopenedHandler.WaferHandler.VerticalAxisId == "axis.process"
                        && reopenedHandler.WaferHandler.WorkpieceComponentId == "wafer",
                        "Save/reopen did not preserve the typed wafer-handler contract.");
                    vm.RecipeConnections.ValidateSimulationReadinessCommand.Execute(null);
                    AssertSmoke(
                        vm.RecipeConnections.RunRecipeDryRunCommand.CanExecute(null),
                        "The reopened wafer-handler recipe did not restore simulation readiness.");
                }
                if (!string.IsNullOrWhiteSpace(savePath)
                    && initialProject?.Devices.Any(device => device.InspectionSortRouter is not null) == true)
                {
                    await vm.SaveProjectAsync(savePath);
                    AssertSmoke(
                        await vm.OpenProjectAsync(savePath),
                        "The saved inspection-sorter recipe could not be reopened.");
                    var reopened = await new ProjectDocumentStore().LoadAsync(savePath);
                    var reopenedSorter = reopened.Devices.Single(device =>
                        device.Kind == DeviceKind.Sorter && device.InspectionSortRouter is not null);
                    AssertSmoke(
                        reopened.Schema == MachineProjectDocument.CurrentSchema
                        && reopenedSorter.InspectionSortRouter!.CameraId == "camera.metrology"
                        && reopenedSorter.InspectionSortRouter.PassConveyorComponentId == "transport"
                        && reopenedSorter.InspectionSortRouter.NgConveyorComponentId == "sort-transport",
                        "Save/reopen did not preserve the typed inspection-sorter contract.");
                    vm.RecipeConnections.ValidateSimulationReadinessCommand.Execute(null);
                    AssertSmoke(
                        vm.RecipeConnections.RunRecipeDryRunCommand.CanExecute(null),
                        "The reopened inspection-sorter recipe did not restore simulation readiness.");
                }
                if (!string.IsNullOrWhiteSpace(savePath)
                    && initialProject?.Devices.Any(device => device.InspectionHandoff is not null) == true)
                {
                    await vm.SaveProjectAsync(savePath);
                    AssertSmoke(
                        await vm.OpenProjectAsync(savePath),
                        "The saved inspection-handoff recipe could not be reopened.");
                    var reopened = await new ProjectDocumentStore().LoadAsync(savePath);
                    var reopenedHandoff = reopened.Devices.Single(device =>
                        device.Kind == DeviceKind.Inspection && device.InspectionHandoff is not null);
                    AssertSmoke(
                        reopened.Schema == MachineProjectDocument.CurrentSchema
                        && reopenedHandoff.InspectionHandoff!.CameraId == "camera.ocr"
                        && reopenedHandoff.InspectionHandoff.InspectionPositionSensorChannelId == "di.sensor-process"
                        && reopenedHandoff.InspectionHandoff.ResultAcceptedCommandChannelId == "do.inspection-result-accepted",
                        "Save/reopen did not preserve the typed inspection-handoff contract.");
                    vm.RecipeConnections.ValidateSimulationReadinessCommand.Execute(null);
                    AssertSmoke(
                        vm.RecipeConnections.RunRecipeDryRunCommand.CanExecute(null)
                        && !vm.IsRunning
                        && vm.IsDesignMode
                        && vm.SceneSnapshots.Latest?.TickIndex == 0,
                        "The reopened inspection-handoff recipe did not restore stopped simulation readiness.");
                }
                if (!string.IsNullOrWhiteSpace(savePath)
                    && initialProject?.Devices.Any(device => device.OhtHandoff is not null) == true)
                {
                    await vm.SaveProjectAsync(savePath);
                    AssertSmoke(
                        await vm.OpenProjectAsync(savePath),
                        "The saved OHT handoff recipe could not be reopened.");
                    var reopened = await new ProjectDocumentStore().LoadAsync(savePath);
                    var reopenedHandoff = reopened.Devices.Single(device =>
                        device.Kind == DeviceKind.Oht && device.OhtHandoff is not null);
                    AssertSmoke(
                        reopened.Schema == MachineProjectDocument.CurrentSchema
                        && reopenedHandoff.OhtHandoff!.TransportConveyorComponentId == "transport"
                        && reopenedHandoff.OhtHandoff.LoadPortReadySensorChannelId == "di.cylinder.extended"
                        && reopenedHandoff.OhtHandoff.CarrierReceivedSensorChannelId == "di.sensor-process",
                        "Save/reopen did not preserve the typed OHT handoff contract.");
                    vm.RecipeConnections.ValidateSimulationReadinessCommand.Execute(null);
                    AssertSmoke(
                        vm.RecipeConnections.RunRecipeDryRunCommand.CanExecute(null)
                        && !vm.IsRunning,
                        "The reopened OHT handoff recipe did not restore readiness safely.");
                }
                if (!string.IsNullOrWhiteSpace(savePath)
                    && initialProject?.Devices.Any(device => device.Prealigner is not null) == true)
                {
                    await vm.SaveProjectAsync(savePath);
                    AssertSmoke(
                        await vm.OpenProjectAsync(savePath),
                        "The saved pre-aligner recipe could not be reopened.");
                    var reopened = await new ProjectDocumentStore().LoadAsync(savePath);
                    var reopenedPrealigner = reopened.Devices.Single(device =>
                        device.Kind == DeviceKind.Prealigner && device.Prealigner is not null);
                    AssertSmoke(
                        reopened.Schema == MachineProjectDocument.CurrentSchema
                        && reopenedPrealigner.Prealigner!.RotaryStageComponentId == "alignment-table"
                        && reopenedPrealigner.Prealigner.ClampCylinderComponentId == "process-cylinder"
                        && reopenedPrealigner.Prealigner.AlignmentTargetDegrees == 180
                        && reopenedPrealigner.Prealigner.AlignmentToleranceDegrees == 0.1,
                        "Save/reopen did not preserve the typed pre-aligner contract.");
                    vm.RecipeConnections.ValidateSimulationReadinessCommand.Execute(null);
                    AssertSmoke(
                        vm.RecipeConnections.RunRecipeDryRunCommand.CanExecute(null)
                        && !vm.IsRunning
                        && vm.IsDesignMode
                        && vm.SceneSnapshots.Latest?.TickIndex == 0,
                        "The reopened pre-aligner recipe did not restore stopped simulation readiness.");
                }
                break;
            case "dry-run-fault":
                var faultProject = initialProject
                    ?? throw new InvalidOperationException("A project is required for the recipe dry-run fault state.");
                var faultSequence = faultProject.Sequences.FirstOrDefault()
                    ?? throw new InvalidOperationException("A sequence is required for the recipe dry-run fault state.");
                var faultStep = faultSequence.Steps.FirstOrDefault(step =>
                    string.Equals(step.Id, "wait-process-position", StringComparison.Ordinal))
                    ?? throw new InvalidOperationException("The process-position wait step was not available.");
                faultStep.TimeoutMs = 20;
                vm.RecipeConnections.Load(faultProject);
                vm.RecipeConnections.ValidateSimulationReadinessCommand.Execute(null);
                AssertSmoke(
                    vm.RecipeConnections.RunRecipeDryRunCommand.CanExecute(null),
                    "Recipe dry run was not enabled for the fault state.");
                vm.RecipeConnections.RunRecipeDryRunCommand.Execute(null);
                for (var attempt = 0;
                     attempt < 150 && !vm.RecipeConnections.HasRecipeDryRunResult;
                     attempt++)
                {
                    await Task.Delay(20);
                    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                }
                AssertSmoke(
                    vm.RecipeConnections.RecipeDryRunResult?.Outcome == RecipeDryRunOutcome.Faulted,
                    "The recipe dry-run fault state did not fault.");
                AssertSmoke(
                    vm.RecipeConnections.RecipeDryRunResult?.FirstIssue?.StepId == faultStep.Id
                    && vm.RecipeConnections.RecipeDryRunTimeline.Any(trace => trace.HasIssue),
                    "The recipe dry-run fault state did not identify its first issue.");
                AssertSmoke(
                    vm.RecipeConnections.SelectedRecipeDryRunStep is { HasIssue: true }
                    && vm.RecipeConnections.SelectedRow?.ComponentId
                        == vm.RecipeConnections.SelectedRecipeDryRunStep.ComponentId
                    && vm.Layout.SelectedItem?.Id
                        == vm.RecipeConnections.SelectedRecipeDryRunStep.ComponentId,
                    "The recipe dry-run fault state did not select its issue and connected equipment.");
                break;
            case "dry-run-checkpoint-mismatch":
            case "dry-run-checkpoint-playback":
                var checkpointProject = initialProject
                    ?? throw new InvalidOperationException("A project is required for the checkpoint smoke state.");
                var checkpointStep = checkpointProject.Sequences.First().Steps.Single(step =>
                    string.Equals(step.Id, "wait-cylinder-extended", StringComparison.Ordinal));
                checkpointStep.ExpectedTargetId = "process-cylinder";
                checkpointStep.ExpectedState = "Retracted";
                vm.SequenceEditor.Load(checkpointProject);
                vm.RecipeConnections.Load(checkpointProject);
                vm.RecipeConnections.ValidateSimulationReadinessCommand.Execute(null);
                AssertSmoke(
                    vm.RecipeConnections.RunRecipeDryRunCommand.CanExecute(null),
                    "Recipe dry run was not enabled for the checkpoint state.");
                vm.RecipeConnections.RunRecipeDryRunCommand.Execute(null);
                for (var attempt = 0;
                     attempt < 150 && !vm.RecipeConnections.HasRecipeDryRunResult;
                     attempt++)
                {
                    await Task.Delay(20);
                    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                }
                AssertSmoke(
                    vm.RecipeConnections.RecipeDryRunResult?.Outcome
                        == RecipeDryRunOutcome.CompletedWithMismatch,
                    "The expected-state mismatch did not produce its distinct outcome.");
                AssertSmoke(
                    vm.RecipeConnections.RecipeDryRunResult?.FirstCheckpointMismatch?.StepId
                        == checkpointStep.Id
                    && vm.RecipeConnections.SelectedRecipeDryRunStep is
                        { HasCheckpointMismatch: true },
                    "The first expected-state mismatch was not selected.");
                AssertSmoke(
                    vm.RecipeConnections.SelectedRow?.ComponentId == "process-cylinder"
                    && vm.Layout.SelectedItem?.Id == "process-cylinder",
                    "The mismatch did not select its connected cylinder.");
                if (state.Equals(
                        "dry-run-checkpoint-playback",
                        StringComparison.OrdinalIgnoreCase))
                {
                    var mismatchStep = vm.RecipeConnections.SelectedRecipeDryRunStep!;
                    vm.RecipeConnections.PlayRecipeDryRunStepCommand.Execute(mismatchStep);
                    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    AssertSmoke(
                        vm.IsDryRunPlaybackActive
                        && vm.HasDryRunPlaybackMismatch
                        && vm.DryRunPlaybackCheckpointText.Contains("Retracted", StringComparison.Ordinal)
                        && vm.DryRunPlaybackCheckpointText.Contains("Extended", StringComparison.Ordinal),
                        "Checkpoint mismatch detail was not visible in layout playback.");
                }
                break;
            case "dry-run-load-lock-fault":
            case "dry-run-load-lock-fault-playback":
                var loadLockFaultProject = initialProject
                    ?? throw new InvalidOperationException("A project is required for the load-lock fault state.");
                var loadLockFaultSequence = loadLockFaultProject.Sequences.FirstOrDefault()
                    ?? throw new InvalidOperationException("A sequence is required for the load-lock fault state.");
                var requestOuterDoorStep = loadLockFaultSequence.Steps.Single(step =>
                    string.Equals(step.Id, "extend-outer-door", StringComparison.Ordinal));
                var requestOuterDoorIndex = loadLockFaultSequence.Steps.IndexOf(requestOuterDoorStep);
                const string conflictStepId = "request-inner-door-conflict";
                loadLockFaultSequence.Steps.Insert(requestOuterDoorIndex + 1, new SequenceStepDefinition
                {
                    Id = conflictStepId,
                    Name = "Request Both Load Lock Doors",
                    Action = SequenceStepAction.SetSignal,
                    TargetId = "do.cylinder.extend",
                    Parameter = "true",
                    NextStepId = requestOuterDoorStep.NextStepId
                });
                requestOuterDoorStep.NextStepId = conflictStepId;
                vm.SequenceEditor.Load(loadLockFaultProject);
                vm.RecipeConnections.Load(loadLockFaultProject);
                var loadLockFaultStore = new ProjectDocumentStore();
                var loadLockFaultProjectBeforeRun = loadLockFaultStore.Serialize(loadLockFaultProject);
                var loadLockFaultRuntimeBeforeRun = vm.SceneSnapshots.Latest;
                vm.RecipeConnections.ValidateSimulationReadinessCommand.Execute(null);
                AssertSmoke(
                    vm.RecipeConnections.RunRecipeDryRunCommand.CanExecute(null),
                    "Recipe dry run was not enabled for the load-lock fault state.");
                vm.RecipeConnections.RunRecipeDryRunCommand.Execute(null);
                for (var attempt = 0;
                     attempt < 150 && !vm.RecipeConnections.HasRecipeDryRunResult;
                     attempt++)
                {
                    await Task.Delay(20);
                    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                }
                var loadLockFaultSnapshot = vm.RecipeConnections.RecipeDryRunResult?.FinalSnapshot?.LoadLocks.Single();
                var loadLockFaultPresentation = vm.RecipeConnections.RecipeDryRunFinalStates.Single(state =>
                    state.IsLoadLock);
                AssertSmoke(
                    vm.RecipeConnections.RecipeDryRunResult?.Outcome == RecipeDryRunOutcome.Faulted
                    && loadLockFaultSnapshot is
                    {
                        State: LoadLockState.InterlockFault,
                        IsVacuumReady: false,
                        IsAtmosphereReady: false,
                        IsOuterDoorPermitted: false,
                        IsInnerDoorPermitted: false
                    }
                    && loadLockFaultPresentation.IsFault
                    && loadLockFaultPresentation.Text.Contains(
                        OpenVisionLanguageService.T("Connections.LoadLockState.InterlockFault"),
                        StringComparison.CurrentCulture)
                    && loadLockFaultPresentation.Text.Contains(
                        OpenVisionLanguageService.T("Connections.LoadLockDoorBlocked"),
                        StringComparison.CurrentCulture),
                    "The induced load-lock interlock fault was not exposed with blocked door permissions.");
                AssertSmoke(
                    loadLockFaultRuntimeBeforeRun?.TickIndex == vm.SceneSnapshots.Latest?.TickIndex
                    && loadLockFaultRuntimeBeforeRun?.SimulationTime == vm.SceneSnapshots.Latest?.SimulationTime
                    && loadLockFaultProjectBeforeRun == loadLockFaultStore.Serialize(loadLockFaultProject),
                    "The isolated load-lock fault dry run changed the main runtime or project.");
                if (state.Equals(
                        "dry-run-load-lock-fault-playback",
                        StringComparison.OrdinalIgnoreCase))
                {
                    var conflictTimelineStep = vm.RecipeConnections.RecipeDryRunTimeline.First(step =>
                        step.BoundarySnapshot.LoadLocks.Any(loadLock =>
                            loadLock.State == LoadLockState.InterlockFault));
                    vm.RecipeConnections.PlayRecipeDryRunStepCommand.Execute(conflictTimelineStep);
                    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    AssertSmoke(
                        vm.IsDryRunPlaybackActive
                        && vm.IsDryRunPlaybackLoadLockFault
                        && vm.DryRunPlaybackLoadLockText.Contains(
                            OpenVisionLanguageService.T("Connections.LoadLockState.InterlockFault"),
                            StringComparison.CurrentCulture)
                        && vm.DryRunPlaybackLoadLockText.Contains(
                            OpenVisionLanguageService.T("Connections.LoadLockDoorBlocked"),
                            StringComparison.CurrentCulture),
                        "The immutable playback overlay did not expose the load-lock interlock fault.");
                }
                break;
            case "dry-run-oht-fault-playback":
                var ohtFaultProject = initialProject
                    ?? throw new InvalidOperationException("A project is required for the OHT handoff fault state.");
                ohtFaultProject.Channels.Single(channel =>
                    string.Equals(channel.Id, "di.oht.route-available", StringComparison.Ordinal)).InitialValue = 0;
                ohtFaultProject.Sequences.Single().Steps.Single(step =>
                    string.Equals(step.Id, "wait-oht-handoff-ready", StringComparison.Ordinal)).TargetId =
                    "di.cylinder.extended";
                vm.SequenceEditor.Load(ohtFaultProject);
                vm.RecipeConnections.Load(ohtFaultProject);
                var ohtFaultStore = new ProjectDocumentStore();
                var ohtFaultProjectBeforeRun = ohtFaultStore.Serialize(ohtFaultProject);
                var ohtFaultRuntimeBeforeRun = vm.SceneSnapshots.Latest;
                vm.RecipeConnections.ValidateSimulationReadinessCommand.Execute(null);
                AssertSmoke(
                    vm.RecipeConnections.RunRecipeDryRunCommand.CanExecute(null),
                    "Recipe dry run was not enabled for the OHT handoff fault state.");
                vm.RecipeConnections.RunRecipeDryRunCommand.Execute(null);
                for (var attempt = 0;
                     attempt < 150 && !vm.RecipeConnections.HasRecipeDryRunResult;
                     attempt++)
                {
                    await Task.Delay(20);
                    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                }
                var ohtFaultSnapshot = vm.RecipeConnections.RecipeDryRunResult?.FinalSnapshot?.OhtHandoffs.Single();
                var ohtFaultPresentation = vm.RecipeConnections.RecipeDryRunFinalStates.Single(state =>
                    state.IsOhtHandoff);
                AssertSmoke(
                    vm.RecipeConnections.RecipeDryRunResult?.Outcome == RecipeDryRunOutcome.Faulted
                    && ohtFaultSnapshot?.State == OhtHandoffOwnershipState.InterlockFault
                    && !ohtFaultSnapshot.IsTransferPermitted
                    && ohtFaultPresentation.IsFault
                    && ohtFaultPresentation.Text.Contains(
                        OpenVisionLanguageService.T("Connections.OhtHandoffState.InterlockFault"),
                        StringComparison.CurrentCulture),
                    "The premature OHT transfer did not expose a fail-closed interlock fault.");
                AssertSmoke(
                    ohtFaultRuntimeBeforeRun?.TickIndex == vm.SceneSnapshots.Latest?.TickIndex
                    && ohtFaultRuntimeBeforeRun?.SimulationTime == vm.SceneSnapshots.Latest?.SimulationTime
                    && ohtFaultProjectBeforeRun == ohtFaultStore.Serialize(ohtFaultProject),
                    "The isolated OHT handoff fault dry run changed the main runtime or project.");
                var ohtConflictStep = vm.RecipeConnections.RecipeDryRunTimeline.First(step =>
                    step.BoundarySnapshot.OhtHandoffs.Any(handoff =>
                        handoff.State == OhtHandoffOwnershipState.InterlockFault));
                vm.RecipeConnections.PlayRecipeDryRunStepCommand.Execute(ohtConflictStep);
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                AssertSmoke(
                    vm.IsDryRunPlaybackActive
                    && vm.IsDryRunPlaybackOhtHandoffFault
                    && vm.DryRunPlaybackOhtHandoffText.Contains(
                        OpenVisionLanguageService.T("Connections.OhtHandoffState.InterlockFault"),
                        StringComparison.CurrentCulture),
                    "The immutable playback overlay did not expose the OHT handoff interlock fault.");
                break;
            case "dry-run-wafer-handler-fault":
            case "dry-run-wafer-handler-fault-playback":
                var handlerFaultProject = initialProject
                    ?? throw new InvalidOperationException("A project is required for the wafer-handler fault state.");
                var handlerFaultSequence = handlerFaultProject.Sequences.FirstOrDefault()
                    ?? throw new InvalidOperationException("A sequence is required for the wafer-handler fault state.");
                var pickStep = handlerFaultSequence.Steps.Single(step =>
                    string.Equals(step.Id, "pick-wafer", StringComparison.Ordinal));
                var pickIndex = handlerFaultSequence.Steps.IndexOf(pickStep);
                const string unsafePlaceStepId = "unsafe-place-before-pick";
                handlerFaultSequence.Steps.Insert(pickIndex, new SequenceStepDefinition
                {
                    Id = unsafePlaceStepId,
                    Name = "Unsafe Place Before Pick",
                    Action = SequenceStepAction.SetSignal,
                    TargetId = "do.handler.place",
                    Parameter = "true",
                    NextStepId = pickStep.Id
                });
                handlerFaultSequence.Steps.Single(step =>
                    string.Equals(step.NextStepId, pickStep.Id, StringComparison.Ordinal)
                    && !string.Equals(step.Id, unsafePlaceStepId, StringComparison.Ordinal)).NextStepId = unsafePlaceStepId;
                vm.SequenceEditor.Load(handlerFaultProject);
                vm.RecipeConnections.Load(handlerFaultProject);
                var handlerFaultStore = new ProjectDocumentStore();
                var handlerFaultProjectBeforeRun = handlerFaultStore.Serialize(handlerFaultProject);
                var handlerFaultRuntimeBeforeRun = vm.SceneSnapshots.Latest;
                vm.RecipeConnections.ValidateSimulationReadinessCommand.Execute(null);
                AssertSmoke(
                    vm.RecipeConnections.RunRecipeDryRunCommand.CanExecute(null),
                    "Recipe dry run was not enabled for the wafer-handler fault state.");
                vm.RecipeConnections.RunRecipeDryRunCommand.Execute(null);
                for (var attempt = 0;
                     attempt < 150 && !vm.RecipeConnections.HasRecipeDryRunResult;
                     attempt++)
                {
                    await Task.Delay(20);
                    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                }
                var handlerFaultSnapshot = vm.RecipeConnections.RecipeDryRunResult?.FinalSnapshot?.WaferHandlers.Single();
                var handlerFaultPresentation = vm.RecipeConnections.RecipeDryRunFinalStates.Single(state =>
                    state.IsWaferHandler);
                AssertSmoke(
                    vm.RecipeConnections.RecipeDryRunResult?.Outcome == RecipeDryRunOutcome.Faulted
                    && handlerFaultSnapshot?.State == WaferHandlerOwnershipState.InterlockFault
                    && handlerFaultPresentation.IsFault
                    && handlerFaultPresentation.Text.Contains(
                        OpenVisionLanguageService.T("Connections.WaferHandlerState.InterlockFault"),
                        StringComparison.CurrentCulture),
                    "The unsafe wafer place did not expose a fail-closed interlock fault.");
                AssertSmoke(
                    handlerFaultRuntimeBeforeRun?.TickIndex == vm.SceneSnapshots.Latest?.TickIndex
                    && handlerFaultRuntimeBeforeRun?.SimulationTime == vm.SceneSnapshots.Latest?.SimulationTime
                    && handlerFaultProjectBeforeRun == handlerFaultStore.Serialize(handlerFaultProject),
                    "The isolated wafer-handler fault dry run changed the main runtime or project.");
                if (state.Equals(
                        "dry-run-wafer-handler-fault-playback",
                        StringComparison.OrdinalIgnoreCase))
                {
                    var handlerConflictStep = vm.RecipeConnections.RecipeDryRunTimeline.First(step =>
                        step.BoundarySnapshot.WaferHandlers.Any(handler =>
                            handler.State == WaferHandlerOwnershipState.InterlockFault));
                    vm.RecipeConnections.PlayRecipeDryRunStepCommand.Execute(handlerConflictStep);
                    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    AssertSmoke(
                        vm.IsDryRunPlaybackActive
                        && vm.IsDryRunPlaybackWaferHandlerFault
                        && handlerConflictStep.BoundarySnapshot.LayoutComponents.Single(component =>
                            component.Id == handlerConflictStep.BoundarySnapshot.WaferHandlers.Single().WorkpieceComponentId)
                            .TransferOwnershipState == WaferHandlerOwnershipState.InterlockFault
                        && vm.DryRunPlaybackWaferHandlerText.Contains(
                            OpenVisionLanguageService.T("Connections.WaferHandlerState.InterlockFault"),
                            StringComparison.CurrentCulture),
                        "The immutable playback overlay did not expose the wafer-handler interlock fault.");
                    var transferViewport = FindVisualDescendant<MachineSceneViewport>(window)
                        ?? throw new InvalidOperationException("Machine scene viewport was not found.");
                    AssertSmoke(
                        transferViewport.LastRenderedTransferOwnershipState
                            == WaferHandlerOwnershipState.InterlockFault
                        && transferViewport.LastRenderedTransferOwnershipText == "FAULT",
                        "The linked workpiece did not render fail-closed ownership.");
                }
                break;
            case "dry-run-inspection-sort-pass-playback":
                var passSortProject = initialProject
                    ?? throw new InvalidOperationException("A project is required for the inspection-sort PASS state.");
                passSortProject.Devices.Single(device => device.Id == "camera.metrology")
                    .Camera!.PlaceholderDecision = PlaceholderInspectionDecision.Pass;
                vm.SequenceEditor.Load(passSortProject);
                vm.RecipeConnections.Load(passSortProject);
                vm.RecipeConnections.ValidateSimulationReadinessCommand.Execute(null);
                AssertSmoke(
                    vm.RecipeConnections.RunRecipeDryRunCommand.CanExecute(null),
                    "Recipe dry run was not enabled for the inspection-sort PASS state.");
                vm.RecipeConnections.RunRecipeDryRunCommand.Execute(null);
                for (var attempt = 0;
                     attempt < 150 && !vm.RecipeConnections.HasRecipeDryRunResult;
                     attempt++)
                {
                    await Task.Delay(20);
                    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                }
                var passSorter = vm.RecipeConnections.RecipeDryRunResult?.FinalSnapshot?
                    .InspectionSortRouters.Single();
                AssertSmoke(
                    vm.RecipeConnections.RecipeDryRunResult?.Outcome == RecipeDryRunOutcome.Completed
                    && passSorter?.State == InspectionSortRouteState.PassRouted
                    && passSorter.Decision == PlaceholderInspectionDecision.Pass,
                    "The PASS decision did not select only the PASS route.");
                var passRoutedStep = vm.RecipeConnections.RecipeDryRunTimeline.Single(step =>
                    string.Equals(step.StepId, "wait-pass-routed", StringComparison.Ordinal));
                vm.RecipeConnections.PlayRecipeDryRunStepCommand.Execute(passRoutedStep);
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                AssertSmoke(
                    vm.IsDryRunPlaybackActive
                    && vm.HasDryRunPlaybackInspectionSorter
                    && !vm.IsDryRunPlaybackInspectionSorterFault
                    && vm.DryRunPlaybackInspectionSorterText.Contains(
                        OpenVisionLanguageService.T("Connections.InspectionSortState.PassRouted"),
                        StringComparison.CurrentCulture),
                    "The immutable playback overlay did not expose the PASS route.");
                break;
            case "dry-run-inspection-sort-fault-playback":
                var sortFaultProject = initialProject
                    ?? throw new InvalidOperationException("A project is required for the inspection-sort fault state.");
                sortFaultProject.Sequences.Single().Steps.Single(step =>
                    string.Equals(step.Id, "wait-metrology-result", StringComparison.Ordinal))
                    .FailureStepId = "start-pass-transport";
                vm.SequenceEditor.Load(sortFaultProject);
                vm.RecipeConnections.Load(sortFaultProject);
                vm.RecipeConnections.ValidateSimulationReadinessCommand.Execute(null);
                AssertSmoke(
                    vm.RecipeConnections.RunRecipeDryRunCommand.CanExecute(null),
                    "Recipe dry run was not enabled for the inspection-sort fault state.");
                vm.RecipeConnections.RunRecipeDryRunCommand.Execute(null);
                for (var attempt = 0;
                     attempt < 150 && !vm.RecipeConnections.HasRecipeDryRunResult;
                     attempt++)
                {
                    await Task.Delay(20);
                    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                }
                var sortFaultSnapshot = vm.RecipeConnections.RecipeDryRunResult?.FinalSnapshot?
                    .InspectionSortRouters.Single();
                var sortFaultPresentation = vm.RecipeConnections.RecipeDryRunFinalStates.Single(state =>
                    state.IsInspectionSorter);
                AssertSmoke(
                    vm.RecipeConnections.RecipeDryRunResult?.Outcome == RecipeDryRunOutcome.Faulted
                    && sortFaultSnapshot?.State == InspectionSortRouteState.InterlockFault
                    && sortFaultPresentation.IsFault
                    && sortFaultPresentation.Text.Contains(
                        OpenVisionLanguageService.T("Connections.InspectionSortState.InterlockFault"),
                        StringComparison.CurrentCulture),
                    "The wrong inspection route did not expose a fail-closed interlock fault.");
                var sortConflictStep = vm.RecipeConnections.RecipeDryRunTimeline.First(step =>
                    step.BoundarySnapshot.InspectionSortRouters.Any(sorter =>
                        sorter.State == InspectionSortRouteState.InterlockFault));
                vm.RecipeConnections.PlayRecipeDryRunStepCommand.Execute(sortConflictStep);
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                AssertSmoke(
                    vm.IsDryRunPlaybackActive
                    && vm.IsDryRunPlaybackInspectionSorterFault
                    && vm.DryRunPlaybackInspectionSorterText.Contains(
                        OpenVisionLanguageService.T("Connections.InspectionSortState.InterlockFault"),
                        StringComparison.CurrentCulture),
                    "The immutable playback overlay did not expose the inspection-sort interlock fault.");
                break;
            case "dry-run-inspection-handoff-fault-playback":
                var inspectionFaultProject = initialProject
                    ?? throw new InvalidOperationException("A project is required for the inspection-handoff fault state.");
                SequenceDefinition inspectionFaultSequence = inspectionFaultProject.Sequences.Single();
                SequenceStepDefinition waitFocus = inspectionFaultSequence.Steps.Single(step =>
                    string.Equals(step.Id, "wait-ocr-focus", StringComparison.Ordinal));
                const string prematureAcceptanceStepId = "smoke-premature-inspection-accept";
                inspectionFaultSequence.Steps.Insert(
                    inspectionFaultSequence.Steps.IndexOf(waitFocus) + 1,
                    new SequenceStepDefinition
                    {
                        Id = prematureAcceptanceStepId,
                        Name = "Smoke Premature Inspection Acceptance",
                        Action = SequenceStepAction.SetSignal,
                        TargetId = "do.inspection-result-accepted",
                        Parameter = "true",
                        NextStepId = waitFocus.NextStepId
                    });
                waitFocus.NextStepId = prematureAcceptanceStepId;
                vm.SequenceEditor.Load(inspectionFaultProject);
                vm.RecipeConnections.Load(inspectionFaultProject);
                var inspectionFaultStore = new ProjectDocumentStore();
                string inspectionFaultBeforeRun = inspectionFaultStore.Serialize(inspectionFaultProject);
                SimulationSnapshot? inspectionFaultRuntimeBeforeRun = vm.SceneSnapshots.Latest;
                vm.RecipeConnections.ValidateSimulationReadinessCommand.Execute(null);
                AssertSmoke(
                    vm.RecipeConnections.RunRecipeDryRunCommand.CanExecute(null),
                    "Recipe dry run was not enabled for the inspection-handoff fault state.");
                vm.RecipeConnections.RunRecipeDryRunCommand.Execute(null);
                for (var attempt = 0;
                     attempt < 150 && !vm.RecipeConnections.HasRecipeDryRunResult;
                     attempt++)
                {
                    await Task.Delay(20);
                    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                }
                InspectionHandoffSnapshot? inspectionFaultSnapshot = vm.RecipeConnections
                    .RecipeDryRunResult?.FinalSnapshot?.InspectionHandoffs.Single();
                var inspectionFaultPresentation = vm.RecipeConnections.RecipeDryRunFinalStates.Single(state =>
                    state.IsInspectionHandoff);
                AssertSmoke(
                    vm.RecipeConnections.RecipeDryRunResult?.Outcome == RecipeDryRunOutcome.Faulted
                    && inspectionFaultSnapshot?.State == InspectionHandoffState.InterlockFault
                    && !inspectionFaultSnapshot.IsInspectionReady
                    && !inspectionFaultSnapshot.IsInspectionComplete
                    && inspectionFaultPresentation.IsFault
                    && inspectionFaultPresentation.Text.Contains(
                        OpenVisionLanguageService.T("Connections.InspectionHandoffState.InterlockFault"),
                        StringComparison.CurrentCulture),
                    "Premature result acceptance did not expose a fail-closed inspection-handoff fault.");
                AssertSmoke(
                    inspectionFaultRuntimeBeforeRun?.TickIndex == vm.SceneSnapshots.Latest?.TickIndex
                    && inspectionFaultRuntimeBeforeRun?.SimulationTime == vm.SceneSnapshots.Latest?.SimulationTime
                    && inspectionFaultBeforeRun == inspectionFaultStore.Serialize(inspectionFaultProject),
                    "The isolated inspection-handoff fault dry run changed the main runtime or project.");
                var inspectionConflictStep = vm.RecipeConnections.RecipeDryRunTimeline.First(step =>
                    step.BoundarySnapshot.InspectionHandoffs.Any(handoff =>
                        handoff.State == InspectionHandoffState.InterlockFault));
                vm.RecipeConnections.PlayRecipeDryRunStepCommand.Execute(inspectionConflictStep);
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                AssertSmoke(
                    vm.IsDryRunPlaybackActive
                    && vm.IsDryRunPlaybackInspectionHandoffFault
                    && vm.DryRunPlaybackInspectionHandoffText.Contains(
                        OpenVisionLanguageService.T("Connections.InspectionHandoffState.InterlockFault"),
                        StringComparison.CurrentCulture),
                    "The immutable playback overlay did not expose the inspection-handoff interlock fault.");
                break;
            case "dry-run-prealigner-fault-playback":
                var prealignerFaultProject = initialProject
                    ?? throw new InvalidOperationException("A project is required for the pre-aligner fault state.");
                SequenceDefinition prealignerFaultSequence = prealignerFaultProject.Sequences.Single();
                SequenceStepDefinition waitAlignmentReady = prealignerFaultSequence.Steps.Single(step =>
                    string.Equals(step.Id, "wait-alignment-ready", StringComparison.Ordinal));
                const string prematureAlignmentAcceptanceStepId = "smoke-premature-alignment-accept";
                prealignerFaultSequence.Steps.Insert(
                    prealignerFaultSequence.Steps.IndexOf(waitAlignmentReady),
                    new SequenceStepDefinition
                    {
                        Id = prematureAlignmentAcceptanceStepId,
                        Name = "Smoke Premature Alignment Acceptance",
                        Action = SequenceStepAction.SetSignal,
                        TargetId = "do.alignment-accepted",
                        Parameter = "true",
                        NextStepId = waitAlignmentReady.Id
                    });
                SequenceStepDefinition waitClampExtended = prealignerFaultSequence.Steps.Single(step =>
                    string.Equals(step.Id, "wait-cylinder-extended", StringComparison.Ordinal));
                waitClampExtended.NextStepId = prematureAlignmentAcceptanceStepId;
                vm.SequenceEditor.Load(prealignerFaultProject);
                vm.RecipeConnections.Load(prealignerFaultProject);
                var prealignerFaultStore = new ProjectDocumentStore();
                string prealignerFaultBeforeRun = prealignerFaultStore.Serialize(prealignerFaultProject);
                SimulationSnapshot? prealignerFaultRuntimeBeforeRun = vm.SceneSnapshots.Latest;
                vm.RecipeConnections.ValidateSimulationReadinessCommand.Execute(null);
                AssertSmoke(
                    vm.RecipeConnections.RunRecipeDryRunCommand.CanExecute(null),
                    "Recipe dry run was not enabled for the pre-aligner fault state.");
                vm.RecipeConnections.RunRecipeDryRunCommand.Execute(null);
                for (var attempt = 0;
                     attempt < 150 && !vm.RecipeConnections.HasRecipeDryRunResult;
                     attempt++)
                {
                    await Task.Delay(20);
                    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                }
                PrealignerSnapshot? prealignerFaultSnapshot = vm.RecipeConnections
                    .RecipeDryRunResult?.FinalSnapshot?.Prealigners.Single();
                var prealignerFaultPresentation = vm.RecipeConnections.RecipeDryRunFinalStates.Single(state =>
                    state.IsPrealigner);
                AssertSmoke(
                    vm.RecipeConnections.RecipeDryRunResult?.Outcome == RecipeDryRunOutcome.Faulted
                    && prealignerFaultSnapshot?.State == PrealignerState.InterlockFault
                    && !prealignerFaultSnapshot.IsAlignmentReady
                    && !prealignerFaultSnapshot.IsAlignmentComplete
                    && prealignerFaultPresentation.IsFault
                    && prealignerFaultPresentation.Text.Contains(
                        OpenVisionLanguageService.T("Connections.PrealignerState.InterlockFault"),
                        StringComparison.CurrentCulture),
                    "Premature alignment acceptance did not expose a fail-closed pre-aligner fault.");
                AssertSmoke(
                    prealignerFaultRuntimeBeforeRun?.TickIndex == vm.SceneSnapshots.Latest?.TickIndex
                    && prealignerFaultRuntimeBeforeRun?.SimulationTime == vm.SceneSnapshots.Latest?.SimulationTime
                    && prealignerFaultBeforeRun == prealignerFaultStore.Serialize(prealignerFaultProject),
                    "The isolated pre-aligner fault dry run changed the main runtime or project.");
                var prealignerConflictStep = vm.RecipeConnections.RecipeDryRunTimeline.First(step =>
                    step.BoundarySnapshot.Prealigners.Any(prealigner =>
                        prealigner.State == PrealignerState.InterlockFault));
                vm.RecipeConnections.PlayRecipeDryRunStepCommand.Execute(prealignerConflictStep);
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                AssertSmoke(
                    vm.IsDryRunPlaybackActive
                    && vm.IsDryRunPlaybackPrealignerFault
                    && vm.DryRunPlaybackPrealignerText.Contains(
                        OpenVisionLanguageService.T("Connections.PrealignerState.InterlockFault"),
                        StringComparison.CurrentCulture),
                    "The immutable playback overlay did not expose the pre-aligner interlock fault.");
                break;
            case "dry-run-open-step":
                vm.RecipeConnections.ValidateSimulationReadinessCommand.Execute(null);
                vm.RecipeConnections.RunRecipeDryRunCommand.Execute(null);
                for (var attempt = 0;
                     attempt < 150 && !vm.RecipeConnections.HasRecipeDryRunResult;
                     attempt++)
                {
                    await Task.Delay(20);
                    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                }
                var openTimelineStep = FindConnectionDryRunStep(
                    vm.RecipeConnections.RecipeDryRunTimeline,
                    "wait-process-position",
                    "wait-station-position")
                    ?? throw new InvalidOperationException("The dry-run navigation step was not available.");
                vm.RecipeConnections.OpenRecipeDryRunStepCommand.Execute(openTimelineStep);
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                AssertSmoke(
                    vm.SelectedDocumentTabIndex == 2
                    && vm.SequenceEditor.SelectedSequence?.Id == openTimelineStep.SequenceId
                    && vm.SequenceEditor.SelectedStep?.Id == openTimelineStep.StepId,
                    "The dry-run timeline did not open the exact Sequence step.");
                AssertSmoke(
                    vm.RecipeConnections.SelectedRow?.ComponentId == openTimelineStep.ComponentId
                    && vm.Layout.SelectedItem?.Id == openTimelineStep.ComponentId,
                    "The dry-run timeline did not retain the connected equipment selection.");
                break;
            case "dry-run-playback":
            case "dry-run-playback-first":
            case "dry-run-playback-last":
            case "dry-run-playback-control-focus":
            case "dry-run-playback-control-hover":
            case "dry-run-playback-control-pressed":
                vm.RecipeConnections.ValidateSimulationReadinessCommand.Execute(null);
                vm.RecipeConnections.RunRecipeDryRunCommand.Execute(null);
                for (var attempt = 0;
                     attempt < 150 && !vm.RecipeConnections.HasRecipeDryRunResult;
                     attempt++)
                {
                    await Task.Delay(20);
                    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                }
                var playbackStep = state.Equals(
                    "dry-run-playback-first",
                    StringComparison.OrdinalIgnoreCase)
                    ? vm.RecipeConnections.RecipeDryRunTimeline.First()
                    : state.Equals(
                        "dry-run-playback-last",
                        StringComparison.OrdinalIgnoreCase)
                        ? vm.RecipeConnections.RecipeDryRunTimeline.Last()
                        : FindConnectionDryRunStep(
                            vm.RecipeConnections.RecipeDryRunTimeline,
                            "wait-cylinder-extended",
                            "wait-stopper-extended")
                            ?? throw new InvalidOperationException(
                                "The dry-run playback step was not available.");
                vm.RecipeConnections.PlayRecipeDryRunStepCommand.Execute(playbackStep);
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                AssertSmoke(
                    vm.IsDryRunPlaybackActive
                    && vm.SelectedDocumentTabIndex == 0
                    && !vm.IsSceneEditable
                    && ReferenceEquals(vm.SceneSnapshotSource.Latest, playbackStep.BoundarySnapshot),
                    "The selected dry-run boundary was not shown read-only on Machine Layout.");
                if (state.Equals("dry-run-playback-first", StringComparison.OrdinalIgnoreCase))
                {
                    AssertSmoke(
                        !vm.PreviousDryRunPlaybackStepCommand.CanExecute(null),
                        "Previous remained enabled at the first dry-run boundary.");
                    break;
                }
                if (state.Equals("dry-run-playback-last", StringComparison.OrdinalIgnoreCase))
                {
                    AssertSmoke(
                        !vm.NextDryRunPlaybackStepCommand.CanExecute(null),
                        "Next remained enabled at the last dry-run boundary.");
                    break;
                }
                if (state.Equals("dry-run-playback", StringComparison.OrdinalIgnoreCase))
                {
                    if (playbackStep.BoundarySnapshot.LoadLocks is [var vacuumLoadLock])
                    {
                        var vacuumText = vm.DryRunPlaybackLoadLockText;
                        AssertSmoke(
                            vm.HasDryRunPlaybackLoadLock
                            && !vm.IsDryRunPlaybackLoadLockFault
                            && vacuumLoadLock.State == LoadLockState.Vacuum
                            && vacuumLoadLock.IsVacuumReady
                            && !vacuumLoadLock.IsAtmosphereReady
                            && !vacuumLoadLock.IsOuterDoorPermitted
                            && vacuumLoadLock.IsInnerDoorPermitted
                            && vacuumText.Contains(
                                OpenVisionLanguageService.T("Connections.LoadLockState.Vacuum"),
                                StringComparison.CurrentCulture),
                            "The vacuum playback boundary did not expose load-lock readiness and door permissions.");
                        var atmosphereStep = vm.RecipeConnections.RecipeDryRunTimeline.Single(step =>
                            string.Equals(step.StepId, "wait-atmosphere-ready", StringComparison.Ordinal));
                        vm.RecipeConnections.PlayRecipeDryRunStepCommand.Execute(atmosphereStep);
                        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                        var atmosphereLoadLock = atmosphereStep.BoundarySnapshot.LoadLocks.Single();
                        AssertSmoke(
                            atmosphereLoadLock.State == LoadLockState.Atmosphere
                            && !atmosphereLoadLock.IsVacuumReady
                            && atmosphereLoadLock.IsAtmosphereReady
                            && atmosphereLoadLock.IsOuterDoorPermitted
                            && !atmosphereLoadLock.IsInnerDoorPermitted
                            && !string.Equals(
                                vacuumText,
                                vm.DryRunPlaybackLoadLockText,
                                StringComparison.CurrentCulture),
                            "Load-lock playback did not change from the vacuum boundary to the atmosphere boundary.");
                        vm.RecipeConnections.PlayRecipeDryRunStepCommand.Execute(playbackStep);
                        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    }
                    if (playbackStep.BoundarySnapshot.OhtHandoffs.Count == 1)
                    {
                        AssertSmoke(
                            vm.HasDryRunPlaybackOhtHandoff
                            && !vm.IsDryRunPlaybackOhtHandoffFault
                            && playbackStep.BoundarySnapshot.OhtHandoffs.Single().State
                                == OhtHandoffOwnershipState.Ready
                            && vm.DryRunPlaybackOhtHandoffText.Contains(
                                OpenVisionLanguageService.T("Connections.OhtHandoffState.Ready"),
                                StringComparison.CurrentCulture),
                            "OHT playback did not expose ready vehicle-to-load-port ownership.");
                        var receivedStep = vm.RecipeConnections.RecipeDryRunTimeline.Single(step =>
                            string.Equals(step.StepId, "wait-oht-carrier-transferred", StringComparison.Ordinal));
                        vm.RecipeConnections.PlayRecipeDryRunStepCommand.Execute(receivedStep);
                        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                        AssertSmoke(
                            receivedStep.BoundarySnapshot.OhtHandoffs.Single().State
                                == OhtHandoffOwnershipState.LoadPort
                            && vm.DryRunPlaybackOhtHandoffText.Contains(
                                OpenVisionLanguageService.T("Connections.OhtHandoffState.LoadPort"),
                                StringComparison.CurrentCulture),
                            "OHT playback did not expose load-port ownership after receipt.");
                        vm.RecipeConnections.PlayRecipeDryRunStepCommand.Execute(playbackStep);
                        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    }
                    if (playbackStep.BoundarySnapshot.WaferHandlers.Count == 1)
                    {
                        var holdingStep = vm.RecipeConnections.RecipeDryRunTimeline.Single(step =>
                            string.Equals(step.StepId, "wait-handler-holding", StringComparison.Ordinal));
                        vm.RecipeConnections.PlayRecipeDryRunStepCommand.Execute(holdingStep);
                        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                        AssertSmoke(
                            vm.HasDryRunPlaybackWaferHandler
                            && !vm.IsDryRunPlaybackWaferHandlerFault
                            && holdingStep.BoundarySnapshot.WaferHandlers.Single().State
                                == WaferHandlerOwnershipState.Handler
                            && holdingStep.BoundarySnapshot.LayoutComponents.Single(component =>
                                component.Id == holdingStep.BoundarySnapshot.WaferHandlers.Single().WorkpieceComponentId)
                                .TransferOwnershipState == WaferHandlerOwnershipState.Handler
                            && vm.DryRunPlaybackWaferHandlerText.Contains(
                                OpenVisionLanguageService.T("Connections.WaferHandlerState.Handler"),
                                StringComparison.CurrentCulture),
                            "Wafer-handler playback did not expose handler ownership after pick.");
                        var transferViewport = FindVisualDescendant<MachineSceneViewport>(window)
                            ?? throw new InvalidOperationException("Machine scene viewport was not found.");
                        AssertSmoke(
                            transferViewport.LastRenderedTransferOwnershipState
                                == WaferHandlerOwnershipState.Handler
                            && transferViewport.LastRenderedTransferOwnershipText == "HANDLER",
                            "The linked workpiece did not render handler ownership.");
                        var placedStep = vm.RecipeConnections.RecipeDryRunTimeline.Single(step =>
                            string.Equals(step.StepId, "wait-handler-placed", StringComparison.Ordinal));
                        vm.RecipeConnections.PlayRecipeDryRunStepCommand.Execute(placedStep);
                        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                        AssertSmoke(
                            placedStep.BoundarySnapshot.WaferHandlers.Single().State
                                == WaferHandlerOwnershipState.Destination
                            && placedStep.BoundarySnapshot.LayoutComponents.Single(component =>
                                component.Id == placedStep.BoundarySnapshot.WaferHandlers.Single().WorkpieceComponentId)
                                .TransferOwnershipState == WaferHandlerOwnershipState.Destination
                            && vm.DryRunPlaybackWaferHandlerText.Contains(
                                OpenVisionLanguageService.T("Connections.WaferHandlerState.Destination"),
                                StringComparison.CurrentCulture),
                            "Wafer-handler playback did not expose destination ownership after place.");
                        AssertSmoke(
                            transferViewport.LastRenderedTransferOwnershipState
                                == WaferHandlerOwnershipState.Destination
                            && transferViewport.LastRenderedTransferOwnershipText == "DEST",
                            "The linked workpiece did not render destination ownership.");
                        vm.RecipeConnections.PlayRecipeDryRunStepCommand.Execute(holdingStep);
                        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    }
                    if (playbackStep.BoundarySnapshot.InspectionSortRouters.Count == 1)
                    {
                        var routedStep = vm.RecipeConnections.RecipeDryRunTimeline.Single(step =>
                            string.Equals(step.StepId, "wait-ng-routed", StringComparison.Ordinal));
                        vm.RecipeConnections.PlayRecipeDryRunStepCommand.Execute(routedStep);
                        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                        AssertSmoke(
                            vm.HasDryRunPlaybackInspectionSorter
                            && !vm.IsDryRunPlaybackInspectionSorterFault
                            && routedStep.BoundarySnapshot.InspectionSortRouters.Single().State
                                == InspectionSortRouteState.NgRouted
                            && vm.DryRunPlaybackInspectionSorterText.Contains(
                                OpenVisionLanguageService.T("Connections.InspectionSortState.NgRouted"),
                                StringComparison.CurrentCulture),
                            "Inspection-sorter playback did not expose the NG route selection.");
                    }
                    if (playbackStep.BoundarySnapshot.InspectionHandoffs.Count == 1)
                    {
                        var readyStep = vm.RecipeConnections.RecipeDryRunTimeline.Single(step =>
                            string.Equals(step.StepId, "wait-inspection-ready", StringComparison.Ordinal));
                        vm.RecipeConnections.PlayRecipeDryRunStepCommand.Execute(readyStep);
                        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                        AssertSmoke(
                            vm.HasDryRunPlaybackInspectionHandoff
                            && !vm.IsDryRunPlaybackInspectionHandoffFault
                            && readyStep.BoundarySnapshot.InspectionHandoffs.Single().State
                                == InspectionHandoffState.Ready
                            && vm.DryRunPlaybackInspectionHandoffText.Contains(
                                OpenVisionLanguageService.T("Connections.InspectionHandoffState.Ready"),
                                StringComparison.CurrentCulture),
                            "Inspection-handoff playback did not expose the ready boundary.");
                        var completedStep = vm.RecipeConnections.RecipeDryRunTimeline.Single(step =>
                            string.Equals(step.StepId, "wait-inspection-complete", StringComparison.Ordinal));
                        vm.RecipeConnections.PlayRecipeDryRunStepCommand.Execute(completedStep);
                        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                        AssertSmoke(
                            completedStep.BoundarySnapshot.InspectionHandoffs.Single().State
                                == InspectionHandoffState.Complete
                            && vm.DryRunPlaybackInspectionHandoffText.Contains(
                                OpenVisionLanguageService.T("Connections.InspectionHandoffState.Complete"),
                                StringComparison.CurrentCulture),
                            "Inspection-handoff playback did not expose result acceptance and completion.");
                    }
                    if (playbackStep.BoundarySnapshot.Prealigners.Count == 1)
                    {
                        AssertSmoke(
                            vm.HasDryRunPlaybackPrealigner
                            && !vm.IsDryRunPlaybackPrealignerFault
                            && playbackStep.BoundarySnapshot.Prealigners.Single().State == PrealignerState.Ready
                            && vm.DryRunPlaybackPrealignerText.Contains(
                                OpenVisionLanguageService.T("Connections.PrealignerState.Ready"),
                                StringComparison.CurrentCulture),
                            "Pre-aligner playback did not expose the clamped ready boundary.");
                        var alignedStep = vm.RecipeConnections.RecipeDryRunTimeline.Single(step =>
                            string.Equals(step.StepId, "wait-alignment-complete", StringComparison.Ordinal));
                        vm.RecipeConnections.PlayRecipeDryRunStepCommand.Execute(alignedStep);
                        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                        AssertSmoke(
                            alignedStep.BoundarySnapshot.Prealigners.Single().State == PrealignerState.Aligned
                            && vm.DryRunPlaybackPrealignerText.Contains(
                                OpenVisionLanguageService.T("Connections.PrealignerState.Aligned"),
                                StringComparison.CurrentCulture),
                            "Pre-aligner playback did not expose accepted target alignment.");
                        var releasedStep = vm.RecipeConnections.RecipeDryRunTimeline.Single(step =>
                            string.Equals(step.StepId, "wait-cylinder-retracted", StringComparison.Ordinal));
                        vm.RecipeConnections.PlayRecipeDryRunStepCommand.Execute(releasedStep);
                        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                        AssertSmoke(
                            releasedStep.BoundarySnapshot.Prealigners.Single().State == PrealignerState.Released
                            && vm.DryRunPlaybackPrealignerText.Contains(
                                OpenVisionLanguageService.T("Connections.PrealignerState.Released"),
                                StringComparison.CurrentCulture),
                            "Pre-aligner playback did not expose safe clamp release.");
                        vm.RecipeConnections.PlayRecipeDryRunStepCommand.Execute(playbackStep);
                        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    }
                    break;
                }
                var playbackControl = FindVisualDescendant<Button>(window, candidate =>
                    ReferenceEquals(candidate.Command, vm.NextDryRunPlaybackStepCommand))
                    ?? throw new InvalidOperationException("The dry-run playback Next button was not available.");
                interaction.ActivateWindow();
                playbackControl.Focus();
                Keyboard.Focus(playbackControl);
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                AssertSmoke(playbackControl.IsKeyboardFocused, "The playback Next button did not receive focus.");
                if (state.Equals("dry-run-playback-control-focus", StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }
                interaction.MovePointerToCenter(playbackControl);
                Mouse.Capture(playbackControl, CaptureMode.SubTree);
                Mouse.Synchronize();
                await Task.Delay(200);
                AssertSmoke(playbackControl.IsMouseOver, "The playback Next button did not enter hover state.");
                if (state.Equals("dry-run-playback-control-pressed", StringComparison.OrdinalIgnoreCase))
                {
                    interaction.MouseEvent(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
                    interaction.MarkSmokePointerHeld();
                    playbackControl.RaiseEvent(new MouseButtonEventArgs(
                        Mouse.PrimaryDevice,
                        Environment.TickCount,
                        MouseButton.Left)
                    {
                        RoutedEvent = Mouse.MouseDownEvent
                    });
                    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    AssertSmoke(playbackControl.IsPressed, "The playback Next button did not enter pointer-down state.");
                }
                break;
            case "dry-run-playback-entry-focus":
            case "dry-run-playback-entry-hover":
            case "dry-run-playback-entry-pressed":
            case "dry-run-playback-entry-disabled":
                vm.RecipeConnections.ValidateSimulationReadinessCommand.Execute(null);
                vm.RecipeConnections.RunRecipeDryRunCommand.Execute(null);
                for (var attempt = 0;
                     attempt < 150 && !vm.RecipeConnections.HasRecipeDryRunResult;
                     attempt++)
                {
                    await Task.Delay(20);
                    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                }
                var playbackEntryStep = FindConnectionDryRunStep(
                    vm.RecipeConnections.RecipeDryRunTimeline,
                    "wait-cylinder-extended",
                    "wait-stopper-extended")
                    ?? throw new InvalidOperationException(
                        "The dry-run playback entry step was not available.");
                var playbackEntryButton = FindVisualDescendant<Button>(workbench, candidate =>
                    ReferenceEquals(candidate.Command, vm.RecipeConnections.PlayRecipeDryRunStepCommand)
                    && ReferenceEquals(candidate.CommandParameter, playbackEntryStep))
                    ?? throw new InvalidOperationException("The dry-run playback entry button was not available.");
                if (state.Equals("dry-run-playback-entry-disabled", StringComparison.OrdinalIgnoreCase))
                {
                    vm.IsRunMode = true;
                    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    AssertSmoke(!playbackEntryButton.IsEnabled, "The playback entry remained enabled in Run mode.");
                    break;
                }
                interaction.ActivateWindow();
                playbackEntryButton.BringIntoView();
                playbackEntryButton.Focus();
                Keyboard.Focus(playbackEntryButton);
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                AssertSmoke(playbackEntryButton.IsKeyboardFocused, "The playback entry did not receive focus.");
                if (state.Equals("dry-run-playback-entry-focus", StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }
                interaction.MovePointerToCenter(playbackEntryButton);
                Mouse.Capture(playbackEntryButton, CaptureMode.SubTree);
                Mouse.Synchronize();
                await Task.Delay(200);
                AssertSmoke(playbackEntryButton.IsMouseOver, "The playback entry did not enter hover state.");
                if (state.Equals("dry-run-playback-entry-pressed", StringComparison.OrdinalIgnoreCase))
                {
                    interaction.MouseEvent(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
                    interaction.MarkSmokePointerHeld();
                    playbackEntryButton.RaiseEvent(new MouseButtonEventArgs(
                        Mouse.PrimaryDevice,
                        Environment.TickCount,
                        MouseButton.Left)
                    {
                        RoutedEvent = Mouse.MouseDownEvent
                    });
                    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    AssertSmoke(playbackEntryButton.IsPressed, "The playback entry did not enter pointer-down state.");
                }
                break;
            case "dry-run-timeline-focus":
            case "dry-run-timeline-hover":
            case "dry-run-timeline-pressed":
            case "dry-run-timeline-disabled":
                vm.RecipeConnections.ValidateSimulationReadinessCommand.Execute(null);
                vm.RecipeConnections.RunRecipeDryRunCommand.Execute(null);
                for (var attempt = 0;
                     attempt < 150 && !vm.RecipeConnections.HasRecipeDryRunResult;
                     attempt++)
                {
                    await Task.Delay(20);
                    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                }
                var timelineStep = FindConnectionDryRunStep(
                    vm.RecipeConnections.RecipeDryRunTimeline,
                    "wait-process-position",
                    "wait-station-position")
                    ?? throw new InvalidOperationException("The dry-run timeline visual-state step was not available.");
                vm.RecipeConnections.SelectedRecipeDryRunStep = timelineStep;
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                var timelineButton = FindVisualDescendant<Button>(workbench, candidate =>
                    ReferenceEquals(candidate.Command, vm.RecipeConnections.OpenRecipeDryRunStepCommand)
                    && ReferenceEquals(candidate.CommandParameter, timelineStep))
                    ?? throw new InvalidOperationException("The dry-run timeline button was not available.");
                if (state.Equals("dry-run-timeline-disabled", StringComparison.OrdinalIgnoreCase))
                {
                    vm.IsRunMode = true;
                    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    AssertSmoke(!timelineButton.IsEnabled, "The dry-run timeline button remained enabled in Run mode.");
                    break;
                }
                interaction.ActivateWindow();
                timelineButton.BringIntoView();
                timelineButton.Focus();
                Keyboard.Focus(timelineButton);
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                AssertSmoke(timelineButton.IsKeyboardFocused, "The dry-run timeline button did not receive focus.");
                if (state.Equals("dry-run-timeline-focus", StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }
                interaction.MovePointerToCenter(timelineButton);
                Mouse.Capture(timelineButton, CaptureMode.SubTree);
                Mouse.Synchronize();
                await Task.Delay(200);
                AssertSmoke(timelineButton.IsMouseOver, "The dry-run timeline button did not enter hover state.");
                if (state.Equals("dry-run-timeline-pressed", StringComparison.OrdinalIgnoreCase))
                {
                    interaction.MouseEvent(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
                    interaction.MarkSmokePointerHeld();
                    timelineButton.RaiseEvent(new MouseButtonEventArgs(
                        Mouse.PrimaryDevice,
                        Environment.TickCount,
                        MouseButton.Left)
                    {
                        RoutedEvent = Mouse.MouseDownEvent
                    });
                    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    AssertSmoke(timelineButton.IsPressed, "The dry-run timeline button did not enter pointer-down state.");
                }
                break;
            case "dry-run-focus":
            case "dry-run-hover":
            case "dry-run-pressed":
                vm.RecipeConnections.ValidateSimulationReadinessCommand.Execute(null);
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                interaction.ActivateWindow();
                dryRunButton.BringIntoView();
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                dryRunButton.UpdateLayout();
                dryRunButton.Focus();
                Keyboard.Focus(dryRunButton);
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                AssertSmoke(dryRunButton.IsKeyboardFocused, "Recipe dry-run button did not receive focus.");
                if (state.Equals("dry-run-focus", StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }
                interaction.MovePointerToCenter(dryRunButton);
                Mouse.Capture(dryRunButton, CaptureMode.SubTree);
                Mouse.Synchronize();
                await Task.Delay(200);
                if (state.Equals("dry-run-hover", StringComparison.OrdinalIgnoreCase))
                {
                    AssertSmoke(dryRunButton.IsMouseOver, "Recipe dry-run button did not enter hover state.");
                }
                else
                {
                    interaction.MouseEvent(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
                    interaction.MarkSmokePointerHeld();
                    dryRunButton.RaiseEvent(new MouseButtonEventArgs(
                        Mouse.PrimaryDevice,
                        Environment.TickCount,
                        MouseButton.Left)
                    {
                        RoutedEvent = Mouse.MouseDownEvent
                    });
                    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    AssertSmoke(dryRunButton.IsPressed, "Recipe dry-run button did not enter pointer-down state.");
                }
                break;

        }
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

    private static void AssertSmoke(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
