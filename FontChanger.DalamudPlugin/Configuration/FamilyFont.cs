using System;

using FontChanger.Presets.Records;

using Newtonsoft.Json;

namespace FontChanger.DalamudPlugin.Configuration;

[Serializable]
public sealed record FamilyFont
{
    public string FamilyName { get; init; } = string.Empty;

    // FamilyName's key before a rename; read but never written.
    [JsonProperty("Name", NullValueHandling = NullValueHandling.Ignore)]
    private string? LegacyName
    {
        get => null;
        init
        {
            if (this.FamilyName.Length == 0 && value is not null)
                this.FamilyName = value;
        }
    }

    public int Weight { get; init; } = 400;

    public int Stretch { get; init; } = 5;

    public int Style { get; init; }

    public LookupStruct ToLookup() => LookupStruct.Of(this.FamilyName) with { Weight = this.Weight, Stretch = this.Stretch, Style = this.Style };
}