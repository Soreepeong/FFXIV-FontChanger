using System;
using System.Collections.Generic;

using Dalamud.Configuration;

namespace FontChanger.DalamudPlugin.Configuration;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 2;

    public Dictionary<string, List<FontSource>> FamilySources { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public HashSet<string> DisabledFamilies { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public bool SystemFallback { get; set; } = true;

    public EdgeSettings Edge { get; set; } = new();

    public SubpixelMode Subpixel { get; set; } = SubpixelMode.Off;

    public SubpixelRenderingParameters ClearType { get; set; } = new();

    public AtlasSettings Atlas { get; set; } = new();

    public bool ConfigWindowOpen { get; set; } = true;

    /// <summary>Updates what an older version saved; true if anything changed.</summary>
    public bool Migrate()
    {
        if (this.Version >= 2)
            return false;

        // Version 1 saved zeros for edge and atlas settings never set: their defaults then. Edges couldn't be turned off, so
        // those set to other than the game's own are kept on.
        if (this.Edge == default)
            this.Edge = new();
        else
            this.Edge = this.Edge with { Enabled = this.Edge.Clamped() != new EdgeSettings() };
        if (this.Atlas == default)
            this.Atlas = new();
        this.Version = 2;
        return true;
    }
}
