using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using OpenVisionLab;
using OpenVisionLab.Machine.Core.Devices;
using OpenVisionLab.Machine.Core.Layouts;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Core.Sequences;
using OpenVisionLab.MachineStudio.View.Sequence;
using OpenVisionLab.MachineStudio.ViewModel;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

[Collection(EquipmentOutlineViewAutomationTestCollection.Name)]
public sealed class SequenceEditorViewAutomationTests
{
    [Fact]
    public async Task GraphConnectorShowsConfiguredRoutesAndTerminalCompletion()
    {
        await RunOnStaAsync(() =>
        {
            if (Application.Current is null) new App().InitializeComponent();

            var project = CreateProject();
            var sequence = Assert.Single(project.Sequences);
            var cameraDefinition = sequence.Steps.Single(step => step.Id == "trigger-camera");
            cameraDefinition.ErrorStepId = "error-complete";
            cameraDefinition.FailureStepId = "failure-complete";
            sequence.Steps.Add(new SequenceStepDefinition { Id = "error-complete", Action = SequenceStepAction.Complete });
            sequence.Steps.Add(new SequenceStepDefinition { Id = "failure-complete", Action = SequenceStepAction.Complete });

            var editor = new SequenceEditorViewModel();
            editor.Load(project);
            var cameraStep = editor.Steps.Single(step => step.Id == "trigger-camera");
            editor.SelectedStep = cameraStep;
            var terminalStep = editor.Steps.Single(step => step.Id == "complete");
            var view = new SequenceEditorView { DataContext = editor };
            var window = new Window
            {
                Width = 980,
                Height = 720,
                ShowInTaskbar = false,
                ShowActivated = false,
                WindowStyle = WindowStyle.None,
                Content = view
            };
            window.Show();
            try
            {
                window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(window.UpdateLayout));
                var controls = Descendants(view).ToArray();
                var graph = controls.OfType<ListBox>().Single(listBox =>
                    listBox.GetBindingExpression(ItemsControl.ItemsSourceProperty)?.ParentBinding.Path.Path == "Steps");
                var errorRoute = controls.OfType<TextBox>().Single(textBox =>
                    textBox.GetBindingExpression(TextBox.TextProperty)?.ParentBinding.Path.Path == "SelectedStep.ErrorStepId");
                var errorRouteValue = Assert.IsAssignableFrom<IValueProvider>(
                    UIElementAutomationPeer.CreatePeerForElement(errorRoute)?.GetPattern(PatternInterface.Value));
                TextBlock cameraConnector = FindTransitionSummary(graph, cameraStep);

                Assert.Equal(
                    $"{OpenVisionLanguageService.T("Sequence.Next")} → complete"
                    + $"  |  {OpenVisionLanguageService.T("Sequence.Error")} → error-complete"
                    + $"  |  {OpenVisionLanguageService.T("Sequence.Failure")} → failure-complete",
                    cameraConnector.Text);

                errorRouteValue.SetValue(string.Empty);
                PumpBindings();
                Assert.Equal(string.Empty, cameraStep.ErrorStepId);
                Assert.Equal(
                    $"{OpenVisionLanguageService.T("Sequence.Next")} → complete"
                    + $"  |  {OpenVisionLanguageService.T("Sequence.Failure")} → failure-complete",
                    cameraConnector.Text);

                TextBlock terminalConnector = FindTransitionSummary(graph, terminalStep);
                Assert.Equal(OpenVisionLanguageService.T("Sequence.Complete", "완료", "Complete"), terminalConnector.Text);
            }
            finally
            {
                window.Close();
                editor.Dispose();
            }

            return true;
        });
    }

    [Fact]
    public async Task WorkpieceTargetComboBoxBindingSurvivesFeedAndEjectActionChanges()
    {
        var state = await RunOnStaAsync(() =>
        {
            if (Application.Current is null) new App().InitializeComponent();

            var project = CreateProject();
            var editor = new SequenceEditorViewModel();
            editor.Load(project);
            editor.SelectedStep = Assert.Single(editor.Steps, step => step.Id == "trigger-camera");
            var step = editor.SelectedStep!;
            var view = new SequenceEditorView { DataContext = editor };
            var window = new Window
            {
                Width = 980,
                Height = 720,
                ShowInTaskbar = false,
                ShowActivated = false,
                WindowStyle = WindowStyle.None,
                Content = view
            };
            window.Show();
            try
            {
                window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(window.UpdateLayout));
                var controls = Descendants(view).ToArray();
                var workpiece = controls.OfType<ComboBox>().Single(comboBox =>
                    comboBox.GetBindingExpression(Selector.SelectedValueProperty)?.ParentBinding.Path.Path
                        == "SelectedStep.WorkpieceComponentId");
                var action = controls.OfType<ComboBox>().Single(comboBox =>
                    comboBox.GetBindingExpression(Selector.SelectedItemProperty)?.ParentBinding.Path.Path
                        == "SelectedStep.Action");
                workpiece.SelectedValue = "workpiece-1";
                PumpBindings();
                var cameraState = (step.Action, step.TargetId, step.WorkpieceComponentId);

                action.SelectedItem = SequenceStepAction.FeedWorkpiece;
                PumpBindings();
                var feedState = (step.Action, step.TargetId, step.WorkpieceComponentId);

                action.SelectedItem = SequenceStepAction.EjectWorkpiece;
                PumpBindings();
                var ejectState = (step.Action, step.TargetId, step.WorkpieceComponentId);

                action.SelectedItem = SequenceStepAction.TriggerCamera;
                PumpBindings();
                var restoredCameraState = (step.Action, step.TargetId, step.WorkpieceComponentId);

                return (cameraState, feedState, ejectState, restoredCameraState,
                    modelWorkpieceId: project.Sequences.Single().Steps.Single(candidate => candidate.Id == step.Id)
                        .WorkpieceComponentId);
            }
            finally
            {
                window.Close();
            }
        });

        Assert.Equal((SequenceStepAction.TriggerCamera, "camera-1", "workpiece-1"), state.cameraState);
        Assert.Equal((SequenceStepAction.FeedWorkpiece, string.Empty, "workpiece-1"), state.feedState);
        Assert.Equal((SequenceStepAction.EjectWorkpiece, string.Empty, "workpiece-1"), state.ejectState);
        Assert.Equal((SequenceStepAction.TriggerCamera, "camera-1", "workpiece-1"), state.restoredCameraState);
        Assert.Equal("workpiece-1", state.modelWorkpieceId);
    }

    private static MachineProjectDocument CreateProject()
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
        return project;
    }

    private static void PumpBindings() =>
        Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.DataBind, new Action(() => { }));

    private static TextBlock FindTransitionSummary(ListBox graph, SequenceStepEditorItem item)
    {
        graph.ScrollIntoView(item);
        PumpBindings();
        var container = Assert.IsType<ListBoxItem>(graph.ItemContainerGenerator.ContainerFromItem(item));
        return Descendants(container).OfType<TextBlock>().Single(textBlock =>
            textBlock.DataContext == item
            && textBlock.GetBindingExpression(TextBlock.TextProperty)?.ParentBinding.Path.Path
                == nameof(SequenceStepEditorItem.TransitionSummary));
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private static Task<T> RunOnStaAsync<T>(Func<T> action)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { completion.SetResult(action()); }
            catch (Exception exception) { completion.SetException(exception); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }
}
