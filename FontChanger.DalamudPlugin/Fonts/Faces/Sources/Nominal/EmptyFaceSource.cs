using System.Collections.Generic;
using System.Text;

using FontChanger.DalamudPlugin.Fonts.Records;
using FontChanger.DalamudPlugin.Utilities;

namespace FontChanger.DalamudPlugin.Fonts.Faces.Sources.Nominal;

/// <summary>Port of xivres empty_fixed_size_font; <c>ascent</c> and <c>lineHeight</c> are in pixels at <c>size</c>.</summary>
internal sealed class EmptyFaceSource(float size, float ascent, float lineHeight) : NominalFaceSource
{
    public override IEnumerable<Rune> Codepoints => [];

    public override uint GetGlyphId(Rune rune) => 0;

    public override float GetAdvance(float px, uint glyphId) => 0;

    public override (int Ascent, int LineHeight) GetLineMetrics(float px) =>
        size > 0 ? ((int)Misc.StdRound(ascent * px / size), (int)Misc.StdRound(lineHeight * px / size)) : (0, 0);

    protected override RasterGlyph? RasterizeGlyph(float px, uint glyphId) => null;
}
