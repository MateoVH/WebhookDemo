using Microsoft.EntityFrameworkCore;
using WebhookDemo.Inbox;
using WebhookDemo.Tests.Infrastructure;

namespace WebhookDemo.Tests;

public sealed class ConcurrencyTests
{
    [Fact]
    public async Task Eight_workers_in_parallel_process_two_hundred_events_exactly_once()
    {
        await using var app = new WebhookApp();
        var payments = Enumerable.Range(0, 200).Select(_ => StripeEvents.PaymentSucceeded()).ToList();
        foreach (var payment in payments) await app.SendStripeAsync(payment.Json);

        await Task.WhenAll(Enumerable.Range(1, 8).Select(n => app.RunWorkerAsync($"worker-{n}")));

        var inbox = await app.QueryAsync(db => db.InboxEvents.AsNoTracking().ToListAsync());
        Assert.All(inbox, e =>
        {
            Assert.Equal(InboxStatus.Completed, e.Status);
            Assert.Equal(1, e.Attempts); // claimed by exactly one worker
        });
        var invoiceNumbers = await app.QueryAsync(db => db.Invoices.OrderBy(i => i.Number).Select(i => i.Number).ToListAsync());
        Assert.Equal(Enumerable.Range(1, 200), invoiceNumbers);
        var emails = await app.ProviderCallsAsync("email");
        Assert.Equal(200, emails.Count); // no step ever ran twice, so the provider never even saw a repeat
        Assert.DoesNotContain(emails, e => e.Deduplicated);
    }
}
