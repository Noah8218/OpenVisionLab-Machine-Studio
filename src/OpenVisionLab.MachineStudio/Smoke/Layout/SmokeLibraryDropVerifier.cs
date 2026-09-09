using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using OpenVisionLab.Machine.Core.Layouts;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.MachineStudio.Model;
using OpenVisionLab.MachineStudio.View.Scene;
using OpenVisionLab.MachineStudio.View.Shell;
using OpenVisionLab.MachineStudio.ViewModel;
using static OpenVisionLab.MachineStudio.SmokeRoundTripScenario;

namespace OpenVisionLab.MachineStudio;

internal sealed class SmokeLibraryDropReport
{
    public string Schema { get; init; } = "1.0";
    public DateTimeOffset CapturedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public required IReadOnlyDictionary<string, bool> Checks { get; init; }
    public required IReadOnlyList<string> Failures { get; init; }
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

internal static class SmokeLibraryDropVerifier
{
    public static async Task<SmokeLibraryDropReport> VerifyAsync(
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

        var viewport = findViewport(window)
            ?? throw new InvalidOperationException("Machine scene viewport was not available.");
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);

        var initialCount = viewModel.Layout.Items.Count;
        foreach (var kind in Enum.GetValues<LayoutComponentKind>())
        {
            viewport.FitToLayout();
            var screenPoint = new Point(
                viewport.ActualWidth * 0.68,
                viewport.ActualHeight * 0.34);
            if (kind == LayoutComponentKind.MachineFrame)
            {
                viewport.ZoomAt(screenPoint, 120);
                viewport.PanBy(new Vector(36, -22));
            }
            var worldPoint = viewport.GetDropWorldPoint(screenPoint)
                ?? throw new InvalidOperationException("Library drop world point was unavailable.");
            var expectedX = Math.Round(
                worldPoint.X / viewModel.Layout.GridSize,
                MidpointRounding.AwayFromZero) * viewModel.Layout.GridSize;
            var expectedY = Math.Round(
                worldPoint.Y / viewModel.Layout.GridSize,
                MidpointRounding.AwayFromZero) * viewModel.Layout.GridSize;

            Check($"{kind}.dropRequested", viewport.RequestLibraryComponentDrop(kind, screenPoint));
            var added = viewModel.Layout.SelectedItem;
            Check($"{kind}.addedAndSelected",
                viewModel.Layout.Items.Count == initialCount + 1 &&
                added?.Component?.Kind == kind);
            Check($"{kind}.usesViewportProjection",
                added is not null &&
                added.CurrentX == expectedX &&
                added.CurrentY == expectedY);
            Check($"{kind}.snappedToGrid",
                added is not null &&
                Math.Abs(added.CurrentX % viewModel.Layout.GridSize) < 0.000001d &&
                Math.Abs(added.CurrentY % viewModel.Layout.GridSize) < 0.000001d);
            Check($"{kind}.createdHistory", viewModel.UndoLayoutEditCommand.CanExecute(null));

            viewModel.UndoLayoutEditCommand.Execute(null);
            Check($"{kind}.undoRemovesDrop", viewModel.Layout.Items.Count == initialCount);
            Check($"{kind}.oneUndoEntry", !viewModel.UndoLayoutEditCommand.CanExecute(null));
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        }

        Check(
            "clickAddStillAvailable",
            viewModel.AddLayoutComponentCommand.CanExecute(LayoutComponentKind.RotaryStage));
        viewModel.AddLayoutComponentCommand.Execute(LayoutComponentKind.RotaryStage);
        var clickAdded = viewModel.Layout.SelectedItem;
        Check(
            "clickAddUsesNearestFreeGridPosition",
            viewModel.Layout.Items.Count == initialCount + 1 &&
            clickAdded?.Component?.Kind == LayoutComponentKind.RotaryStage &&
            Math.Abs(clickAdded.CurrentX % viewModel.Layout.GridSize) < 0.000001d &&
            Math.Abs(clickAdded.CurrentY % viewModel.Layout.GridSize) < 0.000001d &&
            viewModel.Layout.Items
                .Where(item => item.Id != clickAdded.Id &&
                               item.Component?.Kind != LayoutComponentKind.MachineFrame)
                .All(item => !Overlaps(clickAdded, item)));
        viewModel.UndoLayoutEditCommand.Execute(null);
        Check("clickAddUndo", viewModel.Layout.Items.Count == initialCount);

        viewModel.IsRunMode = true;
        var runCount = viewModel.Layout.Items.Count;
        Check(
            "runModeBlocksDrop",
            !viewport.RequestLibraryComponentDrop(
                LayoutComponentKind.PneumaticCylinder,
                new Point(viewport.ActualWidth / 2, viewport.ActualHeight / 2)) &&
            viewModel.Layout.Items.Count == runCount);
        viewModel.IsRunMode = false;

