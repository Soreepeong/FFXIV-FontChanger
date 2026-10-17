using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

using FFXIVClientStructs.FFXIV.Component.GUI;

using FontChanger.DalamudPlugin.PluginInterface;
using FontChanger.DalamudPlugin.Rendering.Faces;
using FontChanger.DalamudPlugin.Rendering.Faces.Sources;
using FontChanger.DalamudPlugin.Rendering.Rasterization;
using FontChanger.DalamudPlugin.Utilities;

using Lumina.Text.Payloads;
using Lumina.Text.ReadOnly;

using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;

namespace FontChanger.DalamudPlugin.Rendering.Text;

// Inside the namespace, so that Pointer<T> is XIVCS's over TerraFX's.
using FFXIVClientStructs.Interop;

/// <summary>
/// Shapes the game's text (ligatures, contextual alternates, kerning, marks), and answers the game's per-character glyph
/// lookups from the result.
/// </summary>
/// <remarks>
/// <para>The game lays text out one character at a time (LayOutCharacter); the run a character starts is shaped once,
/// to the end of the line. Each grapheme goes to the element its face picks; the line is cut into runs of one element,
/// script and direction (<see cref="DirectWriteTextAnalyzer"/>), and each run's source shapes it. Each shaped cluster becomes one
/// cell, advancing by the whole-pixel difference of its rounded start and end; right-to-left runs, the game's own
/// glyphs, and glyphs a source lacks are left to the per-character path. A run ends at an italic macro
/// (<see cref="ItalicMode"/>).</para>
/// <para>Clusters are placed one after another by their advances on screen (with the elements' transformations,
/// emboldening, letter spacing and monospacing), not as shaping laid them out.</para>
/// </remarks>
internal sealed unsafe class GameTextLayout : IDisposable
{
    private const int MaxRunBytes = 4096;
    private const int MaxActiveRuns = 8;
    private const int MaxCachedRuns = 8192;

    // Graphemes longer than this are picked an element by their first runes.
    private const int MaxGraphemeRunes = 16;

    // Cells are made at a quarter-pixel resolution of the cluster's position.
    private const float SubpixelSteps = 4;

    private readonly FontReplacer _replacer;
    private readonly DirectWriteTextAnalyzer _analyzer;
    private readonly Dictionary<(ulong Hash, int Length, FontReplacer.SizedFont Sized, ItalicMode Italic), ShapedRun> _runs = [];

    // Cells by their sized font too: a cell is placed in its family's atlas, with its metrics, and families may share a face.
    private readonly Dictionary<(RasterKey Raster, FontReplacer.SizedFont Sized, int Pad, int Advance), Pointer<AtkFontManager.Glyph>> _cells = [];
    private readonly Dictionary<(FontReplacer.SizedFont Sized, int Advance), Pointer<AtkFontManager.Glyph>> _spacers = [];

    private readonly List<ActiveRun> _active = [];
    private readonly List<(int Byte, int Owner)> _absorbed = [];

    // The clusters of the line being shaped, the analyzer's runs of it, and coverage of clusters by glyphs and position.
    private readonly List<Cluster> _clusters = [];
    private readonly List<TextRun> _textRuns = [];
    private readonly List<ShapedCluster> _shaped = [];
    private readonly Dictionary<RasterKey, RasterGlyph> _rasters = [];
    private bool _clusterGap;

    // The glyph of no width given to a cluster's characters after the first.
    private readonly Pointer<AtkFontManager.Glyph> _emptyGlyph;

    // While shaping a run: its text, and each code unit's byte offset, glyph source and whether it is in italics.
    private char[] _text = new char[256];
    private int[] _textToByte = new int[256];
    private FontReplacer.GlyphSource?[] _textSource = new FontReplacer.GlyphSource?[256];
    private bool[] _textItalic = new bool[256];
    private readonly bool[] _byteItalic = new bool[MaxRunBytes];
    private byte* _shapingBytes;
    private Pointer<AtkFontManager.Glyph>[]? _shapingGlyphs;
    private FontReplacer.SizedFont? _shapingSized;

    public GameTextLayout(GlyphRasterizer rasterizer, FontReplacer replacer, Pointer<AtkFontManager.Glyph> emptyGlyph)
    {
        this._replacer = replacer;
        this._emptyGlyph = emptyGlyph;
        this._analyzer = new(rasterizer);
    }

