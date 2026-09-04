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
        var semanticKind = project.Devices.Any(device => device.Kind == DeviceKind.Prealigner) ? "prealigner"
            : project.Devices.Any(device => device.Kind == DeviceKind.Handler) ? "wafer-handler"
            : project.Devices.Any(device => device.Kind == DeviceKind.Inspection) ? "inspection-handoff"
            : project.Devices.Any(device => device.Kind == DeviceKind.Sorter) ? "inspection-sort"
            : "oht";
        switch (semanticKind)
        {
            case "prealigner":
                vm.RecipeConnections.SemanticSetups.PreviewPrealignerSetupCommand.Execute(null);
                break;
            case "wafer-handler":
                vm.RecipeConnections.SemanticSetups.PreviewWaferHandlerSetupCommand.Execute(null);
                break;
            case "inspection-handoff":
                vm.RecipeConnections.SemanticSetups.PreviewInspectionHandoffSetupCommand.Execute(null);
                break;
            case "inspection-sort":
                vm.RecipeConnections.SemanticSetups.PreviewInspectionSortRouterSetupCommand.Execute(null);
                break;
            default:
                vm.RecipeConnections.SemanticSetups.PreviewOhtHandoffSetupCommand.Execute(null);
                break;
        }

        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Check(
            (semanticKind == "prealigner"
                ? vm.RecipeConnections.SemanticSetups.IsPrealignerSetupVisible
                : semanticKind == "wafer-handler"
                    ? vm.RecipeConnections.SemanticSetups.IsWaferHandlerSetupVisible
                    : semanticKind == "inspection-handoff"
                        ? vm.RecipeConnections.SemanticSetups.IsInspectionHandoffSetupVisible
                        : semanticKind == "inspection-sort"
                            ? vm.RecipeConnections.SemanticSetups.IsInspectionSortRouterSetupVisible
                            : vm.RecipeConnections.SemanticSetups.IsOhtHandoffSetupVisible)
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
                    vm.RecipeConnections.SemanticSetups.InspectionHandoffCameraId = missingId;
                    break;
                case "inspection-sort":
                    vm.RecipeConnections.SemanticSetups.InspectionSortCameraId = missingId;
                    break;
                case "oht":
                    vm.RecipeConnections.SemanticSetups.OhtTransportConveyorId = missingId;
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
                "inspection-handoff" => vm.RecipeConnections.SemanticSetups.InspectionHandoffCameraId == missingId
                    && vm.RecipeConnections.SemanticSetups.HasInspectionHandoffSetupValidationError
                    && vm.RecipeConnections.SemanticSetups.InspectionCameraOptions.Any(option => option.Id == missingId),
                "inspection-sort" => vm.RecipeConnections.SemanticSetups.InspectionSortCameraId == missingId
                    && vm.RecipeConnections.SemanticSetups.HasInspectionSortRouterSetupValidationError
                    && vm.RecipeConnections.SemanticSetups.InspectionCameraOptions.Any(option => option.Id == missingId),
                _ => vm.RecipeConnections.SemanticSetups.OhtTransportConveyorId == missingId
                    && vm.RecipeConnections.SemanticSetups.HasOhtHandoffSetupValidationError
                    && vm.RecipeConnections.SemanticSetups.InspectionConveyorOptions.Any(option => option.Id == missingId)
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
                    vm.RecipeConnections.SemanticSetups.PrealignerRotaryStageComponentId =
                        vm.RecipeConnections.SemanticSetups.PrealignerClampCylinderComponentId;
                    break;
                case "wafer-handler":
                    vm.RecipeConnections.SemanticSetups.WaferHandlerHorizontalAxisId =
                        vm.RecipeConnections.SemanticSetups.WaferHandlerVerticalAxisId;
                    break;
                case "inspection-handoff":
                    vm.RecipeConnections.SemanticSetups.InspectionHandoffCameraId = null;
                    break;
                case "inspection-sort":
                    vm.RecipeConnections.SemanticSetups.InspectionSortNgConveyorId =
                        vm.RecipeConnections.SemanticSetups.InspectionSortPassConveyorId;
                    break;
                default:
                    vm.RecipeConnections.SemanticSetups.OhtVehicleDockedChannelId =
                        vm.RecipeConnections.SemanticSetups.OhtRouteAvailableChannelId;
                    break;
            }

            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Check(
                semanticKind == "prealigner"
                    ? vm.RecipeConnections.SemanticSetups.HasPrealignerSetupValidationError
                    : semanticKind == "wafer-handler"
                        ? vm.RecipeConnections.SemanticSetups.HasWaferHandlerSetupValidationError
                        : semanticKind == "inspection-handoff"
                            ? vm.RecipeConnections.SemanticSetups.HasInspectionHandoffSetupValidationError
                            : semanticKind == "inspection-sort"
                                ? vm.RecipeConnections.SemanticSetups.HasInspectionSortRouterSetupValidationError
                                : vm.RecipeConnections.SemanticSetups.HasOhtHandoffSetupValidationError,
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
                vm.RecipeConnections.SemanticSetups.ApplyPrealignerSetupCommand.Execute(null);
                break;
            case "wafer-handler":
                vm.RecipeConnections.SemanticSetups.ApplyWaferHandlerSetupCommand.Execute(null);
                break;
            case "inspection-handoff":
                vm.RecipeConnections.SemanticSetups.ApplyInspectionHandoffSetupCommand.Execute(null);
                break;
            case "inspection-sort":
                vm.RecipeConnections.SemanticSetups.ApplyInspectionSortRouterSetupCommand.Execute(null);
                break;
            default:
                vm.RecipeConnections.SemanticSetups.ApplyOhtHandoffSetupCommand.Execute(null);
                break;
        }

        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Check(
            !vm.RecipeConnections.SemanticSetups.IsPrealignerSetupVisible
            && !vm.RecipeConnections.SemanticSetups.IsWaferHandlerSetupVisible
            && !vm.RecipeConnections.SemanticSetups.IsInspectionHandoffSetupVisible
            && !vm.RecipeConnections.SemanticSetups.IsInspectionSortRouterSetupVisible
            && !vm.RecipeConnections.SemanticSetups.IsOhtHandoffSetupVisible
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
                    vm.RecipeConnections.SemanticSetups.PreviewPrealignerSetupCommand.Execute(null);
                    break;
                case "wafer-handler":
                    vm.RecipeConnections.SemanticSetups.PreviewWaferHandlerSetupCommand.Execute(null);
                    break;
                case "inspection-handoff":
                    vm.RecipeConnections.SemanticSetups.PreviewInspectionHandoffSetupCommand.Execute(null);
                    break;
                case "inspection-sort":
                    vm.RecipeConnections.SemanticSetups.PreviewInspectionSortRouterSetupCommand.Execute(null);
                    break;
                default:
                    vm.RecipeConnections.SemanticSetups.PreviewOhtHandoffSetupCommand.Execute(null);
                    break;
            }

            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Check(
                (semanticKind == "prealigner"
                    ? vm.RecipeConnections.SemanticSetups.IsPrealignerSetupVisible
                    : semanticKind == "wafer-handler"
                        ? vm.RecipeConnections.SemanticSetups.IsWaferHandlerSetupVisible
                        : semanticKind == "inspection-handoff"
                            ? vm.RecipeConnections.SemanticSetups.IsInspectionHandoffSetupVisible
                            : semanticKind == "inspection-sort"
                                ? vm.RecipeConnections.SemanticSetups.IsInspectionSortRouterSetupVisible
                                : vm.RecipeConnections.SemanticSetups.IsOhtHandoffSetupVisible)
                && !vm.IsRunning
                && vm.IsDesignMode,
                "Saved semantic setup was not restored safely after reopen.");
        }
    }
}
