using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

using FFXIVClientStructs.FFXIV.Component.GUI;
using FFXIVClientStructs.Interop;

using FontChanger.DalamudPlugin.Fonts.Enums;
using FontChanger.DalamudPlugin.Game;
using FontChanger.DalamudPlugin.Utilities;

namespace FontChanger.DalamudPlugin.Fonts;

/// <summary>Copies of the game's fonts at the sizes text is drawn at, which font sets are made to pick.</summary>
/// <remarks>
/// After <see cref="MarkStale"/>, each copy takes its new sized font and borrows from its game font again when next used, as
/// every font set picks again before drawing.
/// </remarks>
internal sealed unsafe class CopyFonts(SizedFontRegistry sized, KernelTextureWrapper blankTexture, FrameState state) : IDisposable
{
    // Sizes in half pixels. Below 4 px nothing is legible.
    public const int MinHalfPx = 8;
    public const int MaxHalfPx = 2 * byte.MaxValue;

    // A glyph's box is a line high and at most 255 pixels. Text whose lines are taller is drawn from a smaller copy, scaled up by the
    // renderer.
    private const int MaxDrawnLineHeight = byte.MaxValue;

    // A size asked for in fewer frames in a row is drawn from the nearest copy, scaled by the renderer, as the sizes a scale
    // animation passes through are each asked for a frame or so.
    private const int SettleFrames = 4;

    private readonly Dictionary<(Pointer<AtkFontManager.Font> Original, int HalfPx, TextFace Face), Pointer<AtkFontManager.Font>> _byKey = [];
    private readonly Dictionary<Pointer<AtkFontManager.Font>, CopyInfo> _copies = [];

    // Picks by the size and face asked for, which the size is clamped and the face resolved from.
    private readonly Dictionary<(Pointer<AtkFontManager.Font> Original, int HalfPx, TextFace Face), (Pointer<AtkFontManager.Font> Copy, int HalfPx)> _picks = [];

    // The sizes asked for that aren't picked yet, with the last frame each was asked in and in how many frames in a row.
    private readonly Dictionary<(Pointer<AtkFontManager.Font> Original, int HalfPx, TextFace Face), (long Frame, int Streak)> _unsettled = [];

    // The sizes of the copies made, by game font and resolved face.
    private readonly Dictionary<(Pointer<AtkFontManager.Font> Original, TextFace Face), List<int>> _madeSizes = [];

    private int _generation;

    // The copy GetGlyph asked about last, as it asks per character.
    private AtkFontManager.Font* _lastCopy;
    private CopyInfo? _lastInfo;

    public static int HalfPxOf(float px) => Math.Clamp((int)Misc.StdRound(px * 2), MinHalfPx, MaxHalfPx);

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

    /// <summary>Brought up to date if stale.</summary>
    public bool TryGet(AtkFontManager.Font* copy, out CopyInfo info)
    {
        if (copy is null || copy != this._lastCopy)
        {
            if (!this._copies.TryGetValue(copy, out info!))
                return false;
            this._lastCopy = copy;
            this._lastInfo = info;
        }

        info = this._lastInfo!;
        if (info.Generation != this._generation)
            this.Resync(copy, info);
        return true;
    }

    /// <summary>The game font a copy is of, else <paramref name="font"/>.</summary>
    public AtkFontManager.Font* OriginalOf(AtkFontManager.Font* font) => this._copies.TryGetValue(font, out var info) ? info.Original : font;

