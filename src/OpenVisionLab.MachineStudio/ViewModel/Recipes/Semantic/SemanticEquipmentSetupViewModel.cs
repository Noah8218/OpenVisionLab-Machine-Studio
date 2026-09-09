using OpenVisionLab.Machine.Core.Devices;
using OpenVisionLab.Machine.Core.Projects;

namespace OpenVisionLab.MachineStudio.ViewModel;

/// <summary>
/// Composes the semantic setup cards and coordinates their shared preview lifetime.
/// Each child owns one equipment definition and its editable draft.
/// </summary>
public sealed class SemanticEquipmentSetupViewModel : ViewModelBase
{
    private bool _isEditable = true;

    public SemanticEquipmentSetupViewModel(
        Func<WaferHandlerDefinition, int> applyWaferHandlerSetup,
        Func<PrealignerDefinition, int> applyPrealignerSetup,
        Func<InspectionHandoffDefinition, int> applyInspectionHandoffSetup,
        Func<InspectionSortRouterDefinition, int> applyInspectionSortRouterSetup,
        Func<OhtHandoffDefinition, int> applyOhtHandoffSetup,
        Action clearWorkbenchPreviews)
    {
        WaferHandler = new WaferHandlerSetupViewModel(applyWaferHandlerSetup, clearWorkbenchPreviews);
        Prealigner = new PrealignerSetupViewModel(applyPrealignerSetup, clearWorkbenchPreviews);
        InspectionHandoff = new InspectionHandoffSetupViewModel(applyInspectionHandoffSetup, clearWorkbenchPreviews);
        InspectionSortRouter = new InspectionSortRouterSetupViewModel(applyInspectionSortRouterSetup, clearWorkbenchPreviews);
        OhtHandoff = new OhtHandoffSetupViewModel(applyOhtHandoffSetup, clearWorkbenchPreviews);
    }

    public WaferHandlerSetupViewModel WaferHandler { get; }
    public PrealignerSetupViewModel Prealigner { get; }
    public InspectionHandoffSetupViewModel InspectionHandoff { get; }
    public InspectionSortRouterSetupViewModel InspectionSortRouter { get; }
    public OhtHandoffSetupViewModel OhtHandoff { get; }

    public bool IsEditable
    {
        get => _isEditable;
        set
        {
            if (!SetProperty(ref _isEditable, value)) return;
            WaferHandler.IsEditable = value;
            Prealigner.IsEditable = value;
            InspectionHandoff.IsEditable = value;
            InspectionSortRouter.IsEditable = value;
            OhtHandoff.IsEditable = value;
        }
    }

    public void Load(MachineProjectDocument project)
    {
        WaferHandler.Load(project);
        Prealigner.Load(project);
        InspectionHandoff.Load(project);
        InspectionSortRouter.Load(project);
        OhtHandoff.Load(project);
    }

    public void ClearPreviewForCompetingSetup()
    {
        WaferHandler.ClearPreviewForCompetingSetup();
        Prealigner.ClearPreviewForCompetingSetup();
        InspectionHandoff.ClearPreviewForCompetingSetup();
        InspectionSortRouter.ClearPreviewForCompetingSetup();
        OhtHandoff.ClearPreviewForCompetingSetup();
    }

    internal void RefreshLocalization(Action reloadWorkbench)
    {
        if (WaferHandler.IsVisible)
        {
            WaferHandler.RefreshLocalization(reloadWorkbench);
        }
        else if (Prealigner.IsVisible)
        {
            Prealigner.RefreshLocalization(reloadWorkbench);
        }
        else if (InspectionHandoff.IsVisible)
        {
            InspectionHandoff.RefreshLocalization(reloadWorkbench);
        }
        else if (InspectionSortRouter.IsVisible)
        {
            InspectionSortRouter.RefreshLocalization(reloadWorkbench);
        }
        else if (OhtHandoff.IsVisible)
        {
            OhtHandoff.RefreshLocalization(reloadWorkbench);
        }
        else
        {
            reloadWorkbench();
        }
    }
}
