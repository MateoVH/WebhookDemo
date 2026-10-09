using System.Text.Json;

namespace WebhookDemo.Webhooks;

internal static class WebhookJson
{
    /// <summary>Parses the body and reads it, turning any shape mismatch into a <see cref="MalformedWebhookException"/>.</summary>
    public static T Read<T>(byte[] body, Func<JsonElement, T> read)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return read(document.RootElement);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new MalformedWebhookException(ex.Message, ex);
        }
    }
}
