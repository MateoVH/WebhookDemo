using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WebhookDemo.Data;

namespace WebhookDemo.Inbox;

/// <summary>
/// The worker side of the inbox: leases. Every state change is a conditional UPDATE that only succeeds for the
/// current lease holder, so two workers can never both own an event, and a worker that lost its lease can't write.
/// </summary>
public sealed class InboxQueue(WebhookDbContext db, TimeProvider clock, IOptions<ProcessingOptions> options)
{
    private ProcessingOptions Options => options.Value;
    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    /// <summary>
    /// Claims the next due event: a pending one whose time has come, or one whose worker died (its lease expired).
    /// The attempt is counted here, at claim time, so an event that keeps killing its worker still ends up
    /// dead-lettered instead of crash-looping forever.
    /// </summary>
    public async Task<InboxEvent?> TryClaimNextAsync(string worker, CancellationToken ct)
    {
        // Another worker can win the race for a candidate; then we just look for the next one.
        for (var round = 0; round < 10; round++)
        {
            var now = Now;
            var candidate = await Due(db.InboxEvents, now)
                .OrderBy(e => e.NextAttemptAt).ThenBy(e => e.Id)
                .Select(e => new { e.Id, e.LockedBy })
                .FirstOrDefaultAsync(ct);
            if (candidate is null) return null;

            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var leaseUntil = now + Options.LeaseDuration;
            var claimed = await Due(db.InboxEvents.Where(e => e.Id == candidate.Id), now)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(e => e.Status, InboxStatus.Processing)
                    .SetProperty(e => e.LockedBy, worker)
                    .SetProperty(e => e.LockedUntil, leaseUntil)
                    .SetProperty(e => e.Attempts, e => e.Attempts + 1), ct);
            if (claimed == 0) continue;

            var evt = await db.InboxEvents.AsNoTracking().SingleAsync(e => e.Id == candidate.Id, ct);
            if (candidate.LockedBy is not null)
            {
                Log(evt, TimelineKind.LeaseExpired, worker, detail: candidate.LockedBy);
            }
            Log(evt, TimelineKind.Claimed, worker);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return evt;
        }

