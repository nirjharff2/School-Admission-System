using AdmissionSystem.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace AdmissionSystem.Api.Infrastructure;

/// <summary>
/// Stores lapsed HELD holds as EXPIRED. Used by the sweeper (every class) and by an application (its own class).
/// </summary>
/// <remarks>
/// Both callers update many rows at once and run at the same time, so the rows must be locked in one fixed order, or two
/// statements that each hold part of an overlapping set wait for each other and PostgreSQL aborts one with a deadlock
/// (40P01). A plain <c>UPDATE ... WHERE</c> locks rows in whatever order its plan scans them, which differs between the
/// two callers. Here the rows are first locked by a <c>SELECT ... ORDER BY id FOR UPDATE</c>, always in id order, and
/// only those rows are updated. <c>FOR UPDATE</c> also re-checks the predicate after waiting for a row, so a row a
/// concurrent confirm has just turned CONFIRMED is skipped; that is what keeps expiry and confirm in agreement.
/// </remarks>
public static class HoldExpiry
{
    /// <summary>Expires every lapsed HELD hold. Returns how many rows changed.</summary>
    public static Task<int> ExpireAllLapsedAsync(AdmissionDbContext db, DateTime now, CancellationToken ct) =>
        db.Database.ExecuteSqlAsync($"""
            WITH lapsed AS (
                SELECT id FROM holds
                WHERE status = 'HELD' AND expires_at <= {now}
                ORDER BY id
                FOR UPDATE)
            UPDATE holds h SET status = 'EXPIRED' FROM lapsed WHERE h.id = lapsed.id
            """, ct);

    /// <summary>Expires every lapsed HELD hold of one class. Returns how many rows changed.</summary>
    public static Task<int> ExpireLapsedInClassAsync(AdmissionDbContext db, string classId, DateTime now, CancellationToken ct) =>
        db.Database.ExecuteSqlAsync($"""
            WITH lapsed AS (
                SELECT id FROM holds
                WHERE class_id = {classId} AND status = 'HELD' AND expires_at <= {now}
                ORDER BY id
                FOR UPDATE)
            UPDATE holds h SET status = 'EXPIRED' FROM lapsed WHERE h.id = lapsed.id
            """, ct);
}
