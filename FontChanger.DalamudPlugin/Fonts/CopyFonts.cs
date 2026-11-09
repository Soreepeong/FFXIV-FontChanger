using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

using FFXIVClientStructs.FFXIV.Component.GUI;
using FFXIVClientStructs.Interop;

using FontChanger.DalamudPlugin.Configuration;
using FontChanger.DalamudPlugin.Fonts.Enums;
using FontChanger.DalamudPlugin.Game;
using FontChanger.DalamudPlugin.Utilities;

namespace FontChanger.DalamudPlugin.Fonts;

/// <summary>Copies of the game's fonts at the sizes text is drawn at, which font sets are made to pick.</summary>
internal sealed unsafe class CopyFonts(SizedFontRegistry sized, KernelTextureWrapper blankTexture) : IDisposable
{
    private readonly Dictionary<(Pointer<AtkFontManager.Font> Original, int HalfPx, TextFace Face), Pointer<AtkFontManager.Font>> _byKey = [];
    private readonly Dictionary<Pointer<AtkFontManager.Font>, CopyInfo> _copies = [];

    /// <summary>Gets or sets the edge, which copies claim their texture width by.</summary>
    public EdgeSettings Edge { get; set; } = new();

    /// <summary>Has every font set pick its font again.</summary>
    public static void ForgetPicked()
    {
        var stage = AtkStage.Instance();
        var manager = stage is null ? null : stage->AtkFontManager;
        if (manager is null || manager->FontSets is null)
            return;
        for (var i = 0; i < AtkFontManager.FontSetCount; i++)
        {
            var set = manager->FontSets + i;
            set->CurrentFont = null;
            set->CurrentFontSize = 0;
        }
    }

    public bool TryGet(AtkFontManager.Font* copy, out CopyInfo info) => this._copies.TryGetValue(copy, out info!);

    public AtkFontManager.Font* GetOrCreate(AtkFontManager.Font* original, int halfPx, TextFace face)
    {
        var key = ((Pointer<AtkFontManager.Font>)original, halfPx, face);
        if (this._byKey.TryGetValue(key, out var existing))
            return existing;

        var copy = (AtkFontManager.Font*)NativeMemory.Alloc((nuint)sizeof(AtkFontManager.Font));
        var info = new CopyInfo(original, halfPx, face, sized.Get(original, halfPx, face, false));
        this.Sync(copy, info);
        this._copies.Add(copy, info);
        this._byKey.Add(key, copy);
        return copy;
    }

    /// <summary>Copies take new sized fonts, and borrow from their game fonts again.</summary>
    public void ResyncAll()
    {
        foreach (var (copy, info) in this._copies)
        {
            info.Resize(sized.Get(info.Original, info.HalfPx, info.Face, false));
            this.Sync(copy, info);
        }
    }

    /// <summary>For the edge, which copies claim their texture width by.</summary>
    public void SyncAll()
    {
        foreach (var (copy, info) in this._copies)
            this.Sync(copy, info);
    }

    /// <summary>The atlas got a page.</summary>
    public void ApplyPages(GlyphAtlas atlas)
    {
        foreach (var (copy, info) in this._copies)
        {
            if (info.Sized.Atlas == atlas)
                this.ApplyPages(copy, atlas);
        }
    }

    /// <summary>Once no font set has one picked.</summary>
    public void Dispose()
    {
        foreach (var copy in this._copies.Keys)
            NativeMemory.Free(copy.Value);
        this._copies.Clear();
        this._byKey.Clear();
    }

    /// <summary>
    /// Borrows the game font's glyph map, glyphs and flags, keeping the copy's own size, metrics and texture slots. BuildFont
    /// frees what was borrowed, so it is taken again then.
    /// </summary>
    private void Sync(AtkFontManager.Font* copy, CopyInfo info)
    {
        var s = info.Sized;
        *copy = *(AtkFontManager.Font*)info.Original;
        copy->Size = s.Px;
        copy->Ascent = s.Ascent;
        copy->LineHeight = s.LineHeight;

        // Neither the narrower-glyph substitution nor the game's kerning pairs apply to these glyphs.
        copy->Secondary = null;
        copy->SecondaryRatio = 0;
        copy->KerningCount = 0;

        // Real italics are spaced by shaping, so no gap after them for sheared glyphs. Measuring reads it from the copy too.
        copy->ItalicCorrection = 0;

        // The edge and glare shaders step by one texel of this width (it goes into every vertex), which must be the pages'. Claiming
        // a narrower width widens the edge as many times, and the plugin's edge shader takes the step as its radius.
        var width = (ushort)Math.Clamp(Misc.StdRound(s.Atlas.Width / this.Edge.GetWidth(s.Px)), 256, ushort.MaxValue);
        copy->TextureWidth = width;
        copy->TextureHeight = width;

        // The game font's textures belong to its resource handles, which the game releases on its own.
        for (var i = 0; i < AtkFontManager.Font.MaxTextures; i++)
            copy->TextureResourceHandles[i] = default;
        this.ApplyPages(copy, s.Atlas);
    }

    /// <summary>
    /// The blank texture fills the slots after the pages, so that the renderer has a vertex buffer and a texture for every texture
    /// index.
    /// </summary>
    private void ApplyPages(AtkFontManager.Font* copy, GlyphAtlas atlas)
    {
        Misc.DebugAssertMainThread();
        var blank = blankTexture.Kernel;
        var pages = Math.Min(atlas.PageCount, AtkFontManager.Font.MaxTextures);
        for (var i = 0; i < AtkFontManager.Font.MaxTextures; i++)
            copy->Textures[i] = i < pages ? atlas.GetKernelTexture(i) : blank;
        copy->TextureCount = AtkFontManager.Font.MaxTextures;
    }
}

internal sealed class CopyInfo(Pointer<AtkFontManager.Font> original, int halfPx, TextFace face, SizedFont sized)
{
    private SizedFont? _bold;
    private bool _boldFailed;

    public Pointer<AtkFontManager.Font> Original { get; } = original;

    public int HalfPx { get; } = halfPx;

    public TextFace Face { get; } = face;

    public SizedFont Sized { get; private set; } = sized;

    public void Resize(SizedFont value)
    {
        this.Sized = value;
        this._bold = null;
        this._boldFailed = false;
    }

    /// <summary>Its bold is drawn upright from now on.</summary>
    public void BoldFailed() => this._boldFailed = true;

    /// <summary>Made on its first use.</summary>
    public SizedFont GetBold(SizedFontRegistry registry) =>
        this._boldFailed ? this.Sized : this._bold ??= registry.Get(this.Original, this.HalfPx, this.Face, true);
}
