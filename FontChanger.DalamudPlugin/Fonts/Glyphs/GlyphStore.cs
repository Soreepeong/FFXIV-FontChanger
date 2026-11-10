using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

using FFXIVClientStructs.FFXIV.Component.GUI;
using FFXIVClientStructs.Interop;

using FontChanger.DalamudPlugin.Fonts.Records;

namespace FontChanger.DalamudPlugin.Fonts.Glyphs;

/// <summary>Every glyph handed to the game, and how long each lives.</summary>
/// <remarks>
/// <ul>
/// <li>A glyph lives while something of ours holds it (<see cref="Keep"/>), and for <see cref="GraceFrames"/> after it was last
/// handed to the game, which may keep its pointer that long.</li>
/// <li>Null and <see cref="EmptyGlyph"/> are ignored by every method.</li>
/// </ul>
/// </remarks>
internal sealed unsafe class GlyphStore : IDisposable
{
    public const long GraceFrames = 8;

    private const int SlotBlockSize = 1024;

    // The game reads glyphs from these blocks, so they must not move. _cells is indexed by GlyphSlot.Id. Freed slots are reused.
    private readonly List<Pointer<GlyphSlot>> _slotBlocks = [];
    private readonly List<Cell> _cells = [];
    private readonly Stack<int> _freeIds = [];

    // Glyphs nothing of ours holds anymore.
    private readonly List<int> _unreferenced = [];

    private GlyphSlot* _emptyGlyph;

    public GlyphStore()
    {
        this._emptyGlyph = (GlyphSlot*)NativeMemory.AllocZeroed((nuint)sizeof(GlyphSlot));
        this._emptyGlyph->Id = -1;
        this.ReleaseAction = g => this.Release(g);
        this.HeldOnce = g => this.RefCount(g) == 1;
    }

    /// <summary>Gets <see cref="Release"/>, for the caches.</summary>
    public Action<Pointer<AtkFontManager.Glyph>> ReleaseAction { get; }

    /// <summary>Gets whether only the asking cache holds a glyph, so that one a run still has stays and isn't made again alongside.</summary>
    public Func<Pointer<AtkFontManager.Glyph>, bool> HeldOnce { get; }

    /// <summary>Gets a glyph of no width, for the characters of a shaped cluster after its first.</summary>
    public AtkFontManager.Glyph* EmptyGlyph => &this._emptyGlyph->Glyph;

    public static bool IsOwn(AtkFontManager.Glyph* glyph) => glyph is not null && ((GlyphSlot*)glyph)->Id >= 0;

    /// <summary>Reuses a freed slot if there is one.</summary>
    /// <returns>A glyph held once by the caller.</returns>
    public AtkFontManager.Glyph* Allocate()
    {
        if (!this._freeIds.TryPop(out var id))
        {
            id = this._cells.Count;
            if (id % SlotBlockSize == 0)
                this._slotBlocks.Add((GlyphSlot*)NativeMemory.AllocZeroed((nuint)(SlotBlockSize * sizeof(GlyphSlot))));
            this.SlotAt(id)->Id = id;
            this._cells.Add(default);
        }

        CollectionsMarshal.AsSpan(this._cells)[id].Refs = 1;
        return &this.SlotAt(id)->Glyph;
    }

    /// <returns>The glyph.</returns>
    public AtkFontManager.Glyph* Keep(AtkFontManager.Glyph* glyph)
    {
        if (IsOwn(glyph))
            this.CellOf(glyph).Refs++;
        return glyph;
    }

    public void Release(AtkFontManager.Glyph* glyph)
    {
        if (!IsOwn(glyph))
            return;
        ref var cell = ref this.CellOf(glyph);
        if (--cell.Refs == 0 && !cell.Queued)
        {
            cell.Queued = true;
            this._unreferenced.Add(((GlyphSlot*)glyph)->Id);
        }
    }

    public int RefCount(AtkFontManager.Glyph* glyph) => IsOwn(glyph) ? this.CellOf(glyph).Refs : 0;

    /// <summary>Marks it handed to the game in <paramref name="frame"/>.</summary>
    public void Hand(AtkFontManager.Glyph* glyph, long frame)
    {
        if (IsOwn(glyph))
            this.CellOf(glyph).Handed = frame;
    }

    /// <summary>The reference is good until the next <see cref="Allocate"/>.</summary>
    public ref Cell CellOf(AtkFontManager.Glyph* glyph) => ref CollectionsMarshal.AsSpan(this._cells)[((GlyphSlot*)glyph)->Id];

    /// <summary>After the game's reads, which hold glyphs without a reference.</summary>
    public void FreeUnreferenced(long frame)
    {
        var count = this._unreferenced.Count;
        for (var i = 0; i < count; i++)
        {
            var id = this._unreferenced[i];
            ref var cell = ref CollectionsMarshal.AsSpan(this._cells)[id];
            if (cell.Refs != 0)
            {
                cell.Queued = false;
                continue;
            }

            if (frame - cell.Handed < GraceFrames)
            {
                this._unreferenced.Add(id);
                continue;
            }

            // Its blank then waits for its own grace period.
            var blank = cell.Blank;
            cell = default;
            *this.SlotAt(id) = new() { Id = id };
            this._freeIds.Push(id);
            this.Release(blank);
        }

        this._unreferenced.RemoveRange(0, count);
    }