    /// <summary>
    /// Gets the glyph for the character at <paramref name="p"/> as shaped in its run, in the italics it is in, or 0 to
    /// look it up per character.
    /// </summary>
    public Pointer<AtkFontManager.Glyph> TryGetGlyph(FontReplacer.SizedFont sized, byte* p, ItalicMode italic)
    {
        for (var i = this._active.Count - 1; i >= 0; i--)
        {
            var run = this._active[i];
            if (run.Sized == sized && p >= run.Start && p < run.End && run.ItalicAt(p) == italic && run.Matches(p))
                return run.Glyphs[p - run.Start];
        }

        var reused = this._active.Count == MaxActiveRuns ? this._active[0] : null;
        if (!this.ShapeFrom(sized, p, italic, ref reused))
            return default;
        if (this._active.Count == MaxActiveRuns)
            this._active.RemoveAt(0);
        this._active.Add(reused!);
        return reused!.Glyphs[0];
    }

    /// <summary>Forgets every shaped run and cell (their glyphs are about to be freed, or their faces replaced).</summary>
    public void Clear()
    {
        this._active.Clear();
        this._runs.Clear();
        this._cells.Clear();
        this._spacers.Clear();
        this._rasters.Clear();
    }

    public void Dispose() => this.Clear();

    /// <summary>
    /// Makes the cells of the clusters a line was shaped into, in order. The game has no left bearing: a cell's box starts
    /// at its pen, so ink left of a cluster's pen (a j's hook, a kerned pair's overhang) would be moved right. Instead the
    /// cell starts that much earlier, up to the previous cell's start, and the previous cell advances that much less; the
    /// line's width stays the same, and the ink lands where shaping put it.
    /// </summary>
    private void PlaceClusters()
    {
        var count = this._clusters.Count;
        var starts = count <= 256 ? stackalloc int[count] : new int[count];
        for (var i = 0; i < count; i++)
        {
            var c = this._clusters[i];
            starts[i] = c.X0;
            if (c.Missing)
                continue;

            // A cluster after one left to the per-character path (or a gap) can't move into it.
            var raster = this.GetRaster(c);
            if (i > 0 && !c.AfterGap && !this._clusters[i - 1].Missing && raster.Width > 0 && raster.Left < 0)
                starts[i] = Math.Max(c.X0 + raster.Left, starts[i - 1]);
        }

        for (var i = 0; i < count; i++)
        {
            var c = this._clusters[i];

            // A ligature's characters (text elements: a letter with its marks is one) share its advance evenly, so the
            // caret and selection can stop inside it; the first draws all of it, and the others draw nothing.
            var elements = 0;
            for (var t = c.TextStart; t < c.TextEnd; t += StringInfo.GetNextTextElementLength(this._text.AsSpan(t, c.TextEnd - t)))
                elements++;

            // A glyph left to the per-character path stays its (the game's own font, at worst).
            var cell = default(Pointer<AtkFontManager.Glyph>);
            var advance = 0;
            if (!c.Missing)
            {
                var end = i + 1 < count && !this._clusters[i + 1].Missing && !this._clusters[i + 1].AfterGap ? starts[i + 1] : c.X1;
                advance = end - starts[i];
                cell = this.GetCell(c, c.X0 - starts[i], Share(advance, elements, 0));
            }

            var element = -1;
            var nextElement = c.TextStart;
            for (var t = c.TextStart; t < c.TextEnd; t++)
            {
                var startsElement = t == nextElement;
                if (startsElement)
                {
                    element++;
                    nextElement += StringInfo.GetNextTextElementLength(this._text.AsSpan(t, c.TextEnd - t));
                }

                var b = this._textToByte[t];
                if (b < 0)
                    continue;
                this._shapingGlyphs![b] = cell.IsNull ? default
                                         : t == c.TextStart ? cell
                                         : startsElement ? this.GetSpacer(Share(advance, elements, element))
                                         : this._emptyGlyph;
            }
        }

        static int Share(int advance, int parts, int part) => (advance * (part + 1) / parts) - (advance * part / parts);
    }

