using System.Net;
using AdmissionSystem.Api.Data;
using AdmissionSystem.Api.Domain;
using AdmissionSystem.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using static AdmissionSystem.Tests.TestSupport;

namespace AdmissionSystem.Tests;

/// <summary>
/// What the database guarantees about holds: the one-active-hold unique index, the in-transaction expiry of a student's own
/// lapsed hold, and (as a sanity check on the class-row lock) a burst of simultaneous applications.
/// The thorough concurrency suite, including two backend processes, is a separate piece of work.
/// </summary>
[Collection(ApiCollection.Name)]
public class HoldCapacityTests(ApiFixture api)
{
    private readonly HttpClient _client = api.CreateClient();

    // ---- the unique index (migration) -----------------------------------------------------------------------

    [Theory]
    [InlineData(HoldStatus.HELD, HoldStatus.HELD)]
    [InlineData(HoldStatus.HELD, HoldStatus.CONFIRMED)]
    [InlineData(HoldStatus.CONFIRMED, HoldStatus.HELD)]
    [InlineData(HoldStatus.CONFIRMED, HoldStatus.CONFIRMED)]
    public async Task The_database_rejects_a_second_active_hold_for_the_same_student_and_class(HoldStatus first, HoldStatus second)
    {
        var classId = await CreateClassAsync(_client, 5);
        var student = NewStudentId();
        var future = DateTime.UtcNow.AddMinutes(5);
        await SeedAsync(NewHold(classId, student, first, future));

        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => SeedAsync(NewHold(classId, student, second, future)));

