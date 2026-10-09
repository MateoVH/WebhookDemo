using System.Text.Json;
using WebhookDemo.Inbox;
using WebhookDemo.Shared;

namespace WebhookDemo.Pipelines.WhatsApp;

/// <summary>One message, as <see cref="Webhooks.WhatsAppSource"/> stored it after splitting the batch.</summary>
public sealed record IncomingWhatsAppMessage(string MessageId, string From, string? ContactName, string Type, string? Text)
{
    public static IncomingWhatsAppMessage FromPayload(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var message = root.GetProperty("message");
            return new IncomingWhatsAppMessage(
                MessageId: message.RequiredString("id"),
                From: message.RequiredString("from"),
                ContactName: root.OptionalString("contactName"),
                Type: message.RequiredString("type"),
                Text: message.OptionalObject("text")?.OptionalString("body"));
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new NonRetryableException($"Malformed WhatsApp message: {ex.Message}", ex);
        }
    }
}
