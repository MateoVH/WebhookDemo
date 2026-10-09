using System.Net.Http.Json;
using System.Text.Json;

namespace WebhookDemo.Tests.Infrastructure;

public sealed record StripeTestEvent(string Id, string PaymentIntentId, string Json);

public static class StripeEvents
{
    public static StripeTestEvent PaymentSucceeded(string? paymentIntentId = null, long amount = 4900, string? email = "ana@example.com")
    {
        var id = NewId("evt");
        var intentId = paymentIntentId ?? NewId("pi");
        var json = JsonSerializer.Serialize(new
        {
            id,
            @object = "event",
            type = "payment_intent.succeeded",
            created = 1760011200,
            data = new
            {
                @object = new
                {
                    id = intentId,
                    @object = "payment_intent",
                    amount,
                    amount_received = amount,
                    currency = "eur",
                    receipt_email = email,
                    status = "succeeded",
                },
            },
        });
        return new StripeTestEvent(id, intentId, json);
    }

    /// <summary>What Stripe Checkout sends alongside payment_intent.succeeded for the same payment.</summary>
    public static StripeTestEvent CheckoutCompleted(string paymentIntentId, long amount = 4900, string email = "ana@example.com")
    {
        var id = NewId("evt");
        var json = JsonSerializer.Serialize(new
        {
            id,
            @object = "event",
            type = "checkout.session.completed",
            created = 1760011200,
            data = new
            {
                @object = new
                {
                    id = NewId("cs_test"),
                    @object = "checkout.session",
                    amount_total = amount,
                    currency = "eur",
                    customer_details = new { email, name = "Ana Gómez" },
                    payment_intent = paymentIntentId,
                    payment_status = "paid",
                },
            },
        });
        return new StripeTestEvent(id, paymentIntentId, json);
    }

    /// <summary>A real event type that no pipeline handles.</summary>
    public static StripeTestEvent CustomerCreated()
    {
        var id = NewId("evt");
        var json = JsonSerializer.Serialize(new
        {
            id,
            @object = "event",
            type = "customer.created",
            data = new { @object = new { id = NewId("cus"), @object = "customer", email = "ana@example.com" } },
        });
        return new StripeTestEvent(id, "", json);
    }

    private static string NewId(string prefix) => $"{prefix}_{Guid.NewGuid():N}";
}

public static class WhatsAppPayloads
{
    public static string NewMessageId() => $"wamid.{Guid.NewGuid():N}";

    /// <summary>One delivery batching several messages, shaped like the WhatsApp Cloud API's.</summary>
    public static string Batch(params (string Id, string From, string Text)[] messages) => JsonSerializer.Serialize(new
    {
        @object = "whatsapp_business_account",
        entry = new[]
        {
            new
            {
                id = "102290129340398",
                changes = new[]
                {
                    new
                    {
                        field = "messages",
                        value = new
                        {
                            messaging_product = "whatsapp",
                            metadata = new { display_phone_number = "15550783881", phone_number_id = "106540352242922" },
                            contacts = messages.Select(m => new { profile = new { name = "Customer " + m.From[^4..] }, wa_id = m.From }).ToArray(),
                            messages = messages.Select(m => new
                            {
                                from = m.From,
                                id = m.Id,
                                timestamp = "1760011200",
                                type = "text",
                                text = new { body = m.Text },
                            }).ToArray(),
                        },
                    },
                },
            },
        },
    });
}

public static class FormPayloads
{
    public static string Submission(string submissionId, string email = "lead@example.com", string name = "Daniel Brooks") =>
        JsonSerializer.Serialize(new
        {
            submissionId,
            form = "contact",
            submittedAt = "2026-10-09T12:00:00Z",
            fields = new { name, email, company = "Brooks Logistics", message = "Can you help with our webhooks?" },
        });
}

public sealed record IngestResponse(int Received, List<IngestResponse.Event> Events)
{
    public sealed record Event(string Id, string Type, string Status, int Deliveries, long InboxId);
}

public static class ResponseExtensions
{
    public static async Task<IngestResponse> ReadIngestAsync(this HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<IngestResponse>())!;

    public static async Task<string> SingleStatusAsync(this HttpResponseMessage response) =>
        Assert.Single((await response.ReadIngestAsync()).Events).Status;
}
