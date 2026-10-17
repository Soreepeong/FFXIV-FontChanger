using FontChanger.DalamudPlugin.Rendering.Fonts;
using FontChanger.DalamudPlugin.Rendering.Rasterization;
using FontChanger.Presets.Enums;
using FontChanger.Presets.Records;
using FontChanger.Presets.Utilities;

using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;

namespace FontChanger.DalamudPlugin.Rendering.Faces.Sources.Outline;

/// <summary>A font's glyphs drawn with DirectWrite (xivres directwrite_fixed_size_font).</summary>
internal sealed unsafe class DirectWriteFaceSource(
    GlyphRasterizer rasterizer, FaceElement def, Renderer renderer, LookupFont font, byte[]? gamma, string faceName)
    : OutlineFaceSource(rasterizer, def, renderer, font, gamma, faceName)
{
    protected override RasterGlyph DrawRun(
        IDWriteFontFace5* face,
        float size,
        ushort* glyphs,
        float* advances,
        DWRITE_GLYPH_OFFSET* offsets,
        uint count,
        float originX,
        int advance,
        Matrix2x2 transform) =>
        this.DrawWithDirectWrite(face, size, glyphs, advances, offsets, count, originX, advance, transform);

    protected override RECT MeasureRun(IDWriteFontFace5* face, float size, ushort glyph, DWRITE_GLYPH_OFFSET offset, Matrix2x2 transform) =>
        this.MeasureWithDirectWrite(face, size, glyph, offset, transform);
}
