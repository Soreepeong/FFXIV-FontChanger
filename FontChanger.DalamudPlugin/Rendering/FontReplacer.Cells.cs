using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

using FFXIVClientStructs.FFXIV.Component.GUI;

using FontChanger.DalamudPlugin.Configuration;
using FontChanger.DalamudPlugin.Game;
using FontChanger.DalamudPlugin.PluginInterface;
using FontChanger.DalamudPlugin.Rendering.Rasterization;
using FontChanger.DalamudPlugin.Rendering.Records;
using FontChanger.DalamudPlugin.Utilities;

using KernelTexture = FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Texture;

namespace FontChanger.DalamudPlugin.Rendering;

// Inside the namespace, so that Pointer<T> is XIVCS's and FontNames is ours, over TerraFX's.
using FFXIVClientStructs.Interop;

// The glyphs made: rasterized or copied from the game's, placed in the atlas when first drawn, and made room for.
internal sealed unsafe partial class FontReplacer
{
    /// <summary>Rasterizes a glyph into the atlas; null to leave it to the game.</summary>
    private AtkFontManager.Glyph* CreateGlyph(SizedFont sized, uint utf8Value)
    {
        if (!GameUtf8.TryUnpack(utf8Value, out var rune))
            return null;
        var pick = sized.Pick([rune]);
        if (pick.Element is { DrawsGame: true })
            return null;
        return pick.Face.TryRasterize(rune, sized.Px, pick.Game, out var r)
                   ? this.PlaceCell(sized, this.FinishGlyph(sized, r), utf8Value)
                   : null;
    }

    /// <summary>
    /// Gets a glyph of the atlas made from a game glyph of the largest of the sized font's game fonts that has it, scaled
    /// to its size: drawn from the game's texture, the edge would step by texels of the atlas's width (all of a font's
    /// textures share one), not the game texture's. Its pixels are read from the game's texture at the next upload. Null
    /// if none has it.
    /// </summary>
    private AtkFontManager.Glyph* GetGameGlyph(SizedFont sized, uint utf8Value)
    {
        if (sized.GameGlyphs.TryGetValue(utf8Value, out var made))
            return made;

        AtkFontManager.Glyph* glyph = null;
        IReadOnlyList<Pointer<AtkFontManager.Font>> sources = GameUtf8.TryUnpack(utf8Value, out var rune) ? sized.Sources : [];
        foreach (var source in sources)
        {
            var font = (AtkFontManager.Font*)source;
            glyph = this.PlaceGameGlyph(sized, font, font->FindGlyph(rune), utf8Value);
            if (glyph is not null)
                break;
        }

        sized.GameGlyphs[utf8Value] = glyph;
        return glyph;
    }

    /// <summary>
    /// Gets a glyph of the atlas made from a game glyph the game font's own lookup gave a copy, scaled to the copy's
    /// size; one of no width at the glyph's advance if its texture can't be read.
    /// </summary>
    private Pointer<AtkFontManager.Glyph> ConvertGameGlyph(
        SizedFont sized, Pointer<AtkFontManager.Font> font, Pointer<AtkFontManager.Glyph> game, uint utf8Value)
    {
        var key = (font, game);
        if (sized.ConvertedGlyphs.TryGetValue(key, out var made))
            return (AtkFontManager.Glyph*)made;
        var glyph = this.PlaceGameGlyph(sized, font, game, utf8Value);
        if (glyph.IsNull)
        {
            var advance = (int)Misc.StdRound((game.Value->Width + game.Value->OffsetX) * GetGameScale(sized, font));
            glyph = this.PlaceCell(sized, new(advance, 0, 0, 0, 0, []), utf8Value);
        }

        sized.ConvertedGlyphs[key] = glyph;
        return glyph;
    }

