using OpenVisionLab.Machine.Core.Axes;
using OpenVisionLab.Machine.Core.Channels;
using OpenVisionLab.Machine.Core.Devices;
using OpenVisionLab.Machine.Core.Layouts;
using OpenVisionLab.Machine.Core.Models;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Core.Sequences;
using Xunit;

namespace OpenVisionLab.Machine.Core.Tests;

public class ProjectDocumentStoreTests
{
    private readonly ProjectDocumentStore _store = new();

    [Fact]
    public void Serialize_DoesNotChangeProjectMetadata()
    {
        var modifiedAt = new DateTimeOffset(2026, 8, 10, 0, 0, 0, TimeSpan.Zero);
        var document = new MachineProjectDocument
        {
            Id = "stable-project",
            Name = "Stable project",
            ModifiedAt = modifiedAt
        };

        var first = _store.Serialize(document);
        var second = _store.Serialize(document);

        Assert.Equal(modifiedAt, document.ModifiedAt);
        Assert.Equal(first, second);
    }

    [Fact]
    public void SerializeForEvidence_IgnoresModifiedTimestamp()
    {
        var document = new MachineProjectDocument
        {
            Id = "evidence-project",
            Name = "Evidence project",
            ModifiedAt = new DateTimeOffset(2026, 8, 10, 0, 0, 0, TimeSpan.Zero)
        };
        var first = _store.SerializeForEvidence(document);

        document.ModifiedAt = document.ModifiedAt.AddDays(1);
        var second = _store.SerializeForEvidence(document);

        Assert.Equal(first, second);
        Assert.DoesNotContain("modifiedAt", first, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SaveAsync_ReplacesExistingFileAndRetainsPreviousVersion()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"ovl-project-save-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "machine.ovmachine");
        try
        {
            await _store.SaveAsync(new MachineProjectDocument { Name = "Before" }, path);
            var previousJson = await File.ReadAllTextAsync(path);

            await _store.SaveAsync(new MachineProjectDocument { Name = "After" }, path);

            Assert.Equal("After", (await _store.LoadAsync(path)).Name);
            Assert.Equal(previousJson, await File.ReadAllTextAsync(path + ".bak"));
            Assert.Empty(Directory.EnumerateFiles(directory, ".*.tmp"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void SaveAndLoad_RoundTrip_PreservesValues()
    {
        var doc = new MachineProjectDocument
        {
            Name = "Coupon Pick-and-Place",
            Simulation = new SimulationDefinition
            {
                FixedStepMilliseconds = 5,
                DefaultTimeScale = 2.0,
                Seed = 20260806,
                TestScenarioProfileId = "fault-injection",
                TestScenarioSeed = 424242,
                TestScenarioDurationCycles = 480,
                TestScenarioTargetId = "conveyor-1",
                TestScenarioBatchRepetitions = 7,
                TestScenarioFault = new TestScenarioFaultDefinition
                {
                    Enabled = true,
                    Kind = TestScenarioFaultKind.StuckDigitalInput,
                    TargetId = "di.part-present",
                    ForcedValue = true,
                    InjectTick = 120,
                    HoldTicks = 4,
                    RestartSequenceId = "main"
                },
                TestScenarioAssertions =
                {
                    new TestScenarioAssertionDefinition
                    {
                        AssertionId = "cycle-completed",
                        Kind = TestScenarioAssertionKind.AutomaticCycleCompleted,
                        MinimumCount = 2
                    },
                    new TestScenarioAssertionDefinition
                    {
                        AssertionId = "faults-cleared",
                        Kind = TestScenarioAssertionKind.NoActiveFaults
                    },
                    new TestScenarioAssertionDefinition
                    {
                        AssertionId = "conveyor-stopped",
                        Kind = TestScenarioAssertionKind.FinalEquipmentState,
                        TargetId = "conveyor-1",
                        ExpectedState = "Stopped"
                    }
                },
                AutomaticRun = new AutomaticRunDefinition
                {
                    SequenceId = "main",
                    StartInputId = "di.start",
                    StartInputValue = true,
                    Repeat = true,
                    RepeatDelayMilliseconds = 250
                }
            },
            Axes =
            {
                new VirtualAxisDefinition
                {
                    Id = "x",
                    Name = "X Stage",
                    Kind = AxisKind.Linear,
                    MaxVelocity = 200.0,
                    MaxAcceleration = 800.0,
                    MaxDeceleration = 700.0,
                    FollowingErrorLimit = 0.08,
                    SoftLimitMin = 0,
                    SoftLimitMax = 500
                }
            },
            MultiAxisCommissioningRecipe = new MultiAxisCommissioningRecipeDefinition
            {
                Id = "pick-position",
                Name = "Pick position",
                ValidationRepetitions = 4,
                Targets =
                {
                    new MultiAxisCommissioningTargetDefinition
                    {
                        AxisId = "y",
                        TargetPosition = 120
                    },
                    new MultiAxisCommissioningTargetDefinition
                    {
                        AxisId = "x",
                        TargetPosition = 240
                    }
                }
            },
            Channels =
            {
                new ChannelDefinition
                {
                    Id = "di.start",
                    Name = "Start Button",
                    Kind = ChannelKind.DigitalInput
                }
            },
            Devices =
            {
                new DeviceDefinition
                {
                    Id = "cam1",
                    Name = "Top Camera",
                    Kind = DeviceKind.Camera,
                    MountPosition = new Coordinate3D(0, 0, 300),
                    ChannelIds = { "di.trigger" },
                    Camera = new VirtualCameraDefinition
                    {
                        ExposureDelayMilliseconds = 20,
                        TransferDelayMilliseconds = 30,
                        PlaceholderDecision = PlaceholderInspectionDecision.Fail,
                        SingleImageSource = new VirtualSingleImageSourceDefinition
                        {
                            SourceRelativePath = "assets/presence-check.pgm",
                            Width = 16,
                            Height = 12,
                            PixelFormat = "Mono8"
                        }
                    }
                },
                new DeviceDefinition
                {
                    Id = "sensor1",
                    Name = "Inspection Sensor",
                    Kind = DeviceKind.Sensor,
                    Sensor = new DigitalSensorDefinition
                    {
                        OutputChannelId = "di.start",
                        TargetComponentId = "stage1",
                        OnDelayMilliseconds = 10,
                        OffDelayMilliseconds = 15
                    }
                },
                new DeviceDefinition
                {
                    Id = "load-lock-1",
                    Name = "Load Lock",
                    Kind = DeviceKind.LoadLock,
                    LoadLock = new LoadLockDefinition
                    {
                        OuterDoorComponentId = "outer-door",
                        InnerDoorComponentId = "inner-door",
                        EvacuateCommandChannelId = "do.evacuate",
                        VentCommandChannelId = "do.vent",
                        VacuumReadySensorChannelId = "di.vacuum",
                        AtmosphereReadySensorChannelId = "di.atmosphere",
                        PumpDownDurationMilliseconds = 250,
                        VentDurationMilliseconds = 300
                    }
                }
            },
            Layouts =
            {
                new MachineLayoutDefinition
                {
                    Id = "main-cell",
                    Name = "Main Cell",
                    GridSize = 25,
                    SnapToGrid = true,
                    Components =
                    {
                        new LayoutComponentDefinition
                        {
                            Id = "frame1",
                            Name = "Machine Frame",
                            Kind = LayoutComponentKind.MachineFrame,
                            Transform = new Transform2D { X = 10, Y = 20, RotationDegrees = 0 },
                            Size = new Size2D { Width = 600, Height = 350 },
                            ZIndex = 0
                        },
                        new LayoutComponentDefinition
                        {
                            Id = "stage1",
                            Name = "Inspection Stage",
                            Kind = LayoutComponentKind.LinearStage,
                            Transform = new Transform2D { X = 80, Y = 180, RotationDegrees = 0 },
                            Size = new Size2D { Width = 120, Height = 80 },
                            ZIndex = 10,
                            BehaviorBindingId = "x"
                        },
                        new LayoutComponentDefinition
                        {
                            Id = "sensor-visual1",
                            Name = "Inspection Sensor",
                            Kind = LayoutComponentKind.DigitalSensor,
                            Transform = new Transform2D { X = 300, Y = 150, RotationDegrees = 90 },
                            Size = new Size2D { Width = 30, Height = 80 },
                            ZIndex = 20,
                            BehaviorBindingId = "sensor1"
                        }
                    }
                }
            },
            Sequences =
            {
                new SequenceDefinition
                {
                    Id = "main",
                    Name = "Main Cycle",
                    Steps =
                    {
                        new SequenceStepDefinition
                        {
                            Id = "home",
                            Name = "Home axes",
                            Action = SequenceStepAction.MoveAxis,
                            ExpectedTargetId = "x",
                            ExpectedState = "Idle"
                        }
                    }
                }
            }
        };

        var json = _store.Save(doc);
        var loaded = _store.Load(json);

        Assert.Contains(
            $"\"schema\": \"{MachineProjectDocument.CurrentSchema}\"",
            json,
            StringComparison.Ordinal);
        Assert.Contains("\"simulation\"", json, StringComparison.Ordinal);
        Assert.Contains("\"layouts\"", json, StringComparison.Ordinal);
        Assert.Contains("\"sensor\"", json, StringComparison.Ordinal);
        Assert.Equal(MachineProjectDocument.CurrentSchema, loaded.Schema);
        Assert.Equal(doc.Name, loaded.Name);
        Assert.Equal(5, loaded.Simulation.FixedStepMilliseconds);
        Assert.Equal(2.0, loaded.Simulation.DefaultTimeScale);
        Assert.Equal(20260806, loaded.Simulation.Seed);
        Assert.Equal("fault-injection", loaded.Simulation.TestScenarioProfileId);
        Assert.Equal(424242, loaded.Simulation.TestScenarioSeed);
        Assert.Equal(480, loaded.Simulation.TestScenarioDurationCycles);
        Assert.Equal("conveyor-1", loaded.Simulation.TestScenarioTargetId);
        Assert.Equal(7, loaded.Simulation.TestScenarioBatchRepetitions);
        var fault = Assert.IsType<TestScenarioFaultDefinition>(
            loaded.Simulation.TestScenarioFault);
        Assert.True(fault.Enabled);
        Assert.Equal(TestScenarioFaultKind.StuckDigitalInput, fault.Kind);
        Assert.Equal("di.part-present", fault.TargetId);
        Assert.True(fault.ForcedValue);
        Assert.Equal(120, fault.InjectTick);
        Assert.Equal(4, fault.HoldTicks);
        Assert.Equal("main", fault.RestartSequenceId);
        Assert.Collection(
            loaded.Simulation.TestScenarioAssertions,
            assertion =>
            {
                Assert.Equal("cycle-completed", assertion.AssertionId);
                Assert.Equal(TestScenarioAssertionKind.AutomaticCycleCompleted, assertion.Kind);
                Assert.Equal(2, assertion.MinimumCount);
            },
            assertion =>
            {
                Assert.Equal("faults-cleared", assertion.AssertionId);
                Assert.Equal(TestScenarioAssertionKind.NoActiveFaults, assertion.Kind);
            },
            assertion =>
            {
                Assert.Equal("conveyor-stopped", assertion.AssertionId);
                Assert.Equal(TestScenarioAssertionKind.FinalEquipmentState, assertion.Kind);
                Assert.Equal("conveyor-1", assertion.TargetId);
                Assert.Equal("Stopped", assertion.ExpectedState);
            });
        var automaticRun = Assert.IsType<AutomaticRunDefinition>(loaded.Simulation.AutomaticRun);
        Assert.Equal("main", automaticRun.SequenceId);
        Assert.Equal("di.start", automaticRun.StartInputId);
        Assert.True(automaticRun.StartInputValue);
        Assert.True(automaticRun.Repeat);
        Assert.Equal(250, automaticRun.RepeatDelayMilliseconds);
        Assert.Single(loaded.Axes);
        Assert.Equal("x", loaded.Axes[0].Id);
        Assert.Equal(AxisKind.Linear, loaded.Axes[0].Kind);
        Assert.Equal(200.0, loaded.Axes[0].MaxVelocity);
        Assert.Equal(800.0, loaded.Axes[0].MaxAcceleration);
        Assert.Equal(700.0, loaded.Axes[0].MaxDeceleration);
        Assert.Equal(0.08, loaded.Axes[0].FollowingErrorLimit);
        var recipe = Assert.IsType<MultiAxisCommissioningRecipeDefinition>(
            loaded.MultiAxisCommissioningRecipe);
        Assert.Equal("pick-position", recipe.Id);
        Assert.Equal("Pick position", recipe.Name);
        Assert.Equal(4, recipe.ValidationRepetitions);
        Assert.Collection(
            recipe.Targets,
            target =>
            {
                Assert.Equal("y", target.AxisId);
                Assert.Equal(120, target.TargetPosition);
            },
            target =>
            {
                Assert.Equal("x", target.AxisId);
                Assert.Equal(240, target.TargetPosition);
            });
        Assert.Single(loaded.Channels);
        Assert.Equal(ChannelKind.DigitalInput, loaded.Channels[0].Kind);
        Assert.Equal(3, loaded.Devices.Count);
        var loadedCamera = Assert.Single(loaded.Devices, device => device.Id == "cam1");
        Assert.Equal(DeviceKind.Camera, loadedCamera.Kind);
        Assert.Equal(300.0, loadedCamera.MountPosition.Z);
        var camera = Assert.IsType<VirtualCameraDefinition>(loadedCamera.Camera);
        Assert.Equal(20, camera.ExposureDelayMilliseconds);
        Assert.Equal(30, camera.TransferDelayMilliseconds);
        Assert.Equal(PlaceholderInspectionDecision.Fail, camera.PlaceholderDecision);
        var imageSource = Assert.IsType<VirtualSingleImageSourceDefinition>(camera.SingleImageSource);
        Assert.Equal("assets/presence-check.pgm", imageSource.SourceRelativePath);
        Assert.Equal(16, imageSource.Width);
        Assert.Equal(12, imageSource.Height);
        Assert.Equal("Mono8", imageSource.PixelFormat);
        var sensor = Assert.IsType<DigitalSensorDefinition>(
            Assert.Single(loaded.Devices, device => device.Id == "sensor1").Sensor);
        Assert.Equal("di.start", sensor.OutputChannelId);
        Assert.Equal("stage1", sensor.TargetComponentId);
        Assert.Equal(10, sensor.OnDelayMilliseconds);
        Assert.Equal(15, sensor.OffDelayMilliseconds);
        var loadLock = Assert.IsType<LoadLockDefinition>(
            Assert.Single(loaded.Devices, device => device.Id == "load-lock-1").LoadLock);
        Assert.Equal("outer-door", loadLock.OuterDoorComponentId);
        Assert.Equal("inner-door", loadLock.InnerDoorComponentId);
        Assert.Equal("do.evacuate", loadLock.EvacuateCommandChannelId);
        Assert.Equal("do.vent", loadLock.VentCommandChannelId);
        Assert.Equal("di.vacuum", loadLock.VacuumReadySensorChannelId);
        Assert.Equal("di.atmosphere", loadLock.AtmosphereReadySensorChannelId);
        Assert.Equal(250, loadLock.PumpDownDurationMilliseconds);
        Assert.Equal(300, loadLock.VentDurationMilliseconds);
        var layout = Assert.Single(loaded.Layouts);
        Assert.Equal("main-cell", layout.Id);
        Assert.Equal(25.0, layout.GridSize);
        Assert.True(layout.SnapToGrid);
        Assert.Equal(3, layout.Components.Count);
        var stage = Assert.Single(layout.Components, component => component.Id == "stage1");
        Assert.Equal(LayoutComponentKind.LinearStage, stage.Kind);
        Assert.Equal(80.0, stage.Transform.X);
        Assert.Equal(120.0, stage.Size.Width);
        Assert.Equal("x", stage.BehaviorBindingId);
        Assert.True(new MachineProjectLayoutValidator().Validate(loaded).IsValid);
        Assert.Single(loaded.Sequences);
        Assert.Single(loaded.Sequences[0].Steps);
        Assert.Equal("x", loaded.Sequences[0].Steps[0].ExpectedTargetId);
        Assert.Equal("Idle", loaded.Sequences[0].Steps[0].ExpectedState);
    }

    [Fact]
    public void Load_MissingSchema_FallsBackToCurrentSchema()
    {
        var json = "{\"name\":\"legacy\"}";
        var loaded = _store.Load(json);
        Assert.Equal(MachineProjectDocument.CurrentSchema, loaded.Schema);
        Assert.Equal("legacy", loaded.Name);
        Assert.Equal(5, loaded.Simulation.FixedStepMilliseconds);
        Assert.Equal(1.0, loaded.Simulation.DefaultTimeScale);
        Assert.Equal(1001, loaded.Simulation.Seed);
        Assert.Empty(loaded.Simulation.TestScenarioAssertions);
        Assert.Null(loaded.Simulation.AutomaticRun);
        Assert.Empty(loaded.Layouts);
        Assert.Null(loaded.MultiAxisCommissioningRecipe);
        Assert.True(new MachineProjectLayoutValidator().Validate(loaded).IsValid);
    }

    [Fact]
    public void Validate_InvalidGeometryAndDuplicateIds_ReturnsExplicitErrors()
    {
        var project = new MachineProjectDocument
        {
            Layouts =
            {
                new MachineLayoutDefinition
                {
                    Id = "layout",
                    Name = "Layout A",
                    GridSize = double.NaN,
                    Components =
                    {
                        new LayoutComponentDefinition
                        {
                            Id = "component",
                            Name = "Frame",
                            Kind = LayoutComponentKind.MachineFrame,
                            Transform = new Transform2D { X = double.PositiveInfinity },
                            Size = new Size2D { Width = 0, Height = -1 }
                        }
                    }
                },
                new MachineLayoutDefinition
                {
                    Id = "layout",
                    Name = "Layout B",
                    Components =
                    {
                        new LayoutComponentDefinition
                        {
                            Id = "component",
                            Name = "Duplicate Frame",
                            Kind = LayoutComponentKind.MachineFrame
                        }
                    }
                }
            }
        };

        var result = new MachineProjectLayoutValidator().Validate(project);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Code == MachineProjectLayoutValidationErrorCode.InvalidGridSize);
        Assert.Contains(result.Errors, error => error.Code == MachineProjectLayoutValidationErrorCode.InvalidTransform);
        Assert.Contains(result.Errors, error => error.Code == MachineProjectLayoutValidationErrorCode.InvalidSize);
        Assert.Contains(result.Errors, error => error.Code == MachineProjectLayoutValidationErrorCode.DuplicateLayoutId);
        Assert.Contains(result.Errors, error => error.Code == MachineProjectLayoutValidationErrorCode.DuplicateComponentId);
    }

    [Fact]
    public void Validate_InvalidBehaviorBindings_ReturnsExplicitErrors()
    {
        var project = new MachineProjectDocument
        {
            Axes =
            {
                new VirtualAxisDefinition
                {
                    Id = "axis-rotary",
                    Name = "Rotary Axis",
                    Kind = AxisKind.Rotary
                },
                new VirtualAxisDefinition
                {
                    Id = "axis-linear",
                    Name = "Linear Axis",
                    Kind = AxisKind.Linear
                }
            },
            Channels =
            {
                new ChannelDefinition
                {
                    Id = "do.sensor",
                    Name = "Invalid Sensor Output",
                    Kind = ChannelKind.DigitalOutput
                }
            },
            Devices =
            {
                new DeviceDefinition
                {
                    Id = "camera",
                    Name = "Not A Sensor",
                    Kind = DeviceKind.Camera
                },
                new DeviceDefinition
                {
                    Id = "sensor",
                    Name = "Sensor",
                    Kind = DeviceKind.Sensor,
                    Sensor = new DigitalSensorDefinition
                    {
                        OutputChannelId = "do.sensor",
                        TargetComponentId = "missing-target",
                        OnDelayMilliseconds = -1
                    }
                }
            },
            Layouts =
            {
                new MachineLayoutDefinition
                {
                    Id = "main",
                    Name = "Main",
                    Components =
                    {
                        new LayoutComponentDefinition
                        {
                            Id = "frame",
                            Name = "Frame",
                            Kind = LayoutComponentKind.MachineFrame,
                            BehaviorBindingId = "not-supported"
                        },
                        new LayoutComponentDefinition
                        {
                            Id = "stage-unbound",
                            Name = "Unbound Stage",
                            Kind = LayoutComponentKind.LinearStage
                        },
                        new LayoutComponentDefinition
                        {
                            Id = "stage-missing-axis",
                            Name = "Missing Axis Stage",
                            Kind = LayoutComponentKind.LinearStage,
                            BehaviorBindingId = "axis-missing"
                        },
                        new LayoutComponentDefinition
                        {
                            Id = "stage-rotary-axis",
                            Name = "Rotary-bound Stage",
                            Kind = LayoutComponentKind.LinearStage,
                            BehaviorBindingId = "axis-rotary"
                        },
                        new LayoutComponentDefinition
                        {
                            Id = "rotary-stage-linear-axis",
                            Name = "Linear-bound Rotary Stage",
                            Kind = LayoutComponentKind.RotaryStage,
                            BehaviorBindingId = "axis-linear"
                        },
                        new LayoutComponentDefinition
                        {
                            Id = "sensor-wrong-device",
                            Name = "Wrong Device Sensor",
                            Kind = LayoutComponentKind.DigitalSensor,
                            BehaviorBindingId = "camera"
                        },
                        new LayoutComponentDefinition
                        {
                            Id = "sensor-invalid-definition",
                            Name = "Invalid Definition Sensor",
                            Kind = LayoutComponentKind.DigitalSensor,
                            BehaviorBindingId = "sensor"
                        }
                    }
                }
            }
        };

        var result = new MachineProjectLayoutValidator().Validate(project);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Code == MachineProjectLayoutValidationErrorCode.UnsupportedBehaviorBinding);
        Assert.Contains(result.Errors, error => error.Code == MachineProjectLayoutValidationErrorCode.MissingBehaviorBinding);
        Assert.Contains(result.Errors, error => error.Code == MachineProjectLayoutValidationErrorCode.AxisBindingNotFound);
        Assert.Contains(result.Errors, error => error.Code == MachineProjectLayoutValidationErrorCode.AxisBindingMustBeLinear);
        Assert.Contains(result.Errors, error => error.Code == MachineProjectLayoutValidationErrorCode.AxisBindingMustBeRotary);
        Assert.Contains(result.Errors, error => error.Code == MachineProjectLayoutValidationErrorCode.SensorDeviceBindingInvalid);
        Assert.Contains(result.Errors, error => error.Code == MachineProjectLayoutValidationErrorCode.SensorOutputChannelMustBeDigitalInput);
        Assert.Contains(result.Errors, error => error.Code == MachineProjectLayoutValidationErrorCode.SensorTargetComponentNotFound);
        Assert.Contains(result.Errors, error => error.Code == MachineProjectLayoutValidationErrorCode.SensorDelayInvalid);
    }

    [Fact]
    public void SerializeAndLoad_RotaryStage_PreservesTypedAxisBinding()
    {
        var project = new MachineProjectDocument
        {
            Axes =
            {
                new VirtualAxisDefinition
                {
                    Id = "axis-r",
                    Name = "Rotation Axis",
                    Kind = AxisKind.Rotary,
                    Unit = "deg",
                    SoftLimitMin = -360,
                    SoftLimitMax = 360
                }
            },
            Layouts =
            {
                new MachineLayoutDefinition
                {
                    Id = "main",
                    Name = "Main",
                    Components =
                    {
                        new LayoutComponentDefinition
                        {
                            Id = "rotary-stage",
                            Name = "Rotary Stage",
                            Kind = LayoutComponentKind.RotaryStage,
                            BehaviorBindingId = "axis-r"
                        }
                    }
                }
            }
        };

        MachineProjectDocument restored = _store.Load(_store.Serialize(project));

        Assert.Equal(AxisKind.Rotary, Assert.Single(restored.Axes).Kind);
        LayoutComponentDefinition stage = Assert.Single(Assert.Single(restored.Layouts).Components);
        Assert.Equal(LayoutComponentKind.RotaryStage, stage.Kind);
        Assert.Equal("axis-r", stage.BehaviorBindingId);
        Assert.True(new MachineProjectLayoutValidator().Validate(restored).IsValid);
    }

    [Fact]
    public void SerializeAndLoad_InspectionHandoff_PreservesTypedCameraResultContract()
    {
        var project = new MachineProjectDocument
        {
            Devices =
            {
                new DeviceDefinition
                {
                    Id = "inspection-1",
                    Name = "Inspection Handoff",
                    Kind = DeviceKind.Inspection,
                    InspectionHandoff = new InspectionHandoffDefinition
                    {
                        CameraId = "camera-1",
                        InspectionPositionSensorChannelId = "di.position",
                        ResultAcceptedCommandChannelId = "do.accept",
                        InspectionReadyFeedbackChannelId = "di.ready",
                        InspectionCompleteFeedbackChannelId = "di.complete"
                    }
                }
            }
        };

        MachineProjectDocument restored = _store.Load(_store.Serialize(project));

        DeviceDefinition device = Assert.Single(restored.Devices);
        Assert.Equal(DeviceKind.Inspection, device.Kind);
        InspectionHandoffDefinition handoff = Assert.IsType<InspectionHandoffDefinition>(device.InspectionHandoff);
        Assert.Equal("camera-1", handoff.CameraId);
        Assert.Equal("do.accept", handoff.ResultAcceptedCommandChannelId);
        Assert.Equal("di.complete", handoff.InspectionCompleteFeedbackChannelId);
        Assert.Equal(MachineProjectDocument.CurrentSchema, restored.Schema);
    }

    [Fact]
    public void SerializeAndLoad_Prealigner_PreservesTypedAlignmentContract()
    {
        var project = new MachineProjectDocument
        {
            Devices =
            {
                new DeviceDefinition
                {
                    Id = "prealigner-1",
                    Name = "Pre-aligner",
                    Kind = DeviceKind.Prealigner,
                    Prealigner = new PrealignerDefinition
                    {
                        RotaryStageComponentId = "stage-r",
                        ClampCylinderComponentId = "clamp",
                        WaferPresentSensorChannelId = "di.wafer",
                        AlignmentAcceptedCommandChannelId = "do.accept",
                        AlignmentReadyFeedbackChannelId = "di.ready",
                        AlignmentCompleteFeedbackChannelId = "di.complete",
                        AlignmentTargetDegrees = 180,
                        AlignmentToleranceDegrees = 0.1
                    }
                }
            }
        };

        MachineProjectDocument restored = _store.Load(_store.Serialize(project));

        DeviceDefinition device = Assert.Single(restored.Devices);
        Assert.Equal(DeviceKind.Prealigner, device.Kind);
        PrealignerDefinition prealigner = Assert.IsType<PrealignerDefinition>(device.Prealigner);
        Assert.Equal("stage-r", prealigner.RotaryStageComponentId);
        Assert.Equal("clamp", prealigner.ClampCylinderComponentId);
        Assert.Equal("do.accept", prealigner.AlignmentAcceptedCommandChannelId);
        Assert.Equal(180, prealigner.AlignmentTargetDegrees);
        Assert.Equal(0.1, prealigner.AlignmentToleranceDegrees);
        Assert.Equal(MachineProjectDocument.CurrentSchema, restored.Schema);
    }

    [Fact]
    public void SerializeAndLoad_OhtHandoff_PreservesTypedOwnershipContract()
    {
        var project = new MachineProjectDocument
        {
            Devices =
            {
                new DeviceDefinition
                {
                    Id = "oht-1",
                    Name = "OHT Handoff",
                    Kind = DeviceKind.Oht,
                    OhtHandoff = new OhtHandoffDefinition
                    {
                        TransportConveyorComponentId = "transport",
                        RouteAvailableSensorChannelId = "di.route",
                        VehicleDockedSensorChannelId = "di.docked",
                        LoadPortReadySensorChannelId = "di.ready",
                        CarrierReceivedSensorChannelId = "di.received",
                        HandoffReadyFeedbackChannelId = "di.handoff-ready",
                        CarrierTransferredFeedbackChannelId = "di.transferred"
                    }
                }
            }
        };

        MachineProjectDocument restored = _store.Load(_store.Serialize(project));

        DeviceDefinition device = Assert.Single(restored.Devices);
        Assert.Equal(DeviceKind.Oht, device.Kind);
        OhtHandoffDefinition handoff = Assert.IsType<OhtHandoffDefinition>(device.OhtHandoff);
        Assert.Equal("transport", handoff.TransportConveyorComponentId);
        Assert.Equal("di.ready", handoff.LoadPortReadySensorChannelId);
        Assert.Equal("di.transferred", handoff.CarrierTransferredFeedbackChannelId);
        Assert.Equal(MachineProjectDocument.CurrentSchema, restored.Schema);
    }

    [Fact]
    public void Save_LegacyDocument_UpgradesSchemaWithoutExecutingOrChangingDefinitions()
    {
        var project = new MachineProjectDocument
        {
            Schema = "1.4",
            Name = "Legacy"
        };

        string json = _store.Save(project);
        MachineProjectDocument restored = _store.Load(json);

        Assert.Equal(MachineProjectDocument.CurrentSchema, project.Schema);
        Assert.Equal(MachineProjectDocument.CurrentSchema, restored.Schema);
        Assert.Equal("Legacy", restored.Name);
        Assert.Empty(restored.Axes);
        Assert.Empty(restored.Layouts);
    }

    [Fact]
    public void Load_Schema10CameraProperties_RemainReadableWithoutTypedCamera()
    {
        const string json = """
            {
              "schema": "1.0",
              "name": "Legacy Camera Project",
              "devices": [
                {
                  "id": "camera-top",
                  "name": "Top Camera",
                  "kind": "Camera",
                  "properties": {
                    "exposureDelayMs": "20",
                    "transferDelayMs": "30"
                  }
                }
              ]
            }
            """;

        var loaded = _store.Load(json);

        Assert.Equal("1.0", loaded.Schema);
        var camera = Assert.Single(loaded.Devices);
        Assert.Null(camera.Camera);
        Assert.Equal("20", camera.Properties["exposureDelayMs"]);
        Assert.Equal("30", camera.Properties["transferDelayMs"]);
        Assert.Empty(loaded.Layouts);
        Assert.True(new MachineProjectLayoutValidator().Validate(loaded).IsValid);
    }

    [Fact]
    public void Load_UndefinedPlaceholderDecision_IsRejected()
    {
        const string json = """
            {
              "schema": "1.1",
              "name": "Invalid Camera Project",
              "devices": [
                {
                  "id": "camera-top",
                  "name": "Top Camera",
                  "kind": "Camera",
                  "camera": {
                    "exposureDelayMilliseconds": 20,
                    "transferDelayMilliseconds": 30,
                    "placeholderDecision": 99
                  }
                }
              ]
            }
            """;

        Assert.Throws<ArgumentOutOfRangeException>(() => _store.Load(json));
    }

    [Fact]
    public async Task LoadAsync_SampleFile_ParsesWithoutError()
    {
        var path = "sample-pick-and-place.ovmachine";
        var loaded = await _store.LoadAsync(path);

        Assert.Equal("Sample Pick-and-Place Cell", loaded.Name);
        Assert.Equal(2, loaded.Axes.Count);
        var xAxis = Assert.Single(loaded.Axes, axis => axis.Id == "x");
        var yAxis = Assert.Single(loaded.Axes, axis => axis.Id == "y");
        Assert.Equal((100d, 200d), (xAxis.Position.X, xAxis.Position.Y));
        Assert.Equal((100d, 400d), (yAxis.Position.X, yAxis.Position.Y));
        var recipe = Assert.IsType<MultiAxisCommissioningRecipeDefinition>(
            loaded.MultiAxisCommissioningRecipe);
        Assert.Equal(new[] { "y", "x" }, recipe.Targets.Select(target => target.AxisId));
        Assert.Equal(3, recipe.ValidationRepetitions);
        Assert.Null(loaded.Simulation.AutomaticRun);
        Assert.NotNull(loaded.Simulation.PickPlaceWorkpiece);
        Assert.Equal("part-1", loaded.Simulation.PickPlaceWorkpiece.Id);
        Assert.Equal("x", loaded.Simulation.PickPlaceWorkpiece.XAxisId);
        Assert.Equal("y", loaded.Simulation.PickPlaceWorkpiece.YAxisId);
        Assert.Equal("do.gripper", loaded.Simulation.PickPlaceWorkpiece.GripperSignalId);
        Assert.Equal(240, loaded.Simulation.PickPlaceWorkpiece.PickX);
        Assert.Equal(120, loaded.Simulation.PickPlaceWorkpiece.PickY);
        Assert.Single(loaded.Sequences);
        Assert.Equal(15, loaded.Sequences[0].Steps.Count);
        Assert.Equal("move-y-pick", loaded.Sequences[0].Steps[0].Id);
        Assert.Equal(2, loaded.Sequences[0].Steps.Count(step => step.Action == SequenceStepAction.SetSignal));
        Assert.Equal(6, loaded.Sequences[0].Steps.Count(step => step.Action == SequenceStepAction.MoveAxis));
        Assert.Equal(6, loaded.Sequences[0].Steps.Count(step => step.Action == SequenceStepAction.WaitAxisDone));

        var restored = _store.Load(_store.Serialize(loaded));
        Assert.Null(restored.Simulation.AutomaticRun);
        Assert.Equal("part-1", restored.Simulation.PickPlaceWorkpiece?.Id);
        Assert.Equal("x", restored.Simulation.PickPlaceWorkpiece?.XAxisId);
        Assert.Equal("y", restored.Simulation.PickPlaceWorkpiece?.YAxisId);
        Assert.Equal("do.gripper", restored.Simulation.PickPlaceWorkpiece?.GripperSignalId);
        Assert.Equal(240, restored.Simulation.PickPlaceWorkpiece?.PickX);
        Assert.Equal(120, restored.Simulation.PickPlaceWorkpiece?.PickY);
        Assert.Equal(
            loaded.Sequences[0].Steps.Select(step => $"{step.Id}:{step.Action}:{step.TargetId}:{step.Parameter}"),
            restored.Sequences[0].Steps.Select(step => $"{step.Id}:{step.Action}:{step.TargetId}:{step.Parameter}"));
    }

    [Fact]
    public async Task LoadAsync_VisionInspectionCell_PreservesDigitalCycleContract()
    {
        var loaded = await _store.LoadAsync("VisionInspectionCell.ovmachine");

        Assert.Equal("1.5", loaded.Schema);
        Assert.Equal(3, loaded.Channels.Count);
        Assert.Contains(loaded.Channels, channel =>
            channel.Id == "di.cycle-start" && channel.Kind == ChannelKind.DigitalInput);
        Assert.Equal(2, loaded.Channels.Count(channel => channel.Kind == ChannelKind.DigitalOutput));

        var camera = Assert.Single(loaded.Devices, device => device.Kind == DeviceKind.Camera);
        var cameraDefinition = Assert.IsType<VirtualCameraDefinition>(camera.Camera);
        Assert.Equal(20, cameraDefinition.ExposureDelayMilliseconds);
        Assert.Equal(30, cameraDefinition.TransferDelayMilliseconds);
        Assert.Equal(PlaceholderInspectionDecision.Pass, cameraDefinition.PlaceholderDecision);

        var sequence = Assert.Single(loaded.Sequences);
        Assert.Equal("inspection-cycle", sequence.Id);
        Assert.Collection(
            sequence.Steps,
            step => Assert.Equal(SequenceStepAction.WaitSignal, step.Action),
            step => Assert.Equal(SequenceStepAction.SetSignal, step.Action),
            step => Assert.Equal(SequenceStepAction.MoveAxis, step.Action),
            step => Assert.Equal(SequenceStepAction.WaitAxisDone, step.Action),
            step =>
            {
                Assert.Equal(SequenceStepAction.TriggerCamera, step.Action);
                Assert.Equal("cam1", step.TargetId);
                Assert.Equal("presence-check", step.Parameter);
            },
            step =>
            {
                Assert.Equal(SequenceStepAction.WaitVisionResult, step.Action);
                Assert.Equal("cam1", step.TargetId);
                Assert.Equal("active-off-failed", step.FailureStepId);
            },
            step => Assert.Equal(SequenceStepAction.SetSignal, step.Action),
            step => Assert.Equal(SequenceStepAction.SetSignal, step.Action),
            step => Assert.Equal(SequenceStepAction.SetSignal, step.Action),
            step => Assert.Equal(SequenceStepAction.Complete, step.Action));
    }

    [Fact]
    public async Task LoadAsync_AutomaticTransferCell_PreservesLayoutSensorAndAutomaticRunContract()
    {
        var loaded = await _store.LoadAsync("AutomaticTransferCell.ovmachine");

        Assert.Equal("1.5", loaded.Schema);
        Assert.Equal("main-cell", loaded.Simulation.ActiveLayoutId);
        var automaticRun = Assert.IsType<AutomaticRunDefinition>(loaded.Simulation.AutomaticRun);
        Assert.Equal("auto-transfer-cycle", automaticRun.SequenceId);
        Assert.True(automaticRun.Repeat);
        Assert.Equal(250, automaticRun.RepeatDelayMilliseconds);

        var layout = Assert.Single(loaded.Layouts);
        Assert.Equal(7, layout.Components.Count);
        var stage = Assert.Single(layout.Components, item => item.Kind == LayoutComponentKind.LinearStage);
        Assert.Equal("x", stage.BehaviorBindingId);
        var sensorComponent = Assert.Single(
            layout.Components,
            item => item.Id == "sensor-1");
        var sensorDevice = Assert.Single(loaded.Devices, item => item.Id == "device.sensor-1");
        Assert.Equal(sensorDevice.Id, sensorComponent.BehaviorBindingId);
        var sensor = Assert.IsType<DigitalSensorDefinition>(sensorDevice.Sensor);
        Assert.Equal("workpiece-1", sensor.TargetComponentId);
        Assert.Equal("di.station-present", sensor.OutputChannelId);
        var cylinderComponent = Assert.Single(
            layout.Components,
            item => item.Kind == LayoutComponentKind.PneumaticCylinder);
        var cylinderDevice = Assert.Single(loaded.Devices, item => item.Kind == DeviceKind.Cylinder);
        Assert.Equal(cylinderDevice.Id, cylinderComponent.BehaviorBindingId);
        var cylinder = Assert.IsType<PneumaticCylinderDefinition>(cylinderDevice.Cylinder);
        Assert.Equal("do.cylinder-1.extend", cylinder.ExtendCommandChannelId);
        Assert.Equal("di.cylinder-1.extended", cylinder.ExtendedSensorChannelId);
        Assert.Equal("di.cylinder-1.retracted", cylinder.RetractedSensorChannelId);
        Assert.Equal(100, cylinder.ExtendDurationMilliseconds);
        Assert.Equal(100, cylinder.RetractDurationMilliseconds);
        Assert.Equal(60, cylinder.Stroke);
        var conveyorComponent = Assert.Single(
            layout.Components,
            item => item.Kind == LayoutComponentKind.Conveyor);
        var conveyorDevice = Assert.Single(loaded.Devices, item => item.Kind == DeviceKind.Conveyor);
        Assert.Equal(conveyorDevice.Id, conveyorComponent.BehaviorBindingId);
        var conveyor = Assert.IsType<ConveyorDefinition>(conveyorDevice.Conveyor);
        Assert.Equal("do.conveyor-1.run", conveyor.RunCommandChannelId);
        Assert.Equal("do.conveyor-1.reverse", conveyor.ReverseCommandChannelId);
        Assert.Equal(400, conveyor.SpeedUnitsPerSecond);
        var workpieceComponent = Assert.Single(
            layout.Components,
            item => item.Kind == LayoutComponentKind.Workpiece);
        var workpieceDevice = Assert.Single(loaded.Devices, item => item.Kind == DeviceKind.Workpiece);
        Assert.Equal(workpieceDevice.Id, workpieceComponent.BehaviorBindingId);
        var workpiece = Assert.IsType<WorkpieceDefinition>(workpieceDevice.Workpiece);
        Assert.Equal(conveyorComponent.Id, workpiece.ConveyorComponentId);
        Assert.Equal("Inspection Carrier", workpiece.Type);
        Assert.Equal(WorkpieceInspectionState.Pending, workpiece.InspectionState);
        Assert.Equal(2, loaded.Devices.Count(item => item.Kind == DeviceKind.Sensor));

        var validation = new MachineProjectLayoutValidator().Validate(loaded);
        Assert.True(validation.IsValid, string.Join("; ", validation.Errors.Select(item => item.Message)));
        Assert.Single(loaded.Sequences, item => item.Id == automaticRun.SequenceId);
    }
}
