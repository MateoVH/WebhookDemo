using WebhookDemo.Inbox;
using WebhookDemo.Integrations;

namespace WebhookDemo.Pipelines.Leads;

/// <summary>Form submitted → save the lead → upsert the CRM contact (by email) → email the sales team.</summary>
public sealed class FormLeadPipeline(ICrmClient crm, IEmailSender email, TimeProvider clock) : IWebhookPipeline
{
    public bool CanHandle(string source, string eventType) => source == "forms";

    public IReadOnlyList<PipelineStep> Plan(InboxEvent evt)
    {
        var lead = FormSubmission.FromPayload(evt.Payload);

        return
        [
            PipelineStep.Local("save-lead", (db, _, _) =>
            {
                db.Leads.Add(new Lead
                {
                    SubmissionId = lead.SubmissionId,
                    Email = lead.Email,
                    Name = lead.Name,
                    Company = lead.Company,
                    Message = lead.Message,
                    InboxEventId = evt.Id,
                    ReceivedAt = clock.GetUtcNow().UtcDateTime,
                });
                return Task.FromResult<string?>(lead.Email);
            }),

            PipelineStep.External("upsert-contact", async (_, ct) =>
            {
                // Keyed by email: the same person writing in twice is still one contact.
                var contact = await crm.UpsertContactAsync(lead.Email.ToLowerInvariant(), lead.Name, lead.Email, phone: null, ct);
                return contact.Deduplicated ? $"{contact.Id} (updated)" : $"{contact.Id} (created)";
            }),

            PipelineStep.External("notify-sales", async (_, ct) =>
            {
                var sent = await email.SendAsync(
                    to: "sales@example.com",
                    subject: $"New lead: {lead.Name ?? lead.Email}{(lead.Company is null ? "" : $" ({lead.Company})")}",
                    body: lead.Message ?? "",
                    idempotencyKey: $"lead:{lead.SubmissionId}",
                    ct);
                return sent.Deduplicated ? $"{sent.Id} (already sent)" : sent.Id;
            }),
        ];
    }
}
