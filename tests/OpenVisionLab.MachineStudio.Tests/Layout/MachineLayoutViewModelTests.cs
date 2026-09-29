using OpenVisionLab.Machine.Core.Axes;
using OpenVisionLab.Machine.Core.Layouts;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Core.Sequences;
using OpenVisionLab.MachineStudio.ViewModel;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

public sealed class MachineLayoutViewModelTests
{
    [Fact]
    public void EquipmentScopeContextTracksStationAndUnitWithoutChangingProject()
    {
        var unit = new MachineUnitDefinition { Id = "unit-inspection", Name = "Inspection Unit" };
        var station = new MachineStationDefinition { Id = "station-inspection", Name = "Inspection Station", Units = [unit] };
        var definition = new MachineLayoutDefinition { Id = "layout-main", Name = "Main" };
        var project = new MachineProjectDocument { Stations = [station], Layouts = [definition] };
        project.Simulation.ActiveLayoutId = definition.Id;
        using var layout = new MachineLayoutViewModel();
        Assert.Equal(string.Empty, layout.EquipmentScopeContextText);
        layout.Load(project);
        var wholeMachineContext = layout.EquipmentScopeContextText;
        var serializedBefore = new ProjectDocumentStore().Serialize(project);
        var changedProperties = new List<string?>();
        layout.PropertyChanged += (_, args) => changedProperties.Add(args.PropertyName);

        layout.ShowUnit(unit.Id);

        Assert.Equal("Inspection Station / Inspection Unit", layout.EquipmentScopeContextText);
        Assert.Contains(nameof(MachineLayoutViewModel.EquipmentScopeContextText), changedProperties);
        Assert.Equal(serializedBefore, new ProjectDocumentStore().Serialize(project));

        layout.ShowOverview();

        Assert.Equal(wholeMachineContext, layout.EquipmentScopeContextText);
        Assert.Equal(serializedBefore, new ProjectDocumentStore().Serialize(project));
    }

    [Fact]
    public void EquipmentOutlineUnitLabelsDisambiguateDuplicateNamesByStableId()
    {
        var first = new MachineUnitDefinition { Id = "unit-camera-a", Name = "Inspection Unit" };
        var second = new MachineUnitDefinition { Id = "unit-camera-b", Name = "Inspection Unit" };
        var unique = new MachineUnitDefinition { Id = "unit-load", Name = "Load Unit" };
        var definition = new MachineLayoutDefinition { Id = "layout-main", Name = "Main" };
        definition.Components.Add(new LayoutComponentDefinition
        {
            Id = "unassigned-frame", Name = "Unassigned frame", Kind = LayoutComponentKind.MachineFrame,
            Transform = new Transform2D(), Size = new Size2D { Width = 20, Height = 20 }
        });
        var project = new MachineProjectDocument
        {
            Stations = [new MachineStationDefinition
            {
                Id = "station-inspection", Name = "Inspection",
                Units = [first, second, unique]
            }],
            Layouts = [definition]
        };
        project.Simulation.ActiveLayoutId = definition.Id;
        using var layout = new MachineLayoutViewModel();
        layout.Load(project);

        Assert.Equal(
            ["Inspection Unit / unit-camera-a", "Inspection Unit / unit-camera-b", "Load Unit"],
            layout.EquipmentOutlineUnits.Where(unit => unit.UnitId is not null).Select(unit => unit.DisplayName));
        Assert.All(layout.EquipmentOutlineUnits.Where(unit => unit.UnitId is not null), unit =>
            Assert.Equal(unit.Name == "Inspection Unit", unit.ShowUnitId));
        var unassigned = Assert.Single(layout.EquipmentOutlineUnits, unit => unit.UnitId is null);
        Assert.False(unassigned.ShowUnitId);
    }

    [Fact]
    public void EquipmentOutlinePartAutomationNamesIncludeLocalizedIdentityAndSelectionAction()
    {
        var originalLanguage = OpenVisionLanguageService.CurrentLanguage;
        try
        {
            OpenVisionLanguageService.SetLanguage(OpenVisionLanguage.Korean, save: false);
            var unit = new MachineUnitDefinition { Id = "unit-inspection", Name = "검사 유닛" };
            var definition = new MachineLayoutDefinition { Id = "layout-main", Name = "Main" };
            definition.Components.Add(new LayoutComponentDefinition
            {
                Id = "camera-01", Name = "검사 카메라", Kind = LayoutComponentKind.Camera,
                UnitId = unit.Id, Transform = new Transform2D(), Size = new Size2D { Width = 20, Height = 20 }
            });
            var project = new MachineProjectDocument
            {
                Stations = [new MachineStationDefinition { Id = "station-main", Name = "Main", Units = [unit] }],
                Layouts = [definition]
            };
            project.Simulation.ActiveLayoutId = definition.Id;
            using var layout = new MachineLayoutViewModel();
            layout.Load(project);

            var koreanPart = Assert.Single(layout.EquipmentOutlineUnits.SelectMany(item => item.Parts));
            Assert.Equal("검사 카메라 / camera-01 / 카메라", koreanPart.AutomationName);
            Assert.Equal("camera-01 선택", koreanPart.SelectionAutomationName);

            OpenVisionLanguageService.SetLanguage(OpenVisionLanguage.English, save: false);
            layout.RefreshLocalization();

            var englishPart = Assert.Single(layout.EquipmentOutlineUnits.SelectMany(item => item.Parts));
            Assert.Equal("검사 카메라 / camera-01 / Camera", englishPart.AutomationName);
            Assert.Equal("Select camera-01", englishPart.SelectionAutomationName);
        }
        finally
        {
            OpenVisionLanguageService.SetLanguage(originalLanguage, save: false);
        }
    }

