using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using OpenVisionLab;
using OpenVisionLab.Machine.Core.Axes;
using OpenVisionLab.Machine.Core.Channels;
using OpenVisionLab.Machine.Core.Devices;
using OpenVisionLab.Machine.Core.Layouts;
using OpenVisionLab.Machine.Core.Projects;

namespace OpenVisionLab.MachineStudio.ViewModel;

public sealed class SemanticEquipmentSetupViewModel : ViewModelBase
{
    private readonly Func<WaferHandlerDefinition, int> _applyWaferHandlerSetup;
    private readonly Func<PrealignerDefinition, int> _applyPrealignerSetup;
    private readonly Func<InspectionHandoffDefinition, int> _applyInspectionHandoffSetup;
    private readonly Func<InspectionSortRouterDefinition, int> _applyInspectionSortRouterSetup;
    private readonly Func<OhtHandoffDefinition, int> _applyOhtHandoffSetup;
    private readonly Action _clearWorkbenchPreviews;
    private readonly RelayCommand _previewWaferHandlerSetupCommand;
    private readonly RelayCommand _applyWaferHandlerSetupCommand;
    private readonly RelayCommand _cancelWaferHandlerSetupCommand;
    private readonly RelayCommand _resetWaferHandlerSetupCommand;
    private readonly RelayCommand _previewPrealignerSetupCommand;
    private readonly RelayCommand _applyPrealignerSetupCommand;
    private readonly RelayCommand _cancelPrealignerSetupCommand;
    private readonly RelayCommand _resetPrealignerSetupCommand;
    private readonly RelayCommand _previewInspectionHandoffSetupCommand;
    private readonly RelayCommand _applyInspectionHandoffSetupCommand;
    private readonly RelayCommand _cancelInspectionHandoffSetupCommand;
    private readonly RelayCommand _resetInspectionHandoffSetupCommand;
    private readonly RelayCommand _previewInspectionSortRouterSetupCommand;
    private readonly RelayCommand _applyInspectionSortRouterSetupCommand;
    private readonly RelayCommand _cancelInspectionSortRouterSetupCommand;
    private readonly RelayCommand _resetInspectionSortRouterSetupCommand;
    private readonly RelayCommand _previewOhtHandoffSetupCommand;
    private readonly RelayCommand _applyOhtHandoffSetupCommand;
    private readonly RelayCommand _cancelOhtHandoffSetupCommand;
    private readonly RelayCommand _resetOhtHandoffSetupCommand;
    private MachineProjectDocument? _project;
    private bool _isEditable = true;
    private bool _isWaferHandlerSetupVisible;
    private bool _isPrealignerSetupVisible;
    private bool _isInspectionHandoffSetupVisible;
    private bool _isInspectionSortRouterSetupVisible;
    private bool _isOhtHandoffSetupVisible;
    private WaferHandlerDefinition? _savedWaferHandlerSetup;
    private PrealignerDefinition? _savedPrealignerSetup;
    private InspectionHandoffDefinition? _savedInspectionHandoffSetup;
    private InspectionSortRouterDefinition? _savedInspectionSortRouterSetup;
    private OhtHandoffDefinition? _savedOhtHandoffSetup;
    private string? _waferHandlerHorizontalAxisId;
    private string? _waferHandlerVerticalAxisId;
    private string? _waferHandlerWorkpieceComponentId;
    private string? _waferHandlerSourcePresentSensorChannelId;
    private string? _waferHandlerGateOpenSensorChannelId;
    private string? _waferHandlerPickCommandChannelId;
    private string? _waferHandlerPlaceCommandChannelId;
    private string? _waferHandlerHoldingFeedbackChannelId;
    private string? _waferHandlerPlacedFeedbackChannelId;
    private string _waferHandlerPickHorizontalText = string.Empty;
    private string _waferHandlerPickVerticalText = string.Empty;
    private string _waferHandlerPlaceHorizontalText = string.Empty;
    private string _waferHandlerPlaceVerticalText = string.Empty;
    private string? _prealignerRotaryStageComponentId;
    private string? _prealignerClampCylinderComponentId;
    private string? _prealignerWaferPresentSensorChannelId;
    private string? _prealignerAlignmentAcceptedCommandChannelId;
    private string? _prealignerAlignmentReadyFeedbackChannelId;
    private string? _prealignerAlignmentCompleteFeedbackChannelId;
    private string _prealignerAlignmentTargetText = string.Empty;
    private string _prealignerAlignmentToleranceText = string.Empty;
    private string? _inspectionHandoffCameraId;
    private string? _inspectionHandoffPositionSensorChannelId;
    private string? _inspectionHandoffAcceptedChannelId;
    private string? _inspectionHandoffReadyChannelId;
    private string? _inspectionHandoffCompleteChannelId;
    private string? _inspectionSortCameraId;
    private string? _inspectionSortPassConveyorId;
    private string? _inspectionSortNgConveyorId;
    private string? _inspectionSortPassFeedbackChannelId;
    private string? _inspectionSortNgFeedbackChannelId;
    private string? _ohtTransportConveyorId;
    private string? _ohtRouteAvailableChannelId;
    private string? _ohtVehicleDockedChannelId;
    private string? _ohtLoadPortReadyChannelId;
    private string? _ohtCarrierReceivedChannelId;
    private string? _ohtHandoffReadyChannelId;
    private string? _ohtCarrierTransferredChannelId;

    public SemanticEquipmentSetupViewModel(
        Func<WaferHandlerDefinition, int> applyWaferHandlerSetup,
        Func<PrealignerDefinition, int> applyPrealignerSetup,
        Func<InspectionHandoffDefinition, int> applyInspectionHandoffSetup,
        Func<InspectionSortRouterDefinition, int> applyInspectionSortRouterSetup,
        Func<OhtHandoffDefinition, int> applyOhtHandoffSetup,
        Action clearWorkbenchPreviews)
    {
        _applyWaferHandlerSetup = applyWaferHandlerSetup;
        _applyPrealignerSetup = applyPrealignerSetup;
        _applyInspectionHandoffSetup = applyInspectionHandoffSetup;
        _applyInspectionSortRouterSetup = applyInspectionSortRouterSetup;
        _applyOhtHandoffSetup = applyOhtHandoffSetup;
        _clearWorkbenchPreviews = clearWorkbenchPreviews;
        _previewWaferHandlerSetupCommand = new RelayCommand(_ => PreviewWaferHandlerSetup(), _ => IsEditable && _project is not null);
        _applyWaferHandlerSetupCommand = new RelayCommand(_ => ApplyWaferHandlerSetup(), ignored => IsEditable && IsWaferHandlerSetupVisible && TryCreateWaferHandlerSetup(out _));
        _cancelWaferHandlerSetupCommand = new RelayCommand(_ => ClearWaferHandlerSetup(), _ => IsWaferHandlerSetupVisible);
        _resetWaferHandlerSetupCommand = new RelayCommand(_ => ResetWaferHandlerSetup(), _ => IsEditable && IsWaferHandlerSetupVisible);
        _previewPrealignerSetupCommand = new RelayCommand(_ => PreviewPrealignerSetup(), _ => IsEditable && _project is not null);
        _applyPrealignerSetupCommand = new RelayCommand(_ => ApplyPrealignerSetup(), ignored => IsEditable && IsPrealignerSetupVisible && TryCreatePrealignerSetup(out _));
        _cancelPrealignerSetupCommand = new RelayCommand(_ => ClearPrealignerSetup(), _ => IsPrealignerSetupVisible);
        _resetPrealignerSetupCommand = new RelayCommand(_ => ResetPrealignerSetup(), _ => IsEditable && IsPrealignerSetupVisible);
        _previewInspectionHandoffSetupCommand = new RelayCommand(_ => PreviewInspectionHandoffSetup(), _ => IsEditable && _project is not null);
        _applyInspectionHandoffSetupCommand = new RelayCommand(_ => ApplyInspectionHandoffSetup(), ignored => IsEditable && IsInspectionHandoffSetupVisible && TryCreateInspectionHandoffSetup(out _));
        _cancelInspectionHandoffSetupCommand = new RelayCommand(_ => ClearInspectionHandoffSetup(), _ => IsInspectionHandoffSetupVisible);
        _resetInspectionHandoffSetupCommand = new RelayCommand(_ => ResetInspectionHandoffSetup(), _ => IsEditable && IsInspectionHandoffSetupVisible);
        _previewInspectionSortRouterSetupCommand = new RelayCommand(_ => PreviewInspectionSortRouterSetup(), _ => IsEditable && _project is not null);
        _applyInspectionSortRouterSetupCommand = new RelayCommand(_ => ApplyInspectionSortRouterSetup(), ignored => IsEditable && IsInspectionSortRouterSetupVisible && TryCreateInspectionSortRouterSetup(out _));
        _cancelInspectionSortRouterSetupCommand = new RelayCommand(_ => ClearInspectionSortRouterSetup(), _ => IsInspectionSortRouterSetupVisible);
        _resetInspectionSortRouterSetupCommand = new RelayCommand(_ => ResetInspectionSortRouterSetup(), _ => IsEditable && IsInspectionSortRouterSetupVisible);
        _previewOhtHandoffSetupCommand = new RelayCommand(_ => PreviewOhtHandoffSetup(), _ => IsEditable && _project is not null);
        _applyOhtHandoffSetupCommand = new RelayCommand(_ => ApplyOhtHandoffSetup(), ignored => IsEditable && IsOhtHandoffSetupVisible && TryCreateOhtHandoffSetup(out _));
        _cancelOhtHandoffSetupCommand = new RelayCommand(_ => ClearOhtHandoffSetup(), _ => IsOhtHandoffSetupVisible);
        _resetOhtHandoffSetupCommand = new RelayCommand(_ => ResetOhtHandoffSetup(), _ => IsEditable && IsOhtHandoffSetupVisible);
    }

