using System.Globalization;
using OpenVisionLab;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Core.Layouts;
using OpenVisionLab.MachineStudio.ViewModel;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

[Collection(StudioUiTestCollection.Name)]
public sealed class MaterialFlowSettingsViewModelTests
{
    [Fact]
    public void TotalSupplyAndPositionConnectionsApplyTogetherAndReopenWithoutChangingLegacyRepeats()
    {
        var project = new MachineProjectDocument { Id = "setup" };
        project.Stations.Add(new() { Id = "s", Name = "s", Units = [new() { Id = "a", Name = "투입", RepeatCount = 7 }, new() { Id = "b", Name = "검사", RepeatCount = 4 }] });
        project.Layouts.Add(new() { Id = "l", Name = "l", Components =
        [
            new() { Id = "cv", Kind = LayoutComponentKind.Conveyor, UnitId = "a" },
            new() { Id = "feed", Kind = LayoutComponentKind.FeedPosition, UnitId = "a", Name = "투입 위치" },
            new() { Id = "exit", Kind = LayoutComponentKind.ExitPosition, UnitId = "a", Name = "배출 위치" }
        ] });
        var settings = new MaterialFlowSettingsViewModel(() => project, () => "a", () => true, () => { });
        settings.OpenCommand.Execute(null);
        Assert.False(settings.UseTotalFeedCount);
        Assert.Empty(settings.TotalFeedCount);
        settings.Enabled = true;
        settings.UseTotalFeedCount = true;
        settings.TotalFeedCount = "0";
        settings.ApplyCommand.Execute(null);
        Assert.Null(project.MaterialFlow.TotalFeedCount);
        settings.TotalFeedCount = "12";
        settings.ApplyCommand.Execute(null);
        Assert.True(settings.IsOpen);
        Assert.False(project.MaterialFlow.Enabled);
        foreach (var position in settings.Positions) position.ConveyorId = "cv";
        settings.Positions.Single(position => position.IsExit).Route = "auto";
        settings.SlotName = "투입 자리";
        settings.SelectedUnit = settings.Units[1];
        settings.SlotName = "검사 자리";
        settings.ApplyCommand.Execute(null);
        Assert.False(settings.IsOpen);
        Assert.Equal(12, project.MaterialFlow.TotalFeedCount);
        Assert.Equal(new[] { 7, 4 }, project.Stations[0].Units.Select(unit => unit.RepeatCount));
        Assert.Equal(new[] { "투입 자리", "검사 자리" }, project.Stations[0].Units.Select(unit => unit.SlotName));
        var store = new ProjectDocumentStore();
        project = store.Load(store.Save(project));
        settings.OpenCommand.Execute(null);
        Assert.Equal("12", settings.TotalFeedCount);
        Assert.All(settings.Positions, position => Assert.Equal("cv", position.ConveyorId));
        Assert.Equal("auto", settings.Positions.Single(position => position.IsExit).Route);
        settings.TotalFeedCount = "3";
        settings.CancelCommand.Execute(null);
        Assert.Equal(12, project.MaterialFlow.TotalFeedCount);
        Assert.Null(project.Simulation.AutomaticRun);
    }

    [Fact]
    public void ApplySaveReopenRestoresNamesAndRepeatsWithoutStartingAnything()
    {
        var project = new MachineProjectDocument { Id = "recipe" };
        project.Stations.Add(new() { Id = "station", Units = [new() { Id = "a", Name = "투입" }, new() { Id = "b", Name = "검사" }] });
        int changed = 0;
        var settings = new MaterialFlowSettingsViewModel(() => project, () => "b", () => true, () => changed++);
        settings.OpenCommand.Execute(null);
        settings.Enabled = true;
        settings.WorkpieceName = "검증 작업물";
        settings.SlotName = "검사 자리";
        settings.RepeatCount = "0";
        settings.ApplyCommand.Execute(null);
        Assert.True(settings.IsOpen);
        Assert.False(project.MaterialFlow.Enabled);
        Assert.Equal(0, changed);
        settings.RepeatCount = "3";
        settings.ApplyCommand.Execute(null);
        Assert.False(settings.IsOpen);
        Assert.Equal(1, changed);
        var store = new ProjectDocumentStore();
        var reopened = store.Load(store.Save(project));
        Assert.True(reopened.MaterialFlow.Enabled);
        Assert.All(reopened.Stations[0].Units, unit => Assert.Equal(3, unit.RepeatCount));
        var restored = new MaterialFlowSettingsViewModel(() => reopened, () => "b", () => true, () => changed++);
        restored.OpenCommand.Execute(null);
        Assert.Equal("검증 작업물", restored.WorkpieceName);
        Assert.Equal("검사 자리", restored.SlotName);
        Assert.Equal("3", restored.RepeatCount);
        restored.CancelCommand.Execute(null);
        Assert.Equal(1, changed);
        Assert.Null(reopened.Simulation.AutomaticRun);
    }

