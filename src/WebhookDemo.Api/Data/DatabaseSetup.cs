using Microsoft.EntityFrameworkCore;

namespace WebhookDemo.Data;

public static class DatabaseSetup
{
    /// <summary>
    /// Creates the SQLite schema on first run. EnsureCreated keeps the demo zero-setup;
    /// a production service would use EF Core migrations.
    /// </summary>
    public static void EnsureDatabase(this WebApplication app)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WebhookDbContext>();
        db.Database.EnsureCreated();

        // WAL lets the dashboard read while the workers write. The setting sticks to the database file.
        db.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");
    }
}