    public ObservableCollection<LoadLockSetupOption> WaferHandlerAxisOptions { get; } = new();
    public ObservableCollection<LoadLockSetupOption> WaferHandlerWorkpieceOptions { get; } = new();
    public ObservableCollection<LoadLockSetupOption> WaferHandlerInputOptions { get; } = new();
    public ObservableCollection<LoadLockSetupOption> WaferHandlerOutputOptions { get; } = new();
    public ObservableCollection<LoadLockSetupOption> PrealignerStageOptions { get; } = new();
    public ObservableCollection<LoadLockSetupOption> PrealignerCylinderOptions { get; } = new();
    public ObservableCollection<LoadLockSetupOption> PrealignerInputOptions { get; } = new();
    public ObservableCollection<LoadLockSetupOption> PrealignerOutputOptions { get; } = new();
    public ObservableCollection<LoadLockSetupOption> InspectionCameraOptions { get; } = new();
    public ObservableCollection<LoadLockSetupOption> InspectionInputOptions { get; } = new();
    public ObservableCollection<LoadLockSetupOption> InspectionOutputOptions { get; } = new();
    public ObservableCollection<LoadLockSetupOption> InspectionConveyorOptions { get; } = new();
    public ICommand PreviewWaferHandlerSetupCommand => _previewWaferHandlerSetupCommand;
    public ICommand ApplyWaferHandlerSetupCommand => _applyWaferHandlerSetupCommand;
    public ICommand CancelWaferHandlerSetupCommand => _cancelWaferHandlerSetupCommand;
    public ICommand ResetWaferHandlerSetupCommand => _resetWaferHandlerSetupCommand;
    public ICommand PreviewPrealignerSetupCommand => _previewPrealignerSetupCommand;
    public ICommand ApplyPrealignerSetupCommand => _applyPrealignerSetupCommand;
    public ICommand CancelPrealignerSetupCommand => _cancelPrealignerSetupCommand;
    public ICommand ResetPrealignerSetupCommand => _resetPrealignerSetupCommand;
    public ICommand PreviewInspectionHandoffSetupCommand => _previewInspectionHandoffSetupCommand;
    public ICommand ApplyInspectionHandoffSetupCommand => _applyInspectionHandoffSetupCommand;
    public ICommand CancelInspectionHandoffSetupCommand => _cancelInspectionHandoffSetupCommand;
    public ICommand ResetInspectionHandoffSetupCommand => _resetInspectionHandoffSetupCommand;
    public ICommand PreviewInspectionSortRouterSetupCommand => _previewInspectionSortRouterSetupCommand;
    public ICommand ApplyInspectionSortRouterSetupCommand => _applyInspectionSortRouterSetupCommand;
    public ICommand CancelInspectionSortRouterSetupCommand => _cancelInspectionSortRouterSetupCommand;
    public ICommand ResetInspectionSortRouterSetupCommand => _resetInspectionSortRouterSetupCommand;
    public ICommand PreviewOhtHandoffSetupCommand => _previewOhtHandoffSetupCommand;
    public ICommand ApplyOhtHandoffSetupCommand => _applyOhtHandoffSetupCommand;
    public ICommand CancelOhtHandoffSetupCommand => _cancelOhtHandoffSetupCommand;
    public ICommand ResetOhtHandoffSetupCommand => _resetOhtHandoffSetupCommand;

    public bool IsEditable
    {
        get => _isEditable;
        set
        {
            if (!SetProperty(ref _isEditable, value)) return;
            RaiseCommandStates();
        }
    }

    public bool IsWaferHandlerSetupVisible => _isWaferHandlerSetupVisible;
    public bool IsPrealignerSetupVisible => _isPrealignerSetupVisible;
    public bool IsInspectionHandoffSetupVisible => _isInspectionHandoffSetupVisible;
    public bool IsInspectionSortRouterSetupVisible => _isInspectionSortRouterSetupVisible;
    public bool IsOhtHandoffSetupVisible => _isOhtHandoffSetupVisible;
    public string? WaferHandlerHorizontalAxisId { get => _waferHandlerHorizontalAxisId; set => SetSelection(ref _waferHandlerHorizontalAxisId, value, nameof(WaferHandlerHorizontalAxisId)); }
    public string? WaferHandlerVerticalAxisId { get => _waferHandlerVerticalAxisId; set => SetSelection(ref _waferHandlerVerticalAxisId, value, nameof(WaferHandlerVerticalAxisId)); }
    public string? WaferHandlerWorkpieceComponentId { get => _waferHandlerWorkpieceComponentId; set => SetSelection(ref _waferHandlerWorkpieceComponentId, value, nameof(WaferHandlerWorkpieceComponentId)); }
    public string? WaferHandlerSourcePresentSensorChannelId { get => _waferHandlerSourcePresentSensorChannelId; set => SetSelection(ref _waferHandlerSourcePresentSensorChannelId, value, nameof(WaferHandlerSourcePresentSensorChannelId)); }
    public string? WaferHandlerGateOpenSensorChannelId { get => _waferHandlerGateOpenSensorChannelId; set => SetSelection(ref _waferHandlerGateOpenSensorChannelId, value, nameof(WaferHandlerGateOpenSensorChannelId)); }
    public string? WaferHandlerPickCommandChannelId { get => _waferHandlerPickCommandChannelId; set => SetSelection(ref _waferHandlerPickCommandChannelId, value, nameof(WaferHandlerPickCommandChannelId)); }
    public string? WaferHandlerPlaceCommandChannelId { get => _waferHandlerPlaceCommandChannelId; set => SetSelection(ref _waferHandlerPlaceCommandChannelId, value, nameof(WaferHandlerPlaceCommandChannelId)); }
    public string? WaferHandlerHoldingFeedbackChannelId { get => _waferHandlerHoldingFeedbackChannelId; set => SetSelection(ref _waferHandlerHoldingFeedbackChannelId, value, nameof(WaferHandlerHoldingFeedbackChannelId)); }
    public string? WaferHandlerPlacedFeedbackChannelId { get => _waferHandlerPlacedFeedbackChannelId; set => SetSelection(ref _waferHandlerPlacedFeedbackChannelId, value, nameof(WaferHandlerPlacedFeedbackChannelId)); }
    public string WaferHandlerPickHorizontalText { get => _waferHandlerPickHorizontalText; set => SetText(ref _waferHandlerPickHorizontalText, value, nameof(WaferHandlerPickHorizontalText)); }
    public string WaferHandlerPickVerticalText { get => _waferHandlerPickVerticalText; set => SetText(ref _waferHandlerPickVerticalText, value, nameof(WaferHandlerPickVerticalText)); }
    public string WaferHandlerPlaceHorizontalText { get => _waferHandlerPlaceHorizontalText; set => SetText(ref _waferHandlerPlaceHorizontalText, value, nameof(WaferHandlerPlaceHorizontalText)); }
    public string WaferHandlerPlaceVerticalText { get => _waferHandlerPlaceVerticalText; set => SetText(ref _waferHandlerPlaceVerticalText, value, nameof(WaferHandlerPlaceVerticalText)); }
    public bool HasMultipleWaferHandlers => _project?.Devices.Count(device => device.Kind == DeviceKind.Handler) > 1;
    public bool IsWaferHandlerHorizontalAxisValid => IsLinearAxis(WaferHandlerHorizontalAxisId);
    public bool IsWaferHandlerVerticalAxisValid => IsLinearAxis(WaferHandlerVerticalAxisId) && !Same(WaferHandlerHorizontalAxisId, WaferHandlerVerticalAxisId);
    public bool IsWaferHandlerWorkpieceValid => IsLayoutComponent(WaferHandlerWorkpieceComponentId, LayoutComponentKind.Workpiece);
    public bool IsWaferHandlerSourcePresentValid => IsChannel(WaferHandlerSourcePresentSensorChannelId, ChannelKind.DigitalInput);
    public bool IsWaferHandlerGateOpenValid => IsChannel(WaferHandlerGateOpenSensorChannelId, ChannelKind.DigitalInput);
    public bool IsWaferHandlerPickCommandValid => IsChannel(WaferHandlerPickCommandChannelId, ChannelKind.DigitalOutput);
    public bool IsWaferHandlerPlaceCommandValid => IsChannel(WaferHandlerPlaceCommandChannelId, ChannelKind.DigitalOutput);
    public bool IsWaferHandlerHoldingFeedbackValid => IsChannel(WaferHandlerHoldingFeedbackChannelId, ChannelKind.DigitalInput);
    public bool IsWaferHandlerPlacedFeedbackValid => IsChannel(WaferHandlerPlacedFeedbackChannelId, ChannelKind.DigitalInput);
    public bool IsWaferHandlerPickHorizontalValid => IsAxisPosition(WaferHandlerHorizontalAxisId, WaferHandlerPickHorizontalText);
    public bool IsWaferHandlerPickVerticalValid => IsAxisPosition(WaferHandlerVerticalAxisId, WaferHandlerPickVerticalText);
    public bool IsWaferHandlerPlaceHorizontalValid => IsAxisPosition(WaferHandlerHorizontalAxisId, WaferHandlerPlaceHorizontalText);
    public bool IsWaferHandlerPlaceVerticalValid => IsAxisPosition(WaferHandlerVerticalAxisId, WaferHandlerPlaceVerticalText);
    public bool HasWaferHandlerSetupValidationError => !TryCreateWaferHandlerSetup(out _);
    public string WaferHandlerSetupValidationText => ValidationText(HasMultipleWaferHandlers, HasWaferHandlerSetupValidationError, "Connections.WaferHandlerSetupMultipleError", "Connections.WaferHandlerSetupValidationError", "Connections.WaferHandlerSetupValidationReady");

    public string? PrealignerRotaryStageComponentId { get => _prealignerRotaryStageComponentId; set => SetSelection(ref _prealignerRotaryStageComponentId, value, nameof(PrealignerRotaryStageComponentId)); }
    public string? PrealignerClampCylinderComponentId { get => _prealignerClampCylinderComponentId; set => SetSelection(ref _prealignerClampCylinderComponentId, value, nameof(PrealignerClampCylinderComponentId)); }
    public string? PrealignerWaferPresentSensorChannelId { get => _prealignerWaferPresentSensorChannelId; set => SetSelection(ref _prealignerWaferPresentSensorChannelId, value, nameof(PrealignerWaferPresentSensorChannelId)); }
    public string? PrealignerAlignmentAcceptedCommandChannelId { get => _prealignerAlignmentAcceptedCommandChannelId; set => SetSelection(ref _prealignerAlignmentAcceptedCommandChannelId, value, nameof(PrealignerAlignmentAcceptedCommandChannelId)); }
    public string? PrealignerAlignmentReadyFeedbackChannelId { get => _prealignerAlignmentReadyFeedbackChannelId; set => SetSelection(ref _prealignerAlignmentReadyFeedbackChannelId, value, nameof(PrealignerAlignmentReadyFeedbackChannelId)); }
    public string? PrealignerAlignmentCompleteFeedbackChannelId { get => _prealignerAlignmentCompleteFeedbackChannelId; set => SetSelection(ref _prealignerAlignmentCompleteFeedbackChannelId, value, nameof(PrealignerAlignmentCompleteFeedbackChannelId)); }
    public string PrealignerAlignmentTargetText { get => _prealignerAlignmentTargetText; set => SetText(ref _prealignerAlignmentTargetText, value, nameof(PrealignerAlignmentTargetText)); }
    public string PrealignerAlignmentToleranceText { get => _prealignerAlignmentToleranceText; set => SetText(ref _prealignerAlignmentToleranceText, value, nameof(PrealignerAlignmentToleranceText)); }
    public bool HasMultiplePrealigners => _project?.Devices.Count(device => device.Kind == DeviceKind.Prealigner) > 1;
    public bool IsPrealignerRotaryStageValid => TryGetPrealignerStage(PrealignerRotaryStageComponentId, out _);
    public bool IsPrealignerClampCylinderValid => IsLayoutComponent(PrealignerClampCylinderComponentId, LayoutComponentKind.PneumaticCylinder);
    public bool IsPrealignerWaferPresentValid => IsChannel(PrealignerWaferPresentSensorChannelId, ChannelKind.DigitalInput);
    public bool IsPrealignerAlignmentAcceptedValid => IsChannel(PrealignerAlignmentAcceptedCommandChannelId, ChannelKind.DigitalOutput);
    public bool IsPrealignerAlignmentReadyValid => IsChannel(PrealignerAlignmentReadyFeedbackChannelId, ChannelKind.DigitalInput);
    public bool IsPrealignerAlignmentCompleteValid => IsChannel(PrealignerAlignmentCompleteFeedbackChannelId, ChannelKind.DigitalInput);
    public bool IsPrealignerAlignmentTargetValid => IsRotaryPosition(PrealignerRotaryStageComponentId, PrealignerAlignmentTargetText);
    public bool IsPrealignerAlignmentToleranceValid => TryPositiveDouble(PrealignerAlignmentToleranceText, out _);
    public bool HasPrealignerSetupValidationError => !TryCreatePrealignerSetup(out _);
    public string PrealignerSetupValidationText => ValidationText(HasMultiplePrealigners, HasPrealignerSetupValidationError, "Connections.PrealignerSetupMultipleError", "Connections.PrealignerSetupValidationError", "Connections.PrealignerSetupValidationReady");

