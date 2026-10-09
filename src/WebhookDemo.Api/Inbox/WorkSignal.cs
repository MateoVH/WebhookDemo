namespace WebhookDemo.Inbox;

/// <summary>Lets ingestion wake a worker right away instead of waiting for the next poll.</summary>
public sealed class WorkSignal
{
    private readonly SemaphoreSlim _signal = new(0, 1);

    public void Notify()
    {
        try
        {
            _signal.Release();
        }
        catch (SemaphoreFullException)
        {
            // Already signaled; one wake-up is enough.
        }
    }

    public Task WaitAsync(TimeSpan timeout, CancellationToken ct) => _signal.WaitAsync(timeout, ct);
}
