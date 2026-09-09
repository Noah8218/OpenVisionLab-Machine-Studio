using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using OpenVisionLab.Machine.Core.Layouts;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.MachineStudio.View.Inspector;
using OpenVisionLab.MachineStudio.View.Scene;
using OpenVisionLab.MachineStudio.View.Shell;
using OpenVisionLab.MachineStudio.ViewModel;
using static OpenVisionLab.MachineStudio.SmokeRoundTripScenario;

namespace OpenVisionLab.MachineStudio;

internal sealed class SmokeLayerOrderReport
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

internal static class SmokeLayerOrderVerifier
{
    public static async Task<SmokeLayerOrderReport> VerifyAsync(
        ShellWindow window,
        MainViewModel viewModel,
        string reportPath,
        Func<DependencyObject, MachineSceneViewport?> findViewport,
        Func<DependencyObject, RightToolRegionView?> findInspector,
        Func<DependencyObject, Func<Button, bool>, Button?> findButton,
        Func<DependencyObject, Func<Border, bool>, Border?> findBorder)
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

        static void Execute(ICommand command, LayoutLayerOrder order)
        {
            var parameter = order.ToString();
            if (!command.CanExecute(parameter))
            {
                throw new InvalidOperationException($"Layer order command '{order}' was disabled.");
            }
            command.Execute(parameter);
        }

        IReadOnlyList<string> Order() => viewModel.Layout.Items
            .Where(item => item.Component is not null)
            .OrderBy(item => item.ZIndex)
            .ThenBy(item => item.Id, StringComparer.Ordinal)
            .Select(item => item.Id)
            .ToArray();

        IReadOnlyDictionary<string, int> ZState() => viewModel.Layout.Items
            .Where(item => item.Component is not null)
            .ToDictionary(item => item.Id, item => item.ZIndex, StringComparer.Ordinal);

        IReadOnlyDictionary<string, (double X, double Y, double Width, double Height, double Rotation)> GeometryState() =>
            viewModel.Layout.Items
                .Where(item => item.Component is not null)
                .ToDictionary(
                    item => item.Id,
                    item => (item.CurrentX, item.CurrentY, item.CurrentWidth, item.CurrentHeight, item.CurrentRotationDegrees),
                    StringComparer.Ordinal);

        static bool SameZ(
            IReadOnlyDictionary<string, int> expected,
            IReadOnlyDictionary<string, int> actual) =>
            expected.Count == actual.Count && expected.All(pair =>
                actual.TryGetValue(pair.Key, out var value) && value == pair.Value);

        static bool SameGeometry(
            IReadOnlyDictionary<string, (double X, double Y, double Width, double Height, double Rotation)> expected,
            IReadOnlyDictionary<string, (double X, double Y, double Width, double Height, double Rotation)> actual) =>
            expected.Count == actual.Count && expected.All(pair =>
                actual.TryGetValue(pair.Key, out var value) && value == pair.Value);