    public string? InspectionHandoffCameraId { get => _inspectionHandoffCameraId; set => SetSelection(ref _inspectionHandoffCameraId, value, nameof(InspectionHandoffCameraId)); }
    public string? InspectionHandoffPositionSensorChannelId { get => _inspectionHandoffPositionSensorChannelId; set => SetSelection(ref _inspectionHandoffPositionSensorChannelId, value, nameof(InspectionHandoffPositionSensorChannelId)); }
    public string? InspectionHandoffAcceptedChannelId { get => _inspectionHandoffAcceptedChannelId; set => SetSelection(ref _inspectionHandoffAcceptedChannelId, value, nameof(InspectionHandoffAcceptedChannelId)); }
    public string? InspectionHandoffReadyChannelId { get => _inspectionHandoffReadyChannelId; set => SetSelection(ref _inspectionHandoffReadyChannelId, value, nameof(InspectionHandoffReadyChannelId)); }
    public string? InspectionHandoffCompleteChannelId { get => _inspectionHandoffCompleteChannelId; set => SetSelection(ref _inspectionHandoffCompleteChannelId, value, nameof(InspectionHandoffCompleteChannelId)); }
    public bool IsInspectionHandoffCameraValid => IsCamera(InspectionHandoffCameraId);
    public bool IsInspectionHandoffPositionValid => IsChannel(InspectionHandoffPositionSensorChannelId, ChannelKind.DigitalInput);
    public bool IsInspectionHandoffAcceptedValid => IsChannel(InspectionHandoffAcceptedChannelId, ChannelKind.DigitalOutput);
    public bool IsInspectionHandoffReadyValid => IsChannel(InspectionHandoffReadyChannelId, ChannelKind.DigitalInput);
    public bool IsInspectionHandoffCompleteValid => IsChannel(InspectionHandoffCompleteChannelId, ChannelKind.DigitalInput);
    public bool HasMultipleInspectionHandoffs => _project?.Devices.Count(device => device.Kind == DeviceKind.Inspection) > 1;
    public bool HasInspectionHandoffSetupValidationError => !TryCreateInspectionHandoffSetup(out _);
    public string InspectionHandoffSetupValidationText => ValidationText(HasMultipleInspectionHandoffs, HasInspectionHandoffSetupValidationError, "Connections.InspectionHandoffSetupMultipleError", "Connections.InspectionHandoffSetupValidationError", "Connections.InspectionHandoffSetupValidationReady");

    public string? InspectionSortCameraId { get => _inspectionSortCameraId; set => SetSelection(ref _inspectionSortCameraId, value, nameof(InspectionSortCameraId)); }
    public string? InspectionSortPassConveyorId { get => _inspectionSortPassConveyorId; set => SetSelection(ref _inspectionSortPassConveyorId, value, nameof(InspectionSortPassConveyorId)); }
    public string? InspectionSortNgConveyorId { get => _inspectionSortNgConveyorId; set => SetSelection(ref _inspectionSortNgConveyorId, value, nameof(InspectionSortNgConveyorId)); }
    public string? InspectionSortPassFeedbackChannelId { get => _inspectionSortPassFeedbackChannelId; set => SetSelection(ref _inspectionSortPassFeedbackChannelId, value, nameof(InspectionSortPassFeedbackChannelId)); }
    public string? InspectionSortNgFeedbackChannelId { get => _inspectionSortNgFeedbackChannelId; set => SetSelection(ref _inspectionSortNgFeedbackChannelId, value, nameof(InspectionSortNgFeedbackChannelId)); }
    public bool IsInspectionSortCameraValid => IsCamera(InspectionSortCameraId);
    public bool IsInspectionSortPassConveyorValid => IsLayoutComponent(InspectionSortPassConveyorId, LayoutComponentKind.Conveyor);
    public bool IsInspectionSortNgConveyorValid => IsLayoutComponent(InspectionSortNgConveyorId, LayoutComponentKind.Conveyor) && !Same(InspectionSortPassConveyorId, InspectionSortNgConveyorId);
    public bool IsInspectionSortPassFeedbackValid => IsChannel(InspectionSortPassFeedbackChannelId, ChannelKind.DigitalInput);
    public bool IsInspectionSortNgFeedbackValid => IsChannel(InspectionSortNgFeedbackChannelId, ChannelKind.DigitalInput) && !Same(InspectionSortPassFeedbackChannelId, InspectionSortNgFeedbackChannelId);
    public bool HasMultipleInspectionSortRouters => _project?.Devices.Count(device => device.Kind == DeviceKind.Sorter) > 1;
    public bool HasInspectionSortRouterSetupValidationError => !TryCreateInspectionSortRouterSetup(out _);
    public string InspectionSortRouterSetupValidationText => ValidationText(HasMultipleInspectionSortRouters, HasInspectionSortRouterSetupValidationError, "Connections.InspectionSortSetupMultipleError", "Connections.InspectionSortSetupValidationError", "Connections.InspectionSortSetupValidationReady");

    public string? OhtTransportConveyorId { get => _ohtTransportConveyorId; set => SetSelection(ref _ohtTransportConveyorId, value, nameof(OhtTransportConveyorId)); }
    public string? OhtRouteAvailableChannelId { get => _ohtRouteAvailableChannelId; set => SetSelection(ref _ohtRouteAvailableChannelId, value, nameof(OhtRouteAvailableChannelId)); }
    public string? OhtVehicleDockedChannelId { get => _ohtVehicleDockedChannelId; set => SetSelection(ref _ohtVehicleDockedChannelId, value, nameof(OhtVehicleDockedChannelId)); }
    public string? OhtLoadPortReadyChannelId { get => _ohtLoadPortReadyChannelId; set => SetSelection(ref _ohtLoadPortReadyChannelId, value, nameof(OhtLoadPortReadyChannelId)); }
    public string? OhtCarrierReceivedChannelId { get => _ohtCarrierReceivedChannelId; set => SetSelection(ref _ohtCarrierReceivedChannelId, value, nameof(OhtCarrierReceivedChannelId)); }
    public string? OhtHandoffReadyChannelId { get => _ohtHandoffReadyChannelId; set => SetSelection(ref _ohtHandoffReadyChannelId, value, nameof(OhtHandoffReadyChannelId)); }
    public string? OhtCarrierTransferredChannelId { get => _ohtCarrierTransferredChannelId; set => SetSelection(ref _ohtCarrierTransferredChannelId, value, nameof(OhtCarrierTransferredChannelId)); }
    public bool IsOhtTransportConveyorValid => IsLayoutComponent(OhtTransportConveyorId, LayoutComponentKind.Conveyor);
    public bool IsOhtRouteAvailableValid => IsChannel(OhtRouteAvailableChannelId, ChannelKind.DigitalInput);
    public bool IsOhtVehicleDockedValid => IsChannel(OhtVehicleDockedChannelId, ChannelKind.DigitalInput);
    public bool IsOhtLoadPortReadyValid => IsChannel(OhtLoadPortReadyChannelId, ChannelKind.DigitalInput);
    public bool IsOhtCarrierReceivedValid => IsChannel(OhtCarrierReceivedChannelId, ChannelKind.DigitalInput);
    public bool IsOhtHandoffReadyValid => IsChannel(OhtHandoffReadyChannelId, ChannelKind.DigitalInput);
    public bool IsOhtCarrierTransferredValid => IsChannel(OhtCarrierTransferredChannelId, ChannelKind.DigitalInput);
    public bool HasMultipleOhtHandoffs => _project?.Devices.Count(device => device.Kind == DeviceKind.Oht) > 1;
    public bool HasOhtHandoffSetupValidationError => !TryCreateOhtHandoffSetup(out _);
    public string OhtHandoffSetupValidationText => ValidationText(HasMultipleOhtHandoffs, HasOhtHandoffSetupValidationError, "Connections.OhtSetupMultipleError", "Connections.OhtSetupValidationError", "Connections.OhtSetupValidationReady");

    public void Load(MachineProjectDocument project)
    {
        _project = project ?? throw new ArgumentNullException(nameof(project));
        ClearAll();
        RaiseCommandStates();
    }

    public void ClearPreviewForCompetingSetup() => ClearAll();

    internal void RefreshLocalization(Action reloadWorkbench)
    {
        var waferHandler = IsWaferHandlerSetupVisible ? CaptureWaferHandlerDraft() : null;
        var prealigner = IsPrealignerSetupVisible ? CapturePrealignerDraft() : null;
        var inspectionHandoff = IsInspectionHandoffSetupVisible ? CaptureInspectionHandoffDraft() : null;
        var inspectionSort = IsInspectionSortRouterSetupVisible ? CaptureInspectionSortRouterDraft() : null;
        var oht = IsOhtHandoffSetupVisible ? CaptureOhtHandoffDraft() : null;
        reloadWorkbench();
        if (waferHandler is not null) { PreviewWaferHandlerSetup(); RestoreWaferHandlerDraft(waferHandler); }
        if (prealigner is not null) { PreviewPrealignerSetup(); RestorePrealignerDraft(prealigner); }
        if (inspectionHandoff is not null) { PreviewInspectionHandoffSetup(); RestoreInspectionHandoffDraft(inspectionHandoff); }
        if (inspectionSort is not null) { PreviewInspectionSortRouterSetup(); RestoreInspectionSortRouterDraft(inspectionSort); }
        if (oht is not null) { PreviewOhtHandoffSetup(); RestoreOhtHandoffDraft(oht); }
    }

