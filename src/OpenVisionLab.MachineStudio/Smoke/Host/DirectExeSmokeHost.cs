using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using OpenVisionLab;
using OpenVisionLab.Machine.Core.Axes;
using OpenVisionLab.Machine.Core.Channels;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Core.Sequences;
using OpenVisionLab.Machine.Core.Layouts;
using OpenVisionLab.Machine.Core.Devices;
using OpenVisionLab.Machine.Infrastructure.Integration;
using OpenVisionLab.Machine.Sequence.Authoring;
using OpenVisionLab.Machine.Simulation.Axis;
using OpenVisionLab.Machine.Simulation.Camera;
using OpenVisionLab.Machine.Simulation.Commands;
using OpenVisionLab.Machine.Simulation.Compilation;
using OpenVisionLab.Machine.Simulation.Commissioning;
using OpenVisionLab.Machine.Simulation.Engine;
using OpenVisionLab.Machine.Simulation.Faults;
using OpenVisionLab.Machine.Simulation.Layout;
using OpenVisionLab.Machine.Simulation.Scenarios;
using OpenVisionLab.Machine.Simulation.Sequences;
using OpenVisionLab.Machine.Simulation.Snapshots;
using OpenVisionLab.Machine.Simulation.Workpieces;
using OpenVisionLab.MachineStudio.Model;
using OpenVisionLab.MachineStudio.View;
using OpenVisionLab.MachineStudio.View.Diagnostics;
using OpenVisionLab.MachineStudio.View.Dialogs;
using OpenVisionLab.MachineStudio.View.Inspector;
using OpenVisionLab.MachineStudio.View.Project;
using OpenVisionLab.MachineStudio.View.Scene;
using OpenVisionLab.MachineStudio.View.Shell;
using OpenVisionLab.MachineStudio.View.Simulation;
using OpenVisionLab.MachineStudio.ViewModel;
using OpenVisionLab.Wpf.MessageDialogs;
using static OpenVisionLab.MachineStudio.DirectExeSmokeArgumentParser;
using static OpenVisionLab.MachineStudio.SmokeProjectTreeQuery;
using static OpenVisionLab.MachineStudio.SmokeRoundTripScenario;
using static OpenVisionLab.MachineStudio.SmokeVisualTreeQuery;

namespace OpenVisionLab.MachineStudio;

internal static class DirectExeSmokeHost
{
    public static bool IsRequested(IReadOnlyList<string> args) =>
        DirectExeSmokeArgumentParser.IsRequested(args);

