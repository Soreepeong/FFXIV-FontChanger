using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

using FFXIVClientStructs.FFXIV.Component.GUI;
using FFXIVClientStructs.Interop;

using FontChanger.DalamudPlugin.Configuration;
using FontChanger.DalamudPlugin.Fonts.Records;
using FontChanger.DalamudPlugin.Game;
using FontChanger.DalamudPlugin.PluginInterface;
using FontChanger.DalamudPlugin.Utilities;

using KernelTexture = FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Texture;

namespace FontChanger.DalamudPlugin.Fonts;

internal sealed unsafe partial class FontReplacer
{
    private const int SlotBlockSize = 1024;

    // When an atlas is full, its least recently drawn plane is reused, at most this often.
    private const long EvictIntervalMs = 1000;

    // The game may keep a glyph pointer (font caches, vertex data) for a few frames after it was last handed out.
    private const long GraceFrames = 8;

    // The game reads glyphs from these blocks, so they must not move; _cells is indexed by GlyphSlot.Id. Freed slots are reused.
    private readonly List<Pointer<GlyphSlot>> _slotBlocks = [];
    private readonly List<Cell> _cells = [];
    private readonly Stack<int> _freeIds = [];

    // Glyphs nothing of ours holds anymore, freed once GraceFrames have passed since they were last handed out.
    private readonly List<int> _unreferenced = [];

    private long _frame;
    private long _frameTick;

    private readonly HashSet<GlyphAtlas> _fullAtlases = [];
    private readonly Dictionary<GlyphAtlas, long> _lastEviction = [];
    private bool _planeReused;

    // Placed glyphs made from game glyphs, whose pixels are read from the game's textures at the next upload.
    private readonly List<Pointer<AtkFontManager.Glyph>> _gameReads = [];

    /// <summary>Counted by <see cref="Upload"/>.</summary>
    internal long Frame => this._frame;

    /// <summary>Null leaves the glyph to the game.</summary>
    private AtkFontManager.Glyph* CreateGlyph(SizedFont sized, uint utf8Value)
    {
        if (!GameUtf8.TryUnpack(utf8Value, out var rune))
            return null;
        var pick = sized.Pick([rune]);
        if (pick.Element is { DrawsGame: true })
            return null;
        return pick.Font.TryRasterize(rune, sized.Px, pick.Game, sized.Bold, out var r)
                   ? this.PlaceCell(sized, this.FinishGlyph(sized, r), utf8Value)
                   : null;
    }

    /// <summary>
    /// From the largest of the sized font's game fonts that has it, copied into the atlas because the edge steps by texels of
    /// the width all of a font's textures share, which is the atlas's.
    /// </summary>
    private AtkFontManager.Glyph* GetGameGlyph(SizedFont sized, uint utf8Value)
    {
        if (sized.GameGlyphs.TryGetValue(utf8Value, this._frame, out var made))
            return made;

        AtkFontManager.Glyph* glyph = null;
        if (GameUtf8.TryUnpack(utf8Value, out var rune))
        {
            foreach (var (_, font) in sized.Fonts)
            {
                glyph = this.PlaceGameGlyph(sized, font, font.Value->FindGlyph(rune), utf8Value);
                if (glyph is not null)
                    break;
            }
        }

        return sized.GameGlyphs.Add(utf8Value, this.Keep(glyph), this._frame);
    }

    private Pointer<AtkFontManager.Glyph> ConvertGameGlyph(
        SizedFont sized, Pointer<AtkFontManager.Font> font, Pointer<AtkFontManager.Glyph> game, uint utf8Value)
    {
        var key = (font, game);
        if (sized.ConvertedGlyphs.TryGetValue(key, this._frame, out var made))
            return (AtkFontManager.Glyph*)made;
        var glyph = this.PlaceGameGlyph(sized, font, game, utf8Value);
        if (glyph.IsNull)
        {
            var advance = (int)Misc.StdRound((game.Value->Width + game.Value->OffsetX) * GetGameScale(sized, font));
            glyph = this.PlaceCell(sized, new(advance, 0, 0, 0, 0, []), utf8Value);
        }

        return sized.ConvertedGlyphs.Add(key, this.Keep(glyph), this._frame);
    }

