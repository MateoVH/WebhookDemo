using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using WebhookDemo.Data;
using WebhookDemo.Inbox;
using WebhookDemo.Integrations.Simulated;

namespace WebhookDemo.Tests.Infrastructure;

/// <summary>
/// The real app, in memory, on its own throwaway SQLite file. Background workers are off: each test decides which
/// worker runs and when, and the clock only moves when the test moves it.
/// </summary>
public sealed class WebhookApp : WebApplicationFactory<Program>
{
    public static readonly DateTimeOffset Start = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);

    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"webhookdemo-tests-{Guid.NewGuid():N}.db");
    private readonly IChaosMonkey _chaos;
    private readonly IReadOnlyDictionary<string, string> _settings;
    private readonly Lazy<HttpClient> _http; // thread-safe: the first requests may well arrive in parallel

    public WebhookApp(IChaosMonkey? chaos = null, IReadOnlyDictionary<string, string>? settings = null)
    {
        _chaos = chaos ?? Chaos;
        _settings = settings ?? new Dictionary<string, string>();
        _http = new Lazy<HttpClient>(() => CreateClient());
    }

    public FakeTimeProvider Clock { get; } = new(Start);

    /// <summary>Rules added here fire inside the processor (unless the test passed its own chaos monkey).</summary>
    public ScriptedChaosMonkey Chaos { get; } = new();

    public ProcessingOptions Processing => Services.GetRequiredService<IOptions<ProcessingOptions>>().Value;
    public InboxProcessor Processor => Services.GetRequiredService<InboxProcessor>();
    public HttpClient Http => _http.Value;

    private string ConnectionString => $"Data Source={_databasePath}";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Webhooks", ConnectionString);
        builder.UseSetting("Processing:Workers", "0");
        builder.UseSetting("Processing:RetryJitter", "0");
        builder.UseSetting("Webhooks:Stripe:SigningSecret", TestSecrets.Stripe);
        builder.UseSetting("Webhooks:WhatsApp:AppSecret", TestSecrets.WhatsApp);
        builder.UseSetting("Webhooks:WhatsApp:VerifyToken", TestSecrets.WhatsAppVerifyToken);
        builder.UseSetting("Webhooks:Forms:SigningSecret", TestSecrets.Forms);
        foreach (var (key, value) in _settings) builder.UseSetting(key, value);

        builder.ConfigureLogging(logging => logging.ClearProviders());
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Clock);
            services.RemoveAll<ScriptedChaosMonkey>();
            services.AddSingleton(Chaos);
            services.RemoveAll<IChaosMonkey>();
            services.AddSingleton(_chaos);
            services.ConfigureDbContext<WebhookDbContext>(options => options.AddInterceptors(new FastSqlite()));
        });
    }

    // ── Delivering webhooks ─────────────────────────────────────────────────────

    public Task<HttpResponseMessage> SendStripeAsync(string json, DateTimeOffset? signedAt = null, string secret = TestSecrets.Stripe) =>
        PostAsync("/webhooks/stripe", json, ("Stripe-Signature", Sign.StripeHeader(json, secret, (signedAt ?? Clock.GetUtcNow()).ToUnixTimeSeconds())));

    public Task<HttpResponseMessage> SendWhatsAppAsync(string json, string secret = TestSecrets.WhatsApp) =>
        PostAsync("/webhooks/whatsapp", json, ("X-Hub-Signature-256", Sign.Sha256Header(json, secret)));

    public Task<HttpResponseMessage> SendFormAsync(string json, string secret = TestSecrets.Forms) =>
        PostAsync("/webhooks/forms", json, ("X-Signature-256", Sign.Sha256Header(json, secret)));

    public async Task<HttpResponseMessage> PostAsync(string path, string body, params (string Name, string Value)[] headers)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        foreach (var (name, value) in headers) request.Headers.TryAddWithoutValidation(name, value);
        return await Http.SendAsync(request);
    }

    // ── Running workers ─────────────────────────────────────────────────────────

    /// <summary>One worker processes whatever is due until it runs out of work (Idle) or dies (Crashed).</summary>
    public async Task<ProcessOutcome> RunWorkerAsync(string worker)
    {
        while (true)
        {
            var outcome = await Processor.ProcessNextAsync(worker, CancellationToken.None);
            if (outcome != ProcessOutcome.Processed) return outcome;
        }
    }

    /// <summary>
    /// Runs workers, and moves time forward, until every event is finished: completed, ignored or dead-lettered.
    /// A crashed worker comes back as a new process. Between rounds the clock jumps to the next lease expiry or retry.
    /// </summary>
    public async Task SettleAsync(int workers = 1, int maxRounds = 500)
    {
        for (var round = 1; round <= maxRounds; round++)
        {
            await Task.WhenAll(Enumerable.Range(1, workers).Select(w => KeepWorkingAsync($"worker-{w}.{round}")));

            var wakeUp = await NextWakeUpAsync();
            if (wakeUp is null) return;

            var now = Clock.GetUtcNow().UtcDateTime;
            if (wakeUp >= now) Clock.Advance(wakeUp.Value - now + TimeSpan.FromMilliseconds(1));
        }
        throw new TimeoutException($"The inbox didn't settle after {maxRounds} rounds");
    }

    private async Task KeepWorkingAsync(string name)
    {
        for (var life = 1; ; life++)
        {
            if (await RunWorkerAsync($"{name}#{life}") == ProcessOutcome.Idle) return;
        }
    }

    private Task<DateTime?> NextWakeUpAsync() => QueryAsync(async db =>
    {
        var retry = await db.InboxEvents.Where(e => e.Status == InboxStatus.Pending).MinAsync(e => (DateTime?)e.NextAttemptAt);
        var lease = await db.InboxEvents.Where(e => e.Status == InboxStatus.Processing).MinAsync(e => e.LockedUntil);
        return retry is null ? lease : lease is null ? retry : retry < lease ? retry : lease;
    });

    // ── Looking at the database ─────────────────────────────────────────────────

    public async Task<T> QueryAsync<T>(Func<WebhookDbContext, Task<T>> query)
    {
        await using var scope = Services.CreateAsyncScope();
        return await query(scope.ServiceProvider.GetRequiredService<WebhookDbContext>());
    }

    public Task<int> CountAsync<TEntity>() where TEntity : class =>
        QueryAsync(db => db.Set<TEntity>().CountAsync());

    public Task<InboxEvent> InboxAsync(string externalId) =>
        QueryAsync(db => db.InboxEvents.AsNoTracking().SingleAsync(e => e.ExternalId == externalId));

    public Task<List<StepCheckpoint>> CheckpointsAsync(string externalId) =>
        QueryAsync(db => db.Checkpoints.AsNoTracking()
            .Where(c => db.InboxEvents.Any(e => e.Id == c.InboxEventId && e.ExternalId == externalId))
            .OrderBy(c => c.Id)
            .ToListAsync());

    public Task<List<TimelineEntry>> TimelineAsync(string externalId) =>
        QueryAsync(db => db.Timeline.AsNoTracking()
            .Where(t => db.InboxEvents.Any(e => e.Id == t.InboxEventId && e.ExternalId == externalId))
            .OrderBy(t => t.Id)
            .ToListAsync());

    public Task<List<ProviderCall>> ProviderCallsAsync(string provider) =>
        QueryAsync(db => db.ProviderCalls.AsNoTracking().Where(c => c.Provider == provider).OrderBy(c => c.Id).ToListAsync());

    /// <summary>Emails the provider actually sent (requests it recognized as repeats don't count).</summary>
    public Task<int> EmailsSentAsync() =>
        QueryAsync(db => db.ProviderCalls.CountAsync(c => c.Provider == "email" && !c.Deduplicated));

    public override async ValueTask DisposeAsync()
    {
        if (_http.IsValueCreated) _http.Value.Dispose();
        await base.DisposeAsync();
        SqliteConnection.ClearPool(new SqliteConnection(ConnectionString));
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try
            {
                File.Delete(_databasePath + suffix);
            }
            catch (IOException)
            {
                // A leftover temp file is harmless.
            }
        }
    }
}
