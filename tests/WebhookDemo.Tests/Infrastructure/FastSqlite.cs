using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace WebhookDemo.Tests.Infrastructure;

/// <summary>
/// Tests simulate process crashes, never power cuts, so they can skip the disk flush on every commit.
/// What a crashed process leaves behind is the same either way; the suite just runs much faster.
/// </summary>
public sealed class FastSqlite : DbConnectionInterceptor
{
    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData) => Apply(connection);

    public override Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        Apply(connection);
        return Task.CompletedTask;
    }

    private static void Apply(DbConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA synchronous = OFF;";
        command.ExecuteNonQuery();
    }
}
