using System;

using FontChanger.Presets.Records;

namespace FontChanger.DalamudPlugin.Configuration;

[Serializable]
public sealed record FamilyFont
{
    public string Name { get; init; } = string.Empty;

    public int Weight { get; init; } = 400;

    public int Stretch { get; init; } = 5;

    public int Style { get; init; }

    public LookupStruct ToLookup() => LookupStruct.Of(this.Name) with { Weight = this.Weight, Stretch = this.Stretch, Style = this.Style };
}
