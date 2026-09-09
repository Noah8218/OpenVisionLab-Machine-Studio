using OpenVisionLab.Machine.Core.Projects;

namespace OpenVisionLab.MachineStudio.ViewModel;

/// <summary>
/// Owns the guarded transaction that validates and applies an authored
/// project to the live simulation runtime. MainViewModel supplies only the
/// shell-state callbacks and keeps project presentation ownership.
/// </summary>
internal sealed class ProjectRuntimeApplicationWorkflow
{
    private readonly RuntimeDefinitionApplicationWorkflow _runtimeDefinitionApplicationWorkflow;
    private readonly Action<bool> _onApplyingStateChanged;
    private readonly Action<RuntimeDefinitionApplicationResult> _onRejected;
    private readonly Action<MachineProjectDocument> _onApplied;
    private int _isApplying;

    internal ProjectRuntimeApplicationWorkflow(
        RuntimeDefinitionApplicationWorkflow runtimeDefinitionApplicationWorkflow,
        Action<bool> onApplyingStateChanged,
        Action<RuntimeDefinitionApplicationResult> onRejected,
        Action<MachineProjectDocument> onApplied)
    {
        _runtimeDefinitionApplicationWorkflow = runtimeDefinitionApplicationWorkflow
            ?? throw new ArgumentNullException(nameof(runtimeDefinitionApplicationWorkflow));
        _onApplyingStateChanged = onApplyingStateChanged
            ?? throw new ArgumentNullException(nameof(onApplyingStateChanged));
        _onRejected = onRejected ?? throw new ArgumentNullException(nameof(onRejected));
        _onApplied = onApplied ?? throw new ArgumentNullException(nameof(onApplied));
    }

    internal bool IsApplying => Volatile.Read(ref _isApplying) != 0;

    internal async Task<bool> ApplyAsync(MachineProjectDocument project)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (Interlocked.CompareExchange(ref _isApplying, 1, 0) != 0)
        {
            return false;
        }

        try
        {
            _onApplyingStateChanged(true);
            var result = await _runtimeDefinitionApplicationWorkflow.ApplyAsync(project);
            if (!result.IsAccepted)
            {
                _onRejected(result);
                return false;
            }

            _onApplied(project);
            return true;
        }
        finally
        {
            Volatile.Write(ref _isApplying, 0);
            _onApplyingStateChanged(false);
        }
    }
}
