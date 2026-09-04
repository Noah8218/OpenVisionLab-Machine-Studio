using System.Globalization;
using System.Windows.Input;
using OpenVisionLab;
using OpenVisionLab.Machine.Simulation.Sequences;
using OpenVisionLab.Machine.Simulation.Snapshots;

namespace OpenVisionLab.MachineStudio.ViewModel;

public sealed class RecipeSequenceStepPreviewViewModel : ViewModelBase
{
    private readonly Func<string, string, string, Task<SequenceStepPreviewResult>> _previewSequenceStep;
    private readonly Func<bool> _isEditable;
    private readonly Func<bool> _isReady;
    private readonly Func<RecipeConnectionRowViewModel, bool> _isCurrentRow;
    private readonly AsyncRelayCommand _previewSequenceStepCommand;

    public RecipeSequenceStepPreviewViewModel(
        Func<string, string, string, Task<SequenceStepPreviewResult>> previewSequenceStep,
        Func<bool> isEditable,
        Func<bool> isReady,
        Func<RecipeConnectionRowViewModel, bool> isCurrentRow)
    {
        _previewSequenceStep = previewSequenceStep;
        _isEditable = isEditable;
        _isReady = isReady;
        _isCurrentRow = isCurrentRow;
        _previewSequenceStepCommand = new AsyncRelayCommand(
            PreviewSequenceStepAsync,
            parameter => _isEditable()
                         && _isReady()
                         && parameter is RecipeConnectionRowViewModel { CanPreviewSequenceStep: true });
    }

    public ICommand PreviewSequenceStepCommand => _previewSequenceStepCommand;

    public void RefreshCanExecute() => _previewSequenceStepCommand.RaiseCanExecuteChanged();

    private async Task PreviewSequenceStepAsync(object? parameter)
    {
        if (parameter is not RecipeConnectionRowViewModel
            {
                FirstSequenceId: { } sequenceId,
                FirstSequenceStepId: { } stepId
            } row)
        {
            return;
        }

        var result = await _previewSequenceStep(sequenceId, stepId, row.ComponentId);
        if (!_isCurrentRow(row) || !_isReady())
        {
            return;
        }

        row.ApplyPreview(result, BuildObservation(row, result));
    }

    private static string BuildObservation(
        RecipeConnectionRowViewModel row,
        SequenceStepPreviewResult result)
    {
        var snapshot = result.FinalSnapshot;
        if (snapshot is null)
        {
            return result.Detail;
        }

        var axis = snapshot.Axes.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, result.TargetId, StringComparison.Ordinal));
        if (axis is not null)
        {
            return Format("Connections.PreviewAxisFormat", axis.Position, axis.State);
        }

        var signal = snapshot.Signals.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, result.TargetId, StringComparison.Ordinal));
        var component = snapshot.LayoutComponents.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, row.ComponentId, StringComparison.Ordinal));
        if (component?.CylinderState is { } cylinderState)
        {
            return Format(
                "Connections.PreviewCylinderFormat",
                signal?.Value == true ? "ON" : "OFF",
                cylinderState,
                component.MotionProgress ?? 0);
        }

        if (component?.ConveyorRunning is { } conveyorRunning)
        {
            return Format(
                "Connections.PreviewConveyorFormat",
                conveyorRunning ? "ON" : "OFF",
                component.ConveyorDirection?.ToString() ?? "—");
        }

        if (component?.IsDetected is { } detected)
        {
            return Format("Connections.PreviewSensorFormat", detected ? "ON" : "OFF");
        }

        return signal is null
            ? result.Detail
            : Format("Connections.PreviewSignalFormat", signal.Id, signal.Value ? "ON" : "OFF");
    }

    private static string Format(string key, params object[] args) =>
        string.Format(CultureInfo.CurrentCulture, OpenVisionLanguageService.T(key), args);
}