    [Fact]
    public void EquipmentOutlineSearchPagesAndSelectsExistingPartsWithoutEditingProject()
    {
        var press = new MachineUnitDefinition { Id = "unit-press", Name = "Press unit" };
        var empty = new MachineUnitDefinition { Id = "unit-empty", Name = "Empty unit" };
        var secondary = new MachineUnitDefinition { Id = "unit-secondary", Name = "Secondary unit" };
        var definition = new MachineLayoutDefinition { Id = "layout-main", Name = "Main" };
        for (var index = 0; index < 61; index++)
        {
            definition.Components.Add(new LayoutComponentDefinition
            {
                Id = $"frame-{index}", Name = $"Frame {index}", Kind = LayoutComponentKind.MachineFrame,
                UnitId = press.Id, Transform = new Transform2D(), Size = new Size2D { Width = 20, Height = 20 }
            });
        }

        var project = new MachineProjectDocument
        {
            Stations =
            [
                new MachineStationDefinition { Id = "station-main", Name = "Main station", Units = [press, empty] },
                new MachineStationDefinition { Id = "station-secondary", Name = "Secondary station", Units = [secondary] }
            ],
            Layouts = [definition]
        };
        project.Simulation.ActiveLayoutId = definition.Id;
        var serializedBefore = new ProjectDocumentStore().Serialize(project);
        using var layout = new MachineLayoutViewModel();
        layout.Load(project);

        Assert.Equal(61, layout.Items.Count);
        Assert.Equal(60, layout.EquipmentOutlineUnits.SelectMany(unit => unit.Parts).Count());
        Assert.True(layout.EquipmentOutlineHasPages);
        Assert.True(layout.NextEquipmentOutlinePageCommand.CanExecute(null));
        Assert.Contains(layout.EquipmentOutlineUnits, unit => unit.UnitId == empty.Id && unit.Parts.Count == 0);
        Assert.Equal(new[] { "Main station", "Main station", "Secondary station" },
            layout.EquipmentOutlineUnits.Select(unit => unit.StationName));
        Assert.Equal(new[] { true, false, true }, layout.EquipmentOutlineUnits.Select(unit => unit.ShowStationHeader));
        Assert.Equal(new[] { 61, 61, 0 }, layout.EquipmentOutlineUnits.Select(unit => unit.StationComponentCount));

        layout.NextEquipmentOutlinePageCommand.Execute(null);
        var lastPart = Assert.Single(layout.EquipmentOutlineUnits.SelectMany(unit => unit.Parts));
        Assert.False(layout.NextEquipmentOutlinePageCommand.CanExecute(null));

        layout.SelectEquipmentOutlinePartCommand.Execute(lastPart.Item);
        Assert.Equal(press.Id, layout.ActiveUnitId);
        Assert.Equal(lastPart.Item.Id, layout.SelectedItem?.Id);
        Assert.Equal(1, layout.SelectionCount);

        layout.EquipmentOutlineSearchText = "frame-1";
        Assert.Equal(1, layout.EquipmentOutlineUnits.SelectMany(unit => unit.Parts).Count(part => part.Item.Id == "frame-1"));
        Assert.False(layout.EquipmentOutlineHasPages);
        layout.EquipmentOutlineSearchText = "press unit";
        Assert.Equal(60, layout.EquipmentOutlineUnits.SelectMany(unit => unit.Parts).Count());
        Assert.True(layout.EquipmentOutlineHasPages);
        layout.EquipmentOutlineSearchText = "no such part";
        Assert.True(layout.EquipmentOutlineHasNoMatches);
        Assert.Empty(layout.EquipmentOutlineUnits);
        Assert.True(layout.ClearEquipmentOutlineSearchCommand.CanExecute(null));
        layout.ClearEquipmentOutlineSearchCommand.Execute(null);
        Assert.Equal(string.Empty, layout.EquipmentOutlineSearchText);
        Assert.Equal(60, layout.EquipmentOutlineUnits.SelectMany(unit => unit.Parts).Count());
        Assert.Equal("1 / 2", layout.EquipmentOutlinePageText);
        Assert.Same(lastPart.Item, layout.SelectedItem);
        Assert.Equal(1, layout.SelectionCount);
        Assert.False(layout.ClearEquipmentOutlineSearchCommand.CanExecute(null));
        layout.EquipmentOutlineSearchText = "  ";
        Assert.False(layout.EquipmentOutlineHasNoMatches);
        Assert.True(layout.ClearEquipmentOutlineSearchCommand.CanExecute(null));

        var firstPart = layout.EquipmentOutlineUnits.SelectMany(unit => unit.Parts).First().Item;
        layout.ToggleEquipmentOutlinePartCommand.Execute(firstPart);
        Assert.Equal(2, layout.SelectionCount);
        layout.ToggleEquipmentOutlinePartCommand.Execute(firstPart);
        Assert.Equal(1, layout.SelectionCount);
        Assert.Equal(serializedBefore, new ProjectDocumentStore().Serialize(project));
    }

