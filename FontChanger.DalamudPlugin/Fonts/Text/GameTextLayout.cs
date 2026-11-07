using System;
using System.Collections.Generic;
using System.Globalization;

using FFXIVClientStructs.FFXIV.Component.GUI;
using FFXIVClientStructs.Interop;

using FontChanger.DalamudPlugin.Fonts.Enums;
using FontChanger.DalamudPlugin.Fonts.Records;
using FontChanger.DalamudPlugin.Utilities;

namespace FontChanger.DalamudPlugin.Fonts.Text;

// The game lays text out a character at a time (LayOutCharacter), so a character no active run has is shaped with the rest of
// its line, which later lookups are answered from.
internal sealed unsafe partial class GameTextLayout
{
    private const int MaxRunBytes = 4096;
    private const int MaxActiveRuns = 8;

    // A resized chat log lays out every line again at each wrapping size, which the runs of a long log must all be kept for.
    private const int CachedRuns = 65536;

    // Graphemes longer than this are picked an element by their first runes.
    private const int MaxGraphemeRunes = 16;

    // Cells are made at a quarter-pixel resolution of the cluster's position.
    private const float SubpixelSteps = 4;

    private readonly FontReplacer _replacer;
    private readonly DirectWriteTextAnalyzer _analyzer;
    private readonly Pointer<AtkFontManager.Glyph> _emptyGlyph;
    private readonly AgedMap<(ulong Hash, int Length, SizedFont Sized, ItalicMode Italic), ShapedRun> _runs = new(CachedRuns);

    // Keyed by the largest size's sized font.
    private readonly AgedMap<(ulong Hash, int Length, SizedFont Sized, ItalicMode Italic), ShapedRun> _wrapRuns = new(CachedRuns);

    // Keyed by sized font too, as families may share a font but each places cells in its own atlas with its own metrics. Held
    // with FontReplacer.Keep.
    private readonly AgedMap<(RasterKey Raster, SizedFont Sized, int Pad, int Advance), Pointer<AtkFontManager.Glyph>> _cells = new();
    private readonly AgedMap<(SizedFont Sized, int Advance), Pointer<AtkFontManager.Glyph>> _spacers = new();
    private readonly AgedMap<RasterKey, RasterGlyph> _rasters = new();

    // Made once for the sweeps. Runs an active run still has, and cells a run still has, stay so that they aren't made again
    // alongside.
    private readonly Action<Pointer<AtkFontManager.Glyph>> _release;
    private readonly Action<ShapedRun> _dropRun;
    private readonly Func<ShapedRun, bool> _inactive = static r => r.Holders == 1;
    private readonly Func<Pointer<AtkFontManager.Glyph>, bool> _unused;

    private readonly List<ActiveRun> _active = [];
    private readonly List<(int Byte, int Owner)> _absorbed = [];
    private readonly List<Cluster> _clusters = [];
    private readonly List<TextRun> _textRuns = [];
    private readonly List<ShapedCluster> _shaped = [];
    private readonly List<int> _elementStarts = [];

    // Per UTF-16 code unit of the run being shaped; a byte offset of -1 (a low surrogate or replaced character) gets no glyph.
    private char[] _text = new char[256];
    private int[] _textToByte = new int[256];
    private GlyphSource?[] _textSource = new GlyphSource?[256];
    private bool[] _textItalic = new bool[256];
    private readonly bool[] _byteItalic = new bool[MaxRunBytes];
    private SizedFont? _shapingSized;

    public GameTextLayout(FontServices services, FontReplacer replacer, Pointer<AtkFontManager.Glyph> emptyGlyph)
    {
        this._replacer = replacer;
        this._emptyGlyph = emptyGlyph;
        this._analyzer = services.TextAnalyzer;
        this._release = g => replacer.Release(g);
        this._dropRun = this.Drop;
        this._unused = g => replacer.RefCount(g) == 1;
    }

    /// <summary>Returns null to have the game look the character up on its own.</summary>
    public Pointer<AtkFontManager.Glyph> TryGetGlyph(SizedFont sized, byte* p, ItalicMode italic) => this.LookUp(sized, null, null, p, italic);

