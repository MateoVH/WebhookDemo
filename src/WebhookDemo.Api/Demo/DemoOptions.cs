namespace WebhookDemo.Demo;

public sealed class DemoOptions
{
    public const string Section = "Demo";

    /// <summary>Scenario buttons and fault injection. Never turn this on for real traffic.</summary>
    public bool Enabled { get; set; }
}
