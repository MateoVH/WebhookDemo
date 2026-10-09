using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace WebhookDemo.Demo;

/// <summary>Realistic fake payloads, shaped like the ones Stripe, Meta and form tools actually send.</summary>
internal static class DemoData
{
    private const string StripeApiVersion = "2025-09-30.clover";
    private const string WhatsAppBusinessAccountId = "102290129340398";
    private const string WhatsAppPhoneNumberId = "106540352242922";
    private const string WhatsAppDisplayNumber = "15550783881";
    private const string Alphanumeric = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly Customer[] Customers =
    [
        new("Ana Gómez", "ana.gomez@example.com"),
        new("Liam Carter", "liam.carter@example.com"),
        new("Lucía Fernández", "lucia.fernandez@example.com"),
        new("Noah Schmidt", "noah.schmidt@example.com"),
        new("Valentina Ríos", "valentina.rios@example.com"),
        new("Oliver Bennett", "oliver.bennett@example.com"),
    ];

    private static readonly (string Phone, string Name, string Text)[] WhatsAppMessages =
    [
        ("34611223344", "Carmen Ortiz", "Hola, ¿tienen el faro delantero de un Seat Ibiza 2015?"),
        ("573001112233", "Julián Mesa", "Buenas, ¿mi pedido ya salió?"),
        ("14155550123", "Emma Wilson", "Hi! Can I change the delivery address of my order?"),
        ("447700900123", "Jack Turner", "Do you ship to the UK?"),
    ];

    private static readonly (string Name, string Email, string Company, string Message)[] Leads =
    [
        ("Sofía Martín", "sofia@autopartes-martin.example", "Autopartes Martín", "Necesitamos que los pagos de Stripe lleguen solos a HubSpot."),
        ("Daniel Brooks", "daniel@brooks-logistics.example", "Brooks Logistics", "Our order webhooks sometimes create duplicates. Can you help?"),
        ("Marta Vidal", "marta@clinicavidal.example", "Clínica Vidal", "Queremos confirmar citas por WhatsApp automáticamente."),
    ];

    public sealed record Customer(string Name, string Email);

    public sealed record StripeEvent(string EventId, string PaymentIntentId, string Json);

    public static Customer RandomCustomer() => Pick(Customers);

    public static long RandomAmount() => Random.Shared.Next(19, 250) * 100 + Pick<long>([0, 50, 90, 99]);

    public static string RandomCurrency() => Pick(["eur", "usd"]);

    public static StripeEvent PaymentIntentSucceeded(
        DateTimeOffset now, string? paymentIntentId = null, long? amount = null, string? currency = null, Customer? customer = null)
    {
        var buyer = customer ?? RandomCustomer();
        var intentId = paymentIntentId ?? NewId("pi");
        var eventId = NewId("evt");
        var total = amount ?? RandomAmount();

        var json = JsonSerializer.Serialize(new
        {
            id = eventId,
            @object = "event",
            api_version = StripeApiVersion,
            created = now.ToUnixTimeSeconds(),
            data = new
            {
                @object = new
                {
                    id = intentId,
                    @object = "payment_intent",
                    amount = total,
                    amount_received = total,
                    currency = currency ?? RandomCurrency(),
                    status = "succeeded",
                    receipt_email = buyer.Email,
                    shipping = new { name = buyer.Name },
                    metadata = new { order_id = $"ORD-{Random.Shared.Next(1000, 10000)}" },
                },
            },
            livemode = false,
            pending_webhooks = 1,
            request = new { id = NewId("req", 14), idempotency_key = Guid.NewGuid().ToString() },
            type = "payment_intent.succeeded",
        }, Json);

        return new StripeEvent(eventId, intentId, json);
    }

