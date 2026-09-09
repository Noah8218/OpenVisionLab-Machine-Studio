using OpenVisionLab.Machine.Core.Axes;
using OpenVisionLab.Machine.Core.Devices;
using OpenVisionLab.Machine.Core.Layouts;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Core.Sequences;
using OpenVisionLab.Machine.Simulation.Commands;
using OpenVisionLab.Machine.Simulation.Compilation;
using OpenVisionLab.Machine.Simulation.Engine;
using OpenVisionLab.Machine.Simulation.Layout;
using OpenVisionLab.Machine.Simulation.Snapshots;
using Xunit;

namespace OpenVisionLab.Machine.Simulation.Tests;

public sealed class SemiconductorRecipeEndToEndTests
{
    private const int MaximumStepCount = 2_000;
    private static readonly TimeSpan FixedStep = TimeSpan.FromMilliseconds(5);
    private static readonly IReadOnlyDictionary<string, RecipeProfile> Profiles =
        new Dictionary<string, RecipeProfile>(StringComparer.Ordinal)
        {
            ["01-FoupLoadPort.ovmachine"] = new(1, 2, 1, 1, 1, 0, 0, 0, 0, 1, 0, 7, 14),
            ["02-CassetteMapper.ovmachine"] = new(2, 3, 1, 1, 1, 0, 0, 0, 0, 0, 0, 9, 15),
            ["03-WaferPrealigner.ovmachine"] = new(2, 2, 1, 1, 1, 0, 0, 0, 0, 0, 1, 8, 17),
            ["04-WaferOcrInspection.ovmachine"] = new(2, 3, 1, 1, 1, 0, 0, 0, 1, 0, 0, 9, 21),
            ["05-LoadLockEntry.ovmachine"] = new(1, 2, 2, 1, 1, 1, 0, 0, 0, 0, 0, 8, 22),
            ["06-SpinCoatTrack.ovmachine"] = new(3, 2, 1, 1, 1, 0, 0, 0, 0, 0, 0, 9, 16),
            ["07-DevelopTrack.ovmachine"] = new(1, 3, 2, 1, 1, 0, 0, 0, 0, 0, 0, 9, 17),
            ["08-DryEtchTransfer.ovmachine"] = new(2, 3, 2, 1, 1, 0, 1, 0, 0, 0, 0, 10, 27),
            ["09-CmpTransfer.ovmachine"] = new(3, 2, 2, 1, 1, 0, 0, 0, 0, 0, 0, 10, 20),
            ["10-MetrologySorter.ovmachine"] = new(1, 3, 1, 2, 2, 0, 0, 1, 0, 0, 0, 10, 21)
        };

    [Fact]
    public async Task PersistedRecipes_CompileAndCompleteConnectedEquipmentCycle()
    {
        string recipeDirectory = Path.Combine(AppContext.BaseDirectory, "SemiconductorRecipes");
        string[] paths = Directory.GetFiles(recipeDirectory, "*.ovmachine")
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(10, paths.Length);
        var fingerprints = new HashSet<string>(StringComparer.Ordinal);
        foreach (string path in paths)
        {
            MachineProjectDocument project = new ProjectDocumentStore().Load(File.ReadAllText(path));
            string fileName = Path.GetFileName(path);
            Assert.True(Profiles.TryGetValue(fileName, out RecipeProfile? profile));
            VerifyProfile(fileName, project, profile!);
            Assert.True(
                fingerprints.Add(Fingerprint(project)),
                $"{fileName}: equipment topology and sequence fingerprint was duplicated.");
            await VerifyRecipeAsync(fileName, project);
        }

        Assert.Equal(10, fingerprints.Count);
    }

