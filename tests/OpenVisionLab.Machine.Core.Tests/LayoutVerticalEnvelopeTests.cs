using OpenVisionLab.Machine.Core.Layouts;
using OpenVisionLab.Machine.Core.Projects;
using Xunit;

namespace OpenVisionLab.Machine.Core.Tests;

public sealed class LayoutVerticalEnvelopeTests
{
    [Fact]
    public void LegacyProjectKeepsVerticalEnvelopeUnsetWhenExplicitlySavedAndReopened()
    {
        const string legacyJson = """
            {"schema":"1.12","layouts":[{"id":"layout","name":"Layout","components":[{"id":"frame","name":"Frame","kind":"MachineFrame","transform":{"x":12,"y":24,"rotationDegrees":15},"size":{"width":100,"height":80},"zIndex":0}]}]}
            """;
        var store = new ProjectDocumentStore();

        var loaded = store.Load(legacyJson);
        var component = Assert.Single(Assert.Single(loaded.Layouts).Components);
        var saved = store.SerializeForSave(loaded, DateTimeOffset.UnixEpoch);
        var reopened = store.Load(saved);

        Assert.Equal(MachineProjectDocument.CurrentSchema, reopened.Schema);
        Assert.Null(component.VerticalEnvelope);
        Assert.DoesNotContain("verticalEnvelope", saved, StringComparison.Ordinal);
        Assert.Null(Assert.Single(Assert.Single(reopened.Layouts).Components).VerticalEnvelope);
        Assert.Equal(12, component.Transform.X);
        Assert.Equal(24, component.Transform.Y);
        Assert.Equal(15, component.Transform.RotationDegrees);
        Assert.Equal(100, component.Size.Width);
        Assert.Equal(80, component.Size.Height);
    }

    [Fact]
    public void ExplicitVerticalEnvelopeRoundTripsWithProjectDocument()
    {
        var component = new LayoutComponentDefinition
        {
            Id = "frame",
            Name = "Frame",
            Kind = LayoutComponentKind.MachineFrame,
            VerticalEnvelope = new LayoutVerticalEnvelope { BaseElevation = 12.5, Height = 64 }
        };
        var document = new MachineProjectDocument();
        document.Layouts.Add(new MachineLayoutDefinition
        {
            Id = "layout",
            Name = "Layout",
            Components = { component }
        });
        var store = new ProjectDocumentStore();

        var reopened = store.Load(store.SerializeForSave(document, DateTimeOffset.UnixEpoch));

        var envelope = Assert.IsType<LayoutVerticalEnvelope>(
            Assert.Single(Assert.Single(reopened.Layouts).Components).VerticalEnvelope);
        Assert.Equal(12.5, envelope.BaseElevation);
        Assert.Equal(64, envelope.Height);
    }

    [Theory]
    [InlineData(double.NaN, 10)]
    [InlineData(double.PositiveInfinity, 10)]
    [InlineData(0, 0)]
    [InlineData(0, double.NegativeInfinity)]
    public void ValidatorRejectsInvalidExplicitVerticalEnvelope(double baseElevation, double height)
    {
        var project = new MachineProjectDocument();
        project.Layouts.Add(new MachineLayoutDefinition
        {
            Id = "layout",
            Name = "Layout",
            Components =
            {
                new LayoutComponentDefinition
                {
                    Id = "frame",
                    Name = "Frame",
                    Kind = LayoutComponentKind.MachineFrame,
                    VerticalEnvelope = new LayoutVerticalEnvelope
                    {
                        BaseElevation = baseElevation,
                        Height = height
                    }
                }
            }
        });

        var result = new MachineProjectLayoutValidator().Validate(project);

        Assert.Contains(result.Errors, error => error.Code == MachineProjectLayoutValidationErrorCode.InvalidVerticalEnvelope);
    }

    [Fact]
    public void SchematicHeightUsesFootprintScaleWithoutClaimingPhysicalUnits()
    {
        Assert.Equal(32d, LayoutVerticalEnvelope.GetSchematicHeight(100, 80));
        Assert.Equal(8d, LayoutVerticalEnvelope.GetSchematicHeight(10, 12));
    }
}
