using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

using FFXIVClientStructs.FFXIV.Component.GUI;

using FontChanger.DalamudPlugin.Configuration;
using FontChanger.DalamudPlugin.Game;
using FontChanger.DalamudPlugin.Rendering.Faces;
using FontChanger.DalamudPlugin.Utilities;

namespace FontChanger.DalamudPlugin.Rendering;

// Inside the namespace, so that Pointer<T> is XIVCS's and FontNames is ours, over TerraFX's.
using FFXIVClientStructs.Interop;

// The copies of the game's fonts at the sizes they are drawn at: their sized fonts, atlases and texture slots.
internal sealed unsafe partial class FontReplacer
{
    /// <summary>
    /// Makes the copies (of one game font, or all) take their sized fonts again, and what they borrow from their game
    /// fonts; false if there were none.
    /// </summary>
    private bool ResyncCopies(AtkFontManager.Font* only = null)
    {
        var any = false;
        foreach (var (copy, info) in this._copies)
        {
            if (only is not null && info.Original != only)
                continue;
            info.Sized = this.GetSized(info.Original, info.HalfPx);
            this.Sync(copy, info);
            any = true;
        }

        return any;
    }

    /// <summary>
    /// Gets the glyphs of a game font's family at a size, shared by the fonts of one name (a font and its lobby version),
    /// going to the atlas of the family. At the size of one of the family's faces, that face's glyphs with the metrics they
    /// have for its game font; at another, each glyph comes from the largest face that draws it itself (<see
    /// cref="SizedFont.Pick"/>), with the metrics of the face that draws the most glyphs itself.
    /// </summary>
    private SizedFont GetSized(Pointer<AtkFontManager.Font> original, int halfPx)
    {
        var px = halfPx / 2f;
        var faces = new List<(string Name, float Size, MaterializedFont Face, Pointer<AtkFontManager.Font> Game)>();
        if (FontNames.TryGetFaceName(original, out var originalName))
        {
            foreach (var (name, size) in FontNames.FaceNamesOf(Preset.FamilyOf(originalName)).OrderByDescending(f => f.Size))
            {
                var font = original;
                if (!string.Equals(name, originalName, StringComparison.OrdinalIgnoreCase) && !FontNames.TryFindFont(name, out font))
                    continue;
                if (font.IsNull || !font.Value->Flags.HasFlag(AtkFontManager.FontFlags.Ready) || font.Value->Size <= 0)
                    continue;
                faces.Add((name, size, this.FaceOf(name), font));
            }
        }

        if (faces.Count == 0)
        {
            var name = originalName ?? $"0x{(nint)original.Value:X}";
            faces = [(name, 0, originalName is not null ? this.FaceOf(originalName) : this._gameFace, original)];
        }
        else if (faces.FindIndex(f => MathF.Abs(f.Size - px) < 0.01f) is var exact and >= 0)
        {
            faces = [faces[exact]];
        }

        var best = 0;
        for (var i = 1; i < faces.Count; i++)
        {
            if (faces[i].Face.OwnGlyphCount > faces[best].Face.OwnGlyphCount)
                best = i;
        }

        var (fontName, _, face, game) = faces[best];
        if (!this._sizedFonts.TryGetValue((face, halfPx, fontName), out var sized))
        {
            var (ascent, lineHeight) = face.GetLineMetrics(px, game);
            sized = new(face, px, ascent, lineHeight, game, this.GetAtlas(Preset.FamilyOf(fontName)), faces.Select(f => (f.Face, f.Game)).ToList());
            this._sizedFonts.Add((face, halfPx, fontName), sized);
        }

        return sized;
    }

    private MaterializedFont FaceOf(string faceName) => this._presetFaces.GetValueOrDefault(faceName) ?? this._gameFace;

    /// <summary>Gets the atlas of a game font family, made on its first use.</summary>
    private GlyphAtlas GetAtlas(string family)
    {
        if (this._atlases.TryGetValue(family, out var atlas))
            return atlas;

        // FontSubpixelPS tells subpixel pages from the game's textures by their odd width; grayscale pages get an even one.
        var subpixel = this._subpixelShaders.Active;
        var s = this.Atlas;
        var (width, height, maxPages) = string.Equals(family, "AXIS", StringComparison.OrdinalIgnoreCase)
                                            ? (s.AxisPageWidth, s.AxisPageHeight, s.AxisMaxPages)
                                            : (s.OtherPageWidth, s.OtherPageHeight, s.OtherMaxPages);
        if (subpixel == (width % 2 == 0))
            width--;
        var format = s.Format == AtlasTextureFormat.Bgra4444 && this.Supports4444 ? AtlasTextureFormat.Bgra4444 : AtlasTextureFormat.Bgra8888;
        atlas = new(width, height, maxPages, family, subpixel, format);
        atlas.PageAdded += () => this.OnPageAdded(atlas);
        this._atlases.Add(family, atlas);
        if (!atlas.TryEnsurePages(1))
            throw new InvalidOperationException("Could not add an atlas page.");
        return atlas;
    }

