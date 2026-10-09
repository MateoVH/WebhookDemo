using Microsoft.EntityFrameworkCore;
using WebhookDemo.Inbox;
using WebhookDemo.Pipelines.Payments;
using WebhookDemo.Tests.Infrastructure;

namespace WebhookDemo.Tests;

/// <summary>A worker can die at any instant. The next one resumes at the first unfinished step and repeats nothing.</summary>
public sealed class CrashRecoveryTests
{
    private static readonly string[] PaymentSteps = ["record-payment", "issue-invoice", "send-receipt", "sync-crm"];

    [Theory]
    [InlineData("record-payment")]
    [InlineData("issue-invoice")]
    [InlineData("send-receipt")]
    [InlineData("sync-crm")]
    public async Task Worker_dies_after_a_step_and_the_next_worker_resumes_at_the_following_step(string lastStepBeforeCrash)
    {
        await using var app = new WebhookApp();
        app.Chaos.Add(new ChaosRule(ChaosPoint.AfterCheckpoint, lastStepBeforeCrash, ChaosAction.Crash));
        var payment = StripeEvents.PaymentSucceeded();
        await app.SendStripeAsync(payment.Json);

        Assert.Equal(ProcessOutcome.Crashed, await app.RunWorkerAsync("worker-A"));

        // The dead worker's lease is still valid, so nobody may touch the event yet.
        var stuck = await app.InboxAsync(payment.Id);
        Assert.Equal(InboxStatus.Processing, stuck.Status);
        Assert.Equal("worker-A", stuck.LockedBy);
        Assert.Equal(ProcessOutcome.Idle, await app.RunWorkerAsync("worker-B"));

        // Once the lease expires, worker B takes over and finishes the job.
        app.Clock.Advance(app.Processing.LeaseDuration + TimeSpan.FromSeconds(1));
        await app.RunWorkerAsync("worker-B");

        Assert.Equal(InboxStatus.Completed, (await app.InboxAsync(payment.Id)).Status);
        var checkpoints = await app.CheckpointsAsync(payment.Id);
        Assert.Equal(PaymentSteps, checkpoints.Select(c => c.Step)); // every step exactly once
        var crashedAfter = Array.IndexOf(PaymentSteps, lastStepBeforeCrash);
        Assert.All(checkpoints.Take(crashedAfter + 1), c => Assert.Equal("worker-A", c.Worker));
        Assert.All(checkpoints.Skip(crashedAfter + 1), c => Assert.Equal("worker-B", c.Worker));
        Assert.Equal(1, await app.CountAsync<Invoice>());
        Assert.Single(await app.ProviderCallsAsync("email"));
        Assert.Contains(await app.TimelineAsync(payment.Id), t => t.Kind == TimelineKind.LeaseExpired && t.Detail == "worker-A");
    }

    [Fact]
    public async Task Crash_inside_the_invoice_transaction_rolls_back_and_leaves_no_gap_in_invoice_numbers()
    {
        await using var app = new WebhookApp();
        var first = StripeEvents.PaymentSucceeded();
        var second = StripeEvents.PaymentSucceeded();
        // The worker dies after INSERTing the invoice and before COMMIT.
        app.Chaos.Add(new ChaosRule(ChaosPoint.AfterWork, "issue-invoice", ChaosAction.Crash, externalId: first.Id));

        await app.SendStripeAsync(first.Json);
        Assert.Equal(ProcessOutcome.Crashed, await app.RunWorkerAsync("worker-A"));
        Assert.Equal(0, await app.CountAsync<Invoice>()); // rolled back: no half-issued invoice

        // Meanwhile another payment arrives and gets number 1: the crash didn't burn it.
        await app.SendStripeAsync(second.Json);
        await app.RunWorkerAsync("worker-B");
        app.Clock.Advance(app.Processing.LeaseDuration + TimeSpan.FromSeconds(1));
        await app.RunWorkerAsync("worker-B");

        var invoices = await app.QueryAsync(db => db.Invoices.OrderBy(i => i.Number).Select(i => new { i.Number, i.PaymentId }).ToListAsync());
        Assert.Equal([1, 2], invoices.Select(i => i.Number));
        Assert.Equal([second.PaymentIntentId, first.PaymentIntentId], invoices.Select(i => i.PaymentId));
    }

