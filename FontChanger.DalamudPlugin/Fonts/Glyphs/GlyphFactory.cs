using System;

using FFXIVClientStructs.FFXIV.Component.GUI;
using FFXIVClientStructs.Interop;

using FontChanger.DalamudPlugin.Configuration;
using FontChanger.DalamudPlugin.Fonts.Records;
using FontChanger.DalamudPlugin.Game;
using FontChanger.DalamudPlugin.Utilities;

namespace FontChanger.DalamudPlugin.Fonts.Glyphs;

/// <summary>Makes glyphs, whose pixels are placed only once they are drawn.</summary>
/// <remarks>Every glyph returned is held once by the caller, unless it is null.</remarks>
internal sealed unsafe class GlyphFactory(GlyphStore store, FrameState state)
{
    public static int GetEdgeMargin(EdgeSettings edge, float px) => Math.Max(0, (int)MathF.Ceiling(edge.GetReach(px)) - 1);

    public int GetEdgeMargin(SizedFont sized) => GetEdgeMargin(state.Edge, sized.Px);

    /// <summary>Adds empty margins for a wide edge, as the edge is only drawn within a glyph's box and the pixel around it.</summary>
    public RasterGlyph Finish(SizedFont sized, RasterGlyph r)
    {
        var m = this.GetEdgeMargin(sized);
        return m == 0 || r.Width == 0 ? r : r.Reboxed(r.Left - m, r.Top - m, r.Width + (2 * m), r.Height + (2 * m));
    }

    /// <summary>With <paramref name="source"/>, <paramref name="r"/> is blank and its pixels are read from the game's texture once placed.</summary>
    public AtkFontManager.Glyph* FromRaster(SizedFont sized, RasterGlyph r, uint utf8Value, GameTextureSource? source = null)
    {
        // The game has no left bearing, so overhanging ink moves right into the box. Past 255 wide, it is cut on the right.
        var boxLeft = Math.Min(r.Left, 0);
        var width = r.Width == 0 ? 0 : Math.Min(r.Left + r.Width - boxLeft, byte.MaxValue);

        // A box is a full line high, as the game's are, so that italics slant alike. Ink beyond the line grows the box.
        var inkTop = sized.Ascent + r.Top;
        var top = 0;
        var height = sized.LineHeight;
        if (width != 0)
        {
            top = Math.Clamp(inkTop, sbyte.MinValue, 0);
            height = Math.Min(Math.Max(sized.LineHeight, inkTop + r.Height) - top, byte.MaxValue);
        }

        var glyph = store.Allocate();
        glyph->Utf8Value = utf8Value;

        // One without a box stays here, so that it adds no draw from the blank slot.
        glyph->Packed = (0, 0, 0, 0);
        glyph->Width = (byte)width;
        glyph->Height = (byte)height;
        glyph->OffsetX = (sbyte)Math.Clamp(r.Advance - width, sbyte.MinValue, sbyte.MaxValue);
        glyph->OffsetY = (sbyte)top;

        // Text is measured far more than drawn (every text change, at the node's unscaled size too), so a glyph is placed only
        // once it is drawn.
        if (width != 0)
            store.CellOf(glyph).Pend(glyph, sized.Atlas, r, boxLeft, inkTop - top, source);
        return glyph;
    }

    /// <summary>Pixels of no ink that advance by <paramref name="advance"/>.</summary>
    public AtkFontManager.Glyph* Spacer(SizedFont sized, int advance, uint utf8Value = 0) => this.FromRaster(sized, new(advance, 0, 0, 0, 0, []), utf8Value);

    /// <summary>Null leaves the glyph to the game.</summary>
    public AtkFontManager.Glyph* FromCodepoint(SizedFont sized, uint utf8Value)
    {
        if (!GameUtf8.TryUnpack(utf8Value, out var rune))
            return null;
        var pick = sized.Pick([rune]);
        if (pick.Element is { DrawsGame: true })
            return null;
        return pick.Font.TryRasterize(rune, sized.Px, pick.Game, sized.Bold, out var r)
                   ? this.FromRaster(sized, this.Finish(sized, r), utf8Value)
                   : null;
    }

    /// <summary>
    /// From the largest of the sized font's game fonts that has it, copied into the atlas because the edge steps by texels of
    /// the width all of a font's textures share, which is the atlas's.
    /// </summary>
    /// <returns>Null if none has it.</returns>
    public AtkFontManager.Glyph* GameGlyphOf(SizedFont sized, uint utf8Value)
    {
        if (!GameUtf8.TryUnpack(utf8Value, out var rune))
            return null;
        foreach (var (_, font) in sized.Fonts)
        {
            var glyph = this.FromGameGlyph(sized, font, font.Value->FindGlyph(rune), utf8Value);
            if (glyph is not null)
                return glyph;
        }

        return null;
    }

    /// <summary>The game font's own glyph, which has a texture index of the game font's textures.</summary>
    public AtkFontManager.Glyph* Convert(SizedFont sized, Pointer<AtkFontManager.Font> font, Pointer<AtkFontManager.Glyph> game, uint utf8Value) =>
        this.FromGameGlyph(sized, font, game, utf8Value) is var glyph and not null
            ? glyph
            : this.Spacer(sized, (int)Misc.StdRound((game.Value->Width + game.Value->OffsetX) * GetGameScale(sized, font)), utf8Value);

    // 1 within the half-pixel rounding of copy sizes.
    private static float GetGameScale(SizedFont sized, AtkFontManager.Font* font) =>
        font->Size <= 0 || MathF.Abs(sized.Px - font->Size) <= 0.25f ? 1 : sized.Px / font->Size;

    private AtkFontManager.Glyph* FromGameGlyph(SizedFont sized, Pointer<AtkFontManager.Font> font, Pointer<AtkFontManager.Glyph> game, uint utf8Value)
    {
        if (game.IsNull)
            return null;
        var textureIndex = game.Value->TextureIndex;
        var texture = textureIndex < font.Value->TextureCount && textureIndex < AtkFontManager.Font.MaxTextures ? font.Value->Textures[textureIndex] : default;
        if (texture.IsNull || !TextureReader.CanRead(texture))
            return null;
        var scale = GetGameScale(sized, font);
        var top = game.Value->OffsetY - font.Value->Ascent;
        var (left, t, w, h) = RasterGlyph.ScaledBounds(0, top, game.Value->Width, game.Value->Height, scale, 0, 0);
        var r = new RasterGlyph((int)Misc.StdRound((game.Value->Width + game.Value->OffsetX) * scale), left, t, w, h, new byte[w * h]);
        var pixels = new GameTextureSource(texture, game.Value->X, game.Value->Y, game.Value->Width, game.Value->Height, game.Value->Channel, top, scale);

        // Its box starts at the pen, with room for the edge in its own empty columns. A margin left of it would move it right.
        var finished = this.Finish(sized, r);
        if (finished.Left < 0 && finished.Width + finished.Left > 0)
            finished = finished.Reboxed(0, finished.Top, finished.Width + finished.Left, finished.Height);
        return this.FromRaster(sized, finished, utf8Value, pixels);
    }
}
