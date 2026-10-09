using Microsoft.EntityFrameworkCore;
using WebhookDemo.Data;
using WebhookDemo.Webhooks;

namespace WebhookDemo.Inbox;

public sealed record InboxWriteResult(long InboxId, bool IsDuplicate, int DeliveryCount);

/// <summary>
/// Stores incoming events. Deduplication is a single atomic statement, so two deliveries of the same event
/// arriving at the same instant can't both get in: there is no "check, then insert" window to race through.
/// </summary>
public sealed class InboxWriter(WebhookDbContext db, TimeProvider clock, WorkSignal signal)
{
    public async Task<InboxWriteResult> RecordAsync(string source, IncomingEvent incoming, bool hasPipeline, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var status = (hasPipeline ? InboxStatus.Pending : InboxStatus.Ignored).ToString();

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        // Inserts the event, or, if (Source, ExternalId) already exists, just counts one more delivery.
        // The same statement works on PostgreSQL.
        var rows = await db.Database.SqlQuery<UpsertedRow>($"""
            INSERT INTO "InboxEvents"
                ("Source", "ExternalId", "EventType", "Payload", "Summary", "Status",
                 "DeliveryCount", "Attempts", "ReceivedAt", "LastDeliveredAt", "NextAttemptAt")
            VALUES
                ({source}, {incoming.ExternalId}, {incoming.EventType}, {incoming.Payload}, {incoming.Summary}, {status},
                 1, 0, {now}, {now}, {now})
            ON CONFLICT ("Source", "ExternalId") DO UPDATE
                SET "DeliveryCount" = "InboxEvents"."DeliveryCount" + 1,
                    "LastDeliveredAt" = excluded."LastDeliveredAt"
            RETURNING "Id", "DeliveryCount"
            """).ToListAsync(ct);
        var row = rows.Single();
        var isDuplicate = row.DeliveryCount > 1;

        db.Timeline.Add(new TimelineEntry
        {
            InboxEventId = row.Id,
            At = now,
            Kind = isDuplicate ? TimelineKind.Duplicate : TimelineKind.Received,
            Detail = row.DeliveryCount.ToString(),
        });
        if (!isDuplicate && !hasPipeline)
        {
            db.Timeline.Add(new TimelineEntry { InboxEventId = row.Id, At = now, Kind = TimelineKind.Ignored, Detail = incoming.EventType });
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        if (!isDuplicate && hasPipeline) signal.Notify();
        return new InboxWriteResult(row.Id, isDuplicate, row.DeliveryCount);
    }

    internal sealed class UpsertedRow
    {
        public long Id { get; set; }
        public int DeliveryCount { get; set; }
    }
}