    private void PreviewWaferHandlerSetup()
    {
        if (_project is null) return;
        _clearWorkbenchPreviews();
        ClearPrealignerSetup();
        WaferHandlerAxisOptions.Clear(); WaferHandlerWorkpieceOptions.Clear(); WaferHandlerInputOptions.Clear(); WaferHandlerOutputOptions.Clear();
        foreach (var axis in _project.Axes.Where(axis => axis.Kind == AxisKind.Linear).OrderBy(axis => axis.Name, StringComparer.CurrentCulture).ThenBy(axis => axis.Id, StringComparer.Ordinal))
            WaferHandlerAxisOptions.Add(Option(axis.Id, axis.Name));
        foreach (var component in (ActiveLayout()?.Components ?? []).Where(component => component.Kind == LayoutComponentKind.Workpiece).OrderBy(component => component.Name, StringComparer.CurrentCulture).ThenBy(component => component.Id, StringComparer.Ordinal))
            WaferHandlerWorkpieceOptions.Add(Option(component.Id, component.Name));
        foreach (var channel in _project.Channels.OrderBy(channel => channel.Name, StringComparer.CurrentCulture).ThenBy(channel => channel.Id, StringComparer.Ordinal))
        {
            if (channel.Kind == ChannelKind.DigitalInput) WaferHandlerInputOptions.Add(Option(channel.Id, channel.Name));
            if (channel.Kind == ChannelKind.DigitalOutput) WaferHandlerOutputOptions.Add(Option(channel.Id, channel.Name));
        }
        var existing = _project.Devices.Where(device => device is { Kind: DeviceKind.Handler, WaferHandler: not null }).ToArray();
        _savedWaferHandlerSetup = existing.Length == 1 ? Clone(existing[0].WaferHandler!) : null;
        var draft = _savedWaferHandlerSetup ?? SuggestWaferHandlerSetup();
        AddMissing(WaferHandlerAxisOptions, draft.HorizontalAxisId); AddMissing(WaferHandlerAxisOptions, draft.VerticalAxisId); AddMissing(WaferHandlerWorkpieceOptions, draft.WorkpieceComponentId);
        foreach (var id in new[] { draft.SourcePresentSensorChannelId, draft.GateOpenSensorChannelId, draft.HoldingFeedbackChannelId, draft.PlacedFeedbackChannelId }) AddMissing(WaferHandlerInputOptions, id);
        foreach (var id in new[] { draft.PickCommandChannelId, draft.PlaceCommandChannelId }) AddMissing(WaferHandlerOutputOptions, id);
        ApplyDraft(draft); _isWaferHandlerSetupVisible = true; RaiseStateChanged();
    }

    private void PreviewPrealignerSetup()
    {
        if (_project is null) return;
        _clearWorkbenchPreviews();
        ClearWaferHandlerSetup();
        PrealignerStageOptions.Clear(); PrealignerCylinderOptions.Clear(); PrealignerInputOptions.Clear(); PrealignerOutputOptions.Clear();
        foreach (var component in (ActiveLayout()?.Components ?? []).Where(component => component.Kind == LayoutComponentKind.RotaryStage).OrderBy(component => component.Name, StringComparer.CurrentCulture).ThenBy(component => component.Id, StringComparer.Ordinal)) PrealignerStageOptions.Add(Option(component.Id, component.Name));
        foreach (var component in (ActiveLayout()?.Components ?? []).Where(component => component.Kind == LayoutComponentKind.PneumaticCylinder).OrderBy(component => component.Name, StringComparer.CurrentCulture).ThenBy(component => component.Id, StringComparer.Ordinal)) PrealignerCylinderOptions.Add(Option(component.Id, component.Name));
        foreach (var channel in _project.Channels.OrderBy(channel => channel.Name, StringComparer.CurrentCulture).ThenBy(channel => channel.Id, StringComparer.Ordinal))
        {
            if (channel.Kind == ChannelKind.DigitalInput) PrealignerInputOptions.Add(Option(channel.Id, channel.Name));
            if (channel.Kind == ChannelKind.DigitalOutput) PrealignerOutputOptions.Add(Option(channel.Id, channel.Name));
        }
        var existing = _project.Devices.Where(device => device is { Kind: DeviceKind.Prealigner, Prealigner: not null }).ToArray();
        _savedPrealignerSetup = existing.Length == 1 ? Clone(existing[0].Prealigner!) : null;
        var draft = _savedPrealignerSetup ?? SuggestPrealignerSetup();
        AddMissing(PrealignerStageOptions, draft.RotaryStageComponentId); AddMissing(PrealignerCylinderOptions, draft.ClampCylinderComponentId);
        foreach (var id in new[] { draft.WaferPresentSensorChannelId, draft.AlignmentReadyFeedbackChannelId, draft.AlignmentCompleteFeedbackChannelId }) AddMissing(PrealignerInputOptions, id);
        AddMissing(PrealignerOutputOptions, draft.AlignmentAcceptedCommandChannelId);
        ApplyDraft(draft); _isPrealignerSetupVisible = true; RaiseStateChanged();
    }

    private void PreviewInspectionHandoffSetup()
    {
        if (_project is null) return;
        PrepareInspectionPreview(); ClearInspectionSortRouterSetup(); ClearOhtHandoffSetup();
        var existing = _project.Devices.Where(device => device is { Kind: DeviceKind.Inspection, InspectionHandoff: not null }).ToArray();
        _savedInspectionHandoffSetup = existing.Length == 1 ? Clone(existing[0].InspectionHandoff!) : null;
        var draft = _savedInspectionHandoffSetup ?? SuggestInspectionHandoffSetup();
        AddMissing(InspectionCameraOptions, draft.CameraId); foreach (var id in new[] { draft.InspectionPositionSensorChannelId, draft.InspectionReadyFeedbackChannelId, draft.InspectionCompleteFeedbackChannelId }) AddMissing(InspectionInputOptions, id); AddMissing(InspectionOutputOptions, draft.ResultAcceptedCommandChannelId);
        ApplyDraft(draft); _isInspectionHandoffSetupVisible = true; RaiseStateChanged();
    }

    private void PreviewInspectionSortRouterSetup()
    {
        if (_project is null) return;
        PrepareInspectionPreview(); ClearInspectionHandoffSetup(); ClearOhtHandoffSetup();
        var existing = _project.Devices.Where(device => device is { Kind: DeviceKind.Sorter, InspectionSortRouter: not null }).ToArray();
        _savedInspectionSortRouterSetup = existing.Length == 1 ? Clone(existing[0].InspectionSortRouter!) : null;
        var draft = _savedInspectionSortRouterSetup ?? SuggestInspectionSortRouterSetup();
        AddMissing(InspectionCameraOptions, draft.CameraId); AddMissing(InspectionConveyorOptions, draft.PassConveyorComponentId); AddMissing(InspectionConveyorOptions, draft.NgConveyorComponentId); AddMissing(InspectionInputOptions, draft.PassRoutedFeedbackChannelId); AddMissing(InspectionInputOptions, draft.NgRoutedFeedbackChannelId);
        ApplyDraft(draft); _isInspectionSortRouterSetupVisible = true; RaiseStateChanged();
    }

    private void PreviewOhtHandoffSetup()
    {
        if (_project is null) return;
        PrepareInspectionPreview(); ClearInspectionHandoffSetup(); ClearInspectionSortRouterSetup();
        var existing = _project.Devices.Where(device => device is { Kind: DeviceKind.Oht, OhtHandoff: not null }).ToArray();
        _savedOhtHandoffSetup = existing.Length == 1 ? Clone(existing[0].OhtHandoff!) : null;
        var draft = _savedOhtHandoffSetup ?? SuggestOhtHandoffSetup();
        AddMissing(InspectionConveyorOptions, draft.TransportConveyorComponentId); foreach (var id in new[] { draft.RouteAvailableSensorChannelId, draft.VehicleDockedSensorChannelId, draft.LoadPortReadySensorChannelId, draft.CarrierReceivedSensorChannelId, draft.HandoffReadyFeedbackChannelId, draft.CarrierTransferredFeedbackChannelId }) AddMissing(InspectionInputOptions, id);
        ApplyDraft(draft); _isOhtHandoffSetupVisible = true; RaiseStateChanged();
    }

    private void PrepareInspectionPreview()
    {
        _clearWorkbenchPreviews(); ClearWaferHandlerSetup(); ClearPrealignerSetup();
        InspectionCameraOptions.Clear(); InspectionInputOptions.Clear(); InspectionOutputOptions.Clear(); InspectionConveyorOptions.Clear();
        foreach (var camera in _project!.Devices.Where(device => device is { Kind: DeviceKind.Camera, Camera: not null }).OrderBy(device => device.Name, StringComparer.CurrentCulture).ThenBy(device => device.Id, StringComparer.Ordinal)) InspectionCameraOptions.Add(Option(camera.Id, camera.Name));
        foreach (var component in (ActiveLayout()?.Components ?? []).Where(component => component.Kind == LayoutComponentKind.Conveyor).OrderBy(component => component.Name, StringComparer.CurrentCulture).ThenBy(component => component.Id, StringComparer.Ordinal)) InspectionConveyorOptions.Add(Option(component.Id, component.Name));
        foreach (var channel in _project.Channels.Where(channel => channel.Kind == ChannelKind.DigitalInput).OrderBy(channel => channel.Name, StringComparer.CurrentCulture).ThenBy(channel => channel.Id, StringComparer.Ordinal)) InspectionInputOptions.Add(Option(channel.Id, channel.Name));
        foreach (var channel in _project.Channels.Where(channel => channel.Kind == ChannelKind.DigitalOutput).OrderBy(channel => channel.Name, StringComparer.CurrentCulture).ThenBy(channel => channel.Id, StringComparer.Ordinal)) InspectionOutputOptions.Add(Option(channel.Id, channel.Name));
    }

    private void ApplyWaferHandlerSetup() => Apply(_applyWaferHandlerSetup, TryCreateWaferHandlerSetup, IsEquivalentToSaved, ClearWaferHandlerSetup, PreviewWaferHandlerSetup);
    private void ApplyPrealignerSetup() => Apply(_applyPrealignerSetup, TryCreatePrealignerSetup, IsEquivalentToSaved, ClearPrealignerSetup, PreviewPrealignerSetup);
    private void ApplyInspectionHandoffSetup() => Apply(_applyInspectionHandoffSetup, TryCreateInspectionHandoffSetup, IsEquivalentToSaved, ClearInspectionHandoffSetup, PreviewInspectionHandoffSetup);
    private void ApplyInspectionSortRouterSetup() => Apply(_applyInspectionSortRouterSetup, TryCreateInspectionSortRouterSetup, IsEquivalentToSaved, ClearInspectionSortRouterSetup, PreviewInspectionSortRouterSetup);
    private void ApplyOhtHandoffSetup() => Apply(_applyOhtHandoffSetup, TryCreateOhtHandoffSetup, IsEquivalentToSaved, ClearOhtHandoffSetup, PreviewOhtHandoffSetup);
    private void ResetWaferHandlerSetup() => ApplyDraft(_savedWaferHandlerSetup is null ? SuggestWaferHandlerSetup() : Clone(_savedWaferHandlerSetup));
    private void ResetPrealignerSetup() => ApplyDraft(_savedPrealignerSetup is null ? SuggestPrealignerSetup() : Clone(_savedPrealignerSetup));
    private void ResetInspectionHandoffSetup() => ApplyDraft(_savedInspectionHandoffSetup is null ? SuggestInspectionHandoffSetup() : Clone(_savedInspectionHandoffSetup));
    private void ResetInspectionSortRouterSetup() => ApplyDraft(_savedInspectionSortRouterSetup is null ? SuggestInspectionSortRouterSetup() : Clone(_savedInspectionSortRouterSetup));
    private void ResetOhtHandoffSetup() => ApplyDraft(_savedOhtHandoffSetup is null ? SuggestOhtHandoffSetup() : Clone(_savedOhtHandoffSetup));

