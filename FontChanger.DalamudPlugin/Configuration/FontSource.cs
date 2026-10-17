using System;

using FontChanger.Presets.Records;

using Newtonsoft.Json;

namespace FontChanger.DalamudPlugin.Configuration;

[Serializable]
public sealed record FontSource
{
    public bool Enabled { get; init; } = true;

    public FontSourceType Type { get; init; }

    public string Path { get; init; } = string.Empty;

    public FamilyFont? Font { get; init; }

    public bool MonospacedDigits { get; init; } = true;

    // Newtonsoft otherwise populates the existing default in place, which breaks if that default is ever shared (like FontAdjustments.None).
    [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
    public FontAdjustments Adjustments { get; init; } = new();
}