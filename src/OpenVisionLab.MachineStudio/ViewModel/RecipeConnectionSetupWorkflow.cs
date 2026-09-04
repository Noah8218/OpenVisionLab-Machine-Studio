using System.Globalization;
using OpenVisionLab.Machine.Core.Devices;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Sequence.Authoring;

namespace OpenVisionLab.MachineStudio.ViewModel;

/// <summary>
/// Coordinates the common Recipe Connection setup callback workflow.
/// Project mutation remains owned by <see cref="RecipeConnectionProjectApplier"/>;
/// this class maps its result to shell-owned completion, status, and log callbacks.
/// </summary>
internal sealed class RecipeConnectionSetupWorkflow
{
    private readonly RecipeConnectionProjectApplier _projectApplier;
    private readonly Func<MachineProjectDocument> _projectAccessor;
    private readonly Action _exitDryRunPlayback;
    private readonly Action _completeSetupMutation;
    private readonly Action _completeProcessBlockMutation;
    private readonly Action<string> _setStatus;
    private readonly Action<string, string> _log;

    internal RecipeConnectionSetupWorkflow(
        RecipeConnectionProjectApplier projectApplier,
        Func<MachineProjectDocument> projectAccessor,
        Action exitDryRunPlayback,
        Action completeSetupMutation,
        Action completeProcessBlockMutation,
        Action<string> setStatus,
        Action<string, string> log)
    {
        ArgumentNullException.ThrowIfNull(projectApplier);
        ArgumentNullException.ThrowIfNull(projectAccessor);
        ArgumentNullException.ThrowIfNull(exitDryRunPlayback);
        ArgumentNullException.ThrowIfNull(completeSetupMutation);
        ArgumentNullException.ThrowIfNull(completeProcessBlockMutation);
        ArgumentNullException.ThrowIfNull(setStatus);
        ArgumentNullException.ThrowIfNull(log);

        _projectApplier = projectApplier;
        _projectAccessor = projectAccessor;
        _exitDryRunPlayback = exitDryRunPlayback;
        _completeSetupMutation = completeSetupMutation;
        _completeProcessBlockMutation = completeProcessBlockMutation;
        _setStatus = setStatus;
        _log = log;
    }

    internal int ApplyStationSkeleton(SemiconductorStationSetupDefinition setup)
    {
        ArgumentNullException.ThrowIfNull(setup);

        _exitDryRunPlayback();
        var result = _projectApplier.ApplyStationSkeleton(_projectAccessor(), setup);
        if (!result.Changed)
        {
            _setStatus(OpenVisionLanguageService.T("Connections.StationSkeletonNoChangesStatus"));
            return 0;
        }

        _completeSetupMutation();
        _setStatus(result.AppliedCount > 0
            ? string.Format(
                CultureInfo.CurrentCulture,
                OpenVisionLanguageService.T("Connections.StationSkeletonAppliedStatus"),
                result.AppliedCount)
            : OpenVisionLanguageService.T("Connections.StationSetupAppliedStatus"));
        _log("Project", $"Applied semiconductor station setup · {result.AppliedCount} missing role(s)");
        return result.ChangeCount;
    }

    internal int ApplyLoadLockSetup(LoadLockDefinition setup) =>
        ApplySingleDeviceSetup(
            setup,
            _projectApplier.ApplyLoadLockSetup,
            "Connections.LoadLockSetupMultipleError",
            "Connections.LoadLockSetupNoChangesStatus",
            "Connections.LoadLockSetupAppliedStatus",
            "Applied load-lock setup · ");

    internal int ApplyWaferHandlerSetup(WaferHandlerDefinition setup) =>
        ApplySingleDeviceSetup(
            setup,
            _projectApplier.ApplyWaferHandlerSetup,
            "Connections.WaferHandlerSetupMultipleError",
            "Connections.WaferHandlerSetupNoChangesStatus",
            "Connections.WaferHandlerSetupAppliedStatus",
            "Applied wafer-handler setup · ");

