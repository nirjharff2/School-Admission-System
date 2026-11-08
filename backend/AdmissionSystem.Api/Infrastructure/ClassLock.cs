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
/// Limits how many applications per process may be doing database work at once, so the rest wait in memory instead of
/// each pinning a pooled connection while queueing on the class row lock. Without it a burst of a few thousand
/// applications exhausts the connection pool and fails requests. A request needs a slot for its own class AND a slot from
/// the process-wide cap, taken in that order: a burst on one class fills only that class's few slots, waits there, and
/// never holds process-wide slots while it waits, so applications to other classes are not held up. This is a throughput
/// guard only: correctness comes from the database lock, which also protects against other processes.
/// </summary>
public sealed class ClassWriteGate(int totalLimit, int perClassLimit)
{
    private const int Stripes = 64;

    private readonly SemaphoreSlim _total = new(totalLimit, totalLimit);

    // A fixed set of per-class gates, picked by hash of the class id, instead of one per id seen: ids come from the URL,
    // so a dictionary keyed by them would grow without bound. Two classes sharing a stripe (1 in 64) only share a queue.
    private readonly SemaphoreSlim[] _perClass =
        Enumerable.Range(0, Stripes).Select(_ => new SemaphoreSlim(perClassLimit, perClassLimit)).ToArray();

    /// <summary>Which per-class gate a class id uses (classes in the same stripe share one queue).</summary>
    public static int StripeOf(string classId) => (int)((uint)classId.GetHashCode() % Stripes);

    /// <summary>
    /// Half the connection pool process-wide (15 of the default 30) and a fifth of that per class (3), leaving the rest of
    /// the pool for reads, confirms, the sweeper and migrations. The class row lock admits one transaction at a time
    /// anyway, so a few in flight per class keep it saturated.
    /// </summary>
    public static ClassWriteGate From(IConfiguration configuration)
    {
        var poolSize = new NpgsqlConnectionStringBuilder(DatabaseConnection.FromConfiguration(configuration)).MaxPoolSize;
        var total = Math.Max(1, poolSize / 2);
        return new ClassWriteGate(total, Math.Max(1, total / 5));
    }

    /// <summary>Waits for a slot for this class and one process-wide; dispose the result to release both. Honours cancellation.</summary>
    public async Task<IDisposable> EnterAsync(string classId, CancellationToken ct)
    {
        var perClass = _perClass[(uint)classId.GetHashCode() % Stripes];
        await perClass.WaitAsync(ct);
        try
        {
            await _total.WaitAsync(ct);
        }
        catch
        {
            perClass.Release();
            throw;
        }
        return new Slot(perClass, _total);
    }

    private sealed class Slot(SemaphoreSlim perClass, SemaphoreSlim total) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                total.Release();
                perClass.Release();
            }
        }
    }
}
