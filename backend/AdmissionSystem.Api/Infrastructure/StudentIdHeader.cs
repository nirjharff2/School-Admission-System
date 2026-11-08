namespace AdmissionSystem.Api.Infrastructure;

/// <summary>Reads and validates the <c>X-Student-Id</c> header (the 17-digit birth registration number).</summary>
public static class StudentIdHeader
{
    public const string Name = "X-Student-Id";
    public const int Length = 17;

    public static string Require(HttpRequest request)
    {
        var values = request.Headers[Name];
        if (values.Count == 0 || string.IsNullOrWhiteSpace(values[0]))
        {
            throw ApiException.BadRequest(ErrorCodes.StudentIdRequired,
                $"Enter the student's {Length}-digit birth registration number first.");
        }

        var value = values[0]!.Trim();
        if (values.Count > 1 || !IsValid(value))
        {
            throw ApiException.BadRequest(ErrorCodes.InvalidStudentId,
                $"The birth registration number must be exactly {Length} digits (0-9 only).");
        }
        return value;
    }

    // Explicit ASCII check: char.IsDigit and \d also accept digits from other scripts.
    private static bool IsValid(string value) => value.Length == Length && value.All(c => c is >= '0' and <= '9');
}
