using System.Text.Json;
using WebhookDemo.Inbox;
using WebhookDemo.Shared;

namespace WebhookDemo.Pipelines.Leads;

public sealed record FormSubmission(string SubmissionId, string Email, string? Name, string? Company, string? Message)
{
    public static FormSubmission FromPayload(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var fields = root.GetProperty("fields");
            return new FormSubmission(
                SubmissionId: root.RequiredString("submissionId"),
                Email: fields.RequiredString("email"),
                Name: fields.OptionalString("name"),
                Company: fields.OptionalString("company"),
                Message: fields.OptionalString("message"));
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new NonRetryableException($"Malformed form submission: {ex.Message}", ex);
        }
    }
}
