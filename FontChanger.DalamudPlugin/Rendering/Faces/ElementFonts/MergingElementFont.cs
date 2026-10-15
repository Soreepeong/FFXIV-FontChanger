using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using FFXIVClientStructs.FFXIV.Component.GUI;

using FontChanger.DalamudPlugin.Rendering.Faces.ElementFonts.MergingHelpers;
using FontChanger.DalamudPlugin.Rendering.Rasterization;
using FontChanger.Presets.Enums;
using FontChanger.Presets.Records;

namespace FontChanger.DalamudPlugin.Rendering.Faces.ElementFonts;

internal sealed unsafe class MergingElementFont(
    IElementFont baseFont,
    FaceElement def,
    GlyphMergingStruct merging,
    Func<float, float, IMergeTextFont> textFont,
    GlyphRasterizer rasterizer,
    OutlineElementFont? textOutline) : IElementFont
{
    public IEnumerable<OutlineElementFont> OutlineFonts => new[] { baseFont as OutlineElementFont, textOutline }.OfType<OutlineElementFont>();

    public bool Has(Rune rune, AtkFontManager.Font* game) => merging.Glyphs.ContainsKey(rune);

    public IEnumerable<Rune> Codepoints => merging.Glyphs.Keys;

    public (int Ascent, int LineHeight) GetLineMetrics(float px, AtkFontManager.Font* game) => baseFont.GetLineMetrics(px, game);

    public bool TryGetBaseline(uint tag, float px, out float baseline) => baseFont.TryGetBaseline(tag, px, out baseline);

    public RasterGlyph? Rasterize(Rune rune, float px)
    {
        if (!merging.Glyphs.TryGetValue(rune, out var glyph))
            return null;

        return GlyphMerger.Draw(
            glyph.Mapping,
            glyph.Text,
            merging,
            def.Size > 0 ? px / def.Size : 1,
            px,
            def.Size,
            baseFont.GetLineMetrics(px, null).Ascent,
            textFont,
            glyph.Mapping.Shape == MergeShape.Glyph ? baseFont.Rasterize(rune, px) : null,
            rasterizer.Images,
            rasterizer.FreeType);
    }

    public float GetAdvance(Rune rune, float px) => baseFont.GetAdvance(rune, px);

    public void Dispose()
    {
        baseFont.Dispose();
        if (!ReferenceEquals(textOutline, baseFont))
            textOutline?.Dispose();
    }
}
