using OpenVisionLab.Machine.Core.Devices;
using OpenVisionLab.Machine.Core.Layouts;
using OpenVisionLab.Machine.Core.Models;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Core.Sequences;
using Xunit;

namespace OpenVisionLab.Machine.Core.Tests;

public sealed class LayoutComponentAuthoringServiceTests
{
    [Fact]
    public void IndependentMaterialPositionsRoundTripAndProtectTheirConveyorDependency()
    {
        var project = new MachineProjectDocument
        {
            Stations = [new() { Id = "s", Name = "Station", Units = [new() { Id = "u", Name = "Unit", RepeatCount = 7 }] }]
        };
        var service = new LayoutComponentAuthoringService();
        var conveyor = service.TryAdd(project, LayoutComponentKind.Conveyor, unitId: "u");
        Assert.True(conveyor.IsSuccess);
        var deviceCount = project.Devices.Count;
        var feed = service.TryAdd(project, LayoutComponentKind.FeedPosition, unitId: "u");
        var exit = service.TryAdd(project, LayoutComponentKind.ExitPosition, unitId: "u");
        Assert.True(feed.IsSuccess);
        Assert.True(exit.IsSuccess);
        Assert.Equal(deviceCount, project.Devices.Count);
        feed.Component!.MaterialConveyorId = conveyor.Component!.Id;
        exit.Component!.MaterialConveyorId = conveyor.Component.Id;
        exit.Component.MaterialRoute = "NG";
        project.MaterialFlow.Enabled = true;
        project.MaterialFlow.TotalFeedCount = 2;
        Assert.True(new MachineProjectLayoutValidator().Validate(project).IsValid);
        var store = new ProjectDocumentStore();
        var reopened = store.Load(store.Save(project));
        Assert.Equal(2, reopened.MaterialFlow.TotalFeedCount);
        Assert.Equal(7, reopened.Stations[0].Units[0].RepeatCount);
        Assert.Equal("NG", reopened.Layouts[0].Components.Single(component => component.Id == exit.Component.Id).MaterialRoute);
        var removal = service.TryRemove(project, conveyor.Layout!, conveyor.Component.Id);
        Assert.Equal(LayoutComponentRemovalFailureKind.MaterialPositionDependency, removal.Failure);
        Assert.Contains(conveyor.Component, conveyor.Layout!.Components);
        feed.Component.MaterialConveyorId = "missing";
        Assert.Contains(new MachineProjectLayoutValidator().Validate(project).Errors, error => error.ComponentId == feed.Component.Id);
        feed.Component.MaterialConveyorId = conveyor.Component.Id;
        Assert.True(new MachineProjectLayoutValidator().Validate(project).IsValid);
    }

    [Theory]
    [InlineData(NumericalCameraInputKind.Image2D)]
    [InlineData(NumericalCameraInputKind.HeightMap3D)]
    public void CameraVariantsPreserveExistingSourcesAndRollbackFailedAdmission(NumericalCameraInputKind inputKind)
    {
        var source = new VirtualSingleImageSourceDefinition { SourceRelativePath = "existing.png", Width = 12, Height = 9, PixelFormat = "Mono8" };
        var existing = new DeviceDefinition { Id = "existing-camera", Name = "Existing", Kind = DeviceKind.Camera, Camera = new() { SingleImageSource = source } };
        var project = new MachineProjectDocument { Devices = [existing], Stations = [new()
        {
            Id = "station", Name = "Station", Units = [new() { Id = "unit", Name = "Unit" }]
        }] };
        var service = new LayoutComponentAuthoringService();
        var rejected = service.TryAdd(project, LayoutComponentKind.Camera, unitId: "missing", cameraInputKind: inputKind);
        Assert.False(rejected.IsSuccess);
        Assert.Single(project.Devices);
        Assert.Empty(project.Layouts);
        Assert.Same(source, existing.Camera!.SingleImageSource);
        Assert.Null(existing.Camera.NumericalInput);
        var added = service.TryAdd(project, LayoutComponentKind.Camera, unitId: "unit", cameraInputKind: inputKind);
        Assert.True(added.IsSuccess);
        Assert.Equal("unit", added.Component!.UnitId);
        var camera = project.Devices.Single(device => device.Id == added.Component.BehaviorBindingId);
        Assert.NotEqual(existing.Id, camera.Id);
        Assert.Equal(inputKind, camera.Camera!.NumericalInput!.InputKind);
        Assert.Null(camera.Camera.NumericalInput.Validate());
        Assert.Same(source, existing.Camera.SingleImageSource);
        Assert.Null(existing.Camera.NumericalInput);
        Assert.True(new MachineProjectLayoutValidator().Validate(project).IsValid);
    }

