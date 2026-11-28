using System.Text.Json;
using System.Text.Json.Serialization;
using SAW.Domain.Common;

namespace SAW.API.Serialization;

// Timestamps use UTC with an explicit Z. Calendar dates use DateOnly and are
// deliberately unaffected by this converter.
public sealed class UtcDateTimeJsonConverter : JsonConverter<DateTime>
{
    // Preserve input Kind: legacy QC datetime-local requests are interpreted as
    // Vietnam time by their service; explicit offsets are normalized there.
    public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.GetDateTime();
    public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options) =>
        writer.WriteStringValue(VietnamTime.AsUtc(value));
}
