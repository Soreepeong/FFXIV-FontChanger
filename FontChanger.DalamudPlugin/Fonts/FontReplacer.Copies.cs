using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

using FFXIVClientStructs.FFXIV.Component.GUI;
using FFXIVClientStructs.Interop;

using FontChanger.DalamudPlugin.Configuration;
using FontChanger.DalamudPlugin.Fonts.Faces;
using FontChanger.DalamudPlugin.Game;
using FontChanger.DalamudPlugin.Utilities;

namespace FontChanger.DalamudPlugin.Fonts;

internal sealed unsafe partial class FontReplacer
{
    private readonly Dictionary<(Pointer<AtkFontManager.Font> Original, int HalfPx), Pointer<AtkFontManager.Font>> _copiesByKey = [];
    private readonly Dictionary<Pointer<AtkFontManager.Font>, CopyInfo> _copies = [];
    private readonly Dictionary<(MaterializedFont Font, int HalfPx, string FontName, bool Bold), SizedFont> _sizedFonts = [];

    // Each game font family gets an atlas of its own, so that one family's text can't push another's out; AXIS, the family of most
    // of the UI's text, has its own page size and count.
    private readonly Dictionary<string, GlyphAtlas> _atlases = new(StringComparer.OrdinalIgnoreCase);

    // Copy texture slots are changed only on the thread the game lays text out on; pages added elsewhere wait for it.
    private readonly int _mainThreadId;
    private readonly HashSet<GlyphAtlas> _pendingPageAtlases = [];

    /// <summary>Returns false if there were no copies (of <paramref name="only"/>, if given).</summary>
    private bool ResyncCopies(AtkFontManager.Font* only = null)
    {
        var any = false;
        foreach (var (copy, info) in this._copies)
        {
            if (only is not null && info.Original != only)
                continue;
            info.Sized = this.GetSized(info.Original, info.HalfPx, false);
            info.BoldSized = null;
            this.Sync(copy, info);
            any = true;
        }

        return any;
    }

    /// <summary>
    /// At the size of one of the family's fonts, that font's glyphs with its game font's metrics; at another, each glyph from the
    /// largest font that draws it itself, with the metrics of the font that draws the most glyphs itself.
    /// </summary>
    private SizedFont GetSized(Pointer<AtkFontManager.Font> original, int halfPx, bool bold)
    {
        var px = halfPx / 2f;
        var fonts = new List<(string Name, float Size, MaterializedFont Font, Pointer<AtkFontManager.Font> Game)>();
        if (FontNames.TryGetFontName(original, out var originalName))
        {
            foreach (var (name, size) in FontNames.FontNamesOf(Preset.FamilyOf(originalName)).OrderByDescending(f => f.Size))
            {
                var loaded = original;
                if (!string.Equals(name, originalName, StringComparison.OrdinalIgnoreCase) && !FontNames.TryFindFont(name, out loaded))
                    continue;
                if (loaded.IsNull || !loaded.Value->Flags.HasFlag(AtkFontManager.FontFlags.Ready) || loaded.Value->Size <= 0)
                    continue;
                fonts.Add((name, size, this.FontOf(name), loaded));
            }
        }

        if (fonts.Count == 0)
        {
            var name = originalName ?? $"0x{(nint)original.Value:X}";
            fonts = [(name, 0, originalName is not null ? this.FontOf(originalName) : this._gameFont, original)];
        }
        else if (fonts.FindIndex(f => MathF.Abs(f.Size - px) < 0.01f) is var exact and >= 0)
        {
            fonts = [fonts[exact]];
        }

        var best = 0;
        for (var i = 1; i < fonts.Count; i++)
        {
            if (fonts[i].Font.OwnCodepointCount > fonts[best].Font.OwnCodepointCount)
                best = i;
        }

        var (fontName, _, font, game) = fonts[best];
        var key = (font, halfPx, fontName, bold);
        if (!this._sizedFonts.TryGetValue(key, out var sized))
        {
            var (ascent, lineHeight) = font.GetLineMetrics(px, game);
            sized = new(font, px, ascent, lineHeight, game, this.GetAtlas(Preset.FamilyOf(fontName)), fonts.Select(f => (f.Font, f.Game)).ToList(), bold);
            this._sizedFonts.Add(key, sized);
        }

        return sized;
    }

    private SizedFont GetBoldSized(CopyInfo info) => info.BoldSized ??= this.GetSized(info.Original, info.HalfPx, true);

    private MaterializedFont FontOf(string fontName) => this._presetFonts.GetValueOrDefault(fontName) ?? this._gameFont;

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

    private void DisposeAtlases()
    {
        foreach (var atlas in this._atlases.Values)
            atlas.Dispose();
        this._atlases.Clear();
    }

    /// <summary>
    /// Borrows the game font's glyph map, glyphs and flags, keeping the copy's own size, metrics and texture slots; BuildFont
    /// frees what was borrowed, so it is taken again then.
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

        // Real italics are spaced by shaping, so no gap after them for sheared glyphs; measuring reads it from the copy too.
        copy->ItalicCorrection = 0;

        // The edge and glare shaders step by one texel of this width (it goes into every vertex), which must be the pages'; claiming
        // a narrower width widens the edge as many times, and the plugin's edge shader takes the step as its radius.
        var width = (ushort)Math.Clamp(Misc.StdRound(sized.Atlas.Width / this.Edge.GetWidth(sized.Px)), 256, ushort.MaxValue);
        copy->TextureWidth = width;
        copy->TextureHeight = width;

        // The game font's textures belong to its resource handles, which the game releases on its own.
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
        var info = new CopyInfo(original, halfPx, this.GetSized(original, halfPx, false));
        this.Sync(copy, info);
        this._copies.Add(copy, info);
        this._copiesByKey.Add(key, copy);
        return copy;
    }

    private void FreeCopies()
    {
        foreach (var copy in this._copies.Keys)
            NativeMemory.Free(copy.Value);
        this._copies.Clear();
        this._copiesByKey.Clear();
    }

    /// <summary>
    /// The blank texture fills the slots after the pages, so that the renderer has a vertex buffer and a texture for every texture
    /// index.
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

    private void ApplyPendingPages()
    {
        Misc.DebugAssertMainThread();
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

    private sealed class CopyInfo(Pointer<AtkFontManager.Font> original, int halfPx, SizedFont sized)
    {
        public Pointer<AtkFontManager.Font> Original { get; } = original;

        public int HalfPx { get; } = halfPx;

        public SizedFont Sized { get; set; } = sized;

        public SizedFont? BoldSized { get; set; }
    }
}
