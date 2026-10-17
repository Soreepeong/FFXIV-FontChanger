using System.Collections.Generic;
using System.Text;

using FontChanger.DalamudPlugin.Rendering.Rasterization;

namespace FontChanger.DalamudPlugin.Rendering.Faces.Sources.Nominal;

/// <summary>A source of no glyphs (xivres empty_fixed_size_font), with the line metrics it is given at a size.</summary>
internal sealed class EmptyFaceSource(float size, float ascent, float lineHeight) : NominalFaceSource
{
    public override IEnumerable<Rune> Codepoints => [];

    public override uint GetGlyphId(Rune rune) => 0;

    public override float GetAdvance(float px, uint glyphId) => 0;

    public override (int Ascent, int LineHeight) GetLineMetrics(float px) =>
        size > 0 ? ((int)Rounding.Round(ascent * px / size), (int)Rounding.Round(lineHeight * px / size)) : (0, 0);

    public override RasterGlyph? RasterizeGlyph(float px, uint glyphId) => null;
}