    /// <summary>Gets a glyph that draws nothing and advances by <paramref name="advance"/>.</summary>
    private Pointer<AtkFontManager.Glyph> GetSpacer(int advance)
    {
        var sized = this._shapingSized!;
        var key = (sized, advance);
        if (!this._spacers.TryGetValue(key, out var cell))
        {
            cell = this._replacer.PlaceCell(sized, new(advance, 0, 0, 0, 0, []), 0);
            this._spacers.Add(key, cell);
        }

        return cell;
    }

    /// <summary>
    /// Shapes (or finds shaped) the run starting at <paramref name="p"/> into <paramref name="run"/>, which is reused if
    /// given; false if there is nothing to shape.
    /// </summary>
    private bool ShapeFrom(FontReplacer.SizedFont sized, byte* p, ItalicMode italic, ref ActiveRun? run)
    {
        // The run: to the end of the line, as UTF-16, each code unit's byte offset kept (-1 for a low surrogate), and each
        // character's byte whether it is in italics; a run in a node's italics ends at an italic macro instead.
        var count = 0;
        var i = 0;
        var inItalic = italic != ItalicMode.Upright;
        Span<char> units = stackalloc char[2];
        while (i < MaxRunBytes)
        {
            var b = p[i];
            if (b == 0 || b == 0x0A || b == 0x0D)
                break;

            if (b == ReadOnlySeString.Stx)
            {
                // A macro: skipped whole; a line break macro ends the run, and so does an italic one in a node's italics.
                var macro = (MacroCode)p[i + 1];
                if (macro == MacroCode.NewLine || (macro == MacroCode.Italic && italic == ItalicMode.Images))
                    break;
                var total = GameUtf8.MacroLength(p + i);
                if (total == 0 || i + total > MaxRunBytes)
                    break;

                // <italic>: its argument is a plain integer (1 on, 0 off); anything else ends the run.
                if (macro == MacroCode.Italic)
                {
                    if (!GameUtf8.TryReadInteger(p + i + 2, out var payload, out var n)
                        || !GameUtf8.TryReadInteger(p + i + 2 + n, out var on, out var onLength) || onLength != payload)
                        break;
                    inItalic = on != 0;
                }

                i += total;
                continue;
            }

            this._byteItalic[i] = inItalic;

            // Stepped as the game does; the run ends before a sequence that would run into the end of the text.
            var length = GameUtf8.CharacterLength(p + i);
            if (length == 0 || i + length > MaxRunBytes)
                break;

            var codepoint = -1;
            if (b >= 0x20 &&
                Rune.DecodeFromUtf8(new(p + i, length), out var rune, out var consumed) == System.Buffers.OperationStatus.Done &&
                consumed == length)
            {
                codepoint = rune.Value;
            }

            // Control characters and broken sequences shape as a replacement character, which keeps them per character.
            var r = codepoint < 0 ? new Rune(0xFFFD) : new Rune(codepoint);
            this.EnsureCapacity(count + 2);
            var unitCount = r.EncodeToUtf16(units);
            this._text[count] = units[0];
            this._textToByte[count] = codepoint < 0 ? -1 : i;
            count++;
            if (unitCount == 2)
            {
                this._text[count] = units[1];
                this._textToByte[count] = -1;
                count++;
            }

            i += length;
        }

        if (i == 0)
            return false;

        var hash = 0xCBF29CE484222325ul;
        for (var j = 0; j < i; j++)
            hash = (hash ^ p[j]) * 0x100000001B3ul;

        // The byte that ended the run is compared too, as what follows the last character could join its cluster.
        var ended = i < MaxRunBytes;
        var key = (hash, i, sized, italic);
        if (!this._runs.TryGetValue(key, out var shaped))
        {
            shaped = new(new Pointer<AtkFontManager.Glyph>[i], italic == ItalicMode.Images ? null : this._byteItalic.AsSpan(0, i).ToArray());
            if (count != 0)
                this.Shape(sized, p, count, shaped.Glyphs, italic);
            if (this._runs.Count >= MaxCachedRuns)
                this._runs.Clear();
            this._runs.Add(key, shaped);
        }

        run ??= new();
        run.Set(p, i, ended ? 1 : 0, sized, italic, shaped);
        return true;
    }