    private Pointer<AtkFontManager.Glyph> PlaceGameGlyph(SizedFont sized, Pointer<AtkFontManager.Font> font, Pointer<AtkFontManager.Glyph> game, uint utf8Value)
    {
        if (game.IsNull)
            return null;
        var textureIndex = game.Value->TextureIndex;
        var texture = textureIndex < font.Value->TextureCount && textureIndex < AtkFontManager.Font.MaxTextures ? font.Value->Textures[textureIndex] : default;
        if (texture.IsNull || !TextureReader.CanRead(texture))
            return null;
        var scale = GetGameScale(sized, font);
        var top = game.Value->OffsetY - font.Value->Ascent;
        var (left, t, w, h) = RasterGlyph.ScaledBounds(0, top, game.Value->Width, game.Value->Height, scale, 0, 0);
        var r = new RasterGlyph((int)Misc.StdRound((game.Value->Width + game.Value->OffsetX) * scale), left, t, w, h, new byte[w * h]);
        var pixels = new GameTextureSource(texture, game.Value->X, game.Value->Y, game.Value->Width, game.Value->Height, game.Value->Channel, top, scale);

        // Its box starts at the pen, with room for the edge in its own empty columns: a margin left of it would move it right.
        var finished = this.FinishGlyph(sized, r);
        if (finished.Left < 0 && finished.Width + finished.Left > 0)
            finished = finished.Reboxed(0, finished.Top, finished.Width + finished.Left, finished.Height);
        return this.PlaceCell(sized, finished, utf8Value, pixels);
    }

    // 1 within the half-pixel rounding of copy sizes.
    private static float GetGameScale(SizedFont sized, AtkFontManager.Font* font) =>
        font->Size <= 0 || MathF.Abs(sized.Px - font->Size) <= 0.25f ? 1 : sized.Px / font->Size;

    /// <summary>On the thread that calls Present, once a frame.</summary>
    public void Upload()
    {
        Misc.DebugAssertMainThread();
        this._frameTick = Environment.TickCount64;
        this.ReadGameGlyphs();

        foreach (var atlas in this._fullAtlases.ToArray())
        {
            if (Environment.TickCount64 - this._lastEviction.GetValueOrDefault(atlas) < EvictIntervalMs)
                continue;
            try
            {
                this.EvictPlane(atlas);
            }
            catch (Exception ex)
            {
                Plugin.Log.Error(ex, "Making room in the {family} glyph atlas failed; making every glyph again", atlas.Name);
                this.RebuildGlyphs();
                break;
            }
        }

        foreach (var atlas in this._atlases.Values)
            atlas.Upload();

        // This frame's UI draw commands are processed by now (Present, which is called even while the UI is hidden).
        this._subpixelShaders.EndFrame();

        // After the game reads, which hold glyphs without a reference.
        this.SweepCaches();
        this.FreeUnreferenced();
        this._frame++;
    }

    private void ReadGameGlyphs()
    {
        foreach (var g in this._gameReads)
        {
            var glyph = g.Value;
            ref var cell = ref this.CellOf(glyph);
            try
            {
                var s = cell.Source!;
                var pixels = this._gameTextures.Read(s.Texture, s.X, s.Y, s.Width, s.Height, s.Plane);
                cell.Raster = WithSourcePixels(cell.Raster, s, pixels);
                WriteCell(glyph, cell);
            }
            catch (Exception ex)
            {
                Plugin.Log.Error(ex, "Reading a game glyph's pixels failed");
            }

            cell.Source = null;
        }

        this._gameReads.Clear();
    }

    private void SweepCaches()
    {
        this._layout.Sweep(this._frame);
        foreach (var sized in this._sizedFonts.Values)
        {
            sized.Glyphs.Sweep(this._frame, null, this._release);
            sized.GameGlyphs.Sweep(this._frame, null, this._release);
            sized.ConvertedGlyphs.Sweep(this._frame, null, this._release);
            sized.WrapCells.Sweep(this._frame, null, this._release);
        }
    }