        return null;
    }

    /// <summary>
    /// Records that a step completed and extends the lease. Call it inside the step's transaction: if this worker no
    /// longer holds the lease (it stalled and another worker took over), it throws and the whole step rolls back.
    /// </summary>
    public async Task CheckpointAsync(InboxEvent evt, string step, string? result, string worker, CancellationToken ct)
    {
        var now = Now;
        var leaseUntil = now + Options.LeaseDuration;
        var renewed = await Owned(evt.Id, worker)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.LockedUntil, leaseUntil), ct);
        if (renewed == 0) throw new LeaseLostException(evt.ExternalId, worker);

        db.Checkpoints.Add(new StepCheckpoint
        {
            InboxEventId = evt.Id,
            Step = step,
            Result = result,
            Worker = worker,
            Attempt = evt.Attempts,
            CompletedAt = now,
        });
        Log(evt, TimelineKind.StepCompleted, worker, step, result);
        await db.SaveChangesAsync(ct);
    }

    public async Task CompleteAsync(InboxEvent evt, string worker, CancellationToken ct)
    {
        var now = Now;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var updated = await Owned(evt.Id, worker).ExecuteUpdateAsync(s => s
            .SetProperty(e => e.Status, InboxStatus.Completed)
            .SetProperty(e => e.CompletedAt, now)
            .SetProperty(e => e.LastError, (string?)null)
            .SetProperty(e => e.LockedBy, (string?)null)
            .SetProperty(e => e.LockedUntil, (DateTime?)null), ct);
        if (updated == 0) throw new LeaseLostException(evt.ExternalId, worker);

        Log(evt, TimelineKind.Completed, worker);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    /// <summary>Schedules a retry with exponential backoff, or dead-letters the event when retrying is pointless.</summary>
    public async Task FailAsync(InboxEvent evt, string worker, string? step, Exception error, CancellationToken ct)
    {
        var now = Now;
        var retryable = error is not NonRetryableException;
        var giveUp = !retryable || evt.Attempts >= Options.MaxAttempts;
        var delay = Backoff.Delay(evt.Attempts, Options, Random.Shared.NextDouble());
        var nextAttemptAt = now + delay;

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var updated = giveUp
            ? await Owned(evt.Id, worker).ExecuteUpdateAsync(s => s
                .SetProperty(e => e.Status, InboxStatus.DeadLettered)
                .SetProperty(e => e.LastError, error.Message)
                .SetProperty(e => e.LockedBy, (string?)null)
                .SetProperty(e => e.LockedUntil, (DateTime?)null), ct)
            : await Owned(evt.Id, worker).ExecuteUpdateAsync(s => s
                .SetProperty(e => e.Status, InboxStatus.Pending)
                .SetProperty(e => e.NextAttemptAt, nextAttemptAt)
                .SetProperty(e => e.LastError, error.Message)
                .SetProperty(e => e.LockedBy, (string?)null)
                .SetProperty(e => e.LockedUntil, (DateTime?)null), ct);
        if (updated == 0) return; // we lost the lease meanwhile; whoever holds it now is in charge

        Log(evt, TimelineKind.StepFailed, worker, step, error.Message);
        if (giveUp)
        {
            Log(evt, TimelineKind.DeadLettered, worker, step, retryable ? "max-attempts" : "non-retryable");
        }
        else
        {
            Log(evt, TimelineKind.RetryScheduled, worker, step, Math.Ceiling(delay.TotalSeconds).ToString("0"));
        }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    /// <summary>
    /// For an event that keeps killing its worker. A crash never reaches <see cref="FailAsync"/>, so the attempt limit
    /// is enforced when the event is claimed again.
    /// </summary>
    public async Task DeadLetterAsync(InboxEvent evt, string worker, string reason, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var updated = await Owned(evt.Id, worker).ExecuteUpdateAsync(s => s
            .SetProperty(e => e.Status, InboxStatus.DeadLettered)
            .SetProperty(e => e.LastError, $"Gave up after {evt.Attempts - 1} attempts: the worker kept crashing on this event")
            .SetProperty(e => e.LockedBy, (string?)null)
            .SetProperty(e => e.LockedUntil, (DateTime?)null), ct);
        if (updated == 0) return;

        Log(evt, TimelineKind.DeadLettered, worker, detail: reason);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    /// <summary>Graceful shutdown: hand the event back now instead of making the others wait for the lease to expire.</summary>
    public async Task ReleaseAsync(InboxEvent evt, string worker, CancellationToken ct)
    {
        var now = Now;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var updated = await Owned(evt.Id, worker).ExecuteUpdateAsync(s => s
            .SetProperty(e => e.Status, InboxStatus.Pending)
            .SetProperty(e => e.NextAttemptAt, now)
            .SetProperty(e => e.LockedBy, (string?)null)
            .SetProperty(e => e.LockedUntil, (DateTime?)null), ct);
        if (updated == 0) return;

        Log(evt, TimelineKind.Released, worker);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    /// <summary>Puts a dead-lettered event back in the queue. Checkpoints are kept, so it resumes at the step that failed.</summary>
    public async Task<bool> ReplayAsync(long inboxId, CancellationToken ct)
    {
        var now = Now;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var updated = await db.InboxEvents
            .Where(e => e.Id == inboxId && e.Status == InboxStatus.DeadLettered)
            .ExecuteUpdateAsync(s => s
                .SetProperty(e => e.Status, InboxStatus.Pending)
                .SetProperty(e => e.Attempts, 0)
                .SetProperty(e => e.NextAttemptAt, now)
                .SetProperty(e => e.LastError, (string?)null), ct);
        if (updated == 0) return false;

        db.Timeline.Add(new TimelineEntry { InboxEventId = inboxId, At = now, Kind = TimelineKind.Replayed });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return true;
    }

    /// <summary>Adds a timeline entry, saved with the caller's next SaveChanges.</summary>
    public void Log(InboxEvent evt, TimelineKind kind, string? worker = null, string? step = null, string? detail = null) =>
        db.Timeline.Add(new TimelineEntry
        {
            InboxEventId = evt.Id,
            At = Now,
            Kind = kind,
            Worker = worker,
            Step = step,
            Detail = detail,
            Attempt = evt.Attempts,
        });

    private IQueryable<InboxEvent> Owned(long inboxId, string worker) =>
        db.InboxEvents.Where(e => e.Id == inboxId && e.Status == InboxStatus.Processing && e.LockedBy == worker);

    private static IQueryable<InboxEvent> Due(IQueryable<InboxEvent> events, DateTime now) =>
        events.Where(e => (e.Status == InboxStatus.Pending && e.NextAttemptAt <= now)
                       || (e.Status == InboxStatus.Processing && e.LockedUntil < now));
}

public sealed class LeaseLostException(string externalId, string worker)
    : Exception($"{worker} no longer holds the lease on {externalId}");
