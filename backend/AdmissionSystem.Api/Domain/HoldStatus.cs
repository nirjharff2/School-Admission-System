namespace AdmissionSystem.Api.Domain;

/// <summary>
/// Member names are upper-case on purpose: they are the exact wire and database values
/// required by the API contract (HELD | CONFIRMED | EXPIRED | CANCELLED).
/// </summary>
public enum HoldStatus
{
    HELD,
    CONFIRMED,
    EXPIRED,
    CANCELLED,
}