    /// <summary>Once the game holds none of them.</summary>
    public void Dispose()
    {
        foreach (var block in this._slotBlocks)
            NativeMemory.Free(block.Value);
        this._slotBlocks.Clear();
        this._cells.Clear();
        this._freeIds.Clear();
        this._unreferenced.Clear();
        NativeMemory.Free(this._emptyGlyph);
        this._emptyGlyph = null;
    }

    private GlyphSlot* SlotAt(int id) => this._slotBlocks[id / SlotBlockSize].Value + (id % SlotBlockSize);

    /// <summary>Glyph pointers handed to the game point into these. Id indexes the cells, or is -1 for the empty glyph.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct GlyphSlot
    {
        public AtkFontManager.Glyph Glyph;
        public int Id;
    }
}

/// <summary>Where a glyph's pixels are.</summary>
internal enum CellStage : byte
{
    /// <summary>No pixels, as of an empty glyph, a spacer or a blank.</summary>
    Empty,

    /// <summary>Its pixels wait for room in the atlas, which they get when it is drawn.</summary>
    Pending,

    /// <summary>Placed, its pixels read from the game's texture at the next Present.</summary>
    AwaitingRead,

    Placed,

    /// <summary>Its pixels couldn't be placed or read, and it draws nothing at the same advance.</summary>
    Dropped,
}

/// <summary>A glyph's pixels and lifetime.</summary>
/// <remarks>The methods are the only way between its stages.</remarks>
internal unsafe struct Cell
{
    public CellStage Stage;

    public GlyphAtlas? Atlas;

    // Kept once placed, so that it can be placed again after its plane is reused. A game glyph's is blank until its pixels
    // are read.
    public RasterGlyph Raster;

    // The box's left relative to the pen, as Raster's Left is.
    public int BoxLeft;

    // The ink's top row in the box.
    public int InkTop;

    // While pending or awaiting its read.
    public GameTextureSource? Source;

    // Of its plane when placed.
    public uint Generation;

    // Drawn instead while its pixels find no room. Held with Keep.
    public AtkFontManager.Glyph* Blank;

    // A real italic glyph, which the game must not shear.
    public bool RealItalic;

    // Caches, shaped runs and the glyph whose blank it is holding it.
    public int Refs;

    // The frame it was last handed to the game in.
    public long Handed;

    // In GlyphStore's list of unreferenced glyphs.
    public bool Queued;

    public readonly bool IsPlaced => this.Stage is CellStage.Placed or CellStage.AwaitingRead;

    public void Pend(GlyphAtlas atlas, RasterGlyph raster, int boxLeft, int inkTop, GameTextureSource? source)
    {
        this.Stage = CellStage.Pending;
        this.Atlas = atlas;
        this.Raster = raster;
        this.BoxLeft = boxLeft;
        this.InkTop = inkTop;
        this.Source = source;
    }

    /// <summary>Writes its pixels, or leaves them to be read if they come from the game.</summary>
    public void Place(AtkFontManager.Glyph* glyph, in GlyphAtlas.Allocation a)
    {
        glyph->Packed = (a.X, a.Y, a.Plane, a.Page);
        this.Generation = a.Generation;
        if (this.Source is not null)
        {
            this.Stage = CellStage.AwaitingRead;
            return;
        }

        this.Stage = CellStage.Placed;
        this.Write(glyph);
    }

    public void PixelsRead(AtkFontManager.Glyph* glyph, RasterGlyph raster)
    {
        this.Raster = raster;
        this.Source = null;
        this.Stage = CellStage.Placed;
        this.Write(glyph);
    }

    /// <summary>Its plane was reused, so it is placed again when next drawn.</summary>
    public void MarkStale()
    {
        if (this.Stage == CellStage.Placed)
            this.Stage = CellStage.Pending;
    }

    public void Drop(AtkFontManager.Glyph* glyph)
    {
        this.Stage = CellStage.Dropped;
        this.Raster = default;
        this.Source = null;

        // OffsetX takes the width, so that the advance stays as measured.
        glyph->OffsetX = (sbyte)Math.Clamp(glyph->OffsetX + glyph->Width, sbyte.MinValue, sbyte.MaxValue);
        glyph->Width = 0;
    }

    /// <summary>Marks its plane drawn in <paramref name="frame"/> if the plane still has its pixels.</summary>
    /// <returns>Whether the plane still has its pixels.</returns>
    public readonly bool Touch(AtkFontManager.Glyph* glyph, long frame) => this.Atlas!.Touch(glyph->TextureIndex, glyph->Channel, this.Generation, frame);

    private readonly void Write(AtkFontManager.Glyph* glyph)
    {
        var r = this.Raster;

        // Ink rows are cut off only past the game's byte-sized fields.
        var firstRow = Math.Max(0, -this.InkTop);
        var lastRow = Math.Min(r.Height, glyph->Height - this.InkTop);
        if (lastRow <= firstRow)
            return;
        this.Atlas!.Write(
            glyph->TextureIndex,
            glyph->Channel,
            glyph->X,
            glyph->Y + this.InkTop + firstRow,
            glyph->Width,
            lastRow - firstRow,
            r.Alpha.AsSpan(firstRow * r.Width, (lastRow - firstRow) * r.Width),
            r.Width,
            r.Left - this.BoxLeft,
            r.Lcd is { } lcd ? lcd.AsSpan(firstRow * r.Width * 3, (lastRow - firstRow) * r.Width * 3) : default);
    }
}
