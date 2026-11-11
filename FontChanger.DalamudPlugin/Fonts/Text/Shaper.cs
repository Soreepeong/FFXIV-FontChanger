using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

using FFXIVClientStructs.FFXIV.Component.GUI;
using FFXIVClientStructs.Interop;

using FontChanger.DalamudPlugin.Fonts.Enums;
using FontChanger.DalamudPlugin.Fonts.Faces;
using FontChanger.DalamudPlugin.Fonts.Records;
using FontChanger.DalamudPlugin.PluginInterface;
using FontChanger.DalamudPlugin.Utilities;

using Lumina.Text.Payloads;
using Lumina.Text.ReadOnly;

namespace FontChanger.DalamudPlugin.Fonts.Text;

/// <summary>Shapes a run of the game's text into a glyph per byte.</summary>
internal sealed unsafe class Shaper(DirectWriteTextAnalyzer analyzer, CellCache cells, Pointer<AtkFontManager.Glyph> emptyGlyph)
{
    /// <summary>The advance <see cref="Measure"/> gives a byte left to the game.</summary>
    public const int Missing = -1;

    // Graphemes longer than this are picked an element by their first runes.
    private const int MaxGraphemeRunes = 16;

    private readonly ShapingContext _ctx = new();

    // RasterizeConcurrently's, reused.
    private readonly List<Cluster> _pending = [];
    private readonly HashSet<RasterKey> _seen = [];

    /// <summary>Gets, per byte of the run last read, whether an italic macro has it in italics.</summary>
    public ReadOnlySpan<bool> ByteItalic => this._ctx.ByteItalic;

    /// <summary>Finds the run at <paramref name="p"/>, and if <paramref name="decode"/>, reads it for <see cref="Shape"/>.</summary>
    /// <returns>The run's count of UTF-16 code units if decoded, and its length in bytes in <paramref name="length"/>.</returns>
    public int ReadRun(byte* p, ItalicMode italic, bool decode, out int length)
    {
        var count = 0;
        var i = 0;
        var inItalic = italic != ItalicMode.Upright;
        while (i < ShapingContext.MaxRunBytes)
        {
            var b = p[i];
            if (b is 0 or 0x0A or 0x0D)
                break;

            if (b == ReadOnlySeString.Stx)
            {
                // Bold is laid out with another sized font.
                var macro = (MacroCode)p[i + 1];
                if (macro is MacroCode.NewLine or MacroCode.Bold || (macro == MacroCode.Italic && italic == ItalicMode.Images))
                    break;
                var total = GameUtf8.MacroLength(p + i);
                if (total == 0 || i + total > ShapingContext.MaxRunBytes)
                    break;

                // <italic>'s argument must be a plain integer (1 on, 0 off). Anything else ends the run.
                if (macro == MacroCode.Italic)
                {
                    if (!GameUtf8.TryReadInteger(p + i + 2, out var payload, out var n)
                        || !GameUtf8.TryReadInteger(p + i + 2 + n, out var on, out var onLength) || onLength != payload)
                        break;
                    inItalic = on != 0;
                }

                if (decode)
                    this._ctx.ByteItalic.AsSpan(i, total).Fill(inItalic);
                i += total;
                continue;
            }

            // Stepped as the game does.
            var charLength = GameUtf8.CharacterLength(p + i);
            if (charLength == 0 || i + charLength > ShapingContext.MaxRunBytes)
                break;
            if (!decode)
            {
                i += charLength;
                continue;
            }

            this._ctx.ByteItalic.AsSpan(i, charLength).Fill(inItalic);

            // Control characters and broken sequences shape as a replacement character, which keeps them per character.
            var r = Rune.ReplacementChar;
            var valid = b >= 0x20 && GameUtf8.TryDecode(p + i, charLength, out r);
            if (!valid)
                r = Rune.ReplacementChar;
            this._ctx.EnsureCapacity(count + 2);
            this._ctx.TextToByte[count] = valid ? i : -1;
            var units = r.EncodeToUtf16(this._ctx.Text.AsSpan(count));
            if (units == 2)
                this._ctx.TextToByte[count + 1] = -1;
            count += units;
            i += charLength;
        }

        length = i;
        return count;
    }