    private void ClearAll()
    {
        ClearWaferHandlerSetup(); ClearPrealignerSetup(); ClearInspectionHandoffSetup(); ClearInspectionSortRouterSetup(); ClearOhtHandoffSetup();
        InspectionCameraOptions.Clear(); InspectionInputOptions.Clear(); InspectionOutputOptions.Clear(); InspectionConveyorOptions.Clear();
    }

    private void ClearWaferHandlerSetup() { _isWaferHandlerSetupVisible = false; _savedWaferHandlerSetup = null; WaferHandlerAxisOptions.Clear(); WaferHandlerWorkpieceOptions.Clear(); WaferHandlerInputOptions.Clear(); WaferHandlerOutputOptions.Clear(); RaiseStateChanged(); }
    private void ClearPrealignerSetup() { _isPrealignerSetupVisible = false; _savedPrealignerSetup = null; PrealignerStageOptions.Clear(); PrealignerCylinderOptions.Clear(); PrealignerInputOptions.Clear(); PrealignerOutputOptions.Clear(); RaiseStateChanged(); }
    private void ClearInspectionHandoffSetup() { _isInspectionHandoffSetupVisible = false; _savedInspectionHandoffSetup = null; RaiseStateChanged(); }
    private void ClearInspectionSortRouterSetup() { _isInspectionSortRouterSetupVisible = false; _savedInspectionSortRouterSetup = null; RaiseStateChanged(); }
    private void ClearOhtHandoffSetup() { _isOhtHandoffSetupVisible = false; _savedOhtHandoffSetup = null; RaiseStateChanged(); }

    private WaferHandlerDefinition SuggestWaferHandlerSetup() => new()
    {
        HorizontalAxisId = WaferHandlerAxisOptions.ElementAtOrDefault(0)?.Id ?? string.Empty, VerticalAxisId = WaferHandlerAxisOptions.ElementAtOrDefault(1)?.Id ?? string.Empty, WorkpieceComponentId = WaferHandlerWorkpieceOptions.FirstOrDefault()?.Id ?? string.Empty,
        SourcePresentSensorChannelId = WaferHandlerInputOptions.ElementAtOrDefault(0)?.Id ?? string.Empty, GateOpenSensorChannelId = WaferHandlerInputOptions.ElementAtOrDefault(1)?.Id ?? string.Empty, HoldingFeedbackChannelId = WaferHandlerInputOptions.ElementAtOrDefault(2)?.Id ?? string.Empty, PlacedFeedbackChannelId = WaferHandlerInputOptions.ElementAtOrDefault(3)?.Id ?? string.Empty,
        PickCommandChannelId = WaferHandlerOutputOptions.ElementAtOrDefault(0)?.Id ?? string.Empty, PlaceCommandChannelId = WaferHandlerOutputOptions.ElementAtOrDefault(1)?.Id ?? string.Empty,
        PickHorizontalPosition = AxisLimit(WaferHandlerAxisOptions.ElementAtOrDefault(0)?.Id, false), PickVerticalPosition = AxisLimit(WaferHandlerAxisOptions.ElementAtOrDefault(1)?.Id, false), PlaceHorizontalPosition = AxisLimit(WaferHandlerAxisOptions.ElementAtOrDefault(0)?.Id, true), PlaceVerticalPosition = AxisLimit(WaferHandlerAxisOptions.ElementAtOrDefault(1)?.Id, true)
    };
    private PrealignerDefinition SuggestPrealignerSetup() => new() { RotaryStageComponentId = PrealignerStageOptions.FirstOrDefault()?.Id ?? string.Empty, ClampCylinderComponentId = PrealignerCylinderOptions.FirstOrDefault()?.Id ?? string.Empty, WaferPresentSensorChannelId = PrealignerInputOptions.ElementAtOrDefault(0)?.Id ?? string.Empty, AlignmentReadyFeedbackChannelId = PrealignerInputOptions.ElementAtOrDefault(1)?.Id ?? string.Empty, AlignmentCompleteFeedbackChannelId = PrealignerInputOptions.ElementAtOrDefault(2)?.Id ?? string.Empty, AlignmentAcceptedCommandChannelId = PrealignerOutputOptions.FirstOrDefault()?.Id ?? string.Empty, AlignmentTargetDegrees = StageAxisLimit(PrealignerStageOptions.FirstOrDefault()?.Id), AlignmentToleranceDegrees = 0.1 };
    private InspectionHandoffDefinition SuggestInspectionHandoffSetup() => new() { CameraId = InspectionCameraOptions.FirstOrDefault()?.Id ?? string.Empty, InspectionPositionSensorChannelId = InspectionInputOptions.ElementAtOrDefault(0)?.Id ?? string.Empty, InspectionReadyFeedbackChannelId = InspectionInputOptions.ElementAtOrDefault(1)?.Id ?? string.Empty, InspectionCompleteFeedbackChannelId = InspectionInputOptions.ElementAtOrDefault(2)?.Id ?? string.Empty, ResultAcceptedCommandChannelId = InspectionOutputOptions.FirstOrDefault()?.Id ?? string.Empty };
    private InspectionSortRouterDefinition SuggestInspectionSortRouterSetup() => new() { CameraId = InspectionCameraOptions.FirstOrDefault()?.Id ?? string.Empty, PassConveyorComponentId = InspectionConveyorOptions.ElementAtOrDefault(0)?.Id ?? string.Empty, NgConveyorComponentId = InspectionConveyorOptions.ElementAtOrDefault(1)?.Id ?? string.Empty, PassRoutedFeedbackChannelId = InspectionInputOptions.ElementAtOrDefault(0)?.Id ?? string.Empty, NgRoutedFeedbackChannelId = InspectionInputOptions.ElementAtOrDefault(1)?.Id ?? string.Empty };
    private OhtHandoffDefinition SuggestOhtHandoffSetup() => new() { TransportConveyorComponentId = InspectionConveyorOptions.FirstOrDefault()?.Id ?? string.Empty, RouteAvailableSensorChannelId = InspectionInputOptions.ElementAtOrDefault(0)?.Id ?? string.Empty, VehicleDockedSensorChannelId = InspectionInputOptions.ElementAtOrDefault(1)?.Id ?? string.Empty, LoadPortReadySensorChannelId = InspectionInputOptions.ElementAtOrDefault(2)?.Id ?? string.Empty, CarrierReceivedSensorChannelId = InspectionInputOptions.ElementAtOrDefault(3)?.Id ?? string.Empty, HandoffReadyFeedbackChannelId = InspectionInputOptions.ElementAtOrDefault(4)?.Id ?? string.Empty, CarrierTransferredFeedbackChannelId = InspectionInputOptions.ElementAtOrDefault(5)?.Id ?? string.Empty };

    private void ApplyDraft(WaferHandlerDefinition value) { _waferHandlerHorizontalAxisId = value.HorizontalAxisId; _waferHandlerVerticalAxisId = value.VerticalAxisId; _waferHandlerWorkpieceComponentId = value.WorkpieceComponentId; _waferHandlerSourcePresentSensorChannelId = value.SourcePresentSensorChannelId; _waferHandlerGateOpenSensorChannelId = value.GateOpenSensorChannelId; _waferHandlerPickCommandChannelId = value.PickCommandChannelId; _waferHandlerPlaceCommandChannelId = value.PlaceCommandChannelId; _waferHandlerHoldingFeedbackChannelId = value.HoldingFeedbackChannelId; _waferHandlerPlacedFeedbackChannelId = value.PlacedFeedbackChannelId; _waferHandlerPickHorizontalText = Format(value.PickHorizontalPosition); _waferHandlerPickVerticalText = Format(value.PickVerticalPosition); _waferHandlerPlaceHorizontalText = Format(value.PlaceHorizontalPosition); _waferHandlerPlaceVerticalText = Format(value.PlaceVerticalPosition); RaiseStateChanged(); }
    private void ApplyDraft(PrealignerDefinition value) { _prealignerRotaryStageComponentId = value.RotaryStageComponentId; _prealignerClampCylinderComponentId = value.ClampCylinderComponentId; _prealignerWaferPresentSensorChannelId = value.WaferPresentSensorChannelId; _prealignerAlignmentAcceptedCommandChannelId = value.AlignmentAcceptedCommandChannelId; _prealignerAlignmentReadyFeedbackChannelId = value.AlignmentReadyFeedbackChannelId; _prealignerAlignmentCompleteFeedbackChannelId = value.AlignmentCompleteFeedbackChannelId; _prealignerAlignmentTargetText = Format(value.AlignmentTargetDegrees); _prealignerAlignmentToleranceText = Format(value.AlignmentToleranceDegrees); RaiseStateChanged(); }
    private void ApplyDraft(InspectionHandoffDefinition value) { _inspectionHandoffCameraId = value.CameraId; _inspectionHandoffPositionSensorChannelId = value.InspectionPositionSensorChannelId; _inspectionHandoffAcceptedChannelId = value.ResultAcceptedCommandChannelId; _inspectionHandoffReadyChannelId = value.InspectionReadyFeedbackChannelId; _inspectionHandoffCompleteChannelId = value.InspectionCompleteFeedbackChannelId; RaiseStateChanged(); }
    private void ApplyDraft(InspectionSortRouterDefinition value) { _inspectionSortCameraId = value.CameraId; _inspectionSortPassConveyorId = value.PassConveyorComponentId; _inspectionSortNgConveyorId = value.NgConveyorComponentId; _inspectionSortPassFeedbackChannelId = value.PassRoutedFeedbackChannelId; _inspectionSortNgFeedbackChannelId = value.NgRoutedFeedbackChannelId; RaiseStateChanged(); }
    private void ApplyDraft(OhtHandoffDefinition value) { _ohtTransportConveyorId = value.TransportConveyorComponentId; _ohtRouteAvailableChannelId = value.RouteAvailableSensorChannelId; _ohtVehicleDockedChannelId = value.VehicleDockedSensorChannelId; _ohtLoadPortReadyChannelId = value.LoadPortReadySensorChannelId; _ohtCarrierReceivedChannelId = value.CarrierReceivedSensorChannelId; _ohtHandoffReadyChannelId = value.HandoffReadyFeedbackChannelId; _ohtCarrierTransferredChannelId = value.CarrierTransferredFeedbackChannelId; RaiseStateChanged(); }

