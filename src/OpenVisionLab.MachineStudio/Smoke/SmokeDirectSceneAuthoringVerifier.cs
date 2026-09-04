using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using OpenVisionLab.MachineStudio.Model;
using OpenVisionLab.MachineStudio.View.Scene;
using OpenVisionLab.MachineStudio.View.Shell;
using OpenVisionLab.MachineStudio.ViewModel;
using static OpenVisionLab.MachineStudio.SmokeRoundTripScenario;

namespace OpenVisionLab.MachineStudio;

internal sealed class SmokeDirectSceneAuthoringReport
{
    public string Schema { get; init; } = "1.0";
    public DateTimeOffset CapturedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public required IReadOnlyDictionary<string, bool> Checks { get; init; }
    public required IReadOnlyList<string> Failures { get; init; }
    public SmokeMonitorEvidence? Monitor { get; init; }
    public bool IsValid => Failures.Count == 0 && Checks.Values.All(value => value);

    public void Save(string path)
    {
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(
            fullPath,
            JsonSerializer.Serialize(this, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = true
            }));
    }
}

internal static class SmokeDirectSceneAuthoringVerifier
{
    public static async Task<SmokeDirectSceneAuthoringReport> VerifyAsync(
        ShellWindow window,
        MainViewModel viewModel,
        string reportPath,
        Func<DependencyObject, MachineSceneViewport?> findViewport)
    {
        var checks = new Dictionary<string, bool>(StringComparer.Ordinal);
        var failures = new List<string>();

        void Check(string name, bool passed)
        {
            checks[name] = passed;
            if (!passed)
            {
                failures.Add(name);
            }
        }

        static void Execute(ICommand command)
        {
            if (!command.CanExecute(null))
            {
                throw new InvalidOperationException("Expected layout history command was disabled.");
            }
            command.Execute(null);
        }

        static IReadOnlyDictionary<string, (double X, double Y)> Positions(
            MachineLayoutViewModel layout,
            IEnumerable<string> ids) => ids.ToDictionary(
                id => id,
                id =>
                {
                    var item = layout.Items.Single(candidate => candidate.Id == id);
                    return (item.CurrentX, item.CurrentY);
                },
                StringComparer.Ordinal);

        static bool SamePosition(
            MachineLayoutViewModel layout,
            IReadOnlyDictionary<string, (double X, double Y)> expected) => expected.All(entry =>
        {
            var item = layout.Items.Single(candidate => candidate.Id == entry.Key);
            return item.CurrentX == entry.Value.X && item.CurrentY == entry.Value.Y;
        });

        var viewport = findViewport(window)
            ?? throw new InvalidOperationException("Machine scene viewport was not available.");
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);

        var groupIds = new[] { RoundTripStageId, RoundTripCylinderId };
        viewModel.Layout.SelectMany(groupIds, RoundTripCylinderId);
        var groupBefore = Positions(viewModel.Layout, groupIds);
        Check("groupDragRequested", viewport.RequestSelectionDrag(RoundTripCylinderId, new Vector(48, 24)));
        var groupAfter = Positions(viewModel.Layout, groupIds);
        var groupDelta = (
            X: groupAfter[RoundTripCylinderId].X - groupBefore[RoundTripCylinderId].X,
            Y: groupAfter[RoundTripCylinderId].Y - groupBefore[RoundTripCylinderId].Y);
        Check("groupDragApplied", groupDelta != default);
        Check(
            "groupOffsetsPreserved",
            groupIds.All(id =>
                groupAfter[id].X - groupBefore[id].X == groupDelta.X &&
                groupAfter[id].Y - groupBefore[id].Y == groupDelta.Y));
        Check(
            "groupDragSnapped",
            Math.Abs(groupDelta.X % viewModel.Layout.GridSize) < 0.000001 &&
            Math.Abs(groupDelta.Y % viewModel.Layout.GridSize) < 0.000001);
        Check("groupDragCreatedHistory", viewModel.UndoLayoutEditCommand.CanExecute(null));

        Execute(viewModel.UndoLayoutEditCommand);
        Check("groupDragUndo", SamePosition(viewModel.Layout, groupBefore));
        Check("oneUndoEntryPerGesture", !viewModel.UndoLayoutEditCommand.CanExecute(null));
        Execute(viewModel.RedoLayoutEditCommand);
        Check("groupDragRedo", SamePosition(viewModel.Layout, groupAfter));
        Execute(viewModel.UndoLayoutEditCommand);

