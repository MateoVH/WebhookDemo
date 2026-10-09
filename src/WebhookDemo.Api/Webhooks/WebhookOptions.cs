namespace WebhookDemo.Webhooks;

public sealed class WebhookOptions
{
    public const string Section = "Webhooks";

    public int MaxBodyBytes { get; set; } = 256 * 1024;
    public StripeSettings Stripe { get; set; } = new();
    public WhatsAppSettings WhatsApp { get; set; } = new();
    public FormSettings Forms { get; set; } = new();

    public sealed class StripeSettings
    {
        /// <summary>The endpoint's signing secret (whsec_…), from the Stripe dashboard or from `stripe listen`.</summary>
        public string SigningSecret { get; set; } = "";

        /// <summary>How old a signed timestamp may be. Stripe's own libraries default to 5 minutes.</summary>
        public TimeSpan Tolerance { get; set; } = TimeSpan.FromMinutes(5);
    }

    public sealed class WhatsAppSettings
    {
        /// <summary>The Meta app secret, used to sign every delivery.</summary>
        public string AppSecret { get; set; } = "";

        /// <summary>The token you type in the Meta dashboard when registering the webhook URL.</summary>
        public string VerifyToken { get; set; } = "";
    }

    public sealed class FormSettings
    {
        public string SigningSecret { get; set; } = "";
    }
}