    [Fact]
    public void UnitDefaultsPlaceNewPartsAtTheirUnitAndExplicitDropPreservesTheRequestedWorldPoint()
    {
        var origin = LayoutComponentPlacementService.CreateUnitBaseOrigin(2);
        var unit = new MachineUnitDefinition { Id = "unit-3", Name = "Third unit", BaseOrigin = origin };
        var project = new MachineProjectDocument { Stations = [new() { Id = "station", Name = "Station", Units = [unit] }] };
        var service = new LayoutComponentAuthoringService();
        var stage = service.TryAdd(project, LayoutComponentKind.LinearStage, unitId: unit.Id);
        Assert.True(stage.IsSuccess);
        Assert.InRange(stage.Component!.Transform.X - origin.X, -55, -45);
        Assert.InRange(stage.Component.Transform.Y - origin.Y, 45, 55);
        Assert.Equal(stage.Component.Transform.X, Assert.Single(project.Axes).Position.X);
        Assert.Equal(stage.Component.Transform.Y, Assert.Single(project.Axes).Position.Y);

        var defaultConveyor = service.TryAdd(project, LayoutComponentKind.Conveyor, unitId: unit.Id);
        Assert.True(defaultConveyor.IsSuccess);
        Assert.Equal(452.5, defaultConveyor.Component!.Transform.X);
        Assert.Equal(-447.5, defaultConveyor.Component.Transform.Y);
        Assert.Equal(defaultConveyor.Component.Transform.X, Assert.Single(project.Devices).MountPosition.X);
        Assert.Equal(defaultConveyor.Component.Transform.Y, Assert.Single(project.Devices).MountPosition.Y);

        var conveyor = service.TryAdd(project, LayoutComponentKind.Conveyor, worldX: 105, worldY: 195, unitId: unit.Id);
        Assert.True(conveyor.IsSuccess);
        Assert.Equal(110, conveyor.Component!.Transform.X);
        Assert.Equal(200, conveyor.Component.Transform.Y);
        Assert.Equal(new Coordinate3D(450, -450, 0), unit.BaseOrigin);
    }

    [Fact]
    public void TryAddAssignsTheRequestedExistingUnit()
    {
        var unit = new MachineUnitDefinition { Id = "unit-camera", Name = "Camera" };
        var project = new MachineProjectDocument
        {
            Name = "Unit-scoped authoring",
            Stations =
            [
                new MachineStationDefinition
                {
                    Id = "station-inspection",
                    Name = "Inspection",
                    Units = [unit]
                }
            ]
        };

        var result = new LayoutComponentAuthoringService().TryAdd(
            project,
            LayoutComponentKind.MachineFrame,
            unitId: unit.Id);

        Assert.True(result.IsSuccess);
        Assert.Equal(unit.Id, result.Component!.UnitId);
        Assert.True(new MachineProjectLayoutValidator().Validate(project).IsValid);
    }

    [Theory]
    [InlineData(LayoutComponentKind.MachineFrame)]
    [InlineData(LayoutComponentKind.Camera)]
    public void TryAddUnknownUnitRollsBackImplicitLayoutAndComponent(LayoutComponentKind kind)
    {
        var project = new MachineProjectDocument { Name = "Invalid unit" };

        var result = new LayoutComponentAuthoringService().TryAdd(
            project,
            kind,
            unitId: "missing-unit");

        Assert.False(result.IsSuccess);
        Assert.Equal(LayoutComponentAuthoringFailureKind.InvalidDefinition, result.Failure?.Kind);
        Assert.Equal(
            MachineProjectLayoutValidationErrorCode.ComponentUnitNotFound,
            result.Failure?.ValidationError?.Code);
        Assert.Empty(project.Layouts);
        Assert.Empty(project.Devices);
        Assert.Null(project.Simulation.ActiveLayoutId);
    }

    [Fact]
    public void TryAddLinearStageCreatesBoundAxisAndSnapsDropPosition()
    {
        var project = new MachineProjectDocument { Name = "Authoring" };
        var service = new LayoutComponentAuthoringService();

        var result = service.TryAdd(
            project,
            LayoutComponentKind.LinearStage,
            worldX: 45,
            worldY: 185);

        Assert.True(result.IsSuccess);
        var layout = Assert.Single(project.Layouts);
        Assert.NotNull(result.Component);
        var component = result.Component!;
        var axis = Assert.Single(project.Axes);
        Assert.Same(layout, result.Layout);
        Assert.Equal("main-cell", project.Simulation.ActiveLayoutId);
        Assert.Equal(LayoutComponentKind.LinearStage, component.Kind);
        Assert.Equal(50, component.Transform.X);
        Assert.Equal(190, component.Transform.Y);
        Assert.Equal(axis.Id, component.BehaviorBindingId);
        Assert.Equal(component.Transform.X, axis.Position.X);
        Assert.Equal(component.Transform.Y, axis.Position.Y);
        Assert.True(new MachineProjectLayoutValidator().Validate(project).IsValid);
    }

