using System;
using System.Collections.Generic;

using FontChanger.DalamudPlugin.Configuration;

namespace FontChanger.DalamudPlugin.Fonts;

/// <summary>
/// Each game font family gets an atlas of its own, so that one family's text can't push another's out. AXIS, the family of most
/// of the UI's text, has its own page size and count.
/// </summary>
internal sealed class FamilyAtlases(Func<bool> supports4444) : IDisposable
{
    private readonly Dictionary<string, GlyphAtlas> _atlases = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Gets or sets the settings atlases made from now on are made with.</summary>
    public AtlasSettings Settings { get; set; } = new AtlasSettings().Clamped();

    /// <summary>Gets or sets whether atlases made from now on take subpixel coverage.</summary>
    public bool Subpixel { get; set; }

    public IEnumerable<GlyphAtlas> All => this._atlases.Values;

    /// <summary>Made on its first use.</summary>
    public GlyphAtlas Get(string family)
    {
        if (this._atlases.TryGetValue(family, out var atlas))
            return atlas;

        // FontSubpixelPS tells subpixel pages from the game's textures by their odd width. Grayscale pages get an even one.
        var s = this.Settings;
        var (width, height, maxPages) = string.Equals(family, "AXIS", StringComparison.OrdinalIgnoreCase)
                                            ? (s.AxisPageWidth, s.AxisPageHeight, s.AxisMaxPages)
                                            : (s.OtherPageWidth, s.OtherPageHeight, s.OtherMaxPages);
        if (this.Subpixel == (width % 2 == 0))
            width--;
        var format = s.Format == AtlasTextureFormat.Bgra4444 && supports4444() ? AtlasTextureFormat.Bgra4444 : AtlasTextureFormat.Bgra8888;
        atlas = new(width, height, maxPages, family, this.Subpixel, format);
        this._atlases.Add(family, atlas);
        if (!atlas.TryEnsurePages(1))
            throw new InvalidOperationException("Could not add an atlas page.");
        return atlas;
    }

    public void Upload()
    {
        foreach (var atlas in this._atlases.Values)
            atlas.Upload();
    }

    /// <summary>Keeps the pages, which copies refer to.</summary>
    public void ClearAll()
    {
        foreach (var atlas in this._atlases.Values)
            atlas.Clear();
    }

    /// <summary>The next <see cref="Get"/> makes new ones.</summary>
    public void Dispose()
    {
        foreach (var atlas in this._atlases.Values)
            atlas.Dispose();
        this._atlases.Clear();
    }
}
