using System;
using System.Collections.Generic;
using System.Text;

using FontChanger.DalamudPlugin.Rendering.Rasterization;

namespace FontChanger.DalamudPlugin.Rendering.Faces;

/// <summary>
/// The glyphs a face element draws (xivres fixed_size_font), at any size: drawn as the element's renderer, gamma and
/// transformation say, but not yet adjusted by the face (<see cref="MaterializedFontElement.Wrap"/>). Glyph IDs are the
/// source's own: a font's glyph indices for outline fonts, codepoints for the others; 0 is no glyph. Glyphs are placed
/// relative to the pen on the baseline.
/// </summary>
/// <remarks>Sizes are the element's: the face's drawn size scaled by the element's size.</remarks>
internal interface IFaceSource : IDisposable
{
    /// <summary>Gets the codepoints it has glyphs of.</summary>
    IEnumerable<Rune> Codepoints { get; }

    /// <summary>Gets a codepoint's glyph without shaping; 0 if there is none.</summary>
    uint GetGlyphId(Rune rune);

    /// <summary>Gets a glyph's advance as a layout places it, before the source's transformation and emboldening; 0 if none.</summary>
    float GetAdvance(float px, uint glyphId);

    /// <summary>Gets a run's advance on screen: its advances transformed, with emboldening.</summary>
    float GetScreenAdvance(float px, in GlyphRun run)
    {
        var sum = 0f;
        foreach (var a in run.Advances)
            sum += a;
        return sum;
    }

    /// <summary>Gets the line metrics at a size, in whole pixels.</summary>
    (int Ascent, int LineHeight) GetLineMetrics(float px);

    /// <summary>Gets a baseline of the BASE table, in pixels above the glyphs' origin; false if it has none.</summary>
    bool TryGetBaseline(float px, Baseline baseline, out float y);

    /// <summary>
    /// Gets the source that draws its text in italics: its family's italic (<c>Real</c>), or itself, for the game to
    /// shear.
    /// </summary>
    (IFaceSource Source, bool Real) GetItalic() => (this, false);

    /// <summary>
    /// Shapes text from its start into clusters of its glyphs, in logical order; stops before the first cluster it lacks a
    /// glyph of. Returns how many code units it shaped, always at a cluster boundary.
    /// </summary>
    int Shape(float px, ReadOnlySpan<char> text, ScriptToken script, List<ShapedCluster> clusters);

    /// <summary>Rasterizes a run (one glyph, or a shaped cluster) into one image; null if the source has none of its glyphs.</summary>
    RasterGlyph? Rasterize(float px, in GlyphRun run, in RasterizeOptions options = default);
}
