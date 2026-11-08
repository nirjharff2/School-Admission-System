using Npgsql;

namespace AdmissionSystem.Api.Infrastructure;

public static class DatabaseConnection
{
    /// <summary>
    /// Npgsql allows 100 pooled connections per process by default and PostgreSQL allows 100 in total, so two backend
    /// processes under a burst of simultaneous applications could ask for more than the server has and fail requests with
    /// "too many clients". Every application request holds its connection while it waits for the class row lock, so the
    /// pool must be smaller than the server limit divided by the number of backend instances. 30 leaves room for three.
    /// Requests beyond the pool size wait for a free connection instead of failing.
    /// An explicit "Maximum Pool Size" in the connection string wins.
    /// </summary>
    public const int DefaultMaxPoolSize = 30;

    private const string MaxPoolSizeKeyword = "Maximum Pool Size";

    /// <summary>The configured connection string (<c>ConnectionStrings__Default</c>) with the pool bounded; fails clearly if unset.</summary>
    public static string FromConfiguration(IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Default");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "No database connection string. Set the ConnectionStrings__Default environment variable.");
        }
        return WithBoundedPool(connectionString);
    }

    public static string WithBoundedPool(string connectionString)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        if (!builder.ShouldSerialize(MaxPoolSizeKeyword))
        {
            builder.MaxPoolSize = DefaultMaxPoolSize;
        }
        return builder.ConnectionString;
    }
}
