using AdmissionSystem.Api.Data;
using AdmissionSystem.Api.Domain;
using AdmissionSystem.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace AdmissionSystem.Api.Endpoints;

public sealed record HoldCreatedResponse(string HoldId, string ClassId, string StudentId, HoldStatus Status, DateTime ExpiresAt);

public sealed record HoldResponse(string HoldId, string ClassId, HoldStatus Status, DateTime ExpiresAt);

public sealed record MyHoldResponse(string HoldId, string ClassId, string ClassName, HoldStatus Status, DateTime ExpiresAt);

public static class HoldEndpoints
{
    public static void MapHoldEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/classes/{classId}/holds", CreateHold);
        app.MapGet("/holds/{holdId}", GetHold);
        app.MapGet("/me/holds", ListMyHolds);
    }

    private static async Task<IResult> CreateHold(
        string classId, HttpRequest request, AdmissionDbContext db, TimeProvider clock, HoldSettings settings, CancellationToken ct)
    {
        var studentId = StudentIdHeader.Require(request);

        var totalSeats = await db.Classes.AsNoTracking()
            .Where(c => c.Id == classId)
            .Select(c => (int?)c.TotalSeats)
            .FirstOrDefaultAsync(ct)
            ?? throw ApiException.NotFound(ErrorCodes.ClassNotFound, "This class does not exist.");

        var now = clock.GetUtcNow().UtcDateTime;
        var isActive = Hold.IsActive(now);

        var alreadyApplied = await db.Holds
            .Where(h => h.ClassId == classId && h.StudentId == studentId)
            .AnyAsync(isActive, ct);
        if (alreadyApplied)
        {
            throw ApiException.Conflict(ErrorCodes.AlreadyApplied,
                "You already have an active application for this class.");
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
        await db.SaveChangesAsync(ct);

        return Results.Created($"/holds/{hold.Id}",
            new HoldCreatedResponse(hold.Id, hold.ClassId, hold.StudentId, hold.Status, hold.ExpiresAt));
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
}
