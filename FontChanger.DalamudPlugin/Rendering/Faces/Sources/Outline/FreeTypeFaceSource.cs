using FontChanger.DalamudPlugin.Rendering.Rasterization;
using FontChanger.Presets.Records;
using FontChanger.Presets.Utilities;

using FreeTypeSharp;

using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;

namespace FontChanger.DalamudPlugin.Rendering.Faces.Sources.Outline;

/// <summary>
/// A font's glyphs drawn with FreeType (xivres freetype_fixed_size_font), with its emboldening, which advances them
/// further than DirectWrite lays them out.
/// </summary>
/// <param name="freeTypeFace">The font's face in FreeType, owned by <see cref="FreeTypeGlyphRasterizer"/>.</param>
internal sealed unsafe class FreeTypeFaceSource(FontServices services, FaceElement def, LookupFont font, FT_FaceRec_* freeTypeFace, string faceName)
    : OutlineFaceSource(services, def, font, faceName)
{
    protected override float GetExtraAdvance(float emSize) => this.Font.Embolden * emSize;

    protected override RasterGlyph DrawRun(
        float size,
        ushort* glyphs,
        float* advances,
        DWRITE_GLYPH_OFFSET* offsets,
        uint count,
        float originX,
        int advance,
        Matrix2x2 transform) =>
        this.Services.FreeType.RasterizeRun(
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

    protected override RECT MeasureRun(float size, ushort glyph, DWRITE_GLYPH_OFFSET offset, Matrix2x2 transform)
    {
        var advance = 0f;
        var g = this.DrawRun(size, &glyph, &advance, &offset, 1, 0, 0, transform);
        return new() { left = g.Left, top = g.Top, right = g.Left + g.Width, bottom = g.Top + g.Height };
    }
}
