namespace AdmissionSystem.Api.Infrastructure;

/// <summary>Hold lifetime, from the <c>HOLD_DURATION_SECONDS</c> environment variable (default 600).</summary>
public sealed record HoldSettings(TimeSpan Duration)
{
    public const string ConfigKey = "HOLD_DURATION_SECONDS";
    public const int DefaultSeconds = 600;

    public static HoldSettings From(IConfiguration configuration) =>
        new(SecondsSetting.Read(configuration, ConfigKey, DefaultSeconds));
}