    /// <summary>
    /// The copy for text at <paramref name="px"/> in <paramref name="face"/>, made smaller if its lines would be too tall for
    /// glyph boxes.
    /// </summary>
    /// <param name="original">The game font.</param>
    /// <param name="px">The size text is drawn at.</param>
    /// <param name="face">The face asked for, which falls back to one with fonts.</param>
    /// <param name="mayStandIn">Whether a copy of another size may stand in for one of a size not settled yet, which text
    /// laid out once and kept must not take.</param>
    /// <param name="halfPx">The copy's size in half pixels.</param>
    /// <param name="clamped">Whether the copy is of another size, to be scaled by the renderer.</param>
    /// <returns>The copy.</returns>
    public AtkFontManager.Font* Pick(AtkFontManager.Font* original, float px, TextFace face, bool mayStandIn, out int halfPx, out bool clamped)
    {
        var asked = HalfPxOf(px);
        var key = ((Pointer<AtkFontManager.Font>)original, asked, face);
        if (!this._picks.TryGetValue(key, out var pick) && !(mayStandIn && this.TryStandIn(key, out pick)))
        {
            this._unsettled.Remove(key);
            var resolved = sized.Fonts.ResolveFace(face);
            var drawn = asked;
            if (sized.Get(original, drawn, resolved, false).LineHeight is var lineHeight && lineHeight > MaxDrawnLineHeight)
            {
                // Line metrics are rounded, so the size whose lines fit is checked.
                drawn = Math.Max(MinHalfPx, drawn * MaxDrawnLineHeight / lineHeight);
                while (drawn > MinHalfPx && sized.Get(original, drawn, resolved, false).LineHeight > MaxDrawnLineHeight)
                    drawn--;
            }

            pick = ((Pointer<AtkFontManager.Font>)this.GetOrCreate(original, drawn, resolved), drawn);
            this._picks.Add(key, pick);
        }

        // Picks are forgotten by MarkStale, so the copy is up to date.
        halfPx = pick.HalfPx;
        clamped = pick.HalfPx != asked;
        return pick.Copy;
    }

    public AtkFontManager.Font* GetOrCreate(AtkFontManager.Font* original, int halfPx, TextFace face)
    {
        var key = ((Pointer<AtkFontManager.Font>)original, halfPx, face);
        if (this._byKey.TryGetValue(key, out var existing))
        {
            this.TryGet(existing, out _);
            return existing;
        }

        var copy = (AtkFontManager.Font*)NativeMemory.Alloc((nuint)sizeof(AtkFontManager.Font));
        var info = new CopyInfo(original, halfPx, face, sized.Get(original, halfPx, face, false)) { Generation = this._generation };
        this.Sync(copy, info);
        this._copies.Add(copy, info);
        this._byKey.Add(key, copy);
        if (!this._madeSizes.TryGetValue((key.Item1, face), out var sizes))
            this._madeSizes.Add((key.Item1, face), sizes = []);
        sizes.Add(halfPx);
        return copy;
    }

    /// <summary>The sized fonts or what copies borrow from their game fonts changed.</summary>
    /// <remarks>
    /// Copies borrow again at once, with their old sized fonts, whose atlases are only cleared or else disposed (leaving the
    /// blank texture), so that none keeps what the game freed. Finding the new sized fonts waits for each copy's next use.
    /// </remarks>
    public void MarkStale()
    {
        this._generation++;
        this._picks.Clear();
        this._unsettled.Clear();
        foreach (var (copy, info) in this._copies)
            this.Sync(copy, info);
    }

    /// <summary>For the edge, which copies claim their texture width by.</summary>
    public void SyncAll()
    {
        foreach (var (copy, info) in this._copies)
        {
            if (info.Generation == this._generation)
                this.Sync(copy, info);
        }
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
        this._picks.Clear();
        this._unsettled.Clear();
        this._madeSizes.Clear();
        this._lastCopy = null;
        this._lastInfo = null;
    }

    /// <returns>False once the size is settled, or if no copy of the font and face was made yet.</returns>
    private bool TryStandIn((Pointer<AtkFontManager.Font> Original, int HalfPx, TextFace Face) key, out (Pointer<AtkFontManager.Font> Copy, int HalfPx) pick)
    {
        pick = default;
        var frame = state.Frame;
        var streak = !this._unsettled.TryGetValue(key, out var known) || known.Frame < frame - 1 ? 1
                     : known.Frame == frame ? known.Streak
                     : known.Streak + 1;
        this._unsettled[key] = (frame, streak);
        var resolved = sized.Fonts.ResolveFace(key.Face);
        if (streak >= SettleFrames || !this._madeSizes.TryGetValue((key.Original, resolved), out var sizes))
            return false;

        var nearest = sizes[0];
        foreach (var size in sizes)
        {
            if (Math.Abs(size - key.HalfPx) < Math.Abs(nearest - key.HalfPx))
                nearest = size;
        }

        pick = ((Pointer<AtkFontManager.Font>)this.GetOrCreate(key.Original, nearest, resolved), nearest);
        return true;
    }

