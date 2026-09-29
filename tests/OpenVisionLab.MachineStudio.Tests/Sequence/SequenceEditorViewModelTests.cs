using System.Windows.Input;
using OpenVisionLab.Machine.Core.Axes;
using OpenVisionLab.Machine.Core.Devices;
using OpenVisionLab.Machine.Core.Layouts;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Core.Sequences;
using OpenVisionLab.Machine.Sequence.Authoring;
using OpenVisionLab.Machine.Sequence.Compilation;
using OpenVisionLab.MachineStudio.ViewModel;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

public sealed class SequenceEditorViewModelTests
{
    [Fact]
    public void TargetStepCanBeAddedToStrictSequenceAndRoundTrips()
    {
        var project = new MachineProjectDocument
        {
            Id = "target-step-project",
            Name = "Target step project",
            Axes =
            [
                new VirtualAxisDefinition
                {
                    Id = "axis-1",
                    Name = "Axis 1"
                }
            ],
            Sequences =
            [
                CompleteSequence("sequence-1")
            ]
        };

        var editor = new SequenceEditorViewModel();
        editor.Load(project);

        var stepId = editor.TryAddStepForTarget("axis-1");

        Assert.NotNull(stepId);
        var addedStep = Assert.Single(project.Sequences[0].Steps, step => step.Id == stepId);
        Assert.Equal(SequenceStepAction.MoveAxis, addedStep.Action);
        Assert.Equal("axis-1", addedStep.TargetId);
        Assert.Equal("complete", addedStep.NextStepId);
        Assert.Equal(stepId, editor.SelectedStep?.Id);

        var reopened = new ProjectDocumentStore().Load(new ProjectDocumentStore().Serialize(project));
        var reopenedStep = Assert.Single(reopened.Sequences[0].Steps, step => step.Id == stepId);
        Assert.Equal(SequenceStepAction.MoveAxis, reopenedStep.Action);
        Assert.Equal("axis-1", reopenedStep.TargetId);
        Assert.Equal("complete", reopenedStep.NextStepId);
    }

