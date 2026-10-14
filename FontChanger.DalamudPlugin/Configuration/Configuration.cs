using System;
using System.Collections.Generic;

using Dalamud.Configuration;

namespace FontChanger.DalamudPlugin.Configuration;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    public Dictionary<string, List<FontSource>> FamilySources { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public HashSet<string> DisabledFamilies { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public bool SystemFallback { get; set; } = true;

    public EdgeSettings Edge { get; set; }

    public SubpixelMode Subpixel { get; set; } = SubpixelMode.Off;

    public AtlasSettings Atlas { get; set; }

    public bool ConfigWindowOpen { get; set; } = true;
}