    /// <summary>Shapes the run <see cref="ReadRun"/> read, of <paramref name="count"/> code units.</summary>
    public void Shape(SizedFont sized, byte* p, int count, Pointer<AtkFontManager.Glyph>[] glyphs, ItalicMode italic)
    {
        this.Cluster(sized, count, italic);
        this.PlaceClusters(this._ctx, p, glyphs);
        foreach (var (b, owner) in this._ctx.Absorbed)
            glyphs[b] = glyphs[owner].IsNull ? default : emptyGlyph;
    }

    /// <summary>
    /// How far each byte of the run <see cref="ReadRun"/> read advances, as <see cref="Shape"/> would place it, without
    /// rasterizing. <see cref="Missing"/> marks a byte left to the game.
    /// </summary>
    /// <param name="sized">The sized font.</param>
    /// <param name="count">The run's count of code units.</param>
    /// <param name="advances">Per byte of the run.</param>
    /// <param name="italic">The run's italics.</param>
    public void Measure(SizedFont sized, int count, int[] advances, ItalicMode italic)
    {
        this.Cluster(sized, count, italic);
        this.MeasureClusters(this._ctx, advances);
        foreach (var (b, owner) in this._ctx.Absorbed)
            advances[b] = advances[owner] == Missing ? Missing : 0;
    }

    private void Cluster(SizedFont sized, int count, ItalicMode italic)
    {
        this._ctx.Sized = sized;
        this._ctx.Absorbed.Clear();
        count = HangulComposer.Compose(this._ctx.Text, this._ctx.TextToByte, count, this._ctx.Absorbed);
        AssignSources(this._ctx, count);

        if (italic != ItalicMode.Images)
        {
            // A code unit with no byte of its own is in the italics of the one before it.
            for (var t = 0; t < count; t++)
            {
                var b = this._ctx.TextToByte[t];
                this._ctx.TextItalic[t] = b >= 0 ? this._ctx.ByteItalic[b] : t > 0 ? this._ctx.TextItalic[t - 1] : italic != ItalicMode.Upright;
            }

            this.Collect(this._ctx, count, false);
        }
        else if (!this.CollectSpreadItalics(this._ctx, count))
        {
            this._ctx.TextItalic.AsSpan(0, count).Clear();
            this.Collect(this._ctx, count, true);
        }
    }

    private static void AssignSources(ShapingContext ctx, int count)
    {
        Span<Rune> runes = stackalloc Rune[MaxGraphemeRunes];
        for (var t = 0; t < count;)
        {
            var length = ctx.NextGrapheme(t, count);
            var n = 0;
            for (var u = t; u < t + length && n < MaxGraphemeRunes;)
            {
                if (Rune.DecodeFromUtf16(ctx.Text.AsSpan(u, t + length - u), out var rune, out var used) != OperationStatus.Done)
                    (rune, used) = (Rune.ReplacementChar, 1);
                runes[n++] = rune;
                u += used;
            }

            var source = ctx.Sized.Pick(runes[..n]);

            // Codepoint replacements are written into the text in place, so only where the UTF-16 length stays the same.
            if (source.Element is { DrawsGame: false } element)
            {
                for (var u = t; u < t + length;)
                {
                    if (Rune.DecodeFromUtf16(ctx.Text.AsSpan(u, t + length - u), out var rune, out var used) != OperationStatus.Done)
                    {
                        u++;
                        continue;
                    }

                    var drawn = element.GetDrawnCodepoint(rune);
                    if (drawn != rune && drawn.Utf16SequenceLength == used)
                        drawn.EncodeToUtf16(ctx.Text.AsSpan(u, used));
                    u += used;
                }
            }

            ctx.TextSource.AsSpan(t, length).Fill(source);
            t += length;
        }
    }

