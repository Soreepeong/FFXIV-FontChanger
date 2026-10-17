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

        /// <summary>Gets a property that is an object; false if there is none, or it is something else.</summary>
        public bool TryGetObject(string name, out JsonElement value) =>
            json.TryGetPropertyOfKind(name, JsonValueKind.Object, out value);

        /// <summary>Gets a property that is an array; false if there is none, or it is something else.</summary>
        public bool TryGetArray(string name, out JsonElement value) =>
            json.TryGetPropertyOfKind(name, JsonValueKind.Array, out value);

        private bool TryGetPropertyOfKind(string name, JsonValueKind kind, out JsonElement value)
        {
            if (json is { ValueKind: JsonValueKind.Object } && json.TryGetProperty(name, out value) && value.ValueKind == kind)
                return true;
            value = default;
            return false;
        }
    }

    extension(JsonElement? json)
    {
        public float? GetPropertyNumberOrDefault(string name) => json?.GetPropertyNumberOrDefault(name);

        public float GetPropertyNumberOrDefault(string name, float fallback) => json.GetPropertyNumberOrDefault(name) ?? fallback;
    }
}
