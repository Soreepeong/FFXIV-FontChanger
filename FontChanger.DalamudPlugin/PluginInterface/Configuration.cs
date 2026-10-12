using System;
using System.Collections.Generic;

using Dalamud.Configuration;

namespace CustomFonts;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; }

    /// <summary>Gets or sets the folder presets are chosen from.</summary>
    public string PresetFolder { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the presets in use per game font family (<c>AXIS</c>, <c>JupiterN</c>, ...), as paths relative to
    /// <see cref="PresetFolder"/>, in the order they were selected: of the family's faces, only those are used, and of a
    /// face in several, the last one's. A family without any (and a face its presets lack) uses the game's glyphs, with
    /// <see cref="SystemFallback"/> for the characters they lack.
    /// </summary>
    public Dictionary<string, List<string>> FamilyPresets { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets or sets the system font per game font family to draw its faces with, made as by
    /// <see cref="FontChanger.Presets.FaceFromFont"/>; over the family's presets.
    /// </summary>
    public Dictionary<string, FamilyFont> FamilyFonts { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets or sets whether a system font's digits are made monospaced: with its tabular figures (tnum) if it has them, else
    /// by putting each in a cell as wide as its 0.
    /// </summary>
    public bool MonospacedDigits { get; set; } = true;

    /// <summary>Gets or sets whether characters the preset lacks are drawn with Windows' fallback fonts, instead of the game's.</summary>
    public bool SystemFallback { get; set; } = true;

    public EdgeSettings Edge { get; set; } = new();

    public SubpixelMode Subpixel { get; set; } = SubpixelMode.System;

    /// <summary>Gets or sets whether the main window was open, to open it again when the plugin loads.</summary>
    public bool MainWindowOpen { get; set; }
}

/// <summary>Subpixel text: off, Windows' ClearType setting for the game's monitor, or a stripe order with Windows' tuning.</summary>
public enum SubpixelMode
{
    Off,
    System,
    Rgb,
    Bgr,
}

/// <summary>A system font: its family's name (English) and the face's weight, stretch and style (DWRITE_* values).</summary>
[Serializable]
public sealed record FamilyFont
{
    public string Name { get; init; } = string.Empty;

    public int Weight { get; init; } = 400;

    public int Stretch { get; init; } = 5;

    public int Style { get; init; }

    public LookupDef ToLookup() => LookupDef.Of(this.Name) with { Weight = this.Weight, Stretch = this.Stretch, Style = this.Style };
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

    /// <summary>
    /// Gets the softness k: 0 is solid with a 1 px antialiased rim; up to 1, solid to <c>(1 - k) * width</c>, then
    /// linear to 0 at the width; above 1, linear from the glyph to 0 at <c>k * width</c> (8 px at most), a glow.
    /// </summary>
    public float Softness { get; init; }

    public float GetWidth(float px) => Math.Clamp(this.Scale * px, this.Min, this.Max);

    /// <summary>Gets how far the edge reaches past the glyph, as the edge shader keeps it.</summary>
    public float GetReach(float px) => Math.Min(this.GetWidth(px) * Math.Max(this.Softness, 1), 8);

    /// <summary>Gets the settings within what the edge can do: 0.25 to 8 px, softness 0 to 8.</summary>
    public EdgeSettings Clamped()
    {
        var min = Math.Clamp(this.Min, 0.25f, 8);
        return new()
        {
            Scale = Math.Clamp(this.Scale, 0, 1),
            Min = min,
            Max = Math.Clamp(this.Max, min, 8),
            Softness = Math.Clamp(this.Softness, 0, EdgeShaderCode.MaxSoftness),
        };
    }
}
