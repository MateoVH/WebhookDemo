using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WebhookDemo.Dashboard;
using WebhookDemo.Data;
using WebhookDemo.Demo;
using WebhookDemo.Inbox;
using WebhookDemo.Integrations;
using WebhookDemo.Integrations.Simulated;
using WebhookDemo.Pipelines.Leads;
using WebhookDemo.Pipelines.Payments;
using WebhookDemo.Pipelines.WhatsApp;
using WebhookDemo.Webhooks;

var builder = WebApplication.CreateBuilder(args);
var services = builder.Services;

services.AddSingleton(TimeProvider.System);
services.AddDbContext<WebhookDbContext>((sp, options) =>
    options.UseSqlite(sp.GetRequiredService<IConfiguration>().GetConnectionString("Webhooks")));
services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
services.AddHealthChecks();

// 1. Receive: verify the signature, store each event exactly once, answer 200 right away.
services.AddOptions<WebhookOptions>().BindConfiguration(WebhookOptions.Section);
services.AddSingleton<IWebhookSource, StripeSource>();
services.AddSingleton<IWebhookSource, WhatsAppSource>();
services.AddSingleton<IWebhookSource, FormSource>();
services.AddScoped<InboxWriter>();
services.AddScoped<WebhookIngestor>();
services.AddSingleton<WorkSignal>();

// 2. Process: workers lease events and run their pipeline step by step, checkpointing each step.
services.AddOptions<ProcessingOptions>().BindConfiguration(ProcessingOptions.Section);
services.AddScoped<InboxQueue>();
services.AddSingleton<InboxProcessor>();
services.AddHostedService<InboxWorker>();

// 3. The business flows.
services.AddScoped<IWebhookPipeline, StripePaymentPipeline>();
services.AddScoped<IWebhookPipeline, FormLeadPipeline>();
services.AddScoped<IWebhookPipeline, WhatsAppMessagePipeline>();

// 4. Outbound providers, simulated so the demo runs offline. Swap in real email, CRM and WhatsApp clients here.
services.AddSingleton<SimulatedProviders>();
services.AddSingleton<IEmailSender>(sp => sp.GetRequiredService<SimulatedProviders>());
services.AddSingleton<ICrmClient>(sp => sp.GetRequiredService<SimulatedProviders>());
services.AddSingleton<IWhatsAppClient>(sp => sp.GetRequiredService<SimulatedProviders>());

// 5. Demo mode: scenario buttons and fault injection. Off unless Demo:Enabled is true.
services.AddOptions<DemoOptions>().BindConfiguration(DemoOptions.Section);
services.AddSingleton<ScriptedChaosMonkey>();
services.AddSingleton<IChaosMonkey>(sp => sp.GetRequiredService<IOptions<DemoOptions>>().Value.Enabled
    ? sp.GetRequiredService<ScriptedChaosMonkey>()
    : new NoChaos());
services.AddSingleton<DemoScenarios>();

var app = builder.Build();

app.EnsureDatabase();
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapWebhookEndpoints();
app.MapDashboardEndpoints();
if (app.Services.GetRequiredService<IOptions<DemoOptions>>().Value.Enabled)
{
    app.MapDemoEndpoints();
}
app.MapHealthChecks("/health");

app.Run();
