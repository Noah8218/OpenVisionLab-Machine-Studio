using OpenVisionLab.Machine.Core.Axes;
using OpenVisionLab.Machine.Core.Channels;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Core.Devices;
using OpenVisionLab.Machine.Simulation.Compilation;
using OpenVisionLab.Machine.Simulation.Commands;
using OpenVisionLab.Machine.Simulation.Engine;
using OpenVisionLab.Machine.Simulation.Axis;
using OpenVisionLab.Machine.Simulation.Layout;
using Xunit;

namespace OpenVisionLab.Machine.Simulation.Tests;

public sealed class MachineProjectRuntimeCompilerTests
{
    private static readonly TimeSpan FixedStep = TimeSpan.FromMilliseconds(5);

    public static TheoryData<string> BundledSamplePaths => new()
    {
        "AutomaticTransferCell.ovmachine",
        "sample-pick-and-place.ovmachine",
        "VisionInspectionCell.ovmachine",
        "WaferEFEMVisionCell.ovmachine",
        Path.Combine("SemiconductorRecipes", "01-FoupLoadPort.ovmachine"),
        Path.Combine("SemiconductorRecipes", "02-CassetteMapper.ovmachine"),
        Path.Combine("SemiconductorRecipes", "03-WaferPrealigner.ovmachine"),
        Path.Combine("SemiconductorRecipes", "04-WaferOcrInspection.ovmachine"),
        Path.Combine("SemiconductorRecipes", "05-LoadLockEntry.ovmachine"),
        Path.Combine("SemiconductorRecipes", "06-SpinCoatTrack.ovmachine"),
        Path.Combine("SemiconductorRecipes", "07-DevelopTrack.ovmachine"),
        Path.Combine("SemiconductorRecipes", "08-DryEtchTransfer.ovmachine"),
        Path.Combine("SemiconductorRecipes", "09-CmpTransfer.ovmachine"),
        Path.Combine("SemiconductorRecipes", "10-MetrologySorter.ovmachine")
    };

    [Theory]
    [MemberData(nameof(BundledSamplePaths))]
    public void Compile_AllBundledSamples_ProducesRuntimeWithAuthoredTimeScale(string relativePath)
    {
        string path = Path.Combine(AppContext.BaseDirectory, relativePath);
        MachineProjectDocument project = new ProjectDocumentStore().Load(File.ReadAllText(path));

        MachineProjectRuntimeCompilationResult result = Compile(project);

        Assert.True(result.IsSuccess, $"{relativePath}:{Environment.NewLine}{ErrorSummary(result)}");
        Assert.Equal(project.Simulation.DefaultTimeScale, result.Configuration!.TimeScale);
        Assert.All(result.Configuration.Sequences, sequence => Assert.Equal(TimeSpan.Zero, sequence.WatchdogTimeout));
    }

    [Fact]
    public void Compile_WatchdogPolicyRoundTripsIntoRuntime()
    {
        MachineProjectDocument project = LoadSample();
        project.Sequences.Single().WatchdogTimeoutMs = 1234;
        var store = new ProjectDocumentStore();

        MachineProjectDocument reopened = store.Load(store.Serialize(project));
        MachineProjectRuntimeCompilationResult result = Compile(reopened);

        Assert.Equal(1234, reopened.Sequences.Single().WatchdogTimeoutMs);
        Assert.True(result.IsSuccess, ErrorSummary(result));
        Assert.Equal(
            TimeSpan.FromMilliseconds(1234),
            Assert.Single(result.Configuration!.Sequences).WatchdogTimeout);
    }