        viewModel.Layout.Select(RoundTripCylinderId);
        var singleBefore = Positions(viewModel.Layout, new[] { RoundTripCylinderId });
        Check("singleDragRequested", viewport.RequestSelectionDrag(RoundTripCylinderId, new Vector(32, -18)));
        var singleAfter = Positions(viewModel.Layout, new[] { RoundTripCylinderId });
        Check("singleDragApplied", !SamePosition(viewModel.Layout, singleBefore));
        Check("newDragClearsRedo", !viewModel.RedoLayoutEditCommand.CanExecute(null));
        Execute(viewModel.UndoLayoutEditCommand);
        Check("singleDragUndo", SamePosition(viewModel.Layout, singleBefore));

        var stageBounds = viewport.GetItemScreenBounds(RoundTripStageId)
            ?? throw new InvalidOperationException("Stage screen bounds were unavailable.");
        stageBounds.Inflate(2, 2);
        var stageMarqueeIds = viewport.RequestMarqueeSelection(stageBounds, ModifierKeys.None);
        Check(
            "marqueeReplace",
            stageMarqueeIds.Contains(RoundTripStageId, StringComparer.Ordinal) &&
            viewModel.Layout.SelectedItems.All(item => item.Kind != LayoutItemKind.MachineFrame) &&
            viewModel.Layout.SelectedItems.Select(item => item.Id).ToHashSet(StringComparer.Ordinal)
                .SetEquals(stageMarqueeIds));

        var cylinderBounds = viewport.GetItemScreenBounds(RoundTripCylinderId)
            ?? throw new InvalidOperationException("Cylinder screen bounds were unavailable.");
        cylinderBounds.Inflate(2, 2);
        viewport.RequestMarqueeSelection(cylinderBounds, ModifierKeys.Shift);
        Check(
            "marqueeShiftAdd",
            viewModel.Layout.SelectedItems.Any(item => item.Id == RoundTripStageId) &&
            viewModel.Layout.SelectedItems.Any(item => item.Id == RoundTripCylinderId));
        viewport.RequestMarqueeSelection(stageBounds, ModifierKeys.Control);
        Check(
            "marqueeControlToggle",
            viewModel.Layout.SelectedItems.All(item => item.Id != RoundTripStageId) &&
            viewModel.Layout.SelectedItems.Any(item => item.Id == RoundTripCylinderId));
        Check(
            "marqueeDoesNotCreateHistory",
            !viewModel.UndoLayoutEditCommand.CanExecute(null) &&
            viewModel.RedoLayoutEditCommand.CanExecute(null));

        viewModel.Layout.Select(RoundTripStageId);
        var canceledBefore = Positions(viewModel.Layout, new[] { RoundTripStageId });
        Check("cancelDragBegins", viewModel.Layout.BeginSelectionDrag());
        viewModel.Layout.UpdateSelectionDrag(30, 20);
        viewModel.Layout.CancelSelectionDrag();
        Check("cancelDragRestores", SamePosition(viewModel.Layout, canceledBefore));
        Check("cancelDragDoesNotCreateHistory", !viewModel.UndoLayoutEditCommand.CanExecute(null));

        viewModel.IsRunMode = true;
        Check("runPolicyBlocksDrag", !viewModel.Layout.BeginSelectionDrag());
        viewModel.IsRunMode = false;

        viewModel.Layout.Select(RoundTripCylinderId);
        viewport.RequestSelectionDrag(RoundTripCylinderId, new Vector(44, 0));
        var persistedPosition = Positions(viewModel.Layout, new[] { RoundTripCylinderId })[RoundTripCylinderId];
        var reportDirectory = Path.GetDirectoryName(Path.GetFullPath(reportPath))!;
        Directory.CreateDirectory(reportDirectory);
        var projectPath = Path.Combine(reportDirectory, "direct-scene-roundtrip.ovmachine");
        await viewModel.SaveProjectAsync(projectPath);
        Check("roundTripOpen", await viewModel.OpenProjectAsync(projectPath));
        Check(
            "dragPersists",
            viewModel.Layout.Items.Single(item => item.Id == RoundTripCylinderId).CurrentX == persistedPosition.X &&
            viewModel.Layout.Items.Single(item => item.Id == RoundTripCylinderId).CurrentY == persistedPosition.Y);
        Check(
            "reopenDoesNotRun",
            viewModel.IsDesignMode && !viewModel.IsRunning && !viewModel.UndoLayoutEditCommand.CanExecute(null));

        return new SmokeDirectSceneAuthoringReport
        {
            Checks = checks,
            Failures = failures
        };
    }
}