    [Fact]
    public void TryAddSensorWithoutTargetRollsBackImplicitLayout()
    {
        var project = new MachineProjectDocument { Name = "Authoring" };
        var service = new LayoutComponentAuthoringService();

        var result = service.TryAdd(project, LayoutComponentKind.DigitalSensor);

        Assert.False(result.IsSuccess);
        Assert.Equal(
            LayoutComponentAuthoringFailureKind.SensorTargetRequired,
            result.Failure?.Kind);
        Assert.Empty(project.Layouts);
        Assert.Null(project.Simulation.ActiveLayoutId);
        Assert.Empty(project.Axes);
        Assert.Empty(project.Devices);
        Assert.Empty(project.Channels);
    }

    [Fact]
    public void TryAddCameraPlacesAnExistingDeviceAtItsMountPosition()
    {
        var layout = new MachineLayoutDefinition { Id = "main", Name = "Main" };
        var project = new MachineProjectDocument { Name = "Authoring" };
        project.Layouts.Add(layout);
        project.Simulation.ActiveLayoutId = layout.Id;
        project.Devices.Add(new DeviceDefinition
        {
            Id = "device.camera-1",
            Name = "Top camera",
            Kind = DeviceKind.Camera,
            MountPosition = new(470, 230, 120)
        });

        var result = new LayoutComponentAuthoringService().TryAdd(project, LayoutComponentKind.Camera);

        Assert.True(result.IsSuccess);
        Assert.Same(layout, result.Layout);
        Assert.Equal(LayoutComponentKind.Camera, result.Component!.Kind);
        Assert.Equal("device.camera-1", result.Component.BehaviorBindingId);
        Assert.Equal(470, result.Component.Transform.X);
        Assert.Equal(230, result.Component.Transform.Y);
        Assert.Single(project.Devices);
        Assert.True(new MachineProjectLayoutValidator().Validate(project).IsValid);
    }

    [Fact]
    public void TryAddCameraAgainCreatesIndependentPlacementAndDevice()
    {
        var layout = new MachineLayoutDefinition
        {
            Id = "main",
            Name = "Main",
            Components =
            [
                new LayoutComponentDefinition
                {
                    Id = "camera-1",
                    Name = "Top camera",
                    Kind = LayoutComponentKind.Camera,
                    BehaviorBindingId = "device.camera-1"
                }
            ]
        };
        var project = new MachineProjectDocument { Name = "Authoring" };
        project.Layouts.Add(layout);
        project.Simulation.ActiveLayoutId = layout.Id;
        project.Devices.Add(new DeviceDefinition
        {
            Id = "device.camera-1",
            Name = "Top camera",
            Kind = DeviceKind.Camera
        });

        var result = new LayoutComponentAuthoringService().TryAdd(project, LayoutComponentKind.Camera);

        Assert.True(result.IsSuccess);
        Assert.Equal("camera-2", result.Component!.Id);
        Assert.Equal("device.camera-2", result.Component.BehaviorBindingId);
        Assert.Equal(2, layout.Components.Count);
        Assert.Equal(2, project.Devices.Count);
        Assert.Empty(project.Sequences);
    }

    [Fact]
    public void TryRemoveConveyorWithWorkpieceReturnsDependencyWithoutMutation()
    {
        var project = new MachineProjectDocument { Name = "Authoring" };
        var layout = new MachineLayoutDefinition
        {
            Id = "main-cell",
            Name = "Main Cell"
        };
        var conveyor = new LayoutComponentDefinition
        {
            Id = "conveyor-1",
            Name = "Conveyor 1",
            Kind = LayoutComponentKind.Conveyor,
            Transform = new Transform2D(),
            Size = new Size2D { Width = 360, Height = 80 },
            BehaviorBindingId = "device.conveyor-1"
        };
        var workpiece = new LayoutComponentDefinition
        {
            Id = "workpiece-1",
            Name = "Workpiece 1",
            Kind = LayoutComponentKind.Workpiece,
            Transform = new Transform2D(),
            Size = new Size2D { Width = 42, Height = 42 },
            BehaviorBindingId = "device.workpiece-1"
        };
        layout.Components.AddRange([conveyor, workpiece]);
        project.Layouts.Add(layout);
        project.Simulation.ActiveLayoutId = layout.Id;
        project.Devices.Add(new DeviceDefinition
        {
            Id = "device.workpiece-1",
            Name = "Workpiece 1",
            Kind = DeviceKind.Workpiece,
            Workpiece = new WorkpieceDefinition
            {
                ConveyorComponentId = conveyor.Id
            }
        });
        var service = new LayoutComponentAuthoringService();

        var result = service.TryRemove(project, layout, conveyor.Id);

        Assert.False(result.IsSuccess);
        Assert.Equal(
            LayoutComponentRemovalFailureKind.WorkpieceDependency,
            result.Failure);
        Assert.Same(workpiece, result.BlockingComponent);
        Assert.Contains(conveyor, layout.Components);
        Assert.Equal(2, layout.Components.Count);
    }

