using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WebhookDemo.Data;
using WebhookDemo.Demo;
using WebhookDemo.Inbox;
using WebhookDemo.Pipelines.Payments;
using WebhookDemo.Shared;

namespace WebhookDemo.Dashboard;

public sealed record StepView(string Name, StepKind Kind, bool Done, string? Result, string? Worker);

public sealed record EventView(
    long Id,
    string Source,
    string Type,
    string ExternalId,
    string? Summary,
    InboxStatus Status,
    int Attempts,
    int Deliveries,
    DateTime ReceivedAt,
    DateTime NextAttemptAt,
    string? LockedBy,
    DateTime? LockedUntil,
    string? LastError,
    IReadOnlyList<StepView> Steps);

/// <summary>
/// The read API behind the dashboard, plus replay for dead-lettered events.
/// A real deployment would put these behind authentication.
/// </summary>
public static class DashboardEndpoints
{
    private static readonly JsonSerializerOptions PrettyJson = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static void MapDashboardEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api");
        api.MapGet("/overview", GetOverviewAsync);
        api.MapGet("/events/{id:long}", GetEventAsync);
        api.MapPost("/events/{id:long}/replay", ReplayAsync);
    }

    private static async Task<IResult> GetOverviewAsync(
        WebhookDbContext db, IEnumerable<IWebhookPipeline> pipelines, TimeProvider clock, IOptions<DemoOptions> demo, CancellationToken ct)
    {
        var byStatus = await db.InboxEvents
            .GroupBy(e => e.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Status, x => x.Count, ct);
        var events = byStatus.Values.Sum();
        var deliveries = await db.InboxEvents.SumAsync(e => e.DeliveryCount, ct);
        var invoices = await db.Invoices.CountAsync(ct);
        var lastInvoice = await db.Invoices.MaxAsync(i => (int?)i.Number, ct) ?? 0;
        var emails = await db.ProviderCalls
            .Where(c => c.Provider == "email")
            .GroupBy(c => c.Deduplicated)
            .Select(g => new { Deduplicated = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Deduplicated, x => x.Count, ct);
        var timeline = await db.Timeline
            .Where(t => t.Kind == TimelineKind.LeaseExpired || t.Kind == TimelineKind.RetryScheduled || t.Kind == TimelineKind.Crashed)
            .GroupBy(t => t.Kind)
            .Select(g => new { Kind = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Kind, x => x.Count, ct);

        var recent = await db.InboxEvents.AsNoTracking().OrderByDescending(e => e.Id).Take(25).ToListAsync(ct);
        var ids = recent.Select(e => e.Id).ToList();
        var checkpoints = (await db.Checkpoints.AsNoTracking().Where(c => ids.Contains(c.InboxEventId)).ToListAsync(ct))
            .ToLookup(c => c.InboxEventId);

        var latestInvoices = await db.Invoices.AsNoTracking().OrderByDescending(i => i.Number).Take(6).ToListAsync(ct);
        var providerCalls = await db.ProviderCalls.AsNoTracking().OrderByDescending(c => c.Id).Take(8).ToListAsync(ct);

        return Results.Ok(new
        {
            serverTime = clock.GetUtcNow(),
            demoEnabled = demo.Value.Enabled,
            stats = new
            {
                deliveries,
                events,
                duplicates = deliveries - events,
                pending = byStatus.GetValueOrDefault(InboxStatus.Pending),
                processing = byStatus.GetValueOrDefault(InboxStatus.Processing),
                completed = byStatus.GetValueOrDefault(InboxStatus.Completed),
                ignored = byStatus.GetValueOrDefault(InboxStatus.Ignored),
                deadLettered = byStatus.GetValueOrDefault(InboxStatus.DeadLettered),
                invoices,
                invoiceGaps = lastInvoice - invoices,
                emailsSent = emails.GetValueOrDefault(false),
                emailsDeduplicated = emails.GetValueOrDefault(true),
                crashes = timeline.GetValueOrDefault(TimelineKind.Crashed),
                recoveries = timeline.GetValueOrDefault(TimelineKind.LeaseExpired),
                retries = timeline.GetValueOrDefault(TimelineKind.RetryScheduled),
            },
            events = recent.Select(e => ToView(e, checkpoints[e.Id], pipelines)),
            invoices = latestInvoices.Select(i => new
            {
                code = Invoice.Code(i.Number),
                i.PaymentId,
                amount = Money.Format(i.AmountMinor, i.Currency),
                i.CustomerEmail,
                i.IssuedAt,
            }),
            providerCalls = providerCalls.Select(c => new { c.Provider, c.Operation, c.Key, c.ResourceId, c.Summary, c.Deduplicated, c.At }),
        });
    }

    private static async Task<IResult> GetEventAsync(long id, WebhookDbContext db, IEnumerable<IWebhookPipeline> pipelines, CancellationToken ct)
    {
        var evt = await db.InboxEvents.AsNoTracking().FirstOrDefaultAsync(e => e.Id == id, ct);
        if (evt is null) return Results.NotFound();

        var checkpoints = await db.Checkpoints.AsNoTracking().Where(c => c.InboxEventId == id).ToListAsync(ct);
        var timeline = await db.Timeline.AsNoTracking().Where(t => t.InboxEventId == id).OrderBy(t => t.Id).ToListAsync(ct);

        return Results.Ok(new
        {
            @event = ToView(evt, checkpoints, pipelines),
            timeline = timeline.Select(t => new { t.At, t.Kind, t.Step, t.Detail, t.Worker, t.Attempt }),
            payload = Pretty(evt.Payload),
        });
    }

    private static async Task<IResult> ReplayAsync(long id, InboxQueue queue, WorkSignal signal, CancellationToken ct)
    {
        if (!await queue.ReplayAsync(id, ct))
            return Results.Conflict(new { error = "Only dead-lettered events can be replayed." });

        signal.Notify();
        return Results.Ok(new { replayed = id });
    }

    private static EventView ToView(InboxEvent evt, IEnumerable<StepCheckpoint> checkpoints, IEnumerable<IWebhookPipeline> pipelines)
    {
        var done = checkpoints.ToDictionary(c => c.Step);
        IReadOnlyList<PipelineStep> plan;
        try
        {
            plan = pipelines.FirstOrDefault(p => p.CanHandle(evt.Source, evt.EventType))?.Plan(evt) ?? [];
        }
        catch (NonRetryableException)
        {
            plan = []; // a malformed payload has no plan; its LastError says why
        }

        return new EventView(
            evt.Id, evt.Source, evt.EventType, evt.ExternalId, evt.Summary, evt.Status, evt.Attempts, evt.DeliveryCount,
            evt.ReceivedAt, evt.NextAttemptAt, evt.LockedBy, evt.LockedUntil, evt.LastError,
            plan.Select(s => new StepView(
                s.Name,
                s.Kind,
                done.ContainsKey(s.Name),
                done.GetValueOrDefault(s.Name)?.Result,
                done.GetValueOrDefault(s.Name)?.Worker)).ToList());
    }

    private static string Pretty(string json)
    {
        try
        {
            return JsonNode.Parse(json)?.ToJsonString(PrettyJson) ?? json;
        }
        catch (JsonException)
        {
            return json;
        }
    }
}
