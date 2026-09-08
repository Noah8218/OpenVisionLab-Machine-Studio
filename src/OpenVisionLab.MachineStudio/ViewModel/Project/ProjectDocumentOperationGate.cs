namespace OpenVisionLab.MachineStudio.ViewModel;

/// <summary>
/// Serializes project document transitions without owning project data or UI policy.
/// </summary>
internal sealed class ProjectDocumentOperationGate
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    internal async Task RunAsync(Func<Task> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        await _semaphore.WaitAsync();
        try
        {
            await operation();
        }
        finally
        {
            _semaphore.Release();
        }
    }

    internal async Task<T> RunAsync<T>(Func<Task<T>> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        await _semaphore.WaitAsync();
        try
        {
            return await operation();
        }
        finally
        {
            _semaphore.Release();
        }
    }
}
