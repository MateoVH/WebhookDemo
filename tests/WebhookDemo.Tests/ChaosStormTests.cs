using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using WebhookDemo.Inbox;
using WebhookDemo.Pipelines.Payments;
using WebhookDemo.Tests.Infrastructure;

namespace WebhookDemo.Tests;

/// <summary>
/// Everything at once. Thousands of payments arrive out of order and repeated, workers crash at random
/// moments and providers fail, and still every payment ends with exactly one invoice, numbered without gaps,
/// one receipt and one CRM deal.
/// </summary>
public sealed class ChaosStormTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Storm_of_4409_payments_with_duplicates_crashes_and_flaky_providers_yields_4409_gapless_invoices()
    {
        const int Payments = 4409;
        var random = new Random(Payments);
        var chaos = new RandomChaosMonkey(seed: Payments, crashRate: 0.02, transientErrorRate: 0.03);
        await using var app = new WebhookApp(chaos, new Dictionary<string, string> { ["Processing:MaxAttempts"] = "100" });
        var stopwatch = Stopwatch.StartNew();

        // 4,409 payments. One in five arrives the Stripe Checkout way: two different events for the same payment.
        var events = new List<StripeTestEvent>();
        for (var i = 0; i < Payments; i++)
        {
            var amount = random.Next(500, 50_000);
            var intent = StripeEvents.PaymentSucceeded(amount: amount);
            events.Add(intent);
            if (random.NextDouble() < 0.2) events.Add(StripeEvents.CheckoutCompleted(intent.PaymentIntentId, amount));
        }

        // Each event is delivered one to three times, all shuffled together, sixteen requests at a time.
        var deliveries = events
            .SelectMany(e => Enumerable.Repeat(e, random.Next(1, 4)))
            .OrderBy(_ => random.Next())
            .ToList();
        await Parallel.ForEachAsync(deliveries, new ParallelOptions { MaxDegreeOfParallelism = 16 }, async (e, _) =>
            (await app.SendStripeAsync(e.Json)).EnsureSuccessStatusCode());

        var delivered = stopwatch.Elapsed;

        // Four workers process it all while the chaos monkey kills them and breaks the providers.
        await app.SettleAsync(workers: 4);
        var processed = stopwatch.Elapsed;

        var invoices = await app.QueryAsync(db => db.Invoices.OrderBy(i => i.Number).Select(i => new { i.Number, i.PaymentId }).ToListAsync());
        Assert.Equal(Enumerable.Range(1, Payments), invoices.Select(i => i.Number)); // INV-000001 … INV-004409: no gaps, no repeats
        Assert.Equal(Payments, invoices.Select(i => i.PaymentId).Distinct().Count()); // one invoice per payment
        Assert.Equal(Payments, await app.CountAsync<Payment>());
        Assert.Equal(Payments, await app.EmailsSentAsync());
        Assert.Equal(Payments, (await app.ProviderCallsAsync("crm")).Count(c => !c.Deduplicated));

        var inbox = await app.QueryAsync(db => db.InboxEvents.AsNoTracking().Select(e => new { e.Status, e.DeliveryCount }).ToListAsync());
        Assert.Equal(events.Count, inbox.Count);
        Assert.All(inbox, e => Assert.Equal(InboxStatus.Completed, e.Status));
        Assert.Equal(deliveries.Count, inbox.Sum(e => e.DeliveryCount));
        Assert.True(chaos.Crashes > 100 && chaos.TransientErrors > 100, "the storm should actually be stormy");

        var emailRequests = (await app.ProviderCallsAsync("email")).Count;
        output.WriteLine($"""
            {Payments:N0} payments → {events.Count:N0} distinct events → {deliveries.Count:N0} deliveries ({deliveries.Count - events.Count:N0} duplicates absorbed)
            Chaos: {chaos.Crashes:N0} worker crashes, {chaos.TransientErrors:N0} provider failures
            Result: {invoices.Count:N0} invoices (INV-000001 … {Invoice.Code(Payments)}, no gaps, no repeats), {Payments:N0} emails sent out of {emailRequests:N0} requests, {Payments:N0} CRM deals
            Took {stopwatch.Elapsed.TotalSeconds:0.0}s: delivering {delivered.TotalSeconds:0.0}s, processing {(processed - delivered).TotalSeconds:0.0}s
            """);
    }
}