    private void EnsureCapacity(int n)
    {
        if (n <= this._text.Length)
            return;
        Array.Resize(ref this._text, Math.Max(n, this._text.Length * 2));
        Array.Resize(ref this._textToByte, this._text.Length);
        Array.Resize(ref this._textSource, this._text.Length);
        Array.Resize(ref this._textItalic, this._text.Length);
    }

    private void Shape(FontReplacer.SizedFont sized, byte* p, int count, Pointer<AtkFontManager.Glyph>[] glyphs, ItalicMode italic)
    {
        this._absorbed.Clear();
        count = this.ComposeHangul(count);
        this.AssignSources(sized, count);

        // Each code unit is in italics as its character's byte is (a low surrogate or control character as the one before).
        for (var t = 0; t < count; t++)
        {
            var b = this._textToByte[t];
            this._textItalic[t] = b >= 0 ? this._byteItalic[b] : t > 0 ? this._textItalic[t - 1] : italic != ItalicMode.Upright;
        }

        this._shapingBytes = p;
        this._shapingGlyphs = glyphs;
        this._shapingSized = sized;
        try
        {
            // A run in a node's italics is spaced to its upright width (or drawn per character at its upright places);
            // the others are shaped whole, in italics where their italic macros say.
            if (italic != ItalicMode.Images)
                this.Collect(sized, count, ItalicMode.Real);
            else if (!this.CollectSpreadItalics(sized, count))
                this.Collect(sized, count, ItalicMode.Images);
            this.PlaceClusters();
        }
        finally
        {
            this._shapingBytes = null;
            this._shapingGlyphs = null;
            this._shapingSized = null;
        }

        // A jamo composed into its syllable is part of the syllable's cluster.
        foreach (var (b, owner) in this._absorbed)
            glyphs[b] = glyphs[owner].IsNull ? default : this._emptyGlyph;
    }

    /// <summary>
    /// Shapes the text being shaped into clusters as <paramref name="italic"/> says: with
    /// <see cref="ItalicMode.Real"/>, the code units in italics (<see cref="_textItalic"/>) are shaped with their
    /// sources' italics; with <see cref="ItalicMode.Images"/>, upright, each single glyph then drawn with its font's
    /// italic, where there is one.
    /// </summary>
    private void Collect(FontReplacer.SizedFont sized, int count, ItalicMode italic)
    {
        this._clusters.Clear();
        this._clusterGap = false;
        this._analyzer.Itemize(this._text.AsSpan(0, count), this._textRuns);

        var pen = 0f;
        foreach (var run in this._textRuns)
        {
            for (var start = run.Start; start < run.End;)
            {
                // A piece of one glyph source, in italics or not.
                var source = this._textSource[start];
                var inItalic = italic == ItalicMode.Real && this._textItalic[start];
                var end = start + 1;
                while (end < run.End && this._textSource[end] == source && (italic == ItalicMode.Real && this._textItalic[end]) == inItalic)
                    end++;

                this.CollectPiece(sized, source, start, end, run, inItalic, italic == ItalicMode.Images, ref pen);
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
        FontReplacer.SizedFont sized, FontReplacer.GlyphSource? glyphSource, int start, int end, TextRun run, bool inItalic, bool italicImages, ref float pen)
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

        var px = element.GetPx(sized.Px);
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
                this.AddCluster(sized, glyphSource, element, px, shaped, position, realItalic, italicImages, ref pen);
            position += shapedLength;

            // What the source lacks: a grapheme left to the per-character path.
            if (position < end)
            {
                var length = Math.Min(StringInfo.GetNextTextElementLength(this._text.AsSpan(position, end - position)), end - position);
                this.AddMissing(position, position + length, pen);
                position += length;
            }
        }
    }

