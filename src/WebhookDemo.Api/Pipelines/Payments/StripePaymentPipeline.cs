using Microsoft.EntityFrameworkCore;
using WebhookDemo.Inbox;
using WebhookDemo.Integrations;

namespace WebhookDemo.Pipelines.Payments;

/// <summary>
/// Payment succeeded → record the payment → issue the invoice → email the receipt → sync the CRM deal.
///
/// Every effect is keyed by the PaymentIntent id, not by the event id. Stripe Checkout sends both
/// checkout.session.completed and payment_intent.succeeded for one payment: two different events, so
/// deduplicating by event id alone would invoice the customer twice.
/// </summary>
public sealed class StripePaymentPipeline(IEmailSender email, ICrmClient crm, TimeProvider clock) : IWebhookPipeline
{
    public bool CanHandle(string source, string eventType) =>
        source == "stripe" && eventType is "payment_intent.succeeded" or "checkout.session.completed";

    public IReadOnlyList<PipelineStep> Plan(InboxEvent evt)
    {
        var payment = StripePayment.FromEvent(evt.Payload);

        return
        [
            PipelineStep.Local("record-payment", async (db, _, ct) =>
            {
                // Another event about the same payment may have got here first.
                if (await db.Payments.AnyAsync(p => p.PaymentId == payment.PaymentId, ct))
                    return "already recorded";

                db.Payments.Add(new Payment
                {
                    PaymentId = payment.PaymentId,
                    AmountMinor = payment.AmountMinor,
                    Currency = payment.Currency,
                    CustomerEmail = payment.Email,
                    CustomerName = payment.CustomerName,
                    InboxEventId = evt.Id,
                    RecordedAt = clock.GetUtcNow().UtcDateTime,
                });
                return payment.Amount;
            }),

            PipelineStep.Local("issue-invoice", async (db, _, ct) =>
            {
                var existing = await db.Invoices
                    .Where(i => i.PaymentId == payment.PaymentId)
                    .Select(i => (int?)i.Number)
                    .FirstOrDefaultAsync(ct);
                if (existing is not null)
                    return Invoice.Code(existing.Value);

                // The number is taken inside the transaction that also saves the invoice and the checkpoint.
                // A crash before COMMIT rolls back all three: no number is burned and none is used twice.
                var number = (await db.Invoices.MaxAsync(i => (int?)i.Number, ct) ?? 0) + 1;
                db.Invoices.Add(new Invoice
                {
                    Number = number,
                    PaymentId = payment.PaymentId,
                    AmountMinor = payment.AmountMinor,
                    Currency = payment.Currency,
                    CustomerEmail = payment.Email,
                    IssuedAt = clock.GetUtcNow().UtcDateTime,
                });
                return Invoice.Code(number);
            }),

            PipelineStep.External("send-receipt", async (context, ct) =>
            {
                if (payment.Email is null)
                    return "skipped: no customer email";

                // The same key on every attempt, so the email goes out once even if this runs twice.
                var invoice = context.ResultOf("issue-invoice");
                var sent = await email.SendAsync(
                    to: payment.Email,
                    subject: $"Your receipt · {invoice}",
                    body: $"Thanks! We received your payment of {payment.Amount}. Invoice {invoice} is attached.",
                    idempotencyKey: $"receipt:{payment.PaymentId}",
                    ct);
                return sent.Deduplicated ? $"{sent.Id} (already sent)" : sent.Id;
            }),

            PipelineStep.External("sync-crm", async (context, ct) =>
            {
                // An upsert keyed by the payment: running it twice updates the same deal instead of adding one.
                var deal = await crm.UpsertDealAsync(
                    externalId: payment.PaymentId,
                    name: $"{payment.CustomerName ?? payment.Email ?? payment.PaymentId} · {context.ResultOf("issue-invoice")}",
                    amountMinor: payment.AmountMinor,
                    currency: payment.Currency,
                    contactEmail: payment.Email,
                    ct);
                return deal.Deduplicated ? $"{deal.Id} (updated)" : $"{deal.Id} (created)";
            }),
        ];
    }
}