    [Fact]
    public void EquipmentOutlineSearchMatchesR19AxisTypeId()
    {
        var unit = new MachineUnitDefinition { Id = "unit-motion", Name = "Motion unit" };
        var definition = new MachineLayoutDefinition { Id = "layout-main", Name = "Main" };
        definition.Components.Add(new LayoutComponentDefinition
        {
            Id = "stage-main", Name = "Transfer stage", Kind = LayoutComponentKind.LinearStage,
            UnitId = unit.Id, Transform = new Transform2D(), Size = new Size2D { Width = 20, Height = 20 }
        });
        var project = new MachineProjectDocument
        {
            Stations = [new MachineStationDefinition { Id = "station-main", Name = "Main station", Units = [unit] }],
            Layouts = [definition]
        };
        project.Simulation.ActiveLayoutId = definition.Id;
        var serializedBefore = new ProjectDocumentStore().Serialize(project);
        using var layout = new MachineLayoutViewModel();
        layout.Load(project);

        layout.EquipmentOutlineSearchText = "axis";

        var result = Assert.Single(layout.EquipmentOutlineUnits.SelectMany(item => item.Parts));
        Assert.Equal("stage-main", result.Item.Id);
        Assert.False(layout.EquipmentOutlineHasNoMatches);
        Assert.Equal(serializedBefore, new ProjectDocumentStore().Serialize(project));
    }

    [Fact]
    public void ComponentLibrarySearchFiltersKindAndLocalizedContentWithoutChangingTheLibrary()
    {
        using var layout = new MachineLayoutViewModel();
        var originalItems = layout.LibraryItems.ToArray();

        layout.LibrarySearchText = "camera";

        var camera = Assert.Single(layout.FilteredLibraryItems);
        Assert.Equal(LayoutComponentKind.Camera, camera.Kind);
        Assert.False(layout.HasNoLibrarySearchResults);

        layout.LibrarySearchText = "no-such-component";

        Assert.Empty(layout.FilteredLibraryItems);
        Assert.True(layout.HasNoLibrarySearchResults);
        Assert.True(layout.ClearLibrarySearchCommand.CanExecute(null));

        layout.ClearLibrarySearchCommand.Execute(null);

        Assert.Equal(string.Empty, layout.LibrarySearchText);
        Assert.Equal(originalItems, layout.FilteredLibraryItems);
        Assert.False(layout.ClearLibrarySearchCommand.CanExecute(null));

        layout.LibrarySearchText = "  ";

        Assert.Equal(originalItems, layout.FilteredLibraryItems);
        Assert.Equal(originalItems, layout.LibraryItems);
        Assert.True(layout.ClearLibrarySearchCommand.CanExecute(null));
    }

    [Fact]
    public void LargeOverviewStartsAtOneHundredOneAndSummarizesUnitComponentsAndReferencedActions()
    {
        var press = new MachineUnitDefinition { Id = "unit-press", Name = "Press" };
        var inspection = new MachineUnitDefinition { Id = "unit-inspection", Name = "Inspection" };
        var empty = new MachineUnitDefinition { Id = "unit-empty", Name = "Empty" };
        var definition = new MachineLayoutDefinition { Id = "layout-main", Name = "Main" };
        for (var index = 0; index < 100; index++)
        {
            definition.Components.Add(new LayoutComponentDefinition
            {
                Id = $"press-{index}",
                Name = $"Press {index}",
                Kind = LayoutComponentKind.MachineFrame,
                UnitId = press.Id,
                BehaviorBindingId = index == 0 ? "axis-press" : null,
                Transform = new Transform2D(),
                Size = new Size2D { Width = 20, Height = 20 }
            });
        }

        var project = new MachineProjectDocument
        {
            Stations =
            [
                new MachineStationDefinition { Id = "station-main", Name = "Main station", Units = [press, inspection, empty] }
            ],
            Layouts = [definition],
            Sequences =
            [
                new SequenceDefinition
                {
                    Id = "sequence-main",
                    Name = "Main",
                    Steps =
                    [
                        new SequenceStepDefinition { Id = "target", TargetId = "press-0" },
                        new SequenceStepDefinition { Id = "binding", ExpectedTargetId = "axis-press" },
                        new SequenceStepDefinition { Id = "inspection", WorkpieceComponentId = "inspection-0" },
                        new SequenceStepDefinition { Id = "unrelated", TargetId = "other" }
                    ]
                }
            ]
        };
        project.Simulation.ActiveLayoutId = definition.Id;

        using var layout = new MachineLayoutViewModel();
        layout.Load(project);

        Assert.False(layout.IsLargeOverview);

        definition.Components.Add(new LayoutComponentDefinition
        {
            Id = "inspection-0",
            Name = "Inspection 0",
            Kind = LayoutComponentKind.Camera,
            UnitId = inspection.Id,
            Transform = new Transform2D(),
            Size = new Size2D { Width = 20, Height = 20 }
        });
        layout.Load(project);
        var serializedBeforeNavigation = new ProjectDocumentStore().Serialize(project);

        Assert.True(layout.IsLargeOverview);
        Assert.Collection(
            layout.UnitOverviewItems,
            item =>
            {
                Assert.Equal(press.Id, item.UnitId);
                Assert.Equal(100, item.ComponentCount);
                Assert.Equal(2, item.ActionCount);
            },
            item =>
            {
                Assert.Equal(inspection.Id, item.UnitId);
                Assert.Equal(1, item.ComponentCount);
                Assert.Equal(1, item.ActionCount);
            },
            item =>
            {
                Assert.Equal(empty.Id, item.UnitId);
                Assert.Equal(0, item.ComponentCount);
                Assert.Equal(0, item.ActionCount);
            });

        layout.ShowUnit(empty.Id);
        Assert.False(layout.IsLargeOverview);
        Assert.Empty(layout.SceneItems);
        layout.ShowOverview();
        Assert.True(layout.IsLargeOverview);
        Assert.Equal(serializedBeforeNavigation, new ProjectDocumentStore().Serialize(project));
    }

