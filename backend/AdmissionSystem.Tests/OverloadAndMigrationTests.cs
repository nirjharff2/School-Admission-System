using System.Net;
using AdmissionSystem.Api.Data;
using AdmissionSystem.Api.Domain;
using AdmissionSystem.Api.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using static AdmissionSystem.Tests.TestSupport;

namespace AdmissionSystem.Tests;

/// <summary>Behaviour around the class lock under heavy load, and the migration that adds the unique index to existing data.</summary>
[Collection(ApiCollection.Name)]
public class OverloadAndMigrationTests(ApiFixture api)
{
    private readonly HttpClient _client = api.CreateClient();

    // ---- load -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_burst_of_thousands_of_applications_gets_only_201_or_409_and_sells_each_seat_once()
    {
        const int seats = 300;
        const int requests = 3000;
        var classId = await CreateClassAsync(_client, seats);

        var statuses = await Task.WhenAll(Enumerable.Range(0, requests).Select(_ =>
            Task.Run(async () => (await SendAsync(_client, HttpMethod.Post, $"/classes/{classId}/holds", NewStudentId())).StatusCode)));

        Assert.Equal(seats, statuses.Count(s => s == HttpStatusCode.Created));
        Assert.Equal(requests - seats, statuses.Count(s => s == HttpStatusCode.Conflict));
        AssertSeatCounts(await GetClassAsync(_client, classId), null, total: seats, held: seats, confirmed: 0, available: 0);
    }

    [Fact]
    public async Task Applications_and_confirms_racing_on_one_class_never_overbook_it()
    {
        const int seats = 20;
        var classId = await CreateClassAsync(_client, seats);
        var students = Enumerable.Range(0, seats).Select(_ => NewStudentId()).ToList();
        var holdIds = new List<string>();
        foreach (var s in students)
        {
            holdIds.Add(await ApplyAsync(_client, classId, s));
        }

        var confirms = holdIds.Select((id, i) => Task.Run(async () =>
            (await SendAsync(_client, HttpMethod.Post, $"/holds/{id}/confirm", students[i])).StatusCode));
        var newcomers = Enumerable.Range(0, 100).Select(_ => Task.Run(async () =>
            (await SendAsync(_client, HttpMethod.Post, $"/classes/{classId}/holds", NewStudentId())).StatusCode));
        var results = await Task.WhenAll(confirms.Concat(newcomers));

        Assert.DoesNotContain(results, s => (int)s >= 500);
        AssertSeatCounts(await GetClassAsync(_client, classId), null, total: seats, held: 0, confirmed: seats, available: 0);
    }

    [Fact]
    public async Task When_the_database_connection_pool_stays_exhausted_the_answer_is_503_with_retry_after_not_500()
    {
        var connection = new NpgsqlConnectionStringBuilder(api.ConnectionString) { MaxPoolSize = 1, Timeout = 1 }.ConnectionString;
        using var starved = api.WithWebHostBuilder(b => b.UseSetting("ConnectionStrings:Default", connection));
        var client = starved.CreateClient();

        // Take the only pooled connection and keep it.
        await using var scope = starved.Services.CreateAsyncScope();
        var hog = scope.ServiceProvider.GetRequiredService<AdmissionDbContext>();
        await hog.Database.OpenConnectionAsync();

        var response = await client.GetAsync("/classes");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.True(response.Headers.RetryAfter is not null);
        var body = await ReadJsonAsync(response);
        AssertErrorBody(body);
        Assert.Equal("service_busy", body.GetProperty("error").GetString());
    }