    internal int ApplyPrealignerSetup(PrealignerDefinition setup) =>
        ApplySingleDeviceSetup(
            setup,
            _projectApplier.ApplyPrealignerSetup,
            "Connections.PrealignerSetupMultipleError",
            "Connections.PrealignerSetupNoChangesStatus",
            "Connections.PrealignerSetupAppliedStatus",
            "Applied pre-aligner setup · ");

    internal int ApplyInspectionHandoffSetup(InspectionHandoffDefinition setup) =>
        ApplySingleDeviceSetup(
            setup,
            _projectApplier.ApplyInspectionHandoffSetup,
            "Connections.InspectionHandoffSetupMultipleError",
            "Connections.InspectionHandoffSetupNoChangesStatus",
            "Connections.InspectionHandoffSetupAppliedStatus",
            "Applied inspection-handoff setup · ");

    internal int ApplyInspectionSortRouterSetup(InspectionSortRouterDefinition setup) =>
        ApplySingleDeviceSetup(
            setup,
            _projectApplier.ApplyInspectionSortRouterSetup,
            "Connections.InspectionSortSetupMultipleError",
            "Connections.InspectionSortSetupNoChangesStatus",
            "Connections.InspectionSortSetupAppliedStatus",
            "Applied inspection-sort setup · ");

    internal int ApplyOhtHandoffSetup(OhtHandoffDefinition setup) =>
        ApplySingleDeviceSetup(
            setup,
            _projectApplier.ApplyOhtHandoffSetup,
            "Connections.OhtSetupMultipleError",
            "Connections.OhtSetupNoChangesStatus",
            "Connections.OhtSetupAppliedStatus",
            "Applied OHT setup · ");

    internal int ApplyProcessBlocks(IReadOnlyList<SemiconductorProcessBlockKind> kinds)
    {
        ArgumentNullException.ThrowIfNull(kinds);

        _exitDryRunPlayback();
        var result = _projectApplier.ApplyProcessBlocks(_projectAccessor(), kinds);
        if (!result.Changed)
        {
            _setStatus(OpenVisionLanguageService.T("Connections.ProcessBlockEditNoChangesStatus"));
            return 0;
        }

        _completeProcessBlockMutation();
        _setStatus(string.Format(
            CultureInfo.CurrentCulture,
            OpenVisionLanguageService.T("Connections.ProcessBlockEditAppliedStatus"),
            kinds.Count,
            result.AddedConnectionCount,
            result.AddedStepCount,
            result.RemovedStepCount));
        _log(
            "Sequence",
            $"Applied semiconductor process plan · {kinds.Count} block(s) · {result.AddedConnectionCount} connection role(s) · {result.AddedStepCount} step(s) added · {result.RemovedStepCount} managed step(s) removed");
        return result.ChangeCount;
    }

    private int ApplySingleDeviceSetup<TSetup>(
        TSetup setup,
        Func<MachineProjectDocument, TSetup, RecipeConnectionProjectApplyResult> apply,
        string multipleDevicesStatusKey,
        string noChangesStatusKey,
        string appliedStatusKey,
        string logPrefix)
    {
        ArgumentNullException.ThrowIfNull(setup);
        ArgumentNullException.ThrowIfNull(apply);

        _exitDryRunPlayback();
        var result = apply(_projectAccessor(), setup);
        if (result.Outcome == RecipeConnectionProjectApplyOutcome.MultipleDevices)
        {
            _setStatus(OpenVisionLanguageService.T(multipleDevicesStatusKey));
            return 0;
        }
        if (!result.Changed)
        {
            _setStatus(OpenVisionLanguageService.T(noChangesStatusKey));
            return 0;
        }

        _completeSetupMutation();
        _setStatus(OpenVisionLanguageService.T(appliedStatusKey));
        _log("Project", logPrefix + result.EntityId);
        return result.ChangeCount;
    }
}
