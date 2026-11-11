using System;
using System.Collections.Generic;
using System.Linq;

using FFXIVClientStructs.FFXIV.Component.GUI;
using FFXIVClientStructs.Interop;

using FontChanger.DalamudPlugin.Fonts.Records;
using FontChanger.DalamudPlugin.Game;
using FontChanger.DalamudPlugin.PluginInterface;

using KernelTexture = FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Texture;

namespace FontChanger.DalamudPlugin.Fonts.Glyphs;

/// <summary>A rectangle of a game font texture's plane, whose top left is at (0, <see cref="Top"/>) from the pen in the game font's pixels.</summary>
internal sealed record GameTextureSource(Pointer<KernelTexture> Texture, int X, int Y, int Width, int Height, int Plane, int Top, float Scale);

/// <summary>
/// Puts a glyph's pixels in its atlas when it is drawn, reads those of glyphs copied from the game's fonts at Present, and makes
/// room in full atlases.
/// </summary>
internal sealed unsafe class GlyphAtlasPlacer(GlyphStore store, ReplacementFontTable copies) : IDisposable
{
    // When an atlas is full, its least recently drawn plane is reused, at most this often.
    private const long EvictIntervalMs = 1000;

    private readonly HashSet<GlyphAtlas> _full = [];
    private readonly Dictionary<GlyphAtlas, long> _lastEviction = [];
    private readonly GameTextureReader _textures = new();
    private readonly List<Pointer<AtkFontManager.Glyph>> _pendingReads = [];

    /// <summary>A glyph that finds no room draws nothing this time, at the same advance, and room is made at the next Present.</summary>
    /// <remarks>A glyph whose plane was reused is placed again here, showing in this frame, so that text baked now keeps it.</remarks>
    public AtkFontManager.Glyph* WithPixels(AtkFontManager.Glyph* glyph, long frame)
    {
        if (!GlyphStore.IsOwn(glyph))
            return glyph;

        ref var cell = ref store.CellOf(glyph);
        if (cell.IsPlaced)
        {
            if (cell.Touch(glyph, frame))
                return glyph;
            cell.MarkStale(glyph);
        }

        if (cell.Stage != GlyphPixelStage.Pending)
            return glyph;
        var atlas = cell.Atlas!;
        if (!atlas.CanHold(glyph->Width, glyph->Height))
        {
            cell.Drop(glyph);
            return glyph;
        }

        try
        {
            switch (atlas.TryAllocate(glyph->Width, glyph->Height, frame, out var a))
            {
                case GlyphAtlas.AllocationResult.PlacedOnNewPage:
                    copies.ApplyPages(atlas);
                    goto case GlyphAtlas.AllocationResult.Placed;
                case GlyphAtlas.AllocationResult.Placed:
                    cell.Place(glyph, a);
                    if (cell.Stage == GlyphPixelStage.AwaitingRead)
                        this._pendingReads.Add(glyph);
                    return glyph;
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Placing a glyph in the atlas failed");
            store.CellOf(glyph).Drop(glyph);
            return glyph;
        }

        this._full.Add(atlas);
        return glyph;
    }

    /// <summary>At Present, after this frame's glyphs were placed.</summary>
    public void ReadGameGlyphs()
    {
        foreach (var g in this._pendingReads)
        {
            var glyph = g.Value;
            ref var cell = ref store.CellOf(glyph);
            if (cell.Stage != GlyphPixelStage.AwaitingRead)
                continue;
            try
            {
                var s = cell.Source!;
                var pixels = this._textures.Read(s.Texture, s.X, s.Y, s.Width, s.Height, s.Plane);
                cell.PixelsRead(glyph, WithSourcePixels(cell.Raster, s, pixels));
            }
            catch (Exception ex)
            {
                Plugin.Log.Error(ex, "Reading a game glyph's pixels failed");
                cell.Drop(glyph);
            }
        }

        this._pendingReads.Clear();
    }

    /// <summary>At Present, after this frame's text was laid out. The planes' glyphs are placed again as they are drawn.</summary>
    /// <returns>Whether a plane was reused.</returns>
    public bool EvictFull()
    {
        var now = Environment.TickCount64;
        var reused = false;
        foreach (var atlas in this._full.ToArray())
        {
            if (now - this._lastEviction.GetValueOrDefault(atlas) < EvictIntervalMs)
                continue;
            var plane = atlas.RecycleLeastRecentlyDrawn();
            this._full.Remove(atlas);
            this._lastEviction[atlas] = now;
            reused = true;
            Plugin.Log.Information("The {family} glyph atlas was full: reusing plane {plane}, whose glyphs are placed again as they are drawn", atlas.Name, plane);
        }

        return reused;
    }

    /// <summary>The glyphs waiting for their pixels are drawn blank.</summary>
    public void Clear()
    {
        this._full.Clear();
        this._lastEviction.Clear();
        foreach (var g in this._pendingReads)
            store.CellOf(g).Drop(g);
        this._pendingReads.Clear();
    }

    /// <summary>Also forgets the textures read, which the game may have let go.</summary>
    public void ClearAll()
    {
        this.Clear();
        this._textures.Clear();
    }

    public void Dispose() => this._textures.Dispose();

    private static RasterGlyph WithSourcePixels(RasterGlyph box, GameTextureSource s, byte[] sourceAlpha)
    {
        var scaled = new RasterGlyph(0, 0, s.Top, s.Width, s.Height, sourceAlpha);
        if (s.Scale != 1)
            scaled = scaled.Scaled(s.Scale, 0, 0);
        var alpha = new byte[box.Width * box.Height];
        RasterGlyph.BlitMax(alpha, box.Width, box.Height, scaled, scaled.Left - box.Left, scaled.Top - box.Top);
        return box with { Alpha = alpha };
    }
}
