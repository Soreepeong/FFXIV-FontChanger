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
    // Advances are those of the drawn glyphs, unless a source knows them otherwise: kept by size and glyph, with the
    // glyphs drawn to measure them until they are drawn for good.
    private readonly Dictionary<(float Px, uint Id), float> _advances = [];
    private readonly Dictionary<(float Px, uint Id), RasterGlyph?> _measured = [];

    public abstract IEnumerable<Rune> Codepoints { get; }

    public abstract uint GetGlyphId(Rune rune);

    public virtual float GetAdvance(float px, uint glyphId)
    {
        if (!this._advances.TryGetValue((px, glyphId), out var advance))
        {
            var glyph = this.RasterizeGlyph(px, glyphId);
            this._measured[(px, glyphId)] = glyph;
            advance = glyph?.Advance ?? 0;
            this._advances.Add((px, glyphId), advance);
        }

        return advance;
    }

    public abstract (int Ascent, int LineHeight) GetLineMetrics(float px);

    public virtual bool TryGetBaseline(float px, Baseline baseline, out float y)
    {
        y = 0;
        return false;
    }

    /// <summary>
    /// Shapes text one glyph per codepoint, each a cluster, at its advance and the kerning with the next; stops before a
    /// codepoint it has no glyph of.
    /// </summary>
    public int Shape(float px, ReadOnlySpan<char> text, ScriptToken script, List<ShapedCluster> clusters)
    {
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
                last.Advances[0] += this.GetKerning(px, last.GlyphIds[0], id);

            last = new([id], [this.GetAdvance(px, id)], null, position, position + length);
            clusters.Add(last);
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
            glyph = this.DrawGlyph(px, run.GlyphIds[0]);
        }
        else
        {
            var pieces = new List<RasterGlyph>();
            var pen = 0f;
            for (var i = 0; i < run.GlyphIds.Length; i++)
            {
                if (this.DrawGlyph(px, run.GlyphIds[i]) is { } g)
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

    /// <summary>Draws a glyph at a size, relative to the pen on the baseline; null if there is none.</summary>
    protected abstract RasterGlyph? RasterizeGlyph(float px, uint glyphId);

    /// <summary>Gets the kerning between two glyphs at a size, in pixels.</summary>
    protected virtual float GetKerning(float px, uint leftGlyphId, uint rightGlyphId) => 0;

    /// <summary>Draws a glyph, or takes it as it was drawn to measure its advance.</summary>
    private RasterGlyph? DrawGlyph(float px, uint glyphId) =>
        this._measured.Remove((px, glyphId), out var glyph) ? glyph : this.RasterizeGlyph(px, glyphId);
}
