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
}
