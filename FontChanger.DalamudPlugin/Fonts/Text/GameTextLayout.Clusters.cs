using System;
using System.Buffers;
using System.Text;

using FontChanger.DalamudPlugin.Fonts.Faces;
using FontChanger.DalamudPlugin.Fonts.Records;
using FontChanger.DalamudPlugin.PluginInterface;
using FontChanger.DalamudPlugin.Utilities;

namespace FontChanger.DalamudPlugin.Fonts.Text;

// Clusters are placed one after another by their advances on screen (with the elements' transformations, emboldening,
// letter spacing and monospacing), not where shaping put them.
internal sealed unsafe partial class GameTextLayout
{
    private void Collect(int count, bool italicImages)
    {
        this._clusters.Clear();
        this._analyzer.Itemize(this._text.AsSpan(0, count), this._textRuns);

        var pen = 0f;
        foreach (var run in this._textRuns)
        {
            for (var start = run.Start; start < run.End;)
            {
                var source = this._textSource[start];
                var inItalic = this._textItalic[start];
                var end = start + 1;
                while (end < run.End && this._textSource[end] == source && this._textItalic[end] == inItalic)
                    end++;

                this.CollectPiece(source, start, end, run, inItalic, italicImages, ref pen);
                start = end;
            }
        }
    }

    private void CollectPiece(
        GlyphSource? glyphSource, int start, int end, TextRun run, bool inItalic, bool italicImages, ref float pen)
    {
        var element = glyphSource?.Element;
        var source = element?.Source;
        if (source is not null && this._shapingSized!.Bold)
            source = source.GetBold();
        var realItalic = false;
        if (source is not null && inItalic)
            (source, realItalic) = source.GetItalic();
        if (glyphSource is null || element is null || source is null || run.RightToLeft)
        {
            this.AddMissing(start, end, pen);
            return;
        }

        var px = element.GetPx(this._shapingSized!.Px);
        for (var position = start; position < end;)
        {
            this._shaped.Clear();
            int shapedLength;
            try
            {
                shapedLength = source.Shape(px, this._text.AsSpan(position, end - position), run.Script, this._shaped);
            }
            catch (Exception ex)
            {
                Plugin.Log.Error(ex, "Shaping with {family} failed; the text is laid out per character", element.Def.Lookup.FamilyName);
                this._shaped.Clear();
                shapedLength = 0;
            }

            foreach (var shaped in this._shaped)
                this.AddCluster(glyphSource, source, px, shaped, position, realItalic, italicImages, ref pen);
            position += shapedLength;

            // The source lacks this grapheme.
            if (position < end)
            {
                var length = this.NextGrapheme(position, end);
                this.AddMissing(position, position + length, pen);
                position += length;
            }
        }
    }

    private void AddCluster(
        GlyphSource glyphSource,
        IFaceSource source,
        float px,
        ShapedCluster shaped,
        int textOffset,
        bool realItalic,
        bool italicImages,
        ref float pen)
    {
        var element = glyphSource.Element!;
        var glyphs = shaped.GlyphIds;
        var offsets = shaped.Offsets;
        var textStart = textOffset + shaped.TextStart;
        var textEnd = textOffset + shaped.TextEnd;

        // What shaping moved the glyphs by besides their own advances, towards the next cluster.
        var kerning = source.GetScreenAdvance(px, new(glyphs, shaped.Advances, offsets)) - GetOwnWidth(source, px, glyphs, offsets);

        // In a node's italics, shaped upright: a glyph of one character is drawn with its font's italic, if it has one.
        if (italicImages && glyphs.Length == 1 &&
            Rune.DecodeFromUtf16(this._text.AsSpan(textStart, textEnd - textStart), out var rune, out var used) == OperationStatus.Done &&
            used == textEnd - textStart &&
            source.GetItalic() is (var italic, true) &&
            italic.GetGlyphId(rune) is var id and not 0)
        {
            source = italic;
            glyphs = [id];
            offsets = null;
            realItalic = true;
        }

        var width = source.GetScreenAdvance(px, new(glyphs, shaped.Advances, offsets));
        var mono = element.Def.Monospacing;
        var rawAdvance = mono is null ? 0 : (int)Misc.StdRound(width - kerning);

        // As in xivres's wrapping_fixed_size_font, a monospaced cluster that drops its kerning drops that of the cluster before it too.
        if (mono is { DropKerning: true } && rawAdvance > 0)
        {
            kerning = 0;
            if (this._clusters is [.., var previous] && previous.TextEnd == textStart && previous.GlyphSource == glyphSource && previous.Kerning != 0)
            {
                pen -= previous.Kerning;
                previous.X1 = (int)Misc.StdRound(pen);
                previous.Kerning = 0;
            }
        }

        var x0 = Misc.StdRound(pen);
        var cluster = new Cluster
        {
            Source = source,
            Px = px,
            GlyphIds = glyphs,
            Advances = shaped.Advances,
            Offsets = offsets,
            RealItalic = realItalic,
            RawAdvance = rawAdvance,
            Kerning = kerning,
            Origin = Misc.StdRound((pen - x0) * SubpixelSteps) / SubpixelSteps,
            X0 = (int)x0,
            TextStart = textStart,
            TextEnd = textEnd,
            GlyphSource = glyphSource,
        };

        if (mono is not null)
        {
            pen = cluster.X0 + this.GetRaster(cluster).Advance + kerning;
            cluster.X1 = (int)Misc.StdRound(pen);
        }
        else
        {
            var spacing = element.GetLetterSpacing(this._shapingSized!.Px);
            cluster.X1 = (int)Misc.StdRound(pen + width) + spacing;
            pen += width + spacing;
        }

        this._clusters.Add(cluster);
    }