    [Fact]
    public void SelectedEditorAssignsReassignsAndClearsExistingUnitsWithoutDuplicateChanges()
    {
        var component = new LayoutComponentDefinition
        {
            Id = "frame-1",
            Name = "Frame",
            Kind = LayoutComponentKind.MachineFrame,
            Transform = new Transform2D { X = 20, Y = 30 },
            Size = new Size2D { Width = 100, Height = 80 }
        };
        var definition = new MachineLayoutDefinition
        {
            Id = "main-cell",
            Name = "Main Cell",
            Components = { component }
        };
        var project = new MachineProjectDocument();
        project.Stations.Add(new MachineStationDefinition
        {
            Id = "station-assembly",
            Name = "Assembly",
            Units = { new MachineUnitDefinition { Id = "unit-press", Name = "Press" } }
        });
        project.Stations.Add(new MachineStationDefinition
        {
            Id = "station-inspection",
            Name = "Inspection",
            Units = { new MachineUnitDefinition { Id = "unit-camera", Name = "Camera" } }
        });
        project.Layouts.Add(definition);
        project.Simulation.ActiveLayoutId = definition.Id;

        using var layout = new MachineLayoutViewModel();
        layout.Load(project);
        layout.Select(component.Id);

        var editor = Assert.IsType<LayoutComponentEditorViewModel>(layout.SelectedComponentEditor);
        var definitionChangedCount = 0;
        layout.DefinitionChanged += (_, _) => definitionChangedCount++;

        Assert.True(editor.HasUnitAssignmentOptions);
        Assert.Equal(string.Empty, editor.UnitId);
        Assert.Collection(
            editor.UnitOptions,
            option => Assert.Equal(string.Empty, option.Id),
            option => Assert.Equal("unit-press", option.Id),
            option => Assert.Equal("unit-camera", option.Id));
        Assert.Equal("Press / unit-press", editor.UnitOptions[1].DisplayName);
        Assert.Equal("Camera / unit-camera", editor.UnitOptions[2].DisplayName);

        editor.UnitId = "unit-press";
        Assert.Equal("unit-press", component.UnitId);
        Assert.Equal(1, definitionChangedCount);
        editor.UnitId = "unit-press";
        Assert.Equal(1, definitionChangedCount);

        editor.UnitId = "unit-camera";
        Assert.Equal("unit-camera", component.UnitId);
        Assert.Equal(2, definitionChangedCount);
        var reopened = new ProjectDocumentStore().Load(new ProjectDocumentStore().Serialize(project));
        Assert.Equal("unit-camera", Assert.Single(reopened.Layouts).Components[0].UnitId);

        editor.UnitId = string.Empty;
        Assert.Null(component.UnitId);
        Assert.Equal(3, definitionChangedCount);
        Assert.Equal(20d, component.Transform.X);
        Assert.Equal(30d, component.Transform.Y);
    }

    [Fact]
    public void SelectedEditorHasNoUnitAssignmentChoiceWhenProjectHasNoUnits()
    {
        var component = new LayoutComponentDefinition
        {
            Id = "frame-1",
            Name = "Frame",
            Kind = LayoutComponentKind.MachineFrame,
            Transform = new Transform2D(),
            Size = new Size2D { Width = 100, Height = 80 }
        };
        var definition = new MachineLayoutDefinition
        {
            Id = "main-cell",
            Name = "Main Cell",
            Components = { component }
        };
        var project = new MachineProjectDocument();
        project.Layouts.Add(definition);
        project.Simulation.ActiveLayoutId = definition.Id;

        using var layout = new MachineLayoutViewModel();
        layout.Load(project);
        layout.Select(component.Id);

        var editor = Assert.IsType<LayoutComponentEditorViewModel>(layout.SelectedComponentEditor);
        Assert.False(editor.HasUnitAssignmentOptions);
        Assert.Equal(string.Empty, editor.UnitId);
        Assert.Single(editor.UnitOptions);
    }