    /// <summary>Puts a game glyph into the atlas, its pixels read from its font's texture at the next upload; null if they can't be.</summary>
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
        return this.PlaceCell(sized, this.FinishGlyph(sized, r), utf8Value, pixels);
    }

    /// <summary>Gets the scale from a game font's glyphs to a sized font's: 1 within the half-pixel rounding of copy sizes.</summary>
    private static float GetGameScale(SizedFont sized, AtkFontManager.Font* font) =>
        font->Size <= 0 || MathF.Abs(sized.Px - font->Size) <= 0.25f ? 1 : sized.Px / font->Size;

    /// <summary>
    /// Fills the cells of game glyphs from the game's textures, then copies the atlas's changes to the GPU. On the thread
    /// that calls Present, once a frame.
    /// </summary>
    public void Upload()
    {
        Misc.DebugAssertMainThread();
        this._frameTick = Environment.TickCount64;
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

            ReleasePixels(ref cell);
        }

        this._gameReads.Clear();

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
    }

    /// <summary>Gets a glyph's box filled with its game texture's pixels (read as 8-bit coverage), scaled.</summary>
    private static RasterGlyph WithSourcePixels(RasterGlyph box, GameTextureSource s, byte[] sourceAlpha)
    {
        var scaled = new RasterGlyph(0, 0, s.Top, s.Width, s.Height, sourceAlpha);
        if (s.Scale != 1)
            scaled = scaled.Scaled(s.Scale, 0, 0);
        var alpha = new byte[box.Width * box.Height];
        RasterGlyph.BlitMax(alpha, box.Width, box.Height, scaled, scaled.Left - box.Left, scaled.Top - box.Top);
        return box with { Alpha = alpha };
    }

    /// <summary>Gets the empty pixels <see cref="FinishGlyph"/> adds around a glyph of a size, on each side.</summary>
    internal int GetEdgeMargin(SizedFont sized) => GetEdgeMargin(this.Edge, sized.Px);

    private static int GetEdgeMargin(EdgeSettings edge, float px) => Math.Max(0, (int)MathF.Ceiling(edge.GetReach(px)) - 1);

    /// <summary>
    /// Makes a glyph rasterized for a size (and adjusted as its element says) ready to be placed: surrounded with empty
    /// pixels for a wide edge, which is only drawn within a glyph's box and the pixel around it.
    /// </summary>
    internal RasterGlyph FinishGlyph(SizedFont sized, RasterGlyph r)
    {
        var m = this.GetEdgeMargin(sized);
        return m == 0 || r.Width == 0 ? r : r.Reboxed(r.Left - m, r.Top - m, r.Width + (2 * m), r.Height + (2 * m));
    }

    /// <summary>
    /// Puts a rasterized glyph or cluster into the atlas as a game glyph advancing by <see cref="RasterGlyph.Advance"/>.
    /// Its pixels go to the atlas when it is first drawn; for a glyph made from a game glyph, they are read from
    /// <paramref name="source"/> then (<paramref name="r"/> is blank).
    /// </summary>
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

        // Text is measured far more than drawn (every text change, at the node's unscaled size too): a glyph keeps its
        // pixels here until it is drawn (WithPixels, which every glyph handed to the game goes through).
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

    /// <summary>Marks a cell as a real italic glyph, which the game must not shear.</summary>
    internal void MarkRealItalic(AtkFontManager.Glyph* cell) => this.CellOf(cell).RealItalic = true;

    /// <summary>
    /// Puts a glyph's pixels into the atlas if they aren't yet, for a glyph about to be drawn, and notes that it was drawn.
    /// A glyph that finds no room draws nothing this time (the same advance), and room is made at the next upload (EvictPlane).
    /// </summary>
    private AtkFontManager.Glyph* WithPixels(AtkFontManager.Glyph* glyph)
    {
        if (_current.MeasuringOnly || ((GlyphSlot*)glyph)->Id < 0)
            return glyph;

        ref var cell = ref this.CellOf(glyph);
        if (cell.State == CellState.Placed)
        {
            cell.Drawn = this._frameTick;
            return glyph;
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
            if (this.WritePixels(glyph, ref cell, this._frameTick))
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

    /// <summary>
    /// Places a pending glyph's pixels in its atlas, drawn last at <paramref name="drawn"/>; false if there is no room. A
    /// game glyph's pixels are read at the next upload.
    /// </summary>
    private bool WritePixels(AtkFontManager.Glyph* glyph, ref Cell cell, long drawn)
    {
        if (!cell.Atlas!.TryAllocate(glyph->Width, glyph->Height, out var page, out var plane, out var x, out var y))
            return false;

        glyph->Packed = (x, y, plane, page);
        cell.State = CellState.Placed;
        cell.Drawn = drawn;
        if (cell.Source is not null)
        {
            this._gameReads.Add(glyph);
        }
        else
        {
            WriteCell(glyph, cell);
            cell.Raster = default;
        }

        return true;
    }

    /// <summary>Gets a glyph that advances as a glyph does but draws nothing, for one whose pixels found no room yet.</summary>
    private AtkFontManager.Glyph* GetBlank(AtkFontManager.Glyph* glyph)
    {
        var blank = this.CellOf(glyph).Blank;
        if (blank is not null)
            return blank;
        var b = this.AllocateGlyph();
        *b = *glyph;
        DropPixels(b);
        this.CellOf(glyph).Blank = b;
        return b;
    }

    /// <summary>
    /// Makes room in a full atlas by emptying one of its planes: the one whose glyphs not drawn in the last
    /// <see cref="KeepDrawnMs"/> take the most room. Its glyphs drawn since are placed in it again (the most recently drawn
    /// first), from their pixels; the others leave the atlas, keeping their pixels to be placed again when they are next
    /// drawn. If every glyph was drawn recently, the plane's least recently drawn leave until half of it is free. On the
    /// thread that calls Present, after this frame's text was laid out: the next frame lays out with the new places, and
    /// the upload that follows puts them in the texture.
    /// </summary>
    private void EvictPlane(GlyphAtlas atlas)
    {
        Misc.DebugAssertMainThread();
        var now = Environment.TickCount64;
        var stale = new long[atlas.MaxPlanes];
        var oldest = new long[atlas.MaxPlanes];
        Array.Fill(oldest, long.MaxValue);
        var all = CollectionsMarshal.AsSpan(this._cells);
        for (var id = 0; id < all.Length; id++)
        {
            if (all[id].State != CellState.Placed || all[id].Atlas != atlas)
                continue;
            var glyph = &this.SlotAt(id)->Glyph;
            var plane = PlaneOf(glyph);
            if (now - all[id].Drawn > KeepDrawnMs)
                stale[plane] += GlyphAtlas.PaddedArea(glyph->Width, glyph->Height);
            oldest[plane] = Math.Min(oldest[plane], all[id].Drawn);
        }

        // The most stale room; without any, the plane whose glyphs were drawn least recently.
        var victim = Array.IndexOf(stale, stale.Max());
        if (stale[victim] == 0)
            victim = Array.IndexOf(oldest, oldest.Min());

        var moved = new List<(int Id, long Drawn, byte[] Pixels, byte[]? Lcd)>();
        for (var id = 0; id < all.Length; id++)
        {
            var glyph = &this.SlotAt(id)->Glyph;
            if (all[id].State != CellState.Placed || all[id].Atlas != atlas || PlaneOf(glyph) != victim)
                continue;
            var page = glyph->TextureIndex;
            var pixels = atlas.Read(page, glyph->Channel, glyph->X, glyph->Y, glyph->Width, glyph->Height);
            var lcd = atlas.Subpixel ? atlas.ReadLcd(page, glyph->X, glyph->Y, glyph->Width, glyph->Height) : null;
            moved.Add((id, all[id].Drawn, pixels, lcd));
        }

        moved.Sort((a, b) => b.Drawn.CompareTo(a.Drawn));
        atlas.ClearPlane(victim / GlyphAtlas.PlanesPerPage, victim % GlyphAtlas.PlanesPerPage);

        // What may stay: the glyphs drawn recently, but no more than half of the plane if none were stale.
        var budget = stale[victim] == 0 ? (long)atlas.Width * atlas.Height / 2 : long.MaxValue;
        var kept = 0;
        foreach (var (id, drawn, pixels, lcd) in moved)
        {
            var glyph = &this.SlotAt(id)->Glyph;
            ref var cell = ref all[id];
            cell.State = CellState.Pending;
            cell.Raster = new(0, 0, 0, glyph->Width, glyph->Height, pixels, lcd);
            cell.BoxLeft = 0;
            cell.InkTop = 0;
            glyph->Packed = (0, 0, 0, 0);
            var area = GlyphAtlas.PaddedArea(glyph->Width, glyph->Height);
            if (now - drawn <= KeepDrawnMs && area <= budget && this.WritePixels(glyph, ref cell, drawn))
            {
                budget -= area;
                kept++;
            }
        }

        this._fullAtlases.Remove(atlas);
        this._lastEviction[atlas] = now;
        Plugin.Log.Information(
            "The {family} glyph atlas was full: emptied plane {plane}, kept {kept} recently drawn glyphs of its {count}",
            atlas.Name,
            victim,
            kept,
            moved.Count);
    }

    private static int PlaneOf(AtkFontManager.Glyph* glyph) =>
        (glyph->TextureIndex * GlyphAtlas.PlanesPerPage) + glyph->Channel;

    private static void WriteCell(AtkFontManager.Glyph* glyph, in Cell cell)
    {
        var r = cell.Raster;

        // The ink's rows in the box (cut off only past the game's byte-sized fields).
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

    /// <summary>Lets go of a cell's pixels kept to be placed (or read).</summary>
    private static void ReleasePixels(ref Cell cell)
    {
        cell.Raster = default;
        cell.Source = null;
    }

    /// <summary>Makes a glyph whose pixels can't be placed draw nothing from now on, advancing as measured.</summary>
    private static void DropCell(AtkFontManager.Glyph* glyph, ref Cell cell)
    {
        cell.State = CellState.None;
        ReleasePixels(ref cell);
        DropPixels(glyph);
    }

    /// <summary>Makes a glyph whose pixels found no room draw nothing, advancing as measured.</summary>
    private static void DropPixels(AtkFontManager.Glyph* glyph)
    {
        glyph->OffsetX = (sbyte)Math.Clamp(glyph->OffsetX + glyph->Width, sbyte.MinValue, sbyte.MaxValue);
        glyph->Width = 0;
    }

    private AtkFontManager.Glyph* AllocateGlyph()
    {
        var id = this._cells.Count;
        if (id % SlotBlockSize == 0)
            this._slotBlocks.Add((GlyphSlot*)NativeMemory.AllocZeroed((nuint)(SlotBlockSize * sizeof(GlyphSlot))));
        var slot = this.SlotAt(id);
        slot->Id = id;
        this._cells.Add(default);
        return &slot->Glyph;
    }

    private GlyphSlot* SlotAt(int id) => this._slotBlocks[id / SlotBlockSize].Value + (id % SlotBlockSize);

    /// <summary>Gets what is kept of a glyph made here. The reference is good until the next glyph is made.</summary>
    private ref Cell CellOf(AtkFontManager.Glyph* glyph) => ref CollectionsMarshal.AsSpan(this._cells)[((GlyphSlot*)glyph)->Id];

    private void FreeGlyphs()
    {
        foreach (var block in this._slotBlocks)
            NativeMemory.Free(block.Value);
        this._slotBlocks.Clear();
        this._cells.Clear();
    }

    /// <summary>
    /// A glyph made here, where the game reads it (it must not move): the glyph, then the id of what is kept of it
    /// (<see cref="Cell"/>), -1 for the empty glyph. Glyph pointers handed to the game are of these.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct GlyphSlot
    {
        public AtkFontManager.Glyph Glyph;
        public int Id;
    }

    private enum CellState : byte
    {
        /// <summary>No pixels: an empty glyph, a blank, or one whose pixels couldn't be placed.</summary>
        None,

        /// <summary>Pixels waiting to go to the atlas when the glyph is next drawn.</summary>
        Pending,

        /// <summary>In the atlas, where <see cref="AtkFontManager.Glyph.Packed"/> says.</summary>
        Placed,
    }

    /// <summary>What is kept of a glyph made here, by its slot's id.</summary>
    private struct Cell
    {
        public CellState State;

        public GlyphAtlas? Atlas;

        /// <summary>
        /// Its coverage while pending (and for a game glyph, until its pixels are read), with the ink's left in the box and
        /// its top row in the box.
        /// </summary>
        public RasterGlyph Raster;

        public int BoxLeft;

        public int InkTop;

        /// <summary>For a glyph made from a game glyph: where its pixels are read from.</summary>
        public GameTextureSource? Source;

        /// <summary>When it was last drawn (Environment.TickCount64 of the frame), once placed.</summary>
        public long Drawn;

        /// <summary>The glyph drawn instead while its pixels find no room; null until one is needed.</summary>
        public AtkFontManager.Glyph* Blank;

        /// <summary>Whether it is a real italic glyph, which the game must not shear.</summary>
        public bool RealItalic;
    }

    /// <summary>
    /// A game glyph's pixels: a rectangle of a plane of a game font texture (a Kernel::Texture), whose top left is at
    /// (0, <see cref="Top"/>) from the pen in the game font's pixels, scaled by <see cref="Scale"/>.
    /// </summary>
    internal sealed record GameTextureSource(Pointer<KernelTexture> Texture, int X, int Y, int Width, int Height, int Plane, int Top, float Scale);
}