    [Fact]
    public void Timeouts_server_capacity_errors_and_deadlock_victims_count_as_overload_but_other_failures_do_not()
    {
        PostgresException Pg(string sqlState) => new("x", "ERROR", "ERROR", sqlState);

        Assert.True(PostgresErrors.IsOverload(new TimeoutException()));
        Assert.True(PostgresErrors.IsOverload(new NpgsqlException("The connection pool has been exhausted", new TimeoutException())));
        Assert.True(PostgresErrors.IsOverload(new DbUpdateException("save", Pg(PostgresErrorCodes.LockNotAvailable))));
        Assert.True(PostgresErrors.IsOverload(Pg(PostgresErrorCodes.TooManyConnections)));
        Assert.True(PostgresErrors.IsOverload(Pg(PostgresErrorCodes.QueryCanceled)));
        Assert.True(PostgresErrors.IsOverload(new DbUpdateException("save", Pg(PostgresErrorCodes.DeadlockDetected))));
        Assert.False(PostgresErrors.IsOverload(new DbUpdateException("save", Pg(PostgresErrorCodes.UniqueViolation))));
        Assert.False(PostgresErrors.IsOverload(new InvalidOperationException("boom")));
    }

    [Fact]
    public async Task The_gate_admits_only_its_per_class_limit_and_waiting_honours_cancellation()
    {
        var gate = new ClassWriteGate(totalLimit: 10, perClassLimit: 2);
        var first = await gate.EnterAsync("c_a", CancellationToken.None);
        var second = await gate.EnterAsync("c_a", CancellationToken.None);

        var third = gate.EnterAsync("c_a", CancellationToken.None);
        await Task.Delay(100);
        Assert.False(third.IsCompleted);

        using var cts = new CancellationTokenSource(100);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => gate.EnterAsync("c_a", cts.Token));

        first.Dispose();
        first.Dispose(); // releasing twice must not hand out an extra slot
        using var thirdSlot = await third.WaitAsync(TimeSpan.FromSeconds(5));
        var fourth = gate.EnterAsync("c_a", CancellationToken.None);
        await Task.Delay(100);
        Assert.False(fourth.IsCompleted);

        second.Dispose();
        using var fourthSlot = await fourth.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task A_full_class_queue_does_not_block_other_classes_and_the_process_wide_cap_still_applies()
    {
        var gate = new ClassWriteGate(totalLimit: 3, perClassLimit: 1);
        var busyClass = "c_busy";
        var otherClass = Enumerable.Range(0, 1000).Select(i => $"c_other{i}")
            .First(id => ClassWriteGate.StripeOf(id) != ClassWriteGate.StripeOf(busyClass));
        var held = await gate.EnterAsync(busyClass, CancellationToken.None);

        var waiting = gate.EnterAsync(busyClass, CancellationToken.None); // queues on its class, holds no process-wide slot
        await Task.Delay(100);
        Assert.False(waiting.IsCompleted);

        // A class that is not busy gets in at once, even though the busy class has requests waiting.
        using var other = await gate.EnterAsync(otherClass, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));

