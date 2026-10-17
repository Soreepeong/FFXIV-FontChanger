using FontChanger.DalamudPlugin.Rendering.Rasterization;
using FontChanger.DalamudPlugin.Rendering.Records;
using FontChanger.Presets.Records;
using FontChanger.Presets.Utilities;

using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;

namespace FontChanger.DalamudPlugin.Rendering.Faces.Sources.Outline;

/// <summary>A font's glyphs drawn with DirectWrite (xivres directwrite_fixed_size_font), measured as the face measures.</summary>
internal sealed unsafe class DirectWriteFaceSource(FontServices services, FaceElement def, LookupFont font, string faceName)
    : OutlineFaceSource(services, def, font, faceName)
{
    protected override RasterGlyph DrawRun(
        float size,
        ushort* glyphs,
        float* advances,
        DWRITE_GLYPH_OFFSET* offsets,
        uint count,
        float originX,
        int advance,
        Matrix2x2 transform) =>
        this.Services.DirectWrite.RasterizeRun(
            this.Font.GetFace(size),
            size,
            glyphs,
            advances,
            offsets,
            count,
            originX,
            advance,
            this.Def.DirectWrite with { MeasureMode = this.MeasureMode },
            transform);

    protected override RECT MeasureRun(float size, ushort glyph, DWRITE_GLYPH_OFFSET offset, Matrix2x2 transform)
    {
        var advance = 0f;
        return this.Services.DirectWrite.GetRunBounds(
            this.Font.GetFace(size),
            size,
            &glyph,
            &advance,
            &offset,
            1,
            this.Def.DirectWrite with { MeasureMode = this.MeasureMode },
            transform);
    }
}
