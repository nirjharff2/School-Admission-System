using AdmissionSystem.Api.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AdmissionSystem.Api.Infrastructure;

/// <summary>The database-level lock that serializes every operation able to change how many seats of a class are occupied.</summary>
public static class ClassLock
{
    /// <summary>
    /// Locks the class row (<c>SELECT ... FOR UPDATE</c>) for the rest of the current transaction and returns its
    /// <c>total_seats</c>, or null if the class does not exist. Blocks, inside PostgreSQL, while another transaction holds the
    /// lock, from any thread or any backend process. Must be called inside an open transaction.
    /// </summary>
    public static async Task<int?> AcquireAsync(AdmissionDbContext db, string classId, CancellationToken ct)
    {
        var rows = await db.Database
            .SqlQuery<int>($"SELECT total_seats AS \"Value\" FROM classes WHERE id = {classId} FOR UPDATE")
            .ToListAsync(ct);
        return rows.Count == 0 ? null : rows[0];
    }
}

/// <summary>
/// Limits how many requests per process may be inside a class-lock transaction at once, so the rest wait in memory instead
/// of each pinning a pooled connection while queueing on the row lock. Without it a burst of a few thousand applications
/// exhausts the connection pool and fails requests with 500. This is a throughput guard only: correctness comes from the
/// database lock, which is also what protects against other processes.
/// </summary>
public sealed class ClassWriteGate(int limit)
{
    private readonly SemaphoreSlim _slots = new(limit, limit);

    public int Limit { get; } = limit;

    /// <summary>Half the connection pool (15 of the default 30), leaving the rest for reads, confirms, the sweeper and migrations.</summary>
    public static ClassWriteGate From(IConfiguration configuration)
    {
        var poolSize = new NpgsqlConnectionStringBuilder(DatabaseConnection.FromConfiguration(configuration)).MaxPoolSize;
        return new ClassWriteGate(Math.Max(1, poolSize / 2));
    }

    /// <summary>Waits for a free slot; dispose the result to release it. Honours cancellation while waiting.</summary>
    public async Task<IDisposable> EnterAsync(CancellationToken ct)
    {
        await _slots.WaitAsync(ct);
        return new Slot(_slots);
    }

    private sealed class Slot(SemaphoreSlim slots) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                slots.Release();
            }
        }
    }
}