        Check("defaultClickAddForPersistence", viewModel.TryAddLayoutComponent(LayoutComponentKind.RotaryStage));
        var defaultAdded = viewModel.Layout.SelectedItem
            ?? throw new InvalidOperationException("Default-added rotary stage was not selected.");
        var defaultAddedId = defaultAdded.Id;
        var defaultAddedPosition = (defaultAdded.CurrentX, defaultAdded.CurrentY);

        viewport.FitToLayout();
        var persistedScreenPoint = new Point(
            viewport.ActualWidth * 0.76,
            viewport.ActualHeight * 0.29);
        Check(
            "persistenceDropRequested",
            viewport.RequestLibraryComponentDrop(LayoutComponentKind.Conveyor, persistedScreenPoint));
        var persisted = viewModel.Layout.SelectedItem
            ?? throw new InvalidOperationException("Dropped conveyor was not selected.");
        var persistedId = persisted.Id;
        var persistedPosition = (persisted.CurrentX, persisted.CurrentY);

        var reportDirectory = Path.GetDirectoryName(Path.GetFullPath(reportPath))!;
        Directory.CreateDirectory(reportDirectory);
        var projectPath = Path.Combine(reportDirectory, "library-drop-roundtrip.ovmachine");
        await viewModel.SaveProjectAsync(projectPath);
        Check("roundTripOpen", await viewModel.OpenProjectAsync(projectPath));
        var reopened = viewModel.Layout.Items.SingleOrDefault(item => item.Id == persistedId);
        Check(
            "dropPersists",
            reopened is not null &&
            reopened.CurrentX == persistedPosition.CurrentX &&
            reopened.CurrentY == persistedPosition.CurrentY);

        var reopenedDefault = viewModel.Layout.Items.SingleOrDefault(item => item.Id == defaultAddedId);
        Check(
            "defaultClickAddPersists",
            reopenedDefault is not null &&
            reopenedDefault.CurrentX == defaultAddedPosition.CurrentX &&
            reopenedDefault.CurrentY == defaultAddedPosition.CurrentY);

        var storedProject = new ProjectDocumentStore().Load(File.ReadAllText(projectPath));
        var storedComponent = storedProject.Layouts
            .SelectMany(layout => layout.Components)
            .Single(component => component.Id == persistedId);
        var storedDevice = storedProject.Devices.Single(device =>
            string.Equals(device.Id, storedComponent.BehaviorBindingId, StringComparison.Ordinal));
        Check(
            "boundDeviceStartsAtDropPosition",
            storedDevice.MountPosition.X == persistedPosition.CurrentX &&
            storedDevice.MountPosition.Y == persistedPosition.CurrentY);
        var storedDefault = storedProject.Layouts
            .SelectMany(layout => layout.Components)
            .Single(component => component.Id == defaultAddedId);
        var storedDefaultAxis = storedProject.Axes.Single(axis =>
            string.Equals(axis.Id, storedDefault.BehaviorBindingId, StringComparison.Ordinal));
        Check(
            "boundAxisStartsAtDefaultClickPosition",
            storedDefaultAxis.Position.X == defaultAddedPosition.CurrentX &&
            storedDefaultAxis.Position.Y == defaultAddedPosition.CurrentY);
        Check(
            "reopenDoesNotRun",
            viewModel.IsDesignMode && !viewModel.IsRunning &&
            !viewModel.UndoLayoutEditCommand.CanExecute(null));

        return new SmokeLibraryDropReport
        {
            Checks = checks,
            Failures = failures
        };
    }

    private static bool Overlaps(LayoutItem first, LayoutItem second)
    {
        if (first.Component is not { } firstComponent || second.Component is not { } secondComponent)
        {
            return false;
        }

        return Math.Abs(first.CurrentX - second.CurrentX) <
                   HorizontalHalfExtent(firstComponent) + HorizontalHalfExtent(secondComponent) &&
               Math.Abs(first.CurrentY - second.CurrentY) <
                   VerticalHalfExtent(firstComponent) + VerticalHalfExtent(secondComponent);
    }

    private static double HorizontalHalfExtent(LayoutComponentDefinition component)
    {
        var radians = component.Transform.RotationDegrees * Math.PI / 180d;
        return (Math.Abs(Math.Cos(radians)) * component.Size.Width / 2d) +
               (Math.Abs(Math.Sin(radians)) * component.Size.Height / 2d);
    }

    private static double VerticalHalfExtent(LayoutComponentDefinition component)
    {
        var radians = component.Transform.RotationDegrees * Math.PI / 180d;
        return (Math.Abs(Math.Sin(radians)) * component.Size.Width / 2d) +
               (Math.Abs(Math.Cos(radians)) * component.Size.Height / 2d);
    }
}
