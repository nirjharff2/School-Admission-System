using AdmissionSystem.Api.Data;
using AdmissionSystem.Api.Domain;
using AdmissionSystem.Api.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AdmissionSystem.Api.Endpoints;

public sealed record CreateClassRequest(string? Name, int? TotalSeats);

public sealed record CreateClassResponse(string Id, string Name, int TotalSeats);

public sealed record ClassResponse(string Id, string Name, int TotalSeats, int HeldSeats, int ConfirmedSeats, int AvailableSeats);

public static class ClassEndpoints
{
    public static void MapClassEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/classes", CreateClass);
        app.MapGet("/classes", ListClasses);
        app.MapGet("/classes/{classId}", GetClass);
    }

    private static async Task<IResult> CreateClass(
        [FromBody] CreateClassRequest? request, AdmissionDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var name = request?.Name?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            throw ApiException.BadRequest(ErrorCodes.ValidationFailed, "Class name is required.");
        }
        if (name.Length > Limits.ClassNameMaxLength)
        {
            throw ApiException.BadRequest(ErrorCodes.ValidationFailed,
                $"Class name must be at most {Limits.ClassNameMaxLength} characters.");
        }
        if (name.Any(char.IsControl))
        {
            throw ApiException.BadRequest(ErrorCodes.ValidationFailed, "Class name must not contain control characters.");
        }
        if (request!.TotalSeats is not >= 1)
        {
            throw ApiException.BadRequest(ErrorCodes.ValidationFailed, "Total seats must be a whole number of at least 1.");
        }

        var created = new SchoolClass
        {
            Id = IdGenerator.NewClassId(),
            Name = name,
            TotalSeats = request.TotalSeats.Value,
            CreatedAt = clock.GetUtcNow().UtcDateTime,
        };
        db.Classes.Add(created);
        await db.SaveChangesAsync(ct);

        return Results.Created($"/classes/{created.Id}", new CreateClassResponse(created.Id, created.Name, created.TotalSeats));
    }

    private static async Task<IResult> ListClasses(AdmissionDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var rows = await WithSeatCounts(db, clock)
            .OrderBy(r => r.CreatedAt).ThenBy(r => r.Id)
            .ToListAsync(ct);
        return Results.Ok(rows.Select(r => r.ToResponse()));
    }

    private static async Task<IResult> GetClass(string classId, AdmissionDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var row = await WithSeatCounts(db, clock).FirstOrDefaultAsync(r => r.Id == classId, ct)
            ?? throw ApiException.NotFound(ErrorCodes.ClassNotFound, "This class does not exist.");
        return Results.Ok(row.ToResponse());
    }

    private sealed class ClassRow
    {
        public required string Id { get; init; }
        public required string Name { get; init; }
        public int TotalSeats { get; init; }
        public DateTime CreatedAt { get; init; }
        public int HeldSeats { get; init; }
        public int ConfirmedSeats { get; init; }

        public ClassResponse ToResponse() =>
            new(Id, Name, TotalSeats, HeldSeats, ConfirmedSeats, TotalSeats - HeldSeats - ConfirmedSeats);
    }

    /// <summary>A hold counts as held only while HELD and not yet expired, so expiry needs no sweeper to be reflected here.</summary>
    private static IQueryable<ClassRow> WithSeatCounts(AdmissionDbContext db, TimeProvider clock)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        return db.Classes.AsNoTracking().Select(c => new ClassRow
        {
            Id = c.Id,
            Name = c.Name,
            TotalSeats = c.TotalSeats,
            CreatedAt = c.CreatedAt,
            HeldSeats = c.Holds.Count(h => h.Status == HoldStatus.HELD && h.ExpiresAt > now),
            ConfirmedSeats = c.Holds.Count(h => h.Status == HoldStatus.CONFIRMED),
        });
    }
}
