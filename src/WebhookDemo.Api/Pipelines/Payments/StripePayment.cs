using System.Text.Json;
using WebhookDemo.Inbox;
using WebhookDemo.Shared;

namespace WebhookDemo.Pipelines.Payments;

/// <summary>What the payment pipeline needs from a Stripe event, whichever of the two event types carried it.</summary>
public sealed record StripePayment(string PaymentId, long AmountMinor, string Currency, string? Email, string? CustomerName)
{
    public string Amount => Money.Format(AmountMinor, Currency);

    public static StripePayment FromEvent(string eventJson)
    {
        try
        {
            using var document = JsonDocument.Parse(eventJson);
            var root = document.RootElement;
            var obj = root.GetProperty("data").GetProperty("object");

            return root.RequiredString("type") switch
            {
                "payment_intent.succeeded" => new StripePayment(
                    PaymentId: obj.RequiredString("id"),
                    AmountMinor: obj.OptionalInt64("amount_received") ?? obj.GetProperty("amount").GetInt64(),
                    Currency: obj.RequiredString("currency"),
                    Email: obj.OptionalString("receipt_email"),
                    CustomerName: obj.OptionalObject("shipping")?.OptionalString("name")),

                // The session points at the same PaymentIntent. That shared id is what stops
                // the two events from turning into two invoices.
                "checkout.session.completed" => new StripePayment(
                    PaymentId: obj.OptionalString("payment_intent") ?? obj.RequiredString("id"),
                    AmountMinor: obj.GetProperty("amount_total").GetInt64(),
                    Currency: obj.RequiredString("currency"),
                    Email: obj.OptionalObject("customer_details")?.OptionalString("email"),
                    CustomerName: obj.OptionalObject("customer_details")?.OptionalString("name")),

                var type => throw new NonRetryableException($"Unsupported Stripe event type '{type}'"),
            };
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new NonRetryableException($"Malformed Stripe payment event: {ex.Message}", ex);
        }
    }
}
