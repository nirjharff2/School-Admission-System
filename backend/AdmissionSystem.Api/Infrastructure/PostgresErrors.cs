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

    /// <summary>
    /// True if the failure means the database could not serve the request right now but the same request is expected to
    /// work if tried again: the connection pool stayed exhausted until its timeout, a command or lock wait timed out, the
    /// server refused the connection as full, or this transaction was chosen as the victim of a deadlock.
    /// </summary>
    public static bool IsOverload(Exception exception)
    {
        for (var e = exception; e is not null; e = e.InnerException)
        {
            switch (e)
            {
                case TimeoutException:
                case PostgresException { SqlState: PostgresErrorCodes.TooManyConnections or PostgresErrorCodes.LockNotAvailable
                                                   or PostgresErrorCodes.QueryCanceled or PostgresErrorCodes.DeadlockDetected }:
                    return true;
            }
        }
        return false;
    }
}
