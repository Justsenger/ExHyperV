using System.Text.Json;
using System.Text.Json.Serialization;

namespace ExHyperV.App.UI.Shared.Appearance;

internal sealed class ConsoleSessionModeConverter : JsonConverter<ConsoleSessionMode>
{
    public override ConsoleSessionMode Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            string? id = reader.GetString();
            if (string.Equals(id, "RdpEnhanced", StringComparison.OrdinalIgnoreCase) || id == "1")
                return ConsoleSessionMode.RdpEnhanced;
            if (string.Equals(id, "External", StringComparison.OrdinalIgnoreCase) || id == "2")
                return ConsoleSessionMode.External;
            return ConsoleSessionMode.RdpBasic;
        }
        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out int number) &&
            Enum.IsDefined((ConsoleSessionMode)number)) return (ConsoleSessionMode)number;
        reader.Skip();
        return ConsoleSessionMode.RdpBasic;
    }

    public override void Write(Utf8JsonWriter writer, ConsoleSessionMode value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());
}
