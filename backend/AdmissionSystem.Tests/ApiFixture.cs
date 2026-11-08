using AdmissionSystem.Api.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;

namespace AdmissionSystem.Tests;

/// <summary>A throw-away PostgreSQL container plus the real API pointed at it, configured through environment-style settings.</summary>
public abstract class PostgresApiFactory(int holdDurationSeconds, int sweepIntervalSeconds)
    : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16-alpine").Build();

    public async Task InitializeAsync() => await _postgres.StartAsync();

    async Task IAsyncLifetime.DisposeAsync()
    {
        await base.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:Default", _postgres.GetConnectionString());
        builder.UseSetting("HOLD_DURATION_SECONDS", holdDurationSeconds.ToString());
        builder.UseSetting("EXPIRY_SWEEP_INTERVAL_SECONDS", sweepIntervalSeconds.ToString());
    }

    /// <summary>Runs <paramref name="action"/> with a DbContext on the test database (for seeding and inspection).</summary>
    public async Task WithDbAsync(Func<AdmissionDbContext, Task> action)
    {
        await using var scope = Services.CreateAsyncScope();
        await action(scope.ServiceProvider.GetRequiredService<AdmissionDbContext>());
    }
}

/// <summary>
/// The default API under test, shared by every test class through <see cref="ApiCollection"/>; tests isolate themselves
/// by creating their own classes. The sweeper is effectively off (hourly) so tests that seed a lapsed hold that is still
/// stored as HELD exercise the computed-status path instead of racing the sweeper.
/// </summary>
public sealed class ApiFixture() : PostgresApiFactory(HoldDurationSeconds, sweepIntervalSeconds: 3600)
{
    /// <summary>Hold lifetime the API under test is configured with.</summary>
    public const int HoldDurationSeconds = 300;
}

[CollectionDefinition(Name)]
public sealed class ApiCollection : ICollectionFixture<ApiFixture>
{
    public const string Name = "Api";
}

/// <summary>An API with 1-second holds and a 1-second sweeper, on its own database, for the expiry sweeper tests.</summary>
public sealed class SweeperFixture() : PostgresApiFactory(holdDurationSeconds: 1, sweepIntervalSeconds: 1);

[CollectionDefinition(Name)]
public sealed class SweeperCollection : ICollectionFixture<SweeperFixture>
{
    public const string Name = "Sweeper";
}
