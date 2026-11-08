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
        const int seats = 400;
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
    public void Only_timeouts_and_server_capacity_errors_count_as_overload()
    {
        PostgresException Pg(string sqlState) => new("x", "ERROR", "ERROR", sqlState);

        Assert.True(PostgresErrors.IsOverload(new TimeoutException()));
        Assert.True(PostgresErrors.IsOverload(new NpgsqlException("The connection pool has been exhausted", new TimeoutException())));
        Assert.True(PostgresErrors.IsOverload(new DbUpdateException("save", Pg(PostgresErrorCodes.LockNotAvailable))));
        Assert.True(PostgresErrors.IsOverload(Pg(PostgresErrorCodes.TooManyConnections)));
        Assert.True(PostgresErrors.IsOverload(Pg(PostgresErrorCodes.QueryCanceled)));
        Assert.False(PostgresErrors.IsOverload(new DbUpdateException("save", Pg(PostgresErrorCodes.UniqueViolation))));
        Assert.False(PostgresErrors.IsOverload(new InvalidOperationException("boom")));
    }

    [Fact]
    public async Task The_gate_admits_only_its_limit_at_a_time_and_waiting_honours_cancellation()
    {
        var gate = new ClassWriteGate(2);
        var first = await gate.EnterAsync(CancellationToken.None);
        var second = await gate.EnterAsync(CancellationToken.None);

        var third = gate.EnterAsync(CancellationToken.None);
        await Task.Delay(100);
        Assert.False(third.IsCompleted);

        using var cts = new CancellationTokenSource(100);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => gate.EnterAsync(cts.Token));

        first.Dispose();
        first.Dispose(); // releasing twice must not hand out an extra slot
        using var thirdSlot = await third.WaitAsync(TimeSpan.FromSeconds(5));
        var fourth = gate.EnterAsync(CancellationToken.None);
        await Task.Delay(100);
        Assert.False(fourth.IsCompleted);

        second.Dispose();
        using var fourthSlot = await fourth.WaitAsync(TimeSpan.FromSeconds(5));
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
}
