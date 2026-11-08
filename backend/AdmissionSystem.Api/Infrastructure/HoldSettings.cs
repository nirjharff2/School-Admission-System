namespace AdmissionSystem.Api.Infrastructure;

/// <summary>Hold lifetime, from the <c>HOLD_DURATION_SECONDS</c> environment variable (default 600).</summary>
public sealed record HoldSettings(TimeSpan Duration)
{
    public const string ConfigKey = "HOLD_DURATION_SECONDS";
    public const int DefaultSeconds = 600;

    public static HoldSettings From(IConfiguration configuration)
    {
        var raw = configuration[ConfigKey];
        if (string.IsNullOrWhiteSpace(raw))
        {
            return new HoldSettings(TimeSpan.FromSeconds(DefaultSeconds));
        }
        if (!int.TryParse(raw, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var seconds)
            || seconds < 1)
        {
            throw new InvalidOperationException($"{ConfigKey} must be a whole number of seconds, at least 1 (got '{raw}').");
        }
        return new HoldSettings(TimeSpan.FromSeconds(seconds));
    }
}