        Assert.True(PostgresErrors.IsUniqueViolation(ex, DbConstraints.ActiveHoldPerStudentAndClass));
    }

    [Fact]
    public async Task The_unique_index_ignores_released_holds_other_classes_and_other_students()
    {
        var classId = await CreateClassAsync(_client, 5);
        var otherClass = await CreateClassAsync(_client, 5);
        var student = NewStudentId();
        var future = DateTime.UtcNow.AddMinutes(5);

        await SeedAsync(
            NewHold(classId, student, HoldStatus.CANCELLED, future),
            NewHold(classId, student, HoldStatus.CANCELLED, future),
            NewHold(classId, student, HoldStatus.EXPIRED, future),
            NewHold(classId, student, HoldStatus.HELD, future),         // the one active hold
            NewHold(otherClass, student, HoldStatus.HELD, future),      // same student, other class
            NewHold(classId, NewStudentId(), HoldStatus.HELD, future)); // other student, same class
    }

    [Fact]
    public void Only_a_postgres_unique_violation_on_the_named_constraint_counts_as_one()
    {
        DbUpdateException Wrap(string sqlState, string constraint) => new("save failed",
            new PostgresException("duplicate", "ERROR", "ERROR", sqlState, constraintName: constraint));
        var name = DbConstraints.ActiveHoldPerStudentAndClass;

        Assert.True(PostgresErrors.IsUniqueViolation(Wrap(PostgresErrorCodes.UniqueViolation, name), name));
        Assert.False(PostgresErrors.IsUniqueViolation(Wrap(PostgresErrorCodes.UniqueViolation, "some_other_index"), name));
        Assert.False(PostgresErrors.IsUniqueViolation(Wrap(PostgresErrorCodes.ForeignKeyViolation, name), name));
        Assert.False(PostgresErrors.IsUniqueViolation(new DbUpdateException("no inner exception"), name));
        Assert.False(PostgresErrors.IsUniqueViolation(new InvalidOperationException(), name));
    }

    // ---- connections ----------------------------------------------------------------------------------------

    [Fact]
    public void The_connection_pool_is_bounded_below_postgres_limit_unless_the_connection_string_says_otherwise()
    {
        const string baseString = "Host=db;Database=admission;Username=u;Password=p";

        Assert.Equal(DatabaseConnection.DefaultMaxPoolSize,
            new NpgsqlConnectionStringBuilder(DatabaseConnection.WithBoundedPool(baseString)).MaxPoolSize);
        Assert.Equal(7,
            new NpgsqlConnectionStringBuilder(DatabaseConnection.WithBoundedPool(baseString + ";Maximum Pool Size=7")).MaxPoolSize);
        Assert.True(DatabaseConnection.DefaultMaxPoolSize * 3 <= 100, "three instances must fit in PostgreSQL's default max_connections");
    }

    // ---- applying under the class lock ----------------------------------------------------------------------

    [Fact]
    public async Task Applying_again_after_a_lapsed_hold_stores_the_old_hold_as_expired_in_the_same_step()
    {
        var student = NewStudentId();
        var classId = await CreateClassAsync(_client, 3);
        var lapsed = NewHold(classId, student, HoldStatus.HELD, DateTime.UtcNow.AddSeconds(-1)); // sweeper has not run
        await SeedAsync(lapsed);

        var newHoldId = await ApplyAsync(_client, classId, student);

        Assert.NotEqual(lapsed.Id, newHoldId);
        Assert.Equal(HoldStatus.EXPIRED, await StoredStatus(lapsed.Id));
        Assert.Equal(HoldStatus.HELD, await StoredStatus(newHoldId));
        AssertSeatCounts(await GetClassAsync(_client, classId), null, total: 3, held: 1, confirmed: 0, available: 2);
    }

    [Fact]
    public async Task A_refused_application_changes_nothing_in_the_database()
    {
        var student = NewStudentId();
        var classId = await CreateClassAsync(_client, 1);
        var lapsedOfOther = NewHold(classId, NewStudentId(), HoldStatus.HELD, DateTime.UtcNow.AddSeconds(-1));
        await SeedAsync(lapsedOfOther);
        await ApplyAsync(_client, classId, student);

        var refused = await SendAsync(_client, HttpMethod.Post, $"/classes/{classId}/holds", NewStudentId());

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal(2, await CountHolds(classId, HoldStatus.HELD)); // the student's real hold + the other student's lapsed one, no new row
        Assert.Equal(HoldStatus.HELD, await StoredStatus(lapsedOfOther.Id)); // another student's row is not ours to touch
    }

    [Fact]
    public async Task Many_simultaneous_applications_for_a_small_class_never_exceed_its_seats()
    {
        const int seats = 10;
        var classId = await CreateClassAsync(_client, seats);

        var responses = await Task.WhenAll(Enumerable.Range(0, 60).Select(_ =>
            Task.Run(() => SendAsync(_client, HttpMethod.Post, $"/classes/{classId}/holds", NewStudentId()))));

        Assert.Equal(seats, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(60 - seats, responses.Count(r => r.StatusCode == HttpStatusCode.Conflict));
        Assert.Equal(seats, await CountHolds(classId, HoldStatus.HELD));
        AssertSeatCounts(await GetClassAsync(_client, classId), null, total: seats, held: seats, confirmed: 0, available: 0);
    }

    [Fact]
    public async Task Simultaneous_applications_by_one_student_to_one_class_produce_exactly_one_hold()
    {
        var student = NewStudentId();
        var classId = await CreateClassAsync(_client, 20);

        var responses = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ =>
            Task.Run(() => SendAsync(_client, HttpMethod.Post, $"/classes/{classId}/holds", student))));

        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(19, responses.Count(r => r.StatusCode == HttpStatusCode.Conflict));
        Assert.Equal(1, await CountHolds(classId, HoldStatus.HELD));
    }

    // ---- helpers --------------------------------------------------------------------------------------------

    private async Task SeedAsync(params Hold[] holds) =>
        await api.WithDbAsync(async db =>
        {
            db.Holds.AddRange(holds);
            await db.SaveChangesAsync();
        });

    private async Task<HoldStatus> StoredStatus(string holdId)
    {
        HoldStatus status = default;
        await api.WithDbAsync(async db =>
            status = await db.Holds.AsNoTracking().Where(h => h.Id == holdId).Select(h => h.Status).SingleAsync());
        return status;
    }

    private async Task<int> CountHolds(string classId, HoldStatus status)
    {
        var count = 0;
        await api.WithDbAsync(async db =>
            count = await db.Holds.AsNoTracking().CountAsync(h => h.ClassId == classId && h.Status == status));
        return count;
    }
}