    private static float GetOwnWidth(IFaceSource source, float px, uint[] glyphs, GlyphOffset[]? offsets)
    {
        Span<float> own = glyphs.Length <= 32 ? stackalloc float[glyphs.Length] : new float[glyphs.Length];
        for (var i = 0; i < glyphs.Length; i++)
            own[i] = source.GetAdvance(px, glyphs[i]);
        return source.GetScreenAdvance(px, new(glyphs, own, offsets));
    }

    private void AddMissing(int start, int end, float pen)
    {
        var x = (int)Misc.StdRound(pen);
        for (var t = start; t < end;)
        {
            var length = this.NextGrapheme(t, end);
            this._clusters.Add(new() { TextStart = t, TextEnd = t + length, X0 = x, X1 = x });
            t += length;
        }
    }

    /// <summary>
    /// Collects a run in a node's italics shaped in italics but spread out to end where it does upright, as the game
    /// measures it upright; false, with nothing collected, if the two shapings' clusters differ or any is left to the game.
    /// </summary>
    private bool CollectSpreadItalics(int count)
    {
        this._textItalic.AsSpan(0, count).Clear();
        this.Collect(count, false);
        var upright = this._clusters.ToArray();
        this._textItalic.AsSpan(0, count).Fill(true);
        this.Collect(count, false);
        var italic = this._clusters.ToArray();
        this._clusters.Clear();

        var n = italic.Length;
        if (n == 0 || n != upright.Length)
            return false;
        for (var i = 0; i < n; i++)
        {
            var (u, it) = (upright[i], italic[i]);
            if (u.TextStart != it.TextStart || u.TextEnd != it.TextEnd || u.Missing || it.Missing)
                return false;
        }

        var share = (float)(upright[n - 1].X1 - italic[n - 1].X1) / n;
        for (var i = 0; i < n; i++)
            this._clusters.Add(italic[i].MovedBy(share * i, share * (i + 1)));
        return true;
    }

    private sealed record Cluster
    {
        // Null if the cluster is left to the per-character path.
        public IFaceSource? Source { get; init; }

        // The element's size the source draws at.
        public float Px { get; init; }

        public uint[] GlyphIds { get; init; } = [];

        public float[] Advances { get; init; } = [];

        public GlyphOffset[]? Offsets { get; init; }

        // A monospaced cluster's own advance in whole pixels, which its cell is made from; 0 for the others.
        public int RawAdvance { get; init; }

        // In pixels on screen, towards the next cluster.
        public float Kerning { get; set; }

        public RasterKey? RasterKey { get; set; }

        // The fraction of a pixel the pen is at, in SubpixelSteps.
        public float Origin { get; init; }

        // Whole-pixel pen positions.
        public int X0 { get; init; }

        public int X1 { get; set; }

        public int TextStart { get; init; }

        public int TextEnd { get; init; }

        // Of the cluster's first character.
        public GlyphSource? GlyphSource { get; init; }

        public bool Missing => this.Source is null;

        // A real italic, which the game must not shear.
        public bool RealItalic { get; init; }

        public Cluster MovedBy(float start, float end)
        {
            var pen = this.X0 + this.Origin + start;
            var x0 = Misc.StdRound(pen);
            return this with
            {
                RasterKey = null,
                Origin = Misc.StdRound((pen - x0) * SubpixelSteps) / SubpixelSteps,
                X0 = (int)x0,
                X1 = (int)Misc.StdRound(this.X1 + end),
            };
        }
    }
}