    [Fact]
    public void Compile_AutomaticTransferCell_ProducesCompleteRuntimeConfiguration()
    {
        MachineProjectRuntimeCompilationResult result = Compile(LoadSample());

        Assert.True(result.IsSuccess, ErrorSummary(result));
        Assert.Empty(result.Errors);
        Assert.NotNull(result.Configuration);
        var axis = Assert.Single(result.Configuration.Axes);
        Assert.Equal("x", axis.Id);
        Assert.Equal(180, axis.MaximumVelocity);
        Assert.Equal(600, axis.Acceleration);
        Assert.Equal(600, axis.Deceleration);
        Assert.Equal(0.05, axis.FollowingErrorLimit);
        Assert.Equal("auto-transfer-cycle", Assert.Single(result.Configuration.Sequences).Id);

        MachineLayoutRuntimeConfiguration layout = Assert.IsType<MachineLayoutRuntimeConfiguration>(
            result.Configuration.Layout);
        Assert.Equal("main-cell", layout.Id);
        Assert.Equal(7, layout.Components.Count);
        var sensor = Assert.IsType<DigitalSensorRuntimeConfiguration>(
            Assert.Single(layout.Components, component => component.Id == "sensor-1"));
        Assert.Equal(2, sensor.OnDelayTicks);
        Assert.Equal(2, sensor.OffDelayTicks);
        var cylinder = Assert.IsType<PneumaticCylinderRuntimeConfiguration>(
            Assert.Single(layout.Components, component => component.Id == "cylinder-1"));
        Assert.Equal(20, cylinder.ExtendDurationTicks);
        Assert.Equal(20, cylinder.RetractDurationTicks);
        Assert.Equal(2, cylinder.ExtendedSensorDelayTicks);
        Assert.Equal(2, cylinder.RetractedSensorDelayTicks);
        Assert.Equal(60, cylinder.Stroke);
        var conveyor = Assert.IsType<ConveyorRuntimeConfiguration>(
            Assert.Single(layout.Components, component => component.Id == "conveyor-1"));
        Assert.Equal(400, conveyor.SpeedUnitsPerSecond);
        Assert.Equal(0.005, conveyor.FixedStepSeconds, precision: 10);
        Assert.Equal(2, conveyor.TravelPerTick, precision: 10);
        var workpiece = Assert.IsType<WorkpieceRuntimeConfiguration>(
            Assert.Single(layout.Components, component => component.Id == "workpiece-1"));
        Assert.Equal(conveyor.Id, workpiece.ConveyorComponentId);
        Assert.Equal("Inspection Carrier", workpiece.Type);
        Assert.Equal(WorkpieceInspectionState.Pending, workpiece.InspectionState);

        Assert.NotNull(result.Configuration.AutomaticRun);
        Assert.Equal("auto-transfer-cycle", result.Configuration.AutomaticRun.SequenceId);
        Assert.True(result.Configuration.AutomaticRun.Repeat);
        Assert.Equal(250, result.Configuration.AutomaticRun.RepeatDelayMilliseconds);
    }

    [Fact]
    public void Compile_LegacyProjectWithoutLayoutOrAutomaticRun_Succeeds()
    {
        var project = new MachineProjectDocument
        {
            Simulation = new SimulationDefinition { FixedStepMilliseconds = 5 }
        };

        MachineProjectRuntimeCompilationResult result = Compile(project);

        Assert.True(result.IsSuccess, ErrorSummary(result));
        Assert.NotNull(result.Configuration);
        Assert.Null(result.Configuration.Layout);
        Assert.Null(result.Configuration.AutomaticRun);
    }

    [Fact]
    public async Task Compile_AnalogChannels_ProjectsIntoRuntimeSnapshot()
    {
        var project = new MachineProjectDocument
        {
            Simulation = new SimulationDefinition { FixedStepMilliseconds = 5 },
            Channels =
            [
                new ChannelDefinition
                {
                    Id = "ai.height",
                    Name = "Height",
                    Kind = ChannelKind.AnalogInput,
                    InitialValue = 12.5
                },
                new ChannelDefinition
                {
                    Id = "ao.speed",
                    Name = "Speed",
                    Kind = ChannelKind.AnalogOutput,
                    InitialValue = -2.25
                }
            ]
        };

        MachineProjectRuntimeCompilationResult compilation = Compile(project);
        Assert.True(compilation.IsSuccess, ErrorSummary(compilation));

        using var engine = new FixedStepSimulationEngine(
            new SimulationSettings { FixedStep = FixedStep });
        await engine.StartAsync();
        var configured = await engine.EnqueueCommandAsync(
            new ConfigureRuntimeCommand(compilation.Configuration!));

        Assert.True(configured.IsAccepted, configured.Detail);
        Assert.Equal(
            new[] { "ai.height", "ao.speed" },
            engine.CurrentSnapshot.AnalogSignals.Select(signal => signal.Id));
        Assert.Equal(
            12.5,
            engine.CurrentSnapshot.AnalogSignals.Single(signal => signal.Id == "ai.height").Value);
        Assert.Equal(
            -2.25,
            engine.CurrentSnapshot.AnalogSignals.Single(signal => signal.Id == "ao.speed").Value);
        Assert.Empty(engine.CurrentSnapshot.Signals);
        await engine.StopAsync();
    }

