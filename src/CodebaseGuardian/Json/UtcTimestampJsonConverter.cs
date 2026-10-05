using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodebaseGuardian.Json;

/// <summary>Wire format for timestamps: ISO 8601 UTC, <c>yyyy-MM-ddTHH:mm:ss.fffZ</c>. Reads any ISO 8601 offset.</summary>
public sealed class UtcTimestampJsonConverter : JsonConverter<DateTimeOffset>
{
    public const string Format = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        DateTimeOffset.Parse(reader.GetString() ?? throw new JsonException("Expected an ISO 8601 timestamp."),
            CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.UtcDateTime.ToString(Format, CultureInfo.InvariantCulture));
}