    [Fact]
    public void CameraWorkpieceAssociationSurvivesFeedAndEjectActionChangesAndReopens()
    {
        var project = new MachineProjectDocument
        {
            Id = "camera-workpiece-project",
            Name = "Camera workpiece project",
            Simulation = new SimulationDefinition { ActiveLayoutId = "layout-1" },
            Layouts =
            [
                new MachineLayoutDefinition
                {
                    Id = "layout-1",
                    Components =
                    [
                        new LayoutComponentDefinition
                        {
                            Id = "workpiece-1",
                            Name = "Wafer",
                            Kind = LayoutComponentKind.Workpiece,
                            BehaviorBindingId = "workpiece-device"
                        }
                    ]
                }
            ],
            Devices =
            [
                new DeviceDefinition { Id = "camera-1", Name = "Top camera", Kind = DeviceKind.Camera },
                new DeviceDefinition
                {
                    Id = "workpiece-device",
                    Name = "Wafer",
                    Kind = DeviceKind.Workpiece,
                    Workpiece = new WorkpieceDefinition { Type = "Wafer" }
                }
            ],
            Sequences =
            [
                new SequenceDefinition
                {
                    Id = "sequence-1",
                    Name = "Sequence 1",
                    Steps =
                    [
                        new SequenceStepDefinition
                        {
                            Id = "trigger-camera",
                            Action = SequenceStepAction.TriggerCamera,
                            TargetId = "camera-1",
                            Parameter = "presence-check",
                            NextStepId = "complete"
                        },
                        new SequenceStepDefinition { Id = "complete", Action = SequenceStepAction.Complete }
                    ]
                }
            ]
        };
        var editor = new SequenceEditorViewModel();
        editor.Load(project);
        SequenceStepEditorItem step = Assert.Single(editor.Steps, candidate => candidate.Id == "trigger-camera");
        int definitionChangeCount = 0;
        editor.DefinitionChanged += (_, _) => definitionChangeCount++;

        Assert.Equal(new[] { string.Empty, "workpiece-1" }, step.AvailableWorkpieceTargets.Select(target => target.Id));
        Assert.Empty(editor.ValidationIssues);

        step.WorkpieceComponentId = "workpiece-1";
        Assert.Equal("workpiece-1", project.Sequences[0].Steps[0].WorkpieceComponentId);
        Assert.Empty(editor.ValidationIssues);
        step.WorkpieceComponentId = "workpiece-1";
        Assert.Equal(1, definitionChangeCount);

        step.WorkpieceComponentId = string.Empty;
        Assert.Null(project.Sequences[0].Steps[0].WorkpieceComponentId);
        Assert.Equal(2, definitionChangeCount);
        step.Action = SequenceStepAction.WaitVisionResult;
        Assert.False(step.HasWorkpieceTargetOptions);
        Assert.Null(project.Sequences[0].Steps[0].WorkpieceComponentId);
        step.Action = SequenceStepAction.TriggerCamera;
        Assert.True(step.HasWorkpieceTargetOptions);
        step.WorkpieceComponentId = "workpiece-1";
        Assert.Equal(5, definitionChangeCount);
        step.Action = SequenceStepAction.FeedWorkpiece;
        Assert.Equal(string.Empty, step.TargetId);
        Assert.True(step.UsesWorkpieceAsTarget);
        Assert.Equal("workpiece-1", step.WorkpieceComponentId);
        Assert.Empty(editor.ValidationIssues);

        step.Action = SequenceStepAction.EjectWorkpiece;
        Assert.Equal(string.Empty, step.TargetId);
        Assert.True(step.UsesWorkpieceAsTarget);
        Assert.Equal("workpiece-1", step.WorkpieceComponentId);
        Assert.Empty(editor.ValidationIssues);

        step.Action = SequenceStepAction.TriggerCamera;
        Assert.Equal("camera-1", step.TargetId);
        Assert.True(step.HasWorkpieceTargetOptions);
        Assert.Equal("workpiece-1", step.WorkpieceComponentId);
        Assert.Equal(8, definitionChangeCount);
        var reopened = new ProjectDocumentStore().Load(new ProjectDocumentStore().Serialize(project));

        Assert.Equal(MachineProjectDocument.CurrentSchema, reopened.Schema);
        Assert.Equal("workpiece-1", reopened.Sequences[0].Steps[0].WorkpieceComponentId);
        editor.Dispose();
    }

    [Fact]
    public void FeedAndEjectCanBeAuthoredRepeatedlyWithAnExplicitWorkpieceTarget()
    {
        var project = new MachineProjectDocument
        {
            Id = "workpiece-action-project",
            Name = "Workpiece action project",
            Simulation = new SimulationDefinition { ActiveLayoutId = "layout-1" },
            Layouts =
            [
                new MachineLayoutDefinition
                {
                    Id = "layout-1",
                    Components =
                    [
                        new LayoutComponentDefinition
                        {
                            Id = "workpiece-1",
                            Name = "Wafer",
                            Kind = LayoutComponentKind.Workpiece,
                            BehaviorBindingId = "workpiece-device"
                        }
                    ]
                }
            ],
            Devices =
            [
                new DeviceDefinition
                {
                    Id = "workpiece-device",
                    Name = "Wafer",
                    Kind = DeviceKind.Workpiece,
                    Workpiece = new WorkpieceDefinition { Type = "Wafer" }
                }
            ],
            Sequences = [CompleteSequence("sequence-1")]
        };
        var editor = new SequenceEditorViewModel();
        editor.Load(project);

        SequenceStepTemplateDefinition feedTemplate = Assert.Single(
            editor.Templates,
            template => template.Id == "feed-workpiece");
        Assert.Contains(editor.Templates, template => template.Id == "eject-workpiece");
        editor.SelectedTemplate = feedTemplate;
        editor.AddStepCommand.Execute(null);
        editor.AddStepCommand.Execute(null);

        SequenceStepEditorItem feed = Assert.Single(editor.Steps, step => step.Id == "step-1");
        SequenceStepEditorItem eject = Assert.Single(editor.Steps, step => step.Id == "step-2");
        Assert.Equal(SequenceStepAction.FeedWorkpiece, feed.Action);
        Assert.Contains(SequenceStepAction.FeedWorkpiece, feed.AvailableActions);
        Assert.Contains(SequenceStepAction.EjectWorkpiece, feed.AvailableActions);
        Assert.Equal(string.Empty, feed.TargetId);
        Assert.False(feed.HasTargetOptions);
        Assert.True(feed.HasWorkpieceTargetOptions);
        Assert.True(feed.UsesWorkpieceAsTarget);
        Assert.Equal(new[] { string.Empty, "workpiece-1" },
            feed.AvailableWorkpieceTargets.Select(target => target.Id));
        Assert.Equal(string.Empty, feed.WorkpieceComponentId);
        Assert.Contains(editor.ValidationIssues, issue =>
            issue.Code == SequenceCompilationErrorCode.WorkpieceComponentIdRequired
            && issue.StepId == feed.Id);

        eject.Action = SequenceStepAction.EjectWorkpiece;
        Assert.True(eject.HasWorkpieceTargetOptions);
        Assert.False(eject.HasTargetOptions);
        Assert.True(eject.UsesWorkpieceAsTarget);
        feed.WorkpieceComponentId = "workpiece-1";
        eject.WorkpieceComponentId = "workpiece-1";
        Assert.Empty(editor.ValidationIssues);
        Assert.Equal("complete", eject.NextStepId);

        var reopened = new ProjectDocumentStore().Load(new ProjectDocumentStore().Serialize(project));
        Assert.Equal(
            new[] { SequenceStepAction.FeedWorkpiece, SequenceStepAction.EjectWorkpiece },
            reopened.Sequences[0].Steps.Take(2).Select(step => step.Action));
        Assert.All(reopened.Sequences[0].Steps.Take(2), step =>
            Assert.Equal("workpiece-1", step.WorkpieceComponentId));
        Assert.Equal("complete", reopened.Sequences[0].Steps[1].NextStepId);
        editor.Dispose();
    }

