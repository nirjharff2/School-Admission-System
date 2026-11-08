using System.Globalization;

namespace AdmissionSystem.Api.Infrastructure;

/// <summary>Reads a whole-number-of-seconds setting (at least 1) from configuration, failing fast on bad input.</summary>
public static class SecondsSetting
{
    public static TimeSpan Read(IConfiguration configuration, string key, int defaultSeconds)
    {
        var raw = configuration[key];
        if (string.IsNullOrWhiteSpace(raw))
        {
            return TimeSpan.FromSeconds(defaultSeconds);
        }
        if (!int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) || seconds < 1)
        {
            throw new InvalidOperationException($"{key} must be a whole number of seconds, at least 1 (got '{raw}').");
        }
        return TimeSpan.FromSeconds(seconds);
    }
}
