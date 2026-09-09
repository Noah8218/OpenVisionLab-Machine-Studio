using OpenVisionLab;
using OpenVisionLab.Machine.Core.Axes;
using OpenVisionLab.Machine.Core.Layouts;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Core.Sequences;
using OpenVisionLab.MachineStudio.ViewModel;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

[Collection(LayoutStartupTestCollection.Name)]
public sealed class RecipeConnectionRowCatalogTests
{
    [Fact]
    public void BuildRows_UsesActiveLayoutAndPreservesZIndexOrder()
    {
        OpenVisionLanguageService.Load();
        var project = new MachineProjectDocument { Name = "Row catalog layout selection" };
        project.Axes.Add(new VirtualAxisDefinition { Id = "axis-first", Name = "First axis" });
        project.Axes.Add(new VirtualAxisDefinition { Id = "axis-second", Name = "Second axis" });

        project.Layouts.Add(CreateLayout(
            "inactive",
            new LayoutComponentDefinition
            {
                Id = "inactive-frame",
                Name = "Inactive frame",
                Kind = LayoutComponentKind.MachineFrame
            }));

        project.Layouts.Add(CreateLayout(
            "active",
            new LayoutComponentDefinition
            {
                Id = "second-stage",
                Name = "Second stage",
                Kind = LayoutComponentKind.LinearStage,
                BehaviorBindingId = "axis-second",
                ZIndex = 20
            },
            new LayoutComponentDefinition
            {
                Id = "first-stage",
                Name = "First stage",
                Kind = LayoutComponentKind.LinearStage,
                BehaviorBindingId = "axis-first",
                ZIndex = 10
            }));
        project.Simulation.ActiveLayoutId = "active";

        var validation = new MachineProjectLayoutValidator().Validate(project);
        Assert.True(validation.IsValid, string.Join(" | ", validation.Errors.Select(error => error.Message)));

        IReadOnlyList<RecipeConnectionRowViewModel> rows = new RecipeConnectionRowCatalog()
            .BuildRows(project, validation, canEditSequenceStructure: true);

        Assert.Equal(new[] { "first-stage", "second-stage" }, rows.Select(row => row.ComponentId));
        Assert.DoesNotContain(rows, row => row.ComponentId == "inactive-frame");
        Assert.Equal("axis-first", rows[0].SequenceTargetId);
        Assert.Contains("axis-first", rows[0].RelatedTargetIds);
        Assert.True(rows[0].IsConnected);
    }

    [Fact]
    public void BuildRows_ProjectsSequenceUseAndEditabilityFromTheInputSnapshot()
    {
        OpenVisionLanguageService.Load();
        var project = new MachineProjectDocument { Name = "Row catalog sequence use" };
        project.Axes.Add(new VirtualAxisDefinition { Id = "used-axis", Name = "Used axis" });
        project.Axes.Add(new VirtualAxisDefinition { Id = "unused-axis", Name = "Unused axis" });
        project.Layouts.Add(CreateLayout(
            "active",
            new LayoutComponentDefinition
            {
                Id = "used-stage",
                Name = "Used stage",
                Kind = LayoutComponentKind.LinearStage,
                BehaviorBindingId = "used-axis"
            },
            new LayoutComponentDefinition
            {
                Id = "unused-stage",
                Name = "Unused stage",
                Kind = LayoutComponentKind.LinearStage,
                BehaviorBindingId = "unused-axis"
            }));
        project.Simulation.ActiveLayoutId = "active";
        project.Sequences.Add(new SequenceDefinition
        {
            Id = "sequence",
            Name = "Transfer",
            Steps =
            [
                new SequenceStepDefinition
                {
                    Id = "move-used-axis",
                    Name = "Move used axis",
                    Action = SequenceStepAction.MoveAxis,
                    TargetId = "used-axis"
                }
            ]
        });

        var validation = new MachineProjectLayoutValidator().Validate(project);
        Assert.True(validation.IsValid, string.Join(" | ", validation.Errors.Select(error => error.Message)));
        var catalog = new RecipeConnectionRowCatalog();

        IReadOnlyList<RecipeConnectionRowViewModel> rows = catalog.BuildRows(
            project,
            validation,
            canEditSequenceStructure: true);
        var usedRow = Assert.Single(rows, row => row.ComponentId == "used-stage");
        var unusedRow = Assert.Single(rows, row => row.ComponentId == "unused-stage");

        Assert.Equal(1, usedRow.SequenceUseCount);
        Assert.True(usedRow.HasSequenceUse);
        Assert.Equal("sequence", usedRow.FirstSequenceId);
        Assert.Equal("move-used-axis", usedRow.FirstSequenceStepId);
        Assert.Equal(SequenceStepAction.MoveAxis, usedRow.FirstSequenceAction);
        Assert.False(usedRow.CanAddSequenceStep);
        Assert.Equal(0, unusedRow.SequenceUseCount);
        Assert.True(unusedRow.CanAddSequenceStep);

        var readOnlyUnusedRow = Assert.Single(
            catalog.BuildRows(project, validation, canEditSequenceStructure: false),
            row => row.ComponentId == "unused-stage");
        Assert.False(readOnlyUnusedRow.CanAddSequenceStep);
    }