    [Fact]
    public void InspectorUsesFirstSelectedPartInR19SelectionOrderRegardlessOfSceneOrder()
    {
        var first = new LayoutComponentDefinition
        {
            Id = "first", Name = "First", Kind = LayoutComponentKind.MachineFrame,
            Transform = new Transform2D(), Size = new Size2D { Width = 100, Height = 80 }, ZIndex = 10
        };
        var second = new LayoutComponentDefinition
        {
            Id = "second", Name = "Second", Kind = LayoutComponentKind.MachineFrame,
            Transform = new Transform2D(), Size = new Size2D { Width = 100, Height = 80 }, ZIndex = 0
        };
        var definition = new MachineLayoutDefinition { Id = "main", Name = "Main", Components = { first, second } };
        var project = new MachineProjectDocument { Layouts = { definition } };
        project.Simulation.ActiveLayoutId = definition.Id;
        using var layout = new MachineLayoutViewModel();
        layout.Load(project);
        var serialized = new ProjectDocumentStore().Serialize(project);
        var firstItem = layout.Items.Single(item => item.Id == first.Id);
        var secondItem = layout.Items.Single(item => item.Id == second.Id);
        Assert.Equal(new[] { second.Id, first.Id }, layout.Items.Select(item => item.Id));

        layout.ExtendSelection(secondItem, toggle: false);
        layout.ExtendSelection(firstItem, toggle: true);
        Assert.Equal(new[] { second.Id, first.Id }, layout.SelectedItems.Select(item => item.Id));
        Assert.Equal(second.Id, layout.SelectedComponentEditor?.Id);
        Assert.Same(firstItem, layout.SelectedItem);

        layout.SelectMany([second.Id, first.Id], second.Id);
        Assert.Equal(second.Id, layout.SelectedComponentEditor?.Id);
        Assert.Same(secondItem, layout.SelectedItem);

        layout.ExtendSelection(secondItem, toggle: true);
        Assert.Equal([first.Id], layout.SelectedItems.Select(item => item.Id));
        Assert.Equal(first.Id, layout.SelectedComponentEditor?.Id);
        Assert.Same(firstItem, layout.SelectedItem);
        layout.ExtendSelection(firstItem, toggle: true);
        Assert.Empty(layout.SelectedItems);
        Assert.Null(layout.SelectedComponentEditor);
        Assert.Null(layout.SelectedItem);
        Assert.Equal(serialized, new ProjectDocumentStore().Serialize(project));
    }

    [Fact]
    public void DisposeDetachesLayoutAndSelectedEditorSubscriptions()
    {
        var component = new LayoutComponentDefinition
        {
            Id = "frame-1",
            Name = "Frame",
            Kind = LayoutComponentKind.MachineFrame,
            Transform = new Transform2D { X = 20, Y = 30 },
            Size = new Size2D { Width = 100, Height = 80 }
        };
        var definition = new MachineLayoutDefinition
        {
            Id = "main-cell",
            Name = "Main Cell",
            Components = { component }
        };
        var project = new MachineProjectDocument();
        project.Layouts.Add(definition);
        project.Simulation.ActiveLayoutId = definition.Id;

        using var layout = new MachineLayoutViewModel();
        layout.Load(project);
        layout.Select(component.Id);

        var item = Assert.Single(layout.Items);
        var editor = Assert.IsType<LayoutComponentEditorViewModel>(layout.SelectedComponentEditor);
        var definitionChangedCount = 0;
        var editorPropertyChangedCount = 0;
        layout.DefinitionChanged += (_, _) => definitionChangedCount++;
        editor.PropertyChanged += (_, _) => editorPropertyChangedCount++;

        layout.Dispose();
        item.CurrentName = "Disposed Frame";

        Assert.Equal("Disposed Frame", item.CurrentName);
        Assert.Null(layout.SelectedComponentEditor);
        Assert.Equal(0, definitionChangedCount);
        Assert.Equal(0, editorPropertyChangedCount);
        Assert.Throws<ObjectDisposedException>(() => layout.Load(project));
        layout.Dispose();
    }

    [Fact]
    public void DisposeRejectsDirectLocalizationRefresh()
    {
        var definition = new MachineLayoutDefinition
        {
            Id = "main-cell",
            Name = "Main Cell"
        };
        var project = new MachineProjectDocument();
        project.Layouts.Add(definition);
        project.Simulation.ActiveLayoutId = definition.Id;

        using var layout = new MachineLayoutViewModel();
        layout.Load(project);
        var changedProperties = new List<string?>();
        layout.PropertyChanged += (_, args) => changedProperties.Add(args.PropertyName);

        layout.Dispose();
        changedProperties.Clear();

        layout.RefreshLocalization();

        Assert.Empty(changedProperties);
    }

