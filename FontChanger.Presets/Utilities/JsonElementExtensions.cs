using System.Text.Json;

namespace FontChanger.Presets.Utilities;

public static class JsonElementExtensions
{
    extension(JsonElement json)
    {
        public float? GetPropertyNumberOrDefault(string name) =>
            json is { ValueKind: JsonValueKind.Object } && json.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetSingle() : null;

        public float GetPropertyNumberOrDefault(string name, float fallback) =>
            json.GetPropertyNumberOrDefault(name) ?? fallback;

        public string GetPropertyStringOrDefault(string name, string fallback = "") =>
            json is { ValueKind: JsonValueKind.Object } && json.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString()!
                : fallback;

        public bool GetPropertyBoolOrDefault(string name, bool fallback = false) =>
            json is { ValueKind: JsonValueKind.Object } && json.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? v.ValueKind == JsonValueKind.True
                : fallback;
    }

    extension(JsonElement? json)
    {
        public float? GetPropertyNumberOrDefault(string name) => json?.GetPropertyNumberOrDefault(name);

        public float GetPropertyNumberOrDefault(string name, float fallback) => json.GetPropertyNumberOrDefault(name) ?? fallback;
    }
}
