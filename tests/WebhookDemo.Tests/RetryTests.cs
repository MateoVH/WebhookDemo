using System.Net;
using WebhookDemo.Inbox;
using WebhookDemo.Pipelines.Payments;
using WebhookDemo.Tests.Infrastructure;

namespace WebhookDemo.Tests;

/// <summary>Failures that may go away are retried with exponential backoff; the rest go to the dead-letter queue.</summary>
public sealed class RetryTests
{
    [Fact]
    public async Task Transient_failure_is_retried_with_exponential_backoff_until_it_succeeds()
    {
        await using var app = new WebhookApp();
        app.Chaos.Add(new ChaosRule(ChaosPoint.BeforeStep, "send-receipt", ChaosAction.TransientError, times: 2));
        var payment = StripeEvents.PaymentSucceeded();
        await app.SendStripeAsync(payment.Json);

        await app.RunWorkerAsync("worker"); // attempt 1 fails at send-receipt
        var afterFirst = await app.InboxAsync(payment.Id);
        Assert.Equal(InboxStatus.Pending, afterFirst.Status);
        Assert.Equal(app.Clock.GetUtcNow().UtcDateTime.AddSeconds(2), afterFirst.NextAttemptAt);

        Assert.Equal(ProcessOutcome.Idle, await app.RunWorkerAsync("worker")); // not due yet

        app.Clock.Advance(TimeSpan.FromSeconds(2));
        await app.RunWorkerAsync("worker"); // attempt 2 fails again: the wait doubles
        Assert.Equal(app.Clock.GetUtcNow().UtcDateTime.AddSeconds(4), (await app.InboxAsync(payment.Id)).NextAttemptAt);

        app.Clock.Advance(TimeSpan.FromSeconds(4));
        await app.RunWorkerAsync("worker"); // attempt 3 succeeds

        var done = await app.InboxAsync(payment.Id);
        Assert.Equal(InboxStatus.Completed, done.Status);
        Assert.Equal(3, done.Attempts);
        // The steps before the failing one ran once, not three times.
        Assert.Equal(1, await app.CountAsync<Payment>());
        Assert.Equal(1, await app.CountAsync<Invoice>());
        Assert.Equal(1, await app.EmailsSentAsync());
        var retries = (await app.TimelineAsync(payment.Id)).Where(t => t.Kind == TimelineKind.RetryScheduled);
        Assert.Equal(["2", "4"], retries.Select(r => r.Detail));
    }

    [Fact]
    public async Task Failure_that_never_goes_away_ends_in_the_dead_letter_queue_after_max_attempts()
    {
        await using var app = new WebhookApp();
        app.Chaos.Add(new ChaosRule(ChaosPoint.BeforeStep, "send-receipt", ChaosAction.TransientError, times: int.MaxValue));
        var payment = StripeEvents.PaymentSucceeded();
        await app.SendStripeAsync(payment.Json);

        await app.SettleAsync();

        var inbox = await app.InboxAsync(payment.Id);
        Assert.Equal(InboxStatus.DeadLettered, inbox.Status);
        Assert.Equal(app.Processing.MaxAttempts, inbox.Attempts);
        Assert.Contains("503", inbox.LastError);
        var timeline = await app.TimelineAsync(payment.Id);
        Assert.Equal(["2", "4", "8", "16"], timeline.Where(t => t.Kind == TimelineKind.RetryScheduled).Select(t => t.Detail));
        Assert.Equal("max-attempts", Assert.Single(timeline, t => t.Kind == TimelineKind.DeadLettered).Detail);
    }

    [Fact]
    public async Task Non_retryable_failure_goes_straight_to_the_dead_letter_queue()
    {
        await using var app = new WebhookApp();
        app.Chaos.Add(new ChaosRule(ChaosPoint.BeforeStep, "sync-crm", ChaosAction.PermanentError));
        var payment = StripeEvents.PaymentSucceeded();
        await app.SendStripeAsync(payment.Json);

        await app.SettleAsync();

        var inbox = await app.InboxAsync(payment.Id);
        Assert.Equal(InboxStatus.DeadLettered, inbox.Status);
        Assert.Equal(1, inbox.Attempts);
        Assert.Contains("422", inbox.LastError);
    }

    [Fact]
    public async Task Replaying_a_dead_lettered_event_resumes_at_the_step_that_failed()
    {
        await using var app = new WebhookApp();
        app.Chaos.Add(new ChaosRule(ChaosPoint.BeforeStep, "sync-crm", ChaosAction.PermanentError));
        var payment = StripeEvents.PaymentSucceeded();
        await app.SendStripeAsync(payment.Json);
        await app.SettleAsync();
        var deadLettered = await app.InboxAsync(payment.Id);
        Assert.Equal(InboxStatus.DeadLettered, deadLettered.Status);

        // Someone fixes the CRM integration and replays the event from the dashboard.
        var replay = await app.PostAsync($"/api/events/{deadLettered.Id}/replay", "");
        await app.SettleAsync();

        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(InboxStatus.Completed, (await app.InboxAsync(payment.Id)).Status);
        var checkpoints = await app.CheckpointsAsync(payment.Id);
        Assert.Equal(["record-payment", "issue-invoice", "send-receipt", "sync-crm"], checkpoints.Select(c => c.Step));
        Assert.Equal(1, await app.CountAsync<Invoice>());
        Assert.Equal(1, await app.EmailsSentAsync());

        var again = await app.PostAsync($"/api/events/{deadLettered.Id}/replay", "");
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode); // only dead-lettered events can be replayed
    }

    [Fact]
    public async Task Payload_that_can_never_be_processed_is_dead_lettered_without_retrying()
    {
        // Correctly signed, with an id and a type, but the payment has no amount: no retry will fix that.
        await using var app = new WebhookApp();
        const string json = """{"id":"evt_no_amount","type":"payment_intent.succeeded","data":{"object":{"id":"pi_1","currency":"eur"}}}""";

        var response = await app.SendStripeAsync(json);
        await app.SettleAsync();

        Assert.Equal("accepted", await response.SingleStatusAsync());
        var inbox = await app.InboxAsync("evt_no_amount");
        Assert.Equal(InboxStatus.DeadLettered, inbox.Status);
        Assert.Equal(1, inbox.Attempts);
        Assert.StartsWith("Malformed Stripe payment event", inbox.LastError);
    }
}