    public static async Task RunAsync(string[] args)
    {
        using var nativeInput = new SmokeNativeInput();
        var buildIdentityReportPath = GetArgumentValue(args, "--build-identity-report");
        if (!string.IsNullOrWhiteSpace(buildIdentityReportPath))
        {
            BuildIdentity.SaveReport(buildIdentityReportPath);
            Console.WriteLine($"Build identity report saved: {Path.GetFullPath(buildIdentityReportPath)}");
            Application.Current?.Shutdown(0);
            return;
        }

        var smokeOptions = DirectExeSmokeArgumentParser.ParseSmokeOptions(args);
        var startupPerfStopwatch = smokeOptions.PerformSmokePerf ? Stopwatch.StartNew() : null;

        if (HasArgument(args, "--fault-project") ||
            HasArgument(args, "--fault-scenario") ||
            HasArgument(args, "--fault-report"))
        {
            var exitCode = await DirectExeFaultScenarioHost.RunAsync(args);
            Application.Current?.Shutdown(exitCode);
            return;
        }

        DirectExeSmokeArgumentParser.ValidateSmokeArguments(args);
        if (!string.IsNullOrWhiteSpace(smokeOptions.SmokeLanguage))
        {
            OpenVisionLanguageService.SetLanguage(
                smokeOptions.SmokeLanguage.Equals("en", StringComparison.OrdinalIgnoreCase)
                    ? OpenVisionLanguage.English
                    : OpenVisionLanguage.Korean,
                save: false);
        }
        var (width, height) = smokeOptions.WindowSize;

        var initialProjectLoad = DirectExeSmokeProjectLoader.Load(
            smokeOptions.ProjectPath,
            smokeOptions.CameraFirstUseRequested,
            smokeOptions.StartupChoiceState);
        var initialProject = initialProjectLoad.Project;
        var initialProjectPath = initialProjectLoad.InitialProjectPath;
        var startupSamplePath = initialProjectLoad.StartupSamplePath;
        var mmiFixture = string.IsNullOrWhiteSpace(smokeOptions.MmiOperatorState)
            ? null
            : PrepareMmiOperatorSmokeFixture(smokeOptions.MmiOperatorReportPath!);

        var vm = new MainViewModel(
            initialProject,
            initialProjectPath,
            startupSamplePath,
            mmiFixture?.SettingsPath);
        if (smokeOptions.IsSmokeRun)
        {
            vm.UnsavedProjectPrompt = () => UnsavedProjectDecision.Discard;
        }

        var window = new ShellWindow
        {
            DataContext = vm,
            Width = width,
            Height = height,
            WindowStartupLocation = WindowStartupLocation.CenterScreen
        };

        if (smokeOptions.IsSmokeRun)
        {
            SmokeDpiTestHook.PlaceOnTestMonitor(window, width, height);
        }

        window.Show();
        SmokeDpiTestHook.Apply(window, smokeOptions.DpiScalePercent, width, height);
        if (smokeOptions.UseRunLayout)
        {
            vm.IsRunMode = true;
            vm.Navigation.IsSimulationWorkspace = true;
        }
        if (!string.IsNullOrWhiteSpace(smokeOptions.EquipmentOutlineSearchText))
        {
            vm.Layout.EquipmentOutlineSearchText = smokeOptions.EquipmentOutlineSearchText;
        }

        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        await Task.Delay(250);
        var windowCapture = new SmokeWindowCapture();
        var uiInteraction = CreateUiInteraction(window, windowCapture, nativeInput);

        if (GetArgumentValue(args, "--smoke-exit-report") is { } exitReportPath)
        {
            Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var exitReport = await SmokeProjectSafetyVerifier.VerifyExitAsync(window, vm, exitReportPath, windowCapture, nativeInput);
            Application.Current.Shutdown(exitReport.IsValid ? 0 : 1);
            return;
        }

        if (!string.IsNullOrWhiteSpace(smokeOptions.AnalogIoAuthoringState))
        {
            var analogAuthoringReport = await SmokeAnalogIoAuthoringVerifier.VerifyAsync(
                window,
                vm,
                smokeOptions.AnalogIoAuthoringState,
                smokeOptions.AnalogIoAuthoringSavePath,
                smokeOptions.ScreenshotPath,
                root => FindVisualDescendant<RightToolRegionView>(root),
                root => FindVisualDescendant<SceneDocumentView>(root),
                windowCapture.Capture);
            analogAuthoringReport.Save(smokeOptions.AnalogIoAuthoringReportPath!);
            Console.WriteLine(
                $"Analog I/O authoring smoke " +
                $"{(analogAuthoringReport.IsValid ? "passed" : "failed")}. ");
            foreach (var failure in analogAuthoringReport.Failures)
            {
                Console.Error.WriteLine($"  - {failure}");
            }

            nativeInput.ReleasePointer();
            Application.Current?.Shutdown(analogAuthoringReport.IsValid ? 0 : 25);
            return;
        }

        if (!string.IsNullOrWhiteSpace(smokeOptions.StartupChoiceState)
            && !smokeOptions.StartupChoiceState.Equals("idle", StringComparison.OrdinalIgnoreCase))
        {
            var buttonName = smokeOptions.StartupChoiceState.StartsWith("sample", StringComparison.OrdinalIgnoreCase)
                ? "StartSampleButton"
                : smokeOptions.StartupChoiceState.StartsWith("open", StringComparison.OrdinalIgnoreCase)
                ? "OpenRecipeButton"
                : "StartBlankLayoutButton";
            var button = FindVisualDescendant<Button>(
                window,
                candidate => string.Equals(candidate.Name, buttonName, StringComparison.Ordinal))
                ?? throw new InvalidOperationException("Startup choice button was not available.");
            button.Focus();
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            nativeInput.MovePointerToCenter(button);
            await Task.Delay(100);
            AssertSmoke(button.IsMouseOver, "Startup choice button did not enter hover state.");
            if (smokeOptions.StartupChoiceState.EndsWith("pressed", StringComparison.OrdinalIgnoreCase))
            {
                nativeInput.PressLeftButton();
                nativeInput.MarkPointerHeld();
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                AssertSmoke(button.IsPressed, "Startup choice button did not enter pointer-down state.");
            }
        }

        if (!string.IsNullOrWhiteSpace(smokeOptions.CommandTracePath)
            || smokeOptions.CommandTraceStateSpecified)
        {
            await SmokeRuntimeEvidenceVerifier.VerifyCommandTraceAsync(
                window,
                vm,
                smokeOptions.CommandTracePath,
                smokeOptions.CommandTraceState,
                uiInteraction);
        }

        var startupToIdleMs = startupPerfStopwatch?.Elapsed.TotalMilliseconds;
        SmokeProjectRoundTripReport? roundTripReport = null;
        SmokeLayoutAlignmentReport? layoutAlignmentReport = null;
        SmokeLayoutHistoryReport? layoutHistoryReport = null;
        SmokeDirectSceneAuthoringReport? directSceneReport = null;
        SmokeCanvasNavigationReport? canvasNavigationReport = null;
        SmokeDirectTransformReport? directTransformReport = null;
        SmokeMultiSelectionTransformReport? multiTransformReport = null;
        SmokeLibraryDropReport? libraryDropReport = null;
        SmokeLayerOrderReport? layerOrderReport = null;
        SmokeFaultManagerReport? faultManagerReport = null;
        SmokeRuntimeDebuggerReport? runtimeDebuggerReport = null;
        SmokeDigitalIoCommissioningReport? digitalIoCommissioningReport = null;
        SmokeCameraCommissioningReport? cameraCommissioningReport = null;
        SmokeIntegrationResultReport? integrationPanelReport = null;
        SmokeMmiOperatorLayoutReport? mmiOperatorReport = null;
        SmokeAxisCommissioningReport? axisCommissioningReport = null;
        SmokeMultiAxisCommissioningReport? multiAxisRecipeReport = null;
        SmokeCylinderCommissioningReport? cylinderCommissioningReport = null;
        SmokeConveyorCommissioningReport? conveyorCommissioningReport = null;
        SmokeSensorCommissioningReport? sensorCommissioningReport = null;
        SmokeRecipeGalleryReport? recipeGalleryReport = null;
        SmokeConnectionWorkbenchReport? connectionWorkbenchDefaultReport = null;
        SmokeLoadLockSetupReport? loadLockSetupReport = null;
        SmokeStationSkeletonReport? stationSkeletonReport = null;
        SmokeWorkflowReport? connectionWorkbenchReport = null;
        SmokeCameraFirstUseReport? cameraFirstUseReport = null;
        SmokeProjectSafetyReport? projectSafetyReport = null;
        SmokeProjectDiagnosticsReport? projectDiagnosticsReport = null;
        SmokeSupportDiagnosticsReport? supportDiagnosticsReport = null;

        if (!string.IsNullOrWhiteSpace(smokeOptions.RecipeGalleryState))
        {
            recipeGalleryReport = await SmokeRecipeGalleryVerifier.VerifyAsync(
                window,
                vm,
                smokeOptions.RecipeGalleryState,
                initialProject,
                smokeOptions.RecipeGalleryCopyPath,
                smokeOptions.RecipeGalleryCompatibilityReportPath,
                smokeOptions.RecipeGalleryBaselineReportPath,
                smokeOptions.RecipeGalleryCurrentReportPath,
                smokeOptions.RecipeGalleryExpectFailure,
                (root, predicate) => FindVisualDescendant<Button>(root, predicate),
                () =>
                {
                    window.Activate();
                    nativeInput.ActivateWindow(window);
                },
                nativeInput.MovePointerToCenter,
                nativeInput.SendMouseEvent,
                nativeInput.MarkPointerHeld);
            if (!string.IsNullOrWhiteSpace(smokeOptions.RecipeGalleryReportPath))
            {
                recipeGalleryReport.Save(smokeOptions.RecipeGalleryReportPath);
            }

            Console.WriteLine(
                $"Recipe gallery smoke {(recipeGalleryReport.IsValid ? "passed" : "failed")}.");
        }

        if (smokeOptions.CameraFirstUseRequested)
        {
            var effectiveCameraFirstUseState = smokeOptions.CameraFirstUseState ?? "applied";
            cameraFirstUseReport = await SmokeCameraFirstUseVerifier.VerifyAsync(
                window,
                vm,
                effectiveCameraFirstUseState,
                smokeOptions.CameraFirstUseSavePath,
                root => FindVisualDescendant<RecipeConnectionWorkbenchView>(root),
                (root, predicate) => FindVisualDescendant<Button>(root, predicate),
                (root, predicate) => FindVisualDescendant<Border>(root, predicate),
                () =>
                {
                    window.Activate();
                    nativeInput.ActivateWindow(window);
                },
                nativeInput.MovePointerToCenter,
                nativeInput.SendMouseEvent,
                nativeInput.MarkPointerHeld,
                windowCapture.SetPopupContent,
                nativeInput.ReleasePointer);
            if (!string.IsNullOrWhiteSpace(smokeOptions.CameraFirstUseReportPath))
            {
                cameraFirstUseReport.Save(smokeOptions.CameraFirstUseReportPath);
            }

            Console.WriteLine(
                $"Camera first-use smoke {effectiveCameraFirstUseState} " +
                $"{(cameraFirstUseReport.IsValid ? "passed" : "failed")}.");
        }

        connectionWorkbenchDefaultReport = await DirectExeConnectionWorkbenchWorkflow.VerifyDefaultAsync(
            window,
            vm,
            initialProject,
            smokeOptions.ConnectionWorkbenchState,
            smokeOptions.ConnectionWorkbenchReportPath,
            smokeOptions.ConnectionWorkbenchSavePath);
        if (!string.IsNullOrWhiteSpace(smokeOptions.DocumentTab))
        {
            if (smokeOptions.DocumentTab is "Simulation Workspace" or "Sequence")
            {
                vm.Navigation.IsSimulationWorkspace = true;
                vm.Navigation.SelectedExecutionTabIndex = smokeOptions.DocumentTab == "Sequence" ? 1 : 0;
            }
            else
            {
                var document = FindVisualDescendant<SceneDocumentView>(window)
                    ?? throw new InvalidOperationException("Scene document view was not available.");
                var tabs = FindVisualDescendant<TabControl>(document)
                    ?? throw new InvalidOperationException("Document tabs were not available.");
                var localizedDocumentTab = smokeOptions.DocumentTab switch
                {
                    "Machine Layout" => OpenVisionLanguageService.T("Shell.MachineLayout"),
                    "Simulation Workspace" => OpenVisionLanguageService.T("Shell.SimulationWorkspace"),
                    "Sequence" => OpenVisionLanguageService.T("Shell.Sequence"),
                    "Connections" => OpenVisionLanguageService.T("Connections.Tab"),
                    _ => smokeOptions.DocumentTab
                };
                var tab = tabs.Items.OfType<TabItem>().FirstOrDefault(item =>
                    string.Equals(item.Header?.ToString(), smokeOptions.DocumentTab, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(
                        item.Header?.ToString(),
                        localizedDocumentTab,
                        StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException(
                        $"Document tab '{smokeOptions.DocumentTab}' was not available.");
                tab.IsSelected = true;
                if (Equals(tab.Header, OpenVisionLanguageService.T("Connections.Tab"))) vm.Navigation.IsInspectionWorkspace = true;
                else if (Equals(tab.Header, OpenVisionLanguageService.T("Workspace.Results"))) vm.Navigation.IsResultsWorkspace = true;
                else if (Equals(tab.Header, OpenVisionLanguageService.T("Shell.MachineLayout"))) vm.Navigation.IsEquipmentWorkspace = true;
            }
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            await Task.Delay(100);
        }

        if (!string.IsNullOrWhiteSpace(smokeOptions.ConnectionWorkbenchState))
        {
            var connectionWorkbenchResult = await DirectExeConnectionWorkbenchWorkflow.ApplyAsync(
                window,
                vm,
                initialProject,
                smokeOptions.ProjectPath,
                smokeOptions.ConnectionWorkbenchState,
                smokeOptions.ConnectionWorkbenchReportPath,
                smokeOptions.ConnectionWorkbenchSavePath,
                uiInteraction);
            loadLockSetupReport = connectionWorkbenchResult.LoadLockSetupReport;
            stationSkeletonReport = connectionWorkbenchResult.StationSkeletonReport;
            connectionWorkbenchReport = connectionWorkbenchResult.WorkflowReport;
        }

        if (!string.IsNullOrWhiteSpace(smokeOptions.LeftToolTab))
        {
            var leftTools = FindVisualDescendant<LeftToolRegionView>(window)
                ?? throw new InvalidOperationException("Left tool region was not available.");
            var tabs = FindVisualDescendant<TabControl>(leftTools)
                ?? throw new InvalidOperationException("Left tool tabs were not available.");
            var localizedLeftToolTab = smokeOptions.LeftToolTab switch
            {
                "Project" => OpenVisionLanguageService.T("Shell.Project"),
                "Library" => OpenVisionLanguageService.T("Shell.Library"),
                _ => smokeOptions.LeftToolTab
            };
            var tab = tabs.Items.OfType<TabItem>().FirstOrDefault(item =>
                string.Equals(item.Header?.ToString(), smokeOptions.LeftToolTab, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(
                    item.Header?.ToString(),
                    localizedLeftToolTab,
                    StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException(
                    $"Left tool tab '{smokeOptions.LeftToolTab}' was not available.");
            tab.IsSelected = true;
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            await Task.Delay(100);
        }

        if (smokeOptions.LibraryEntryState is { } libraryEntryState
            && (libraryEntryState.Equals("tab", StringComparison.OrdinalIgnoreCase)
                || libraryEntryState.Equals("equipment-tab", StringComparison.OrdinalIgnoreCase)
                || libraryEntryState.Equals("equipment-dialog", StringComparison.OrdinalIgnoreCase)))
        {
            if (!libraryEntryState.Equals("tab", StringComparison.OrdinalIgnoreCase))
            {
                vm.Navigation.SelectedDocumentTabIndex = 0;
            }

            var unitId = vm.ProjectStations
                .SelectMany(station => station.Units)
                .Select(unit => unit.Id)
                .FirstOrDefault()
                ?? throw new InvalidOperationException("The library entry smoke requires a project unit.");
            vm.ShowEquipmentUnitCommand.Execute(unitId);
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            if (libraryEntryState.Equals("equipment-dialog", StringComparison.OrdinalIgnoreCase))
            {
                var openLibraryCommand = vm.OpenEquipmentComponentLibraryDialogCommand;
                var canOpenLibrary = openLibraryCommand.CanExecute(null);
                AssertSmoke(
                    canOpenLibrary,
                    "Equipment component dialog could not open: " +
                    $"editable={vm.IsSceneEditable}, equipment={vm.Navigation.IsEquipmentWorkspace}, " +
                    $"activeUnitId={vm.Layout.ActiveUnitId ?? "<none>"}, " +
                    $"stationEditor={vm.IsStationUnitEditorOpen}, bulkMoveEditor={vm.IsBulkMoveEditorOpen}.");
                openLibraryCommand.Execute(null);
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                AssertSmoke(
                    vm.IsEquipmentComponentLibraryDialogOpen,
                    "Equipment component dialog command completed without opening its ViewModel state.");
                var dialogView = FindVisualDescendant<EquipmentComponentLibraryDialogView>(window)
                    ?? throw new InvalidOperationException("Equipment component dialog view was not available.");
                AssertSmoke(
                    dialogView.Visibility == Visibility.Visible && dialogView.IsVisible,
                    "Equipment component dialog ViewModel state was open, but its View was not visible.");
                AssertSmoke(
                    dialogView.ActualWidth >= window.ActualWidth - 1
                    && dialogView.ActualHeight >= window.ActualHeight - 1,
                    $"Equipment component dialog did not cover the shell: " +
                    $"view={dialogView.ActualWidth:F0}x{dialogView.ActualHeight:F0}, " +
                    $"window={window.ActualWidth:F0}x{window.ActualHeight:F0}.");
            }
            else
            {
                vm.Navigation.OpenComponentLibraryCommand.Execute(null);
            }
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        }

        if (smokeOptions.LibrarySearchText is not null)
        {
            vm.Layout.LibrarySearchText = smokeOptions.LibrarySearchText;
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        }

        if (smokeOptions.LibraryEntryState?.Equals("equipment-dialog", StringComparison.OrdinalIgnoreCase) == true)
        {
            var dialogView = FindVisualDescendant<EquipmentComponentLibraryDialogView>(window)
                ?? throw new InvalidOperationException("Equipment component dialog view was not available.");
            var searchBox = FindVisualDescendant<TextBox>(dialogView, candidate =>
                string.Equals(candidate.Name, "LibrarySearchTextBox", StringComparison.Ordinal));
            if (!string.IsNullOrWhiteSpace(smokeOptions.ScreenshotPath))
            {
                var screenshotDirectory = Path.GetDirectoryName(Path.GetFullPath(smokeOptions.ScreenshotPath))!;
                windowCapture.Capture(window, Path.Combine(screenshotDirectory, "diagnostic-before-assert.png"));
            }
            AssertSmoke(
                searchBox is { IsVisible: true }
                && string.Equals(searchBox.Text, smokeOptions.LibrarySearchText, StringComparison.Ordinal)
                && vm.Layout.FilteredLibraryItems.Count == 1,
                $"Equipment component dialog search did not render its filtered library result: " +
                $"found={searchBox is not null}, visible={searchBox?.IsVisible}, " +
                $"text={searchBox?.Text ?? "<missing>"}, " +
                $"filteredCount={vm.Layout.FilteredLibraryItems.Count}, " +
                $"viewSize={dialogView.ActualWidth:F0}x{dialogView.ActualHeight:F0}.");
            var centerHit = window.InputHitTest(new Point(window.ActualWidth / 2, window.ActualHeight / 2))
                as DependencyObject;
            AssertSmoke(
                IsVisualDescendantOf(dialogView, centerHit),
                $"Equipment component dialog was not the topmost center hit target; " +
                $"hit={centerHit?.GetType().Name ?? "<none>"}.");
        }

        if (!string.IsNullOrWhiteSpace(smokeOptions.LibraryCardState))
        {
            var leftTools = FindVisualDescendant<LeftToolRegionView>(window)
                ?? throw new InvalidOperationException("Left tool region was not available.");
            var libraryList = FindVisualDescendant<ListBox>(
                leftTools,
                candidate => ReferenceEquals(candidate.ItemsSource, vm.Layout.LibraryItems))
                ?? throw new InvalidOperationException("Layout library list was not available.");
            AssertSmoke(
                libraryList.Items.Count == vm.Layout.LibraryItems.Count,
                $"Expected {vm.Layout.LibraryItems.Count} library cards; found {libraryList.Items.Count}.");
            libraryList.ScrollIntoView(libraryList.Items[^1]);
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var lastContainer = libraryList.ItemContainerGenerator.ContainerFromIndex(libraryList.Items.Count - 1) as ListBoxItem;
            AssertSmoke(lastContainer?.IsVisible == true, "The last library card was not reachable by scrolling.");

            libraryList.ScrollIntoView(libraryList.Items[0]);
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var firstContainer = libraryList.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem
                ?? throw new InvalidOperationException("The first library card container was not available.");
            var firstCard = FindVisualDescendant<Button>(firstContainer)
                ?? throw new InvalidOperationException("The first library card was not available.");
            firstCard.Focus();
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            nativeInput.MovePointerToCenter(firstCard);
            await Task.Delay(100);
            AssertSmoke(firstCard.IsMouseOver, "The layout library card did not enter hover state.");
            if (smokeOptions.LibraryCardState.Equals("pressed", StringComparison.OrdinalIgnoreCase))
            {
                nativeInput.PressLeftButton();
                nativeInput.MarkPointerHeld();
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                AssertSmoke(firstCard.IsPressed, "The layout library card did not enter pointer-down state.");
            }
            else if (!smokeOptions.LibraryCardState.Equals("hover", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(
                    $"Unsupported --smoke-library-card-state '{smokeOptions.LibraryCardState}'. Expected hover or pressed.");
            }
        }

        if (!string.IsNullOrWhiteSpace(smokeOptions.LibraryDefaultAddKind))
        {
            if (!Enum.TryParse<LayoutComponentKind>(smokeOptions.LibraryDefaultAddKind, ignoreCase: true, out var kind))
            {
                throw new ArgumentException(
                    $"Unsupported or unavailable --smoke-library-default-add '{smokeOptions.LibraryDefaultAddKind}'.");
            }

            if (vm.IsEquipmentComponentLibraryDialogOpen)
            {
                var project = vm.ProjectTree.Roots.Single().Model as MachineProjectDocument
                    ?? throw new InvalidOperationException("The Equipment library smoke project was not available.");
                var componentCountBefore = project.Layouts.Sum(layout => layout.Components.Count);
                if (!string.IsNullOrWhiteSpace(smokeOptions.ScreenshotPath))
                {
                    var screenshotDirectory = Path.GetDirectoryName(Path.GetFullPath(smokeOptions.ScreenshotPath))!;
                    windowCapture.Capture(
                        window,
                        Path.Combine(screenshotDirectory, $"before-library-add-{kind}.png"));
                }

                vm.AddEquipmentComponentFromLibraryCommand.Execute(kind);
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                var componentCountAfter = project.Layouts.Sum(layout => layout.Components.Count);
                var added = componentCountAfter == componentCountBefore + 1
                    && !vm.IsEquipmentComponentLibraryDialogOpen;
                var rejected = componentCountAfter == componentCountBefore
                    && vm.IsEquipmentComponentLibraryDialogOpen
                    && !string.IsNullOrWhiteSpace(vm.EquipmentComponentLibraryDialogStatusText);
                AssertSmoke(
                    added || rejected,
                    "Equipment component dialog add produced neither one component nor a visible rejected-add reason.");
            }
            else if (!vm.TryAddLayoutComponent(kind))
            {
                throw new ArgumentException(
                    $"Unsupported or unavailable --smoke-library-default-add '{smokeOptions.LibraryDefaultAddKind}'.");
            }

            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        }

        if (!string.IsNullOrWhiteSpace(smokeOptions.SequenceState))
        {
            await SmokeSequenceStateVerifier.ApplyAsync(window, vm, smokeOptions.SequenceState, uiInteraction);
        }

        if (!string.IsNullOrWhiteSpace(smokeOptions.RoundTripSavePath))
        {
            var stage = vm.Layout.Items.Single(item =>
                string.Equals(item.Id, RoundTripStageId, StringComparison.Ordinal));
            stage.CurrentX = RoundTripStageX;
            vm.Layout.Select(RoundTripCylinderId);
            var componentEditor = vm.Layout.SelectedComponentEditor
                ?? throw new InvalidOperationException("Cylinder property editor was not available.");
            componentEditor.Name = RoundTripCylinderName;
            componentEditor.RotationDegrees = RoundTripCylinderRotation;
            componentEditor.Width = RoundTripCylinderWidth;
            componentEditor.Height = RoundTripCylinderHeight;
            componentEditor.CylinderExtendDurationMilliseconds = RoundTripCylinderExtendDuration;
            componentEditor.CylinderStroke = RoundTripCylinderStroke;
            if (componentEditor.HasValidationErrors)
            {
                throw new InvalidOperationException(
                    $"Edited cylinder properties were invalid: {componentEditor.ValidationMessage}");
            }
            vm.Layout.SelectMany(
                new[] { RoundTripAlignedComponentId, RoundTripCylinderId },
                RoundTripCylinderId);
            vm.Layout.AlignSelection(LayoutSelectionAlignment.HorizontalCenter);
            var step = vm.SequenceEditor.Steps.Single(item =>
                string.Equals(item.Id, RoundTripStepId, StringComparison.Ordinal));
            step.Name = RoundTripStepName;
            step.HasExpectedState = true;
            step.ExpectedTargetId = RoundTripStepCheckpointTargetId;
            step.ExpectedState = RoundTripStepCheckpointState;
            vm.SimulationWorkspace.SelectedScenarioProfile =
                vm.SimulationWorkspace.ScenarioProfiles.Single(profile =>
                    string.Equals(profile.ProfileId, RoundTripScenarioProfileId, StringComparison.Ordinal));
            vm.SimulationWorkspace.ScenarioSeed = RoundTripScenarioSeed;
            vm.SimulationWorkspace.ScenarioDurationCycles = RoundTripScenarioDuration;
            vm.SimulationWorkspace.ScenarioTargetId = RoundTripScenarioTargetId;
            SelectNode(vm.ProjectTree, "x");
            var axisEditor = vm.AxisDriveTuningEditor
                ?? throw new InvalidOperationException("Axis drive tuning editor was not available.");
            axisEditor.MaxVelocity = RoundTripAxisMaxVelocity;
            axisEditor.MaxAcceleration = RoundTripAxisMaxAcceleration;
            axisEditor.MaxDeceleration = RoundTripAxisMaxDeceleration;
            axisEditor.FollowingErrorLimit = RoundTripAxisFollowingErrorLimit;
            if (axisEditor.HasValidationErrors)
            {
                throw new InvalidOperationException(
                    $"Edited axis tuning was invalid: {axisEditor.ValidationMessage}");
            }

            await vm.SaveProjectAsync(smokeOptions.RoundTripSavePath);
            if (!await vm.OpenProjectAsync(smokeOptions.RoundTripSavePath))
            {
                throw new InvalidOperationException("Saved project could not be reloaded.");
            }

            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            roundTripReport = SmokeProjectRoundTripVerifier.CreateReport(
                "SaveReload",
                smokeOptions.RoundTripSavePath,
                window,
                vm);
        }
        else if (smokeOptions.VerifyRoundTrip)
        {
            if (string.IsNullOrWhiteSpace(smokeOptions.ProjectPath))
            {
                throw new ArgumentException(
                    "--smoke-project is required with --smoke-roundtrip-verify.");
            }

            roundTripReport = SmokeProjectRoundTripVerifier.CreateReport(
                "Reopen",
                smokeOptions.ProjectPath,
                window,
                vm);
        }

        if (roundTripReport is not null)
        {
            roundTripReport.Save(smokeOptions.RoundTripReportPath!);
            Console.WriteLine(
                $"Project round trip {roundTripReport.Phase} " +
                $"{(roundTripReport.IsValid ? "passed" : "failed")}.");
            foreach (var failure in roundTripReport.Failures)
            {
                Console.Error.WriteLine($"  - {failure}");
            }
        }

        if (!string.IsNullOrEmpty(smokeOptions.SelectPath))
        {
            var selected = SelectNode(vm.ProjectTree, smokeOptions.SelectPath);
            if (selected is not null)
            {
                selected.IsSelected = true;
            }
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        }

        if (!string.IsNullOrWhiteSpace(smokeOptions.LayoutSelectId))
        {
            vm.Layout.Select(smokeOptions.LayoutSelectId);
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        }

        if (smokeOptions.UseTopView)
        {
            vm.Layout.ShowTopViewCommand.Execute(null);
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        }

        if (!string.IsNullOrWhiteSpace(smokeOptions.LayoutSelectMany))
        {
            var selectionIds = smokeOptions.LayoutSelectMany.Split(
                ',',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (selectionIds.Length < 2)
            {
                throw new ArgumentException(
                    "--smoke-layout-select-many requires at least two comma-separated ids.");
            }

            SelectLayoutItemsThroughScene(window, vm, selectionIds);
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);

            if (!string.IsNullOrWhiteSpace(smokeOptions.LayoutAlignmentReportPath))
            {
                layoutAlignmentReport = SmokeLayoutAlignmentVerifier.Verify(
                    vm.Layout,
                    selectionIds,
                    smokeOptions.LayoutAlignment ?? nameof(LayoutSelectionAlignment.HorizontalCenter));
                layoutAlignmentReport.Save(smokeOptions.LayoutAlignmentReportPath);
            }
            else if (!string.IsNullOrWhiteSpace(smokeOptions.LayoutAlignment))
            {
                if (!Enum.TryParse(smokeOptions.LayoutAlignment, out LayoutSelectionAlignment alignment))
                {
                    throw new ArgumentException(
                        $"Unsupported --smoke-layout-align '{smokeOptions.LayoutAlignment}'.");
                }
                vm.Layout.AlignSelection(alignment);
            }
        }

        if (GetArgumentValue(args, "--smoke-layout-delete-dialog-screenshot") is { } removalDialogPath)
        {
            var components = vm.Layout.SelectedItems.Select(item => item.Component)
                .OfType<LayoutComponentDefinition>().ToArray();
            if (initialProject is null || components.Length == 0)
            {
                throw new ArgumentException("A loaded project and layout selection are required for the removal dialog capture.");
            }

            var ids = components.Select(component => component.Id).ToArray();
            var impacts = new LayoutComponentAuthoringService().GetRemovalImpacts(initialProject, ids);
            var dialog = new WpfMessageDialogWindow(
                MainMessageDialogHost.CreateLayoutRemovalDialogOptions(components, impacts))
            {
                Owner = window,
                WindowStartupLocation = WindowStartupLocation.CenterOwner
            };
            dialog.Show();
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            windowCapture.Capture(dialog, removalDialogPath);
            if (GetArgumentValue(args, "--smoke-layout-delete-dialog-bottom-screenshot") is { } bottomPath)
            {
                var messageScroll = FindVisualDescendant<ScrollViewer>(dialog)
                    ?? throw new InvalidOperationException("The removal message scroll region was not found.");
                AssertSmoke(messageScroll.ScrollableHeight > 0, "The long removal impact list was not scrollable.");
                messageScroll.ScrollToBottom();
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                windowCapture.Capture(dialog, bottomPath);
            }
            dialog.Close();
        }

        if (!string.IsNullOrWhiteSpace(smokeOptions.AxisTuningState))
        {
            await SmokeAxisTuningStateVerifier.ApplyAsync(window, vm, smokeOptions.AxisTuningState, uiInteraction);
        }

        if (!string.IsNullOrWhiteSpace(smokeOptions.LayoutPropertyState))
        {
            if (string.IsNullOrWhiteSpace(smokeOptions.LayoutSelectId) && string.IsNullOrWhiteSpace(smokeOptions.LayoutSelectMany))
            {
                throw new ArgumentException(
                    "--smoke-layout-property-state requires a layout selection.");
            }

            if (vm.OpenEquipmentPropertiesCommand.CanExecute(null))
            {
                vm.OpenEquipmentPropertiesCommand.Execute(null);
            }
            await SmokeLayoutPropertyStateVerifier.ApplyAsync(window, vm, smokeOptions.LayoutPropertyState, uiInteraction);
        }

        if (!string.IsNullOrWhiteSpace(smokeOptions.LayoutHistoryReportPath))
        {
            layoutHistoryReport = await SmokeLayoutHistoryVerifier.VerifyAsync(vm, smokeOptions.LayoutHistoryReportPath);
            layoutHistoryReport.Save(smokeOptions.LayoutHistoryReportPath);
        }

        if (!string.IsNullOrWhiteSpace(smokeOptions.DirectSceneReportPath))
        {
            directSceneReport = await SmokeDirectSceneAuthoringVerifier.VerifyAsync(
                window,
                vm,
                smokeOptions.DirectSceneReportPath,
                root => FindVisualDescendant<MachineSceneViewport>(root));
            directSceneReport.Save(smokeOptions.DirectSceneReportPath);
        }

        if (!string.IsNullOrWhiteSpace(smokeOptions.CanvasNavigationReportPath))
        {
            canvasNavigationReport = await SmokeCanvasNavigationVerifier.VerifyAsync(
                window,
                vm,
                root => FindVisualDescendant<MachineSceneViewport>(root),
                root => FindVisualDescendant<SceneDocumentView>(root));
            canvasNavigationReport.Save(smokeOptions.CanvasNavigationReportPath);
        }

        if (!string.IsNullOrWhiteSpace(smokeOptions.DirectTransformReportPath))
        {
            directTransformReport = await SmokeDirectTransformVerifier.VerifyAsync(
                window,
                vm,
                smokeOptions.DirectTransformReportPath,
                root => FindVisualDescendant<MachineSceneViewport>(root),
                root => FindVisualDescendant<RightToolRegionView>(root));
            directTransformReport.Save(smokeOptions.DirectTransformReportPath);
        }

        if (!string.IsNullOrWhiteSpace(smokeOptions.MultiTransformReportPath))
        {
            multiTransformReport = await SmokeMultiSelectionTransformVerifier.VerifyAsync(
                window,
                vm,
                smokeOptions.MultiTransformReportPath,
                root => FindVisualDescendant<MachineSceneViewport>(root));
            multiTransformReport.Save(smokeOptions.MultiTransformReportPath);
        }

        if (!string.IsNullOrWhiteSpace(smokeOptions.LibraryDropReportPath))
        {
            libraryDropReport = await SmokeLibraryDropVerifier.VerifyAsync(
                window,
                vm,
                smokeOptions.LibraryDropReportPath,
                root => FindVisualDescendant<MachineSceneViewport>(root));
            libraryDropReport.Save(smokeOptions.LibraryDropReportPath);
        }

        if (!string.IsNullOrWhiteSpace(smokeOptions.LayerOrderReportPath))
        {
            layerOrderReport = await SmokeLayerOrderVerifier.VerifyAsync(
                window,
                vm,
                smokeOptions.LayerOrderReportPath,
                root => FindVisualDescendant<MachineSceneViewport>(root),
                root => FindVisualDescendant<RightToolRegionView>(root),
                (root, predicate) => FindVisualDescendant<Button>(root, predicate),
                (root, predicate) => FindVisualDescendant<Border>(root, predicate));
            layerOrderReport.Save(smokeOptions.LayerOrderReportPath);
        }

        if (!string.IsNullOrWhiteSpace(smokeOptions.EditMenuState))
        {
            var editMenuPopup = await SmokeEditMenuStateVerifier.ApplyAsync(
                window,
                vm,
                smokeOptions.EditMenuState,
                uiInteraction);
            if (editMenuPopup is not null)
            {
                windowCapture.SetPopupContent(editMenuPopup);
            }
        }

        if (!string.IsNullOrWhiteSpace(smokeOptions.DirectSceneGestureState))
        {
            await ApplyDirectSceneGestureStateAsync(window, smokeOptions.DirectSceneGestureState, nativeInput);
        }

        if (!string.IsNullOrWhiteSpace(smokeOptions.LayoutClickId))
        {
            var viewport = FindVisualDescendant<MachineSceneViewport>(window)
                ?? throw new InvalidOperationException("Machine scene viewport was not available.");
            var point = viewport.GetItemCenter(smokeOptions.LayoutClickId)
                ?? throw new InvalidOperationException(
                    $"Layout item '{smokeOptions.LayoutClickId}' was not visible in the machine scene.");
            if (!viewport.SelectItemAt(point)
                || vm.Layout.SelectedItem is not { } selectedLayoutItem
                || !string.Equals(selectedLayoutItem.Id, smokeOptions.LayoutClickId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Scene hit test did not select layout item '{smokeOptions.LayoutClickId}'.");
            }

            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Console.WriteLine($"Scene hit test selected: {selectedLayoutItem.Name}");
        }

        var globalCommandSmokeHandled = false;
        if (smokeOptions.StartSimulation)
        {
            vm.IsRunMode = true;
            for (var attempt = 0; attempt < 20 && !vm.RunCommand.CanExecute(null); attempt++)
            {
                await Task.Delay(50);
            }

            if (!vm.RunCommand.CanExecute(null))
            {
                throw new InvalidOperationException("Simulation ON was unavailable during the smoke run.");
            }

            if (string.Equals(smokeOptions.GlobalCommandState, "abort", StringComparison.OrdinalIgnoreCase))
            {
                vm.StepCommand.Execute(null);
                for (var attempt = 0;
                     attempt < 40 && !vm.AbortSequenceCommand.CanExecute(null);
                     attempt++)
                {
                    await Task.Delay(50);
                }

                if (!vm.AbortSequenceCommand.CanExecute(null))
                {
                    throw new InvalidOperationException(
                        "Sequence abort command was not available after simulation start.");
                }

                await SmokeGlobalCommandStateVerifier.ApplyAsync(window, vm, "abort", uiInteraction);
                globalCommandSmokeHandled = true;
            }
            else if (string.Equals(smokeOptions.GlobalCommandState, "retry", StringComparison.OrdinalIgnoreCase))
            {
                vm.StepCommand.Execute(null);
                var faultedState = OpenVisionLanguageService.T("Equipment.State.Faulted");
                for (var attempt = 0;
                     attempt < 40
                     && (!vm.CanRetrySequence
                         || !string.Equals(vm.CurrentSequenceStateText, faultedState, StringComparison.Ordinal));
                     attempt++)
                {
                    await Task.Delay(50);
                }

                if (!vm.CanRetrySequence)
                {
                    throw new InvalidOperationException(
                        "Sequence retry command was not available after the deterministic fault.");
                }

                await SmokeGlobalCommandStateVerifier.ApplyAsync(window, vm, "retry", uiInteraction);
                globalCommandSmokeHandled = true;
            }
            else
            {
                vm.RunCommand.Execute(null);
                await Task.Delay(900);
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            }
        }

        if (!string.IsNullOrWhiteSpace(smokeOptions.PickPlaceState))
        {
            if (!smokeOptions.StartSimulation)
            {
                throw new ArgumentException(
                    "--smoke-pick-place-state requires --smoke-start-simulation.");
            }

            await SmokePickAndPlaceStateVerifier.ApplyAsync(window, vm, smokeOptions.PickPlaceState);
        }

        if (smokeOptions.TestConditionScenario)
        {
            await SmokeTestScenarioRuntimeVerifier.VerifyConditionScenarioAsync(
                window,
                vm,
                smokeOptions.StartSimulation);
        }

        if (smokeOptions.TestAxisFaultScenario)
        {
            await SmokeTestScenarioRuntimeVerifier.VerifyAxisFaultScenarioAsync(
                vm,
                smokeOptions.StartSimulation,
                smokeOptions.AxisFaultPersistencePath);
        }

        if (smokeOptions.ShowTestScenarioSettings)
        {
            if (!smokeOptions.UseRunLayout)
            {
                throw new ArgumentException(
                    "--smoke-test-scenario-settings requires --smoke-run-layout.");
            }

            vm.Navigation.IsSimulationWorkspace = true;
            vm.Navigation.SelectedExecutionTabIndex = 2;
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var testScenarioAnchor = FindVisualDescendant<TextBlock>(
                window,
                textBlock => string.Equals(
                    textBlock.Text,
                    OpenVisionLanguageService.T("Simulation.TestScenario"),
                    StringComparison.Ordinal));
            if (testScenarioAnchor is null)
            {
                throw new InvalidOperationException("Test Scenario settings were not visible.");
            }

            testScenarioAnchor.BringIntoView();
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var testWorkspace = FindVisualDescendant<
                OpenVisionLab.MachineStudio.View.Simulation.SimulationTestWorkspaceView>(window)
                ?? throw new InvalidOperationException("Test workspace was unavailable.");

            var settingsState = smokeOptions.TestScenarioSettingsState ?? "normal";
            vm.SimulationWorkspace.RequireAutomaticCycleCompleted = true;
            vm.SimulationWorkspace.MinimumCompletedCycles = 1;
            vm.SimulationWorkspace.RequireNoActiveFaults = true;
            vm.SimulationWorkspace.RequireFinalEquipmentState = !settingsState.Equals(
                "disabled",
                StringComparison.OrdinalIgnoreCase);
            vm.SimulationWorkspace.FinalEquipmentTargetId = "cylinder-1";
            vm.SimulationWorkspace.FinalEquipmentExpectedState = settingsState.Equals(
                "validation",
                StringComparison.OrdinalIgnoreCase)
                ? string.Empty
                : "Extended";
            vm.SimulationWorkspace.IsScheduledFaultEnabled = !settingsState.Equals(
                "disabled",
                StringComparison.OrdinalIgnoreCase);
            vm.SimulationWorkspace.ScheduledFaultKind = smokeOptions.TestScenarioFaultKind?.ToLowerInvariant() switch
            {
                "input" => SimulationFaultKind.StuckDigitalInput,
                "cylinder" => SimulationFaultKind.CylinderTravelBlocked,
                null or "axis" => SimulationFaultKind.AxisMotionBlocked,
                _ => throw new ArgumentException(
                    $"Unsupported --smoke-test-scenario-fault-kind '{smokeOptions.TestScenarioFaultKind}'. " +
                    "Expected input, cylinder, or axis.")
            };
            vm.SimulationWorkspace.ScenarioDurationCycles = Math.Max(
                vm.SimulationWorkspace.ScenarioDurationCycles,
                500);
            vm.SimulationWorkspace.ScheduledFaultTargetId ??= vm.ScheduledFaultTargets.FirstOrDefault()?.Id;
            vm.SimulationWorkspace.ScheduledFaultInjectTick = 403;
            vm.SimulationWorkspace.ScheduledFaultHoldTicks = 3;
            vm.SimulationWorkspace.RestartSequenceAfterFault = true;
            vm.SimulationWorkspace.RecoverySequenceId ??= vm.RecoverySequences.FirstOrDefault()?.Id;
            if (settingsState.Equals("validation", StringComparison.OrdinalIgnoreCase))
            {
                vm.SimulationWorkspace.ScheduledFaultInjectTick =
                    vm.SimulationWorkspace.ScenarioDurationCycles - 1;
            }

            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            testWorkspace.ScenarioAssertionsSectionAnchor.BringIntoView();
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            testWorkspace.FinalEquipmentExpectedStateTextBox.BringIntoView();
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            if (!settingsState.Equals("disabled", StringComparison.OrdinalIgnoreCase)
                && !settingsState.Equals("validation", StringComparison.OrdinalIgnoreCase)
                && (testWorkspace.ScheduledFaultInjectTickTextBox.Text != "403"
                    || testWorkspace.ScheduledFaultHoldTicksTextBox.Text != "3"
                    || testWorkspace.MinimumCompletedCyclesTextBox.Text != "1"
                    || testWorkspace.FinalEquipmentExpectedStateTextBox.Text != "Extended"))
            {
                throw new InvalidOperationException(
                    "Test Scenario values were not rendered from the current settings.");
            }

            switch (settingsState.ToLowerInvariant())
            {
                case "normal":
                    if (!vm.SimulationWorkspace.IsScheduledFaultConfigurationValid
                        || !vm.SimulationWorkspace.IsAssertionConfigurationValid
                        || testWorkspace.ScheduledFaultValidationText.IsVisible
                        || testWorkspace.ScenarioAssertionValidationText.IsVisible)
                    {
                        throw new InvalidOperationException("Valid Test Scenario settings were not rendered normally.");
                    }
                    break;
                case "focus":
                    window.Activate();
                    testWorkspace.FinalEquipmentExpectedStateTextBox.Focus();
                    break;
                case "hover":
                    nativeInput.MovePointerToCenter(testWorkspace.FinalEquipmentStateAssertionCheckBox);
                    break;
                case "pressed":
                    window.Activate();
                    testWorkspace.FinalEquipmentStateAssertionCheckBox.Focus();
                    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    nativeInput.MovePointerToCenter(testWorkspace.FinalEquipmentStateAssertionCheckBox);
                    nativeInput.PressLeftButton();
                    nativeInput.MarkPointerHeld();
                    break;
                case "disabled":
                    if (testWorkspace.ScheduledFaultTargetComboBox.IsEnabled
                        || testWorkspace.FinalEquipmentTargetComboBox.IsEnabled
                        || testWorkspace.FinalEquipmentExpectedStateTextBox.IsEnabled)
                    {
                        throw new InvalidOperationException("Disabled Test Scenario settings remained interactive.");
                    }
                    break;
                case "validation":
                    if (vm.SimulationWorkspace.IsScheduledFaultConfigurationValid
                        || vm.SimulationWorkspace.IsAssertionConfigurationValid
                        || !testWorkspace.ScheduledFaultValidationText.IsVisible
                        || !testWorkspace.ScenarioAssertionValidationText.IsVisible)
                    {
                        throw new InvalidOperationException("Invalid Test Scenario settings were not surfaced.");
                    }
                    testWorkspace.ScenarioAssertionValidationText.BringIntoView();
                    break;
                case "open-popup":
                    window.Activate();
                    testWorkspace.FinalEquipmentTargetComboBox.Focus();
                    testWorkspace.FinalEquipmentTargetComboBox.ApplyTemplate();
                    testWorkspace.FinalEquipmentTargetComboBox.IsDropDownOpen = true;
                    await window.Dispatcher.InvokeAsync(
                        () => { },
                        DispatcherPriority.ApplicationIdle);
                    if (!testWorkspace.FinalEquipmentTargetComboBox.IsDropDownOpen)
                    {
                        throw new InvalidOperationException("Assertion equipment popup did not open.");
                    }
                    var windowRoot = PresentationSource.FromVisual(window)?.RootVisual;
                    windowCapture.SetPopupContent(PresentationSource.CurrentSources
                        .Cast<PresentationSource>()
                        .Select(source => source.RootVisual)
                        .OfType<FrameworkElement>()
                        .FirstOrDefault(root =>
                            !ReferenceEquals(root, windowRoot)
                            && root.IsVisible
                            && root.ActualWidth > 0
                        && root.ActualHeight > 0)
                        ?? throw new InvalidOperationException("Assertion equipment popup content was unavailable."));
                    break;
                default:
                    throw new ArgumentException(
                        $"Unsupported --smoke-test-scenario-settings-state '{settingsState}'. " +
                        "Expected normal, focus, hover, pressed, disabled, validation, or open-popup.");
            }

            await Task.Delay(100);
            Console.WriteLine($"Test Scenario settings smoke passed: {settingsState}.");
        }

        if (smokeOptions.TestScenarioBatch)
        {
            if (!smokeOptions.UseRunLayout)
            {
                throw new ArgumentException(
                    "--smoke-test-scenario-batch requires --smoke-run-layout.");
            }

            vm.Navigation.IsSimulationWorkspace = true;
            vm.Navigation.SelectedExecutionTabIndex = 2;
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            await SmokeScenarioBatchVerifier.VerifyAsync(
                window,
                vm,
                smokeOptions.ProjectPath,
                smokeOptions.ScenarioEvidenceExchangePath,
                smokeOptions.ScenarioEvidenceExchangeState,
                smokeOptions.ScenarioReportPath,
                smokeOptions.ScenarioReportState,
                smokeOptions.UnifiedCommissioningEvidencePath,
                smokeOptions.UnifiedCommissioningEvidenceState,
                windowCapture,
                smokeOptions.ScreenshotPath,
                uiInteraction);

            var repeatValidationAnchor = FindVisualDescendant<TextBlock>(
                window,
                textBlock => string.Equals(
                    textBlock.Name,
                    "RepeatValidationSectionAnchor",
                    StringComparison.Ordinal));
            repeatValidationAnchor?.BringIntoView();
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var testWorkspace = FindVisualDescendant<
                OpenVisionLab.MachineStudio.View.Simulation.SimulationTestWorkspaceView>(window);
            testWorkspace?.ScenarioAssertionOutcomesPanel.BringIntoView();
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            await Task.Delay(100);
        }

        if (smokeOptions.SaveBatchPersistence)
        {
            if (!smokeOptions.UseRunLayout || string.IsNullOrWhiteSpace(smokeOptions.ProjectPath))
            {
                throw new ArgumentException(
                    "--smoke-batch-persistence-save requires --smoke-run-layout and --smoke-project.");
            }

            await SmokeBatchPersistenceVerifier.VerifySaveAndReloadAsync(vm, smokeOptions.ProjectPath);
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        }

        if (smokeOptions.VerifyBatchPersistence && !vm.HasRestoredBatchArtifacts)
        {
            throw new InvalidOperationException(
                "Saved batch evidence did not restore in a new application process.");
        }

        if (smokeOptions.VerifyStaleBatchPersistence && !vm.RejectedStaleBatchArtifacts)
        {
            throw new InvalidOperationException(
                "Changed project or scenario evidence was not rejected as stale.");
        }

        if (smokeOptions.SaveBatchPersistence || smokeOptions.VerifyBatchPersistence || smokeOptions.VerifyStaleBatchPersistence)
        {
            vm.IsRunMode = true;
            vm.Navigation.IsSimulationWorkspace = true;
            vm.Navigation.SelectedExecutionTabIndex = 2;
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var repeatValidationAnchor = FindVisualDescendant<TextBlock>(
                window,
                textBlock => string.Equals(
                    textBlock.Name,
                    "RepeatValidationSectionAnchor",
                    StringComparison.Ordinal));
            repeatValidationAnchor?.BringIntoView();
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var testWorkspace = FindVisualDescendant<
                OpenVisionLab.MachineStudio.View.Simulation.SimulationTestWorkspaceView>(window);
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            await Task.Delay(100);
        }

        if (!string.IsNullOrWhiteSpace(smokeOptions.CylinderFaultTargetId))
        {
            vm.FaultManager.SelectedKind = vm.FaultManager.AvailableKinds.Single(option =>
                option.Kind == SimulationFaultKind.CylinderTravelBlocked);
            vm.FaultManager.SelectedTarget = vm.FaultManager.Targets.FirstOrDefault(target =>
                string.Equals(target.Id, smokeOptions.CylinderFaultTargetId, StringComparison.Ordinal))
                ?? throw new InvalidOperationException(
                    $"Cylinder fault target '{smokeOptions.CylinderFaultTargetId}' was not available.");
            if (!vm.FaultManager.InjectCommand.CanExecute(null))
            {
                throw new InvalidOperationException("Cylinder fault injection was unavailable.");
            }

            vm.FaultManager.InjectCommand.Execute(null);
            for (var attempt = 0;
                 attempt < 20 && !vm.FaultManager.ActiveFaults.Any(fault =>
                     fault.Kind == SimulationFaultKind.CylinderTravelBlocked
                     && string.Equals(fault.TargetId, smokeOptions.CylinderFaultTargetId, StringComparison.Ordinal));
                 attempt++)
            {
                await Task.Delay(50);
            }

            if (!vm.FaultManager.ActiveFaults.Any(fault =>
                    fault.Kind == SimulationFaultKind.CylinderTravelBlocked
                    && string.Equals(fault.TargetId, smokeOptions.CylinderFaultTargetId, StringComparison.Ordinal)))
            {
                throw new InvalidOperationException("Cylinder fault was not published in a runtime snapshot.");
            }

            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        }

        if (!string.IsNullOrWhiteSpace(smokeOptions.RuntimeDebuggerReportPath))
        {
            runtimeDebuggerReport = await SmokeRuntimeDebuggerVerifier.VerifyAsync(
                window,
                vm,
                root => FindVisualDescendant<RightToolRegionView>(root),
                targetWindow =>
                {
                    targetWindow.Activate();
                    nativeInput.ActivateWindow(targetWindow);
                },
                nativeInput.MovePointerToCenter,
                () =>
                {
                    nativeInput.PressLeftButton();
                    nativeInput.MarkPointerHeld();
                },
                nativeInput.ReleasePointer,
                smokeOptions.RuntimeDebuggerState,
                nativeInput,
                windowCapture,
                smokeOptions.RuntimeDebuggerReportPath);
            runtimeDebuggerReport.Save(smokeOptions.RuntimeDebuggerReportPath);
        }

        if (!string.IsNullOrWhiteSpace(smokeOptions.FaultManagerReportPath))
        {
            faultManagerReport = await SmokeFaultManagerVerifier.VerifyAsync(
                window,
                vm,
                root => FindVisualDescendant<RightToolRegionView>(root),
                activeSection => SmokeFaultManagerVerifier.ScrollIntoViewAsync(window, activeSection));
            faultManagerReport.Save(smokeOptions.FaultManagerReportPath);
        }

        if (!string.IsNullOrWhiteSpace(smokeOptions.FaultManagerState))
        {
            await SmokeFaultManagerVerifier.ApplyStateAsync(
                window,
                vm,
                smokeOptions.FaultManagerState,
                uiInteraction);
        }

        if (!string.IsNullOrWhiteSpace(smokeOptions.DigitalIoCommissioningReportPath))
        {
            digitalIoCommissioningReport = await SmokeDigitalIoCommissioningVerifier.VerifyAsync(
                window,
                vm,
                smokeOptions.ProjectPath,
                () => SmokeDigitalIoCommissioningVerifier.ScrollIntoViewAsync(window));
            digitalIoCommissioningReport.Save(smokeOptions.DigitalIoCommissioningReportPath);
        }

        if (!string.IsNullOrWhiteSpace(smokeOptions.DigitalIoCommissioningState))
        {
            await SmokeDigitalIoCommissioningVerifier.ApplyStateAsync(
                window,
                vm,
                smokeOptions.DigitalIoCommissioningState,
                uiInteraction);
        }

        if (!string.IsNullOrWhiteSpace(smokeOptions.CameraCommissioningReportPath))
        {
            cameraCommissioningReport = await SmokeCameraCommissioningVerifier.VerifyAsync(
                window,
                vm,
                smokeOptions.ProjectPath,
                smokeOptions.EditCameraImageSource,
                () => SmokeCameraCommissioningVerifier.ScrollIntoViewAsync(window));
            cameraCommissioningReport.Save(smokeOptions.CameraCommissioningReportPath);
        }

        if (!string.IsNullOrWhiteSpace(smokeOptions.CameraCommissioningState))
        {
            await SmokeCameraCommissioningVerifier.ApplyStateAsync(
                window,
                vm,
                smokeOptions.CameraCommissioningState,
                uiInteraction);
        }

        if (!string.IsNullOrWhiteSpace(smokeOptions.IntegrationPanelState))
        {
            integrationPanelReport = await SmokeIntegrationResultVerifier.VerifyAsync(
                window,
                vm,
                smokeOptions.IntegrationPanelState,
                smokeOptions.IntegrationExchangeRoot,
                root => FindVisualDescendant<RightToolRegionView>(root),
                root => FindVisualDescendant<SimulationIntegrationWorkspaceView>(root));
            if (!string.IsNullOrWhiteSpace(smokeOptions.IntegrationPanelReportPath))
            {
                integrationPanelReport.Save(smokeOptions.IntegrationPanelReportPath);
            }
        }

        if (!string.IsNullOrWhiteSpace(smokeOptions.MmiOperatorState))
        {
            mmiOperatorReport = await SmokeMmiOperatorLayoutVerifier.VerifyAsync(
                window,
                vm,
                smokeOptions.MmiOperatorState,
                mmiFixture!.SettingsPath,
                Path.GetDirectoryName(Path.GetFullPath(smokeOptions.MmiOperatorReportPath!))!,
                uiInteraction,
                nativeInput,
                windowCapture);
            mmiOperatorReport.Save(smokeOptions.MmiOperatorReportPath!);
            Console.WriteLine(
                $"Machine Studio inspection interaction smoke "
                + $"{(mmiOperatorReport.IsValid ? "passed" : "failed")}. ");
            foreach (var failure in mmiOperatorReport.Failures)
            {
                Console.Error.WriteLine($"  - {failure}");
            }
        }

        if (!string.IsNullOrWhiteSpace(smokeOptions.AxisCommissioningReportPath))
        {
            axisCommissioningReport = await SmokeAxisCommissioningVerifier.VerifyAsync(
                window,
                vm,
                root => FindVisualDescendant<RightToolRegionView>(root),
                () => SmokeAxisCommissioningStateVerifier.ScrollIntoViewAsync(window));
            axisCommissioningReport.Save(smokeOptions.AxisCommissioningReportPath);
        }

        if (!string.IsNullOrWhiteSpace(smokeOptions.AxisCommissioningState))
        {
            await SmokeAxisCommissioningStateVerifier.ApplyStateAsync(
                window,
                vm,
                smokeOptions.AxisCommissioningState,
                uiInteraction);
        }

        if (!string.IsNullOrWhiteSpace(smokeOptions.MultiAxisRecipeReportPath))
        {
            multiAxisRecipeReport = await SmokeMultiAxisCommissioningVerifier.VerifyAsync(
                window,
                vm,
                smokeOptions.MultiAxisRecipeSavePath,
                root => FindVisualDescendant<MachineSceneViewport>(root));
            multiAxisRecipeReport.Save(smokeOptions.MultiAxisRecipeReportPath);
        }

        if (!string.IsNullOrWhiteSpace(smokeOptions.MultiAxisRecipeState))
        {
            await SmokeMultiAxisCommissioningStateVerifier.ApplyStateAsync(
                window,
                vm,
                smokeOptions.MultiAxisRecipeState,
                uiInteraction);
        }

        if (!string.IsNullOrWhiteSpace(smokeOptions.CylinderCommissioningReportPath))
        {
            cylinderCommissioningReport = await SmokeCylinderCommissioningVerifier.VerifyAsync(
                window,
                vm,
                root => FindVisualDescendant<RightToolRegionView>(root),
                () => SmokeCylinderCommissioningVerifier.ScrollIntoViewAsync(window));
            cylinderCommissioningReport.Save(smokeOptions.CylinderCommissioningReportPath);
        }

        if (!string.IsNullOrWhiteSpace(smokeOptions.CylinderCommissioningState))
        {
            await SmokeCylinderCommissioningVerifier.ApplyStateAsync(
                window,
                vm,
                smokeOptions.CylinderCommissioningState,
                uiInteraction);
        }

        if (!string.IsNullOrWhiteSpace(smokeOptions.ConveyorCommissioningReportPath))
        {
            conveyorCommissioningReport = await SmokeConveyorCommissioningVerifier.VerifyAsync(
                window,
                vm,
                root => FindVisualDescendant<RightToolRegionView>(root),
                () => SmokeConveyorCommissioningVerifier.ScrollIntoViewAsync(window));
            conveyorCommissioningReport.Save(smokeOptions.ConveyorCommissioningReportPath);
        }

        if (!string.IsNullOrWhiteSpace(smokeOptions.ConveyorCommissioningState))
        {
            await SmokeConveyorCommissioningVerifier.ApplyStateAsync(
                window,
                vm,
                smokeOptions.ConveyorCommissioningState,
                uiInteraction);
        }

        if (!string.IsNullOrWhiteSpace(smokeOptions.SensorCommissioningReportPath))
        {
            sensorCommissioningReport = await SmokeSensorCommissioningVerifier.VerifyAsync(
                window,
                vm,
                root => FindVisualDescendant<RightToolRegionView>(root),
                () => SmokeSensorCommissioningVerifier.ScrollIntoViewAsync(window));
            sensorCommissioningReport.Save(smokeOptions.SensorCommissioningReportPath);
        }

        if (!string.IsNullOrWhiteSpace(smokeOptions.SensorCommissioningState))
        {
            await SmokeSensorCommissioningVerifier.ApplyStateAsync(
                window,
                vm,
                smokeOptions.SensorCommissioningState,
                uiInteraction);
        }

        if (!string.IsNullOrWhiteSpace(smokeOptions.EvidenceDrawerState))
        {
            await SmokeEvidenceDrawerStateVerifier.ApplyAsync(
                window,
                vm,
                smokeOptions.EvidenceDrawerState,
                uiInteraction);
        }

        if (!string.IsNullOrWhiteSpace(smokeOptions.GlobalCommandState) && !globalCommandSmokeHandled)
        {
            await SmokeGlobalCommandStateVerifier.ApplyAsync(
                window,
                vm,
                smokeOptions.GlobalCommandState,
                uiInteraction,
                windowCapture,
                smokeOptions.GlobalCommandStateScreenshotPath);
        }

        if (vm.SelectedEquipmentStatus is { } selectedEquipmentStatus)
        {
            Console.WriteLine(
                $"Selected equipment status: {selectedEquipmentStatus.Name} | " +
                $"{selectedEquipmentStatus.StateText} | {selectedEquipmentStatus.ConditionText}");
        }

        if (!string.IsNullOrWhiteSpace(smokeOptions.ProjectSafetyReportPath))
        {
            projectSafetyReport = await SmokeProjectSafetyVerifier.VerifyAsync(
                window,
                vm,
                smokeOptions.ProjectSafetySavePath!,
                smokeOptions.UnsavedDialogScreenshotPath,
                smokeOptions.ProjectOpenFailureDialogScreenshotPath,
                smokeOptions.DpiScalePercent,
                (root, predicate) => FindVisualDescendant<TextBlock>(root, predicate),
                (root, predicate) => FindVisualDescendant<Button>(root, predicate),
                target =>
                {
                    target.Activate();
                    nativeInput.ActivateWindow(target);
                },
                nativeInput.CheckPointerOwnership,
                nativeInput.MovePointerToCenter,
                nativeInput.SendMouseEvent,
                nativeInput.MarkPointerHeld,
                (x, y) =>
                    nativeInput.SetCursorPosition(x, y),
                Mouse.Synchronize,
                nativeInput.ReleasePointer,
                (target, dpi, targetWidth, targetHeight) =>
                    SmokeDpiTestHook.Apply(target, dpi, targetWidth, targetHeight),
                SmokeDpiTestHook.CaptureMonitorEvidence,
                windowCapture.Capture,
                nativeInput.SendKey);
            if (!string.IsNullOrWhiteSpace(smokeOptions.ProjectSafetyReportPath))
            {
                projectSafetyReport.Save(smokeOptions.ProjectSafetyReportPath);
            }

            Console.WriteLine(
                $"Project safety smoke {(projectSafetyReport.IsValid ? "passed" : "failed")}.");
        }

        if (!string.IsNullOrWhiteSpace(smokeOptions.ProjectDiagnosticsReportPath))
        {
            if (string.IsNullOrWhiteSpace(smokeOptions.ProjectPath))
            {
                throw new ArgumentException(
                    "--smoke-project is required with --smoke-project-diagnostics-report.");
            }

            projectDiagnosticsReport = await SmokeProjectDiagnosticsVerifier.VerifyAsync(
                window,
                vm,
                smokeOptions.ProjectPath,
                nativeInput,
                windowCapture,
                smokeOptions.ProjectDiagnosticsScreenshotPath);
            projectDiagnosticsReport.Save(smokeOptions.ProjectDiagnosticsReportPath);
            Console.WriteLine(
                $"Project diagnostics smoke {(projectDiagnosticsReport.IsValid ? "passed" : "failed") }.");
            foreach (var failure in projectDiagnosticsReport.Failures)
            {
                Console.Error.WriteLine($"  - {failure}");
            }
        }

        if (!string.IsNullOrWhiteSpace(smokeOptions.SupportDiagnosticsReportPath))
        {
            supportDiagnosticsReport = await SmokeSupportDiagnosticsVerifier.VerifyAsync(
                window,
                vm,
                smokeOptions.SupportDiagnosticsExportPath!,
                nativeInput,
                windowCapture,
                smokeOptions.SupportDiagnosticsScreenshotPath);
            supportDiagnosticsReport.Save(smokeOptions.SupportDiagnosticsReportPath);
            Console.WriteLine(
                $"Support diagnostics smoke {(supportDiagnosticsReport.IsValid ? "passed" : "failed") }.");
            foreach (var failure in supportDiagnosticsReport.Failures)
            {
                Console.Error.WriteLine($"  - {failure}");
            }
        }

        if (!string.IsNullOrWhiteSpace(smokeOptions.UnifiedCommissioningEvidencePath))
        {
            var unifiedEvidenceAnchor = FindVisualDescendant<TextBlock>(
                window,
                candidate => string.Equals(
                    candidate.Name,
                    "UnifiedCommissioningEvidenceSectionAnchor",
                    StringComparison.Ordinal));
            unifiedEvidenceAnchor?.BringIntoView();
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        }

        if (GetArgumentValue(args, "--smoke-workspace") is { } workspace)
        {
            await SmokeWorkspaceNavigationVerifier.ApplyAsync(window, vm, workspace,
                GetArgumentValue(args, "--smoke-workspace-report"), nativeInput, windowCapture);
        }

        SmokeLayoutReport? layoutReport = null;
        if (!string.IsNullOrEmpty(smokeOptions.LayoutReportPath))
        {
            layoutReport = SmokeLayoutValidator.Validate(
                window,
                width,
                height,
                smokeOptions.DpiScalePercent);
            layoutReport.Save(smokeOptions.LayoutReportPath);
            Console.WriteLine(
                $"Layout validation {(layoutReport.IsValid ? "passed" : "failed")}: " +
                $"{smokeOptions.SizeArgument} at {smokeOptions.DpiScalePercent}% DPI.");
            foreach (var failure in layoutReport.Failures)
            {
                Console.Error.WriteLine($"  - {failure}");
            }
        }

        SmokePerformanceReport? smokePerfReport = null;
        if (smokeOptions.PerformSmokePerf)
        {
            smokePerfReport = await SmokePerformanceVerifier.MeasureAsync(
                window,
                vm,
                smokeOptions.SizeArgument,
                smokeOptions.DpiScalePercent,
                startupToIdleMs ?? 0,
                smokeOptions.SmokePerfSampleCount,
                smokeOptions.SmokePerfSampleCount);

            if (!string.IsNullOrWhiteSpace(smokeOptions.SmokePerfReportPath))
            {
                smokePerfReport.Save(smokeOptions.SmokePerfReportPath);
            }

            Console.WriteLine(
                $"Startup-to-idle: {smokePerfReport.StartupToIdleMs:F2} ms");
            Console.WriteLine(
                $"Navigation mean/p95: {smokePerfReport.NavigationMeanMs:F2} / " +
                $"{smokePerfReport.NavigationP95Ms:F2} ms");
            Console.WriteLine(
                $"Steady interaction mean/p95: " +
                $"{smokePerfReport.SteadyInteractionMeanMs:F2} / " +
                $"{smokePerfReport.SteadyInteractionP95Ms:F2} ms");
        }

        if (!string.IsNullOrEmpty(smokeOptions.ScreenshotPath))
        {
            windowCapture.Capture(window, smokeOptions.ScreenshotPath);
        }

        if (!string.IsNullOrEmpty(smokeOptions.ScreenshotPath) ||
            !string.IsNullOrEmpty(smokeOptions.GlobalCommandStateScreenshotPath) ||
            !string.IsNullOrEmpty(smokeOptions.LayoutReportPath) ||
            !string.IsNullOrEmpty(smokeOptions.SmokePerfReportPath) ||
            roundTripReport is not null ||
            layoutAlignmentReport is not null ||
            layoutHistoryReport is not null ||
            directSceneReport is not null ||
            canvasNavigationReport is not null ||
            directTransformReport is not null ||
            multiTransformReport is not null ||
            libraryDropReport is not null ||
            layerOrderReport is not null ||
            runtimeDebuggerReport is not null ||
            faultManagerReport is not null ||
            digitalIoCommissioningReport is not null ||
            cameraCommissioningReport is not null ||
            integrationPanelReport is not null ||
            mmiOperatorReport is not null ||
            axisCommissioningReport is not null ||
            multiAxisRecipeReport is not null ||
            cylinderCommissioningReport is not null ||
            conveyorCommissioningReport is not null ||
            sensorCommissioningReport is not null ||
            recipeGalleryReport is not null ||
            connectionWorkbenchDefaultReport is not null ||
            loadLockSetupReport is not null ||
            stationSkeletonReport is not null ||
            connectionWorkbenchReport is not null ||
            cameraFirstUseReport is not null ||
            projectSafetyReport is not null ||
            projectDiagnosticsReport is not null ||
            supportDiagnosticsReport is not null ||
            smokeOptions.PerformSmokePerf)
        {
            if (string.Equals(smokeOptions.CameraFirstUseState, "pressed", StringComparison.OrdinalIgnoreCase)
                && nativeInput.IsPointerHeld)
            {
                var workbench = FindVisualDescendant<RecipeConnectionWorkbenchView>(window);
                var cancelTarget = workbench is null
                    ? null
                    : FindVisualDescendant<Button>(workbench, candidate =>
                        string.Equals(candidate.Name, "AddConnectionStageButton", StringComparison.Ordinal));
                if (cancelTarget is not null)
                {
                    nativeInput.MovePointerToCenter(cancelTarget);
                    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                }
            }
            var smokePointerWasHeld = nativeInput.IsPointerHeld;
            nativeInput.ReleasePointer();
            if (smokePointerWasHeld)
            {
                await window.Dispatcher.InvokeAsync(Mouse.Synchronize, DispatcherPriority.Input);
                await Task.Delay(50);
            }

            var exitCode = DirectExeSmokeFailurePolicy.SelectExitCode(
                new DirectExeSmokeFailureCheck(layoutReport?.IsValid ?? true, 3),
                new DirectExeSmokeFailureCheck(roundTripReport?.IsValid ?? true, 4),
                new DirectExeSmokeFailureCheck(layoutAlignmentReport?.IsValid ?? true, 5),
                new DirectExeSmokeFailureCheck(layoutHistoryReport?.IsValid ?? true, 6),
                new DirectExeSmokeFailureCheck(directSceneReport?.IsValid ?? true, 7),
                new DirectExeSmokeFailureCheck(canvasNavigationReport?.IsValid ?? true, 8),
                new DirectExeSmokeFailureCheck(directTransformReport?.IsValid ?? true, 9),
                new DirectExeSmokeFailureCheck(multiTransformReport?.IsValid ?? true, 10),
                new DirectExeSmokeFailureCheck(libraryDropReport?.IsValid ?? true, 11),
                new DirectExeSmokeFailureCheck(layerOrderReport?.IsValid ?? true, 12),
                new DirectExeSmokeFailureCheck(runtimeDebuggerReport?.IsValid ?? true, 23),
                new DirectExeSmokeFailureCheck(faultManagerReport?.IsValid ?? true, 13),
                new DirectExeSmokeFailureCheck(cameraCommissioningReport?.IsValid ?? true, 18),
                new DirectExeSmokeFailureCheck(integrationPanelReport?.IsValid ?? true, 26),
                new DirectExeSmokeFailureCheck(mmiOperatorReport?.IsValid ?? true, 29),
                new DirectExeSmokeFailureCheck(axisCommissioningReport?.IsValid ?? true, 14),
                new DirectExeSmokeFailureCheck(multiAxisRecipeReport?.IsValid ?? true, 19),
                new DirectExeSmokeFailureCheck(cylinderCommissioningReport?.IsValid ?? true, 15),
                new DirectExeSmokeFailureCheck(conveyorCommissioningReport?.IsValid ?? true, 16),
                new DirectExeSmokeFailureCheck(sensorCommissioningReport?.IsValid ?? true, 17),
                new DirectExeSmokeFailureCheck(recipeGalleryReport?.IsValid ?? true, 20),
                new DirectExeSmokeFailureCheck(loadLockSetupReport?.IsValid ?? true, 21),
                new DirectExeSmokeFailureCheck(stationSkeletonReport?.IsValid ?? true, 21),
                new DirectExeSmokeFailureCheck(connectionWorkbenchDefaultReport?.IsValid ?? true, 21),
                new DirectExeSmokeFailureCheck(connectionWorkbenchReport?.IsValid ?? true, 21),
                new DirectExeSmokeFailureCheck(cameraFirstUseReport?.IsValid ?? true, 24),
                new DirectExeSmokeFailureCheck(projectSafetyReport?.IsValid ?? true, 22),
                new DirectExeSmokeFailureCheck(projectDiagnosticsReport?.IsValid ?? true, 27),
                new DirectExeSmokeFailureCheck(supportDiagnosticsReport?.IsValid ?? true, 28));
            Application.Current.Shutdown(exitCode);
        }
    }

    private static SmokeUiInteraction CreateUiInteraction(
        ShellWindow window,
        SmokeWindowCapture windowCapture,
        SmokeNativeInput nativeInput) =>
        new()
        {
            FindTextBlock = (root, predicate) => FindVisualDescendant<TextBlock>(root, predicate),
            FindButton = (root, predicate) => FindVisualDescendant<Button>(root, predicate),
            ActivateWindow = () =>
            {
                window.Activate();
                nativeInput.ActivateWindow(window);
            },
            MovePointerToCenter = nativeInput.MovePointerToCenter,
            MouseEvent = nativeInput.SendMouseEvent,
            SetCursorPosition = nativeInput.SetCursorPosition,
            GetCursorPosition = nativeInput.GetCursorPosition,
            SetPopupContent = windowCapture.SetPopupContent,
            MarkSmokePointerHeld = nativeInput.MarkPointerHeld,
            ReleaseSmokePointer = nativeInput.ReleasePointer,
            CheckPointerOwnership = nativeInput.CheckPointerOwnership
        };

    private static async Task ApplyDirectSceneGestureStateAsync(
        ShellWindow window,
        string state,
        SmokeNativeInput nativeInput)
    {
        var viewport = FindVisualDescendant<MachineSceneViewport>(window)
            ?? throw new InvalidOperationException("Machine scene viewport was not available.");
        if (state.Equals("navigation", StringComparison.OrdinalIgnoreCase))
        {
            var center = new Point(viewport.ActualWidth * 0.54, viewport.ActualHeight * 0.48);
            viewport.ZoomAt(center, 240);
            viewport.PanBy(new Vector(64, -34));
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            await Task.Delay(150);
            return;
        }

        if (state.Equals("library-drop", StringComparison.OrdinalIgnoreCase))
        {
            if (!viewport.ShowLibraryDropPreview(new Point(
                    viewport.ActualWidth * 0.72,
                    viewport.ActualHeight * 0.34)))
            {
                throw new InvalidOperationException("Library drop preview was unavailable.");
            }
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            await Task.Delay(150);
            return;
        }

        if (!state.Equals("marquee", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"Unsupported --smoke-direct-scene-gesture-state '{state}'. " +
                "Expected marquee, navigation, or library-drop.");
        }

        var start = viewport.PointToScreen(new Point(12, 12));
        var end = viewport.PointToScreen(new Point(
            Math.Max(24, viewport.ActualWidth * 0.72),
            Math.Max(24, viewport.ActualHeight * 0.74)));
        nativeInput.SetCursorPosition((int)Math.Round(start.X), (int)Math.Round(start.Y));
        nativeInput.PressLeftButton();
        nativeInput.MarkPointerHeld();
        viewport.RaiseEvent(new MouseButtonEventArgs(
            Mouse.PrimaryDevice,
            Environment.TickCount,
            MouseButton.Left)
        {
            RoutedEvent = Mouse.MouseDownEvent
        });
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        await Task.Delay(50);
        nativeInput.SetCursorPosition((int)Math.Round(end.X), (int)Math.Round(end.Y));
        viewport.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount)
        {
            RoutedEvent = Mouse.MouseMoveEvent
        });
        if (window.DataContext is MainViewModel viewModel)
        {
            viewModel.Layout.SelectedItem = null;
        }
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        await Task.Delay(150);
    }

    private static MmiOperatorSmokeFixture PrepareMmiOperatorSmokeFixture(string reportPath)
    {
        var fullReportPath = Path.GetFullPath(reportPath);
        if (!fullReportPath.StartsWith("D:\\", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "Machine Studio inspection smoke evidence must be stored on the D: test-data drive.",
                nameof(reportPath));
        }

        var evidenceRoot = Path.GetDirectoryName(fullReportPath)
            ?? throw new ArgumentException("Machine Studio inspection smoke report must include a directory.", nameof(reportPath));
        var fixtureRoot = Path.Combine(evidenceRoot, "mmi-fixture");
        var exchangeRoot = Path.Combine(fixtureRoot, "exchange");
        Directory.CreateDirectory(exchangeRoot);
        var recipePaths = new[]
        {
            Path.Combine(fixtureRoot, "recipe-alpha.2d.json"),
            Path.Combine(fixtureRoot, "recipe-beta.2d.json")
        };
        File.WriteAllText(recipePaths[0], "{\"schema\":\"2d-recipe-smoke\",\"name\":\"Inspection Recipe Alpha\"}");
        File.WriteAllText(recipePaths[1], "{\"schema\":\"2d-recipe-smoke\",\"name\":\"Inspection Recipe Beta\"}");

        var settingsPath = Path.Combine(fixtureRoot, "integration-exchange.json");
        new MachineIntegrationSetupStore(settingsPath).Save(new MachineIntegrationSetup
        {
            ExchangeRoot = exchangeRoot,
            InspectionRecipePath = recipePaths[0],
            RecipeCatalogPaths = recipePaths,
            TwoDConsumerVersion = "2.2.0-dev.smoke",
            TwoDConsumerCommit = new string('0', 40),
            TcpListenAddress = "127.0.0.1",
            TcpListenPort = 45101,
            TcpPeerHost = "127.0.0.1",
            TcpPeerPort = 45102
        });
        return new(settingsPath);
    }

    private sealed record MmiOperatorSmokeFixture(string SettingsPath);

    private static void AssertSmoke(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static bool IsVisualDescendantOf(DependencyObject ancestor, DependencyObject? candidate)
    {
        while (candidate is not null)
        {
            if (ReferenceEquals(candidate, ancestor))
            {
                return true;
            }

            candidate = VisualTreeHelper.GetParent(candidate);
        }

        return false;
    }

    private static void SelectLayoutItemsThroughScene(
        ShellWindow window,
        MainViewModel viewModel,
        IReadOnlyList<string> selectionIds)
    {
        var viewport = FindVisualDescendant<MachineSceneViewport>(window)
            ?? throw new InvalidOperationException("Machine scene viewport was not available.");
        for (var index = 0; index < selectionIds.Count; index++)
        {
            var id = selectionIds[index];
            var point = viewport.GetItemCenter(id)
                ?? throw new InvalidOperationException(
                    $"Layout item '{id}' was not visible in the machine scene.");
            var selected = index == 0
                ? viewport.SelectItemAt(point)
                : viewport.RequestExtendedSelectionAt(
                    point,
                    index == 1 ? ModifierKeys.Shift : ModifierKeys.Control);
            if (!selected)
            {
                throw new InvalidOperationException(
                    $"Layout item '{id}' could not be selected through the scene.");
            }
        }

        var actualIds = viewModel.Layout.SelectedItems.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        if (!actualIds.SetEquals(selectionIds) ||
            !string.Equals(viewModel.Layout.SelectedItem?.Id, selectionIds[^1], StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Scene Ctrl/Shift selection did not match the requested set.");
        }
    }

}
