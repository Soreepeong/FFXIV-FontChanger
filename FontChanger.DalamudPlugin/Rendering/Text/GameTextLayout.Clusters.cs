using System;
using System.Text;

using FontChanger.DalamudPlugin.PluginInterface;
using FontChanger.DalamudPlugin.Rendering.Faces;
using FontChanger.DalamudPlugin.Rendering.Records;
using FontChanger.DalamudPlugin.Utilities;

namespace FontChanger.DalamudPlugin.Rendering.Text;

// Inside the namespace, so that Pointer<T> is XIVCS's over TerraFX's.

// Shaping the text into clusters, placed one after another by their advances on screen.
internal sealed unsafe partial class GameTextLayout
{
    /// <summary>
    /// Shapes the text being shaped into clusters: the code units in italics (<see cref="_textItalic"/>) with their
    /// sources' italics; with <paramref name="italicImages"/>, each single glyph shaped upright is then drawn with its
    /// font's italic, where there is one.
    /// </summary>
    private void Collect(int count, bool italicImages)
    {
        this._clusters.Clear();
        this._analyzer.Itemize(this._text.AsSpan(0, count), this._textRuns);

        var pen = 0f;
        foreach (var run in this._textRuns)
        {
            for (var start = run.Start; start < run.End;)
            {
                // A piece of one glyph source, in italics or not.
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

    /// <summary>
    /// Shapes a piece of the text whose characters go to one glyph source, of one script run, into clusters at
    /// <paramref name="pen"/> on: those its source lacks, and all of those the game draws or right-to-left ones, left to
    /// the per-character path.
    /// </summary>
    private void CollectPiece(
        FontReplacer.GlyphSource? glyphSource, int start, int end, TextRun run, bool inItalic, bool italicImages, ref float pen)
    {
        var element = glyphSource?.Element;
        var source = element?.Source;
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
                Plugin.Log.Error(ex, "Shaping with {family} failed; the text is laid out per character", element.Def.Lookup.Name);
                this._shaped.Clear();
                shapedLength = 0;
            }

            foreach (var shaped in this._shaped)
                this.AddCluster(glyphSource, source, px, shaped, position, realItalic, italicImages, ref pen);
            position += shapedLength;

            // What the source lacks: a grapheme left to the per-character path.
            if (position < end)
            {
                var length = this.NextGrapheme(position, end);
                this.AddMissing(position, position + length, pen);
                position += length;
            }
        }
    }

    /// <summary>
    /// Adds a cluster <paramref name="source"/> shaped at the pen, which it advances by its advance on screen: with its
    /// element's letter spacing, or its monospaced cell (made now, as its advance is). Kerning is what shaping moved the
    /// glyphs by besides their own advances (towards the next cluster); a monospaced cluster that drops it (xivres
    /// wrapping_fixed_size_font) drops that of the cluster before it too.
    /// </summary>
    private void AddCluster(
        FontReplacer.GlyphSource glyphSource,
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
        var real = realItalic;
        var textStart = textOffset + shaped.TextStart;
        var textEnd = textOffset + shaped.TextEnd;
        var kerning = source.GetScreenAdvance(px, new(glyphs, shaped.Advances, offsets)) - GetOwnWidth(source, px, glyphs, offsets);

        // In a node's italics, shaped upright: a glyph of one character is drawn with its font's italic, if it has one.
        if (italicImages && glyphs.Length == 1 &&
            Rune.DecodeFromUtf16(this._text.AsSpan(textStart, textEnd - textStart), out var rune, out var used) == System.Buffers.OperationStatus.Done &&
            used == textEnd - textStart &&
            source.GetItalic() is (var italic, true) &&
            italic.GetGlyphId(rune) is var id and not 0)
        {
            source = italic;
            glyphs = [id];
            offsets = null;
            real = true;
        }

        var width = source.GetScreenAdvance(px, new(glyphs, shaped.Advances, offsets));
        var mono = element.Def.Monospacing;
        var rawAdvance = mono is null ? 0 : (int)Misc.StdRound(width - kerning);
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
            RealItalic = real,
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
            // The cell's advance, kerning (unless dropped) after it.
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

    /// <summary>Gets the advance on screen of glyphs at their own advances, without what shaping moved them by.</summary>
    private static float GetOwnWidth(IFaceSource source, float px, uint[] glyphs, GlyphOffset[]? offsets)
    {
        Span<float> own = glyphs.Length <= 32 ? stackalloc float[glyphs.Length] : new float[glyphs.Length];
        for (var i = 0; i < glyphs.Length; i++)
            own[i] = source.GetAdvance(px, glyphs[i]);
        return source.GetScreenAdvance(px, new(glyphs, own, offsets));
    }

    /// <summary>Adds the graphemes of a piece left to the per-character path, at the pen (which they don't advance).</summary>
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
    /// Collects a run in a text node's italics: shaped in italics, at the italics' own positions, but spaced out evenly to
    /// end where the run shaped upright does (the game measures it upright, as it doesn't see the node's italics then).
    /// False, with nothing collected, if the two shapings don't make the same clusters (a ligature only one has), or a
    /// cluster is left to the game: then each character is drawn in italics at its upright place instead.
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

        // The difference is shared out after each cluster, the last ending where the upright run ends.
        var share = (float)(upright[n - 1].X1 - italic[n - 1].X1) / n;
        for (var i = 0; i < n; i++)
            this._clusters.Add(italic[i].MovedBy(share * i, share * (i + 1)));
        return true;
    }

    /// <summary>A cluster of a line: its glyphs and where they are, and the text it covers.</summary>
    private sealed record Cluster
    {
        /// <summary>The source its glyphs are drawn from; null if it is left to the per-character path.</summary>
        public IFaceSource? Source { get; init; }

        /// <summary>The size its source draws at: its element's.</summary>
        public float Px { get; init; }

        public uint[] GlyphIds { get; init; } = [];

        public float[] Advances { get; init; } = [];

        public GlyphOffset[]? Offsets { get; init; }

        /// <summary>The advance a monospaced cluster's cell is made from (its glyphs' own, in whole pixels); 0 for the others.</summary>
        public int RawAdvance { get; init; }

        /// <summary>The kerning (towards the next cluster) its advance has, in pixels on screen.</summary>
        public float Kerning { get; set; }

        /// <summary>The key of the cluster's coverage, once made (GetRaster).</summary>
        public RasterKey? RasterKey { get; set; }

        /// <summary>The fraction of a pixel the pen is at, in steps of a quarter.</summary>
        public float Origin { get; init; }

        /// <summary>The whole-pixel pen at the cluster's start and end.</summary>
        public int X0 { get; init; }

        public int X1 { get; set; }

        public int TextStart { get; init; }

        public int TextEnd { get; init; }

        /// <summary>Where the cluster's first character's glyph comes from.</summary>
        public FontReplacer.GlyphSource? GlyphSource { get; init; }

        /// <summary>Whether it is left to the per-character path: the game draws it, or its source lacks it.</summary>
        public bool Missing => this.Source is null;

        /// <summary>Whether its glyphs are a real italic, which the game must not shear.</summary>
        public bool RealItalic { get; init; }

        /// <summary>
        /// Gets the cluster moved right: its start by <paramref name="start"/> pixels, its end by <paramref name="end"/>; its
        /// coverage is made again at its new position.
        /// </summary>
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
