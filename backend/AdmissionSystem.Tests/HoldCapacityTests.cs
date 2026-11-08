using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
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
/// The formal concurrency suite (200 requests for 10 seats, parallel confirms, two backend instances) is in <see cref="ConcurrencyTests"/>.
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
    public async Task An_application_stores_every_lapsed_hold_of_the_class_as_expired_and_a_refused_one_adds_no_row()
    {
        var student = NewStudentId();
        var classId = await CreateClassAsync(_client, 1);
        var lapsedOfOther = NewHold(classId, NewStudentId(), HoldStatus.HELD, DateTime.UtcNow.AddSeconds(-1));
        await SeedAsync(lapsedOfOther);

        await ApplyAsync(_client, classId, student);

        // Applying took the lapsed hold's row lock and stored it as EXPIRED, whoever it belonged to (that is what
        // serializes it with a confirm racing the expiry); the seat it occupied on paper was free anyway.
        Assert.Equal(HoldStatus.EXPIRED, await StoredStatus(lapsedOfOther.Id));

        var refused = await SendAsync(_client, HttpMethod.Post, $"/classes/{classId}/holds", NewStudentId());

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal(1, await CountHolds(classId, HoldStatus.HELD));    // the student's real hold, no new row
        Assert.Equal(1, await CountHolds(classId, HoldStatus.EXPIRED)); // the lapsed one
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

    [Fact]
    public async Task The_sweeper_and_applications_expiring_the_same_lapsed_holds_never_deadlock_or_fail()
    {
        // Both statements update many rows and used to lock them in different orders (the application's by student, the
        // sweeper's by physical position), which PostgreSQL resolves by aborting one with a deadlock: a 500 for the
        // applicant, a failed sweep. Each round seeds a couple of hundred lapsed holds in shuffled order, then starts a
        // sweep at a random few-millisecond offset from an application to the same class, so across rounds the two
        // UPDATEs overlap at every possible alignment.
        var sweeper = api.Services.GetServices<IHostedService>().OfType<ExpirySweeper>().Single();
        var random = new Random(12345);

        for (var round = 0; round < 80; round++)
        {
            var classId = await CreateClassAsync(_client, 1000);
            var lapsed = Enumerable.Range(0, 200)
                .Select(_ => NewHold(classId, NewStudentId(), HoldStatus.HELD, DateTime.UtcNow.AddSeconds(-random.Next(1, 3000))))
                .ToArray();
            await SeedAsync(lapsed);

            var offsetMs = random.Next(0, 20);
            var sweep = Task.Run(async () =>
            {
                await Task.Delay(offsetMs);
                return await sweeper.SweepOnceAsync(CancellationToken.None); // throws if the sweep was a deadlock victim
            });
            var apply = SendAsync(_client, HttpMethod.Post, $"/classes/{classId}/holds", NewStudentId());
            await Task.WhenAll(sweep, apply);

            Assert.Equal(HttpStatusCode.Created, (await apply).StatusCode);
            Assert.Equal(0, await CountHolds(classId, HoldStatus.HELD) - 1); // only the new hold is still HELD
        }
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
