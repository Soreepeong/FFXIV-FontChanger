using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

using FFXIVClientStructs.FFXIV.Component.GUI;
using FFXIVClientStructs.Interop;

using FontChanger.DalamudPlugin.Fonts.Enums;
using FontChanger.DalamudPlugin.Fonts.Glyphs;
using FontChanger.DalamudPlugin.PluginInterface;
using FontChanger.DalamudPlugin.Utilities;

namespace FontChanger.DalamudPlugin.Fonts.Text;

/// <summary>
/// The game lays text out a character at a time (LayOutCharacter), so a character no active run has is shaped with the rest of
/// its line, which later lookups are answered from.
/// </summary>
/// <remarks>
/// <ul>
/// <li>A shaped run holds its glyphs while a cache has it.</li>
/// <li>Active runs last for a frame, so that a cache sweep never takes one.</li>
/// </ul>
/// </remarks>
internal sealed unsafe class ShapedRunCache
{
    private const int MaxActiveRuns = 8;

    // A resized chat log lays out every line again at each wrapping size, which the runs of a long log must all be kept for.
    private const int CachedRuns = 65536;

    private readonly GlyphStore _store;
    private readonly Pointer<AtkFontManager.Glyph> _emptyGlyph;
    private readonly ClusterGlyphCache _cells;
    private readonly Shaper _shaper;
    private readonly AgedMap<RunKey, ShapedRun> _runs;

    // Keyed by the largest size's sized font.
    private readonly AgedMap<RunKey, ShapedRun> _wrapRuns;

    private readonly List<ActiveRun> _active = [];
    private readonly Stack<ActiveRun> _freeActive = [];
    private readonly Action<ShapedRun> _dropRun;

