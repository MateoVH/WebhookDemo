using Microsoft.Extensions.Options;

namespace WebhookDemo.Inbox;

/// <summary>Runs the worker loops of this process. Each one claims and processes events until the app stops.</summary>
public sealed class InboxWorker(
    InboxProcessor processor,
    WorkSignal signal,
    IOptions<ProcessingOptions> options,
    ILogger<InboxWorker> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(Enumerable.Range(1, options.Value.Workers).Select(n => RunAsync(n, stoppingToken)));

    private async Task RunAsync(int number, CancellationToken ct)
    {
        var worker = Identity(number);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                switch (await processor.ProcessNextAsync(worker, ct))
                {
                    case ProcessOutcome.Idle:
                        await signal.WaitAsync(options.Value.PollInterval, ct);
                        break;
                    case ProcessOutcome.Crashed:
                        // The process "died". A real one would be restarted by the host as a new process,
                        // so it comes back with a new identity, and the old lease is left to expire.
                        worker = Identity(number);
                        break;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // The database is unreachable, say. Back off and try again; nothing is lost, the inbox is durable.
                logger.LogError(ex, "{Worker} failed unexpectedly; retrying shortly", worker);
                try
                {
                    await Task.Delay(options.Value.PollInterval, ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private static string Identity(int number) => $"worker-{number}#{Random.Shared.Next(0x1000, 0x10000):x4}";
}