    /// <summary>
    /// Adds a shaped cluster at the pen, which it advances by its advance on screen: with its element's letter spacing,
    /// or its monospaced cell (made now, as its advance is).
    /// </summary>
    private void AddCluster(
        FontReplacer.SizedFont sized,
        FontReplacer.GlyphSource glyphSource,
        MaterializedFontElement element,
        float px,
        ShapedCluster shaped,
        int textOffset,
        bool realItalic,
        bool italicImages,
        ref float pen)
    {
        var source = shaped.Source;
        var glyphs = shaped.GlyphIds;
        var offsets = shaped.Offsets;
        var real = realItalic || shaped.RealItalic;
        var textStart = textOffset + shaped.TextStart;
        var textEnd = textOffset + shaped.TextEnd;

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
        var x0 = Rounding.Round(pen);
        var cluster = new Cluster
        {
            Source = source,
            Px = px,
            GlyphIds = glyphs,
            Advances = shaped.Advances,
            Offsets = offsets,
            RealItalic = real,
            Origin = Rounding.Round((pen - x0) * SubpixelSteps) / SubpixelSteps,
            X0 = (int)x0,
            TextStart = textStart,
            TextEnd = textEnd,
            GlyphSource = glyphSource,
            Element = element,
            AfterGap = this._clusterGap,
        };

        if (element.Def.Monospacing is not null)
        {
            var rawAdvance = (int)Rounding.Round(width);
            cluster.Cell = element.Wrap(
                RasterizeCluster(cluster, rawAdvance, 1),
                sized.Px,
                glyphSource.Face.GetAlignment(sized.Px, glyphSource.Game),
                new ClusterSqueeze(cluster, rawAdvance));
            cluster.X1 = cluster.X0 + cluster.Cell.Value.Advance;
            pen = cluster.X1;
        }
        else
        {
            var spacing = element.GetLetterSpacing(sized.Px);
            cluster.X1 = (int)Rounding.Round(pen + width) + spacing;
            pen += width + spacing;
        }

        this._clusters.Add(cluster);
        this._clusterGap = false;
    }

    /// <summary>Adds the graphemes of a piece left to the per-character path, at the pen (which they don't advance).</summary>
    private void AddMissing(int start, int end, float pen)
    {
        var x = (int)Rounding.Round(pen);
        for (var t = start; t < end;)
        {
            var length = Math.Min(StringInfo.GetNextTextElementLength(this._text.AsSpan(t, end - t)), end - t);
            this._clusters.Add(new() { TextStart = t, TextEnd = t + length, X0 = x, X1 = x, Missing = true, AfterGap = this._clusterGap });
            t += length;
        }

        this._clusterGap = true;
    }

    /// <summary>
    /// Collects a run in a text node's italics: shaped in italics, at the italics' own positions, but spaced out evenly to
    /// end where the run shaped upright does (the game measures it upright, as it doesn't see the node's italics then).
    /// False, with nothing collected, if the two shapings don't make the same clusters (a ligature only one has), or a
    /// cluster is left to the game: then each character is drawn in italics at its upright place instead.
    /// </summary>
    private bool CollectSpreadItalics(FontReplacer.SizedFont sized, int count)
    {
        this.Collect(sized, count, ItalicMode.Upright);
        var upright = this._clusters.ToArray();
        this._textItalic.AsSpan(0, count).Fill(true);
        this.Collect(sized, count, ItalicMode.Real);
        var italic = this._clusters.ToArray();
        this._clusters.Clear();

        var n = italic.Length;
        if (n == 0 || n != upright.Length)
            return false;
        for (var i = 0; i < n; i++)
        {
            var (u, it) = (upright[i], italic[i]);
            if (u.TextStart != it.TextStart || u.TextEnd != it.TextEnd || u.Missing || it.Missing || u.AfterGap != it.AfterGap)
                return false;
        }

        // The difference is shared out after each cluster, the last ending where the upright run ends.
        var share = (float)(upright[n - 1].X1 - italic[n - 1].X1) / n;
        for (var i = 0; i < n; i++)
            this._clusters.Add(italic[i].MovedBy(share * i, share * (i + 1)));
        return true;
    }