    [Fact]
    public void CallSubsequenceTargetsExcludeTheSelectedSequenceAndSurviveReopen()
    {
        var project = new MachineProjectDocument
        {
            Id = "composition-project",
            Name = "Composition project",
            Sequences =
            [
                SequenceWithCall("parent", "child"),
                SequenceWithCall("child", "parent"),
                CompleteSequence("other")
            ]
        };

        var editor = new SequenceEditorViewModel();
        editor.Load(project);

        var parentCall = Assert.Single(editor.Steps.Where(step => step.Id == "call-child"));
        Assert.Equal(SequenceStepAction.CallSubsequence, parentCall.Action);
        Assert.Equal(new[] { "child", "other" }, parentCall.AvailableTargets.Select(target => target.Id));
        Assert.DoesNotContain(parentCall.AvailableTargets, target => target.Id == "parent");
        Assert.Contains(parentCall.AvailableActions, action => action == SequenceStepAction.CallSubsequence);
        Assert.Empty(parentCall.AvailableParameterOptions);
        Assert.Equal(0, parentCall.TimeoutMs);

        editor.SelectSequence("child");
        var childCall = Assert.Single(editor.Steps.Where(step => step.Id == "call-parent"));
        Assert.Equal(new[] { "parent", "other" }, childCall.AvailableTargets.Select(target => target.Id));
        Assert.DoesNotContain(childCall.AvailableTargets, target => target.Id == "child");

        var reopened = new ProjectDocumentStore().Load(new ProjectDocumentStore().Serialize(project));
        var reopenedEditor = new SequenceEditorViewModel();
        reopenedEditor.Load(reopened);

        Assert.Equal("parent", reopenedEditor.SelectedSequence?.Id);
        Assert.Contains(reopenedEditor.Templates, template => template.Id == "call-subsequence");
        Assert.Equal(
            new[] { "child", "other" },
            Assert.Single(reopenedEditor.Steps.Where(step => step.Id == "call-child"))
                .AvailableTargets.Select(target => target.Id));
    }