    private static float GetOwnWidth(IFaceSource source, float px, uint[] glyphs, GlyphOffset[]? offsets)
    {
        Span<float> own = glyphs.Length <= 32 ? stackalloc float[glyphs.Length] : new float[glyphs.Length];
        for (var i = 0; i < glyphs.Length; i++)
            own[i] = source.GetAdvance(px, glyphs[i]);
        return source.GetScreenAdvance(px, new(glyphs, own, offsets));
    }

    private static void AddMissing(ShapingContext ctx, int start, int end, float pen)
    {
        var x = (int)Misc.StdRound(pen);
        for (var t = start; t < end;)
        {
            var length = ctx.NextGrapheme(t, end);
            ctx.Clusters.Add(new() { TextStart = t, TextEnd = t + length, X0 = x, X1 = x });
            t += length;
        }
    }

    private void Collect(ShapingContext ctx, int count, bool italicImages)
    {
        ctx.Clusters.Clear();

        var pen = 0f;
        for (var start = 0; start < count;)
        {
            var source = ctx.TextSource[start];
            var inItalic = ctx.TextItalic[start];
            var end = start + 1;
            while (end < count && ctx.TextSource[end] == source && ctx.TextItalic[end] == inItalic)
                end++;

            // Text left to the game needs no scripts.
            if (source?.Element?.Source is null)
            {
                AddMissing(ctx, start, end, pen);
                start = end;
                continue;
            }

            analyzer.Itemize(ctx.Text.AsSpan(start, end - start), ctx.TextRuns);
            foreach (var run in ctx.TextRuns)
                this.CollectPiece(ctx, source, start + run.Start, start + run.End, run, inItalic, italicImages, ref pen);
            start = end;
        }
    }

    private void CollectPiece(
        ShapingContext ctx, GlyphSource? glyphSource, int start, int end, TextRun run, bool inItalic, bool italicImages, ref float pen)
    {
        var element = glyphSource?.Element;
        var source = element?.Source;
        if (source is not null && ctx.Sized.Bold)
            source = source.GetBold();
        var realItalic = false;
        if (source is not null && inItalic)
            (source, realItalic) = source.GetItalic();
        if (glyphSource is null || element is null || source is null || run.RightToLeft)
        {
            AddMissing(ctx, start, end, pen);
            return;
        }

        var px = element.GetPx(ctx.Sized.Px);
        for (var position = start; position < end;)
        {
            ctx.Shaped.Clear();
            int shapedLength;
            try
            {
                shapedLength = source.Shape(px, ctx.Text.AsSpan(position, end - position), run.Script, ctx.Shaped);
            }
            catch (Exception ex)
            {
                Plugin.Log.Error(ex, "Shaping with {family} failed; the text is laid out per character", element.Def.Lookup.FamilyName);
                ctx.Shaped.Clear();
                shapedLength = 0;
            }

            foreach (var shaped in ctx.Shaped)
                this.AddCluster(ctx, glyphSource, source, px, shaped, position, realItalic, italicImages, ref pen);
            position += shapedLength;

            // The source lacks this grapheme.
            if (position < end)
            {
                var length = ctx.NextGrapheme(position, end);
                AddMissing(ctx, position, position + length, pen);
                position += length;
            }
        }
    }

