using AdmissionSystem.Api.Data;
using AdmissionSystem.Api.Domain;
using AdmissionSystem.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace AdmissionSystem.Api.Endpoints;

public sealed record HoldCreatedResponse(string HoldId, string ClassId, string StudentId, HoldStatus Status, DateTime ExpiresAt);

public sealed record HoldResponse(string HoldId, string ClassId, HoldStatus Status, DateTime ExpiresAt);

public sealed record MyHoldResponse(string HoldId, string ClassId, string ClassName, HoldStatus Status, DateTime ExpiresAt);

public sealed record ConfirmResponse(string HoldId, HoldStatus Status);

public static class HoldEndpoints
{
    public static void MapHoldEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/classes/{classId}/holds", CreateHold);
        app.MapGet("/holds/{holdId}", GetHold);
        app.MapGet("/me/holds", ListMyHolds);
        app.MapPost("/holds/{holdId}/confirm", ConfirmHold);
        app.MapDelete("/holds/{holdId}", CancelHold);
    }

    private static async Task<IResult> CreateHold(
        string classId, HttpRequest request, AdmissionDbContext db, TimeProvider clock, HoldSettings settings,
        ClassWriteGate gate, CancellationToken ct)
    {
        var studentId = StudentIdHeader.Require(request);

        // Everything below runs in one transaction that first locks the class row. Concurrent applications for the same
        // class (from any thread or any backend process) queue up on that lock, so each one counts and inserts against
        // the committed result of the one before it: heldSeats + confirmedSeats can never pass totalSeats.
        // READ COMMITTED is enough: every statement after the lock sees all holds committed by earlier lock holders.
        // The gate only keeps waiting requests from each pinning a pooled connection; it is not what makes this correct.
        using var slot = await gate.EnterAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var totalSeats = await ClassLock.AcquireAsync(db, classId, ct)
            ?? throw ApiException.NotFound(ErrorCodes.ClassNotFound, "This class does not exist.");

        // Read the clock only now: a request that queued for seconds behind others must not stamp a stale createdAt
        // or judge expiry with an old "now".
        var now = clock.GetUtcNow().UtcDateTime;

        // The sweeper may not have stored this student's lapsed HELD hold as EXPIRED yet; do it now, under the lock, so it
        // can neither count as an application nor trip the unique index below.
        await db.Holds
            .Where(h => h.ClassId == classId && h.StudentId == studentId && h.Status == HoldStatus.HELD && h.ExpiresAt <= now)
            .ExecuteUpdateAsync(s => s.SetProperty(h => h.Status, HoldStatus.EXPIRED), ct);

        var isActive = Hold.IsActive(now);

        var alreadyApplied = await db.Holds
            .Where(h => h.ClassId == classId && h.StudentId == studentId)
            .AnyAsync(isActive, ct);
        if (alreadyApplied)
        {
            throw AlreadyApplied();
        }

        var occupied = await db.Holds.Where(h => h.ClassId == classId).CountAsync(isActive, ct);
        if (occupied >= totalSeats)
        {
            throw ApiException.Conflict(ErrorCodes.NoSeatsAvailable, "No seats are left in this class.");
        }

        // Whole seconds, so the serialized expiresAt is exactly createdAt + duration.
        var createdAt = new DateTime(now.Ticks - now.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);
        var hold = new Hold
        {
            Id = IdGenerator.NewHoldId(),
            ClassId = classId,
            StudentId = studentId,
            Status = HoldStatus.HELD,
            CreatedAt = createdAt,
            ExpiresAt = createdAt + settings.Duration,
        };
        db.Holds.Add(hold);
        try
        {
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (DbUpdateException ex) when (PostgresErrors.IsUniqueViolation(ex, DbConstraints.ActiveHoldPerStudentAndClass))
        {
            // The database's own one-active-hold-per-student-and-class rule fired: same answer as the explicit check.
            throw AlreadyApplied();
        }

        return Results.Created($"/holds/{hold.Id}",
            new HoldCreatedResponse(hold.Id, hold.ClassId, hold.StudentId, hold.Status, hold.ExpiresAt));
    }

    private static ApiException AlreadyApplied() =>
        ApiException.Conflict(ErrorCodes.AlreadyApplied, "You already have an active application for this class.");

    private static async Task<IResult> GetHold(string holdId, AdmissionDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var hold = await db.Holds.AsNoTracking().FirstOrDefaultAsync(h => h.Id == holdId, ct)
            ?? throw ApiException.NotFound(ErrorCodes.HoldNotFound, "This application does not exist.");

        var now = clock.GetUtcNow().UtcDateTime;
        return Results.Ok(new HoldResponse(hold.Id, hold.ClassId, hold.EffectiveStatus(now), hold.ExpiresAt));
    }

    private static async Task<IResult> ListMyHolds(HttpRequest request, AdmissionDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var studentId = StudentIdHeader.Require(request);

        var holds = await db.Holds.AsNoTracking()
            .Where(h => h.StudentId == studentId)
            .OrderByDescending(h => h.CreatedAt).ThenBy(h => h.Id)
            .Select(h => new { Hold = h, ClassName = h.Class!.Name })
            .ToListAsync(ct);

        var now = clock.GetUtcNow().UtcDateTime;
        return Results.Ok(holds.Select(r =>
            new MyHoldResponse(r.Hold.Id, r.Hold.ClassId, r.ClassName, r.Hold.EffectiveStatus(now), r.Hold.ExpiresAt)));
    }

    // Cancel is ONE conditional UPDATE: it can only move a row out of the active set, so it needs no class lock.
    // Confirm is ONE conditional UPDATE too, but it takes the class lock first (see ConfirmHold). In both, the row only
    // changes while it is still HELD, unexpired and owned by the caller, so concurrent or repeated calls cannot move it
    // twice. Only when the UPDATE touches no row do we read the hold, to answer with the right status.

    private static async Task<IResult> ConfirmHold(
        string holdId, HttpRequest request, AdmissionDbContext db, TimeProvider clock, ClassWriteGate gate, CancellationToken ct)
    {
        var studentId = StudentIdHeader.Require(request);

        // 404 / 403 before touching any lock, so a stranger's or unknown id never queues behind real applications.
        var existing = await LoadOwnedHold(db, holdId, studentId, ct);

        // Confirming turns an active hold into another active hold, so it cannot overbook by itself. It takes the class
        // lock anyway, and reads the clock only after getting it, so that "is this hold still alive?" is decided in the
        // same serial order as CreateHold's count: a hold CreateHold already treated as lapsed (and replaced) can never
        // be revived here by a confirm that read an older clock value.
        using var slot = await gate.EnterAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await ClassLock.AcquireAsync(db, existing.ClassId, ct);
        var now = clock.GetUtcNow().UtcDateTime;

        var updated = await db.Holds
            .Where(h => h.Id == holdId && h.StudentId == studentId && h.Status == HoldStatus.HELD && h.ExpiresAt > now)
            .ExecuteUpdateAsync(s => s.SetProperty(h => h.Status, HoldStatus.CONFIRMED), ct);
        if (updated == 1)
        {
            await tx.CommitAsync(ct);
            return Results.Ok(new ConfirmResponse(holdId, HoldStatus.CONFIRMED));
        }

        var hold = await LoadOwnedHold(db, holdId, studentId, ct);
        return hold.EffectiveStatus(now) switch
        {
            HoldStatus.CONFIRMED => Results.Ok(new ConfirmResponse(holdId, HoldStatus.CONFIRMED)), // repeat confirm: idempotent
            HoldStatus.EXPIRED => throw ApiException.Conflict(ErrorCodes.HoldExpired,
                "This application has expired, so the fee can no longer be paid. Please apply again."),
            HoldStatus.CANCELLED => throw ApiException.Conflict(ErrorCodes.HoldCancelled,
                "This application was cancelled, so the fee cannot be paid. Please apply again."),
            _ => throw StateChanged(),
        };
    }

    private static async Task<IResult> CancelHold(
        string holdId, HttpRequest request, AdmissionDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var studentId = StudentIdHeader.Require(request);
        var now = clock.GetUtcNow().UtcDateTime;

        var updated = await db.Holds
            .Where(h => h.Id == holdId && h.StudentId == studentId && h.Status == HoldStatus.HELD && h.ExpiresAt > now)
            .ExecuteUpdateAsync(s => s.SetProperty(h => h.Status, HoldStatus.CANCELLED), ct);
        if (updated == 1)
        {
            return Results.NoContent();
        }

        var hold = await LoadOwnedHold(db, holdId, studentId, ct);
        return hold.EffectiveStatus(now) switch
        {
            HoldStatus.CANCELLED => Results.NoContent(), // repeat cancel: already in the requested end state
            HoldStatus.CONFIRMED => throw ApiException.Conflict(ErrorCodes.HoldConfirmed,
                "The admission fee is already paid, so this application can no longer be cancelled."),
            HoldStatus.EXPIRED => throw ApiException.Conflict(ErrorCodes.HoldExpired,
                "This application has already expired, so there is nothing to cancel."),
            _ => throw StateChanged(),
        };
    }

    /// <summary>The hold, or 404 if it does not exist and 403 if it belongs to another student.</summary>
    private static async Task<Hold> LoadOwnedHold(AdmissionDbContext db, string holdId, string studentId, CancellationToken ct)
    {
        var hold = await db.Holds.AsNoTracking().FirstOrDefaultAsync(h => h.Id == holdId, ct)
            ?? throw ApiException.NotFound(ErrorCodes.HoldNotFound, "This application does not exist.");
        if (hold.StudentId != studentId)
        {
            throw ApiException.Forbidden(ErrorCodes.NotYourHold, "This application belongs to another student.");
        }
        return hold;
    }

    // Reached only if the hold changed between our UPDATE and the follow-up read in a way the UPDATE should have caught.
    private static ApiException StateChanged() =>
        ApiException.Conflict(ErrorCodes.HoldStateChanged, "This application changed while we were processing your request. Please try again.");
}

