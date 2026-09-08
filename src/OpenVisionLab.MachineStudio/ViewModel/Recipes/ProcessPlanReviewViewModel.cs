using System.Globalization;
using System.Windows.Input;
using OpenVisionLab;

namespace OpenVisionLab.MachineStudio.ViewModel;

/// <summary>
/// Owns the review navigation state between the Process Block preview and the
/// Sequence editor. Project data and the visual shell remain with their
/// existing owners and are reached only through explicit callbacks.
/// </summary>
public sealed class ProcessPlanReviewViewModel : ViewModelBase
{
    private readonly Func<bool> _isEditable;
    private readonly Func<bool> _isPreviewVisible;
    private readonly Func<IReadOnlyList<SemiconductorProcessBlockItemPresentation>> _getVisibleItems;
    private readonly Func<IReadOnlyList<SemiconductorProcessBlockItemPresentation>> _getItems;
    private readonly Func<string, string, string?> _tryOpenSequenceStep;
    private readonly Func<string, string?> _selectProcessBlockStep;
    private readonly Action<int> _selectDocumentTab;
    private readonly Action<string> _setStatus;
    private readonly RelayCommand _returnToProcessPlanCommand;
    private readonly RelayCommand _previousStepCommand;
    private readonly RelayCommand _nextStepCommand;
    private string? _returnStepId;
    private (string SequenceId, string StepId)[] _reviewSteps = [];
    private int _reviewIndex = -1;

    public ProcessPlanReviewViewModel(
        Func<bool> isEditable,
        Func<bool> isPreviewVisible,
        Func<IReadOnlyList<SemiconductorProcessBlockItemPresentation>> getVisibleItems,
        Func<IReadOnlyList<SemiconductorProcessBlockItemPresentation>> getItems,
        Func<string, string, string?> tryOpenSequenceStep,
        Func<string, string?> selectProcessBlockStep,
        Action<int> selectDocumentTab,
        Action<string> setStatus)
    {
        _isEditable = isEditable ?? throw new ArgumentNullException(nameof(isEditable));
        _isPreviewVisible = isPreviewVisible ?? throw new ArgumentNullException(nameof(isPreviewVisible));
        _getVisibleItems = getVisibleItems ?? throw new ArgumentNullException(nameof(getVisibleItems));
        _getItems = getItems ?? throw new ArgumentNullException(nameof(getItems));
        _tryOpenSequenceStep = tryOpenSequenceStep ?? throw new ArgumentNullException(nameof(tryOpenSequenceStep));
        _selectProcessBlockStep = selectProcessBlockStep ?? throw new ArgumentNullException(nameof(selectProcessBlockStep));
        _selectDocumentTab = selectDocumentTab ?? throw new ArgumentNullException(nameof(selectDocumentTab));
        _setStatus = setStatus ?? throw new ArgumentNullException(nameof(setStatus));
        _returnToProcessPlanCommand = new RelayCommand(
            _ => ReturnToProcessPlan(),
            _ => CanReturnToProcessPlan(),
            useCommandManagerRequery: false);
        _previousStepCommand = new RelayCommand(
            _ => MoveReviewStep(-1),
            _ => CanMoveReviewStep(-1),
            useCommandManagerRequery: false);
        _nextStepCommand = new RelayCommand(
            _ => MoveReviewStep(1),
            _ => CanMoveReviewStep(1),
            useCommandManagerRequery: false);
    }

    public bool HasReturnContext => _returnStepId is not null;
    public string? ReturnStepId => _returnStepId;
    public string ReviewPositionText => _reviewIndex < 0
        ? string.Empty
        : string.Format(
            CultureInfo.CurrentCulture,
            OpenVisionLanguageService.T("Connections.ProcessPlanReviewPositionFormat"),
            _reviewIndex + 1,
            _reviewSteps.Length);

    public ICommand ReturnToProcessPlanCommand => _returnToProcessPlanCommand;
    public ICommand PreviousStepCommand => _previousStepCommand;
    public ICommand NextStepCommand => _nextStepCommand;

