using System.Security.Cryptography;
using System.Text;

namespace WebhookDemo.Tests.Infrastructure;

/// <summary>Signs test deliveries the way each provider does. Written independently of the app's verification code.</summary>
public static class Sign
{
    public static string StripeHeader(string payload, string secret, long timestamp) =>
        $"t={timestamp},v1={HexHmac(secret, $"{timestamp}.{payload}")}";

    /// <summary>"sha256=&lt;hex&gt;", as Meta (X-Hub-Signature-256) and the forms endpoint (X-Signature-256) expect.</summary>
    public static string Sha256Header(string payload, string secret) =>
        $"sha256={HexHmac(secret, payload)}";

    private static string HexHmac(string secret, string data) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(data)));
}