    [Fact]
    public void SelectedEditorPersistsVerticalEnvelopeAndViewPreferenceSurvivesReload()
    {
        var component = new LayoutComponentDefinition
        {
            Id = "frame-1",
            Name = "Frame",
            Kind = LayoutComponentKind.MachineFrame,
            Transform = new Transform2D { X = 20, Y = 30 },
            Size = new Size2D { Width = 100, Height = 80 }
        };
        var definition = new MachineLayoutDefinition
        {
            Id = "main-cell",
            Name = "Main Cell",
            Components = { component }
        };
        var project = new MachineProjectDocument();
        project.Layouts.Add(definition);
        project.Simulation.ActiveLayoutId = definition.Id;

        using var layout = new MachineLayoutViewModel();
        layout.Load(project);
        layout.Select(component.Id);
        Assert.True(layout.IsObliqueView);
        Assert.False(layout.IsTopView);
        var initialToggleText = layout.SceneViewToggleText;
        var changedProperties = new List<string?>();
        layout.PropertyChanged += (_, args) => changedProperties.Add(args.PropertyName);

        layout.ShowTopViewCommand.Execute(null);

        Assert.False(layout.IsObliqueView);
        Assert.True(layout.IsTopView);
        Assert.NotEqual(initialToggleText, layout.SceneViewToggleText);
        Assert.Contains(nameof(MachineLayoutViewModel.IsObliqueView), changedProperties);
        Assert.Contains(nameof(MachineLayoutViewModel.IsTopView), changedProperties);
        Assert.Contains(nameof(MachineLayoutViewModel.SceneViewToggleText), changedProperties);

        layout.ShowObliqueViewCommand.Execute(null);

        Assert.True(layout.IsObliqueView);
        Assert.False(layout.IsTopView);

        layout.ToggleSceneViewCommand.Execute(null);

        Assert.False(layout.IsObliqueView);
        Assert.True(layout.IsTopView);

        var editor = Assert.IsType<LayoutComponentEditorViewModel>(layout.SelectedComponentEditor);
        Assert.True(editor.IsVerticalEnvelopeEstimated);
        Assert.Equal(32d, editor.VerticalHeight);
        editor.VerticalBaseElevation = 12;
        editor.VerticalHeight = 48;

        Assert.False(editor.IsVerticalEnvelopeEstimated);
        Assert.Equal(12d, component.VerticalEnvelope?.BaseElevation);
        Assert.Equal(48d, component.VerticalEnvelope?.Height);

        layout.Load(project);

        Assert.False(layout.IsObliqueView);
        Assert.Equal(12d, Assert.IsType<LayoutVerticalEnvelope>(component.VerticalEnvelope).BaseElevation);
    }

    [Fact]
    public void SwitchingSceneProjectionPreservesSelectionAndDoesNotModifyLayoutDefinition()
    {
        var component = new LayoutComponentDefinition
        {
            Id = "conveyor-1",
            Name = "Inspection conveyor",
            Kind = LayoutComponentKind.Conveyor,
            Transform = new Transform2D { X = 24, Y = 36, RotationDegrees = 15 },
            Size = new Size2D { Width = 140, Height = 56 }
        };
        var definition = new MachineLayoutDefinition
        {
            Id = "inspection-cell",
            Name = "Inspection cell",
            Components = { component }
        };
        var project = new MachineProjectDocument();
        project.Layouts.Add(definition);
        project.Simulation.ActiveLayoutId = definition.Id;

        using var layout = new MachineLayoutViewModel();
        layout.Load(project);
        layout.Select(component.Id);

        var selectedItem = layout.SelectedItem;
        Assert.NotNull(selectedItem);
        var selectedEditor = Assert.IsType<LayoutComponentEditorViewModel>(layout.SelectedComponentEditor);
        var definitionChangedCount = 0;
        layout.DefinitionChanged += (_, _) => definitionChangedCount++;

        layout.ShowTopViewCommand.Execute(null);
        layout.ShowTopViewCommand.Execute(null);
        layout.ShowObliqueViewCommand.Execute(null);
        layout.ToggleSceneViewCommand.Execute(null);

        Assert.Same(selectedItem, layout.SelectedItem);
        Assert.Same(selectedEditor, layout.SelectedComponentEditor);
        Assert.Equal(component.Id, selectedEditor.Id);
        Assert.Equal(24d, component.Transform.X);
        Assert.Equal(36d, component.Transform.Y);
        Assert.Equal(15d, component.Transform.RotationDegrees);
        Assert.Equal(140d, component.Size.Width);
        Assert.Equal(56d, component.Size.Height);
        Assert.Equal(0, definitionChangedCount);
    }

