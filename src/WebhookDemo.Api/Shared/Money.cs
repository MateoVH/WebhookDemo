using System.Globalization;

namespace WebhookDemo.Shared;

/// <summary>Formats amounts the way Stripe sends them: in the currency's minor unit (cents), except zero-decimal currencies.</summary>
public static class Money
{
    private static readonly HashSet<string> ZeroDecimal =
        ["bif", "clp", "djf", "gnf", "jpy", "kmf", "krw", "mga", "pyg", "rwf", "ugx", "vnd", "vuv", "xaf", "xof", "xpf"];

    public static string Format(long amountMinor, string currency)
    {
        var code = currency.ToLowerInvariant();
        var amount = ZeroDecimal.Contains(code)
            ? amountMinor.ToString("N0", CultureInfo.InvariantCulture)
            : (amountMinor / 100m).ToString("N2", CultureInfo.InvariantCulture);
        return $"{amount} {code.ToUpperInvariant()}";
    }
}
