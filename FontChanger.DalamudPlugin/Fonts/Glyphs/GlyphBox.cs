using System;

using FFXIVClientStructs.FFXIV.Component.GUI;

using FontChanger.DalamudPlugin.Fonts.Records;

namespace FontChanger.DalamudPlugin.Fonts.Glyphs;

/// <summary>A raster fitted to what the game's glyphs can hold.</summary>
/// <remarks>
/// <ul>
/// <li>A glyph's width and height are bytes, and its offsets signed bytes.</li>
/// <li>A glyph has no left bearing, so its box starts at the pen or right of it.</li>
/// <li>A box is a full line high.</li>
/// </ul>
/// </remarks>
internal readonly unsafe struct GlyphBox
{
    private GlyphBox(int left, int top, int width, int height, int inkTop, int offsetX)
    {
        this.Left = left;
        this.Top = top;
        this.Width = width;
        this.Height = height;
        this.InkTop = inkTop;
        this.OffsetX = offsetX;
    }

    /// <summary>Gets the box's left relative to the pen, as the raster's is.</summary>
    public int Left { get; }

    /// <summary>Gets the box's top relative to the line's.</summary>
    public int Top { get; }

    public int Width { get; }

    public int Height { get; }

    /// <summary>Gets the ink's top row in the box.</summary>
    public int InkTop { get; }

    /// <summary>Gets what the glyph advances by past its width.</summary>
    public int OffsetX { get; }

    /// <summary>Gets a value indicating whether it has pixels to place.</summary>
    public bool HasBox => this.Width != 0;

    /// <summary>How far the ink reaches left of the pen, where a box can't follow it.</summary>
    /// <returns>0 if it doesn't, or if there is no ink.</returns>
    public static int InkLeftOfPen(in RasterGlyph r) => r.Width > 0 && r.Left < 0 ? -r.Left : 0;

    /// <param name="r">The raster, relative to the pen on the baseline.</param>
    /// <param name="ascent">The line's ascent.</param>
    /// <param name="lineHeight">The line's height.</param>
    public static GlyphBox Fit(in RasterGlyph r, int ascent, int lineHeight)
    {
        // The game has no left bearing, so overhanging ink moves right into the box. Past 255 wide, it is cut on the right.
        var left = Math.Min(r.Left, 0);
        var width = r.Width == 0 ? 0 : Math.Min(r.Left + r.Width - left, byte.MaxValue);

        // A box is a full line high, as the game's are, so that italics slant alike. Ink beyond the line grows the box.
        var inkTop = ascent + r.Top;
        var top = 0;
        var height = lineHeight;
        if (width != 0)
        {
            top = Math.Clamp(inkTop, sbyte.MinValue, 0);
            height = Math.Min(Math.Max(lineHeight, inkTop + r.Height) - top, byte.MaxValue);
        }

        return new(left, top, width, height, inkTop - top, Math.Clamp(r.Advance - width, sbyte.MinValue, sbyte.MaxValue));
    }

    public void WriteTo(AtkFontManager.Glyph* glyph)
    {
        glyph->Width = (byte)this.Width;
        glyph->Height = (byte)this.Height;
        glyph->OffsetX = (sbyte)this.OffsetX;
        glyph->OffsetY = (sbyte)this.Top;
    }
}