    /// <summary>
    /// Takes what a copy borrows from its game font (glyph map, glyphs, flags), keeping its own size, metrics and texture
    /// slots, which are all its atlas's. When the game builds the font again in place (BuildFont), what was borrowed is
    /// freed, and taken again.
    /// </summary>
    private void Sync(AtkFontManager.Font* copy, CopyInfo info)
    {
        var sized = info.Sized;
        *copy = *(AtkFontManager.Font*)info.Original;
        copy->Size = sized.Px;
        copy->Ascent = sized.Ascent;
        copy->LineHeight = sized.LineHeight;

        // Neither the narrower-glyph substitution nor the game's kerning pairs apply to these glyphs.
        copy->Secondary = null;
        copy->SecondaryRatio = 0;
        copy->KerningCount = 0;

        // Real italics are spaced by shaping: no gap after them for sheared glyphs leaning past their advance. Measuring
        // reads it from the copy too, so it stays as drawn.
        copy->ItalicCorrection = 0;

        // The edge and glare shaders step by one texel of this width (it goes into every vertex): the pages', which the
        // game font's textures may not share (the lobby fonts' are smaller), else outlines sample several texels apart.
        // Claiming a narrower width makes the step, and so the edge, that many times wider; the plugin's edge shader takes
        // the step as its radius.
        var width = (ushort)Math.Clamp(Misc.StdRound(sized.Atlas.Width / this.Edge.GetWidth(sized.Px)), 256, ushort.MaxValue);
        copy->TextureWidth = width;
        copy->TextureHeight = width;

        // None of the game font's textures: they belong to its resource handles, which the game releases on its own.
        for (var i = 0; i < AtkFontManager.Font.MaxTextures; i++)
            copy->TextureResourceHandles[i] = default;
        this.ApplyPages(copy, sized.Atlas);
    }

    private AtkFontManager.Font* GetOrCreateCopy(AtkFontManager.Font* original, int halfPx)
    {
        var key = ((Pointer<AtkFontManager.Font>)original, halfPx);
        if (this._copiesByKey.TryGetValue(key, out var existing))
            return existing;

        var copy = (AtkFontManager.Font*)NativeMemory.Alloc((nuint)sizeof(AtkFontManager.Font));
        var info = new CopyInfo(original, halfPx, this.GetSized(original, halfPx));
        this.Sync(copy, info);
        this._copies.Add(copy, info);
        this._copiesByKey.Add(key, copy);
        return copy;
    }

    /// <summary>
    /// Gives a copy every texture slot: its family atlas's pages, and the blank texture in the slots after them, so that
    /// the renderer has a vertex buffer and a texture for every texture index. Main thread only.
    /// </summary>
    private void ApplyPages(AtkFontManager.Font* copy, GlyphAtlas atlas)
    {
        Misc.DebugAssertMainThread();
        var blank = this._blankTexture.Kernel;
        var pages = Math.Min(atlas.PageCount, AtkFontManager.Font.MaxTextures);
        for (var i = 0; i < AtkFontManager.Font.MaxTextures; i++)
            copy->Textures[i] = i < pages ? atlas.GetKernelTexture(i) : blank;
        copy->TextureCount = AtkFontManager.Font.MaxTextures;
    }

    private void OnPageAdded(GlyphAtlas atlas)
    {
        if (Environment.CurrentManagedThreadId != this._mainThreadId)
        {
            lock (this._pendingPageAtlases)
                this._pendingPageAtlases.Add(atlas);
            return;
        }

        foreach (var (copy, info) in this._copies)
        {
            if (info.Sized.Atlas == atlas)
                this.ApplyPages(copy, atlas);
        }
    }

    /// <summary>Gives copies the pages added off the main thread. Main thread.</summary>
    private void ApplyPendingPages()
    {
        GlyphAtlas[] pending;
        lock (this._pendingPageAtlases)
        {
            if (this._pendingPageAtlases.Count == 0)
                return;
            pending = [.. this._pendingPageAtlases];
            this._pendingPageAtlases.Clear();
        }

        foreach (var atlas in pending)
            this.OnPageAdded(atlas);
    }

    /// <summary>A copy of a game font at a size in half pixels: its game font, and the glyphs of its family at its size.</summary>
    private sealed class CopyInfo(Pointer<AtkFontManager.Font> original, int halfPx, SizedFont sized)
    {
        public Pointer<AtkFontManager.Font> Original { get; } = original;

        public int HalfPx { get; } = halfPx;

        public SizedFont Sized { get; set; } = sized;
    }
}
