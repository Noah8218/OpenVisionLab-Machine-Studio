namespace OpenVisionLab.MachineStudio.ViewModel;

public sealed record CameraSequenceStepReference(string ProjectId, string CameraId, string SequenceId, string StepId, string SequenceName, string StepName, int Order)
{
    public string Caption => $"{Order}. {StepName}";
    public string LocationText => $"{SequenceName} / {StepId}";
}
