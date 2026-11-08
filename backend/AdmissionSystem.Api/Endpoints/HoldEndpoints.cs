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

        // The gate slot is taken before any database access. Connections are handed out first come, first served, so if
        // thousands of applications each queued for a connection to run their first query, anything else needing one
        // (a confirm, a page refresh) would wait behind all of them. With the slot first, at most a few dozen requests
        // are ever waiting for or using a connection, and the rest wait in memory.
        // Everything after that runs in one transaction that first locks the class row. Concurrent applications for the
        // same class (from any thread or any backend process) queue up on that lock, so each one counts and inserts
        // against the committed result of the one before it: heldSeats + confirmedSeats can never pass totalSeats.
        // READ COMMITTED is enough: every statement after the lock sees all holds committed by earlier lock holders.
        // The gate is a throughput guard; it is not what makes this correct.
        using var slot = await gate.EnterAsync(ct);

        // Fast refusal, no lock. A student who already has an active application, or a class that is already full, is
        // answered from one plain read. In a burst every request arrives before anyone has been granted anything, but by
        // the time a queued request reaches the front the class is usually full, so this turns it away without opening a
        // transaction or taking the lock: the thousands of futile applications drain at read speed instead of one lock
        // transaction each. The read can be slightly stale (a seat may have just been freed): that only means a request
        // racing a cancellation is told "full", exactly as if it had arrived a moment earlier. It can never grant a seat;
        // every grant still goes through the locked path below, which repeats all the checks.
        await RefuseWithoutLockIfAlreadyDecidedAsync(classId, studentId, db, clock, ct);

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var totalSeats = await ClassLock.AcquireAsync(db, classId, ct)
            ?? throw ApiException.NotFound(ErrorCodes.ClassNotFound, "This class does not exist.");

        // Read the clock only now: a request that queued for seconds behind others must not stamp a stale createdAt
        // or judge expiry with an old "now".
        var now = clock.GetUtcNow().UtcDateTime;

        // Store every lapsed HELD hold of this class as EXPIRED before counting (the sweeper may not have got to them yet).
        // That keeps this student's own lapsed hold from counting or tripping the unique index, and, because the UPDATE
        // takes each row's lock, it serializes with a confirm that is changing one of those rows at this very moment
        // (see the comment above ConfirmHold).
        await db.Holds
            .Where(h => h.ClassId == classId && h.Status == HoldStatus.HELD && h.ExpiresAt <= now)
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

    /// <summary>
    /// 404 / 409 answers that do not need the class lock: unknown class, a student who already holds an active
    /// application, or a class with no free seat. It only ever refuses; granting a seat is the locked path's job.
    /// </summary>
    private static async Task RefuseWithoutLockIfAlreadyDecidedAsync(
        string classId, string studentId, AdmissionDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var isActive = Hold.IsActive(clock.GetUtcNow().UtcDateTime);

        // One round trip: the class's size, how many active holds it has, and whether this student has one.
        var view = await db.Classes.AsNoTracking()
            .Where(c => c.Id == classId)
            .Select(c => new
            {
                c.TotalSeats,
                Occupied = c.Holds.AsQueryable().Count(isActive),
                Mine = c.Holds.AsQueryable().Where(h => h.StudentId == studentId).Any(isActive),
            })
            .FirstOrDefaultAsync(ct)
            ?? throw ApiException.NotFound(ErrorCodes.ClassNotFound, "This class does not exist.");

        if (view.Mine)
        {
            throw AlreadyApplied();
        }
        if (view.Occupied >= view.TotalSeats)
        {
            throw ApiException.Conflict(ErrorCodes.NoSeatsAvailable, "No seats are left in this class.");
        }
    }

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

    // Cancel and confirm are each ONE conditional UPDATE. The row only changes while it is still HELD, unexpired and owned
    // by the caller, so concurrent or repeated calls cannot move it twice. Only when the UPDATE touches no row do we read
    // the hold, to answer with the right status.
    //
    // Cancel can only remove a hold from the active set, so it can never overbook a class. Confirm turns one active hold
    // into another, so it cannot either, with one subtlety: a hold that expires at the very moment of a confirm. That race
    // is closed by row locks, not by the class lock: CreateHold marks every lapsed HELD row of the class EXPIRED, in an
    // UPDATE, before it counts. If a confirm already holds that row's lock, the UPDATE waits for it, then re-reads the row
    // as CONFIRMED, skips it, and the count that follows includes it. If CreateHold's UPDATE gets the row first, the
    // confirm's UPDATE waits, re-reads the row as EXPIRED and matches nothing (409). Either way the two agree on whether
    // the hold counts, so confirm never needs to queue behind the applications for the class.

    private static async Task<IResult> ConfirmHold(
        string holdId, HttpRequest request, AdmissionDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var studentId = StudentIdHeader.Require(request);
        var now = clock.GetUtcNow().UtcDateTime;

        var updated = await db.Holds
            .Where(h => h.Id == holdId && h.StudentId == studentId && h.Status == HoldStatus.HELD && h.ExpiresAt > now)
            .ExecuteUpdateAsync(s => s.SetProperty(h => h.Status, HoldStatus.CONFIRMED), ct);
        if (updated == 1)
        {
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