    private bool TryCreateWaferHandlerSetup(out WaferHandlerDefinition value)
    {
        var channels = new[] { WaferHandlerSourcePresentSensorChannelId, WaferHandlerGateOpenSensorChannelId, WaferHandlerPickCommandChannelId, WaferHandlerPlaceCommandChannelId, WaferHandlerHoldingFeedbackChannelId, WaferHandlerPlacedFeedbackChannelId };
        value = new WaferHandlerDefinition { HorizontalAxisId = WaferHandlerHorizontalAxisId ?? string.Empty, VerticalAxisId = WaferHandlerVerticalAxisId ?? string.Empty, WorkpieceComponentId = WaferHandlerWorkpieceComponentId ?? string.Empty, SourcePresentSensorChannelId = WaferHandlerSourcePresentSensorChannelId ?? string.Empty, GateOpenSensorChannelId = WaferHandlerGateOpenSensorChannelId ?? string.Empty, PickCommandChannelId = WaferHandlerPickCommandChannelId ?? string.Empty, PlaceCommandChannelId = WaferHandlerPlaceCommandChannelId ?? string.Empty, HoldingFeedbackChannelId = WaferHandlerHoldingFeedbackChannelId ?? string.Empty, PlacedFeedbackChannelId = WaferHandlerPlacedFeedbackChannelId ?? string.Empty, PickHorizontalPosition = Parse(WaferHandlerPickHorizontalText), PickVerticalPosition = Parse(WaferHandlerPickVerticalText), PlaceHorizontalPosition = Parse(WaferHandlerPlaceHorizontalText), PlaceVerticalPosition = Parse(WaferHandlerPlaceVerticalText) };
        return !HasMultipleWaferHandlers && IsWaferHandlerHorizontalAxisValid && IsWaferHandlerVerticalAxisValid && IsWaferHandlerWorkpieceValid && Distinct(channels) && IsWaferHandlerSourcePresentValid && IsWaferHandlerGateOpenValid && IsWaferHandlerPickCommandValid && IsWaferHandlerPlaceCommandValid && IsWaferHandlerHoldingFeedbackValid && IsWaferHandlerPlacedFeedbackValid && IsWaferHandlerPickHorizontalValid && IsWaferHandlerPickVerticalValid && IsWaferHandlerPlaceHorizontalValid && IsWaferHandlerPlaceVerticalValid;
    }
    private bool TryCreatePrealignerSetup(out PrealignerDefinition value)
    {
        var channels = new[] { PrealignerWaferPresentSensorChannelId, PrealignerAlignmentAcceptedCommandChannelId, PrealignerAlignmentReadyFeedbackChannelId, PrealignerAlignmentCompleteFeedbackChannelId };
        value = new PrealignerDefinition { RotaryStageComponentId = PrealignerRotaryStageComponentId ?? string.Empty, ClampCylinderComponentId = PrealignerClampCylinderComponentId ?? string.Empty, WaferPresentSensorChannelId = PrealignerWaferPresentSensorChannelId ?? string.Empty, AlignmentAcceptedCommandChannelId = PrealignerAlignmentAcceptedCommandChannelId ?? string.Empty, AlignmentReadyFeedbackChannelId = PrealignerAlignmentReadyFeedbackChannelId ?? string.Empty, AlignmentCompleteFeedbackChannelId = PrealignerAlignmentCompleteFeedbackChannelId ?? string.Empty, AlignmentTargetDegrees = Parse(PrealignerAlignmentTargetText), AlignmentToleranceDegrees = Parse(PrealignerAlignmentToleranceText) };
        return !HasMultiplePrealigners && IsPrealignerRotaryStageValid && IsPrealignerClampCylinderValid && Distinct(channels) && IsPrealignerWaferPresentValid && IsPrealignerAlignmentAcceptedValid && IsPrealignerAlignmentReadyValid && IsPrealignerAlignmentCompleteValid && IsPrealignerAlignmentTargetValid && IsPrealignerAlignmentToleranceValid;
    }
    private bool TryCreateInspectionHandoffSetup(out InspectionHandoffDefinition value)
    {
        var channels = new[] { InspectionHandoffPositionSensorChannelId, InspectionHandoffAcceptedChannelId, InspectionHandoffReadyChannelId, InspectionHandoffCompleteChannelId };
        value = new InspectionHandoffDefinition { CameraId = InspectionHandoffCameraId ?? string.Empty, InspectionPositionSensorChannelId = InspectionHandoffPositionSensorChannelId ?? string.Empty, ResultAcceptedCommandChannelId = InspectionHandoffAcceptedChannelId ?? string.Empty, InspectionReadyFeedbackChannelId = InspectionHandoffReadyChannelId ?? string.Empty, InspectionCompleteFeedbackChannelId = InspectionHandoffCompleteChannelId ?? string.Empty };
        return !HasMultipleInspectionHandoffs && IsInspectionHandoffCameraValid && IsInspectionHandoffPositionValid && IsInspectionHandoffAcceptedValid && IsInspectionHandoffReadyValid && IsInspectionHandoffCompleteValid && Distinct(channels);
    }
    private bool TryCreateInspectionSortRouterSetup(out InspectionSortRouterDefinition value)
    {
        value = new InspectionSortRouterDefinition { CameraId = InspectionSortCameraId ?? string.Empty, PassConveyorComponentId = InspectionSortPassConveyorId ?? string.Empty, NgConveyorComponentId = InspectionSortNgConveyorId ?? string.Empty, PassRoutedFeedbackChannelId = InspectionSortPassFeedbackChannelId ?? string.Empty, NgRoutedFeedbackChannelId = InspectionSortNgFeedbackChannelId ?? string.Empty };
        return !HasMultipleInspectionSortRouters && IsInspectionSortCameraValid && IsInspectionSortPassConveyorValid && IsInspectionSortNgConveyorValid && IsInspectionSortPassFeedbackValid && IsInspectionSortNgFeedbackValid;
    }
    private bool TryCreateOhtHandoffSetup(out OhtHandoffDefinition value)
    {
        var channels = new[] { OhtRouteAvailableChannelId, OhtVehicleDockedChannelId, OhtLoadPortReadyChannelId, OhtCarrierReceivedChannelId, OhtHandoffReadyChannelId, OhtCarrierTransferredChannelId };
        value = new OhtHandoffDefinition { TransportConveyorComponentId = OhtTransportConveyorId ?? string.Empty, RouteAvailableSensorChannelId = OhtRouteAvailableChannelId ?? string.Empty, VehicleDockedSensorChannelId = OhtVehicleDockedChannelId ?? string.Empty, LoadPortReadySensorChannelId = OhtLoadPortReadyChannelId ?? string.Empty, CarrierReceivedSensorChannelId = OhtCarrierReceivedChannelId ?? string.Empty, HandoffReadyFeedbackChannelId = OhtHandoffReadyChannelId ?? string.Empty, CarrierTransferredFeedbackChannelId = OhtCarrierTransferredChannelId ?? string.Empty };
        return !HasMultipleOhtHandoffs && IsOhtTransportConveyorValid && Distinct(channels) && IsOhtRouteAvailableValid && IsOhtVehicleDockedValid && IsOhtLoadPortReadyValid && IsOhtCarrierReceivedValid && IsOhtHandoffReadyValid && IsOhtCarrierTransferredValid;
    }

    private bool IsEquivalentToSaved(WaferHandlerDefinition value) => _savedWaferHandlerSetup is not null && Same(_savedWaferHandlerSetup.HorizontalAxisId, value.HorizontalAxisId) && Same(_savedWaferHandlerSetup.VerticalAxisId, value.VerticalAxisId) && Same(_savedWaferHandlerSetup.WorkpieceComponentId, value.WorkpieceComponentId) && Same(_savedWaferHandlerSetup.SourcePresentSensorChannelId, value.SourcePresentSensorChannelId) && Same(_savedWaferHandlerSetup.GateOpenSensorChannelId, value.GateOpenSensorChannelId) && Same(_savedWaferHandlerSetup.PickCommandChannelId, value.PickCommandChannelId) && Same(_savedWaferHandlerSetup.PlaceCommandChannelId, value.PlaceCommandChannelId) && Same(_savedWaferHandlerSetup.HoldingFeedbackChannelId, value.HoldingFeedbackChannelId) && Same(_savedWaferHandlerSetup.PlacedFeedbackChannelId, value.PlacedFeedbackChannelId) && _savedWaferHandlerSetup.PickHorizontalPosition == value.PickHorizontalPosition && _savedWaferHandlerSetup.PickVerticalPosition == value.PickVerticalPosition && _savedWaferHandlerSetup.PlaceHorizontalPosition == value.PlaceHorizontalPosition && _savedWaferHandlerSetup.PlaceVerticalPosition == value.PlaceVerticalPosition;
    private bool IsEquivalentToSaved(PrealignerDefinition value) => _savedPrealignerSetup is not null && Same(_savedPrealignerSetup.RotaryStageComponentId, value.RotaryStageComponentId) && Same(_savedPrealignerSetup.ClampCylinderComponentId, value.ClampCylinderComponentId) && Same(_savedPrealignerSetup.WaferPresentSensorChannelId, value.WaferPresentSensorChannelId) && Same(_savedPrealignerSetup.AlignmentAcceptedCommandChannelId, value.AlignmentAcceptedCommandChannelId) && Same(_savedPrealignerSetup.AlignmentReadyFeedbackChannelId, value.AlignmentReadyFeedbackChannelId) && Same(_savedPrealignerSetup.AlignmentCompleteFeedbackChannelId, value.AlignmentCompleteFeedbackChannelId) && _savedPrealignerSetup.AlignmentTargetDegrees == value.AlignmentTargetDegrees && _savedPrealignerSetup.AlignmentToleranceDegrees == value.AlignmentToleranceDegrees;
    private bool IsEquivalentToSaved(InspectionHandoffDefinition value) => _savedInspectionHandoffSetup is not null && Same(_savedInspectionHandoffSetup.CameraId, value.CameraId) && Same(_savedInspectionHandoffSetup.InspectionPositionSensorChannelId, value.InspectionPositionSensorChannelId) && Same(_savedInspectionHandoffSetup.ResultAcceptedCommandChannelId, value.ResultAcceptedCommandChannelId) && Same(_savedInspectionHandoffSetup.InspectionReadyFeedbackChannelId, value.InspectionReadyFeedbackChannelId) && Same(_savedInspectionHandoffSetup.InspectionCompleteFeedbackChannelId, value.InspectionCompleteFeedbackChannelId);
    private bool IsEquivalentToSaved(InspectionSortRouterDefinition value) => _savedInspectionSortRouterSetup is not null && Same(_savedInspectionSortRouterSetup.CameraId, value.CameraId) && Same(_savedInspectionSortRouterSetup.PassConveyorComponentId, value.PassConveyorComponentId) && Same(_savedInspectionSortRouterSetup.NgConveyorComponentId, value.NgConveyorComponentId) && Same(_savedInspectionSortRouterSetup.PassRoutedFeedbackChannelId, value.PassRoutedFeedbackChannelId) && Same(_savedInspectionSortRouterSetup.NgRoutedFeedbackChannelId, value.NgRoutedFeedbackChannelId);
    private bool IsEquivalentToSaved(OhtHandoffDefinition value) => _savedOhtHandoffSetup is not null && Same(_savedOhtHandoffSetup.TransportConveyorComponentId, value.TransportConveyorComponentId) && Same(_savedOhtHandoffSetup.RouteAvailableSensorChannelId, value.RouteAvailableSensorChannelId) && Same(_savedOhtHandoffSetup.VehicleDockedSensorChannelId, value.VehicleDockedSensorChannelId) && Same(_savedOhtHandoffSetup.LoadPortReadySensorChannelId, value.LoadPortReadySensorChannelId) && Same(_savedOhtHandoffSetup.CarrierReceivedSensorChannelId, value.CarrierReceivedSensorChannelId) && Same(_savedOhtHandoffSetup.HandoffReadyFeedbackChannelId, value.HandoffReadyFeedbackChannelId) && Same(_savedOhtHandoffSetup.CarrierTransferredFeedbackChannelId, value.CarrierTransferredFeedbackChannelId);