    public static StripeEvent CheckoutSessionCompleted(DateTimeOffset now, string paymentIntentId, long amount, string currency, Customer customer)
    {
        var eventId = NewId("evt");
        var json = JsonSerializer.Serialize(new
        {
            id = eventId,
            @object = "event",
            api_version = StripeApiVersion,
            created = now.ToUnixTimeSeconds(),
            data = new
            {
                @object = new
                {
                    id = NewId("cs_test", 58),
                    @object = "checkout.session",
                    amount_total = amount,
                    currency,
                    customer_details = new { email = customer.Email, name = customer.Name },
                    mode = "payment",
                    payment_intent = paymentIntentId,
                    payment_status = "paid",
                    status = "complete",
                },
            },
            livemode = false,
            pending_webhooks = 1,
            request = new { id = (string?)null, idempotency_key = (string?)null },
            type = "checkout.session.completed",
        }, Json);

        return new StripeEvent(eventId, paymentIntentId, json);
    }

    /// <summary>One delivery carrying two messages from different customers, the way Meta batches them.</summary>
    public static string WhatsAppBatch(DateTimeOffset now)
    {
        var picked = WhatsAppMessages.OrderBy(_ => Random.Shared.Next()).Take(2).ToArray();
        return JsonSerializer.Serialize(new
        {
            @object = "whatsapp_business_account",
            entry = new[]
            {
                new
                {
                    id = WhatsAppBusinessAccountId,
                    changes = new[]
                    {
                        new
                        {
                            field = "messages",
                            value = new
                            {
                                messaging_product = "whatsapp",
                                metadata = new { display_phone_number = WhatsAppDisplayNumber, phone_number_id = WhatsAppPhoneNumberId },
                                contacts = picked.Select(m => new { profile = new { name = m.Name }, wa_id = m.Phone }).ToArray(),
                                messages = picked.Select(m => new
                                {
                                    from = m.Phone,
                                    id = NewWamid(),
                                    timestamp = now.ToUnixTimeSeconds().ToString(),
                                    type = "text",
                                    text = new { body = m.Text },
                                }).ToArray(),
                            },
                        },
                    },
                },
            },
        }, Json);
    }

    /// <summary>Rebuilds a delivery around one stored message, for redelivering it.</summary>
    public static string WrapWhatsAppMessage(string storedPayload)
    {
        var stored = JsonNode.Parse(storedPayload)!;
        var message = stored["message"]!.DeepClone();
        return new JsonObject
        {
            ["object"] = "whatsapp_business_account",
            ["entry"] = new JsonArray(new JsonObject
            {
                ["id"] = WhatsAppBusinessAccountId,
                ["changes"] = new JsonArray(new JsonObject
                {
                    ["field"] = "messages",
                    ["value"] = new JsonObject
                    {
                        ["messaging_product"] = "whatsapp",
                        ["metadata"] = new JsonObject
                        {
                            ["display_phone_number"] = WhatsAppDisplayNumber,
                            ["phone_number_id"] = stored["phoneNumberId"]?.DeepClone(),
                        },
                        ["contacts"] = new JsonArray(new JsonObject
                        {
                            ["profile"] = new JsonObject { ["name"] = stored["contactName"]?.DeepClone() },
                            ["wa_id"] = message["from"]!.DeepClone(),
                        }),
                        ["messages"] = new JsonArray(message),
                    },
                }),
            }),
        }.ToJsonString(Json);
    }

    public static string FormSubmission(DateTimeOffset now)
    {
        var lead = Pick(Leads);
        return JsonSerializer.Serialize(new
        {
            submissionId = NewId("sub", 20),
            form = "contact",
            submittedAt = now.ToString("O"),
            fields = new { name = lead.Name, email = lead.Email, company = lead.Company, message = lead.Message },
        }, Json);
    }

    public static string NewId(string prefix, int length = 24) =>
        $"{prefix}_{new string(Random.Shared.GetItems(Alphanumeric.AsSpan(), length))}";

    private static string NewWamid()
    {
        var bytes = new byte[30];
        Random.Shared.NextBytes(bytes);
        return "wamid." + Convert.ToBase64String(bytes);
    }

    private static T Pick<T>(T[] items) => items[Random.Shared.Next(items.Length)];
}