    [Fact]
    public void BatchRemovalIsAtomicAndKeepsReferencedSequenceStepsForRepair()
    {
        var conveyor = new LayoutComponentDefinition
        {
            Id = "conveyor-1",
            Name = "Conveyor 1",
            Kind = LayoutComponentKind.Conveyor,
            BehaviorBindingId = "device.conveyor-1"
        };
        var workpiece = new LayoutComponentDefinition
        {
            Id = "workpiece-1",
            Name = "Workpiece 1",
            Kind = LayoutComponentKind.Workpiece,
            BehaviorBindingId = "device.workpiece-1"
        };
        var layout = new MachineLayoutDefinition
        {
            Id = "main-cell",
            Name = "Main Cell",
            Components = [conveyor, workpiece]
        };
        var step = new SequenceStepDefinition
        {
            Id = "step-1",
            Name = "Transfer",
            Action = SequenceStepAction.EjectWorkpiece,
            TargetId = conveyor.Id,
            WorkpieceComponentId = workpiece.Id,
            ExpectedTargetId = workpiece.Id
        };
        var project = new MachineProjectDocument
        {
            Name = "Authoring",
            Layouts = [layout],
            Sequences =
            [
                new SequenceDefinition
                {
                    Id = "sequence-1",
                    Name = "Cycle",
                    Steps = [step]
                }
            ]
        };
        project.Devices.Add(new DeviceDefinition
        {
            Id = "device.workpiece-1",
            Name = "Workpiece 1",
            Kind = DeviceKind.Workpiece,
            Workpiece = new WorkpieceDefinition { ConveyorComponentId = conveyor.Id }
        });
        var service = new LayoutComponentAuthoringService();

        var impacts = service.GetRemovalImpacts(project, [conveyor.Id, workpiece.Id]);
        var rejected = service.TryRemove(project, layout, [conveyor.Id, "missing"]);

        Assert.Equal(3, impacts.Count);
        Assert.Contains(
            impacts,
            impact => impact.ReferenceKind == LayoutComponentRemovalReferenceKind.Target && impact.ComponentId == conveyor.Id);
        Assert.Contains(
            impacts,
            impact => impact.ReferenceKind == LayoutComponentRemovalReferenceKind.Workpiece && impact.ComponentId == workpiece.Id);
        Assert.Contains(
            impacts,
            impact => impact.ReferenceKind == LayoutComponentRemovalReferenceKind.ExpectedTarget && impact.ComponentId == workpiece.Id);
        Assert.False(rejected.IsSuccess);
        Assert.Equal(LayoutComponentRemovalFailureKind.NotFound, rejected.Failure);
        Assert.Equal(2, layout.Components.Count);

        var result = service.TryRemove(project, layout, [conveyor.Id, workpiece.Id]);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.RemovedComponents.Count);
        Assert.Empty(layout.Components);
        Assert.Equal(conveyor.Id, step.TargetId);
        Assert.Equal(workpiece.Id, step.WorkpieceComponentId);
        Assert.Equal(workpiece.Id, step.ExpectedTargetId);
        Assert.Single(project.Devices);
    }

    [Fact]
    public void RemovalImpactsResolveSequenceAxisBindingsToSelectedComponents()
    {
        var stage = new LayoutComponentDefinition
        {
            Id = "stage-1",
            Name = "Stage 1",
            Kind = LayoutComponentKind.LinearStage,
            BehaviorBindingId = "axis-1"
        };
        var project = new MachineProjectDocument
        {
            Layouts = [new MachineLayoutDefinition { Id = "main", Name = "Main", Components = [stage] }],
            Sequences = [new SequenceDefinition
            {
                Id = "cycle",
                Name = "Cycle",
                Steps = [new SequenceStepDefinition
                {
                    Id = "move",
                    Name = "Move",
                    Action = SequenceStepAction.MoveAxis,
                    TargetId = "axis-1",
                    ExpectedTargetId = "axis-1"
                }]
            }]
        };

        var impacts = new LayoutComponentAuthoringService().GetRemovalImpacts(project, [stage.Id]);

        Assert.Equal(2, impacts.Count);
        Assert.All(impacts, impact => Assert.Equal(stage.Id, impact.ComponentId));
        Assert.Contains(impacts, impact => impact.ReferenceKind == LayoutComponentRemovalReferenceKind.Target);
        Assert.Contains(impacts, impact => impact.ReferenceKind == LayoutComponentRemovalReferenceKind.ExpectedTarget);
    }
}
