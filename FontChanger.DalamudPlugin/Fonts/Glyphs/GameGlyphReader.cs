using System;
using System.Collections.Generic;

using FFXIVClientStructs.FFXIV.Component.GUI;
using FFXIVClientStructs.Interop;

using FontChanger.DalamudPlugin.Fonts.Records;
using FontChanger.DalamudPlugin.Game;
using FontChanger.DalamudPlugin.PluginInterface;

using KernelTexture = FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Texture;

namespace FontChanger.DalamudPlugin.Fonts.Glyphs;

/// <summary>A rectangle of a game font texture's plane, whose top left is at (0, <see cref="Top"/>) from the pen in the game font's pixels.</summary>
internal sealed record GameTextureSource(Pointer<KernelTexture> Texture, int X, int Y, int Width, int Height, int Plane, int Top, float Scale);

/// <summary>Reads the pixels of glyphs copied from the game's fonts, once they are placed.</summary>
internal sealed unsafe class GameGlyphReader(GlyphStore store) : IDisposable
{
    private readonly TextureReader _textures = new();
    private readonly List<Pointer<AtkFontManager.Glyph>> _pending = [];

    public bool IsEmpty => this._pending.Count == 0;

    public void Enqueue(AtkFontManager.Glyph* glyph) => this._pending.Add(glyph);

    /// <summary>At Present, after this frame's glyphs were placed.</summary>
    public void ReadAll()
    {
        foreach (var g in this._pending)
        {
            var glyph = g.Value;
            ref var cell = ref store.CellOf(glyph);
            if (cell.Stage != CellStage.AwaitingRead)
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

        this._pending.Clear();
    }

    /// <summary>Before the game lets <paramref name="textures"/> go.</summary>
    public void Forget(HashSet<Pointer<KernelTexture>> textures)
    {
        this._pending.RemoveAll(g => {
            ref var cell = ref store.CellOf(g);
            if (cell.Source is not { } s || !textures.Contains(s.Texture))
                return false;
            cell.Drop(g);
            return true;
        });
    }

    /// <summary>The glyphs waiting are drawn blank.</summary>
    public void Clear()
    {
        foreach (var g in this._pending)
            store.CellOf(g).Drop(g);
        this._pending.Clear();
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