    private void FreeUnreferenced()
    {
        var count = this._unreferenced.Count;
        for (var i = 0; i < count; i++)
        {
            var id = this._unreferenced[i];
            ref var cell = ref CollectionsMarshal.AsSpan(this._cells)[id];
            if (cell.Refs != 0)
            {
                cell.Unreferenced = false;
                continue;
            }

            if (this._frame - cell.Handed < GraceFrames)
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

    /// <summary>Holds a glyph made by <see cref="PlaceCell"/>, so that it isn't freed.</summary>
    /// <returns>The glyph.</returns>
    /// <remarks>Null and the empty glyph are ignored, here and by the others.</remarks>
    internal AtkFontManager.Glyph* Keep(AtkFontManager.Glyph* glyph)
    {
        if (IsOwn(glyph))
            this.CellOf(glyph).Refs++;
        return glyph;
    }

    internal void Release(AtkFontManager.Glyph* glyph)
    {
        if (!IsOwn(glyph))
            return;
        ref var cell = ref this.CellOf(glyph);
        if (--cell.Refs == 0 && !cell.Unreferenced)
        {
            cell.Unreferenced = true;
            this._unreferenced.Add(((GlyphSlot*)glyph)->Id);
        }
    }

    internal int RefCount(AtkFontManager.Glyph* glyph) => IsOwn(glyph) ? this.CellOf(glyph).Refs : 0;

    // Marks it handed to the game in this frame.
    private void Hand(AtkFontManager.Glyph* glyph)
    {
        if (IsOwn(glyph))
            this.CellOf(glyph).Handed = this._frame;
    }

    private static bool IsOwn(AtkFontManager.Glyph* glyph) => glyph is not null && ((GlyphSlot*)glyph)->Id >= 0;

    private static RasterGlyph WithSourcePixels(RasterGlyph box, GameTextureSource s, byte[] sourceAlpha)
    {
        var scaled = new RasterGlyph(0, 0, s.Top, s.Width, s.Height, sourceAlpha);
        if (s.Scale != 1)
            scaled = scaled.Scaled(s.Scale, 0, 0);
        var alpha = new byte[box.Width * box.Height];
        RasterGlyph.BlitMax(alpha, box.Width, box.Height, scaled, scaled.Left - box.Left, scaled.Top - box.Top);
        return box with { Alpha = alpha };
    }

    internal int GetEdgeMargin(SizedFont sized) => GetEdgeMargin(this.Edge, sized.Px);

    private static int GetEdgeMargin(EdgeSettings edge, float px) => Math.Max(0, (int)MathF.Ceiling(edge.GetReach(px)) - 1);

    /// <summary>Adds empty margins for a wide edge, as the edge is only drawn within a glyph's box and the pixel around it.</summary>
    internal RasterGlyph FinishGlyph(SizedFont sized, RasterGlyph r)
    {
        var m = this.GetEdgeMargin(sized);
        return m == 0 || r.Width == 0 ? r : r.Reboxed(r.Left - m, r.Top - m, r.Width + (2 * m), r.Height + (2 * m));
    }

    /// <summary>With <paramref name="source"/>, <paramref name="r"/> is blank and its pixels are read from the game's texture once placed.</summary>
    internal AtkFontManager.Glyph* PlaceCell(SizedFont sized, RasterGlyph r, uint utf8Value, GameTextureSource? source = null)
    {
        // The game has no left bearing: overhanging ink moves right into the box; past 255 wide it is cut on the right.
        var boxLeft = Math.Min(r.Left, 0);
        var width = r.Width == 0 ? 0 : Math.Min(r.Left + r.Width - boxLeft, byte.MaxValue);

        // A box is a full line high, as the game's are, so that italics slant alike; ink beyond the line grows the box.
        var inkTop = sized.Ascent + r.Top;
        var top = 0;
        var height = sized.LineHeight;
        if (width != 0)
        {
            top = Math.Clamp(inkTop, sbyte.MinValue, 0);
            height = Math.Min(Math.Max(sized.LineHeight, inkTop + r.Height) - top, byte.MaxValue);
        }

        var glyph = this.AllocateGlyph();
        glyph->Utf8Value = utf8Value;
        glyph->Packed = (0, 0, 0, 0);
        glyph->Width = (byte)width;
        glyph->Height = (byte)height;
        glyph->OffsetX = (sbyte)Math.Clamp(r.Advance - width, sbyte.MinValue, sbyte.MaxValue);
        glyph->OffsetY = (sbyte)top;

        // Text is measured far more than drawn (every text change, at the node's unscaled size too), so a glyph is placed only
        // once it is drawn (WithPixels, which every glyph handed to the game goes through).
        if (width != 0)
        {
            this.CellOf(glyph) = new()
            {
                State = CellState.Pending,
                Atlas = sized.Atlas,
                Raster = r,
                BoxLeft = boxLeft,
                InkTop = inkTop - top,
                Source = source,
            };
        }

        return glyph;
    }

    internal void MarkRealItalic(AtkFontManager.Glyph* cell) => this.CellOf(cell).RealItalic = true;

    /// <summary>A glyph that finds no room draws nothing this time, at the same advance, and room is made at the next upload.</summary>
    /// <remarks>A glyph whose plane was reused is placed again here.</remarks>
    private AtkFontManager.Glyph* WithPixels(AtkFontManager.Glyph* glyph)
    {
        if (_current.MeasuringOnly || ((GlyphSlot*)glyph)->Id < 0)
            return glyph;

        ref var cell = ref this.CellOf(glyph);
        if (cell.State == CellState.Placed)
        {
            var page = glyph->TextureIndex;
            var plane = glyph->Channel;
            if (cell.Atlas!.Touch(page, plane, cell.Generation, this._frameTick))
                return glyph;

            // Its plane was reused; it is placed again from its pixels, which show in this frame like any newly placed glyph's,
            // so that text baked now (nameplates) keeps them.
            cell.State = CellState.Pending;
        }

        if (cell.State != CellState.Pending)
            return glyph;
        if (!cell.Atlas!.CanHold(glyph->Width, glyph->Height))
        {
            DropCell(glyph, ref cell);
            return glyph;
        }

        try
        {
            if (this.WritePixels(glyph, ref cell))
                return glyph;
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Placing a glyph in the atlas failed");
            DropCell(glyph, ref cell);
            return glyph;
        }

        this._fullAtlases.Add(cell.Atlas!);
        return this.GetBlank(glyph);
    }

    private bool WritePixels(AtkFontManager.Glyph* glyph, ref Cell cell)
    {
        if (!cell.Atlas!.TryAllocate(glyph->Width, glyph->Height, this._frameTick, out var a))
            return false;

        glyph->Packed = (a.X, a.Y, a.Plane, a.Page);
        cell.State = CellState.Placed;
        cell.Generation = a.Generation;

        if (cell.Source is not null)
            this._gameReads.Add(glyph);
        else
            WriteCell(glyph, cell);
        return true;
    }

    private AtkFontManager.Glyph* GetBlank(AtkFontManager.Glyph* glyph)
    {
        var blank = this.CellOf(glyph).Blank;
        if (blank is not null)
            return blank;
        var b = this.AllocateGlyph();
        *b = *glyph;
        DropPixels(b);
        this.CellOf(glyph).Blank = this.Keep(b);
        return b;
    }

    /// <summary>After this frame's text was laid out.</summary>
    /// <remarks>The plane's glyphs are placed again as they are drawn.</remarks>
    private void EvictPlane(GlyphAtlas atlas)
    {
        Misc.DebugAssertMainThread();
        var plane = atlas.RecycleLeastRecentlyDrawn();
        this._fullAtlases.Remove(atlas);
        this._lastEviction[atlas] = Environment.TickCount64;
        this._planeReused = true;
        Plugin.Log.Information("The {family} glyph atlas was full: reusing plane {plane}, whose glyphs are placed again as they are drawn", atlas.Name, plane);
    }

    private static void WriteCell(AtkFontManager.Glyph* glyph, in Cell cell)
    {
        var r = cell.Raster;

        // Ink rows are cut off only past the game's byte-sized fields.
        var firstRow = Math.Max(0, -cell.InkTop);
        var lastRow = Math.Min(r.Height, glyph->Height - cell.InkTop);
        if (lastRow > firstRow)
        {
            cell.Atlas!.Write(
                glyph->TextureIndex,
                glyph->Channel,
                glyph->X,
                glyph->Y + cell.InkTop + firstRow,
                glyph->Width,
                lastRow - firstRow,
                r.Alpha.AsSpan(firstRow * r.Width, (lastRow - firstRow) * r.Width),
                r.Width,
                r.Left - cell.BoxLeft,
                r.Lcd is { } lcd ? lcd.AsSpan(firstRow * r.Width * 3, (lastRow - firstRow) * r.Width * 3) : default);
        }
    }

    private static void ReleasePixels(ref Cell cell)
    {
        cell.Raster = default;
        cell.Source = null;
    }

    private static void DropCell(AtkFontManager.Glyph* glyph, ref Cell cell)
    {
        cell.State = CellState.None;
        ReleasePixels(ref cell);
        DropPixels(glyph);
    }

    private static void DropPixels(AtkFontManager.Glyph* glyph)
    {
        // OffsetX takes the width, so that the advance stays as measured.
        glyph->OffsetX = (sbyte)Math.Clamp(glyph->OffsetX + glyph->Width, sbyte.MinValue, sbyte.MaxValue);
        glyph->Width = 0;
    }

    private void DropGameReads(HashSet<Pointer<KernelTexture>> textures)
    {
        this._gameReads.RemoveAll(g => {
            ref var cell = ref this.CellOf(g);
            if (cell.Source is not { } s || !textures.Contains(s.Texture))
                return false;
            ReleasePixels(ref cell);
            return true;
        });
    }

    /// <summary>Reuses a freed slot if there is one.</summary>
    private AtkFontManager.Glyph* AllocateGlyph()
    {
        if (this._freeIds.TryPop(out var free))
            return &this.SlotAt(free)->Glyph;

        var id = this._cells.Count;
        if (id % SlotBlockSize == 0)
            this._slotBlocks.Add((GlyphSlot*)NativeMemory.AllocZeroed((nuint)(SlotBlockSize * sizeof(GlyphSlot))));
        var slot = this.SlotAt(id);
        slot->Id = id;
        this._cells.Add(default);
        return &slot->Glyph;
    }

    private GlyphSlot* SlotAt(int id) => this._slotBlocks[id / SlotBlockSize].Value + (id % SlotBlockSize);

    /// <summary>The reference is good until the next glyph is made.</summary>
    private ref Cell CellOf(AtkFontManager.Glyph* glyph) => ref CollectionsMarshal.AsSpan(this._cells)[((GlyphSlot*)glyph)->Id];

    private void FreeGlyphs()
    {
        foreach (var block in this._slotBlocks)
            NativeMemory.Free(block.Value);
        this._slotBlocks.Clear();
        this._cells.Clear();
        this._freeIds.Clear();
        this._unreferenced.Clear();
    }

    /// <summary>Glyph pointers handed to the game point into these; Id indexes the cells, -1 for the empty glyph.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct GlyphSlot
    {
        public AtkFontManager.Glyph Glyph;
        public int Id;
    }

    private enum CellState : byte
    {
        // An empty glyph, a blank, or one whose pixels couldn't be placed.
        None,
        Pending,
        Placed,
    }

    private struct Cell
    {
        public CellState State;

        public GlyphAtlas? Atlas;

        // Kept once placed, so that it can be placed again after its plane is reused. A game glyph's is blank until its pixels
        // are read.
        public RasterGlyph Raster;

        // The box's left relative to the pen, as Raster's Left is.
        public int BoxLeft;

        // The ink's top row in the box.
        public int InkTop;

        // Until its pixels are read.
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

        // In _unreferenced.
        public bool Unreferenced;
    }

    /// <summary>A rectangle of a game font texture's plane, whose top left is at (0, <see cref="Top"/>) from the pen in the game font's pixels.</summary>
    internal sealed record GameTextureSource(Pointer<KernelTexture> Texture, int X, int Y, int Width, int Height, int Plane, int Top, float Scale);
}