    [Fact]
    public void BuildRows_ProjectsValidationErrorsOntoTheMatchingComponent()
    {
        OpenVisionLanguageService.Load();
        var project = new MachineProjectDocument { Name = "Row catalog validation" };
        project.Layouts.Add(CreateLayout(
            "active",
            new LayoutComponentDefinition
            {
                Id = "invalid-stage",
                Name = "Invalid stage",
                Kind = LayoutComponentKind.LinearStage
            }));
        project.Simulation.ActiveLayoutId = "active";

        var validation = new MachineProjectLayoutValidator().Validate(project);
        var validationError = Assert.Single(validation.Errors);
        Assert.Equal(MachineProjectLayoutValidationErrorCode.MissingBehaviorBinding, validationError.Code);

        var row = Assert.Single(new RecipeConnectionRowCatalog().BuildRows(
            project,
            validation,
            canEditSequenceStructure: true));

        Assert.Equal("invalid-stage", row.ComponentId);
        Assert.False(row.IsValid);
        Assert.False(row.IsConnected);
        Assert.Equal(validationError.Message, row.ValidationText);
    }

    [Fact]
    public void BuildRows_UsesSingleLayoutFallbackAndRejectsAmbiguousFallback()
    {
        OpenVisionLanguageService.Load();
        var project = new MachineProjectDocument { Name = "Row catalog fallback" };
        project.Layouts.Add(CreateLayout(
            "first",
            new LayoutComponentDefinition
            {
                Id = "first-frame",
                Name = "First frame",
                Kind = LayoutComponentKind.MachineFrame
            }));
        project.Layouts.Add(CreateLayout(
            "second",
            new LayoutComponentDefinition
            {
                Id = "second-frame",
                Name = "Second frame",
                Kind = LayoutComponentKind.MachineFrame
            }));

        var validation = new MachineProjectLayoutValidator().Validate(project);
        Assert.True(validation.IsValid, string.Join(" | ", validation.Errors.Select(error => error.Message)));
        var catalog = new RecipeConnectionRowCatalog();

        Assert.Empty(catalog.BuildRows(project, validation, canEditSequenceStructure: true));

        project.Layouts.RemoveAt(1);
        var rows = catalog.BuildRows(project, validation, canEditSequenceStructure: true);
        Assert.Equal(new[] { "first-frame" }, rows.Select(row => row.ComponentId));
    }

    private static MachineLayoutDefinition CreateLayout(
        string id,
        params LayoutComponentDefinition[] components)
    {
        var layout = new MachineLayoutDefinition { Id = id, Name = id };
        layout.Components.AddRange(components);
        return layout;
    }
}
