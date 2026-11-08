using System.Security.Cryptography;

namespace AdmissionSystem.Api.Infrastructure;

/// <summary>Prefixed random identifiers such as <c>c_3f9a1b7c5d2e4a68</c> (64 random bits, lower-case hex).</summary>
public static class IdGenerator
{
    public static string NewClassId() => New("c_");
    public static string NewHoldId() => New("h_");

    private static string New(string prefix) =>
        prefix + Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
}