    [Fact]
    public async Task MetrologySorter_PassDecision_CompletesOnlyPassRoute()
    {
        string path = Path.Combine(
            AppContext.BaseDirectory,
            "SemiconductorRecipes",
            "10-MetrologySorter.ovmachine");
        MachineProjectDocument project = new ProjectDocumentStore().Load(File.ReadAllText(path));
        project.Devices.Single(device => device.Id == "camera.metrology")
            .Camera!.PlaceholderDecision = PlaceholderInspectionDecision.Pass;
        MachineProjectRuntimeCompilationResult compilation =
            new MachineProjectRuntimeCompiler(FixedStep).Compile(project);
        Assert.True(compilation.IsSuccess, ErrorSummary(compilation));

        using var engine = new FixedStepSimulationEngine(new SimulationSettings
        {
            FixedStep = FixedStep,
            TimeScale = 0.000001,
            Seed = project.Simulation.Seed
        });
        await engine.StartAsync();
        Assert.True((await engine.EnqueueCommandAsync(
            new ConfigureRuntimeCommand(compilation.Configuration!))).IsAccepted);
        Assert.True((await engine.EnqueueCommandAsync(new StartAutomaticRunCommand())).IsAccepted);
        Assert.True((await engine.EnqueueCommandAsync(new PauseCommand())).IsAccepted);

        int steps = 0;
        while (steps < MaximumStepCount && engine.CurrentSnapshot.AutomaticRun.CompletedCycleCount < 1)
        {
            Assert.True((await engine.EnqueueCommandAsync(new StepCommand())).IsAccepted);
            steps++;
        }

        Assert.True(steps < MaximumStepCount);
        InspectionSortRouterSnapshot sorter = Assert.Single(engine.CurrentSnapshot.InspectionSortRouters);
        Assert.Equal(InspectionSortRouteState.PassRouted, sorter.State);
        Assert.Equal(PlaceholderInspectionDecision.Pass, sorter.Decision);
        Assert.True(engine.CurrentSnapshot.Signals.Single(signal => signal.Id == "di.sort.pass-routed").Value);
        Assert.False(engine.CurrentSnapshot.Signals.Single(signal => signal.Id == "di.sort.ng-routed").Value);
        await engine.StopAsync();
    }

    private static void VerifyProfile(
        string fileName,
        MachineProjectDocument project,
        RecipeProfile profile)
    {
        MachineLayoutDefinition layout = Assert.Single(project.Layouts);
        SequenceDefinition sequence = Assert.Single(project.Sequences);

        Assert.Equal(profile.AxisCount, project.Axes.Count);
        Assert.Equal(profile.SensorCount, project.Devices.Count(device => device is { Kind: DeviceKind.Sensor, Sensor: not null }));
        Assert.Equal(profile.CylinderCount, project.Devices.Count(device => device is { Kind: DeviceKind.Cylinder, Cylinder: not null }));
        Assert.Equal(profile.ConveyorCount, project.Devices.Count(device => device is { Kind: DeviceKind.Conveyor, Conveyor: not null }));
        Assert.Equal(profile.WorkpieceCount, project.Devices.Count(device => device is { Kind: DeviceKind.Workpiece, Workpiece: not null }));
        Assert.Equal(profile.LoadLockCount, project.Devices.Count(device => device is { Kind: DeviceKind.LoadLock, LoadLock: not null }));
        Assert.Equal(profile.WaferHandlerCount, project.Devices.Count(device => device is { Kind: DeviceKind.Handler, WaferHandler: not null }));
        Assert.Equal(profile.InspectionSorterCount, project.Devices.Count(device => device is { Kind: DeviceKind.Sorter, InspectionSortRouter: not null }));
        Assert.Equal(profile.InspectionHandoffCount, project.Devices.Count(device => device is { Kind: DeviceKind.Inspection, InspectionHandoff: not null }));
        Assert.Equal(profile.OhtHandoffCount, project.Devices.Count(device => device is { Kind: DeviceKind.Oht, OhtHandoff: not null }));
        Assert.Equal(profile.PrealignerCount, project.Devices.Count(device => device is { Kind: DeviceKind.Prealigner, Prealigner: not null }));
        Assert.Equal(profile.ComponentCount, layout.Components.Count);
        Assert.Equal(profile.StepCount, sequence.Steps.Count);
        Assert.NotNull(project.Simulation.AutomaticRun);

        foreach (var axis in project.Axes)
        {
            LayoutComponentKind expectedStageKind = axis.Kind == AxisKind.Rotary
                ? LayoutComponentKind.RotaryStage
                : LayoutComponentKind.LinearStage;
            Assert.Contains(layout.Components, component =>
                component.Kind == expectedStageKind &&
                component.BehaviorBindingId == axis.Id);
            Assert.Contains(sequence.Steps, step =>
                step.Action == SequenceStepAction.MoveAxis && step.TargetId == axis.Id);
            Assert.Contains(sequence.Steps, step =>
                step.Action == SequenceStepAction.WaitAxisDone && step.TargetId == axis.Id);
        }

        foreach (var device in project.Devices)
        {
            if (device.Kind is not (DeviceKind.LoadLock or DeviceKind.Handler or DeviceKind.Sorter or DeviceKind.Inspection or DeviceKind.Oht or DeviceKind.Prealigner or DeviceKind.Camera))
            {
                Assert.Contains(layout.Components, component => component.BehaviorBindingId == device.Id);
            }
        }

        foreach (var cylinder in project.Devices.Where(device => device.Cylinder is not null))
        {
            Assert.Contains(sequence.Steps, step =>
                step.Action == SequenceStepAction.SetSignal &&
                step.TargetId == cylinder.Cylinder!.ExtendCommandChannelId &&
                step.Parameter == "true");
            Assert.Contains(sequence.Steps, step =>
                step.Action == SequenceStepAction.SetSignal &&
                step.TargetId == cylinder.Cylinder!.ExtendCommandChannelId &&
                step.Parameter == "false");
        }

        foreach (var conveyor in project.Devices.Where(device => device.Conveyor is not null))
        {
            Assert.Contains(sequence.Steps, step =>
                step.Action == SequenceStepAction.SetSignal &&
                step.TargetId == conveyor.Conveyor!.RunCommandChannelId &&
                step.Parameter == "true");
            Assert.Contains(sequence.Steps, step =>
                step.Action == SequenceStepAction.SetSignal &&
                step.TargetId == conveyor.Conveyor!.RunCommandChannelId &&
                step.Parameter == "false");
        }
    }

