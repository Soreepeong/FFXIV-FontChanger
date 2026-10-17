using System;
using System.Collections.Immutable;

using Dalamud.Configuration;

using Newtonsoft.Json;

namespace FontChanger.DalamudPlugin.Configuration;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    [JsonProperty(TypeNameHandling = TypeNameHandling.None)]
    public ImmutableDictionary<string, ImmutableList<FontSource>> FamilySources
    {
        get;
        set => field = value.WithComparers(StringComparer.OrdinalIgnoreCase);
    } = ImmutableDictionary.Create<string, ImmutableList<FontSource>>(StringComparer.OrdinalIgnoreCase);

    public ImmutableHashSet<string> DisabledFamilies
    {
        get;
        set => field = value.WithComparer(StringComparer.OrdinalIgnoreCase);
    } = ImmutableHashSet.Create<string>(StringComparer.OrdinalIgnoreCase);

    public bool SystemFallback { get; set; } = true;

    public EdgeSettings Edge { get; set; } = new();

    public SubpixelMode Subpixel { get; set; } = SubpixelMode.Off;

    public SubpixelRenderingParameters ClearType { get; set; } = new();

    public VerticalSubpixelRenderer VerticalSubpixel { get; set; } = VerticalSubpixelRenderer.FreeType;

    public AtlasSettings Atlas { get; set; } = new();

    public NamePlateMode NamePlates { get; set; } = NamePlateMode.BakedAtFullSize;

    public bool ConfigWindowOpen { get; set; } = true;
}

// DirectWrite has no vertical ClearType, so vertical layouts use FreeType's hinted LCD_V or unhinted ClearType of glyphs turned a quarter.