    /// <summary>
    /// For wrapping text that may be drawn at any of several sizes: cells of no pixels in the largest size's pixels, each
    /// advancing by how much further the furthest of the sizes' pens has got, so that a line measures as wide as at its widest
    /// size. <paramref name="sizes"/> and <paramref name="scales"/> go largest last; null as for <see cref="TryGetGlyph"/>.
    /// </summary>
    public Pointer<AtkFontManager.Glyph> TryGetWrapGlyph(SizedFont[] sizes, float[] scales, byte* p, ItalicMode italic) =>
        this.LookUp(sizes[^1], sizes, scales, p, italic);

    public void Clear()
    {
        foreach (var active in this._active)
            this.Drop(active.Shaped);
        this._active.Clear();
        this._runs.Clear(this._dropRun);
        this._wrapRuns.Clear(this._dropRun);
        this._cells.Clear(this._release);
        this._spacers.Clear(this._release);
        this._rasters.Clear(null);
    }

    /// <summary>Drops what wasn't used lately, a bounded amount at a time. Once a frame, between frames.</summary>
    public void Sweep(long frame)
    {
        this._runs.Sweep(frame, this._inactive, this._dropRun);
        this._wrapRuns.Sweep(frame, this._inactive, this._dropRun);
        this._cells.Sweep(frame, this._unused, this._release);
        this._spacers.Sweep(frame, this._unused, this._release);
        this._rasters.Sweep(frame, null, null);
    }

    // The first holder keeps the run's glyphs, and the last one lets go of them.
    private void Hold(ShapedRun run)
    {
        if (run.Holders++ != 0)
            return;
        foreach (var glyph in run.Glyphs)
            this._replacer.Keep(glyph);
    }

    private void Drop(ShapedRun run)
    {
        if (--run.Holders != 0)
            return;
        foreach (var glyph in run.Glyphs)
            this._replacer.Release(glyph);
    }

    private static ulong HashBytes(byte* p, int length)
    {
        var hash = 0xCBF29CE484222325ul;
        for (var j = 0; j < length; j++)
            hash = (hash ^ p[j]) * 0x100000001B3ul;
        return hash;
    }

    /// <summary>The run's glyph at <paramref name="p"/>, from an active run, else shaped (laid out for wrapping if <paramref name="sizes"/> is given).</summary>
    private Pointer<AtkFontManager.Glyph> LookUp(SizedFont sized, SizedFont[]? sizes, float[]? scales, byte* p, ItalicMode italic)
    {
        var wrap = sizes is not null;
        for (var i = this._active.Count - 1; i >= 0; i--)
        {
            var active = this._active[i];
            if (active.Sized == sized && active.Wrap == wrap && p >= active.Start && p < active.End && active.ItalicAt(p) == italic && active.Matches())
                return active.Shaped.Glyphs[p - active.Start];
        }

        var shaped = sizes is not null ? this.GetWrapRun(sizes, scales!, p, italic, out var length) : this.GetShapedRun(sized, p, italic, out length);
        if (shaped is null)
            return default;

        ActiveRun run;
        if (this._active.Count == MaxActiveRuns)
        {
            run = this._active[0];
            this._active.RemoveAt(0);
            this.Drop(run.Shaped);
        }
        else
        {
            run = new();
        }

        this.Hold(shaped);
        run.Set(p, length, sized, italic, wrap, shaped);
        this._active.Add(run);
        return shaped.Glyphs[0];
    }

    private int NextGrapheme(int t, int end) => StringInfo.GetNextTextElementLength(this._text.AsSpan(t, end - t));

    private ShapedRun? GetShapedRun(SizedFont sized, byte* p, ItalicMode italic, out int length)
    {
        var count = this.ReadRun(p, italic, out length);
        if (length == 0)
            return null;

        var key = (HashBytes(p, length), length, sized, italic);
        var frame = this._replacer.Frame;
        if (this._runs.TryGetValue(key, frame, out var shaped))
            return shaped;

        shaped = new(new Pointer<AtkFontManager.Glyph>[length], italic == ItalicMode.Images ? null : this._byteItalic.AsSpan(0, length).ToArray());
        if (count != 0)
            this.Shape(sized, p, count, shaped.Glyphs, italic);
        this.Hold(shaped);
        return this._runs.Add(key, shaped, frame);
    }

