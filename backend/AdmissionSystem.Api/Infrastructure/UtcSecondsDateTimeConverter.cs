using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AdmissionSystem.Api.Infrastructure;

/// <summary>
/// Writes timestamps as ISO-8601 UTC with whole seconds and a trailing Z, e.g. <c>2026-11-01T04:10:00Z</c>,
/// exactly as in the API contract. Hold timestamps are created truncated to whole seconds, so nothing is lost.
/// </summary>
public sealed class UtcSecondsDateTimeConverter : JsonConverter<DateTime>
{
    private const string Format = "yyyy-MM-dd'T'HH:mm:ss'Z'";

    public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        DateTime.Parse(reader.GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

    public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToUniversalTime().ToString(Format, CultureInfo.InvariantCulture));
}