    /// <summary>
    /// Composes conjoining jamo (L V, L V T, and a precomposed LV syllable followed by T) into precomposed syllables, as
    /// NFC does: DirectWrite has no Hangul shaping (it applies neither composition nor the fonts' ljmo/vjmo/tjmo), so it
    /// would draw each jamo as a separate full-width letter. The absorbed jamo's bytes are recorded with their syllable's.
    /// Old Hangul sequences, which have no precomposed syllable, are left as they are.
    /// </summary>
    private int ComposeHangul(int count)
    {
        const int SyllableBase = 0xAC00, LeadBase = 0x1100, VowelBase = 0x1161, TrailBase = 0x11A7;
        const int LeadCount = 19, VowelCount = 21, TrailCount = 28, SCount = LeadCount * VowelCount * TrailCount;

        var w = 0;
        for (var i = 0; i < count;)
        {
            int c = this._text[i];
            var n = 1;
            if (c - LeadBase is >= 0 and < LeadCount && i + 1 < count && this._text[i + 1] - VowelBase is >= 0 and < VowelCount)
            {
                c = SyllableBase + ((((c - LeadBase) * VowelCount) + (this._text[i + 1] - VowelBase)) * TrailCount);
                n = 2;
            }

            if ((n == 2 || (c - SyllableBase is >= 0 and < SCount && (c - SyllableBase) % TrailCount == 0)) &&
                i + n < count && this._text[i + n] - TrailBase is > 0 and < TrailCount)
            {
                c += this._text[i + n] - TrailBase;
                n++;
            }

            for (var j = 1; j < n; j++)
                this._absorbed.Add((this._textToByte[i + j], this._textToByte[i]));
            this._text[w] = (char)c;
            this._textToByte[w] = this._textToByte[i];
            w++;
            i += n;
        }

        return w;
    }

    /// <summary>
    /// Finds the glyph source of each grapheme of the text (all its code units get it), and puts in the characters an
    /// element draws with another's glyph (its codepoint replacements), where the UTF-16 length allows.
    /// </summary>
    private void AssignSources(FontReplacer.SizedFont sized, int count)
    {
        Span<Rune> runes = stackalloc Rune[MaxGraphemeRunes];
        for (var t = 0; t < count;)
        {
            var length = Math.Min(StringInfo.GetNextTextElementLength(this._text.AsSpan(t, count - t)), count - t);
            var n = 0;
            for (var u = t; u < t + length && n < MaxGraphemeRunes;)
            {
                if (Rune.DecodeFromUtf16(this._text.AsSpan(u, t + length - u), out var rune, out var used) != System.Buffers.OperationStatus.Done)
                    (rune, used) = (Rune.ReplacementChar, 1);
                runes[n++] = rune;
                u += used;
            }

            var source = sized.Pick(runes[..n]);
            if (source.Element is { DrawsGame: false } element)
            {
                for (var u = t; u < t + length;)
                {
                    if (Rune.DecodeFromUtf16(this._text.AsSpan(u, t + length - u), out var rune, out var used) != System.Buffers.OperationStatus.Done)
                    {
                        u++;
                        continue;
                    }

                    var drawn = element.GetDrawnCodepoint(rune);
                    if (drawn != rune && drawn.Utf16SequenceLength == used)
                        drawn.EncodeToUtf16(this._text.AsSpan(u, used));
                    u += used;
                }
            }

            this._textSource.AsSpan(t, length).Fill(source);
            t += length;
        }
    }

    /// <summary>
    /// Gets a cluster's coverage, relative to its pen, adjusted as its element says (MaterializedFontElement.Wrap) and finished
    /// (FinishGlyph). Its advance is set when its cell is made.
    /// </summary>
    private RasterGlyph GetRaster(Cluster c)
    {
        var sized = this._shapingSized!;
        var key = c.RasterKey ??= new(sized, c);
        if (this._rasters.TryGetValue(key, out var raster))
            return raster;

        raster = c.Cell ?? RasterizeCluster(c, 0, 1);
        if (c.Cell is null && c.Element is { } element && c.GlyphSource is { } glyphSource)
        {
            raster = element.Wrap(
                raster,
                sized.Px,
                glyphSource.Face.GetAlignment(sized.Px, glyphSource.Game),
                new ClusterSqueeze(c, 0));
        }

        // An edge margin makes ink left of the pen: the cell then starts earlier (PlaceClusters), keeping the spacing.
        raster = this._replacer.FinishGlyph(sized, raster);
        this._rasters.Add(key, raster);
        return raster;
    }

    /// <summary>Rasterizes a cluster as its source draws it, given an advance, squeezed horizontally by <paramref name="squeezeX"/>.</summary>
    private static RasterGlyph RasterizeCluster(Cluster c, int advance, float squeezeX) =>
        c.Source!.Rasterize(c.Px, new(c.GlyphIds, c.Advances, c.Offsets), new(c.Origin, advance, squeezeX)) ?? new(advance, 0, 0, 0, 0, []);