    private ShapedRun? GetWrapRun(SizedFont[] sizes, float[] scales, byte* p, ItalicMode italic, out int length)
    {
        // Looked up before shaping, as the sizes' runs, made far more often by texts other than wrapped ones, are let go of much sooner.
        var largest = sizes[^1];
        this.ReadRun(p, italic, out length);
        if (length == 0)
            return null;

        var key = (HashBytes(p, length), length, largest, italic);
        var frame = this._replacer.Frame;
        if (this._wrapRuns.TryGetValue(key, frame, out var known))
            return known;

        if (this.GetShapedRun(largest, p, italic, out length) is not { } top)
            return null;

        // Every size reads the same bytes, so their glyphs line up by byte.
        var runs = new ShapedRun[sizes.Length];
        runs[^1] = top;
        for (var i = 0; i < sizes.Length - 1; i++)
        {
            if (this.GetShapedRun(sizes[i], p, italic, out var otherLength) is not { } run || otherLength != length)
                return top;
            runs[i] = run;
        }

        var wrap = new ShapedRun((Pointer<AtkFontManager.Glyph>[])top.Glyphs.Clone(), top.ItalicBytes);
        var pens = new float[runs.Length];
        var furthest = 0f;
        var placed = 0;
        for (var b = 0; b < length; b++)
        {
            // Characters left to the game advance as it looks them up by themselves.
            var glyph = top.Glyphs[b];
            if (glyph.IsNull)
                continue;
            for (var i = 0; i < runs.Length; i++)
            {
                if (runs[i].Glyphs[b] is { IsNull: false } g)
                    pens[i] += (g.Value->Width + g.Value->OffsetX) * scales[^1] / scales[i];
                furthest = Math.Max(furthest, pens[i]);
            }

            // A cluster's later characters advance nothing; what the sizes add there goes to the next cluster.
            if (glyph == this._emptyGlyph)
                continue;
            var end = (int)MathF.Ceiling(furthest);
            wrap.Glyphs[b] = this.GetSpacer(largest, end - placed);
            placed = end;
        }

        this.Hold(wrap);
        return this._wrapRuns.Add(key, wrap, frame);
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

    /// <summary>ItalicBytes is null for a run in a node's italics, which is all in them.</summary>
    /// <remarks>Holds its glyphs with <see cref="FontReplacer.Keep"/> while the caches or active runs have it. <seealso cref="Hold"/></remarks>
    private sealed class ShapedRun(Pointer<AtkFontManager.Glyph>[] glyphs, bool[]? italicBytes)
    {
        public Pointer<AtkFontManager.Glyph>[] Glyphs { get; } = glyphs;

        public bool[]? ItalicBytes { get; } = italicBytes;

        /// <summary>The caches and active runs that have it.</summary>
        public int Holders { get; set; }
    }

    /// <summary>
    /// A run shaped at an address, used only while the text there is unchanged since shaping, as the game reuses its
    /// text buffers.
    /// </summary>
    private sealed class ActiveRun
    {
        private byte[] _bytes = new byte[64];
        private int _compareLength;
        private ItalicMode _italic;

        public byte* Start { get; private set; }

        public byte* End { get; private set; }

        public SizedFont? Sized { get; private set; }

        /// <summary>Laid out for wrapping (<see cref="TryGetWrapGlyph"/>).</summary>
        public bool Wrap { get; private set; }

        public ShapedRun Shaped { get; private set; } = null!;

        public void Set(byte* start, int length, SizedFont sized, ItalicMode italic, bool wrap, ShapedRun shaped)
        {
            this.Start = start;
            this.End = start + length;
            this.Sized = sized;
            this._italic = italic;
            this.Wrap = wrap;
            this.Shaped = shaped;

            // The byte that ended the run is compared too, as what follows the last character could join its cluster.
            this._compareLength = length < MaxRunBytes ? length + 1 : length;
            if (this._bytes.Length < this._compareLength)
                this._bytes = new byte[Math.Max(this._compareLength, this._bytes.Length * 2)];
            new ReadOnlySpan<byte>(start, this._compareLength).CopyTo(this._bytes);
        }

        public ItalicMode ItalicAt(byte* p) =>
            this.Shaped.ItalicBytes is not { } bytes ? this._italic : bytes[p - this.Start] ? ItalicMode.Real : ItalicMode.Upright;

        public bool Matches()
        {
            // Byte by byte, stopping at the first difference, so that a now shorter text isn't read past its terminator.
            for (var i = 0; i < this._compareLength; i++)
            {
                if (this.Start[i] != this._bytes[i])
                    return false;
            }

            return true;
        }
    }
}