        var viewport = findViewport(window)
            ?? throw new InvalidOperationException("Machine scene viewport was not available.");
        var inspector = findInspector(window)
            ?? throw new InvalidOperationException("Right inspector was not available.");
        var layerPanel = findBorder(inspector, element => element.Name == "LayerOrderPanel")
            ?? throw new InvalidOperationException("Layer order panel was not available.");
        Button ButtonNamed(string name) => findButton(
                inspector,
                button => string.Equals(button.Name, name, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"Layer order button '{name}' was not available.");
        var sendToBackButton = ButtonNamed("SendToBackButton");
        var sendBackwardButton = ButtonNamed("SendBackwardButton");
        var bringForwardButton = ButtonNamed("BringForwardButton");
        var bringToFrontButton = ButtonNamed("BringToFrontButton");

        var reportDirectory = Path.GetDirectoryName(Path.GetFullPath(reportPath))!;
        Directory.CreateDirectory(reportDirectory);
        var baselinePath = Path.Combine(reportDirectory, "layer-order-baseline.ovmachine");
        await viewModel.SaveProjectAsync(baselinePath);
        Check("baselineOpen", await viewModel.OpenProjectAsync(baselinePath));

        viewModel.Layout.Select(RoundTripCylinderId);
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Check("layerPanelVisible", layerPanel.IsVisible);
        Check("fourLayerButtonsVisible", new[]
        {
            sendToBackButton,
            sendBackwardButton,
            bringForwardButton,
            bringToFrontButton
        }.All(button => button.IsVisible && !string.IsNullOrWhiteSpace(button.Content?.ToString())));
        Check("layerTooltipsAvailable", new[]
        {
            sendToBackButton,
            sendBackwardButton,
            bringForwardButton,
            bringToFrontButton
        }.All(button => !string.IsNullOrWhiteSpace(button.ToolTip?.ToString())));

        var stage = viewModel.Layout.Items.Single(item => item.Id == RoundTripStageId);
        var cylinder = viewModel.Layout.Items.Single(item => item.Id == RoundTripCylinderId);
        stage.CurrentX = cylinder.CurrentX;
        stage.CurrentY = cylinder.CurrentY;
        viewport.FitToLayout();
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        var overlapCenter = viewport.GetItemCenter(RoundTripCylinderId)
            ?? throw new InvalidOperationException("Overlapping item center was unavailable.");
        Check("initialTopHitUsesZIndex", viewport.SelectItemAt(overlapCenter) &&
            viewModel.Layout.SelectedItem?.Id == RoundTripCylinderId);

        var overlapGeometry = GeometryState();
        var bindingIds = viewModel.Layout.Items
            .Where(item => item.Component is not null)
            .ToDictionary(item => item.Id, item => item.BehaviorBindingId, StringComparer.Ordinal);
        viewModel.Layout.Select(RoundTripStageId);
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Check("uiButtonEnabled", bringToFrontButton.IsEnabled);
        var buttonPeer = new System.Windows.Automation.Peers.ButtonAutomationPeer(bringToFrontButton);
        var invokeProvider = buttonPeer.GetPattern(System.Windows.Automation.Peers.PatternInterface.Invoke)
            as System.Windows.Automation.Provider.IInvokeProvider
            ?? throw new InvalidOperationException("Bring to front button did not expose the invoke pattern.");
        invokeProvider.Invoke();
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Check("uiButtonChangesTopHit", viewport.SelectItemAt(overlapCenter) &&
            viewModel.Layout.SelectedItem?.Id == RoundTripStageId);
        Check("layerChangePreservesGeometry", SameGeometry(overlapGeometry, GeometryState()));
        Check("layerChangePreservesBindings", viewModel.Layout.Items
            .Where(item => item.Component is not null)
            .All(item => string.Equals(bindingIds[item.Id], item.BehaviorBindingId, StringComparison.Ordinal)));
        viewModel.UndoLayoutEditCommand.Execute(null);
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Check("uiButtonUndoRestoresTopHit", viewport.SelectItemAt(overlapCenter) &&
            viewModel.Layout.SelectedItem?.Id == RoundTripCylinderId);

        Check("restoreAfterOverlap", await viewModel.OpenProjectAsync(baselinePath));
        var originalZ = ZState();
        var originalOrder = Order();
        viewModel.Layout.Select(RoundTripCylinderId);
        var initialIndex = Array.IndexOf(originalOrder.ToArray(), RoundTripCylinderId);
        Execute(viewModel.ChangeLayoutLayerOrderCommand, LayoutLayerOrder.BringForward);
        var forwardOrder = Order();
        Check("singleForwardOneLayer", Array.IndexOf(forwardOrder.ToArray(), RoundTripCylinderId) == initialIndex + 1);
        Check("singleForwardCreatesHistory", viewModel.UndoLayoutEditCommand.CanExecute(null));
        viewModel.UndoLayoutEditCommand.Execute(null);
        Check("singleForwardUndo", SameZ(originalZ, ZState()));
        Check("singleForwardOneHistoryEntry", !viewModel.UndoLayoutEditCommand.CanExecute(null));
        viewModel.RedoLayoutEditCommand.Execute(null);
        Check("singleForwardRedo", Order().SequenceEqual(forwardOrder));
        viewModel.UndoLayoutEditCommand.Execute(null);

        Execute(viewModel.ChangeLayoutLayerOrderCommand, LayoutLayerOrder.SendBackward);
        Check("singleBackwardOneLayer", Array.IndexOf(Order().ToArray(), RoundTripCylinderId) == initialIndex - 1);
        viewModel.UndoLayoutEditCommand.Execute(null);
        Check("singleBackwardUndo", SameZ(originalZ, ZState()) && !viewModel.UndoLayoutEditCommand.CanExecute(null));

        var selectedIds = new[] { RoundTripStageId, RoundTripCylinderId };
        var selectedSet = selectedIds.ToHashSet(StringComparer.Ordinal);
        viewModel.Layout.SelectMany(selectedIds, RoundTripCylinderId);
        var selectedRelativeOrder = originalOrder.Where(selectedSet.Contains).ToArray();
        Execute(viewModel.ChangeLayoutLayerOrderCommand, LayoutLayerOrder.SendToBack);
        var backOrder = Order();
        Check("multiSendToBack", backOrder.Take(selectedIds.Length).All(selectedSet.Contains));
        Check("multiRelativeOrderPreservedAtBack", backOrder.Take(selectedIds.Length).SequenceEqual(selectedRelativeOrder));
        Check("multiSelectionPreserved", viewModel.Layout.SelectedItems.Select(item => item.Id).ToHashSet(StringComparer.Ordinal)
            .SetEquals(selectedSet));
        Check("multiSendToBackCreatesHistory", viewModel.UndoLayoutEditCommand.CanExecute(null));
        viewModel.UndoLayoutEditCommand.Execute(null);
        Check("multiSendToBackUndo", SameZ(originalZ, ZState()));
        Check("multiSendToBackOneHistoryEntry", !viewModel.UndoLayoutEditCommand.CanExecute(null));
        viewModel.RedoLayoutEditCommand.Execute(null);
        Check("multiSendToBackRedo", Order().SequenceEqual(backOrder));
        viewModel.UndoLayoutEditCommand.Execute(null);

        viewModel.Layout.SelectMany(selectedIds, RoundTripCylinderId);
        Execute(viewModel.ChangeLayoutLayerOrderCommand, LayoutLayerOrder.BringToFront);
        var frontOrder = Order();
        Check("multiBringToFront", frontOrder.TakeLast(selectedIds.Length).All(selectedSet.Contains));
        Check("multiRelativeOrderPreservedAtFront", frontOrder.TakeLast(selectedIds.Length).SequenceEqual(selectedRelativeOrder));
        Check("frontBoundaryDisablesForward", !viewModel.ChangeLayoutLayerOrderCommand.CanExecute(
            LayoutLayerOrder.BringForward.ToString()) &&
            !viewModel.ChangeLayoutLayerOrderCommand.CanExecute(LayoutLayerOrder.BringToFront.ToString()));
        Check("normalizedZIndexesUnique", ZState().Values.Distinct().Count() == viewModel.Layout.Items.Count);

        var persistedZ = ZState();
        var persistedBindings = viewModel.Layout.Items
            .Where(item => item.Component is not null)
            .ToDictionary(item => item.Id, item => item.BehaviorBindingId, StringComparer.Ordinal);
        viewModel.IsRunMode = true;
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Check("runModeHidesLayerControls", !layerPanel.IsVisible);
        Check("runModeBlocksLayerOrder", !viewModel.ChangeLayoutLayerOrderCommand.CanExecute(
            LayoutLayerOrder.SendToBack.ToString()));
        viewModel.IsRunMode = false;

        var roundTripPath = Path.Combine(reportDirectory, "layer-order-roundtrip.ovmachine");
        await viewModel.SaveProjectAsync(roundTripPath);
        Check("roundTripOpen", await viewModel.OpenProjectAsync(roundTripPath));
        Check("layerOrderPersists", SameZ(persistedZ, ZState()));
        Check("bindingsPersist", viewModel.Layout.Items
            .Where(item => item.Component is not null)
            .All(item => string.Equals(persistedBindings[item.Id], item.BehaviorBindingId, StringComparison.Ordinal)));
        Check("reopenStoppedAndHistoryCleared", viewModel.IsDesignMode && !viewModel.IsRunning &&
            !viewModel.UndoLayoutEditCommand.CanExecute(null));

        return new SmokeLayerOrderReport
        {
            Checks = checks,
            Failures = failures
        };
    }

}
