using System;

using Dalamud.Configuration;

namespace CustomFonts;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; }

    /// <summary>Gets or sets the path of the FontChanger preset to use; empty for the built-in face.</summary>
    public string PresetPath { get; set; } = string.Empty;

    /// <summary>Gets or sets whether characters the preset lacks are drawn with Windows' fallback fonts, instead of the game's.</summary>
    public bool SystemFallback { get; set; } = true;

    public EdgeSettings Edge { get; set; } = new();
}

/// <summary>
/// The edge outline's width at a text size <c>px</c>: <c>clamp(Scale * px, Min, Max)</c> pixels. The defaults are the
/// game's own edge, 1 px at every size.
/// </summary>
public readonly record struct EdgeSettings
{
    public EdgeSettings()
    {
    }

    /// <summary>Gets the width per pixel of text size (0: <see cref="Min"/> at every size).</summary>
    public float Scale { get; init; }

    public float Min { get; init; } = 1;

    public float Max { get; init; } = 1;

    public float GetWidth(float px) => Math.Clamp(this.Scale * px, this.Min, this.Max);

    /// <summary>Gets the settings within what the edge can do: 0.25 to 8 px.</summary>
    public EdgeSettings Clamped()
    {
        var min = Math.Clamp(this.Min, 0.25f, 8);
        return new() { Scale = Math.Clamp(this.Scale, 0, 1), Min = min, Max = Math.Clamp(this.Max, min, 8) };
    }
}
