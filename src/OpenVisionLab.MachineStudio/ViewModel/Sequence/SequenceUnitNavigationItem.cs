using OpenVisionLab;

namespace OpenVisionLab.MachineStudio.ViewModel;

public sealed record SequenceUnitNavigationItem(string Key, string Name, string Id, string? UnitId, string? SequenceId, int StepCount)
{
    public string ScopeText => OpenVisionLanguageService.T(UnitId is null ? "Shell.Sequence" : "Properties.Unit");
    public string StepCountText => SequenceId is null ? OpenVisionLanguageService.T("Sequence.NoSequenceSelected") : string.Format(OpenVisionLanguageService.T("R24.StepCount"), StepCount);
}