    public void OpenProcessBlockSequenceStep(string sequenceId, string stepId)
    {
        Clear();
        var displayName = _tryOpenSequenceStep(sequenceId, stepId);
        if (displayName is null)
        {
            return;
        }

        _reviewSteps = _getVisibleItems()
            .Where(item => item.CanOpenSequenceStep)
            .Select(item => (SequenceId: item.SequenceId!, item.StepId))
            .ToArray();
        _reviewIndex = Array.FindIndex(
            _reviewSteps,
            item => string.Equals(item.SequenceId, sequenceId, StringComparison.Ordinal)
                    && string.Equals(item.StepId, stepId, StringComparison.Ordinal));
        if (_reviewIndex < 0)
        {
            _reviewSteps = [(sequenceId, stepId)];
            _reviewIndex = 0;
        }

        _returnStepId = stepId;
        RaiseReviewChanged();
    }

    internal void InvalidateCommands()
    {
        RaiseCanExecuteChanged(_returnToProcessPlanCommand);
        RaiseCanExecuteChanged(_previousStepCommand);
        RaiseCanExecuteChanged(_nextStepCommand);
    }

    public void Clear()
    {
        if (_returnStepId is null)
        {
            return;
        }

        _returnStepId = null;
        _reviewSteps = [];
        _reviewIndex = -1;
        RaiseReviewChanged();
    }

    private bool CanReturnToProcessPlan() => _isEditable()
        && _returnStepId is { } stepId
        && _isPreviewVisible()
        && _getItems().Any(item => string.Equals(
            item.StepId,
            stepId,
            StringComparison.Ordinal));

    private bool CanMoveReviewStep(int offset)
    {
        var targetIndex = _reviewIndex + offset;
        if (!CanReturnToProcessPlan()
            || targetIndex < 0
            || targetIndex >= _reviewSteps.Length)
        {
            return false;
        }

        var target = _reviewSteps[targetIndex];
        return _getItems().Any(item =>
            item.CanOpenSequenceStep
            && string.Equals(item.SequenceId, target.SequenceId, StringComparison.Ordinal)
            && string.Equals(item.StepId, target.StepId, StringComparison.Ordinal));
    }

    private void MoveReviewStep(int offset)
    {
        if (!CanMoveReviewStep(offset))
        {
            return;
        }

        var targetIndex = _reviewIndex + offset;
        var target = _reviewSteps[targetIndex];
        var displayName = _tryOpenSequenceStep(target.SequenceId, target.StepId);
        if (displayName is null)
        {
            return;
        }

        _reviewIndex = targetIndex;
        _returnStepId = target.StepId;
        RaiseReviewChanged();
        _setStatus(string.Format(
            CultureInfo.CurrentCulture,
            OpenVisionLanguageService.T("Connections.ProcessPlanReviewStatus"),
            _reviewIndex + 1,
            _reviewSteps.Length,
            displayName));
    }

    private void ReturnToProcessPlan()
    {
        if (_returnStepId is not { } stepId)
        {
            return;
        }

        _selectDocumentTab(1);
        var stepText = _selectProcessBlockStep(stepId);
        if (stepText is null)
        {
            Clear();
            return;
        }

        _setStatus(string.Format(
            CultureInfo.CurrentCulture,
            OpenVisionLanguageService.T("Connections.ReturnToProcessPlanStatus"),
            stepText));
    }

    private void RaiseReviewChanged()
    {
        OnPropertyChanged(nameof(HasReturnContext));
        OnPropertyChanged(nameof(ReturnStepId));
        OnPropertyChanged(nameof(ReviewPositionText));
        InvalidateCommands();
    }

    private static void RaiseCanExecuteChanged(ICommand command)
    {
        if (command is RelayCommand relayCommand)
        {
            relayCommand.RaiseCanExecuteChanged();
        }
    }
}