    /// <summary>
    /// Gets the cell of a cluster whose box starts <paramref name="pad"/> pixels left of its pen and advances by
    /// <paramref name="advance"/>.
    /// </summary>
    private Pointer<AtkFontManager.Glyph> GetCell(Cluster c, int pad, int advance)
    {
        var sized = this._shapingSized!;
        var raster = this.GetRaster(c);
        var key = (c.RasterKey!, sized, pad, advance);
        if (this._cells.TryGetValue(key, out var cell))
            return cell;

        raster = raster with { Advance = advance, Left = raster.Left + pad };

        // A cell that couldn't move left (PlaceClusters) is drawn right of where shaping put its margin: what follows moves along, keeping its distance to the ink.
        if (raster.Width != 0 && raster.Left < 0)
            raster = raster with { Advance = raster.Advance + Math.Min(-raster.Left, this._replacer.GetEdgeMargin(sized)) };
        var b = this._textToByte[c.TextStart];
        var utf8 = b < 0 ? 0u : GameUtf8.Pack(this._shapingBytes + b);
        cell = this._replacer.PlaceCell(sized, raster, utf8);
        if (c.RealItalic)
            this._replacer.MarkRealItalic(cell);
        this._cells.Add(key, cell);
        return cell;
    }

    /// <summary>How a run of text the game draws in italics is shaped.</summary>
    internal enum ItalicMode
    {
        Upright,

        /// <summary>
        /// In italics by an italic macro, which the game also sees when it measures: shaped with the sources' italics,
        /// which are drawn as they are; the game shears the glyphs of sources without (drawn upright).
        /// </summary>
        Real,

        /// <summary>
        /// In italics by the text node, which the game only sees when it draws, measuring the text upright: shaped in
        /// italics as <see cref="Real"/>, spaced out evenly to the upright run's width. Where the two shapings differ,
        /// shaped upright instead, each single-character cluster drawn with the italic of its font, where there is one.
        /// </summary>
        Images,
    }

    /// <summary>Draws a cluster again narrower, given an advance.</summary>
    private readonly struct ClusterSqueeze(Cluster cluster, int advance) : IGlyphSqueeze
    {
        public RasterGlyph Squeeze(float scale) => RasterizeCluster(cluster, advance, scale);
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

        /// <summary>The key of the cluster's coverage, once made (GetRaster).</summary>
        public RasterKey? RasterKey { get; set; }

        /// <summary>The fraction of a pixel the pen is at, in steps of a quarter.</summary>
        public float Origin { get; init; }

        /// <summary>The whole-pixel pen at the cluster's start and end.</summary>
        public int X0 { get; init; }

        public int X1 { get; set; }

        /// <summary>The cluster's coverage in its cell (MaterializedFontElement.Wrap), made as it is laid out if its element is monospaced.</summary>
        public RasterGlyph? Cell { get; set; }

        public int TextStart { get; init; }

        public int TextEnd { get; init; }

        /// <summary>Where the cluster's first character's glyph comes from.</summary>
        public FontReplacer.GlyphSource? GlyphSource { get; init; }

        /// <summary>The face element of the cluster's first character.</summary>
        public MaterializedFontElement? Element { get; init; }

        /// <summary>Whether it is left to the per-character path: the game draws it, or its source lacks it.</summary>
        public bool Missing { get; init; }

        /// <summary>Whether something not shaped here (a right-to-left run, a missing glyph) comes right before it.</summary>
        public bool AfterGap { get; init; }

        /// <summary>Whether its glyphs are a real italic, which the game must not shear.</summary>
        public bool RealItalic { get; init; }

        /// <summary>Gets the cluster moved right: its start by <paramref name="start"/> pixels, its end by <paramref name="end"/>.</summary>
        public Cluster MovedBy(float start, float end)
        {
            var pen = this.X0 + this.Origin + start;
            var x0 = Rounding.Round(pen);
            return this with
            {
                RasterKey = null,
                Origin = Rounding.Round((pen - x0) * SubpixelSteps) / SubpixelSteps,
                X0 = (int)x0,
                X1 = (int)Rounding.Round(this.X1 + end),
            };
        }
    }

