using System.Collections.ObjectModel;
using System.Windows.Input;
using OpenVisionLab;
using OpenVisionLab.Machine.Core.Axes;
using OpenVisionLab.Machine.Core.Layouts;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Core.Sequences;
using OpenVisionLab.Machine.Sequence.Authoring;
using OpenVisionLab.MachineStudio.ViewModel;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

[Collection(LayoutStartupTestCollection.Name)]
public sealed class RecipeConnectionWorkbenchViewModelTests
{
    [Fact]
    public void SemanticEquipmentSetupViewModelOwnsEditabilityAndPreviewCommands()
    {
        var project = new ProjectDocumentStore().Load(File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "SemiconductorRecipes",
            "04-WaferOcrInspection.ovmachine")));
        var clearCount = 0;
        var viewModel = new SemanticEquipmentSetupViewModel(
            _ => 0,
            _ => 0,
            _ => 0,
            _ => 0,
            _ => 0,
            () => clearCount++);

        viewModel.Load(project);
        Assert.True(viewModel.PreviewInspectionHandoffSetupCommand.CanExecute(null));

        viewModel.PreviewInspectionHandoffSetupCommand.Execute(null);

        Assert.Equal(1, clearCount);
        Assert.True(viewModel.IsInspectionHandoffSetupVisible);
        Assert.True(viewModel.CancelInspectionHandoffSetupCommand.CanExecute(null));

        viewModel.IsEditable = false;

        Assert.False(viewModel.PreviewInspectionHandoffSetupCommand.CanExecute(null));
        Assert.False(viewModel.ApplyInspectionHandoffSetupCommand.CanExecute(null));
        Assert.True(viewModel.CancelInspectionHandoffSetupCommand.CanExecute(null));
    }

    [Fact]
    public void ProcessBlockViewModelOwnsPlanSelectionFilteringAndTimeoutState()
    {
        var project = new ProjectDocumentStore().Load(File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "Samples",
            "AutomaticTransferCell.ovmachine")));
        var viewModel = CreateViewModel(() => { }, () => { }, () => { });
        viewModel.Load(project);

        var processBlocks = viewModel.ProcessBlocks;
        processBlocks.PreviewProcessBlockCommand.Execute(null);

        Assert.True(processBlocks.IsProcessBlockPreviewVisible);
        Assert.Equal(13, processBlocks.ProcessBlockItems.Count);
        Assert.True(processBlocks.SelectedProcessBlockCount > 0);

        processBlocks.IsProcessBlockSelected = false;
        Assert.True(processBlocks.SelectedProcessBlockCount < 5);
        processBlocks.IsProcessBlockFilterCustomized = true;
        Assert.All(processBlocks.VisibleProcessBlockItems, item => Assert.True(item.IsCustomized));

        processBlocks.ProcessBlockTimeoutText = "-1";
        Assert.False(processBlocks.IsProcessBlockTimeoutValid);
        processBlocks.IsEditable = false;
        Assert.False(processBlocks.PreviewProcessBlockCommand.CanExecute(null));
    }

    [Fact]
    public void LoadLockSetupViewModelOwnsDraftValidationAndCommands()
    {
        var project = new ProjectDocumentStore().Load(File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "SemiconductorRecipes",
            "05-LoadLockEntry.ovmachine")));
        var clearCount = 0;
        var viewModel = new LoadLockSetupViewModel(_ => 0, () => clearCount++);

        viewModel.Load(project);
        viewModel.PreviewCommand.Execute(null);

        Assert.Equal(1, clearCount);
        Assert.True(viewModel.IsVisible);
        Assert.NotEmpty(viewModel.DoorOptions);
        Assert.NotEmpty(viewModel.OutputOptions);
        Assert.NotEmpty(viewModel.InputOptions);
        Assert.True(viewModel.ApplyCommand.CanExecute(null));

        viewModel.PumpDownDurationText = "251";

        Assert.True(viewModel.HasValidationError);
        Assert.False(viewModel.ApplyCommand.CanExecute(null));

        viewModel.IsEditable = false;

        Assert.False(viewModel.PreviewCommand.CanExecute(null));
        Assert.False(viewModel.ResetCommand.CanExecute(null));
        Assert.True(viewModel.CancelCommand.CanExecute(null));
    }

    [Fact]
    public void StationSkeletonSetupViewModelOwnsDraftValidationAndCommands()
    {
        var project = new MachineProjectDocument { Name = "Station setup test" };
        var store = new ProjectDocumentStore();
        var projectBefore = store.Serialize(project);
        var clearCount = 0;
        var applyCount = 0;
        SemiconductorStationSetupDefinition? appliedSetup = null;
        var viewModel = new StationSkeletonSetupViewModel(
            setup =>
            {
                applyCount++;
                appliedSetup = setup;
                return 1;
            },
            () => clearCount++);

        viewModel.Load(project);
        viewModel.PreviewStationSkeletonCommand.Execute(null);

        Assert.Equal(1, clearCount);
        Assert.True(viewModel.IsStationSkeletonPreviewVisible);
        Assert.Equal(10, viewModel.StationSkeletonProposedCount);
        Assert.Equal(10, viewModel.StationSkeletonItems.Count);
        Assert.All(viewModel.StationSkeletonItems, item => Assert.True(item.IsProposed));
        Assert.True(viewModel.ApplyStationSkeletonCommand.CanExecute(null));
        Assert.Equal(projectBefore, store.Serialize(project));

        viewModel.AxisTravelText = "-1";

        Assert.True(viewModel.HasStationSetupValidationError);
        Assert.False(viewModel.ApplyStationSkeletonCommand.CanExecute(null));

        viewModel.ResetStationSetupCommand.Execute(null);

        Assert.Equal("320", viewModel.AxisTravelText);
        Assert.False(viewModel.HasStationSetupValidationError);
        Assert.Equal(projectBefore, store.Serialize(project));

        viewModel.StationName = "  Lithography Transfer A  ";
        viewModel.WaferType = "  200 mm Wafer  ";
        viewModel.AxisTravelText = "460";
        viewModel.TransportSpeedText = "175";
        viewModel.EntrySensorPositionText = "145";
        viewModel.ProcessSensorPositionText = "510";
        viewModel.CylinderTravelTimeText = "180";
        viewModel.ApplyStationSkeletonCommand.Execute(null);

        Assert.Equal(1, applyCount);
        var actualSetup = Assert.IsType<SemiconductorStationSetupDefinition>(appliedSetup);
        Assert.Equal("Lithography Transfer A", actualSetup.StationName);
        Assert.Equal("200 mm Wafer", actualSetup.WaferType);
        Assert.Equal(460, actualSetup.AxisTravel);
        Assert.Equal(175, actualSetup.TransportSpeed);
        Assert.Equal(145, actualSetup.EntrySensorPosition);
        Assert.Equal(510, actualSetup.ProcessSensorPosition);
        Assert.Equal(180, actualSetup.CylinderTravelTimeMilliseconds);
        Assert.Equal(projectBefore, store.Serialize(project));

        viewModel.IsEditable = false;

        Assert.False(viewModel.PreviewStationSkeletonCommand.CanExecute(null));
        Assert.False(viewModel.ApplyStationSkeletonCommand.CanExecute(null));
        Assert.False(viewModel.ResetStationSetupCommand.CanExecute(null));
        Assert.True(viewModel.CancelStationSkeletonCommand.CanExecute(null));

        viewModel.CancelStationSkeletonCommand.Execute(null);

        Assert.False(viewModel.IsStationSkeletonPreviewVisible);
        Assert.Empty(viewModel.StationSkeletonItems);
        Assert.Equal(1, applyCount);

        viewModel.IsEditable = true;
        viewModel.PreviewStationSkeletonCommand.Execute(null);
        viewModel.ClearPreviewForCompetingSetup();

        Assert.False(viewModel.IsStationSkeletonPreviewVisible);
        Assert.Empty(viewModel.StationSkeletonItems);
        Assert.Equal(projectBefore, store.Serialize(project));
    }

    [Fact]
    public void StationSkeletonSetupViewModelRefreshLocalizationPreservesRawDraftWithoutMutation()
    {
        var originalLanguage = OpenVisionLanguageService.CurrentLanguage;
        try
        {
            OpenVisionLanguageService.Load();
            OpenVisionLanguageService.SetLanguage(OpenVisionLanguage.Korean, save: false);
            var project = new MachineProjectDocument { Name = "Station localization test" };
            var store = new ProjectDocumentStore();
            var projectBefore = store.Serialize(project);
            var applyCount = 0;
            var viewModel = CreateViewModel(
                () => { },
                () => { },
                () => { },
                _ =>
                {
                    applyCount++;
                    return 1;
                });
            viewModel.Load(project);
            viewModel.StationSetups.PreviewStationSkeletonCommand.Execute(null);
            var koreanRoleText = viewModel.StationSetups.StationSkeletonItems[0].RoleText;
            viewModel.StationSetups.AxisTravelText = "-1";
            var koreanValidationText = viewModel.StationSetups.StationSetupValidationText;

            OpenVisionLanguageService.SetLanguage(OpenVisionLanguage.English, save: false);
            viewModel.RefreshLocalization();

            Assert.True(viewModel.StationSetups.IsStationSkeletonPreviewVisible);
            Assert.Equal("-1", viewModel.StationSetups.AxisTravelText);
            Assert.True(viewModel.StationSetups.HasStationSetupValidationError);
            Assert.False(viewModel.StationSetups.ApplyStationSkeletonCommand.CanExecute(null));
            Assert.NotEqual(koreanRoleText, viewModel.StationSetups.StationSkeletonItems[0].RoleText);
            Assert.NotEqual(koreanValidationText, viewModel.StationSetups.StationSetupValidationText);
            Assert.Equal(projectBefore, store.Serialize(project));
            Assert.Equal(0, applyCount);
        }
        finally
        {
            OpenVisionLanguageService.SetLanguage(originalLanguage, save: false);
        }
    }

    [Theory]
    [InlineData("08-DryEtchTransfer.ovmachine", "wafer-handler")]
    [InlineData("03-WaferPrealigner.ovmachine", "prealigner")]
    [InlineData("04-WaferOcrInspection.ovmachine", "inspection-handoff")]
    [InlineData("10-MetrologySorter.ovmachine", "inspection-sort")]
    [InlineData("01-FoupLoadPort.ovmachine", "oht")]
    public void RefreshLocalizationPreservesSemanticSetupDraft(
        string fileName,
        string setupKind)
    {
        var originalLanguage = OpenVisionLanguageService.CurrentLanguage;
        try
        {
            OpenVisionLanguageService.Load();
            OpenVisionLanguageService.SetLanguage(OpenVisionLanguage.Korean, save: false);
            var store = new ProjectDocumentStore();
            var project = store.Load(File.ReadAllText(Path.Combine(
                AppContext.BaseDirectory,
                "SemiconductorRecipes",
                fileName)));
            var applyCount = 0;
            var dryRunCount = 0;
            var playbackCount = 0;
            var viewModel = CreateViewModel(
                () => applyCount++,
                () => dryRunCount++,
                () => playbackCount++);
            viewModel.Load(project);

            ICommand PreviewCommand() => setupKind switch
            {
                "wafer-handler" => viewModel.SemanticSetups.PreviewWaferHandlerSetupCommand,
                "prealigner" => viewModel.SemanticSetups.PreviewPrealignerSetupCommand,
                "inspection-handoff" => viewModel.SemanticSetups.PreviewInspectionHandoffSetupCommand,
                "inspection-sort" => viewModel.SemanticSetups.PreviewInspectionSortRouterSetupCommand,
                _ => viewModel.SemanticSetups.PreviewOhtHandoffSetupCommand
            };
            ICommand ApplyCommand() => setupKind switch
            {
                "wafer-handler" => viewModel.SemanticSetups.ApplyWaferHandlerSetupCommand,
                "prealigner" => viewModel.SemanticSetups.ApplyPrealignerSetupCommand,
                "inspection-handoff" => viewModel.SemanticSetups.ApplyInspectionHandoffSetupCommand,
                "inspection-sort" => viewModel.SemanticSetups.ApplyInspectionSortRouterSetupCommand,
                _ => viewModel.SemanticSetups.ApplyOhtHandoffSetupCommand
            };
            ICommand ResetCommand() => setupKind switch
            {
                "wafer-handler" => viewModel.SemanticSetups.ResetWaferHandlerSetupCommand,
                "prealigner" => viewModel.SemanticSetups.ResetPrealignerSetupCommand,
                "inspection-handoff" => viewModel.SemanticSetups.ResetInspectionHandoffSetupCommand,
                "inspection-sort" => viewModel.SemanticSetups.ResetInspectionSortRouterSetupCommand,
                _ => viewModel.SemanticSetups.ResetOhtHandoffSetupCommand
            };
            ICommand CancelCommand() => setupKind switch
            {
                "wafer-handler" => viewModel.SemanticSetups.CancelWaferHandlerSetupCommand,
                "prealigner" => viewModel.SemanticSetups.CancelPrealignerSetupCommand,
                "inspection-handoff" => viewModel.SemanticSetups.CancelInspectionHandoffSetupCommand,
                "inspection-sort" => viewModel.SemanticSetups.CancelInspectionSortRouterSetupCommand,
                _ => viewModel.SemanticSetups.CancelOhtHandoffSetupCommand
            };
            string? DraftValue() => setupKind switch
            {
                "wafer-handler" => viewModel.SemanticSetups.WaferHandlerHorizontalAxisId,
                "prealigner" => viewModel.SemanticSetups.PrealignerRotaryStageComponentId,
                "inspection-handoff" => viewModel.SemanticSetups.InspectionHandoffCameraId,
                "inspection-sort" => viewModel.SemanticSetups.InspectionSortCameraId,
                _ => viewModel.SemanticSetups.OhtTransportConveyorId
            };
            void SetDraftValue(string value)
            {
                if (setupKind == "wafer-handler") viewModel.SemanticSetups.WaferHandlerHorizontalAxisId = value;
                else if (setupKind == "prealigner") viewModel.SemanticSetups.PrealignerRotaryStageComponentId = value;
                else if (setupKind == "inspection-handoff") viewModel.SemanticSetups.InspectionHandoffCameraId = value;
                else if (setupKind == "inspection-sort") viewModel.SemanticSetups.InspectionSortCameraId = value;
                else viewModel.SemanticSetups.OhtTransportConveyorId = value;
            }
            ObservableCollection<LoadLockSetupOption> DraftOptions() => setupKind switch
            {
                "wafer-handler" => viewModel.SemanticSetups.WaferHandlerAxisOptions,
                "prealigner" => viewModel.SemanticSetups.PrealignerStageOptions,
                "oht" => viewModel.SemanticSetups.InspectionConveyorOptions,
                _ => viewModel.SemanticSetups.InspectionCameraOptions
            };
            bool IsVisible() => setupKind switch
            {
                "wafer-handler" => viewModel.SemanticSetups.IsWaferHandlerSetupVisible,
                "prealigner" => viewModel.SemanticSetups.IsPrealignerSetupVisible,
                "inspection-handoff" => viewModel.SemanticSetups.IsInspectionHandoffSetupVisible,
                "inspection-sort" => viewModel.SemanticSetups.IsInspectionSortRouterSetupVisible,
                _ => viewModel.SemanticSetups.IsOhtHandoffSetupVisible
            };
            bool HasValidationError() => setupKind switch
            {
                "wafer-handler" => viewModel.SemanticSetups.HasWaferHandlerSetupValidationError,
                "prealigner" => viewModel.SemanticSetups.HasPrealignerSetupValidationError,
                "inspection-handoff" => viewModel.SemanticSetups.HasInspectionHandoffSetupValidationError,
                "inspection-sort" => viewModel.SemanticSetups.HasInspectionSortRouterSetupValidationError,
                _ => viewModel.SemanticSetups.HasOhtHandoffSetupValidationError
            };
            string ValidationText() => setupKind switch
            {
                "wafer-handler" => viewModel.SemanticSetups.WaferHandlerSetupValidationText,
                "prealigner" => viewModel.SemanticSetups.PrealignerSetupValidationText,
                "inspection-handoff" => viewModel.SemanticSetups.InspectionHandoffSetupValidationText,
                "inspection-sort" => viewModel.SemanticSetups.InspectionSortRouterSetupValidationText,
                _ => viewModel.SemanticSetups.OhtHandoffSetupValidationText
            };

            PreviewCommand().Execute(null);
            var savedValue = DraftValue();
            var projectBefore = store.Serialize(project);
            var invalidValue = $"missing-{setupKind}";
            SetDraftValue(invalidValue);

            OpenVisionLanguageService.SetLanguage(OpenVisionLanguage.English, save: false);
            viewModel.RefreshLocalization();

            Assert.True(IsVisible());
            Assert.Equal(invalidValue, DraftValue());
            Assert.True(HasValidationError());
            Assert.False(ApplyCommand().CanExecute(null));
            var englishMissingOption = setupKind is "wafer-handler" or "prealigner"
                ? null
                : Assert.Single(DraftOptions(), option => option.Id == invalidValue);
            if (englishMissingOption is not null)
            {
                Assert.Contains(
                    OpenVisionLanguageService.T("Connections.LoadLockSetupMissing"),
                    englishMissingOption.DisplayName);
            }
            var englishValidationText = ValidationText();

            OpenVisionLanguageService.SetLanguage(OpenVisionLanguage.Korean, save: false);
            viewModel.RefreshLocalization();

            Assert.Equal(invalidValue, DraftValue());
            if (englishMissingOption is not null)
            {
                var koreanMissingOption = Assert.Single(
                    DraftOptions(),
                    option => option.Id == invalidValue);
                Assert.NotEqual(englishMissingOption.DisplayName, koreanMissingOption.DisplayName);
            }
            Assert.NotEqual(englishValidationText, ValidationText());
            Assert.Equal(projectBefore, store.Serialize(project));
            Assert.Equal(0, applyCount);
            Assert.Equal(0, dryRunCount);
            Assert.Equal(0, playbackCount);

            ResetCommand().Execute(null);
            Assert.Equal(savedValue, DraftValue());
            Assert.False(HasValidationError());
            Assert.True(ApplyCommand().CanExecute(null));
            ApplyCommand().Execute(null);
            Assert.Equal(1, applyCount);
            Assert.False(IsVisible());

            PreviewCommand().Execute(null);
            SetDraftValue(invalidValue);
            OpenVisionLanguageService.SetLanguage(OpenVisionLanguage.English, save: false);
            viewModel.RefreshLocalization();
            CancelCommand().Execute(null);
            Assert.False(IsVisible());
            Assert.Equal(1, applyCount);
            Assert.Equal(0, dryRunCount);
            Assert.Equal(0, playbackCount);
            Assert.Equal(projectBefore, store.Serialize(project));
        }
        finally
        {
            OpenVisionLanguageService.SetLanguage(originalLanguage, save: false);
        }
    }

    [Fact]
    public void RefreshingRowsDoesNotPublishTransientNullSelectionAndExplicitRowsStillSelect()
    {
        var project = new ProjectDocumentStore().Load(File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "Samples",
            "AutomaticTransferCell.ovmachine")));
        var selectionEvents = new List<string?>();
        var viewModel = CreateViewModel(
            () => { },
            () => { },
            () => { },
            selectComponent: selectionEvents.Add);

        viewModel.Load(project);
        var initiallySelectedRow = viewModel.Rows.FirstOrDefault()
            ?? throw new InvalidOperationException("The sample project did not produce a connection row.");
        viewModel.SelectedRow = initiallySelectedRow;
        Assert.Equal(new[] { initiallySelectedRow.ComponentId }, selectionEvents);

        selectionEvents.Clear();
        viewModel.Load(project, initiallySelectedRow.ComponentId);

        Assert.Equal(initiallySelectedRow.ComponentId, viewModel.SelectedRow?.ComponentId);
        Assert.Empty(selectionEvents);

        var explicitlySelectedRow = viewModel.Rows.Skip(1).FirstOrDefault()
            ?? throw new InvalidOperationException("The sample project did not produce a second connection row.");
        viewModel.SelectedRow = explicitlySelectedRow;

        Assert.Equal(new[] { explicitlySelectedRow.ComponentId }, selectionEvents);
    }

    [Fact]
    public void TargetStepCommandMatchesSequenceStructureEditability()
    {
        var project = new MachineProjectDocument { Name = "Connection structure gate" };
        project.Axes.Add(new VirtualAxisDefinition
        {
            Id = "used-axis",
            Name = "Used axis"
        });
        project.Axes.Add(new VirtualAxisDefinition
        {
            Id = "unused-axis",
            Name = "Unused axis"
        });

        var layout = new MachineLayoutDefinition
        {
            Id = "layout",
            Name = "Layout"
        };
        layout.Components.Add(new LayoutComponentDefinition
        {
            Id = "unused-stage",
            Name = "Unused stage",
            Kind = LayoutComponentKind.LinearStage,
            BehaviorBindingId = "unused-axis",
            Transform = new Transform2D { X = 100, Y = 100 },
            Size = new Size2D { Width = 120, Height = 40 }
        });
        project.Layouts.Add(layout);
        project.Simulation.ActiveLayoutId = layout.Id;

        var sequence = new SequenceDefinition
        {
            Id = "sequence",
            Name = "Branched sequence",
            Steps =
            [
                new SequenceStepDefinition
                {
                    Id = "move-used-axis",
                    Name = "Move used axis",
                    Action = SequenceStepAction.MoveAxis,
                    TargetId = "used-axis",
                    NextStepId = "complete",
                    ErrorStepId = "complete"
                },
                new SequenceStepDefinition
                {
                    Id = "complete",
                    Name = "Complete",
                    Action = SequenceStepAction.Complete
                }
            ]
        };
        project.Sequences.Add(sequence);

        string? addedTargetId = null;
        var viewModel = CreateViewModel(
            () => { },
            () => { },
            () => { },
            addSequenceStep: targetId =>
            {
                addedTargetId = targetId;
                return "added-step";
            });
        viewModel.Load(project);

        var row = Assert.Single(viewModel.Rows);
        Assert.False(SequenceDefinitionEditor.IsStrictLinear(sequence));
        Assert.True(row.IsValid);
        Assert.False(row.HasSequenceUse);
        Assert.False(row.CanAddSequenceStep);
        Assert.False(viewModel.AddSequenceStepCommand.CanExecute(row));

        sequence.Steps[0].ErrorStepId = null;
        viewModel.Load(project, row.ComponentId);

        row = Assert.Single(viewModel.Rows);
        Assert.True(SequenceDefinitionEditor.IsStrictLinear(sequence));
        Assert.True(row.CanAddSequenceStep);
        Assert.True(viewModel.AddSequenceStepCommand.CanExecute(row));

        viewModel.AddSequenceStepCommand.Execute(row);
        Assert.Equal("unused-axis", addedTargetId);
    }

    private static RecipeConnectionWorkbenchViewModel CreateViewModel(
        Action applied,
        Action dryRun,
        Action playback,
        Func<SemiconductorStationSetupDefinition, int>? applyStationSkeleton = null,
        Action<string?>? selectComponent = null,
        Func<string, string?>? addSequenceStep = null)
    {
        int Apply()
        {
            applied();
            return 1;
        }

        return new RecipeConnectionWorkbenchViewModel(
            selectComponent: selectComponent ?? (_ => { }),
            openSequenceStep: (_, _) => { },
            addSequenceStep: addSequenceStep ?? (_ => null),
            validateSimulationReadiness: () => null,
            previewSequenceStep: (_, _, _) => throw new InvalidOperationException(),
            runRecipeDryRun: _ =>
            {
                dryRun();
                throw new InvalidOperationException();
            },
            playRecipeDryRunStep: _ => playback(),
            applyVirtualCameraWorkflow: () => false,
            applyStationSkeleton: applyStationSkeleton ?? (_ => 0),
            applyLoadLockSetup: _ => 0,
            applyWaferHandlerSetup: _ => Apply(),
            applyPrealignerSetup: _ => Apply(),
            applyInspectionHandoffSetup: _ => Apply(),
            applyInspectionSortRouterSetup: _ => Apply(),
            applyOhtHandoffSetup: _ => Apply(),
            applyProcessBlock: _ => 0,
            applyProcessBlockTimeouts: _ => 0,
            checkpointTemplateApplied: _ => { });
    }
}
