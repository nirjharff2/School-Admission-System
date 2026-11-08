using AdmissionSystem.Api.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AdmissionSystem.Api.Infrastructure;

public static class DatabaseMigrator
{
    // Arbitrary constant shared by every backend instance ("ADMS" in ASCII).
    private const long AdvisoryLockKey = 0x41444D53;

    private const string MaintenanceDatabase = "postgres";

    /// <summary>
    /// Applies pending migrations while holding a PostgreSQL advisory lock. EF Core 8 does not lock the database while
    /// migrating, so two backend instances starting together on a fresh database race on creating the migration history
    /// table and one of them crashes at startup. With the lock the
    /// second instance waits, then finds nothing left to apply.
    /// </summary>
    public static async Task MigrateAsync(AdmissionDbContext db, CancellationToken ct = default)
    {
        var connectionString = db.Database.GetConnectionString()
            ?? throw new InvalidOperationException("The database context has no connection string.");

        // A session-level advisory lock lives on this one connection until it is released below. The connection is not
        // pooled: it must not take a slot from the application's pool (migration needs its own connection from it), and
        // closing it ends the session, which releases the lock even if this process dies mid-migration.
        await using var lockConnection = await OpenLockConnectionAsync(connectionString, ct);
        await ExecuteAsync(lockConnection, "SELECT pg_advisory_lock(@key)", ct);
        try
        {
            await db.Database.MigrateAsync(ct);
        }
        finally
        {
            await ExecuteAsync(lockConnection, "SELECT pg_advisory_unlock(@key)", CancellationToken.None);
        }
    }

    /// <summary>
    /// Advisory locks are per database, and the application's own database may not exist yet (EF creates it while
    /// migrating, as before). So the lock is taken in the always-present maintenance database, the same one for every
    /// instance; if that is not reachable with these credentials, in the application's database.
    /// </summary>
    private static async Task<NpgsqlConnection> OpenLockConnectionAsync(string connectionString, CancellationToken ct)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false };

        builder.Database = MaintenanceDatabase;
        var maintenance = new NpgsqlConnection(builder.ConnectionString);
        try
        {
            await maintenance.OpenAsync(ct);
            return maintenance;
        }
        catch (PostgresException)
        {
            await maintenance.DisposeAsync();
        }

        builder = new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false };
        var own = new NpgsqlConnection(builder.ConnectionString);
        await own.OpenAsync(ct);
        return own;
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("key", AdvisoryLockKey);
        await command.ExecuteNonQueryAsync(ct);
    }
}
