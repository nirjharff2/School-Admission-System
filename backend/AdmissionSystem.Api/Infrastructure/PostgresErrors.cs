using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AdmissionSystem.Api.Infrastructure;

public static class PostgresErrors
{
    /// <summary>True if <paramref name="exception"/> is PostgreSQL rejecting a duplicate on the named unique index/constraint.</summary>
    public static bool IsUniqueViolation(Exception exception, string constraintName) =>
        exception is DbUpdateException { InnerException: PostgresException pg }
        && pg.SqlState == PostgresErrorCodes.UniqueViolation
        && pg.ConstraintName == constraintName;
}
