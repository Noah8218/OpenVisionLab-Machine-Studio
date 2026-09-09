using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using OpenVisionLab;
using OpenVisionLab.Machine.Core.Devices;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Simulation.Sequences;
using OpenVisionLab.MachineStudio.View.Project;
using OpenVisionLab.MachineStudio.View.Shell;
using OpenVisionLab.MachineStudio.ViewModel;

namespace OpenVisionLab.MachineStudio;

internal static class SmokeSemanticSetupVerifier
{
    public static bool IsSupportedState(string? state) => state?.ToLowerInvariant() is
        "semantic-setup-preview"
        or "semantic-setup-invalid"
        or "semantic-setup-applied"
        or "semantic-setup-language-refresh";

    public static async Task VerifyAsync(
        ShellWindow window,
        MainViewModel vm,
        string semanticState,
        MachineProjectDocument? initialProject,
        RecipeConnectionWorkbenchView workbench,
        Func<DependencyObject, Func<FrameworkElement, bool>, FrameworkElement?> findFrameworkElement,
        Func<DependencyObject, Func<Button, bool>, Button?> findButton,
        string? savePath)
    {
        var project = initialProject
            ?? throw new InvalidOperationException("A project is required for semantic setup smoke.");
        var normalizedState = semanticState.ToLowerInvariant();
        if (!IsSupportedState(normalizedState))
        {
            throw new ArgumentException(
                $"Unsupported --smoke-connection-workbench-state '{semanticState}'. " +
                "Expected semantic-setup-preview, semantic-setup-invalid, " +
                "semantic-setup-applied, or semantic-setup-language-refresh.");
        }

        void Check(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }

        var semanticStore = new ProjectDocumentStore();
        var semanticBefore = semanticStore.Serialize(project);
        var semanticRuntimeBefore = vm.SceneSnapshots.Latest;
        var semantic = vm.RecipeConnections.SemanticSetups;
        var semanticKind = project.Devices.Any(device => device.Kind == DeviceKind.Prealigner) ? "prealigner"
            : project.Devices.Any(device => device.Kind == DeviceKind.Handler) ? "wafer-handler"
            : project.Devices.Any(device => device.Kind == DeviceKind.Inspection) ? "inspection-handoff"
            : project.Devices.Any(device => device.Kind == DeviceKind.Sorter) ? "inspection-sort"
            : "oht";
        switch (semanticKind)
        {
            case "prealigner":
                semantic.Prealigner.PreviewCommand.Execute(null);
                break;
            case "wafer-handler":
                semantic.WaferHandler.PreviewCommand.Execute(null);
                break;
            case "inspection-handoff":
                semantic.InspectionHandoff.PreviewCommand.Execute(null);
                break;
            case "inspection-sort":
                semantic.InspectionSortRouter.PreviewCommand.Execute(null);
                break;
            default:
                semantic.OhtHandoff.PreviewCommand.Execute(null);
                break;
        }

        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Check(
            (semanticKind == "prealigner"
                ? semantic.Prealigner.IsVisible
                : semanticKind == "wafer-handler"
                    ? semantic.WaferHandler.IsVisible
                    : semanticKind == "inspection-handoff"
                        ? semantic.InspectionHandoff.IsVisible
                        : semanticKind == "inspection-sort"
                            ? semantic.InspectionSortRouter.IsVisible
                            : semantic.OhtHandoff.IsVisible)
            && semanticBefore == semanticStore.Serialize(project)
            && semanticRuntimeBefore?.TickIndex == vm.SceneSnapshots.Latest?.TickIndex
            && !vm.IsRunning
            && vm.IsDesignMode,
            "Semantic setup preview changed project or runtime state.");

        if (normalizedState == "semantic-setup-language-refresh")
        {
            const string missingId = "smoke-missing-draft";
            switch (semanticKind)
            {
                case "inspection-handoff":
                    semantic.InspectionHandoff.CameraId = missingId;
                    break;
                case "inspection-sort":
                    semantic.InspectionSortRouter.CameraId = missingId;
                    break;
                case "oht":
                    semantic.OhtHandoff.TransportConveyorId = missingId;
                    break;
                default:
                    throw new InvalidOperationException(
                        "Language-refresh smoke requires inspection, sorter, or OHT setup.");
            }

            OpenVisionLanguageService.SetLanguage(
                OpenVisionLanguageService.CurrentLanguage == OpenVisionLanguage.Korean
                    ? OpenVisionLanguage.English
                    : OpenVisionLanguage.Korean,
                save: false);
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var preserved = semanticKind switch
            {
                "inspection-handoff" => semantic.InspectionHandoff.CameraId == missingId
                    && semantic.InspectionHandoff.HasValidationError
                    && semantic.InspectionSortRouter.CameraOptions.Any(option => option.Id == missingId),
                "inspection-sort" => semantic.InspectionSortRouter.CameraId == missingId
                    && semantic.InspectionSortRouter.HasValidationError
                    && semantic.InspectionHandoff.CameraOptions.Any(option => option.Id == missingId),
                _ => semantic.OhtHandoff.TransportConveyorId == missingId
                    && semantic.OhtHandoff.HasValidationError
                    && semantic.OhtHandoff.ConveyorOptions.Any(option => option.Id == missingId)
            };
            Check(
                preserved
                && semanticBefore == semanticStore.Serialize(project)
                && semanticRuntimeBefore?.TickIndex == vm.SceneSnapshots.Latest?.TickIndex
                && !vm.IsRunning
                && vm.IsDesignMode,
                "Language refresh discarded the semantic setup draft or changed project/runtime state.");
            var previewName = semanticKind switch
            {
                "inspection-handoff" => "InspectionHandoffSetupPreview",
                "inspection-sort" => "InspectionSortSetupPreview",
                _ => "OhtSetupPreview"
            };
            var preview = findFrameworkElement(
                workbench,
                candidate => string.Equals(candidate.Name, previewName, StringComparison.Ordinal))
                ?? throw new InvalidOperationException(
                    "Semantic setup preview was not available after language refresh.");
            preview.BringIntoView();
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            await Task.Delay(100);
            return;
        }

        if (normalizedState == "semantic-setup-invalid")
        {
            switch (semanticKind)
            {
                case "prealigner":
                    semantic.Prealigner.RotaryStageComponentId =
                        semantic.Prealigner.ClampCylinderComponentId;
                    break;
                case "wafer-handler":
                    semantic.WaferHandler.HorizontalAxisId =
                        semantic.WaferHandler.VerticalAxisId;
                    break;
                case "inspection-handoff":
                    semantic.InspectionHandoff.CameraId = null;
                    break;
                case "inspection-sort":
                    semantic.InspectionSortRouter.NgConveyorId =
                        semantic.InspectionSortRouter.PassConveyorId;
                    break;
                default:
                    semantic.OhtHandoff.VehicleDockedChannelId =
                        semantic.OhtHandoff.RouteAvailableChannelId;
                    break;
            }

            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Check(
                semanticKind == "prealigner"
                    ? semantic.Prealigner.HasValidationError
                    : semanticKind == "wafer-handler"
                        ? semantic.WaferHandler.HasValidationError
                        : semanticKind == "inspection-handoff"
                            ? semantic.InspectionHandoff.HasValidationError
                            : semanticKind == "inspection-sort"
                                ? semantic.InspectionSortRouter.HasValidationError
                                : semantic.OhtHandoff.HasValidationError,
                "Invalid semantic setup did not block Apply.");
            return;
        }

        if (normalizedState == "semantic-setup-preview")
        {
            return;
        }

        var applyName = semanticKind switch
        {
            "prealigner" => "ApplyPrealignerSetupButton",
            "wafer-handler" => "ApplyWaferHandlerSetupButton",
            "inspection-handoff" => "ApplyInspectionHandoffSetupButton",
            "inspection-sort" => "ApplyInspectionSortSetupButton",
            _ => "ApplyOhtSetupButton"
        };
        var apply = findButton(
            workbench,
            candidate => string.Equals(candidate.Name, applyName, StringComparison.Ordinal))
            ?? throw new InvalidOperationException("Semantic setup Apply button was not available.");
        Check(apply.IsEnabled, "Valid semantic setup did not enable Apply.");
        vm.RecipeConnections.ValidateSimulationReadinessCommand.Execute(null);
        switch (semanticKind)
        {
            case "prealigner":
                semantic.Prealigner.ApplyCommand.Execute(null);
                break;
            case "wafer-handler":
                semantic.WaferHandler.ApplyCommand.Execute(null);
                break;
            case "inspection-handoff":
                semantic.InspectionHandoff.ApplyCommand.Execute(null);
                break;
            case "inspection-sort":
                semantic.InspectionSortRouter.ApplyCommand.Execute(null);
                break;
            default:
                semantic.OhtHandoff.ApplyCommand.Execute(null);
                break;
        }

        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Check(
            !semantic.Prealigner.IsVisible
            && !semantic.WaferHandler.IsVisible
            && !semantic.InspectionHandoff.IsVisible
            && !semantic.InspectionSortRouter.IsVisible
            && !semantic.OhtHandoff.IsVisible
            && !vm.IsRunning
            && vm.IsDesignMode
            && semanticRuntimeBefore?.TickIndex == vm.SceneSnapshots.Latest?.TickIndex,
            "Applying semantic setup did not preserve stopped design mode.");
        vm.RecipeConnections.ValidateSimulationReadinessCommand.Execute(null);
        Check(
            vm.RecipeConnections.ReadinessPassed == true,
            "Applied semantic setup did not pass readiness.");
        vm.RecipeConnections.RunRecipeDryRunCommand.Execute(null);
        for (var attempt = 0;
             attempt < 300 && vm.RecipeConnections.IsRecipeDryRunRunning;
             attempt++)
        {
            await Task.Delay(20);
        }

        Check(
            !vm.RecipeConnections.IsRecipeDryRunRunning
            && vm.RecipeConnections.HasRecipeDryRunResult,
            "Applied semantic setup did not complete the existing recipe dry-run.");
        if (!string.IsNullOrWhiteSpace(savePath))
        {
            await vm.SaveProjectAsync(savePath);
            Check(
                await vm.OpenProjectAsync(savePath),
                "Semantic setup project did not reopen.");
            switch (semanticKind)
            {
                case "prealigner":
                    semantic.Prealigner.PreviewCommand.Execute(null);
                    break;
                case "wafer-handler":
                    semantic.WaferHandler.PreviewCommand.Execute(null);
                    break;
                case "inspection-handoff":
                    semantic.InspectionHandoff.PreviewCommand.Execute(null);
                    break;
                case "inspection-sort":
                    semantic.InspectionSortRouter.PreviewCommand.Execute(null);
                    break;
                default:
                    semantic.OhtHandoff.PreviewCommand.Execute(null);
                    break;
            }

            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Check(
                (semanticKind == "prealigner"
                    ? semantic.Prealigner.IsVisible
                    : semanticKind == "wafer-handler"
                        ? semantic.WaferHandler.IsVisible
                        : semanticKind == "inspection-handoff"
                            ? semantic.InspectionHandoff.IsVisible
                            : semanticKind == "inspection-sort"
                                ? semantic.InspectionSortRouter.IsVisible
                                : semantic.OhtHandoff.IsVisible)
                && !vm.IsRunning
                && vm.IsDesignMode,
                "Saved semantic setup was not restored safely after reopen.");
        }
    }
}