    [Fact]
    public void ReplacedRecipeAndLockedEditingCannotMutateSavedSettings()
    {
        var project = new MachineProjectDocument { Id = "same-id" };
        bool editable = true;
        int changes = 0;
        var settings = new MaterialFlowSettingsViewModel(() => project, () => null, () => editable, () => changes++);
        settings.OpenCommand.Execute(null);
        settings.WorkpieceName = "draft";
        project = new MachineProjectDocument { Id = "same-id" };
        settings.ApplyCommand.Execute(null);
        Assert.NotEqual("draft", project.MaterialFlow.WorkpieceName);
        settings.CancelCommand.Execute(null);
        settings.OpenCommand.Execute(null);
        editable = false;
        settings.ApplyCommand.Execute(null);
        Assert.Equal(0, changes);
    }

    [Theory]
    [InlineData(OpenVisionLanguage.Korean)]
    [InlineData(OpenVisionLanguage.English)]
    public void LocalizedDraftRejectsBoundariesAndRecoversWithoutChangingAuthoredNames(OpenVisionLanguage language)
    {
        var previousLanguage = OpenVisionLanguageService.CurrentLanguage;
        OpenVisionLanguageService.SetLanguage(language, save: false);
        try
        {
            string savedName = new('P', 60), savedSlot = new('S', 60);
            var project = new MachineProjectDocument
            {
                MaterialFlow = new() { WorkpieceName = savedName },
                Stations = [new() { Units = [new() { Id = "a", Name = "Inspection", SlotName = savedSlot }, new() { Id = "b", Name = "Output" }] }]
            };
            var store = new ProjectDocumentStore();
            var authored = store.SerializeForSaveEvidence(project);
            int changes = 0;
            var settings = new MaterialFlowSettingsViewModel(() => project, () => "a", () => true, () => changes++);
            settings.OpenCommand.Execute(null);
            Assert.Equal(savedName, settings.WorkpieceName);
            Assert.Equal(savedSlot, settings.SlotName);
            foreach (var input in new (string Name, string Slot, string Count)[]
            {
                ("", savedSlot, "1"), (new string('P', 61), savedSlot, "1"),
                (savedName, "", "1"), (savedName, new string('S', 61), "1"),
                (savedName, savedSlot, "NaN"), (savedName, savedSlot, "0"), (savedName, savedSlot, "101")
            })
            {
                settings.WorkpieceName = input.Name;
                settings.SlotName = input.Slot;
                settings.RepeatCount = input.Count;
                settings.ApplyCommand.Execute(null);
                Assert.True(settings.IsOpen);
                Assert.Equal(OpenVisionLanguageService.T("MaterialFlow.InvalidSettings"), settings.ValidationText);
                Assert.Equal(authored, store.SerializeForSaveEvidence(project));
                Assert.Equal(0, changes);
            }
            var otherLanguage = language == OpenVisionLanguage.Korean ? OpenVisionLanguage.English : OpenVisionLanguage.Korean;
            OpenVisionLanguageService.SetLanguage(otherLanguage, save: false);
            settings.RefreshLocalization();
            Assert.Equal(OpenVisionLanguageService.T("MaterialFlow.InvalidSettings"), settings.ValidationText);
            Assert.Equal(savedName, settings.WorkpieceName);
            Assert.Equal(savedSlot, settings.SlotName);
            settings.ResetCommand.Execute(null);
            Assert.Equal(OpenVisionLanguageService.T("MaterialFlow.SampleProduct"), settings.WorkpieceName);
            Assert.Equal(string.Format(CultureInfo.CurrentCulture, OpenVisionLanguageService.T("MaterialFlow.SlotDefaultFormat"), "Inspection"), settings.SlotName);
            Assert.Empty(settings.ValidationText);
            Assert.False(settings.Enabled);
            Assert.Equal(authored, store.SerializeForSaveEvidence(project));
            settings.CancelCommand.Execute(null);
            settings.OpenCommand.Execute(null);
            Assert.Equal(savedName, settings.WorkpieceName);
            Assert.Equal(savedSlot, settings.SlotName);
            settings.WorkpieceName = " " + savedName + " ";
            settings.SlotName = " " + savedSlot + " ";
            settings.RepeatCount = "100";
            settings.ApplyCommand.Execute(null);
            Assert.False(settings.IsOpen);
            Assert.Equal(1, changes);
            Assert.Equal(savedName, project.MaterialFlow.WorkpieceName);
            Assert.Equal(savedSlot, project.Stations[0].Units[0].SlotName);
            Assert.All(project.Stations[0].Units, unit => Assert.Equal(100, unit.RepeatCount));
            Assert.Null(project.Simulation.AutomaticRun);

            project = new MachineProjectDocument();
            settings.OpenCommand.Execute(null);
            settings.Enabled = true;
            settings.RepeatCount = "1";
            settings.ApplyCommand.Execute(null);
            Assert.True(settings.IsOpen);
            Assert.Equal(OpenVisionLanguageService.T("MaterialFlow.UnitRequired"), settings.ValidationText);
            Assert.False(project.MaterialFlow.Enabled);
            settings.Enabled = false;
            settings.ApplyCommand.Execute(null);
            Assert.False(settings.IsOpen);
            Assert.Equal(2, changes);
            Assert.Null(project.Simulation.AutomaticRun);
        }
        finally { OpenVisionLanguageService.SetLanguage(previousLanguage, save: false); }
    }
}
