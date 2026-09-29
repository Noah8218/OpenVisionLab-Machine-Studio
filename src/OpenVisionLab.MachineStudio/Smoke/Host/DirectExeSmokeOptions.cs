namespace OpenVisionLab.MachineStudio;

internal sealed class DirectExeSmokeOptions
{
    public bool PerformSmokePerf { get; init; }
    public int SmokePerfSampleCount { get; init; }
    public string? SmokePerfReportPath { get; init; }
    public string? ScreenshotPath { get; init; }
    public string? LayoutReportPath { get; init; }
    public string SizeArgument { get; init; } = "1280x760";
    public (int Width, int Height) WindowSize { get; init; } = (1280, 760);
    public int DpiScalePercent { get; init; } = 100;
    public string? SmokeLanguage { get; init; }
    public string? ProjectPath { get; init; }
    public string? EquipmentOutlineSearchText { get; init; }
    public string? SelectPath { get; init; }
    public string? LayoutSelectId { get; init; }
    public bool UseTopView { get; init; }
    public string? LayoutSelectMany { get; init; }
    public string? LayoutAlignment { get; init; }
    public string? LayoutAlignmentReportPath { get; init; }
    public string? LayoutHistoryReportPath { get; init; }
    public string? DirectSceneReportPath { get; init; }
    public string? CanvasNavigationReportPath { get; init; }
    public string? DirectTransformReportPath { get; init; }
    public string? MultiTransformReportPath { get; init; }
    public string? LibraryDropReportPath { get; init; }
    public string? LayerOrderReportPath { get; init; }
    public string? FaultManagerReportPath { get; init; }
    public string? FaultManagerState { get; init; }
    public string? RuntimeDebuggerReportPath { get; init; }
    public string? RuntimeDebuggerState { get; init; }
    public string? DigitalIoCommissioningReportPath { get; init; }
    public string? DigitalIoCommissioningState { get; init; }
    public string? AnalogIoAuthoringReportPath { get; init; }
    public string? AnalogIoAuthoringState { get; init; }
    public string? AnalogIoAuthoringSavePath { get; init; }
    public string? CameraCommissioningReportPath { get; init; }
    public string? CameraCommissioningState { get; init; }
    public string? IntegrationPanelState { get; init; }
    public string? IntegrationExchangeRoot { get; init; }
    public string? IntegrationPanelReportPath { get; init; }
    public string? MmiOperatorState { get; init; }
    public string? MmiOperatorReportPath { get; init; }
    public bool EditCameraImageSource { get; init; }
    public string? AxisCommissioningReportPath { get; init; }
    public string? AxisCommissioningState { get; init; }
    public string? MultiAxisRecipeReportPath { get; init; }
    public string? MultiAxisRecipeSavePath { get; init; }
    public string? MultiAxisRecipeState { get; init; }
    public string? AxisTuningState { get; init; }
    public string? CylinderCommissioningReportPath { get; init; }
    public string? CylinderCommissioningState { get; init; }
    public string? ConveyorCommissioningReportPath { get; init; }
    public string? ConveyorCommissioningState { get; init; }
    public string? SensorCommissioningReportPath { get; init; }
    public string? SensorCommissioningState { get; init; }
    public string? LayoutClickId { get; init; }
    public string? LayoutPropertyState { get; init; }
    public string? EditMenuState { get; init; }
    public string? DirectSceneGestureState { get; init; }
    public string? GlobalCommandState { get; init; }
    public string? GlobalCommandStateScreenshotPath { get; init; }
    public string? StartupChoiceState { get; init; }
    public string? RecipeGalleryState { get; init; }
    public string? RecipeGalleryCopyPath { get; init; }
    public string? RecipeGalleryReportPath { get; init; }
    public string? RecipeGalleryCompatibilityReportPath { get; init; }
    public string? RecipeGalleryBaselineReportPath { get; init; }
    public string? RecipeGalleryCurrentReportPath { get; init; }
    public bool RecipeGalleryExpectFailure { get; init; }
    public string? ConnectionWorkbenchReportPath { get; init; }
    public string? ConnectionWorkbenchSavePath { get; init; }
    public string? ConnectionWorkbenchState { get; init; }
    public string? CameraFirstUseReportPath { get; init; }
    public string? CameraFirstUseSavePath { get; init; }
    public string? CameraFirstUseState { get; init; }
    public string? ProjectSafetyReportPath { get; init; }
    public string? ProjectSafetySavePath { get; init; }
    public string? ProjectDiagnosticsReportPath { get; init; }
    public string? ProjectDiagnosticsScreenshotPath { get; init; }
    public string? SupportDiagnosticsReportPath { get; init; }
    public string? SupportDiagnosticsExportPath { get; init; }
    public string? SupportDiagnosticsScreenshotPath { get; init; }
    public string? UnsavedDialogScreenshotPath { get; init; }
    public string? ProjectOpenFailureDialogScreenshotPath { get; init; }
    public string? EvidenceDrawerState { get; init; }
    public string? LeftToolTab { get; init; }
    public string? LibraryEntryState { get; init; }
    public string? LibrarySearchText { get; init; }
    public string? LibraryCardState { get; init; }
    public string? LibraryDefaultAddKind { get; init; }
    public string? DocumentTab { get; init; }
    public string? SequenceState { get; init; }
    public string? PickPlaceState { get; init; }
    public string? RoundTripSavePath { get; init; }
    public string? RoundTripReportPath { get; init; }
    public bool VerifyRoundTrip { get; init; }
    public bool UseRunLayout { get; init; }
    public bool StartSimulation { get; init; }
    public bool TestConditionScenario { get; init; }
    public bool TestAxisFaultScenario { get; init; }
    public string? AxisFaultPersistencePath { get; init; }
    public string? TestScenarioSettingsState { get; init; }
    public string? TestScenarioFaultKind { get; init; }
    public bool ShowTestScenarioSettings { get; init; }
    public bool TestScenarioBatch { get; init; }
    public string? ScenarioEvidenceExchangePath { get; init; }
    public string? ScenarioReportPath { get; init; }
    public string ScenarioEvidenceExchangeState { get; init; } = "normal";
    public string ScenarioReportState { get; init; } = "normal";
    public string? UnifiedCommissioningEvidencePath { get; init; }
    public string UnifiedCommissioningEvidenceState { get; init; } = "normal";
    public string? CommandTracePath { get; init; }
    public string CommandTraceState { get; init; } = "normal";
    public bool CommandTraceStateSpecified { get; init; }
    public bool SaveBatchPersistence { get; init; }
    public bool VerifyBatchPersistence { get; init; }
    public bool VerifyStaleBatchPersistence { get; init; }
    public string? CylinderFaultTargetId { get; init; }
    public bool CameraFirstUseRequested { get; init; }
    public bool IsSmokeRun { get; init; }
}
