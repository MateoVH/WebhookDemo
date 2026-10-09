using System.Net;
using Microsoft.EntityFrameworkCore;
using WebhookDemo.Inbox;
using WebhookDemo.Pipelines.Leads;
using WebhookDemo.Pipelines.Payments;
using WebhookDemo.Pipelines.WhatsApp;
using WebhookDemo.Tests.Infrastructure;

namespace WebhookDemo.Tests;

/// <summary>Providers deliver at least once. Whatever they repeat, each event is stored once and its effects happen once.</summary>
public sealed class DuplicateDeliveryTests
{
    [Fact]
    public async Task Same_event_delivered_three_times_is_stored_once_and_processed_once()
    {
        await using var app = new WebhookApp();
        var payment = StripeEvents.PaymentSucceeded();

        var statuses = new List<string>();
        for (var delivery = 0; delivery < 3; delivery++)
        {
            var response = await app.SendStripeAsync(payment.Json);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode); // 200 for duplicates too, or Stripe would keep retrying
            statuses.Add(await response.SingleStatusAsync());
        }
        await app.SettleAsync();

        Assert.Equal(["accepted", "duplicate", "duplicate"], statuses);
        var inbox = await app.InboxAsync(payment.Id);
        Assert.Equal(3, inbox.DeliveryCount);
        Assert.Equal(1, inbox.Attempts);
        Assert.Equal(InboxStatus.Completed, inbox.Status);
        Assert.Equal(1, await app.CountAsync<Payment>());
        Assert.Equal(1, await app.CountAsync<Invoice>());
        Assert.Equal(1, await app.EmailsSentAsync());
    }

    [Fact]
    public async Task Duplicate_arriving_after_the_event_was_processed_is_acknowledged_and_not_reprocessed()
    {
        await using var app = new WebhookApp();
        var payment = StripeEvents.PaymentSucceeded();
        await app.SendStripeAsync(payment.Json);
        await app.SettleAsync();

        app.Clock.Advance(TimeSpan.FromHours(1)); // Stripe resends events for up to three days
        var late = await app.SendStripeAsync(payment.Json);
        await app.SettleAsync();

        Assert.Equal("duplicate", await late.SingleStatusAsync());
        var inbox = await app.InboxAsync(payment.Id);
        Assert.Equal(InboxStatus.Completed, inbox.Status);
        Assert.Equal(1, inbox.Attempts);
        Assert.Equal(1, await app.CountAsync<Invoice>());
        Assert.Equal(1, await app.EmailsSentAsync());
    }

    [Fact]
    public async Task Twenty_simultaneous_deliveries_of_the_same_event_create_a_single_inbox_row()
    {
        await using var app = new WebhookApp();
        var payment = StripeEvents.PaymentSucceeded();

        var responses = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => app.SendStripeAsync(payment.Json)));
        var statuses = await Task.WhenAll(responses.Select(r => r.SingleStatusAsync()));
        await app.SettleAsync();

        Assert.Single(statuses, "accepted");
        Assert.Equal(19, statuses.Count(s => s == "duplicate"));
        Assert.Equal(1, await app.CountAsync<InboxEvent>());
        Assert.Equal(20, (await app.InboxAsync(payment.Id)).DeliveryCount);
        Assert.Equal(1, await app.CountAsync<Invoice>());
        Assert.Equal(1, await app.EmailsSentAsync());
    }

    [Fact]
    public async Task Two_different_Stripe_events_for_the_same_payment_produce_one_invoice_and_one_email()
    {
        // Stripe Checkout sends both of these for a single payment. Their event ids differ,
        // so only the business key (the PaymentIntent id) can tell they are the same payment.
        await using var app = new WebhookApp();
        var intent = StripeEvents.PaymentSucceeded();
        var session = StripeEvents.CheckoutCompleted(intent.PaymentIntentId);

        await app.SendStripeAsync(session.Json);
        await app.SendStripeAsync(intent.Json);
        await app.SettleAsync();

        Assert.Equal(InboxStatus.Completed, (await app.InboxAsync(session.Id)).Status);
        Assert.Equal(InboxStatus.Completed, (await app.InboxAsync(intent.Id)).Status);
        Assert.Equal(1, await app.CountAsync<Payment>());
        Assert.Equal(1, await app.CountAsync<Invoice>());
        Assert.Equal(1, await app.EmailsSentAsync());
        var deals = await app.ProviderCallsAsync("crm");
        Assert.Single(deals, d => !d.Deduplicated); // created once, then updated in place
    }

    [Fact]
    public async Task WhatsApp_batch_is_split_into_messages_and_each_message_is_deduplicated_on_its_own()
    {
        await using var app = new WebhookApp();
        var (first, second, third) = (WhatsAppPayloads.NewMessageId(), WhatsAppPayloads.NewMessageId(), WhatsAppPayloads.NewMessageId());

        var original = await app.SendWhatsAppAsync(WhatsAppPayloads.Batch((first, "573001112233", "Hola"), (second, "14155550123", "Hi")));
        // A later delivery overlaps the first one: one message we already have, one new.
        var overlapping = await app.SendWhatsAppAsync(WhatsAppPayloads.Batch((second, "14155550123", "Hi"), (third, "447700900123", "Hello")));
        await app.SettleAsync();

        Assert.Equal(["accepted", "accepted"], (await original.ReadIngestAsync()).Events.Select(e => e.Status));
        Assert.Equal(["duplicate", "accepted"], (await overlapping.ReadIngestAsync()).Events.Select(e => e.Status));
        Assert.Equal(3, await app.CountAsync<ChatMessage>());
        Assert.Equal(3, (await app.ProviderCallsAsync("whatsapp")).Count); // one auto-reply per message
    }

    [Fact]
    public async Task Form_resubmitted_with_the_same_submissionId_is_ignored()
    {
        await using var app = new WebhookApp();
        var submission = FormPayloads.Submission("sub_42");

        var first = await app.SendFormAsync(submission);
        var again = await app.SendFormAsync(submission);
        await app.SettleAsync();

        Assert.Equal("accepted", await first.SingleStatusAsync());
        Assert.Equal("duplicate", await again.SingleStatusAsync());
        Assert.Equal(1, await app.CountAsync<Lead>());
        Assert.Equal(1, await app.EmailsSentAsync()); // one "new lead" email to sales
    }

    [Fact]
    public async Task Event_type_without_a_pipeline_is_acknowledged_and_kept_but_never_processed()
    {
        await using var app = new WebhookApp();
        var customerCreated = StripeEvents.CustomerCreated();

        var response = await app.SendStripeAsync(customerCreated.Json);
        await app.SettleAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("ignored", await response.SingleStatusAsync());
        var inbox = await app.InboxAsync(customerCreated.Id);
        Assert.Equal(InboxStatus.Ignored, inbox.Status);
        Assert.Equal(0, inbox.Attempts);
        Assert.Equal(0, await app.QueryAsync(db => db.Checkpoints.CountAsync()));
    }
}
