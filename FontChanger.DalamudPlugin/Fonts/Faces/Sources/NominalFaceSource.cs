using System;
using System.Collections.Generic;
using System.Text;

using FontChanger.DalamudPlugin.Fonts.Enums;
using FontChanger.DalamudPlugin.Fonts.Records;
using FontChanger.DalamudPlugin.Utilities;

namespace FontChanger.DalamudPlugin.Fonts.Faces.Sources;

/// <summary>Glyph IDs are codepoints, one glyph each, placed at whole pixels.</summary>
internal abstract class NominalFaceSource : IFaceSource
{
    // Measuring an advance draws the glyph, which is kept so that drawing it for good needn't repeat that.
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

            if (last is not null)
                last.Advances[0] += this.GetKerning(px, last.GlyphIds[0], id);

            last = new([id], [this.GetAdvance(px, id)], null, position, position + length);
            clusters.Add(last);
            position += length;
        }

        return position;
    }

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
                        Left = g.Left + (int)Misc.StdRound(pen + offset.Advance),
                        Top = g.Top - (int)Misc.StdRound(offset.Ascender),
                    });
                }

                pen += run.Advances[i];
            }

            glyph = pieces.Count == 0 ? null : RasterGlyph.Merge(pieces, (int)Misc.StdRound(pen));
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

    protected abstract RasterGlyph? RasterizeGlyph(float px, uint glyphId);

    protected virtual float GetKerning(float px, uint leftGlyphId, uint rightGlyphId) => 0;

    private RasterGlyph? DrawGlyph(float px, uint glyphId) =>
        this._measured.Remove((px, glyphId), out var glyph) ? glyph : this.RasterizeGlyph(px, glyphId);
}
