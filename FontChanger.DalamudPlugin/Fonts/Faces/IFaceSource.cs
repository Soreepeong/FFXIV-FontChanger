using System;
using System.Collections.Generic;
using System.Text;

using FontChanger.DalamudPlugin.Fonts.Enums;
using FontChanger.DalamudPlugin.Fonts.Records;

namespace FontChanger.DalamudPlugin.Fonts.Faces;

/// <summary>
/// As xivres fixed_size_font, before <see cref="MaterializedFontElement.Wrap"/> places the glyphs: sizes are the element's,
/// and glyphs are relative to the pen on the baseline.
/// </summary>
internal interface IFaceSource : IDisposable
{
    IEnumerable<Rune> Codepoints { get; }

    /// <summary>A DWRITE_MEASURING_MODE shared by the whole font, so that its elements' text lines up; ignored without DirectWrite.</summary>
    int MeasureMode { set { } }

    /// <summary>Glyph IDs are glyph indices for outline fonts and codepoints otherwise; 0 is no glyph.</summary>
    uint GetGlyphId(Rune rune);

    /// <summary>Before the source's transformation and emboldening, as layout places it.</summary>
    float GetAdvance(float px, uint glyphId);

    /// <summary>With the source's transformation and emboldening.</summary>
    float GetScreenAdvance(float px, in GlyphRun run)
    {
        var sum = 0f;
        foreach (var a in run.Advances)
            sum += a;
        return sum;
    }

    (int Ascent, int LineHeight) GetLineMetrics(float px);

    /// <summary>From the BASE table, in pixels above the glyphs' origin.</summary>
    bool TryGetBaseline(float px, Baseline baseline, out float y);

    /// <summary><c>Real</c> is false if the source has no italic and returns itself.</summary>
    (IFaceSource Source, bool Real) GetItalic() => (this, false);

    /// <summary>Itself if it has no bolder face.</summary>
    IFaceSource GetBold() => this;

    /// <summary>Clusters are in logical order and stop before the first one lacking a glyph; returns the chars shaped, at a cluster boundary.</summary>
    int Shape(float px, ReadOnlySpan<char> text, ScriptToken script, List<ShapedCluster> clusters);

    RasterGlyph? Rasterize(float px, in GlyphRun run, in RasterizeOptions options = default);
}
