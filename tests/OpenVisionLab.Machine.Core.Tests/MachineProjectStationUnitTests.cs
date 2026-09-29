using OpenVisionLab.Machine.Core.Layouts;
using OpenVisionLab.Machine.Core.Projects;
using Xunit;

namespace OpenVisionLab.Machine.Core.Tests;

public sealed class MachineProjectStationUnitTests
{
    [Fact]
    public void SaveAndLoad_PreservesStationUnitMembershipWithoutChangingComponentCoordinates()
    {
        var project = new MachineProjectDocument
        {
            Name = "Station and unit sample",
            Stations =
            [
                new MachineStationDefinition
                {
                    Id = "station-assembly",
                    Name = "Assembly",
                    Units =
                    [
                        new MachineUnitDefinition { Id = "unit-press", Name = "Press" }
                    ]
                }
            ],
            Layouts =
            [
                new MachineLayoutDefinition
                {
                    Id = "machine-layout",
                    Name = "Machine layout",
                    Components =
                    [
                        new LayoutComponentDefinition
                        {
                            Id = "press-frame",
                            Name = "Press frame",
                            Kind = LayoutComponentKind.MachineFrame,
                            Transform = new Transform2D { X = 12, Y = 34 },
                            Size = new Size2D { Width = 80, Height = 60 },
                            UnitId = "unit-press"
                        },
                        new LayoutComponentDefinition
                        {
                            Id = "unassigned-frame",
                            Name = "Unassigned frame",
                            Kind = LayoutComponentKind.MachineFrame,
                            Transform = new Transform2D { X = 90, Y = 120 },
                            Size = new Size2D { Width = 20, Height = 18 }
                        }
                    ]
                }
            ]
        };

        var store = new ProjectDocumentStore();
        var saved = store.Save(project);
        var loaded = store.Load(saved);

        Assert.Equal(MachineProjectDocument.CurrentSchema, loaded.Schema);
        Assert.Equal("station-assembly", Assert.Single(loaded.Stations).Id);
        Assert.Equal("unit-press", Assert.Single(Assert.Single(loaded.Stations).Units).Id);
        Assert.Equal("unit-press", loaded.Layouts[0].Components[0].UnitId);
        Assert.Null(loaded.Layouts[0].Components[1].UnitId);
        Assert.Equal(12, loaded.Layouts[0].Components[0].Transform.X);
        Assert.Equal(34, loaded.Layouts[0].Components[0].Transform.Y);
        Assert.Equal(80, loaded.Layouts[0].Components[0].Size.Width);
        Assert.True(new MachineProjectLayoutValidator().Validate(loaded).IsValid);
    }

    [Fact]
    public void Load_LegacyDocumentWithoutStationsKeepsComponentsUnassigned()
    {
        const string json = """
            {
              "schema": "1.13",
              "id": "legacy-project",
              "name": "Legacy project",
              "layouts": [
                {
                  "id": "legacy-layout",
                  "name": "Legacy layout",
                  "components": [
                    {
                      "id": "legacy-frame",
                      "name": "Legacy frame",
                      "kind": "MachineFrame",
                      "transform": { "x": 5, "y": 8, "rotationDegrees": 0 },
                      "size": { "width": 24, "height": 20 },
                      "zIndex": 0
                    }
                  ]
                }
              ]
            }
            """;

        var store = new ProjectDocumentStore();
        var loaded = store.Load(json);

        Assert.Empty(loaded.Stations);
        Assert.Null(Assert.Single(loaded.Layouts[0].Components).UnitId);
        Assert.True(new MachineProjectLayoutValidator().Validate(loaded).IsValid);

        var reopened = store.Load(store.Save(loaded));
        Assert.Equal(MachineProjectDocument.CurrentSchema, reopened.Schema);
        Assert.Empty(reopened.Stations);
        Assert.Null(Assert.Single(reopened.Layouts[0].Components).UnitId);
    }

    [Fact]
    public void Validate_RejectsDuplicateIdsAndUnknownOrEmptyComponentUnitIds()
    {
        var project = new MachineProjectDocument
        {
            Stations =
            [
                new MachineStationDefinition
                {
                    Id = "station-1",
                    Name = "Station 1",
                    Units =
                    [
                        new MachineUnitDefinition { Id = "unit-1", Name = "Unit 1" }
                    ]
                },
                new MachineStationDefinition
                {
                    Id = "station-1",
                    Name = "Duplicate station",
                    Units =
                    [
                        new MachineUnitDefinition { Id = "unit-1", Name = "Duplicate unit" }
                    ]
                }
            ],
            Layouts =
            [
                new MachineLayoutDefinition
                {
                    Id = "layout-1",
                    Name = "Layout 1",
                    Components =
                    [
                        CreateComponent("unknown-unit", "missing-unit"),
                        CreateComponent("empty-unit", " ")
                    ]
                }
            ]
        };

        var result = new MachineProjectLayoutValidator().Validate(project);
        var codes = result.Errors.Select(error => error.Code).ToHashSet();

        Assert.Contains(MachineProjectLayoutValidationErrorCode.DuplicateStationId, codes);
        Assert.Contains(MachineProjectLayoutValidationErrorCode.DuplicateUnitId, codes);
        Assert.Contains(MachineProjectLayoutValidationErrorCode.ComponentUnitNotFound, codes);
        Assert.Contains(MachineProjectLayoutValidationErrorCode.InvalidComponentUnitId, codes);
    }

    [Fact]
    public void BundledLargeLayoutSample_ContainsTwentyUnitsAndFiveHundredAssignedComponents()
    {
        var samplePath = Path.Combine(AppContext.BaseDirectory, "LargeLayoutExploration.ovmachine");
        var project = new ProjectDocumentStore().Load(File.ReadAllText(samplePath));
        var components = Assert.Single(project.Layouts).Components;

        Assert.Equal(2, project.Stations.Count);
        Assert.Equal(20, project.Stations.Sum(station => station.Units.Count));
        Assert.Equal(500, components.Count);
        Assert.All(components, component => Assert.False(string.IsNullOrWhiteSpace(component.UnitId)));
        Assert.All(components.GroupBy(component => component.UnitId), group => Assert.Equal(25, group.Count()));
        Assert.Empty(project.Sequences);
        Assert.True(new MachineProjectLayoutValidator().Validate(project).IsValid);

        var expectedUnits = project.Stations
            .SelectMany(station => station.Units.Select(unit => (StationId: station.Id, StationName: station.Name, UnitId: unit.Id, UnitName: unit.Name)))
            .ToArray();
        var expectedAssignments = components.Select(component => (component.Id, component.UnitId)).ToArray();
        var store = new ProjectDocumentStore();
        var restored = store.Load(store.Save(project));

        Assert.Equal(expectedUnits, restored.Stations
            .SelectMany(station => station.Units.Select(unit => (StationId: station.Id, StationName: station.Name, UnitId: unit.Id, UnitName: unit.Name)))
            .ToArray());
        Assert.Equal(expectedAssignments, Assert.Single(restored.Layouts).Components.Select(component => (component.Id, component.UnitId)).ToArray());
        Assert.True(new MachineProjectLayoutValidator().Validate(restored).IsValid);
    }

    private static LayoutComponentDefinition CreateComponent(string id, string unitId) => new()
    {
        Id = id,
        Name = id,
        Kind = LayoutComponentKind.MachineFrame,
        UnitId = unitId,
        Transform = new Transform2D(),
        Size = new Size2D { Width = 10, Height = 10 }
    };

}
