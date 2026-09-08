namespace OpenVisionLab.MachineStudio.ViewModel;

/// <summary>
/// Owns the cancellation lifetime for the current manual-camera preparation.
/// </summary>
internal sealed class ManualCameraPreparationLifetime : IDisposable
{
    private readonly object _gate = new();
    private CancellationTokenSource? _source = new();

    internal CancellationToken Token
    {
        get
        {
            lock (_gate)
            {
                return _source?.Token
                    ?? throw new ObjectDisposedException(nameof(ManualCameraPreparationLifetime));
            }
        }
    }

    internal void Invalidate()
    {
        CancellationTokenSource? previous;
        lock (_gate)
        {
            if (_source is null)
            {
                return;
            }

            previous = _source;
            _source = new CancellationTokenSource();
        }

        CancelAndDispose(previous);
    }

    internal void Cancel()
    {
        CancellationTokenSource? source;
        lock (_gate)
        {
            source = _source;
        }

        if (source is null)
        {
            return;
        }

        try
        {
            source.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // A concurrent invalidation owns disposal of the previous source.
        }
    }

    public void Dispose()
    {
        CancellationTokenSource? previous;
        lock (_gate)
        {
            previous = _source;
            _source = null;
        }

        CancelAndDispose(previous);
    }

    private static void CancelAndDispose(CancellationTokenSource? source)
    {
        if (source is null)
        {
            return;
        }

        try
        {
            source.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Another caller completed disposal first.
        }
        finally
        {
            source.Dispose();
        }
    }
}