    public ShapedRunCache(DirectWriteServices services, GlyphStore store, GlyphFactory factory, FrameState state)
    {
        this._store = store;
        this._emptyGlyph = store.EmptyGlyph;
        this._cells = new(store, factory, state);
        this._shaper = new(services.TextAnalyzer, this._cells, this._emptyGlyph);
        this._runs = new(state, CachedRuns);
        this._wrapRuns = new(state, CachedRuns);
        this._dropRun = this.Drop;
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

    /// <summary>A glyph of no pixels that advances by <paramref name="advance"/>, at most <see cref="sbyte.MaxValue"/>.</summary>
    public Pointer<AtkFontManager.Glyph> GetSpacer(SizedFont sized, int advance) => this._cells.GetSpacer(sized, advance);

    public void Clear()
    {
        this.ClearActive();
        this._runs.Clear(this._dropRun);
        this._wrapRuns.Clear(this._dropRun);
        this._cells.Clear();
    }

    /// <summary>At Present. Runs go first, so that the cells they let go of go too.</summary>
    public void Sweep()
    {
        this.ClearActive();
        this._runs.Sweep(null, this._dropRun);
        this._wrapRuns.Sweep(null, this._dropRun);
        this._cells.Sweep();
    }

    private static ulong HashBytes(ReadOnlySpan<byte> bytes)
    {
        var hash = 0xCBF29CE484222325ul;
        for (; bytes.Length >= sizeof(ulong); bytes = bytes[sizeof(ulong)..])
        {
            hash = (hash ^ MemoryMarshal.Read<ulong>(bytes)) * 0x100000001B3ul;
            hash ^= hash >> 32;
        }

        foreach (var b in bytes)
            hash = (hash ^ b) * 0x100000001B3ul;
        return hash;
    }

    private void ClearActive()
    {
        foreach (var active in this._active)
            this._freeActive.Push(active);
        this._active.Clear();
    }

    private void Hold(ShapedRun run)
    {
        foreach (var glyph in run.Glyphs)
            this._store.Keep(glyph);
    }

    private void Drop(ShapedRun run)
    {
        foreach (var glyph in run.Glyphs)
            this._store.Release(glyph);
    }

    /// <summary>The run's glyph at <paramref name="p"/>, from an active run, else shaped (laid out for wrapping if <paramref name="sizes"/> is given).</summary>
    private Pointer<AtkFontManager.Glyph> LookUp(SizedFont sized, SizedFont[]? sizes, float[]? scales, byte* p, ItalicMode italic)
    {
        var wrap = sizes is not null;
        for (var i = this._active.Count - 1; i >= 0; i--)
        {
            var active = this._active[i];
            if (active.Sized == sized && active.Wrap == wrap && active.TryGet(p, italic) is { } glyph)
                return glyph;
        }

        var shaped = sizes is not null ? this.GetWrapRun(sizes, scales!, p, italic, out var length) : this.GetShapedRun(sized, p, italic, out length);
        if (shaped is null)
            return default;

        // A run of the same kind there is stale, and could answer past the end of the new one unchecked.
        for (var i = this._active.Count - 1; i >= 0; i--)
        {
            var active = this._active[i];
            if (active.Sized == sized && active.Wrap == wrap && active.Overlaps(p, length))
            {
                this._active.RemoveAt(i);
                this._freeActive.Push(active);
            }
        }

        ActiveRun run;
        if (this._active.Count == MaxActiveRuns)
        {
            run = this._active[0];
            this._active.RemoveAt(0);
        }
        else if (!this._freeActive.TryPop(out run!))
        {
            run = new();
        }

        run.Set(p, length, sized, wrap, shaped);
        this._active.Add(run);
        return shaped.Glyphs[0];
    }

    /// <summary>The run cached for the bytes at <paramref name="p"/>, found without decoding them.</summary>
    /// <returns>Null if there is none. Another run whose bytes hash alike is let go of, for this one to take its place.</returns>
    private ShapedRun? FindRun(AgedMap<RunKey, ShapedRun> runs, SizedFont sized, byte* p, ItalicMode italic, out RunKey key, out int length)
    {
        this._shaper.ReadRun(p, italic, false, out length);
        var bytes = new ReadOnlySpan<byte>(p, length);
        key = new(HashBytes(bytes), length, sized, italic);
        if (length == 0 || !runs.TryGetValue(key, out var known))
            return null;
        if (bytes.SequenceEqual(known.Bytes))
            return known;
        runs.Remove(key, this._dropRun);
        return null;
    }

    private ShapedRun? GetShapedRun(SizedFont sized, byte* p, ItalicMode italic, out int length)
    {
        if (this.FindRun(this._runs, sized, p, italic, out var key, out length) is { } known)
            return known;
        if (length == 0)
            return null;

        var count = this._shaper.ReadRun(p, italic, true, out length);
        var shaped = new ShapedRun(new Pointer<AtkFontManager.Glyph>[length], new ReadOnlySpan<byte>(p, length).ToArray(), italic, this._shaper.ByteItalic[..length]);
        if (count != 0)
        {
            try
            {
                this._shaper.Shape(sized, p, count, shaped.Glyphs, italic);
            }
            catch (Exception ex)
            {
                // Cached all null, so that the run's characters are looked up by themselves instead of shaping it again for each.
                Plugin.Log.Error(ex, "Shaping failed; the run's characters are laid out by themselves");
                Array.Clear(shaped.Glyphs);
            }
        }

        return this.Cache(this._runs, key, shaped);
    }

    private ShapedRun? GetWrapRun(SizedFont[] sizes, float[] scales, byte* p, ItalicMode italic, out int length)
    {
        var largest = sizes[^1];
        if (this.FindRun(this._wrapRuns, largest, p, italic, out var key, out length) is { } known)
            return known;
        if (length == 0)
            return null;

        // Measured only, as no size draws these. Every size reads the same bytes, so their advances line up by byte.
        var advances = new int[sizes.Length][];
        for (var i = 0; i < sizes.Length; i++)
        {
            // Read again for each, as measuring changes what was read.
            var count = this._shaper.ReadRun(p, italic, true, out length);
            advances[i] = new int[length];
            if (count == 0)
            {
                advances[i].AsSpan().Fill(Shaper.Missing);
                continue;
            }

            try
            {
                this._shaper.Measure(sizes[i], count, advances[i], italic);
            }
            catch (Exception ex)
            {
                // Cached all null, so that the run's characters are measured by themselves instead of shaping it again for each.
                Plugin.Log.Error(ex, "Measuring a run at the UI scales failed; its characters are measured by themselves");
                advances[^1] = new int[length];
                advances[^1].AsSpan().Fill(Shaper.Missing);
                break;
            }
        }

        var top = advances[^1];
        var wrap = new ShapedRun(new Pointer<AtkFontManager.Glyph>[length], new ReadOnlySpan<byte>(p, length).ToArray(), italic, this._shaper.ByteItalic[..length]);
        var pens = new float[sizes.Length];
        var furthest = 0f;
        var placed = 0;
        for (var b = 0; b < length; b++)
        {
            // Characters left to the game advance as it looks them up by themselves.
            if (top[b] == Shaper.Missing)
                continue;
            for (var i = 0; i < sizes.Length; i++)
            {
                if (advances[i][b] != Shaper.Missing)
                    pens[i] += advances[i][b] * scales[^1] / scales[i];
                furthest = Math.Max(furthest, pens[i]);
            }

            // A cluster's later characters advance nothing; what the sizes add there goes to the next cluster.
            if (top[b] == 0)
            {
                wrap.Glyphs[b] = this._emptyGlyph;
                continue;
            }

            // A spacer advances at most sbyte.MaxValue, and what it can't goes to the next cluster.
            var advance = Math.Min((int)MathF.Ceiling(furthest) - placed, sbyte.MaxValue);
            wrap.Glyphs[b] = this._cells.GetSpacer(largest, advance);
            placed += advance;
        }

        return this.Cache(this._wrapRuns, key, wrap);
    }

    private ShapedRun Cache(AgedMap<RunKey, ShapedRun> runs, RunKey key, ShapedRun run)
    {
        this.Hold(run);
        return runs.Add(key, run);
    }

    private readonly record struct RunKey(ulong Hash, int Length, SizedFont Sized, ItalicMode Italic);

    private sealed class ShapedRun
    {
        public ShapedRun(Pointer<AtkFontManager.Glyph>[] glyphs, byte[] bytes, ItalicMode italic, ReadOnlySpan<bool> italicBytes)
        {
            this.Glyphs = glyphs;
            this.Bytes = bytes;
            if (italic == ItalicMode.Images || !italicBytes.Contains(!italicBytes[0]))
                this.Italic = italic == ItalicMode.Images ? italic : italicBytes[0] ? ItalicMode.Real : ItalicMode.Upright;
            else
                this.ItalicBytes = italicBytes.ToArray();
        }

        public Pointer<AtkFontManager.Glyph>[] Glyphs { get; }

        /// <summary>Gets the text it was shaped from, which a run found by its hash must match.</summary>
        public byte[] Bytes { get; }

        /// <summary>Gets the italics of every byte, unless they differ by byte.</summary>
        public ItalicMode Italic { get; }

        /// <summary>Gets whether each byte is in real italics, null if all are alike.</summary>
        public bool[]? ItalicBytes { get; }

        public ItalicMode ItalicAt(int b) =>
            this.ItalicBytes is not { } bytes ? this.Italic : bytes[b] ? ItalicMode.Real : ItalicMode.Upright;
    }

    /// <summary>
    /// A run shaped at an address, used only while the text there is unchanged since shaping, as the game reuses its text
    /// buffers.
    /// </summary>
    /// <remarks>
    /// Text is laid out forward, so the text is compared again only where a lookup isn't past the last one, as where a buffer's
    /// text is laid out again.
    /// </remarks>
    private sealed class ActiveRun
    {
        private byte* _start;
        private byte* _end;
        private byte* _last;
        private ShapedRun _shaped = null!;

        // The byte that ended the run is compared too, as what follows the last character could join its cluster.
        private int _compareLength;
        private byte _endByte;

        public SizedFont? Sized { get; private set; }

        /// <summary>Gets a value indicating whether it was laid out for wrapping.</summary>
        /// <seealso cref="TryGetWrapGlyph"/>
        public bool Wrap { get; private set; }

        public void Set(byte* start, int length, SizedFont sized, bool wrap, ShapedRun shaped)
        {
            this._start = start;
            this._end = start + length;
            this._last = start;
            this.Sized = sized;
            this.Wrap = wrap;
            this._shaped = shaped;
            this._compareLength = length < ShapingContext.MaxRunBytes ? length + 1 : length;
            this._endByte = this._compareLength > length ? start[length] : (byte)0;
        }

        public bool Overlaps(byte* start, int length) => start < this._end && start + length > this._start;

        /// <summary>Null if <paramref name="p"/> isn't in the run, or the text there changed.</summary>
        public Pointer<AtkFontManager.Glyph>? TryGet(byte* p, ItalicMode italic)
        {
            if (p < this._start || p >= this._end)
                return null;
            var b = (int)(p - this._start);
            if (this._shaped.ItalicAt(b) != italic || (p <= this._last && !this.Matches()))
                return null;
            this._last = p;
            return this._shaped.Glyphs[b];
        }

        private bool Matches()
        {
            // Byte by byte, stopping at the first difference, so that a now shorter text isn't read past its terminator.
            var bytes = this._shaped.Bytes;
            for (var i = 0; i < bytes.Length; i++)
            {
                if (this._start[i] != bytes[i])
                    return false;
            }

            return this._compareLength == bytes.Length || this._start[bytes.Length] == this._endByte;
        }
    }
}