    /// <summary>
    /// The key of a cluster's coverage: its face, size and element, and its source's glyphs at its position. Glyphs are
    /// compared by value.
    /// </summary>
    private sealed class RasterKey(FontReplacer.SizedFont sized, Cluster c) : IEquatable<RasterKey>
    {
        private readonly MaterializedFont _face = sized.Face;
        private readonly float _px = sized.Px;
        private readonly MaterializedFontElement? _element = c.Element;
        private readonly IFaceSource? _source = c.Source;
        private readonly float _emSize = c.Px;
        private readonly float _origin = c.Origin;
        private readonly uint[] _glyphs = c.GlyphIds;
        private readonly float[] _advances = c.Advances;
        private readonly GlyphOffset[]? _offsets = c.Offsets;

        public bool Equals(RasterKey? other) =>
            ReferenceEquals(this, other) ||
            (other is not null &&
             this._face == other._face &&
             this._element == other._element &&
             this._source == other._source &&
             this._px.Equals(other._px) &&
             this._emSize.Equals(other._emSize) &&
             this._origin.Equals(other._origin) &&
             this._glyphs.AsSpan().SequenceEqual(other._glyphs) &&
             this._advances.AsSpan().SequenceEqual(other._advances) &&
             MemoryMarshal.AsBytes(this._offsets.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(other._offsets.AsSpan())));

        public override bool Equals(object? obj) => this.Equals(obj as RasterKey);

        public override int GetHashCode()
        {
            var hash = default(HashCode);
            hash.Add(this._face);
            hash.Add(this._px);
            hash.Add(this._element);
            hash.Add(this._source);
            hash.Add(this._emSize);
            hash.Add(this._origin);
            hash.AddBytes(MemoryMarshal.AsBytes(this._glyphs.AsSpan()));
            return hash.ToHashCode();
        }
    }

    /// <summary>
    /// A shaped run: the glyph for each byte, and whether each character's byte is in italics (null for a run in a node's
    /// italics, all of it in them).
    /// </summary>
    private sealed record ShapedRun(Pointer<AtkFontManager.Glyph>[] Glyphs, bool[]? ItalicBytes);

    /// <summary>
    /// A run shaped at an address. The game reuses its buffers (each line drawn from one copy buffer, each word measured
    /// in another), so an address says nothing about the text there now: the run keeps a copy of its bytes, and a
    /// lookup in it only uses it while the text from the looked-up character to the run's end is still the same.
    /// </summary>
    private sealed class ActiveRun
    {
        private byte[] _bytes = new byte[64];
        private int _compareLength;
        private bool[]? _italicBytes;

        public byte* Start { get; private set; }

        public byte* End { get; private set; }

        public FontReplacer.SizedFont? Sized { get; private set; }

        public ItalicMode Italic { get; private set; }

        public Pointer<AtkFontManager.Glyph>[] Glyphs { get; private set; } = [];

        public void Set(byte* start, int length, int endBytes, FontReplacer.SizedFont sized, ItalicMode italic, ShapedRun shaped)
        {
            this.Start = start;
            this.End = start + length;
            this.Sized = sized;
            this.Italic = italic;
            this.Glyphs = shaped.Glyphs;
            this._italicBytes = shaped.ItalicBytes;
            this._compareLength = length + endBytes;
            if (this._bytes.Length < this._compareLength)
                this._bytes = new byte[Math.Max(this._compareLength, this._bytes.Length * 2)];
            new ReadOnlySpan<byte>(start, this._compareLength).CopyTo(this._bytes);
        }

        /// <summary>
        /// Gets the italics the character at <paramref name="p"/> (inside the run) was shaped in: as the run's italic macros
        /// left them, or the node's italics of a run spaced to its upright width.
        /// </summary>
        public ItalicMode ItalicAt(byte* p) =>
            this._italicBytes is null ? this.Italic : this._italicBytes[p - this.Start] ? ItalicMode.Real : ItalicMode.Upright;

        /// <summary>
        /// Gets whether the text from <paramref name="p"/> (inside the run) to its end is what was shaped. Compared byte
        /// by byte, stopping at the first difference: the copy has no terminator before its end, so a shorter text stops
        /// it before reading past its own terminator.
        /// </summary>
        public bool Matches(byte* p)
        {
            for (var i = (int)(p - this.Start); i < this._compareLength; i++)
            {
                if (this.Start[i] != this._bytes[i])
                    return false;
            }

            return true;
        }
    }
}
