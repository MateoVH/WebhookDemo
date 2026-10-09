using WebhookDemo.Inbox;
using WebhookDemo.Integrations;

namespace WebhookDemo.Pipelines.WhatsApp;

/// <summary>
/// Incoming WhatsApp message → save it → upsert the CRM contact (by phone) → send an automatic reply.
///
/// The reply is the honest exception to exactly-once: the WhatsApp Cloud API has no idempotency key, so if the
/// worker dies after sending it and before saving the checkpoint, the customer gets the reply twice. This step is
/// at-least-once, and it's acceptable only because a repeated "we got your message" is harmless.
/// </summary>
public sealed class WhatsAppMessagePipeline(ICrmClient crm, IWhatsAppClient whatsapp, TimeProvider clock) : IWebhookPipeline
{
    public bool CanHandle(string source, string eventType) =>
        source == "whatsapp" && eventType.StartsWith("message.", StringComparison.Ordinal);

    public IReadOnlyList<PipelineStep> Plan(InboxEvent evt)
    {
        var message = IncomingWhatsAppMessage.FromPayload(evt.Payload);

        return
        [
            PipelineStep.Local("save-message", (db, _, _) =>
            {
                db.ChatMessages.Add(new ChatMessage
                {
                    MessageId = message.MessageId,
                    From = message.From,
                    ContactName = message.ContactName,
                    Type = message.Type,
                    Text = message.Text,
                    InboxEventId = evt.Id,
                    ReceivedAt = clock.GetUtcNow().UtcDateTime,
                });
                return Task.FromResult<string?>(message.Type);
            }),

            PipelineStep.External("upsert-contact", async (_, ct) =>
            {
                var contact = await crm.UpsertContactAsync($"+{message.From}", message.ContactName, email: null, phone: $"+{message.From}", ct);
                return contact.Deduplicated ? $"{contact.Id} (updated)" : $"{contact.Id} (created)";
            }),

            PipelineStep.External("auto-reply", async (_, ct) =>
            {
                var reply = await whatsapp.SendTextAsync(
                    message.From,
                    $"Hi {message.ContactName ?? "there"}! We got your message and will get back to you shortly.",
                    ct);
                return reply.Id;
            }),
        ];
    }
}
