using System.Linq.Expressions;

namespace AdmissionSystem.Api.Domain;

/// <summary>A student's application for a seat in a class.</summary>
public class Hold
{
    public required string Id { get; set; }
    public required string ClassId { get; set; }
    public required string StudentId { get; set; }
    public HoldStatus Status { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }

    public SchoolClass? Class { get; set; }

    /// <summary>
    /// The status to report. A stored HELD hold whose expiry has passed is EXPIRED even before any sweeper has
    /// rewritten the row, so expiry never depends on a background job or an API call.
    /// </summary>
    public HoldStatus EffectiveStatus(DateTime now) =>
        Status == HoldStatus.HELD && ExpiresAt <= now ? HoldStatus.EXPIRED : Status;

    /// <summary>Holds that occupy a seat: CONFIRMED, or HELD and not yet expired.</summary>
    public static Expression<Func<Hold, bool>> IsActive(DateTime now) =>
        h => h.Status == HoldStatus.CONFIRMED || (h.Status == HoldStatus.HELD && h.ExpiresAt > now);
}
