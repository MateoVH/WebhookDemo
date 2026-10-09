using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WebhookDemo.Data;

namespace WebhookDemo.Inbox;

public enum ProcessOutcome
{
    /// <summary>Nothing was due.</summary>
    Idle,

    /// <summary>An event was handled: completed, scheduled for a retry, or dead-lettered.</summary>
    Processed,

    /// <summary>The worker "died" (simulated) while handling an event.</summary>
    Crashed,
}

/// <summary>
/// Claims one event and runs its pipeline step by step. Every finished step leaves a checkpoint, so whatever goes
/// wrong, the next attempt starts at the first step without one and never repeats a finished step.
/// </summary>
public sealed class InboxProcessor(
    IServiceScopeFactory scopes,
    IChaosMonkey chaos,
    IOptions<ProcessingOptions> options,
    ILogger<InboxProcessor> logger)
{
    public async Task<ProcessOutcome> ProcessNextAsync(string worker, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<WebhookDbContext>();
        var queue = services.GetRequiredService<InboxQueue>();

        var evt = await queue.TryClaimNextAsync(worker, ct);
        if (evt is null) return ProcessOutcome.Idle;

        logger.LogInformation("{Worker} claimed {Source} {ExternalId} (attempt {Attempt})", worker, evt.Source, evt.ExternalId, evt.Attempts);
        string? currentStep = null;
        try
        {
            if (evt.Attempts > options.Value.MaxAttempts)
            {
                logger.LogError("{ExternalId} keeps crashing its workers; dead-lettering it", evt.ExternalId);
                await queue.DeadLetterAsync(evt, worker, "crash-loop", ct);
                return ProcessOutcome.Processed;
            }

            var pipeline = services.GetServices<IWebhookPipeline>().FirstOrDefault(p => p.CanHandle(evt.Source, evt.EventType))
                ?? throw new NonRetryableException($"No pipeline handles {evt.Source} '{evt.EventType}'");
            var steps = pipeline.Plan(evt);
            var completed = await db.Checkpoints.AsNoTracking()
                .Where(c => c.InboxEventId == evt.Id)
                .ToDictionaryAsync(c => c.Step, c => c.Result, ct);

            if (completed.Count > 0 && steps.FirstOrDefault(s => !completed.ContainsKey(s.Name)) is { } resumeAt)
            {
                logger.LogInformation("{ExternalId}: resuming at {Step}, {Done} step(s) already done", evt.ExternalId, resumeAt.Name, completed.Count);
                queue.Log(evt, TimelineKind.Resumed, worker, resumeAt.Name, completed.Count.ToString());
                await db.SaveChangesAsync(ct);
            }

            foreach (var step in steps)
            {
                if (completed.ContainsKey(step.Name)) continue; // finished in an earlier attempt: never run it again

                currentStep = step.Name;
                completed[step.Name] = await RunStepAsync(db, queue, evt, step, new StepContext(evt, completed), worker, ct);
                logger.LogInformation("{ExternalId}: ✓ {Step} → {Result}", evt.ExternalId, step.Name, completed[step.Name]);
                Inject(ChaosPoint.AfterCheckpoint, evt, step);
            }

            currentStep = null;
            await queue.CompleteAsync(evt, worker, ct);
            logger.LogInformation("{ExternalId}: completed by {Worker}", evt.ExternalId, worker);
            return ProcessOutcome.Processed;
        }
        catch (SimulatedCrashException crash)
        {
            // A real crash ends the process right here: no catch block, no status update, no lease release.
            // So the event is left exactly as it is; its lease expires and another worker resumes from the last
            // checkpoint. The timeline entry below is only a breadcrumb for the demo and doesn't touch the event.
            logger.LogWarning("💥 {Worker} crashed ({Point} {Step}) while handling {ExternalId}", worker, crash.Point, crash.Step, evt.ExternalId);
            db.ChangeTracker.Clear();
            queue.Log(evt, TimelineKind.Crashed, worker, crash.Step, crash.Point.ToString());
            await db.SaveChangesAsync(CancellationToken.None);
            return ProcessOutcome.Crashed;
        }
        catch (LeaseLostException lost)
        {
            logger.LogWarning("{Message}; another worker took over", lost.Message);
            return ProcessOutcome.Processed;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            db.ChangeTracker.Clear();
            await queue.ReleaseAsync(evt, worker, CancellationToken.None);
            throw;
        }
        catch (Exception error)
        {
            logger.LogWarning("{ExternalId}: ✗ {Step} failed on attempt {Attempt}: {Error}", evt.ExternalId, currentStep, evt.Attempts, error.Message);
            db.ChangeTracker.Clear(); // drop whatever the failed step had staged
            await queue.FailAsync(evt, worker, currentStep, error, ct);
            return ProcessOutcome.Processed;
        }
    }

    private async Task<string?> RunStepAsync(
        WebhookDbContext db, InboxQueue queue, InboxEvent evt, PipelineStep step, StepContext context, string worker, CancellationToken ct)
    {
        Inject(ChaosPoint.BeforeStep, evt, step);

        if (step.Kind == StepKind.Local)
        {
            // The step's writes, its checkpoint and the lease renewal commit together, or not at all.
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var result = await step.RunLocalAsync(db, context, ct);
            await db.SaveChangesAsync(ct);
            Inject(ChaosPoint.AfterWork, evt, step); // dying here = dying before COMMIT: the database undoes it all
            await queue.CheckpointAsync(evt, step.Name, result, worker, ct);
            await tx.CommitAsync(ct);
            return result;
        }
        else
        {
            // No transaction is held open across a network call. The call is idempotent instead,
            // so if we die before the checkpoint below, making it again on the next attempt is harmless.
            var result = await step.RunExternalAsync(context, ct);
            Inject(ChaosPoint.AfterWork, evt, step); // dying here = the call went through, but no checkpoint says so
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await queue.CheckpointAsync(evt, step.Name, result, worker, ct);
            await tx.CommitAsync(ct);
            return result;
        }
    }

    private void Inject(ChaosPoint point, InboxEvent evt, PipelineStep step)
    {
        switch (chaos.Decide(point, evt, step.Name))
        {
            case ChaosAction.Crash:
                throw new SimulatedCrashException(point, step.Name);
            case ChaosAction.TransientError:
                throw new SimulatedTransientException(step.Name);
            case ChaosAction.PermanentError:
                throw new NonRetryableException($"Simulated permanent failure in '{step.Name}': the provider answered 422 Unprocessable Entity");
        }
    }
}
