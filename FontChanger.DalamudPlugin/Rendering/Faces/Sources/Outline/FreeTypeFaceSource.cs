using FontChanger.DalamudPlugin.Rendering.Fonts;
using FontChanger.DalamudPlugin.Rendering.Rasterization;
using FontChanger.Presets.Enums;
using FontChanger.Presets.Records;
using FontChanger.Presets.Utilities;

using FreeTypeSharp;

using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;

namespace FontChanger.DalamudPlugin.Rendering.Faces.Sources.Outline;

/// <summary>
/// A font's glyphs drawn with FreeType (xivres freetype_fixed_size_font), with its emboldening, which advances them
/// further than DirectWrite lays them out. Glyphs of another face (glyph merging's texts' layouts may pick one) are drawn
/// with DirectWrite.
/// </summary>
/// <param name="freeTypeFace">The font's face in FreeType, owned by <see cref="FreeTypeFonts"/>.</param>
internal sealed unsafe class FreeTypeFaceSource(
    GlyphRasterizer rasterizer, FaceElement def, Renderer renderer, LookupFont font, FT_FaceRec_* freeTypeFace, byte[]? gamma, string faceName)
    : OutlineFaceSource(rasterizer, def, renderer, font, gamma, faceName)
{
    protected override float GetExtraAdvance(float emSize) => this.Font.Embolden * emSize;

    protected override RasterGlyph DrawRun(
        IDWriteFontFace5* face,
        float size,
        ushort* glyphs,
        float* advances,
        DWRITE_GLYPH_OFFSET* offsets,
        uint count,
        float originX,
        int advance,
        Matrix2x2 transform)
    {
        if (!this.SameFont(this.Font.GetFace(size), face))
            return this.DrawWithDirectWrite(face, size, glyphs, advances, offsets, count, originX, advance, transform);
        return this.Rasterizer.FreeType!.RasterizeRun(
            freeTypeFace,
            size,
            glyphs,
            advances,
            offsets,
            count,
            originX,
            advance,
            this.Def.FreeType,
            transform,
            this.Font.GetAxisValues(size),
            this.Font.Embolden);
    }

    protected override RECT MeasureRun(IDWriteFontFace5* face, float size, ushort glyph, DWRITE_GLYPH_OFFSET offset, Matrix2x2 transform)
    {
        if (!this.SameFont(this.Font.GetFace(size), face))
            return this.MeasureWithDirectWrite(face, size, glyph, offset, transform);

        var advance = 0f;
        var g = this.Rasterizer.FreeType!.RasterizeRun(
            freeTypeFace,
            size,
            &glyph,
            &advance,
            &offset,
            1,
            0,
            0,
            this.Def.FreeType,
            transform,
            this.Font.GetAxisValues(size),
            this.Font.Embolden);
        return new() { left = g.Left, top = g.Top, right = g.Left + g.Width, bottom = g.Top + g.Height };
    }
}