    [Fact]
    public void ProjectionPreferenceIsSessionScopedAndNotSerializedWithProject()
    {
        var project = new MachineProjectDocument();
        project.Layouts.Add(new MachineLayoutDefinition { Id = "main", Name = "Main" });
        project.Simulation.ActiveLayoutId = "main";

        using var currentLayout = new MachineLayoutViewModel();
        currentLayout.Load(project);
        currentLayout.ShowTopViewCommand.Execute(null);
        Assert.True(currentLayout.IsTopView);

        var store = new ProjectDocumentStore();
        var reopenedProject = store.Load(store.Serialize(project));
        using var reopenedLayout = new MachineLayoutViewModel();
        reopenedLayout.Load(reopenedProject);

        Assert.True(currentLayout.IsTopView);
        Assert.True(reopenedLayout.IsObliqueView);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SelectedEditorKeepsExplicitLayoutUnitCoordinatesRegardlessOfGridSnap(bool snapToGrid)
    {
        var component = new LayoutComponentDefinition
        {
            Id = "frame-1", Name = "Frame", Kind = LayoutComponentKind.MachineFrame,
            Transform = new Transform2D(), Size = new Size2D { Width = 100, Height = 48 }
        };
        var definition = new MachineLayoutDefinition
        {
            Id = "main-cell", Name = "Main Cell", GridSize = 10, SnapToGrid = snapToGrid,
            Components = { component }
        };
        var project = new MachineProjectDocument { Layouts = { definition } };
        project.Simulation.ActiveLayoutId = definition.Id;
        using var layout = new MachineLayoutViewModel();
        layout.Load(project);
        layout.Select(component.Id);
        var editor = Assert.IsType<LayoutComponentEditorViewModel>(layout.SelectedComponentEditor);
        var changes = 0;
        layout.DefinitionChanged += (_, _) => changes++;
        Assert.False(editor.ApplyPlacementDraftCommand.CanExecute(null));
        Assert.False(editor.DiscardPlacementDraftCommand.CanExecute(null));

        editor.X = 13.5;
        editor.Y = -27.25;

        Assert.Equal(13.5, component.Transform.X);
        Assert.Equal(-27.25, component.Transform.Y);
        Assert.Equal(2, changes);
    }

    [Fact]
    public void PlacementDraftValidatesAndAppliesOneLayoutChangeWithoutMutatingBeforeApply()
    {
        var component = new LayoutComponentDefinition
        {
            Id = "frame-1", Name = "Frame", Kind = LayoutComponentKind.MachineFrame,
            Transform = new Transform2D { X = 20, Y = 30 }, Size = new Size2D { Width = 100, Height = 80 }
        };
        var definition = new MachineLayoutDefinition
        {
            Id = "main-cell", Name = "Main Cell", GridSize = 10, SnapToGrid = true,
            Components = { component }
        };
        var project = new MachineProjectDocument { Layouts = { definition } };
        project.Simulation.ActiveLayoutId = definition.Id;
        using var layout = new MachineLayoutViewModel();
        layout.Load(project);
        layout.Select(component.Id);
        var editor = Assert.IsType<LayoutComponentEditorViewModel>(layout.SelectedComponentEditor);
        var changes = 0;
        layout.DefinitionChanged += (_, _) => changes++;

        editor.DraftName = " Frame B ";
        editor.DraftXText = "27.5";
        editor.DraftDepthText = "-11.25";
        editor.DraftBaseElevationText = "6";
        editor.DraftRotationText = "45";
        editor.DraftFootprintWidthText = "120";
        editor.DraftFootprintDepthText = "90";
        editor.DraftVerticalHeightText = "60";
        Assert.True(editor.HasPendingPlacementDraft);
        Assert.True(editor.ApplyPlacementDraftCommand.CanExecute(null));
        Assert.True(editor.DiscardPlacementDraftCommand.CanExecute(null));
        Assert.Equal("Frame", component.Name);
        Assert.Equal(20, component.Transform.X);
        Assert.Equal(0, component.Transform.RotationDegrees);
        Assert.Equal(100, component.Size.Width);
        Assert.Equal(80, component.Size.Height);
        Assert.Null(component.VerticalEnvelope);
        Assert.Equal(0, changes);

        editor.DraftXText = "invalid";
        editor.ApplyPlacementDraftCommand.Execute(null);
        Assert.NotEmpty(editor.PlacementDraftError);
        Assert.True(editor.HasPendingPlacementDraft);
        Assert.Equal(0, changes);

        editor.DraftXText = "27.5";
        editor.DraftFootprintWidthText = "0";
        editor.ApplyPlacementDraftCommand.Execute(null);
        Assert.NotEmpty(editor.PlacementDraftError);
        Assert.Equal(0, changes);

        editor.DraftFootprintWidthText = "120";
        foreach (var invalidHeight in new[] { "0", "-1", "NaN", "Infinity", "10001" })
        {
            editor.DraftVerticalHeightText = invalidHeight;
            Assert.False(editor.TryApplyPlacementDraft());
            Assert.NotEmpty(editor.PlacementDraftError);
            Assert.Null(component.VerticalEnvelope);
            Assert.Equal(0, changes);
        }

        editor.DraftVerticalHeightText = "60";
        editor.DraftBaseElevationText = "-10000";
        editor.ApplyPlacementDraftCommand.Execute(null);
        Assert.False(editor.HasPendingPlacementDraft);
        Assert.Equal("Frame B", component.Name);
        Assert.Equal(27.5, component.Transform.X);
        Assert.Equal(-11.25, component.Transform.Y);
        Assert.Equal(45, component.Transform.RotationDegrees);
        Assert.Equal(120, component.Size.Width);
        Assert.Equal(90, component.Size.Height);
        Assert.Equal(-10000, component.VerticalEnvelope?.BaseElevation);
        Assert.Equal(60, component.VerticalEnvelope?.Height);
        Assert.Equal(1, changes);

        editor.DraftName = "Discarded";
        editor.DraftRotationText = "90";
        editor.DraftFootprintDepthText = "105";
        editor.DiscardPlacementDraftCommand.Execute(null);
        Assert.Equal("Frame B", editor.DraftName);
        Assert.Equal("45", editor.DraftRotationText);
        Assert.Equal("90", editor.DraftFootprintDepthText);
        Assert.False(editor.DiscardPlacementDraftCommand.CanExecute(null));
        Assert.Equal(1, changes);
    }

    [Fact]
    public void PlacementDraftKeepsEstimatedVerticalHeightWhenFootprintDepthChanges()
    {
        var component = new LayoutComponentDefinition
        {
            Id = "frame-1", Name = "Frame", Kind = LayoutComponentKind.MachineFrame,
            Transform = new Transform2D(), Size = new Size2D { Width = 100, Height = 48 }
        };
        var definition = new MachineLayoutDefinition { Id = "main", Name = "Main", Components = { component } };
        var project = new MachineProjectDocument { Layouts = { definition } };
        project.Simulation.ActiveLayoutId = definition.Id;
        using var layout = new MachineLayoutViewModel();
        layout.Load(project);
        layout.Select(component.Id);
        var editor = Assert.IsType<LayoutComponentEditorViewModel>(layout.SelectedComponentEditor);
        var changes = 0;
        layout.DefinitionChanged += (_, _) => changes++;

        Assert.Equal("19.2", editor.DraftVerticalHeightText);
        editor.DraftFootprintDepthText = "90";
        Assert.Equal(48, component.Size.Height);
        Assert.Null(component.VerticalEnvelope);
        Assert.True(editor.TryApplyPlacementDraft());

        Assert.Equal(90, component.Size.Height);
        Assert.Null(component.VerticalEnvelope);
        Assert.Equal(LayoutVerticalEnvelope.GetSchematicHeight(component.Size.Width, component.Size.Height), editor.VerticalHeight);
        Assert.True(editor.IsVerticalEnvelopeEstimated);
        Assert.Equal(1, changes);
    }

    [Fact]
    public void PlacementDraftRejectsStaleOrLockedSelectionWithoutOverwritingIt()
    {
        var component = new LayoutComponentDefinition
        {
            Id = "frame-1", Name = "Frame", Kind = LayoutComponentKind.MachineFrame,
            Transform = new Transform2D { X = 20, Y = 30 }, Size = new Size2D { Width = 100, Height = 80 }
        };
        var definition = new MachineLayoutDefinition { Id = "main", Name = "Main", Components = { component } };
        var project = new MachineProjectDocument { Layouts = { definition } };
        project.Simulation.ActiveLayoutId = definition.Id;
        using var layout = new MachineLayoutViewModel();
        layout.Load(project);
        layout.Select(component.Id);
        var editor = Assert.IsType<LayoutComponentEditorViewModel>(layout.SelectedComponentEditor);

        editor.DraftXText = "35";
        editor.X = 40;
        Assert.False(editor.TryApplyPlacementDraft());
        Assert.Equal(40, component.Transform.X);

        editor.DiscardPlacementDraft();
        editor.DraftFootprintWidthText = "120";
        editor.RotationDegrees = 30;
        Assert.False(editor.TryApplyPlacementDraft());
        Assert.Equal(100, component.Size.Width);
        Assert.Equal(30, component.Transform.RotationDegrees);

        editor.DiscardPlacementDraft();
        editor.DraftXText = "45";
        layout.IsEditable = false;
        Assert.False(editor.TryApplyPlacementDraft());
        Assert.Equal(40, component.Transform.X);
        Assert.NotEmpty(editor.PlacementDraftError);
    }

    [Fact]
    public void SelectedStageDriveDraftAppliesAtomicallyAndRejectsInvalidOrStaleValues()
    {
        var axis = new VirtualAxisDefinition { Id = "axis-x", Name = "X", Kind = AxisKind.Linear, SoftLimitMin = -50, SoftLimitMax = 250, HomePosition = 0, MaxVelocity = 100 };
        var component = new LayoutComponentDefinition
        {
            Id = "stage-x", Name = "X stage", Kind = LayoutComponentKind.LinearStage, BehaviorBindingId = axis.Id,
            Transform = new Transform2D(), Size = new Size2D { Width = 100, Height = 40 }
        };
        var definition = new MachineLayoutDefinition { Id = "main", Name = "Main", Components = { component } };
        var project = new MachineProjectDocument { Layouts = { definition }, Axes = { axis } };
        project.Simulation.ActiveLayoutId = definition.Id;
        using var layout = new MachineLayoutViewModel();
        layout.Load(project);
        layout.Select(component.Id);
        var editor = Assert.IsType<LayoutComponentEditorViewModel>(layout.SelectedComponentEditor);
        var changes = 0;
        layout.DefinitionChanged += (_, _) => changes++;
        Assert.True(editor.ShowAxisDriveProperties);
        Assert.False(editor.ApplyAxisDriveDraftCommand.CanExecute(null));

        editor.DraftAxisMinText = "-40";
        editor.DraftAxisMaxText = "200";
        editor.DraftAxisHomeText = "10";
        editor.DraftAxisSpeedText = "120";
        Assert.True(editor.HasPendingInspectorDraft);
        editor.BehaviorBindingId = "another-axis";
        Assert.Equal(axis.Id, component.BehaviorBindingId);
        Assert.Equal(-50, axis.SoftLimitMin);
        Assert.Equal(0, changes);

        editor.DraftAxisHomeText = "250";
        Assert.False(editor.TryApplyAxisDriveDraft());
        Assert.Equal(-50, axis.SoftLimitMin);
        Assert.NotEmpty(editor.AxisDriveDraftError);
        editor.DraftAxisHomeText = "10";
        Assert.True(editor.TryApplyAxisDriveDraft());
        Assert.Equal(-40, axis.SoftLimitMin);
        Assert.Equal(200, axis.SoftLimitMax);
        Assert.Equal(10, axis.HomePosition);
        Assert.Equal(120, axis.MaxVelocity);
        Assert.Equal(1, changes);
        Assert.False(editor.HasPendingInspectorDraft);

        editor.DraftAxisSpeedText = "130";
        axis.MaxVelocity = 110;
        Assert.False(editor.TryApplyAxisDriveDraft());
        Assert.Equal(110, axis.MaxVelocity);
        editor.DiscardAxisDriveDraft();
        Assert.Equal("110", editor.DraftAxisSpeedText);
        editor.DraftAxisSpeedText = "140";
        layout.IsEditable = false;
        Assert.False(editor.TryApplyAxisDriveDraft());
        Assert.Equal(110, axis.MaxVelocity);
    }
}