    [Fact]
    public void ReloadingAndDisposingEditorDetachesRemovedStepEvents()
    {
        var project = new MachineProjectDocument
        {
            Id = "step-lifetime-project",
            Name = "Step lifetime project",
            Sequences =
            [
                CompleteSequence("first"),
                CompleteSequence("second")
            ]
        };

        var editor = new SequenceEditorViewModel();
        editor.Load(project);
        SequenceStepEditorItem previousStep = Assert.Single(editor.Steps);
        var definitionChangedCount = 0;
        editor.DefinitionChanged += (_, _) => definitionChangedCount++;

        editor.SelectSequence("second");
        previousStep.Name = "Detached after reload";

        Assert.Equal(0, definitionChangedCount);
        SequenceStepEditorItem currentStep = Assert.Single(editor.Steps);

        editor.Dispose();
        currentStep.Name = "Detached after dispose";

        Assert.Empty(editor.Steps);
        Assert.Equal(0, definitionChangedCount);
    }

    [Fact]
    public void EditabilityChangeNotifiesStructuralCommands()
    {
        var project = new MachineProjectDocument
        {
            Id = "editability-command-project",
            Name = "Editability command project",
            Axes =
            [
                new VirtualAxisDefinition
                {
                    Id = "axis-1",
                    Name = "Axis 1"
                }
            ],
            Sequences =
            [
                CompleteSequence("sequence-1")
            ]
        };

        var editor = new SequenceEditorViewModel();
        editor.Load(project);

        Assert.True(editor.AddStepCommand.CanExecute(null));
        var notifications = 0;
        editor.AddStepCommand.CanExecuteChanged += (_, _) => notifications++;

        editor.IsEditable = false;

        Assert.False(editor.AddStepCommand.CanExecute(null));
        Assert.Equal(1, notifications);
    }

    [Fact]
    public void DisposeDisablesStructuralCommandsAndNotifiesFinalAdmission()
    {
        var project = new MachineProjectDocument
        {
            Id = "dispose-command-project",
            Name = "Dispose command project",
            Axes =
            [
                new VirtualAxisDefinition
                {
                    Id = "axis-1",
                    Name = "Axis 1"
                }
            ],
            Sequences =
            [
                CompleteSequence("sequence-1")
            ]
        };

        var editor = new SequenceEditorViewModel();
        editor.Load(project);
        var commands = new ICommand[]
        {
            editor.AddStepCommand,
            editor.DeleteStepCommand,
            editor.MoveStepUpCommand,
            editor.MoveStepDownCommand
        };
        var notifications = new int[commands.Length];
        for (var index = 0; index < commands.Length; index++)
        {
            var commandIndex = index;
            commands[commandIndex].CanExecuteChanged += (_, _) => notifications[commandIndex]++;
        }

        editor.Dispose();

        Assert.All(commands, command => Assert.False(command.CanExecute(null)));
        Assert.All(notifications, count => Assert.Equal(1, count));
    }

    [Fact]
    public void DisposedEditorRejectsDirectTargetStepAdmission()
    {
        var project = new MachineProjectDocument
        {
            Id = "disposed-target-step-project",
            Name = "Disposed target step project",
            Axes =
            [
                new VirtualAxisDefinition
                {
                    Id = "axis-1",
                    Name = "Axis 1"
                }
            ],
            Sequences =
            [
                CompleteSequence("sequence-1")
            ]
        };
        var editor = new SequenceEditorViewModel();
        editor.Load(project);
        var originalStepCount = project.Sequences[0].Steps.Count;

        editor.Dispose();

        var stepId = editor.TryAddStepForTarget("axis-1");

        Assert.Null(stepId);
        Assert.Equal(originalStepCount, project.Sequences[0].Steps.Count);
    }

    [Fact]
    public void DisposeRejectsDirectLocalizationRefresh()
    {
        var project = new MachineProjectDocument
        {
            Id = "disposed-localization-project",
            Name = "Disposed localization project",
            Sequences =
            [
                CompleteSequence("sequence-1")
            ]
        };
        var editor = new SequenceEditorViewModel();
        editor.Load(project);
        var changedProperties = new List<string?>();
        editor.PropertyChanged += (_, args) => changedProperties.Add(args.PropertyName);

        editor.Dispose();
        changedProperties.Clear();

        editor.RefreshLocalization();

        Assert.Empty(changedProperties);
    }