    private static async Task VerifyRecipeAsync(
        string fileName,
        MachineProjectDocument project)
    {
        MachineLayoutDefinition layout = Assert.Single(project.Layouts);

        MachineProjectRuntimeCompilationResult compilation =
            new MachineProjectRuntimeCompiler(FixedStep).Compile(project);
        Assert.True(compilation.IsSuccess, $"{fileName}: {ErrorSummary(compilation)}");

        using var engine = new FixedStepSimulationEngine(new SimulationSettings
        {
            FixedStep = FixedStep,
            TimeScale = 0.000001,
            Seed = project.Simulation.Seed
        });
        await engine.StartAsync();
        SimulationCommandResult configured = await engine.EnqueueCommandAsync(
            new ConfigureRuntimeCommand(compilation.Configuration!));
        Assert.True(configured.IsAccepted, $"{fileName}: {configured.Detail}");

        SimulationSnapshot initial = engine.CurrentSnapshot;
        var initialAxisPositions = initial.Axes.ToDictionary(axis => axis.Id, axis => axis.Position, StringComparer.Ordinal);
        var rotaryStages = layout.Components
            .Where(component => component.Kind == LayoutComponentKind.RotaryStage)
            .ToDictionary(component => component.BehaviorBindingId!, StringComparer.Ordinal);
        var initialRotaryStagePoses = rotaryStages.Values.ToDictionary(
            component => component.Id,
            component => (component.Transform.X, component.Transform.Y),
            StringComparer.Ordinal);
        var initialWorkpiecePositions = layout.Components
            .Where(component => component.Kind == LayoutComponentKind.Workpiece)
            .ToDictionary(
                component => component.Id,
                component => Component(initial, component.Id).X,
                StringComparer.Ordinal);
        SimulationCommandResult started = await engine.EnqueueCommandAsync(
            new StartAutomaticRunCommand());
        SimulationCommandResult paused = await engine.EnqueueCommandAsync(new PauseCommand());
        Assert.True(started.IsAccepted, $"{fileName}: {started.Detail}");
        Assert.True(paused.IsAccepted, $"{fileName}: {paused.Detail}");

        var movedAxes = new HashSet<string>(StringComparer.Ordinal);
        var rotatedStages = new HashSet<string>(StringComparer.Ordinal);
        var extendedCylinders = new HashSet<string>(StringComparer.Ordinal);
        var retractedCylinders = new HashSet<string>(StringComparer.Ordinal);
        var detectedSensors = new HashSet<string>(StringComparer.Ordinal);
        var transportedWorkpieces = new HashSet<string>(StringComparer.Ordinal);
        var observedLoadLockStates = new HashSet<LoadLockState>();
        var observedHandlerStates = new HashSet<WaferHandlerOwnershipState>();
        var observedSorterStates = new HashSet<InspectionSortRouteState>();
        var observedInspectionHandoffStates = new HashSet<InspectionHandoffState>();
        var observedOhtHandoffStates = new HashSet<OhtHandoffOwnershipState>();
        var observedPrealignerStates = new HashSet<PrealignerState>();
        int steps = 0;

        while (steps < MaximumStepCount && engine.CurrentSnapshot.AutomaticRun.CompletedCycleCount < 1)
        {
            SimulationCommandResult step = await engine.EnqueueCommandAsync(new StepCommand());
            Assert.True(step.IsAccepted, $"{fileName}: {step.Detail}");
            steps++;

            SimulationSnapshot snapshot = engine.CurrentSnapshot;
            foreach (LoadLockSnapshot loadLock in snapshot.LoadLocks)
            {
                observedLoadLockStates.Add(loadLock.State);
                LayoutComponentSnapshot outerDoor = Component(snapshot, loadLock.OuterDoorComponentId);
                LayoutComponentSnapshot innerDoor = Component(snapshot, loadLock.InnerDoorComponentId);
                Assert.False(
                    outerDoor.MotionProgress > 0 && innerDoor.MotionProgress > 0,
                    $"{fileName}: both load-lock doors were open on tick {snapshot.TickIndex}.");
            }
            foreach (WaferHandlerSnapshot handler in snapshot.WaferHandlers)
            {
                observedHandlerStates.Add(handler.State);
                Assert.NotEqual(WaferHandlerOwnershipState.InterlockFault, handler.State);
            }
            foreach (InspectionSortRouterSnapshot sorter in snapshot.InspectionSortRouters)
            {
                observedSorterStates.Add(sorter.State);
                Assert.NotEqual(InspectionSortRouteState.InterlockFault, sorter.State);
            }
            foreach (InspectionHandoffSnapshot handoff in snapshot.InspectionHandoffs)
            {
                observedInspectionHandoffStates.Add(handoff.State);
                Assert.NotEqual(InspectionHandoffState.InterlockFault, handoff.State);
            }
            foreach (OhtHandoffSnapshot handoff in snapshot.OhtHandoffs)
            {
                observedOhtHandoffStates.Add(handoff.State);
                Assert.NotEqual(OhtHandoffOwnershipState.InterlockFault, handoff.State);
            }
            foreach (PrealignerSnapshot prealigner in snapshot.Prealigners)
            {
                observedPrealignerStates.Add(prealigner.State);
                Assert.NotEqual(PrealignerState.InterlockFault, prealigner.State);
            }
            foreach (var axis in snapshot.Axes)
            {
                if (Math.Abs(axis.Position - initialAxisPositions[axis.Id]) > 0.001)
                {
                    movedAxes.Add(axis.Id);
                }

                if (rotaryStages.TryGetValue(axis.Id, out LayoutComponentDefinition? rotaryStage))
                {
                    LayoutComponentSnapshot stageSnapshot = Component(snapshot, rotaryStage.Id);
                    Assert.Equal(initialRotaryStagePoses[rotaryStage.Id].X, stageSnapshot.X, 9);
                    Assert.Equal(initialRotaryStagePoses[rotaryStage.Id].Y, stageSnapshot.Y, 9);
                    Assert.Equal(
                        rotaryStage.Transform.RotationDegrees + axis.Position -
                        project.Axes.Single(candidate => candidate.Id == axis.Id).HomePosition,
                        stageSnapshot.RotationDegrees,
                        9);
                    if (Math.Abs(stageSnapshot.RotationDegrees - rotaryStage.Transform.RotationDegrees) > 0.001)
                    {
                        rotatedStages.Add(rotaryStage.Id);
                    }
                }
            }

            foreach (var component in layout.Components)
            {
                LayoutComponentSnapshot componentSnapshot = Component(snapshot, component.Id);
                if (component.Kind == LayoutComponentKind.PneumaticCylinder)
                {
                    if (componentSnapshot.CylinderState == PneumaticCylinderState.Extended)
                    {
                        extendedCylinders.Add(component.Id);
                    }
                    else if (extendedCylinders.Contains(component.Id) &&
                             componentSnapshot.CylinderState == PneumaticCylinderState.Retracted)
                    {
                        retractedCylinders.Add(component.Id);
                    }
                }
                else if (component.Kind == LayoutComponentKind.DigitalSensor &&
                         componentSnapshot.IsDetected == true)
                {
                    detectedSensors.Add(component.Id);
                }
                else if (component.Kind == LayoutComponentKind.Workpiece &&
                         componentSnapshot.X > initialWorkpiecePositions[component.Id] + 1)
                {
                    transportedWorkpieces.Add(component.Id);
                }
            }
        }

        Assert.True(steps < MaximumStepCount, $"{fileName}: automatic cycle exceeded the step budget.");
        Assert.Equal(1, engine.CurrentSnapshot.AutomaticRun.CompletedCycleCount);
        Assert.Equal(project.Axes.Count, movedAxes.Count);
        Assert.Equal(rotaryStages.Count, rotatedStages.Count);
        Assert.Equal(ProfileCount(layout, LayoutComponentKind.PneumaticCylinder), extendedCylinders.Count);
        Assert.Equal(ProfileCount(layout, LayoutComponentKind.PneumaticCylinder), retractedCylinders.Count);
        Assert.Equal(ProfileCount(layout, LayoutComponentKind.DigitalSensor), detectedSensors.Count);
        Assert.Equal(ProfileCount(layout, LayoutComponentKind.Workpiece), transportedWorkpieces.Count);
        Assert.Equal(Profiles[fileName].LoadLockCount, engine.CurrentSnapshot.LoadLocks.Count);
        Assert.Equal(Profiles[fileName].WaferHandlerCount, engine.CurrentSnapshot.WaferHandlers.Count);
        Assert.Equal(Profiles[fileName].InspectionSorterCount, engine.CurrentSnapshot.InspectionSortRouters.Count);
        Assert.Equal(Profiles[fileName].InspectionHandoffCount, engine.CurrentSnapshot.InspectionHandoffs.Count);
        Assert.Equal(Profiles[fileName].OhtHandoffCount, engine.CurrentSnapshot.OhtHandoffs.Count);
        Assert.Equal(Profiles[fileName].PrealignerCount, engine.CurrentSnapshot.Prealigners.Count);
        if (Profiles[fileName].LoadLockCount > 0)
        {
            Assert.Contains(LoadLockState.PumpingDown, observedLoadLockStates);
            Assert.Contains(LoadLockState.Vacuum, observedLoadLockStates);
            Assert.Contains(LoadLockState.Venting, observedLoadLockStates);
            Assert.Equal(LoadLockState.Atmosphere, Assert.Single(engine.CurrentSnapshot.LoadLocks).State);
        }
        if (Profiles[fileName].WaferHandlerCount > 0)
        {
            Assert.Contains(WaferHandlerOwnershipState.Source, observedHandlerStates);
            Assert.Contains(WaferHandlerOwnershipState.Handler, observedHandlerStates);
            Assert.Contains(WaferHandlerOwnershipState.Destination, observedHandlerStates);
            Assert.Equal(WaferHandlerOwnershipState.Destination, Assert.Single(engine.CurrentSnapshot.WaferHandlers).State);
        }
        if (Profiles[fileName].InspectionSorterCount > 0)
        {
            Assert.Contains(InspectionSortRouteState.AwaitingDecision, observedSorterStates);
            Assert.Contains(InspectionSortRouteState.NgReady, observedSorterStates);
            Assert.Contains(InspectionSortRouteState.NgRouted, observedSorterStates);
            Assert.Equal(InspectionSortRouteState.NgRouted, Assert.Single(engine.CurrentSnapshot.InspectionSortRouters).State);
        }
        if (Profiles[fileName].InspectionHandoffCount > 0)
        {
            Assert.Contains(InspectionHandoffState.AwaitingMaterial, observedInspectionHandoffStates);
            Assert.Contains(InspectionHandoffState.Ready, observedInspectionHandoffStates);
            Assert.Contains(InspectionHandoffState.Inspecting, observedInspectionHandoffStates);
            Assert.Contains(InspectionHandoffState.ResultAvailable, observedInspectionHandoffStates);
            Assert.Contains(InspectionHandoffState.Complete, observedInspectionHandoffStates);
            Assert.Equal(InspectionHandoffState.Complete, Assert.Single(engine.CurrentSnapshot.InspectionHandoffs).State);
        }
        if (Profiles[fileName].OhtHandoffCount > 0)
        {
            Assert.Contains(OhtHandoffOwnershipState.Vehicle, observedOhtHandoffStates);
            Assert.Contains(OhtHandoffOwnershipState.Ready, observedOhtHandoffStates);
            Assert.Contains(OhtHandoffOwnershipState.Transferring, observedOhtHandoffStates);
            Assert.Contains(OhtHandoffOwnershipState.LoadPort, observedOhtHandoffStates);
            Assert.Equal(OhtHandoffOwnershipState.LoadPort, Assert.Single(engine.CurrentSnapshot.OhtHandoffs).State);
        }
        if (Profiles[fileName].PrealignerCount > 0)
        {
            Assert.Contains(PrealignerState.AwaitingClamp, observedPrealignerStates);
            Assert.Contains(PrealignerState.Ready, observedPrealignerStates);
            Assert.Contains(PrealignerState.Aligning, observedPrealignerStates);
            Assert.Contains(PrealignerState.Aligned, observedPrealignerStates);
            Assert.Contains(PrealignerState.Released, observedPrealignerStates);
            Assert.Equal(PrealignerState.Released, Assert.Single(engine.CurrentSnapshot.Prealigners).State);
        }
        await engine.StopAsync();
    }

