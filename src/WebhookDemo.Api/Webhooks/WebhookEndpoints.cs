using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace WebhookDemo.Webhooks;

public static class WebhookEndpoints
{
    public static void MapWebhookEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/webhooks/{source}", ReceiveAsync);
        app.MapGet("/webhooks/whatsapp", VerifyWhatsAppSubscription);
    }

    private static async Task<IResult> ReceiveAsync(
        string source, HttpRequest request, WebhookIngestor ingestor, IOptions<WebhookOptions> options, CancellationToken ct)
    {
        var body = await ReadBodyAsync(request, options.Value.MaxBodyBytes, ct);
        if (body is null) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);

        var outcome = await ingestor.IngestAsync(source, request.Headers, body, ct);
        return outcome.Status switch
        {
            // 200 for duplicates too: we already have the event, so the provider has to stop retrying it.
            IngestStatus.Accepted => Results.Ok(new { received = outcome.Events.Count, events = outcome.Events }),
            IngestStatus.UnknownSource => Results.NotFound(),
            IngestStatus.InvalidSignature => Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Invalid signature"),
            _ => Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Malformed payload", detail: outcome.Error),
        };
    }

    /// <summary>Meta calls this once, when the webhook URL is registered, and expects the challenge echoed back.</summary>
    private static IResult VerifyWhatsAppSubscription(HttpRequest request, IOptions<WebhookOptions> options)
    {
        var expected = options.Value.WhatsApp.VerifyToken;
        var token = request.Query["hub.verify_token"].ToString();
        var valid = request.Query["hub.mode"] == "subscribe"
            && expected.Length > 0
            && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(token), Encoding.UTF8.GetBytes(expected));

        return valid
            ? Results.Text(request.Query["hub.challenge"].ToString())
            : Results.StatusCode(StatusCodes.Status403Forbidden);
    }

    /// <summary>Reads the raw body, which signature checks need byte for byte, refusing anything over the limit.</summary>
    private static async Task<byte[]?> ReadBodyAsync(HttpRequest request, int maxBytes, CancellationToken ct)
    {
        if (request.ContentLength > maxBytes) return null;

        using var body = new MemoryStream();
        var buffer = new byte[16 * 1024];
        int read;
        while ((read = await request.Body.ReadAsync(buffer, ct)) > 0)
        {
            if (body.Length + read > maxBytes) return null;
            body.Write(buffer, 0, read);
        }
        return body.ToArray();
    }
}