    [Fact]
    public async Task Compile_DefaultTimeScale_ConfiguresRuntimeWithoutChangingFixedStep()
    {
        MachineProjectDocument project = LoadSample();
        project.Simulation.DefaultTimeScale = 2.5;

        MachineProjectRuntimeCompilationResult result = Compile(project);

        Assert.True(result.IsSuccess, ErrorSummary(result));
        Assert.Equal(2.5, result.Configuration!.TimeScale);
        using var engine = new FixedStepSimulationEngine(
            new SimulationSettings { FixedStep = FixedStep, TimeScale = 0.5 });
        await engine.StartAsync();
        SimulationCommandResult configured = await engine.EnqueueCommandAsync(
            new ConfigureRuntimeCommand(result.Configuration));
        SimulationCommandResult stepped = await engine.EnqueueCommandAsync(new StepCommand());

        Assert.True(configured.IsAccepted, configured.Detail);
        Assert.True(stepped.IsAccepted, stepped.Detail);
        Assert.Equal(2.5, engine.CurrentSnapshot.TimeScale);
        Assert.Equal(FixedStep, engine.CurrentSnapshot.SimulationTime);
        Assert.Equal(1, engine.CurrentSnapshot.TickIndex);
        await engine.StopAsync();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(0.09)]
    [InlineData(10.01)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Compile_InvalidDefaultTimeScale_ReturnsStableTypedError(double timeScale)
    {
        MachineProjectDocument project = LoadSample();
        project.Simulation.DefaultTimeScale = timeScale;

        AssertStableError(
            project,
            MachineProjectRuntimeCompilationErrorCode.TimeScaleInvalid,
            "simulation.defaultTimeScale",
            "between 0.1 and 10.0");
    }

    [Fact]
    public void Compile_MissingOutputInterlock_ReturnsStableTypedError()
    {
        MachineProjectDocument project = LoadSample();
        project.Channels.First(channel =>
                channel.Kind == OpenVisionLab.Machine.Core.Channels.ChannelKind.DigitalOutput)
            .InterlockIds.Add("di.missing-interlock");

        AssertStableError(
            project,
            MachineProjectRuntimeCompilationErrorCode.SignalConfigurationInvalid,
            "di.missing-interlock",
            "InterlockChannelNotFound");
    }

    [Fact]
    public void Compile_LegacyAxisTuning_UsesBehaviorPreservingDefaults()
    {
        MachineProjectDocument project = LoadSample();
        var definition = Assert.Single(project.Axes);
        definition.MaxAcceleration = 750;
        definition.MaxDeceleration = null;
        definition.FollowingErrorLimit = null;

        MachineProjectRuntimeCompilationResult result = Compile(project);

        Assert.True(result.IsSuccess, ErrorSummary(result));
        var axis = Assert.Single(result.Configuration!.Axes);
        Assert.Equal(750, axis.Acceleration);
        Assert.Equal(750, axis.Deceleration);
        Assert.Equal(VirtualAxisDefinition.DefaultFollowingErrorLimit, axis.FollowingErrorLimit);
    }

    [Fact]
    public async Task CompiledAuthoredAxisTuning_ConfiguresIdleRuntimeSnapshotWithoutMotion()
    {
        MachineProjectDocument project = LoadSample();
        var definition = Assert.Single(project.Axes);
        definition.MaxVelocity = 175;
        definition.MaxAcceleration = 650;
        definition.MaxDeceleration = 575;
        definition.FollowingErrorLimit = 0.08;
        MachineProjectRuntimeCompilationResult compilation = Compile(project);
        Assert.True(compilation.IsSuccess, ErrorSummary(compilation));

        using var engine = new FixedStepSimulationEngine(
            new SimulationSettings { FixedStep = FixedStep });
        await engine.StartAsync();
        var result = await engine.EnqueueCommandAsync(
            new ConfigureRuntimeCommand(compilation.Configuration!));

        Assert.True(result.IsAccepted, result.Detail);
        var snapshot = Assert.Single(engine.CurrentSnapshot.Axes);
        Assert.Equal(AxisState.Idle, snapshot.State);
        Assert.Equal(0, engine.CurrentSnapshot.TickIndex);
        Assert.Equal(0, snapshot.Position);
        Assert.Equal(175, snapshot.MaximumVelocity);
        Assert.Equal(650, snapshot.Acceleration);
        Assert.Equal(575, snapshot.Deceleration);
        Assert.Equal(0.08, snapshot.FollowingErrorLimit);
        await engine.StopAsync();
    }

    [Theory]
    [InlineData(0, 0.05)]
    [InlineData(600, 0)]
    public void Compile_InvalidAuthoredAxisTuning_ReturnsStableTypedError(
        double maxDeceleration,
        double followingErrorLimit)
    {
        MachineProjectDocument project = LoadSample();
        var definition = Assert.Single(project.Axes);
        definition.MaxDeceleration = maxDeceleration;
        definition.FollowingErrorLimit = followingErrorLimit;

        AssertStableError(
            project,
            MachineProjectRuntimeCompilationErrorCode.AxisConfigurationInvalid,
            "x",
            "invalid limits or motion parameters");
    }

    [Fact]
    public void Compile_InvalidLayout_ReturnsStableTypedError()
    {
        MachineProjectDocument project = LoadSample();
        project.Layouts[0].Components.Single(component => component.Id == "stage-1")
            .BehaviorBindingId = "missing-axis";

        AssertStableError(
            project,
            MachineProjectRuntimeCompilationErrorCode.LayoutValidationFailed,
            "stage-1",
            "AxisBindingNotFound");
    }

    [Fact]
    public void Compile_MissingActiveLayout_ReturnsStableTypedError()
    {
        MachineProjectDocument project = LoadSample();
        project.Simulation.ActiveLayoutId = "missing-layout";

        AssertStableError(
            project,
            MachineProjectRuntimeCompilationErrorCode.ActiveLayoutNotFound,
            "simulation.activeLayoutId",
            "missing-layout");
    }

    [Fact]
    public void Compile_InvalidSequenceTarget_ReturnsStableTypedError()
    {
        MachineProjectDocument project = LoadSample();
        project.Sequences[0].Steps.Single(step => step.Id == "move-station").TargetId = "missing-axis";

        AssertStableError(
            project,
            MachineProjectRuntimeCompilationErrorCode.SequenceCompilationFailed,
            "move-station",
            "missing-axis");
    }

    [Fact]
    public void Compile_InvalidAutomaticSequence_ReturnsStableTypedError()
    {
        MachineProjectDocument project = LoadSample();
        project.Simulation.AutomaticRun!.SequenceId = "missing-sequence";

        AssertStableError(
            project,
            MachineProjectRuntimeCompilationErrorCode.AutomaticSequenceNotFound,
            "missing-sequence",
            "missing-sequence");
    }

    [Fact]
    public void Compile_InvalidPickPlaceGripper_ReturnsStableTypedError()
    {
        MachineProjectDocument project = LoadPickPlaceSample();
        project.Simulation.PickPlaceWorkpiece!.GripperSignalId = "missing-gripper";

        AssertStableError(
            project,
            MachineProjectRuntimeCompilationErrorCode.PickPlaceWorkpieceInvalid,
            "part-1",
            "digital-output gripper signal");
    }

    [Fact]
    public void Compile_UnalignedSensorDelay_ReturnsStableTypedError()
    {
        MachineProjectDocument project = LoadSample();
        project.Devices.Single(device => device.Id == "device.sensor-1")
            .Sensor!.OnDelayMilliseconds = 7;

        AssertStableError(
            project,
            MachineProjectRuntimeCompilationErrorCode.SensorDelayInvalid,
            "sensor-1",
            "exact multiples of 5 ms");
    }

    [Fact]
    public void Compile_UnalignedCylinderDuration_ReturnsStableTypedError()
    {
        MachineProjectDocument project = LoadSample();
        project.Devices.Single(device => device.Id == "device.cylinder-1")
            .Cylinder!.ExtendDurationMilliseconds = 7;

        AssertStableError(
            project,
            MachineProjectRuntimeCompilationErrorCode.CylinderTimingInvalid,
            "cylinder-1",
            "exact multiples of 5 ms");
    }

    [Fact]
    public void Compile_LoadLockRecipe_ProducesTypedChamberConfiguration()
    {
        MachineProjectRuntimeCompilationResult result = Compile(LoadLoadLockRecipe());

        Assert.True(result.IsSuccess, ErrorSummary(result));
        var loadLock = Assert.Single(result.Configuration!.Layout!.LoadLocks);
        Assert.Equal("load-lock-1", loadLock.Id);
        Assert.Equal("outer-door", loadLock.OuterDoorComponentId);
        Assert.Equal("process-cylinder", loadLock.InnerDoorComponentId);
        Assert.Equal(50, loadLock.PumpDownDurationTicks);
        Assert.Equal(50, loadLock.VentDurationTicks);
    }

    [Fact]
    public void Compile_LoadLockWithMissingDoor_ReturnsStableTypedError()
    {
        MachineProjectDocument project = LoadLoadLockRecipe();
        project.Devices.Single(device => device.Kind == DeviceKind.LoadLock)
            .LoadLock!.InnerDoorComponentId = "missing-door";

        AssertStableError(
            project,
            MachineProjectRuntimeCompilationErrorCode.LoadLockConfigurationInvalid,
            "load-lock-1",
            "two distinct pneumatic cylinders");
    }

    [Fact]
    public void Compile_LoadLockWithWrongChannelKind_ReturnsStableTypedError()
    {
        MachineProjectDocument project = LoadLoadLockRecipe();
        project.Channels.Single(channel => channel.Id == "di.load-lock.vacuum-ready").Kind =
            OpenVisionLab.Machine.Core.Channels.ChannelKind.DigitalOutput;

        AssertStableError(
            project,
            MachineProjectRuntimeCompilationErrorCode.LoadLockConfigurationInvalid,
            "load-lock-1",
            "feedback channels must be DigitalInput");
    }

    [Fact]
    public void Compile_LoadLockWithUnalignedTiming_ReturnsStableTypedError()
    {
        MachineProjectDocument project = LoadLoadLockRecipe();
        project.Devices.Single(device => device.Kind == DeviceKind.LoadLock)
            .LoadLock!.PumpDownDurationMilliseconds = 7;

        AssertStableError(
            project,
            MachineProjectRuntimeCompilationErrorCode.LoadLockConfigurationInvalid,
            "load-lock-1",
            "exact multiples of 5 ms");
    }

    [Fact]
    public void Compile_WaferHandlerRecipe_ProducesTypedTransferConfiguration()
    {
        MachineProjectRuntimeCompilationResult result = Compile(LoadWaferHandlerRecipe());

        Assert.True(result.IsSuccess, ErrorSummary(result));
        var handler = Assert.Single(result.Configuration!.Layout!.WaferHandlers);
        Assert.Equal("device.wafer-handler", handler.Id);
        Assert.Equal("axis.robot-reach", handler.HorizontalAxisId);
        Assert.Equal("axis.process", handler.VerticalAxisId);
        Assert.Equal("wafer", handler.WorkpieceComponentId);
        Assert.Equal(140, handler.PlaceHorizontalPosition);
        Assert.Equal(260, handler.PlaceVerticalPosition);
    }

    [Fact]
    public void Compile_WaferHandlerWithWrongCommandKind_ReturnsStableTypedError()
    {
        MachineProjectDocument project = LoadWaferHandlerRecipe();
        project.Channels.Single(channel => channel.Id == "do.handler.pick").Kind =
            OpenVisionLab.Machine.Core.Channels.ChannelKind.DigitalInput;

        AssertStableError(
            project,
            MachineProjectRuntimeCompilationErrorCode.WaferHandlerConfigurationInvalid,
            "device.wafer-handler",
            "pick/place commands must be DigitalOutput");
    }

    [Fact]
    public void Compile_WaferHandlerWithMissingAxis_ReturnsStableTypedError()
    {
        MachineProjectDocument project = LoadWaferHandlerRecipe();
        project.Devices.Single(device => device.Kind == DeviceKind.Handler)
            .WaferHandler!.HorizontalAxisId = "missing-axis";

        AssertStableError(
            project,
            MachineProjectRuntimeCompilationErrorCode.WaferHandlerConfigurationInvalid,
            "device.wafer-handler",
            "two distinct configured linear axes");
    }

    [Fact]
    public void Compile_WaferHandlerWithMissingWorkpiece_ReturnsStableTypedError()
    {
        MachineProjectDocument project = LoadWaferHandlerRecipe();
        project.Devices.Single(device => device.Kind == DeviceKind.Handler)
            .WaferHandler!.WorkpieceComponentId = "missing-wafer";

        AssertStableError(
            project,
            MachineProjectRuntimeCompilationErrorCode.WaferHandlerConfigurationInvalid,
            "device.wafer-handler",
            "workpiece in the active layout");
    }

    [Fact]
    public void Compile_WaferHandlerWithOutOfRangePosition_ReturnsStableTypedError()
    {
        MachineProjectDocument project = LoadWaferHandlerRecipe();
        project.Devices.Single(device => device.Kind == DeviceKind.Handler)
            .WaferHandler!.PlaceHorizontalPosition = 999;

        AssertStableError(
            project,
            MachineProjectRuntimeCompilationErrorCode.WaferHandlerConfigurationInvalid,
            "device.wafer-handler",
            "within their axis soft limits");
    }

    [Fact]
    public void Compile_WaferHandlerWithDuplicateCommand_ReturnsStableTypedError()
    {
        MachineProjectDocument project = LoadWaferHandlerRecipe();
        WaferHandlerDefinition handler = project.Devices.Single(device =>
            device.Kind == DeviceKind.Handler).WaferHandler!;
        handler.PlaceCommandChannelId = handler.PickCommandChannelId;

        AssertStableError(
            project,
            MachineProjectRuntimeCompilationErrorCode.WaferHandlerConfigurationInvalid,
            "device.wafer-handler",
            "channels must be distinct");
    }

    [Fact]
    public void Compile_InspectionSorterRecipe_ProducesTypedRouteConfiguration()
    {
        MachineProjectRuntimeCompilationResult result = Compile(LoadInspectionSorterRecipe());

        Assert.True(result.IsSuccess, ErrorSummary(result));
        var sorter = Assert.Single(result.Configuration!.Layout!.InspectionSortRouters);
        Assert.Equal("device.inspection-sorter", sorter.Id);
        Assert.Equal("camera.metrology", sorter.CameraId);
        Assert.Equal("transport", sorter.PassConveyorComponentId);
        Assert.Equal("sort-transport", sorter.NgConveyorComponentId);
        Assert.Equal("do.transport.run", sorter.PassRunCommandChannelId);
        Assert.Equal("do.sort-transport.run", sorter.NgRunCommandChannelId);
    }

    [Fact]
    public void Compile_InspectionSorterWithMissingCamera_ReturnsStableTypedError()
    {
        MachineProjectDocument project = LoadInspectionSorterRecipe();
        project.Devices.Single(device => device.Kind == DeviceKind.Sorter)
            .InspectionSortRouter!.CameraId = "missing-camera";

        AssertStableError(
            project,
            MachineProjectRuntimeCompilationErrorCode.InspectionSortRouterConfigurationInvalid,
            "device.inspection-sorter",
            "configured virtual camera");
    }

    [Fact]
    public void Compile_InspectionSorterWithSameRoute_ReturnsStableTypedError()
    {
        MachineProjectDocument project = LoadInspectionSorterRecipe();
        InspectionSortRouterDefinition sorter = project.Devices.Single(device =>
            device.Kind == DeviceKind.Sorter).InspectionSortRouter!;
        sorter.NgConveyorComponentId = sorter.PassConveyorComponentId;

        AssertStableError(
            project,
            MachineProjectRuntimeCompilationErrorCode.InspectionSortRouterConfigurationInvalid,
            "device.inspection-sorter",
            "two distinct conveyors");
    }

    [Fact]
    public void Compile_InspectionSorterWithWrongFeedbackKind_ReturnsStableTypedError()
    {
        MachineProjectDocument project = LoadInspectionSorterRecipe();
        project.Channels.Single(channel => channel.Id == "di.sort.pass-routed").Kind =
            OpenVisionLab.Machine.Core.Channels.ChannelKind.DigitalOutput;

        AssertStableError(
            project,
            MachineProjectRuntimeCompilationErrorCode.InspectionSortRouterConfigurationInvalid,
            "device.inspection-sorter",
            "distinct DigitalInput channels");
    }

    [Fact]
    public void Compile_InspectionHandoffRecipe_ProducesTypedHandoffConfiguration()
    {
        MachineProjectRuntimeCompilationResult result = Compile(LoadInspectionHandoffRecipe());

        Assert.True(result.IsSuccess, ErrorSummary(result));
        var handoff = Assert.Single(result.Configuration!.Layout!.InspectionHandoffs);
        Assert.Equal("device.inspection-handoff", handoff.Id);
        Assert.Equal("camera.ocr", handoff.CameraId);
        Assert.Equal("di.sensor-process", handoff.InspectionPositionSensorChannelId);
        Assert.Equal("do.inspection-result-accepted", handoff.ResultAcceptedCommandChannelId);
    }

    [Fact]
    public void Compile_InspectionHandoffWithMissingCamera_ReturnsStableTypedError()
    {
        MachineProjectDocument project = LoadInspectionHandoffRecipe();
        project.Devices.Single(device => device.Kind == DeviceKind.Inspection)
            .InspectionHandoff!.CameraId = "missing-camera";

        AssertStableError(
            project,
            MachineProjectRuntimeCompilationErrorCode.InspectionHandoffConfigurationInvalid,
            "device.inspection-handoff",
            "configured virtual camera");
    }

    [Fact]
    public void Compile_InspectionHandoffWithWrongCommandKind_ReturnsStableTypedError()
    {
        MachineProjectDocument project = LoadInspectionHandoffRecipe();
        project.Channels.Single(channel => channel.Id == "do.inspection-result-accepted").Kind =
            OpenVisionLab.Machine.Core.Channels.ChannelKind.DigitalInput;

        AssertStableError(
            project,
            MachineProjectRuntimeCompilationErrorCode.InspectionHandoffConfigurationInvalid,
            "device.inspection-handoff",
            "DigitalOutput result-accepted command");
    }

    [Fact]
    public void Compile_OhtHandoffRecipe_ProducesTypedOwnershipConfiguration()
    {
        MachineProjectRuntimeCompilationResult result = Compile(LoadOhtHandoffRecipe());

        Assert.True(result.IsSuccess, ErrorSummary(result));
        var handoff = Assert.Single(result.Configuration!.Layout!.OhtHandoffs);
        Assert.Equal("device.oht-handoff", handoff.Id);
        Assert.Equal("transport", handoff.TransportConveyorComponentId);
        Assert.Equal("do.transport.run", handoff.ForwardCommandChannelId);
        Assert.Equal("di.cylinder.extended", handoff.LoadPortReadySensorChannelId);
        Assert.Equal("di.sensor-process", handoff.CarrierReceivedSensorChannelId);
    }

    [Fact]
    public void Compile_OhtHandoffWithMissingConveyor_ReturnsStableTypedError()
    {
        MachineProjectDocument project = LoadOhtHandoffRecipe();
        project.Devices.Single(device => device.Kind == DeviceKind.Oht)
            .OhtHandoff!.TransportConveyorComponentId = "missing-conveyor";

        AssertStableError(
            project,
            MachineProjectRuntimeCompilationErrorCode.OhtHandoffConfigurationInvalid,
            "device.oht-handoff",
            "conveyor in the active layout");
    }

    [Fact]
    public void Compile_OhtHandoffWithWrongFeedbackKind_ReturnsStableTypedError()
    {
        MachineProjectDocument project = LoadOhtHandoffRecipe();
        project.Channels.Single(channel => channel.Id == "di.oht.handoff-ready").Kind =
            OpenVisionLab.Machine.Core.Channels.ChannelKind.DigitalOutput;

        AssertStableError(
            project,
            MachineProjectRuntimeCompilationErrorCode.OhtHandoffConfigurationInvalid,
            "device.oht-handoff",
            "six distinct DigitalInput channels");
    }

    private static void AssertStableError(
        MachineProjectDocument project,
        MachineProjectRuntimeCompilationErrorCode code,
        string targetId,
        string messageFragment)
    {
        MachineProjectRuntimeCompilationResult first = Compile(project);
        MachineProjectRuntimeCompilationResult second = Compile(project);

        Assert.False(first.IsSuccess);
        Assert.Null(first.Configuration);
        MachineProjectRuntimeCompilationError error = Assert.Single(
            first.Errors,
            candidate => candidate.Code == code && candidate.TargetId == targetId);
        Assert.Contains(messageFragment, error.Message, StringComparison.Ordinal);
        Assert.Equal(first.Errors.ToArray(), second.Errors.ToArray());
    }

    private static MachineProjectRuntimeCompilationResult Compile(MachineProjectDocument project) =>
        new MachineProjectRuntimeCompiler(FixedStep).Compile(project);

    private static MachineProjectDocument LoadSample()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "AutomaticTransferCell.ovmachine");
        return new ProjectDocumentStore().Load(File.ReadAllText(path));
    }

    private static MachineProjectDocument LoadPickPlaceSample()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "sample-pick-and-place.ovmachine");
        return new ProjectDocumentStore().Load(File.ReadAllText(path));
    }

    private static MachineProjectDocument LoadLoadLockRecipe()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "SemiconductorRecipes", "05-LoadLockEntry.ovmachine");
        return new ProjectDocumentStore().Load(File.ReadAllText(path));
    }

    private static MachineProjectDocument LoadWaferHandlerRecipe()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "SemiconductorRecipes", "08-DryEtchTransfer.ovmachine");
        return new ProjectDocumentStore().Load(File.ReadAllText(path));
    }

    private static MachineProjectDocument LoadInspectionSorterRecipe()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "SemiconductorRecipes", "10-MetrologySorter.ovmachine");
        return new ProjectDocumentStore().Load(File.ReadAllText(path));
    }

    private static MachineProjectDocument LoadInspectionHandoffRecipe()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "SemiconductorRecipes", "04-WaferOcrInspection.ovmachine");
        return new ProjectDocumentStore().Load(File.ReadAllText(path));
    }

    private static MachineProjectDocument LoadOhtHandoffRecipe()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "SemiconductorRecipes", "01-FoupLoadPort.ovmachine");
        return new ProjectDocumentStore().Load(File.ReadAllText(path));
    }

    private static string ErrorSummary(MachineProjectRuntimeCompilationResult result) =>
        string.Join(
            Environment.NewLine,
            result.Errors.Select(error => $"{error.Code} [{error.TargetId}]: {error.Message}"));
}
