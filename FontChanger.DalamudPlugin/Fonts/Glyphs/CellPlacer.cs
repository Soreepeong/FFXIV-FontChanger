using System;
using System.Collections.Generic;
using System.Linq;

using FFXIVClientStructs.FFXIV.Component.GUI;

using FontChanger.DalamudPlugin.PluginInterface;

namespace FontChanger.DalamudPlugin.Fonts.Glyphs;

/// <summary>Puts a glyph's pixels in its atlas when it is drawn, and makes room in full atlases.</summary>
internal sealed unsafe class CellPlacer(GlyphStore store, CopyFonts copies, GameGlyphReader reader)
{
    // When an atlas is full, its least recently drawn plane is reused, at most this often.
    private const long EvictIntervalMs = 1000;

    private readonly HashSet<GlyphAtlas> _full = [];
    private readonly Dictionary<GlyphAtlas, long> _lastEviction = [];

    /// <summary>A glyph that finds no room draws nothing this time, at the same advance, and room is made at the next Present.</summary>
    /// <remarks>A glyph whose plane was reused is placed again here, showing in this frame, so that text baked now keeps it.</remarks>
    public AtkFontManager.Glyph* WithPixels(AtkFontManager.Glyph* glyph, long tick)
    {
        if (!GlyphStore.IsOwn(glyph))
            return glyph;

        ref var cell = ref store.CellOf(glyph);
        if (cell.IsPlaced)
        {
            if (cell.Touch(glyph, tick))
                return glyph;
            cell.MarkStale();
        }

        if (cell.Stage != CellStage.Pending)
            return glyph;
        var atlas = cell.Atlas!;
        if (!atlas.CanHold(glyph->Width, glyph->Height))
        {
            cell.Drop(glyph);
            return glyph;
        }

        try
        {
            switch (atlas.TryAllocate(glyph->Width, glyph->Height, tick, out var a))
            {
                case GlyphAtlas.AllocationResult.PlacedOnNewPage:
                    copies.ApplyPages(atlas);
                    goto case GlyphAtlas.AllocationResult.Placed;
                case GlyphAtlas.AllocationResult.Placed:
                    cell.Place(glyph, a);
                    if (cell.Stage == CellStage.AwaitingRead)
                        reader.Enqueue(glyph);
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
        return this.GetBlank(glyph);
    }

    /// <summary>At Present, after this frame's text was laid out. The planes' glyphs are placed again as they are drawn.</summary>
    /// <returns>Whether a plane was reused.</returns>
    public bool EvictFull(long tick)
    {
        var reused = false;
        foreach (var atlas in this._full.ToArray())
        {
            if (tick - this._lastEviction.GetValueOrDefault(atlas) < EvictIntervalMs)
                continue;
            var plane = atlas.RecycleLeastRecentlyDrawn();
            this._full.Remove(atlas);
            this._lastEviction[atlas] = tick;
            reused = true;
            Plugin.Log.Information("The {family} glyph atlas was full: reusing plane {plane}, whose glyphs are placed again as they are drawn", atlas.Name, plane);
        }

        return reused;
    }

    public void Clear()
    {
        this._full.Clear();
        this._lastEviction.Clear();
    }

    private AtkFontManager.Glyph* GetBlank(AtkFontManager.Glyph* glyph)
    {
        var blank = store.CellOf(glyph).Blank;
        if (blank is not null)
            return blank;
        var b = store.Allocate();
        *b = *glyph;
        store.CellOf(b).Drop(b);
        store.CellOf(glyph).Blank = b;
        return b;
    }
}