    private WaferHandlerDraft CaptureWaferHandlerDraft() => new(WaferHandlerHorizontalAxisId, WaferHandlerVerticalAxisId, WaferHandlerWorkpieceComponentId, WaferHandlerSourcePresentSensorChannelId, WaferHandlerGateOpenSensorChannelId, WaferHandlerPickCommandChannelId, WaferHandlerPlaceCommandChannelId, WaferHandlerHoldingFeedbackChannelId, WaferHandlerPlacedFeedbackChannelId, WaferHandlerPickHorizontalText, WaferHandlerPickVerticalText, WaferHandlerPlaceHorizontalText, WaferHandlerPlaceVerticalText);
    private PrealignerDraft CapturePrealignerDraft() => new(PrealignerRotaryStageComponentId, PrealignerClampCylinderComponentId, PrealignerWaferPresentSensorChannelId, PrealignerAlignmentAcceptedCommandChannelId, PrealignerAlignmentReadyFeedbackChannelId, PrealignerAlignmentCompleteFeedbackChannelId, PrealignerAlignmentTargetText, PrealignerAlignmentToleranceText);
    private InspectionHandoffDraft CaptureInspectionHandoffDraft() => new(InspectionHandoffCameraId, InspectionHandoffPositionSensorChannelId, InspectionHandoffAcceptedChannelId, InspectionHandoffReadyChannelId, InspectionHandoffCompleteChannelId);
    private InspectionSortRouterDraft CaptureInspectionSortRouterDraft() => new(InspectionSortCameraId, InspectionSortPassConveyorId, InspectionSortNgConveyorId, InspectionSortPassFeedbackChannelId, InspectionSortNgFeedbackChannelId);
    private OhtHandoffDraft CaptureOhtHandoffDraft() => new(OhtTransportConveyorId, OhtRouteAvailableChannelId, OhtVehicleDockedChannelId, OhtLoadPortReadyChannelId, OhtCarrierReceivedChannelId, OhtHandoffReadyChannelId, OhtCarrierTransferredChannelId);
    private void RestoreWaferHandlerDraft(WaferHandlerDraft value) { _waferHandlerHorizontalAxisId = value.HorizontalAxisId; _waferHandlerVerticalAxisId = value.VerticalAxisId; _waferHandlerWorkpieceComponentId = value.WorkpieceId; _waferHandlerSourcePresentSensorChannelId = value.SourceInputId; _waferHandlerGateOpenSensorChannelId = value.GateInputId; _waferHandlerPickCommandChannelId = value.PickOutputId; _waferHandlerPlaceCommandChannelId = value.PlaceOutputId; _waferHandlerHoldingFeedbackChannelId = value.HoldingInputId; _waferHandlerPlacedFeedbackChannelId = value.PlacedInputId; _waferHandlerPickHorizontalText = value.PickHorizontal; _waferHandlerPickVerticalText = value.PickVertical; _waferHandlerPlaceHorizontalText = value.PlaceHorizontal; _waferHandlerPlaceVerticalText = value.PlaceVertical; RaiseStateChanged(); }
    private void RestorePrealignerDraft(PrealignerDraft value) { _prealignerRotaryStageComponentId = value.StageId; _prealignerClampCylinderComponentId = value.ClampId; _prealignerWaferPresentSensorChannelId = value.WaferPresentId; _prealignerAlignmentAcceptedCommandChannelId = value.AcceptedId; _prealignerAlignmentReadyFeedbackChannelId = value.ReadyId; _prealignerAlignmentCompleteFeedbackChannelId = value.CompleteId; _prealignerAlignmentTargetText = value.Target; _prealignerAlignmentToleranceText = value.Tolerance; RaiseStateChanged(); }
    private void RestoreInspectionHandoffDraft(InspectionHandoffDraft value) { AddMissing(InspectionCameraOptions, value.CameraId); foreach (var id in new[] { value.PositionInputId, value.ReadyInputId, value.CompleteInputId }) AddMissing(InspectionInputOptions, id); AddMissing(InspectionOutputOptions, value.AcceptedOutputId); _inspectionHandoffCameraId = value.CameraId; _inspectionHandoffPositionSensorChannelId = value.PositionInputId; _inspectionHandoffAcceptedChannelId = value.AcceptedOutputId; _inspectionHandoffReadyChannelId = value.ReadyInputId; _inspectionHandoffCompleteChannelId = value.CompleteInputId; RaiseStateChanged(); }
    private void RestoreInspectionSortRouterDraft(InspectionSortRouterDraft value) { AddMissing(InspectionCameraOptions, value.CameraId); AddMissing(InspectionConveyorOptions, value.PassConveyorId); AddMissing(InspectionConveyorOptions, value.NgConveyorId); AddMissing(InspectionInputOptions, value.PassFeedbackInputId); AddMissing(InspectionInputOptions, value.NgFeedbackInputId); _inspectionSortCameraId = value.CameraId; _inspectionSortPassConveyorId = value.PassConveyorId; _inspectionSortNgConveyorId = value.NgConveyorId; _inspectionSortPassFeedbackChannelId = value.PassFeedbackInputId; _inspectionSortNgFeedbackChannelId = value.NgFeedbackInputId; RaiseStateChanged(); }
    private void RestoreOhtHandoffDraft(OhtHandoffDraft value) { AddMissing(InspectionConveyorOptions, value.TransportConveyorId); foreach (var id in new[] { value.RouteAvailableInputId, value.VehicleDockedInputId, value.LoadPortReadyInputId, value.CarrierReceivedInputId, value.HandoffReadyInputId, value.CarrierTransferredInputId }) AddMissing(InspectionInputOptions, id); _ohtTransportConveyorId = value.TransportConveyorId; _ohtRouteAvailableChannelId = value.RouteAvailableInputId; _ohtVehicleDockedChannelId = value.VehicleDockedInputId; _ohtLoadPortReadyChannelId = value.LoadPortReadyInputId; _ohtCarrierReceivedChannelId = value.CarrierReceivedInputId; _ohtHandoffReadyChannelId = value.HandoffReadyInputId; _ohtCarrierTransferredChannelId = value.CarrierTransferredInputId; RaiseStateChanged(); }

    private void SetSelection(ref string? field, string? value, string propertyName) { if (SetProperty(ref field, value, propertyName)) RaiseValidationChanged(); }
    private void SetText(ref string field, string value, string propertyName) { if (SetProperty(ref field, value, propertyName)) RaiseValidationChanged(); }
    private MachineLayoutDefinition? ActiveLayout() => _project is null ? null : ResolveActiveLayout(_project);
    private bool IsLinearAxis(string? id) => _project?.Axes.Any(axis => axis.Kind == AxisKind.Linear && Same(axis.Id, id)) == true;
    private bool IsLayoutComponent(string? id, LayoutComponentKind kind) => ActiveLayout()?.Components.Any(component => component.Kind == kind && Same(component.Id, id)) == true;
    private bool IsChannel(string? id, ChannelKind kind) => _project?.Channels.Any(channel => channel.Kind == kind && Same(channel.Id, id)) == true;
    private bool IsCamera(string? id) => _project?.Devices.Any(device => device is { Kind: DeviceKind.Camera, Camera: not null } && Same(device.Id, id)) == true;
    private bool IsAxisPosition(string? axisId, string text) => TryGetAxis(axisId, out var axis) && TryFiniteDouble(text, out var value) && axis.SoftLimitMin.HasValue && axis.SoftLimitMax.HasValue && value >= axis.SoftLimitMin.Value && value <= axis.SoftLimitMax.Value;
    private bool IsRotaryPosition(string? stageId, string text) => TryGetPrealignerStage(stageId, out var stage) && TryFiniteDouble(text, out var value) && _project!.Axes.FirstOrDefault(axis => Same(axis.Id, stage.BehaviorBindingId)) is { Kind: AxisKind.Rotary, SoftLimitMin: not null, SoftLimitMax: not null } axis && value >= axis.SoftLimitMin.Value && value <= axis.SoftLimitMax.Value;
    private bool TryGetAxis(string? id, out OpenVisionLab.Machine.Core.Axes.VirtualAxisDefinition axis) { axis = _project?.Axes.FirstOrDefault(candidate => Same(candidate.Id, id))!; return axis is not null; }
    private bool TryGetPrealignerStage(string? id, out LayoutComponentDefinition stage) { var value = ActiveLayout()?.Components.FirstOrDefault(component => component.Kind == LayoutComponentKind.RotaryStage && Same(component.Id, id)); stage = value!; return value is not null && _project!.Axes.Any(axis => axis.Kind == AxisKind.Rotary && Same(axis.Id, value.BehaviorBindingId)); }
    private double AxisLimit(string? axisId, bool maximum) => TryGetAxis(axisId, out var axis) && axis.SoftLimitMin.HasValue && axis.SoftLimitMax.HasValue ? maximum ? axis.SoftLimitMax.Value : axis.SoftLimitMin.Value : 0;
    private double StageAxisLimit(string? stageId) => TryGetPrealignerStage(stageId, out var stage) && _project!.Axes.FirstOrDefault(axis => Same(axis.Id, stage.BehaviorBindingId)) is { SoftLimitMin: not null } axis ? axis.SoftLimitMin.Value : 0;

    private void RaiseStateChanged()
    {
        OnPropertyChanged(nameof(IsWaferHandlerSetupVisible)); OnPropertyChanged(nameof(IsPrealignerSetupVisible)); OnPropertyChanged(nameof(IsInspectionHandoffSetupVisible)); OnPropertyChanged(nameof(IsInspectionSortRouterSetupVisible)); OnPropertyChanged(nameof(IsOhtHandoffSetupVisible));
        RaiseValidationChanged(); RaiseCommandStates();
    }

    private void RaiseValidationChanged()
    {
        foreach (var property in ValidationProperties) OnPropertyChanged(property);
        _applyWaferHandlerSetupCommand.RaiseCanExecuteChanged(); _applyPrealignerSetupCommand.RaiseCanExecuteChanged(); _applyInspectionHandoffSetupCommand.RaiseCanExecuteChanged(); _applyInspectionSortRouterSetupCommand.RaiseCanExecuteChanged(); _applyOhtHandoffSetupCommand.RaiseCanExecuteChanged();
    }

    private void RaiseCommandStates()
    {
        foreach (var command in new[] { _previewWaferHandlerSetupCommand, _applyWaferHandlerSetupCommand, _cancelWaferHandlerSetupCommand, _resetWaferHandlerSetupCommand, _previewPrealignerSetupCommand, _applyPrealignerSetupCommand, _cancelPrealignerSetupCommand, _resetPrealignerSetupCommand, _previewInspectionHandoffSetupCommand, _applyInspectionHandoffSetupCommand, _cancelInspectionHandoffSetupCommand, _resetInspectionHandoffSetupCommand, _previewInspectionSortRouterSetupCommand, _applyInspectionSortRouterSetupCommand, _cancelInspectionSortRouterSetupCommand, _resetInspectionSortRouterSetupCommand, _previewOhtHandoffSetupCommand, _applyOhtHandoffSetupCommand, _cancelOhtHandoffSetupCommand, _resetOhtHandoffSetupCommand }) command.RaiseCanExecuteChanged();
    }

