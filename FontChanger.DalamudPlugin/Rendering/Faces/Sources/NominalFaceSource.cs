using System;
using System.Collections.Generic;
using System.Text;

using FontChanger.DalamudPlugin.Rendering.Rasterization;

namespace FontChanger.DalamudPlugin.Rendering.Faces.Sources;

/// <summary>
/// A source whose glyph IDs are codepoints, one glyph per codepoint: shaped as each glyph at its advance (and the
/// kerning with the next), and drawn one by one at whole pixels.
/// </summary>
internal abstract class NominalFaceSource : IFaceSource
{
    public abstract IEnumerable<Rune> Codepoints { get; }

    public abstract uint GetGlyphId(Rune rune);

    public abstract float GetAdvance(float px, uint glyphId);

    public abstract (int Ascent, int LineHeight) GetLineMetrics(float px);

    public virtual bool TryGetBaseline(float px, Baseline baseline, out float y)
    {
        y = 0;
        return false;
    }

    /// <summary>Draws a glyph at a size, relative to the pen on the baseline; null if there is none.</summary>
    public abstract RasterGlyph? RasterizeGlyph(float px, uint glyphId);

    /// <summary>Gets the kerning between two glyphs at a size, in pixels.</summary>
    public virtual float GetKerning(float px, uint leftGlyphId, uint rightGlyphId) => 0;

    /// <summary>
    /// Shapes text one glyph per codepoint, each a cluster, at its advance and the kerning with the next; stops before a
    /// codepoint it has no glyph of.
    /// </summary>
    public int Shape(float px, ReadOnlySpan<char> text, ScriptToken script, List<ShapedCluster> clusters)
    {
        var pen = 0f;
        var position = 0;
        ShapedCluster? last = null;
        while (position < text.Length)
        {
            if (Rune.DecodeFromUtf16(text[position..], out var rune, out var length) != System.Buffers.OperationStatus.Done)
                break;
            var id = this.GetGlyphId(rune);
            if (id == 0)
                break;

            // The kerning with this glyph goes to the last one's advance.
            if (last is not null)
            {
                var k = this.GetKerning(px, last.GlyphIds[0], id);
                last.Advances[0] += k;
                pen += k;
            }

            var advance = this.GetAdvance(px, id);
            last = new(this, [id], [advance], null, pen, position, position + length);
            clusters.Add(last);
            pen += advance;
            position += length;
        }

        return position;
    }

    /// <summary>
    /// Draws a run of glyphs one by one: each at its pen (rounded), merged, squeezed and given its advance as the options
    /// say.
    /// </summary>
    public RasterGlyph? Rasterize(float px, in GlyphRun run, in RasterizeOptions options = default)
    {
        RasterGlyph? glyph;
        if (run.GlyphIds.Length == 1)
        {
            glyph = this.RasterizeGlyph(px, run.GlyphIds[0]);
        }
        else
        {
            var pieces = new List<RasterGlyph>();
            var pen = 0f;
            for (var i = 0; i < run.GlyphIds.Length; i++)
            {
                if (this.RasterizeGlyph(px, run.GlyphIds[i]) is { } g)
                {
                    var offset = i < run.Offsets.Length ? run.Offsets[i] : default;
                    pieces.Add(g with
                    {
                        Left = g.Left + (int)Rounding.Round(pen + offset.Advance),
                        Top = g.Top - (int)Rounding.Round(offset.Ascender),
                    });
                }

                pen += run.Advances[i];
            }

            glyph = pieces.Count == 0 ? null : RasterGlyph.Merge(pieces, (int)Rounding.Round(pen));
        }

        if (glyph is not { } r)
            return null;
        if (options.SqueezeX != 1)
            r = r.SqueezedX(options.SqueezeX);
        return options.Advance is { } advance ? r with { Advance = advance } : r;
    }

    public virtual void Dispose()
    {
    }
}