    private static int ProfileCount(MachineLayoutDefinition layout, LayoutComponentKind kind) =>
        layout.Components.Count(component => component.Kind == kind);

    private static string Fingerprint(MachineProjectDocument project)
    {
        SequenceDefinition sequence = Assert.Single(project.Sequences);
        return string.Join('|',
            string.Join(',', project.Axes.Select(axis => $"{axis.Id}:{axis.Kind}")),
            string.Join(',', project.Devices.Select(device => $"{device.Id}:{device.Kind}")),
            string.Join(',', sequence.Steps.Select(step => $"{step.Action}:{step.TargetId}")));
    }

    private static LayoutComponentSnapshot Component(SimulationSnapshot snapshot, string id) =>
        Assert.Single(snapshot.LayoutComponents, component => component.Id == id);

    private static string ErrorSummary(MachineProjectRuntimeCompilationResult result) =>
        string.Join(
            Environment.NewLine,
            result.Errors.Select(error => $"{error.Code} [{error.TargetId}]: {error.Message}"));

    private sealed record RecipeProfile(
        int AxisCount,
        int SensorCount,
        int CylinderCount,
        int ConveyorCount,
        int WorkpieceCount,
        int LoadLockCount,
        int WaferHandlerCount,
        int InspectionSorterCount,
        int InspectionHandoffCount,
        int OhtHandoffCount,
        int PrealignerCount,
        int ComponentCount,
        int StepCount);
}
