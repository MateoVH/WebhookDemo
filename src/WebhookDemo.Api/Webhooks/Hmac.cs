using System.Security.Cryptography;
using System.Text;

namespace WebhookDemo.Webhooks;

internal static class Hmac
{
    public static byte[] Sha256(string secret, ReadOnlySpan<byte> data) =>
        HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), data);

    /// <summary>Constant-time comparison: response timing must not reveal how much of a forged signature was right.</summary>
    public static bool MatchesHex(byte[] expected, string hex)
    {
        if (hex.Length != expected.Length * 2) return false;
        try
        {
            return CryptographicOperations.FixedTimeEquals(expected, Convert.FromHexString(hex));
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>Verifies "sha256=&lt;hex HMAC of the body&gt;" headers, the scheme used by Meta, GitHub and many others.</summary>
    public static SignatureCheck VerifyPrefixed(string header, string secret, byte[] body, string secretSetting)
    {
        const string prefix = "sha256=";
        if (string.IsNullOrEmpty(secret)) return SignatureCheck.Invalid($"{secretSetting} is not configured");
        if (!header.StartsWith(prefix, StringComparison.Ordinal)) return SignatureCheck.Invalid("Missing or malformed signature header");

        return MatchesHex(Sha256(secret, body), header[prefix.Length..])
            ? SignatureCheck.Valid
            : SignatureCheck.Invalid("Signature mismatch");
    }
}
