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
    /// <summary>Gets the codepoints it has glyphs of; null if they can't be listed.</summary>
    IEnumerable<Rune>? Codepoints => null;

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
    bool TryGetBaseline(float px, Baseline baseline, out float y)
    {
        y = 0;
        return false;
    }

    /// <summary>
    /// Gets the source that draws its text in italics: its family's italic (<c>Real</c>), or itself, for the game to
    /// shear.
    /// </summary>
    (IFaceSource Source, bool Real) GetItalic() => (this, false);

    /// <summary>
    /// Shapes text from its start into clusters, in logical order, their pens from 0; stops before the first cluster it
    /// lacks a glyph of. Returns how many code units it shaped, always at a cluster boundary.
    /// </summary>
    int Shape(float px, ReadOnlySpan<char> text, ScriptToken script, List<ShapedCluster> clusters);

    /// <summary>Rasterizes a run (one glyph, or a shaped cluster) into one image; null if the source has none of its glyphs.</summary>
    RasterGlyph? Rasterize(float px, in GlyphRun run, in RasterizeOptions options = default);
}

/// <summary>The baselines of the BASE table a face aligns its elements by.</summary>
internal enum Baseline
{
    /// <summary><c>romn</c>.</summary>
    Roman,

    /// <summary><c>ideo</c>: the bottom of the ideographic em box.</summary>
    IdeographicBottom,

    /// <summary><c>idtp</c>: the top of the ideographic em box.</summary>
    IdeographicTop,

    /// <summary><c>icfb</c>: the bottom of the ideographic character face.</summary>
    IdeographicFaceBottom,

    /// <summary><c>icft</c>: the top of the ideographic character face.</summary>
    IdeographicFaceTop,
}

/// <summary>Glyphs of one source, with their advances as laid out and their offsets in pixels (empty: none).</summary>
internal readonly ref struct GlyphRun(ReadOnlySpan<uint> glyphIds, ReadOnlySpan<float> advances, ReadOnlySpan<GlyphOffset> offsets = default)
{
    public ReadOnlySpan<uint> GlyphIds { get; } = glyphIds;

    public ReadOnlySpan<float> Advances { get; } = advances;

    public ReadOnlySpan<GlyphOffset> Offsets { get; } = offsets;
}

/// <summary>A glyph's offset from where its advance puts it: along the line, and up (DWRITE_GLYPH_OFFSET).</summary>
internal readonly record struct GlyphOffset(float Advance, float Ascender);

/// <param name="OriginX">The fraction of a pixel the pen is at.</param>
/// <param name="Advance">The advance the glyph is given; null for its own, rounded.</param>
/// <param name="SqueezeX">Narrowed by monospacing: outline fonts draw it so, the others resample it.</param>
internal readonly record struct RasterizeOptions(float OriginX = 0, int? Advance = null, float SqueezeX = 1);

/// <summary>
/// A cluster: glyphs of one source, where its pen is from the start of the text it was shaped from, and the code units
/// it covers (relative to that text).
/// </summary>
/// <param name="RealItalic">Its glyphs are a real italic, which the game must not shear.</param>
internal sealed record ShapedCluster(
    IFaceSource Source,
    uint[] GlyphIds,
    float[] Advances,
    GlyphOffset[]? Offsets,
    float Pen,
    int TextStart,
    int TextEnd,
    bool RealItalic = false);

/// <summary>A script as the analyzer found it, opaque but to the shaping that pairs with it (DWRITE_SCRIPT_ANALYSIS).</summary>
internal readonly record struct ScriptToken(ushort Script, int Shapes);