    [Fact]
    public void ValidationIssuesNavigateToStepAndCompareCompileChanges()
    {
        var project = new MachineProjectDocument
        {
            Id = "validation-navigation-project",
            Name = "Validation navigation project",
            Axes =
            [
                new VirtualAxisDefinition
                {
                    Id = "axis-1",
                    Name = "Axis 1"
                }
            ],
            Sequences =
            [
                new SequenceDefinition
                {
                    Id = "sequence-1",
                    Name = "Sequence 1",
                    Steps =
                    [
                        new SequenceStepDefinition
                        {
                            Id = "move-axis",
                            Name = "Move axis",
                            Action = SequenceStepAction.MoveAxis,
                            TargetId = "axis-1",
                            Parameter = "0",
                            NextStepId = "complete"
                        },
                        new SequenceStepDefinition
                        {
                            Id = "complete",
                            Name = "Complete",
                            Action = SequenceStepAction.Complete
                        }
                    ]
                }
            ]
        };
        var editor = new SequenceEditorViewModel();
        editor.Load(project);

        var step = Assert.Single(editor.Steps, candidate => candidate.Id == "move-axis");
        step.NextStepId = "missing-step";

        var issue = Assert.Single(editor.ValidationIssues, candidate =>
            candidate.PropertyName == "Step.NextStepId");
        Assert.Equal("sequence-1", issue.SequenceId);
        Assert.Equal("move-axis", issue.StepId);
        Assert.Equal("axis-1", issue.TargetId);
        Assert.Equal("Step.NextStepId", issue.PropertyName);
        Assert.Contains("missing-step", issue.DisplayText);
        Assert.Contains("1", editor.ValidationComparisonText);

        editor.SelectedValidationIssue = issue;
        Assert.Equal("move-axis", editor.SelectedStep?.Id);

        step.NextStepId = "complete";

        Assert.Empty(editor.ValidationIssues);
        Assert.Contains("0", editor.ValidationComparisonText);
        Assert.Equal("complete", project.Sequences[0].Steps[0].NextStepId);
    }

    [Fact]
    public void RuntimeStepIndicatorTracksSequenceAndClearsWithoutChangingEditorSelection()
    {
        var project = new MachineProjectDocument
        {
            Sequences = [CompleteSequence("first"), CompleteSequence("second")]
        };
        var editor = new SequenceEditorViewModel();
        editor.Load(project);
        var selectedStep = editor.SelectedStep;

        editor.ApplyRuntimeStep("first", "complete");
        Assert.True(Assert.Single(editor.Steps).IsExecuting);
        Assert.True(editor.HasExecutingStep);
        Assert.Equal("Complete", editor.CurrentRuntimeStepName);
        Assert.Same(selectedStep, editor.SelectedStep);

        editor.SelectSequence("second");
        Assert.False(Assert.Single(editor.Steps).IsExecuting);
        Assert.False(editor.HasExecutingStep);
        editor.SelectSequence("first");
        Assert.True(Assert.Single(editor.Steps).IsExecuting);

        editor.ApplyRuntimeStep(null, null);
        Assert.False(Assert.Single(editor.Steps).IsExecuting);
        Assert.False(editor.HasExecutingStep);
        editor.Load(project);
        Assert.False(Assert.Single(editor.Steps).IsExecuting);
        Assert.Equal("complete", project.Sequences[0].Steps[0].Id);
    }

    private static SequenceDefinition SequenceWithCall(string id, string targetId) => new()
    {
        Id = id,
        Name = id,
        Steps =
        [
            new SequenceStepDefinition
            {
                Id = $"call-{targetId}",
                Name = $"Call {targetId}",
                Action = SequenceStepAction.CallSubsequence,
                TargetId = targetId,
                NextStepId = "complete"
            },
            new SequenceStepDefinition
            {
                Id = "complete",
                Name = "Complete",
                Action = SequenceStepAction.Complete
            }
        ]
    };

    private static SequenceDefinition CompleteSequence(string id) => new()
    {
        Id = id,
        Name = id,
        Steps =
        [
            new SequenceStepDefinition
            {
                Id = "complete",
                Name = "Complete",
                Action = SequenceStepAction.Complete
            }
        ]
    };
}
