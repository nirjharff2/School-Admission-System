using AdmissionSystem.Api.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;

namespace AdmissionSystem.Tests;

/// <summary>
/// Starts one throw-away PostgreSQL container and the real API pointed at it.
/// Shared by every test class through <see cref="ApiCollection"/>; tests isolate themselves by creating their own classes.
/// </summary>
public sealed class ApiFixture : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16-alpine").Build();

    public async Task InitializeAsync() => await _postgres.StartAsync();

    async Task IAsyncLifetime.DisposeAsync()
    {
        await base.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    /// <summary>Hold lifetime the API under test is configured with.</summary>
    public const int HoldDurationSeconds = 300;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:Default", _postgres.GetConnectionString());
        builder.UseSetting("HOLD_DURATION_SECONDS", HoldDurationSeconds.ToString());
    }

    /// <summary>Runs <paramref name="action"/> with a DbContext on the test database (for seeding and inspection).</summary>
    public async Task WithDbAsync(Func<AdmissionDbContext, Task> action)
    {
        await using var scope = Services.CreateAsyncScope();
        await action(scope.ServiceProvider.GetRequiredService<AdmissionDbContext>());
    }
}

[CollectionDefinition(Name)]
public sealed class ApiCollection : ICollectionFixture<ApiFixture>
{
    public const string Name = "Api";
}
