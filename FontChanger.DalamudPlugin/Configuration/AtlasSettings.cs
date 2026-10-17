using System;

using FontChanger.DalamudPlugin.Rendering;

namespace FontChanger.DalamudPlugin.Configuration;

public readonly record struct AtlasSettings(
    int AxisPageWidth = 4096,
    int AxisPageHeight = 4096,
    int AxisMaxPages = 4,
    int OtherPageWidth = 2048,
    int OtherPageHeight = 2048,
    int OtherMaxPages = 2,
    AtlasTextureFormat Format = AtlasTextureFormat.Bgra8888)
{
    // Without it, new() gives zeros rather than the defaults above.
    public AtlasSettings() : this(4096)
    {
    }

    private const int MinPageSide = 256;
    private const int MaxPageSide = 4096;

    public AtlasSettings Clamped() => new()
    {
        AxisPageWidth = Math.Clamp(this.AxisPageWidth, MinPageSide, MaxPageSide),
        AxisPageHeight = Math.Clamp(this.AxisPageHeight, MinPageSide, MaxPageSide),
        AxisMaxPages = Math.Clamp(this.AxisMaxPages, 1, GlyphAtlas.MaxTextureSlots),
        OtherPageWidth = Math.Clamp(this.OtherPageWidth, MinPageSide, MaxPageSide),
        OtherPageHeight = Math.Clamp(this.OtherPageHeight, MinPageSide, MaxPageSide),
        OtherMaxPages = Math.Clamp(this.OtherMaxPages, 1, GlyphAtlas.MaxTextureSlots),
        Format = Enum.IsDefined(this.Format) ? this.Format : AtlasTextureFormat.Bgra8888,
    };
}