    private delegate bool TryCreate<T>(out T value) where T : class;
    private static void Apply<T>(Func<T, int> apply, TryCreate<T> create, Func<T, bool> equivalent, Action clear, Action preview) where T : class
    {
        if (!create(out var value)) return;
        if (apply(value) > 0 || equivalent(value)) clear(); else preview();
    }
    private static bool Distinct(string?[] values) => values.All(value => !string.IsNullOrWhiteSpace(value)) && values.Distinct(StringComparer.Ordinal).Count() == values.Length;
    private static bool Same(string? left, string? right) => string.Equals(left, right, StringComparison.Ordinal);
    private static bool TryPositiveDouble(string text, out double value) => TryFiniteDouble(text, out value) && value > 0;
    private static bool TryFiniteDouble(string text, out double value) => (double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value) || double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)) && double.IsFinite(value);
    private static double Parse(string text) => TryFiniteDouble(text, out var value) ? value : double.NaN;
    private static string Format(double value) => value.ToString("0.###", CultureInfo.CurrentCulture);
    private static LoadLockSetupOption Option(string id, string? name) => new(id, string.IsNullOrWhiteSpace(name) ? id : $"{name} — {id}");
    private static void AddMissing(ObservableCollection<LoadLockSetupOption> options, string? id) { if (!string.IsNullOrWhiteSpace(id) && options.All(option => !Same(option.Id, id))) options.Add(new LoadLockSetupOption(id, $"{id} ({OpenVisionLanguageService.T("Connections.LoadLockSetupMissing")})")); }
    private static string ValidationText(bool multiple, bool invalid, string multipleKey, string invalidKey, string readyKey) => OpenVisionLanguageService.T(multiple ? multipleKey : invalid ? invalidKey : readyKey);
    private static MachineLayoutDefinition? ResolveActiveLayout(MachineProjectDocument project) => !string.IsNullOrWhiteSpace(project.Simulation.ActiveLayoutId) ? project.Layouts.FirstOrDefault(layout => Same(layout.Id, project.Simulation.ActiveLayoutId)) : project.Layouts.Count == 1 ? project.Layouts[0] : null;

    private static WaferHandlerDefinition Clone(WaferHandlerDefinition value) => new() { HorizontalAxisId = value.HorizontalAxisId, VerticalAxisId = value.VerticalAxisId, WorkpieceComponentId = value.WorkpieceComponentId, SourcePresentSensorChannelId = value.SourcePresentSensorChannelId, GateOpenSensorChannelId = value.GateOpenSensorChannelId, PickCommandChannelId = value.PickCommandChannelId, PlaceCommandChannelId = value.PlaceCommandChannelId, HoldingFeedbackChannelId = value.HoldingFeedbackChannelId, PlacedFeedbackChannelId = value.PlacedFeedbackChannelId, PickHorizontalPosition = value.PickHorizontalPosition, PickVerticalPosition = value.PickVerticalPosition, PlaceHorizontalPosition = value.PlaceHorizontalPosition, PlaceVerticalPosition = value.PlaceVerticalPosition };
    private static PrealignerDefinition Clone(PrealignerDefinition value) => new() { RotaryStageComponentId = value.RotaryStageComponentId, ClampCylinderComponentId = value.ClampCylinderComponentId, WaferPresentSensorChannelId = value.WaferPresentSensorChannelId, AlignmentAcceptedCommandChannelId = value.AlignmentAcceptedCommandChannelId, AlignmentReadyFeedbackChannelId = value.AlignmentReadyFeedbackChannelId, AlignmentCompleteFeedbackChannelId = value.AlignmentCompleteFeedbackChannelId, AlignmentTargetDegrees = value.AlignmentTargetDegrees, AlignmentToleranceDegrees = value.AlignmentToleranceDegrees };
    private static InspectionHandoffDefinition Clone(InspectionHandoffDefinition value) => new() { CameraId = value.CameraId, InspectionPositionSensorChannelId = value.InspectionPositionSensorChannelId, ResultAcceptedCommandChannelId = value.ResultAcceptedCommandChannelId, InspectionReadyFeedbackChannelId = value.InspectionReadyFeedbackChannelId, InspectionCompleteFeedbackChannelId = value.InspectionCompleteFeedbackChannelId };
    private static InspectionSortRouterDefinition Clone(InspectionSortRouterDefinition value) => new() { CameraId = value.CameraId, PassConveyorComponentId = value.PassConveyorComponentId, NgConveyorComponentId = value.NgConveyorComponentId, PassRoutedFeedbackChannelId = value.PassRoutedFeedbackChannelId, NgRoutedFeedbackChannelId = value.NgRoutedFeedbackChannelId };
    private static OhtHandoffDefinition Clone(OhtHandoffDefinition value) => new() { TransportConveyorComponentId = value.TransportConveyorComponentId, RouteAvailableSensorChannelId = value.RouteAvailableSensorChannelId, VehicleDockedSensorChannelId = value.VehicleDockedSensorChannelId, LoadPortReadySensorChannelId = value.LoadPortReadySensorChannelId, CarrierReceivedSensorChannelId = value.CarrierReceivedSensorChannelId, HandoffReadyFeedbackChannelId = value.HandoffReadyFeedbackChannelId, CarrierTransferredFeedbackChannelId = value.CarrierTransferredFeedbackChannelId };

    private sealed record WaferHandlerDraft(string? HorizontalAxisId, string? VerticalAxisId, string? WorkpieceId, string? SourceInputId, string? GateInputId, string? PickOutputId, string? PlaceOutputId, string? HoldingInputId, string? PlacedInputId, string PickHorizontal, string PickVertical, string PlaceHorizontal, string PlaceVertical);
    private sealed record PrealignerDraft(string? StageId, string? ClampId, string? WaferPresentId, string? AcceptedId, string? ReadyId, string? CompleteId, string Target, string Tolerance);
    private sealed record InspectionHandoffDraft(string? CameraId, string? PositionInputId, string? AcceptedOutputId, string? ReadyInputId, string? CompleteInputId);
    private sealed record InspectionSortRouterDraft(string? CameraId, string? PassConveyorId, string? NgConveyorId, string? PassFeedbackInputId, string? NgFeedbackInputId);
    private sealed record OhtHandoffDraft(string? TransportConveyorId, string? RouteAvailableInputId, string? VehicleDockedInputId, string? LoadPortReadyInputId, string? CarrierReceivedInputId, string? HandoffReadyInputId, string? CarrierTransferredInputId);

    private static readonly string[] ValidationProperties =
    [
        nameof(WaferHandlerHorizontalAxisId), nameof(WaferHandlerVerticalAxisId), nameof(WaferHandlerWorkpieceComponentId), nameof(WaferHandlerSourcePresentSensorChannelId), nameof(WaferHandlerGateOpenSensorChannelId), nameof(WaferHandlerPickCommandChannelId), nameof(WaferHandlerPlaceCommandChannelId), nameof(WaferHandlerHoldingFeedbackChannelId), nameof(WaferHandlerPlacedFeedbackChannelId), nameof(WaferHandlerPickHorizontalText), nameof(WaferHandlerPickVerticalText), nameof(WaferHandlerPlaceHorizontalText), nameof(WaferHandlerPlaceVerticalText), nameof(IsWaferHandlerHorizontalAxisValid), nameof(IsWaferHandlerVerticalAxisValid), nameof(IsWaferHandlerWorkpieceValid), nameof(IsWaferHandlerSourcePresentValid), nameof(IsWaferHandlerGateOpenValid), nameof(IsWaferHandlerPickCommandValid), nameof(IsWaferHandlerPlaceCommandValid), nameof(IsWaferHandlerHoldingFeedbackValid), nameof(IsWaferHandlerPlacedFeedbackValid), nameof(IsWaferHandlerPickHorizontalValid), nameof(IsWaferHandlerPickVerticalValid), nameof(IsWaferHandlerPlaceHorizontalValid), nameof(IsWaferHandlerPlaceVerticalValid), nameof(HasMultipleWaferHandlers), nameof(HasWaferHandlerSetupValidationError), nameof(WaferHandlerSetupValidationText),
        nameof(PrealignerRotaryStageComponentId), nameof(PrealignerClampCylinderComponentId), nameof(PrealignerWaferPresentSensorChannelId), nameof(PrealignerAlignmentAcceptedCommandChannelId), nameof(PrealignerAlignmentReadyFeedbackChannelId), nameof(PrealignerAlignmentCompleteFeedbackChannelId), nameof(PrealignerAlignmentTargetText), nameof(PrealignerAlignmentToleranceText), nameof(IsPrealignerRotaryStageValid), nameof(IsPrealignerClampCylinderValid), nameof(IsPrealignerWaferPresentValid), nameof(IsPrealignerAlignmentAcceptedValid), nameof(IsPrealignerAlignmentReadyValid), nameof(IsPrealignerAlignmentCompleteValid), nameof(IsPrealignerAlignmentTargetValid), nameof(IsPrealignerAlignmentToleranceValid), nameof(HasMultiplePrealigners), nameof(HasPrealignerSetupValidationError), nameof(PrealignerSetupValidationText),
        nameof(InspectionHandoffCameraId), nameof(InspectionHandoffPositionSensorChannelId), nameof(InspectionHandoffAcceptedChannelId), nameof(InspectionHandoffReadyChannelId), nameof(InspectionHandoffCompleteChannelId), nameof(IsInspectionHandoffCameraValid), nameof(IsInspectionHandoffPositionValid), nameof(IsInspectionHandoffAcceptedValid), nameof(IsInspectionHandoffReadyValid), nameof(IsInspectionHandoffCompleteValid), nameof(HasMultipleInspectionHandoffs), nameof(HasInspectionHandoffSetupValidationError), nameof(InspectionHandoffSetupValidationText),
        nameof(InspectionSortCameraId), nameof(InspectionSortPassConveyorId), nameof(InspectionSortNgConveyorId), nameof(InspectionSortPassFeedbackChannelId), nameof(InspectionSortNgFeedbackChannelId), nameof(IsInspectionSortCameraValid), nameof(IsInspectionSortPassConveyorValid), nameof(IsInspectionSortNgConveyorValid), nameof(IsInspectionSortPassFeedbackValid), nameof(IsInspectionSortNgFeedbackValid), nameof(HasMultipleInspectionSortRouters), nameof(HasInspectionSortRouterSetupValidationError), nameof(InspectionSortRouterSetupValidationText),
        nameof(OhtTransportConveyorId), nameof(OhtRouteAvailableChannelId), nameof(OhtVehicleDockedChannelId), nameof(OhtLoadPortReadyChannelId), nameof(OhtCarrierReceivedChannelId), nameof(OhtHandoffReadyChannelId), nameof(OhtCarrierTransferredChannelId), nameof(IsOhtTransportConveyorValid), nameof(IsOhtRouteAvailableValid), nameof(IsOhtVehicleDockedValid), nameof(IsOhtLoadPortReadyValid), nameof(IsOhtCarrierReceivedValid), nameof(IsOhtHandoffReadyValid), nameof(IsOhtCarrierTransferredValid), nameof(HasMultipleOhtHandoffs), nameof(HasOhtHandoffSetupValidationError), nameof(OhtHandoffSetupValidationText)
    ];
}
