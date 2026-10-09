using System.Net;
using WebhookDemo.Inbox;
using WebhookDemo.Tests.Infrastructure;

namespace WebhookDemo.Tests;

/// <summary>Only genuine deliveries get in. Rejected requests never reach the inbox.</summary>
public sealed class SignatureTests
{
    [Fact]
    public async Task Valid_Stripe_signature_is_accepted()
    {
        await using var app = new WebhookApp();

        var response = await app.SendStripeAsync(StripeEvents.PaymentSucceeded().Json);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Stripe_signature_is_compatible_with_the_official_Stripe_library()
    {
        await using var app = new WebhookApp();
        var json = StripeEvents.PaymentSucceeded().Json;
        var header = Stripe.EventUtility.GenerateSignatureHeader(json, TestSecrets.Stripe, app.Clock.GetUtcNow().ToUnixTimeSeconds());

        var response = await app.PostAsync("/webhooks/stripe", json, ("Stripe-Signature", header));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Request_without_a_Stripe_signature_is_rejected()
    {
        await using var app = new WebhookApp();

        var response = await app.PostAsync("/webhooks/stripe", StripeEvents.PaymentSucceeded().Json);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, await app.CountAsync<InboxEvent>());
    }

    [Fact]
    public async Task Stripe_signature_made_with_another_secret_is_rejected()
    {
        await using var app = new WebhookApp();

        var response = await app.SendStripeAsync(StripeEvents.PaymentSucceeded().Json, secret: "whsec_someone_else");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Body_changed_after_signing_is_rejected()
    {
        await using var app = new WebhookApp();
        var original = StripeEvents.PaymentSucceeded(amount: 4900).Json;
        var header = Sign.StripeHeader(original, TestSecrets.Stripe, app.Clock.GetUtcNow().ToUnixTimeSeconds());
        var tampered = original.Replace("4900", "1");

        var response = await app.PostAsync("/webhooks/stripe", tampered, ("Stripe-Signature", header));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Captured_request_replayed_later_is_rejected()
    {
        // Correctly signed, but six minutes old: outside Stripe's five-minute tolerance.
        await using var app = new WebhookApp();

        var response = await app.SendStripeAsync(StripeEvents.PaymentSucceeded().Json, signedAt: app.Clock.GetUtcNow().AddMinutes(-6));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, await app.CountAsync<InboxEvent>());
    }

    [Fact]
    public async Task During_a_secret_rotation_any_matching_signature_is_accepted()
    {
        // While a secret is being rolled, Stripe signs with the old and the new one and sends both.
        await using var app = new WebhookApp();
        var json = StripeEvents.PaymentSucceeded().Json;
        var timestamp = app.Clock.GetUtcNow().ToUnixTimeSeconds();
        var oldSecret = Sign.StripeHeader(json, "whsec_old_secret", timestamp).Split(',')[1];
        var currentSecret = Sign.StripeHeader(json, TestSecrets.Stripe, timestamp).Split(',')[1];

        var response = await app.PostAsync("/webhooks/stripe", json, ("Stripe-Signature", $"t={timestamp},{oldSecret},{currentSecret}"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task WhatsApp_subscription_handshake_echoes_the_challenge_only_with_the_right_token()
    {
        await using var app = new WebhookApp();

        var valid = await app.Http.GetAsync($"/webhooks/whatsapp?hub.mode=subscribe&hub.verify_token={TestSecrets.WhatsAppVerifyToken}&hub.challenge=1158201444");
        var invalid = await app.Http.GetAsync("/webhooks/whatsapp?hub.mode=subscribe&hub.verify_token=guess&hub.challenge=1158201444");

        Assert.Equal(HttpStatusCode.OK, valid.StatusCode);
        Assert.Equal("1158201444", await valid.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Forbidden, invalid.StatusCode);
    }

    [Fact]
    public async Task WhatsApp_delivery_with_a_bad_signature_is_rejected()
    {
        await using var app = new WebhookApp();
        var batch = WhatsAppPayloads.Batch((WhatsAppPayloads.NewMessageId(), "573001112233", "Hola"));

        var response = await app.SendWhatsAppAsync(batch, secret: "not-the-app-secret");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Form_submission_with_a_bad_signature_is_rejected()
    {
        await using var app = new WebhookApp();

        var response = await app.SendFormAsync(FormPayloads.Submission("sub_1"), secret: "not-the-form-secret");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Unknown_source_is_not_found()
    {
        await using var app = new WebhookApp();

        var response = await app.PostAsync("/webhooks/paypal", "{}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Oversized_body_is_refused_before_it_is_read()
    {
        await using var app = new WebhookApp();

        var response = await app.PostAsync("/webhooks/stripe", new string('x', 300 * 1024));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task Correctly_signed_body_that_is_not_a_Stripe_event_is_a_bad_request()
    {
        await using var app = new WebhookApp();

        var response = await app.SendStripeAsync("""{"hello":"world"}""");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, await app.CountAsync<InboxEvent>());
    }
}
