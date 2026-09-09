using System.Collections.ObjectModel;
using OpenVisionLab.Machine.Core.Sequences;
using OpenVisionLab.Machine.Sequence.Authoring;
using OpenVisionLab.Machine.Sequence.Compilation;

namespace OpenVisionLab.MachineStudio.ViewModel;

/// <summary>
/// Owns the materialized step editors and their definition-change subscriptions
/// for one sequence-editor session.
/// </summary>
internal sealed class SequenceStepEditorCollection : IDisposable
{
    private static readonly SequenceStepAction[] SupportedNonTerminalActions =
    {
        SequenceStepAction.SetSignal,
        SequenceStepAction.WaitSignal,
        SequenceStepAction.MoveAxis,
        SequenceStepAction.WaitAxisDone,
        SequenceStepAction.TriggerCamera,
        SequenceStepAction.WaitVisionResult,
        SequenceStepAction.CallSubsequence
    };

    private readonly SequenceStepTemplateCatalog _templateCatalog;
    private readonly SequenceAuthoringTargetCatalog _targetCatalog;
    private bool _disposed;

    internal SequenceStepEditorCollection(
        SequenceStepTemplateCatalog templateCatalog,
        SequenceAuthoringTargetCatalog targetCatalog)
    {
        _templateCatalog = templateCatalog ?? throw new ArgumentNullException(nameof(templateCatalog));
        _targetCatalog = targetCatalog ?? throw new ArgumentNullException(nameof(targetCatalog));
    }

    internal ObservableCollection<SequenceStepEditorItem> Items { get; } = new();

    internal event EventHandler? DefinitionChanged;

    internal void Clear()
    {
        if (_disposed)
        {
            return;
        }

        foreach (SequenceStepEditorItem step in Items)
        {
            step.DefinitionChanged -= OnStepDefinitionChanged;
        }

        Items.Clear();
    }

    internal void Populate(
        SequenceDefinition sequence,
        IReadOnlyList<SequenceAuthoringTarget> authoringTargets,
        IReadOnlyList<SequenceExpectedStateTarget> expectedStateTargets)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(sequence);
        ArgumentNullException.ThrowIfNull(authoringTargets);
        ArgumentNullException.ThrowIfNull(expectedStateTargets);

        IReadOnlyList<SequenceAuthoringTarget> sequenceTargets =
            _targetCatalog.GetTargetsForSequence(authoringTargets, sequence);
        for (var index = 0; index < sequence.Steps.Count; index++)
        {
            var item = new SequenceStepEditorItem(
                sequence.Steps[index],
                sequence.Id,
                index + 1,
                SupportedNonTerminalActions,
                _templateCatalog,
                sequenceTargets,
                expectedStateTargets);
            item.DefinitionChanged += OnStepDefinitionChanged;
            Items.Add(item);
        }
    }

    internal SequenceStepEditorItem? Find(string? stepId) =>
        Items.FirstOrDefault(step => string.Equals(step.Id, stepId, StringComparison.Ordinal));

    internal void RefreshLocalization()
    {
        foreach (SequenceStepEditorItem step in Items)
        {
            step.RefreshLocalization();
        }
    }

    internal void SetValidation(IEnumerable<SequenceCompilationError> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);
        var errorsByStepId = errors
            .Where(error => error.StepId is not null)
            .GroupBy(error => error.StepId!, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => (IEnumerable<SequenceCompilationError>)group.ToArray(),
                StringComparer.Ordinal);
        foreach (SequenceStepEditorItem step in Items)
        {
            step.SetValidation(errorsByStepId.TryGetValue(step.Id, out IEnumerable<SequenceCompilationError>? stepErrors)
                ? stepErrors
                : Array.Empty<SequenceCompilationError>());
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (SequenceStepEditorItem step in Items)
        {
            step.DefinitionChanged -= OnStepDefinitionChanged;
        }
        Items.Clear();
        DefinitionChanged = null;
    }

    private void OnStepDefinitionChanged(object? sender, EventArgs args) =>
        DefinitionChanged?.Invoke(this, EventArgs.Empty);
}