    private void AddCluster(
        ShapingContext ctx,
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

        // What shaping moved the glyphs by besides their own advances, towards the next cluster. Only monospacing replaces the
        // advances, keeping that.
        var mono = element.Def.Monospacing;
        var kerning = mono is null ? 0 : source.GetScreenAdvance(px, new(glyphs, shaped.Advances, offsets)) - GetOwnWidth(source, px, glyphs, offsets);

        // In a node's italics, shaped upright, a glyph of one character is drawn with its font's italic, if it has one.
        if (italicImages && glyphs.Length == 1 &&
            Rune.DecodeFromUtf16(ctx.Text.AsSpan(textStart, textEnd - textStart), out var rune, out var used) == OperationStatus.Done &&
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
        var rawAdvance = mono is null ? 0 : (int)Misc.StdRound(width - kerning);

        // As in xivres's wrapping_fixed_size_font, a monospaced cluster that drops its kerning drops that of the cluster before it too.
        if (mono is { DropKerning: true } && rawAdvance > 0)
        {
            kerning = 0;
            if (ctx.Clusters is [.., var previous] && previous.TextEnd == textStart && previous.GlyphSource == glyphSource && previous.Kerning != 0)
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
            Origin = Misc.StdRound((pen - x0) * ShapingContext.SubpixelSteps) / ShapingContext.SubpixelSteps,
            X0 = (int)x0,
            TextStart = textStart,
            TextEnd = textEnd,
            GlyphSource = glyphSource,
        };

        if (mono is not null)
        {
            pen = cluster.X0 + cells.GetRaster(ctx.Sized, cluster).Advance + kerning;
            cluster.X1 = (int)Misc.StdRound(pen);
        }
        else
        {
            var spacing = element.GetLetterSpacing(ctx.Sized.Px);
            cluster.X1 = (int)Misc.StdRound(pen + width) + spacing;
            pen += width + spacing;
        }

        ctx.Clusters.Add(cluster);
    }

    /// <summary>
    /// Collects a run in a node's italics shaped in italics but spread out to end where it does upright, as the game
    /// measures it upright.
    /// </summary>
    /// <returns>False, with nothing collected, if the two shapings' clusters differ or any is left to the game.</returns>
    private bool CollectSpreadItalics(ShapingContext ctx, int count)
    {
        ctx.TextItalic.AsSpan(0, count).Clear();
        this.Collect(ctx, count, false);
        var upright = ctx.Clusters.ToArray();
        ctx.TextItalic.AsSpan(0, count).Fill(true);
        this.Collect(ctx, count, false);
        var italic = ctx.Clusters.ToArray();
        ctx.Clusters.Clear();

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
            ctx.Clusters.Add(italic[i].MovedBy(share * i, share * (i + 1)));
        return true;
    }

    /// <summary>
    /// The game has no left bearing, so a cell with ink left of its pen starts that much earlier (but not before the previous
    /// cell) and the previous cell advances that much less, keeping the line's width and the ink where shaping put it.
    /// </summary>
    private void PlaceClusters(ShapingContext ctx, byte* bytes, Pointer<AtkFontManager.Glyph>[] glyphs)
    {
        this.RasterizeConcurrently(ctx);
        var sized = ctx.Sized;
        var clusters = ctx.Clusters;
        var count = clusters.Count;
        var starts = count <= 256 ? stackalloc int[count] : new int[count];
        for (var i = 0; i < count; i++)
        {
            var c = clusters[i];
            starts[i] = c.X0;
            if (c.Missing)
                continue;

            // A cluster after one left to the per-character path can't move into it.
            var raster = cells.GetRaster(sized, c);
            if (i > 0 && !clusters[i - 1].Missing && raster is { Width: > 0, Left: < 0 })
                starts[i] = Math.Max(c.X0 + raster.Left, starts[i - 1]);
        }

        for (var i = 0; i < count; i++)
        {
            var c = clusters[i];

            // A ligature's text elements share its advance evenly, so that the caret and selection can stop inside it.
            ctx.ElementStarts.Clear();
            for (var t = c.TextStart; t < c.TextEnd; t += ctx.NextGrapheme(t, c.TextEnd))
                ctx.ElementStarts.Add(t);
            var elements = ctx.ElementStarts.Count;

            var cell = default(Pointer<AtkFontManager.Glyph>);
            var advance = 0;
            if (!c.Missing)
            {
                var end = i + 1 < count && !clusters[i + 1].Missing ? starts[i + 1] : c.X1;
                advance = end - starts[i];
                cell = cells.GetCell(sized, c, bytes, ctx.TextToByte[c.TextStart], c.X0 - starts[i], Share(advance, elements, 0));
            }

            var element = 0;
            for (var t = c.TextStart; t < c.TextEnd; t++)
            {
                var startsElement = element + 1 < elements && ctx.ElementStarts[element + 1] == t;
                if (startsElement)
                    element++;

                var b = ctx.TextToByte[t];
                if (b < 0)
                    continue;
                glyphs[b] = cell.IsNull ? default
                            : t == c.TextStart ? cell
                            : startsElement ? cells.GetSpacer(sized, Share(advance, elements, element))
                            : emptyGlyph;
            }
        }
    }

    /// <summary>As <see cref="PlaceClusters"/> advances, but from the clusters' pens, as cells' left bearings don't change a line's width.</summary>
    private void MeasureClusters(ShapingContext ctx, int[] advances)
    {
        advances.AsSpan().Fill(Missing);
        var margin = cells.GetEdgeMargin(ctx.Sized);
        var clusters = ctx.Clusters;
        var count = clusters.Count;
        for (var i = 0; i < count; i++)
        {
            var c = clusters[i];
            if (c.Missing)
                continue;
            var end = i + 1 < count && !clusters[i + 1].Missing ? clusters[i + 1].X0 : c.X1;
            var advance = Math.Max(end - c.X0, 0);

            // A cell that can't start earlier advances by its edge margin more (GetCell), assumed of every such cluster.
            if (i == 0 || clusters[i - 1].Missing)
                advance += margin;

            ctx.ElementStarts.Clear();
            for (var t = c.TextStart; t < c.TextEnd; t += ctx.NextGrapheme(t, c.TextEnd))
                ctx.ElementStarts.Add(t);
            var elements = ctx.ElementStarts.Count;
            var element = 0;
            for (var t = c.TextStart; t < c.TextEnd; t++)
            {
                var startsElement = element + 1 < elements && ctx.ElementStarts[element + 1] == t;
                if (startsElement)
                    element++;
                var b = ctx.TextToByte[t];
                if (b >= 0)
                    advances[b] = t == c.TextStart || startsElement ? Share(advance, elements, element) : 0;
            }
        }
    }

    // A ligature's text elements share its advance evenly.
    private static int Share(int advance, int parts, int part) => (advance * (part + 1) / parts) - (advance * part / parts);

    /// <summary>
    /// The clusters not rasterized yet whose sources allow it are rasterized on the thread pool together, which this thread
    /// waits for, so that new text costs about as long as its slowest cluster.
    /// </summary>
    private void RasterizeConcurrently(ShapingContext ctx)
    {
        this._pending.Clear();
        this._seen.Clear();
        foreach (var c in ctx.Clusters)
        {
            if (!c.Missing && c.Source!.RasterizesConcurrently && cells.NeedsRaster(ctx.Sized, c) && this._seen.Add(cells.KeyOf(ctx.Sized, c)))
                this._pending.Add(c);
        }

        if (this._pending.Count < 2)
            return;

        // A cluster that fails is rasterized again by GetRaster, which reports it.
        var raws = new RasterGlyph?[this._pending.Count];
        var failed = new bool[this._pending.Count];
        Parallel.For(
            0,
            this._pending.Count,
            i =>
            {
                var c = this._pending[i];
                try
                {
                    raws[i] = c.Rasterize();
                }
                catch
                {
                    failed[i] = true;
                }
            });

        for (var i = 0; i < this._pending.Count; i++)
        {
            if (!failed[i])
                cells.AddRaster(ctx.Sized, this._pending[i], raws[i]);
        }
    }
}
