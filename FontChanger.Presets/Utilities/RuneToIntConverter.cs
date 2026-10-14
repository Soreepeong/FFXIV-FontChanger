using System;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FontChanger.Presets.Utilities;

public class RuneToIntConverter : JsonConverter<Rune>
{
    public override Rune Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        new(reader.GetInt32());

    public override void Write(Utf8JsonWriter writer, Rune value, JsonSerializerOptions options) =>
        writer.WriteNumberValue(value.Value);
}