    [Fact]
    public async Task Crash_after_the_email_was_sent_but_before_the_checkpoint_does_not_send_it_twice()
    {
        await using var app = new WebhookApp();
        app.Chaos.Add(new ChaosRule(ChaosPoint.AfterWork, "send-receipt", ChaosAction.Crash));
        var payment = StripeEvents.PaymentSucceeded();
        await app.SendStripeAsync(payment.Json);

        Assert.Equal(ProcessOutcome.Crashed, await app.RunWorkerAsync("worker-A"));
        app.Clock.Advance(app.Processing.LeaseDuration + TimeSpan.FromSeconds(1));
        await app.RunWorkerAsync("worker-B");

        // The provider got the request twice, recognized the idempotency key, and sent one email.
        var calls = await app.ProviderCallsAsync("email");
        Assert.Equal(2, calls.Count);
        Assert.Equal([false, true], calls.Select(c => c.Deduplicated));
        Assert.Equal(calls[0].ResourceId, calls[1].ResourceId);
        Assert.Equal(InboxStatus.Completed, (await app.InboxAsync(payment.Id)).Status);
    }

    [Fact]
    public async Task WhatsApp_auto_reply_is_at_least_once_because_the_API_has_no_idempotency_key()
    {
        // The honest limit, written down as a test: when the provider can't deduplicate,
        // dying at the worst moment repeats that one effect. Everything before it still runs once.
        await using var app = new WebhookApp();
        app.Chaos.Add(new ChaosRule(ChaosPoint.AfterWork, "auto-reply", ChaosAction.Crash));
        var messageId = WhatsAppPayloads.NewMessageId();
        await app.SendWhatsAppAsync(WhatsAppPayloads.Batch((messageId, "573001112233", "Hola")));

        Assert.Equal(ProcessOutcome.Crashed, await app.RunWorkerAsync("worker-A"));
        app.Clock.Advance(app.Processing.LeaseDuration + TimeSpan.FromSeconds(1));
        await app.RunWorkerAsync("worker-B");

        Assert.Equal(2, (await app.ProviderCallsAsync("whatsapp")).Count(c => !c.Deduplicated));
        Assert.Equal(["save-message", "upsert-contact", "auto-reply"], (await app.CheckpointsAsync(messageId)).Select(c => c.Step));
    }

    [Fact]
    public async Task Event_that_keeps_killing_its_workers_is_dead_lettered_after_max_attempts()
    {
        await using var app = new WebhookApp();
        app.Chaos.Add(new ChaosRule(ChaosPoint.BeforeStep, "record-payment", ChaosAction.Crash, times: int.MaxValue));
        var payment = StripeEvents.PaymentSucceeded();
        await app.SendStripeAsync(payment.Json);

        await app.SettleAsync();

        var inbox = await app.InboxAsync(payment.Id);
        Assert.Equal(InboxStatus.DeadLettered, inbox.Status);
        Assert.Contains("kept crashing", inbox.LastError);
        var timeline = await app.TimelineAsync(payment.Id);
        Assert.Equal(app.Processing.MaxAttempts, timeline.Count(t => t.Kind == TimelineKind.Crashed));
    }

    [Fact]
    public async Task Worker_that_lost_its_lease_cannot_save_its_work()
    {
        // Worker A stalls right before issuing the invoice (a long GC pause, a frozen VM…). Its lease runs out,
        // worker B takes over and finishes the event. When A wakes up and tries to save, the lease check refuses
        // its checkpoint and A's transaction rolls back.
        WebhookApp app = null!;
        var stalled = false;
        app = new WebhookApp(new CallbackChaosMonkey((point, _, step) =>
        {
            if (point == ChaosPoint.BeforeStep && step == "issue-invoice" && !stalled)
            {
                stalled = true;
                app.Clock.Advance(app.Processing.LeaseDuration + TimeSpan.FromSeconds(1));
                Task.Run(() => app.RunWorkerAsync("worker-B")).GetAwaiter().GetResult();
            }
            return ChaosAction.None;
        }));
        await using var _ = app;
        var payment = StripeEvents.PaymentSucceeded();
        await app.SendStripeAsync(payment.Json);

        await app.RunWorkerAsync("worker-A");

        var checkpoints = await app.CheckpointsAsync(payment.Id);
        Assert.Equal(PaymentSteps, checkpoints.Select(c => c.Step));
        Assert.Equal(["worker-A", "worker-B", "worker-B", "worker-B"], checkpoints.Select(c => c.Worker));
        Assert.Equal(1, await app.CountAsync<Invoice>());
        Assert.Equal(InboxStatus.Completed, (await app.InboxAsync(payment.Id)).Status);
    }
}
