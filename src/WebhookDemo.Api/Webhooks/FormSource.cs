using System.Text;
using Microsoft.Extensions.Options;
using WebhookDemo.Shared;

namespace WebhookDemo.Webhooks;

/// <summary>
/// Web forms: your own site, or no-code tools such as Typeform, Tally, Zapier or Make. X-Signature-256 is
/// "sha256=" + hex(HMAC-SHA256(secret, body)), and the submissionId inside the body is the deduplication key.
/// The key has to live inside the signed body: in a header, anyone holding one valid request could resend it
/// with a fresh key and slip past deduplication.
/// </summary>
public sealed class FormSource(IOptions<WebhookOptions> options) : IWebhookSource
{
    public string Name => "forms";

    public SignatureCheck Verify(IHeaderDictionary headers, byte[] body) =>
        Hmac.VerifyPrefixed(headers["X-Signature-256"].ToString(), options.Value.Forms.SigningSecret, body, "Webhooks:Forms:SigningSecret");

    public IReadOnlyList<IncomingEvent> Parse(byte[] body) => WebhookJson.Read<IReadOnlyList<IncomingEvent>>(body, root =>
    {
        var fields = root.GetProperty("fields");
        var name = fields.OptionalString("name");
        var email = fields.OptionalString("email");

        return
        [
            new IncomingEvent(
                ExternalId: root.RequiredString("submissionId"),
                EventType: $"{root.OptionalString("form") ?? "form"}.submitted",
                Payload: Encoding.UTF8.GetString(body),
                Summary: (name, email) switch
                {
                    (null, _) => email,
                    (_, null) => name,
                    _ => $"{name} <{email}>",
                }),
        ];
    });
}
