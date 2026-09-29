using OpenVisionLab.Machine.Core.Channels;
using OpenVisionLab.Machine.Core.Devices;
using OpenVisionLab.Machine.Core.Layouts;
using OpenVisionLab.Machine.Core.Projects;
using Xunit;

namespace OpenVisionLab.Machine.Core.Tests;

public sealed class MachineProjectLayoutBehaviorBindingValidatorTests
{
    [Fact]
    public void Validate_ReportsBindingErrorsWithoutValidatingLayoutGeometry()
    {
        var project = new MachineProjectDocument
        {
            Layouts =
            [
                new MachineLayoutDefinition
                {
                    Id = "main-cell",
                    Name = "Main Cell",
                    GridSize = 0,
                    Components =
                    [
                        new LayoutComponentDefinition
                        {
                            Id = "stage-1",
                            Name = "Stage 1",
                            Kind = LayoutComponentKind.LinearStage,
                            BehaviorBindingId = "axis-missing"
                        }
                    ]
                }
            ]
        };

        var result = new MachineProjectLayoutBehaviorBindingValidator().Validate(project);

        var error = Assert.Single(result.Errors);
        Assert.Equal(MachineProjectLayoutValidationErrorCode.AxisBindingNotFound, error.Code);
        Assert.Equal("stage-1", error.ComponentId);
    }

    [Fact]
    public void Validate_RejectsDigitalSensorTargetInAnotherLayout()
    {
        var project = new MachineProjectDocument
        {
            Channels =
            {
                new ChannelDefinition
                {
                    Id = "di.sensor",
                    Name = "Sensor",
                    Kind = ChannelKind.DigitalInput
                }
            },
            Devices =
            {
                new DeviceDefinition
                {
                    Id = "device.sensor",
                    Name = "Sensor",
                    Kind = DeviceKind.Sensor,
                    Sensor = new DigitalSensorDefinition
                    {
                        OutputChannelId = "di.sensor",
                        TargetComponentId = "target"
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
                            Id = "sensor",
                            Name = "Sensor",
                            Kind = LayoutComponentKind.DigitalSensor,
                            BehaviorBindingId = "device.sensor"
                        }
                    }
                },
                new MachineLayoutDefinition
                {
                    Id = "other",
                    Name = "Other",
                    Components =
                    {
                        new LayoutComponentDefinition
                        {
                            Id = "target",
                            Name = "Target",
                            Kind = LayoutComponentKind.MachineFrame
                        }
                    }
                }
            }
        };

        var result = new MachineProjectLayoutBehaviorBindingValidator().Validate(project);

        var error = Assert.Single(result.Errors);
        Assert.Equal(
            MachineProjectLayoutValidationErrorCode.SensorTargetComponentMustBeInSameLayout,
            error.Code);
        Assert.Equal("main", error.LayoutId);
        Assert.Equal("sensor", error.ComponentId);
        Assert.Contains("target", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_RejectsCameraBindingToNonCameraDevice()
    {
        var project = new MachineProjectDocument
        {
            Devices =
            {
                new DeviceDefinition
                {
                    Id = "device.conveyor",
                    Name = "Conveyor",
                    Kind = DeviceKind.Conveyor
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
                            Id = "camera-1",
                            Name = "Camera",
                            Kind = LayoutComponentKind.Camera,
                            BehaviorBindingId = "device.conveyor"
                        }
                    }
                }
            }
        };

        var result = new MachineProjectLayoutBehaviorBindingValidator().Validate(project);

        var error = Assert.Single(result.Errors);
        Assert.Equal(MachineProjectLayoutValidationErrorCode.CameraDeviceBindingInvalid, error.Code);
        Assert.Equal("camera-1", error.ComponentId);
    }

    [Fact]
    public void Validate_RequiresCameraBehaviorBinding()
    {
        var project = new MachineProjectDocument
        {
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
                            Id = "camera-1",
                            Name = "Camera",
                            Kind = LayoutComponentKind.Camera
                        }
                    }
                }
            }
        };

        var result = new MachineProjectLayoutBehaviorBindingValidator().Validate(project);

        var error = Assert.Single(result.Errors);
        Assert.Equal(MachineProjectLayoutValidationErrorCode.MissingBehaviorBinding, error.Code);
        Assert.Equal("camera-1", error.ComponentId);
    }
}
