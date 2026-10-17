using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

using FFXIVClientStructs.FFXIV.Component.GUI;

using FontChanger.DalamudPlugin.Rendering.Faces;
using FontChanger.DalamudPlugin.Rendering.Rasterization;
using FontChanger.DalamudPlugin.Utilities;

using Lumina.Text.Payloads;
using Lumina.Text.ReadOnly;

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
internal sealed unsafe partial class GameTextLayout : IDisposable
{
    private const int MaxRunBytes = 4096;
    private const int MaxActiveRuns = 8;
    private const int MaxCachedRuns = 8192;
    private const int MaxCachedRasters = 8192;

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
    private readonly List<int> _elementStarts = [];
    private readonly Dictionary<RasterKey, RasterGlyph> _rasters = [];

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

    public GameTextLayout(FontServices services, FontReplacer replacer, Pointer<AtkFontManager.Glyph> emptyGlyph)
    {
        this._replacer = replacer;
        this._emptyGlyph = emptyGlyph;
        this._analyzer = services.TextAnalyzer;
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
            if (run.Sized == sized && p >= run.Start && p < run.End && run.ItalicAt(p) == italic && run.Matches())
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

    /// <summary>Gets the code units of the grapheme at <paramref name="t"/> of the text being shaped, which ends at <paramref name="end"/>.</summary>
    private int NextGrapheme(int t, int end) => StringInfo.GetNextTextElementLength(this._text.AsSpan(t, end - t));

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

            // Control characters and broken sequences shape as a replacement character, which keeps them per character.
            var r = Rune.ReplacementChar;
            var valid = b >= 0x20 && GameUtf8.TryDecode(p + i, length, out r);
            if (!valid)
                r = Rune.ReplacementChar;
            this.EnsureCapacity(count + 2);
            var unitCount = r.EncodeToUtf16(units);
            this._text[count] = units[0];
            this._textToByte[count] = valid ? i : -1;
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

    /// <summary>
    /// A shaped run: the glyph for each byte, and whether each character's byte is in italics (null for a run in a node's
    /// italics, all of it in them).
    /// </summary>
    private sealed record ShapedRun(Pointer<AtkFontManager.Glyph>[] Glyphs, bool[]? ItalicBytes);

    /// <summary>
    /// A run shaped at an address. The game reuses its buffers (each line drawn from one copy buffer, each word measured
    /// in another), so an address says nothing about the text there now: the run keeps a copy of its bytes, and a
    /// lookup in it only uses it while its text is still the same, from its start (which the clusters depend on) to its
    /// end.
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
        /// Gets whether the run's text is what was shaped. Compared byte by byte, stopping at the first difference: the
        /// copy has no terminator before its end, so a shorter text stops it before reading past its own terminator.
        /// </summary>
        public bool Matches()
        {
            for (var i = 0; i < this._compareLength; i++)
            {
                if (this.Start[i] != this._bytes[i])
                    return false;
            }

            return true;
        }
    }
}
