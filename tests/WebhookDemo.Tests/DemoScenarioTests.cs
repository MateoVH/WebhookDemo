using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WebhookDemo.Demo;
using WebhookDemo.Inbox;
using WebhookDemo.Tests.Infrastructure;

namespace WebhookDemo.Tests;

/// <summary>The dashboard's buttons go through the real signing and ingestion code, so they get a test too.</summary>
public sealed class DemoScenarioTests
{
    [Fact]
    public async Task Every_demo_scenario_ends_in_the_state_it_promises()
    {
        await using var app = new WebhookApp(settings: new Dictionary<string, string> { ["Demo:Enabled"] = "true" });

        foreach (var scenario in DemoScenarios.Names)
        {
            var response = await app.PostAsync($"/demo/scenarios/{scenario}", "");
            Assert.True(response.IsSuccessStatusCode, $"{scenario}: {response.StatusCode}");
        }
        await app.SettleAsync(workers: 2);

        var statuses = await app.QueryAsync(db => db.InboxEvents.Select(e => e.Status).ToListAsync());
        Assert.Equal(1, statuses.Count(s => s == InboxStatus.DeadLettered)); // the "poison" one, waiting for a replay
        Assert.All(statuses.Where(s => s != InboxStatus.DeadLettered), s => Assert.Equal(InboxStatus.Completed, s));

        var overview = await app.Http.GetFromJsonAsync<JsonElement>("/api/overview");
        var stats = overview.GetProperty("stats");
        Assert.Equal(0, stats.GetProperty("invoiceGaps").GetInt32());
        Assert.Equal(3, stats.GetProperty("crashes").GetInt32());
        Assert.Equal(3, stats.GetProperty("recoveries").GetInt32());
        Assert.True(stats.GetProperty("duplicates").GetInt32() >= 6); // 2 from "duplicate", 4 from "burst"
    }

    [Fact]
    public async Task Redelivering_a_stored_event_is_absorbed_as_a_duplicate()
    {
        await using var app = new WebhookApp(settings: new Dictionary<string, string> { ["Demo:Enabled"] = "true" });
        await app.PostAsync("/demo/scenarios/whatsapp", "");
        var inboxId = await app.QueryAsync(db => db.InboxEvents.Select(e => e.Id).FirstAsync());

        var response = await app.PostAsync($"/demo/events/{inboxId}/redeliver", "");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("duplicate", body.GetProperty("events")[0].GetProperty("status").GetString());
    }

    [Fact]
    public async Task Demo_endpoints_do_not_exist_unless_demo_mode_is_on()
    {
        await using var app = new WebhookApp();

        var response = await app.PostAsync("/demo/scenarios/payment", "");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
