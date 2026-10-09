namespace WebhookDemo.Demo;

public static class DemoEndpoints
{
    public static void MapDemoEndpoints(this IEndpointRouteBuilder app)
    {
        var demo = app.MapGroup("/demo");

        demo.MapPost("/scenarios/{name}", async (string name, DemoScenarios scenarios, CancellationToken ct) =>
            await scenarios.RunAsync(name, ct) is { } events
                ? Results.Ok(new { scenario = name, events })
                : Results.NotFound(new { error = $"Unknown scenario '{name}'", scenarios = DemoScenarios.Names }));

        demo.MapPost("/events/{id:long}/redeliver", async (long id, DemoScenarios scenarios, CancellationToken ct) =>
            await scenarios.RedeliverAsync(id, ct) is { } events
                ? Results.Ok(new { events })
                : Results.NotFound());

        demo.MapPost("/reset", async (DemoScenarios scenarios, CancellationToken ct) =>
        {
            await scenarios.ResetAsync(ct);
            return Results.NoContent();
        });
    }
}