    private void Resync(AtkFontManager.Font* copy, CopyInfo info)
    {
        info.Resize(sized.Get(info.Original, info.HalfPx, info.Face, false));
        info.Generation = this._generation;
        this.Sync(copy, info);
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
        var width = (ushort)Math.Clamp(Misc.StdRound(s.Atlas.Width / state.Edge.GetWidth(s.Px)), 256, ushort.MaxValue);
        copy->TextureWidth = width;
        copy->TextureHeight = width;

        // The game font's textures belong to its resource handles, which the game releases on its own.
        for (var i = 0; i < AtkFontManager.Font.MaxTextures; i++)
            copy->TextureResourceHandles[i] = default;
        this.ApplyPages(copy, s.Atlas);
    }

    /// <summary>
    /// The blank texture fills the slots after the pages, so that the renderer has a vertex buffer and a texture for every texture
    /// index. The atlas's <see cref="GlyphAtlas.BlankSlot"/> never gets a page.
    /// </summary>
    private void ApplyPages(AtkFontManager.Font* copy, GlyphAtlas atlas)
    {
        Misc.DebugAssertMainThread();
        var blank = blankTexture.Kernel;
        var pages = Math.Min(atlas.PageCount, GlyphAtlas.MaxTextureSlots);
        for (var i = 0; i < AtkFontManager.Font.MaxTextures; i++)
            copy->Textures[i] = i < pages ? atlas.GetKernelTexture(i) : blank;
        copy->TextureCount = AtkFontManager.Font.MaxTextures;
    }
}

internal sealed class CopyInfo(Pointer<AtkFontManager.Font> original, int halfPx, TextFace face, SizedFont sized)
{
    private SizedFont? _bold;
    private bool _boldFailed;

    // The sized fonts of the UI scales wrapping last measured with, and what they were for.
    private SizedFont[]? _wrapSizes;
    private float _wrapSizeY;
    private bool _wrapBold;

    public Pointer<AtkFontManager.Font> Original { get; } = original;

    public int HalfPx { get; } = halfPx;

    public TextFace Face { get; } = face;

    public SizedFont Sized { get; private set; } = sized;

    /// <summary>Gets or sets the <see cref="CopyFonts"/> generation it is up to date with.</summary>
    public int Generation { get; set; }

    public void Resize(SizedFont value)
    {
        this.Sized = value;
        this._bold = null;
        this._boldFailed = false;
        this._wrapSizes = null;
    }

    /// <summary>Its bold is drawn upright from now on.</summary>
    public void BoldFailed() => this._boldFailed = true;

    /// <summary>Made on its first use.</summary>
    public SizedFont GetBold(SizedFontRegistry registry) =>
        this._boldFailed ? this.Sized : this._bold ??= registry.Get(this.Original, this.HalfPx, this.Face, true);

    /// <summary>The sized fonts of <paramref name="scales"/> times <paramref name="sizeY"/>, <paramref name="drawn"/> last for the largest.</summary>
    /// <remarks>The array is reused while the arguments are the same, so it must not be changed.</remarks>
    public SizedFont[] GetWrapSizes(SizedFontRegistry registry, float[] scales, float sizeY, bool bold, SizedFont drawn)
    {
        if (this._wrapSizes is { } known && known[^1] == drawn && this._wrapSizeY == sizeY && this._wrapBold == bold)
            return known;

        var sizes = new SizedFont[scales.Length];
        for (var i = 0; i < sizes.Length - 1; i++)
            sizes[i] = registry.Get(this.Original, CopyFonts.HalfPxOf(sizeY * scales[i]), this.Face, bold);
        sizes[^1] = drawn;
        (this._wrapSizes, this._wrapSizeY, this._wrapBold) = (sizes, sizeY, bold);
        return sizes;
    }
}