        // The process-wide cap is 3: the busy class's holder and "other" use two; a third class takes the last, a fourth waits.
        var thirdClass = Enumerable.Range(0, 1000).Select(i => $"c_third{i}")
            .First(id => new[] { busyClass, otherClass }.All(c => ClassWriteGate.StripeOf(c) != ClassWriteGate.StripeOf(id)));
        var fourthClass = Enumerable.Range(0, 1000).Select(i => $"c_fourth{i}")
            .First(id => new[] { busyClass, otherClass, thirdClass }.All(c => ClassWriteGate.StripeOf(c) != ClassWriteGate.StripeOf(id)));
        using var third = await gate.EnterAsync(thirdClass, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
        var fourth = gate.EnterAsync(fourthClass, CancellationToken.None);
        await Task.Delay(100);
        Assert.False(fourth.IsCompleted);

        other.Dispose();
        using var fourthSlot = await fourth.WaitAsync(TimeSpan.FromSeconds(5));
        held.Dispose();
        using var waitingSlot = await waiting.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Several_backend_instances_starting_together_on_a_fresh_database_all_start_and_migrate_once()
    {
        var connection = await CreateDatabaseAsync("startup", pooling: true);
        var hosts = Enumerable.Range(0, 4)
            .Select(_ => api.WithWebHostBuilder(b => b.UseSetting("ConnectionStrings:Default", connection)))
            .ToList();
        try
        {
            // Touching Server runs Program, including the startup migration; any instance that crashes throws here.
            await Task.WhenAll(hosts.Select(h => Task.Run(() => _ = h.Server)));

            await using var read = new NpgsqlConnection(connection);
            await read.OpenAsync();
            await using var cmd = new NpgsqlCommand("SELECT count(*) FROM \"__EFMigrationsHistory\"", read);
            Assert.Equal(2L, await cmd.ExecuteScalarAsync()); // InitialCreate + ActiveHoldConstraintAndSweepIndex, each once
        }
        finally
        {
            foreach (var h in hosts)
            {
                h.Dispose();
            }
        }
    }

    [Fact]
    public async Task A_full_class_or_a_repeat_applicant_is_refused_at_once_even_while_the_class_lock_is_held()
    {
        var classId = await CreateClassAsync(_client, 1);
        var student = NewStudentId();
        await ApplyAsync(_client, classId, student);

        await using var heldLock = await HoldClassLockAsync(classId);

        // Neither needs the lock: both must be answered while another transaction sits on it.
        using var patience = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var full = await _client.SendAsync(Request(HttpMethod.Post, $"/classes/{classId}/holds", NewStudentId()), patience.Token);
        var repeat = await _client.SendAsync(Request(HttpMethod.Post, $"/classes/{classId}/holds", student), patience.Token);
        var unknown = await _client.SendAsync(Request(HttpMethod.Post, "/classes/c_doesnotexist/holds", student), patience.Token);

        Assert.Equal(HttpStatusCode.Conflict, full.StatusCode);
        Assert.Equal("no_seats_available", (await ReadJsonAsync(full)).GetProperty("error").GetString());
        Assert.Equal(HttpStatusCode.Conflict, repeat.StatusCode);
        Assert.Equal("already_applied", (await ReadJsonAsync(repeat)).GetProperty("error").GetString());
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Fact]
    public async Task Granting_a_seat_waits_for_the_database_class_lock_but_confirming_does_not()
    {
        var classId = await CreateClassAsync(_client, 5);
        var owner = NewStudentId();
        var holdId = await ApplyAsync(_client, classId, owner);

        Task<HttpResponseMessage> apply;
        await using (var heldLock = await HoldClassLockAsync(classId))
        {
            apply = _client.SendAsync(Request(HttpMethod.Post, $"/classes/{classId}/holds", NewStudentId()));

            using var patience = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var confirm = await _client.SendAsync(Request(HttpMethod.Post, $"/holds/{holdId}/confirm", owner), patience.Token);
            Assert.Equal(HttpStatusCode.OK, confirm.StatusCode); // answered while the class row is locked by someone else

            await Task.Delay(700);
            Assert.False(apply.IsCompleted, "applying must wait while the class row is locked by another transaction");
        } // the lock is released here

        Assert.Equal(HttpStatusCode.Created, (await apply.WaitAsync(TimeSpan.FromSeconds(10))).StatusCode);
    }

    [Fact]
    public async Task An_application_waits_for_a_confirm_that_is_changing_a_lapsed_hold_and_then_counts_it()
    {
        // The race: a hold passes its expiry just as its owner's confirm runs. Applying must not count the hold as lapsed
        // (and give its seat away) if that confirm wins the row; it marks lapsed rows EXPIRED with an UPDATE, which has to
        // wait for the confirm's row lock and then see CONFIRMED. Simulated with a transaction that holds the hold's row.
        var classId = await CreateClassAsync(_client, 1);
        var lapsed = NewHold(classId, NewStudentId(), HoldStatus.HELD, DateTime.UtcNow.AddMinutes(-1));
        await api.WithDbAsync(async db =>
        {
            db.Holds.Add(lapsed);
            await db.SaveChangesAsync();
        });

        await using var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(api.ConnectionString) { Pooling = false }.ConnectionString);
        await connection.OpenAsync();
        await using var confirmInFlight = await connection.BeginTransactionAsync();
        await using (var lockRow = new NpgsqlCommand("SELECT 1 FROM holds WHERE id = @id FOR UPDATE", connection, confirmInFlight))
        {
            lockRow.Parameters.AddWithValue("id", lapsed.Id);
            await lockRow.ExecuteScalarAsync();
        }

        // The only seat looks free (the lapsed hold does not count), so the application passes the plain read and then
        // has to wait for the row lock when it tries to store the lapsed hold as EXPIRED.
        var apply = SendAsync(_client, HttpMethod.Post, $"/classes/{classId}/holds", NewStudentId());
        await Task.Delay(700);
        Assert.False(apply.IsCompleted, "applying must wait for the transaction that holds the lapsed hold's row");

        await using (var confirm = new NpgsqlCommand("UPDATE holds SET status = 'CONFIRMED' WHERE id = @id", connection, confirmInFlight))
        {
            confirm.Parameters.AddWithValue("id", lapsed.Id);
            await confirm.ExecuteNonQueryAsync();
        }
        await confirmInFlight.CommitAsync();

        var response = await apply.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode); // the class is full: the confirmed hold counts
        Assert.Equal("no_seats_available", (await ReadJsonAsync(response)).GetProperty("error").GetString());
        AssertSeatCounts(await GetClassAsync(_client, classId), null, total: 1, held: 0, confirmed: 1, available: 0);
    }

    [Fact]
    public async Task A_confirm_and_an_application_to_another_class_sent_during_a_big_burst_are_answered_in_seconds()
    {
        const int seats = 500;
        var classId = await CreateClassAsync(_client, seats);
        var otherClassId = await CreateClassAsync(_client, 20);
        var payer = NewStudentId();
        var holdId = await ApplyAsync(_client, classId, payer);

        var burst = Enumerable.Range(0, seats - 1).Select(_ => Task.Run(async () =>
            (await SendAsync(_client, HttpMethod.Post, $"/classes/{classId}/holds", NewStudentId())).StatusCode)).ToList();
        await Task.Delay(500); // let the queue build

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var confirm = await SendAsync(_client, HttpMethod.Post, $"/holds/{holdId}/confirm", payer);
        var confirmTook = watch.Elapsed;
        watch.Restart();
        var other = await SendAsync(_client, HttpMethod.Post, $"/classes/{otherClassId}/holds", NewStudentId());
        var otherTook = watch.Elapsed;

        Assert.Equal(HttpStatusCode.OK, confirm.StatusCode);
        Assert.True(confirmTook < TimeSpan.FromSeconds(6),
            $"confirm took {confirmTook.TotalSeconds:0.0}s behind {seats - 1} queued applications");
        Assert.Equal(HttpStatusCode.Created, other.StatusCode);
        Assert.True(otherTook < TimeSpan.FromSeconds(6),
            $"an application to an idle class took {otherTook.TotalSeconds:0.0}s behind a burst on another class");

        var statuses = await Task.WhenAll(burst);
        Assert.All(statuses, s => Assert.Equal(HttpStatusCode.Created, s));
        AssertSeatCounts(await GetClassAsync(_client, classId), null, total: seats, held: seats - 1, confirmed: 1, available: 0);
    }

    [Fact]
    public async Task Starting_against_a_server_where_the_database_does_not_exist_yet_creates_it()
    {
        var databaseName = $"fresh_{Guid.NewGuid():N}"; // deliberately not created
        var connection = new NpgsqlConnectionStringBuilder(api.ConnectionString) { Database = databaseName }.ConnectionString;
        using var host = api.WithWebHostBuilder(b => b.UseSetting("ConnectionStrings:Default", connection));

        _ = host.Server; // runs Program, including the startup migration

        await using var read = new NpgsqlConnection(connection);
        await read.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT to_regclass('public.holds') IS NOT NULL", read);
        Assert.True((bool)(await cmd.ExecuteScalarAsync())!);
    }

    // ---- migration ------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_unique_index_migration_cleans_up_rows_written_before_it_so_startup_cannot_fail()
    {
        var connection = await CreateDatabaseAsync("migration", pooling: false);
        await using var db = new AdmissionDbContext(new DbContextOptionsBuilder<AdmissionDbContext>().UseNpgsql(connection).Options);
        var migrator = db.GetService<IMigrator>();

        // The schema as F02-F05 left it, before the unique index.
        await migrator.MigrateAsync(db.Database.GetMigrations().First());
        var future = "now() + interval '10 minutes'";
        await db.Database.ExecuteSqlRawAsync("INSERT INTO classes (id, name, total_seats, created_at) VALUES ('c1', 'C', 50, now())");
        string Row(string id, string student, string status, string expires, int ageMinutes) =>
            $"INSERT INTO holds (id, class_id, student_id, status, created_at, expires_at) VALUES " +
            $"('{id}', 'c1', '{student}', '{status}', now() - interval '{ageMinutes} minutes', {expires})";
        var rows = new[]
        {
            Row("a_lapsed", "10000000000000001", "HELD", "now() - interval '1 minute'", 20),      // lapsed, never swept
            Row("a_live", "10000000000000001", "HELD", future, 1),                                 // + a fresh one: old apply path allowed this
            Row("b_old", "10000000000000002", "HELD", future, 5),                                  // two live HELD rows (a race)
            Row("b_new", "10000000000000002", "HELD", future, 1),
            Row("c_paid", "10000000000000003", "CONFIRMED", "now() - interval '1 hour'", 90),      // paid + a later HELD
            Row("c_held", "10000000000000003", "HELD", future, 1),
            Row("d_paid1", "10000000000000004", "CONFIRMED", "now() - interval '1 hour'", 90),     // two paid rows
            Row("d_paid2", "10000000000000004", "CONFIRMED", "now() - interval '1 hour'", 80),
            Row("e_only", "10000000000000005", "HELD", future, 1),                                 // untouched
        };
        foreach (var sql in rows)
        {
            await db.Database.ExecuteSqlRawAsync(sql);
        }

        await migrator.MigrateAsync(); // must not throw on the duplicates

        var statuses = new Dictionary<string, string>();
        await using (var read = new NpgsqlConnection(connection))
        {
            await read.OpenAsync();
            await using var cmd = new NpgsqlCommand("SELECT id, status FROM holds", read);
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                statuses[reader.GetString(0)] = reader.GetString(1);
            }
        }
        Assert.Equal("EXPIRED", statuses["a_lapsed"]);
        Assert.Equal("HELD", statuses["a_live"]);
        Assert.Equal("CANCELLED", statuses["b_old"]);
        Assert.Equal("HELD", statuses["b_new"]);
        Assert.Equal("CONFIRMED", statuses["c_paid"]);   // money was paid: that one is kept
        Assert.Equal("CANCELLED", statuses["c_held"]);
        Assert.Equal("CANCELLED", statuses["d_paid1"]);  // of two paid rows, the newer is kept
        Assert.Equal("CONFIRMED", statuses["d_paid2"]);
        Assert.Equal("HELD", statuses["e_only"]);
    }

    /// <summary>Creates an empty database on the test server and returns a connection string for it.</summary>
    private async Task<string> CreateDatabaseAsync(string prefix, bool pooling)
    {
        var databaseName = $"{prefix}_{Guid.NewGuid():N}";
        await using (var admin = new NpgsqlConnection(api.ConnectionString))
        {
            await admin.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE {databaseName}", admin);
            await create.ExecuteNonQueryAsync();
        }
        return new NpgsqlConnectionStringBuilder(api.ConnectionString) { Database = databaseName, Pooling = pooling }.ConnectionString;
    }

    private static HttpRequestMessage Request(HttpMethod method, string path, string studentId)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.TryAddWithoutValidation("X-Student-Id", studentId);
        return request;
    }

    /// <summary>Holds the class row lock in a separate transaction until the returned object is disposed (as another backend process would).</summary>
    private async Task<IAsyncDisposable> HoldClassLockAsync(string classId)
    {
        var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(api.ConnectionString) { Pooling = false }.ConnectionString);
        await connection.OpenAsync();
        var transaction = await connection.BeginTransactionAsync();
        await using (var cmd = new NpgsqlCommand("SELECT 1 FROM classes WHERE id = @id FOR UPDATE", connection, transaction))
        {
            cmd.Parameters.AddWithValue("id", classId);
            await cmd.ExecuteScalarAsync();
        }
        return new LockHolder(connection, transaction);
    }

    private sealed class LockHolder(NpgsqlConnection connection, NpgsqlTransaction transaction) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await transaction.RollbackAsync();
            await connection.DisposeAsync();
        }
    }
}
