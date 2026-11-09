using System;

using Dalamud.Hooking;
using Dalamud.Utility;

using FFXIVClientStructs.FFXIV.Component.GUI;

using FontChanger.DalamudPlugin.Fonts.Enums;
using FontChanger.DalamudPlugin.Fonts.Glyphs;
using FontChanger.DalamudPlugin.Fonts.Text;
using FontChanger.DalamudPlugin.PluginInterface;

namespace FontChanger.DalamudPlugin.Fonts.Hooks;

/// <summary>Has font sets pick copies, and answers glyph lookups in them. Anything that fails turns replacing off.</summary>
internal sealed unsafe class FontHooks : IDisposable
{
    // A glyph's box is a line high and at most 255 pixels. Text whose lines are taller is drawn from a smaller copy, scaled up by the
    // renderer.
    private const int MaxDrawnLineHeight = byte.MaxValue;

    // AtkResNode.Type of the first component node type. Those after are components too.
    private const ushort FirstComponentNodeType = 1000;

    // Its callers lay the text out with the font set it returns, and restore the set afterwards.
    // Calls, which matched every version since 5.55, unlike PrepareNode's prologue (since 7.30).
    private const string PrepareMeasureSignature = "E8 ?? ?? ?? ?? 48 8B D8 F7 80 ?? ?? ?? ?? 00 00 00 10";
    // The text wrapper's layout, at its only call.
    private const string TextWrapLayoutSignature = "E8 ?? ?? ?? ?? 48 8B 4B 70 8B F8";
    private const string PrepareNodeSignature = "E8 ?? ?? ?? ?? 48 83 BF ?? ?? ?? ?? 00 74 ?? 48 8B CE E8";

    private readonly CopyFonts _copies;
    private readonly SizedFontRegistry _sized;
    private readonly GlyphStore _store;
    private readonly FrameClock _clock;
    private readonly EnabledState _enabled;
    private readonly FontSetFlags _flags;
    private readonly GlyphLookup _lookup;
    private readonly DisposeSafety.ScopedFinalizer _finalizer = new();

    private readonly Hook<PickFontDelegate> _pickFontHook;
    private readonly Hook<GlyphLookup.GameGlyphDelegate> _getGlyphHook;
    private readonly Hook<FontCacheDelegate> _buildFontCacheHook;
    private readonly Hook<LayOutCharacterDelegate> _layOutCharacterHook;
    private readonly Hook<PrepareMeasureDelegate>? _prepareMeasureHook;
    private readonly Hook<TextWrapLayoutDelegate>? _textWrapLayoutHook;
    private readonly Hook<PrepareNodeDelegate>? _prepareNodeHook;
    private readonly nint _rendererVtbl;
    private readonly nint _renderCountVtbl;

    public FontHooks(
        CopyFonts copies,
        SizedFontRegistry sized,
        GameTextLayout layout,
        GlyphFactory factory,
        CellPlacer placer,
        GlyphStore store,
        FrameClock clock,
        EnabledState enabled,
        FontSetFlags flags)
    {
        this._copies = copies;
        this._sized = sized;
        this._store = store;
        this._clock = clock;
        this._enabled = enabled;
        this._flags = flags;
        try
        {
            this._rendererVtbl = AtkFontAnalyzerRenderer.Addresses.StaticVirtualTable.Value;
            this._renderCountVtbl = AtkFontAnalyzerRenderCount.Addresses.StaticVirtualTable.Value;
            this._getGlyphHook = this.CreateHook<GlyphLookup.GameGlyphDelegate>(AtkFontManager.FontSet.Addresses.GetGlyph.Value, this.GetGlyphDetour);
            this._pickFontHook = this.CreateHook<PickFontDelegate>(AtkFontManager.FontSet.Addresses.PickFont.Value, this.PickFontDetour);
            this._buildFontCacheHook = this.CreateHook<FontCacheDelegate>(AtkTextNode.Addresses.BuildFontCache.Value, this.BuildFontCacheDetour);
            this._layOutCharacterHook = this.CreateHook<LayOutCharacterDelegate>(AtkFontAnalyzerBase.Addresses.LayOutCharacter.Value, this.LayOutCharacterDetour);
            if (Plugin.SigScanner.TryScanText(PrepareMeasureSignature, out var prepareMeasure))
                this._prepareMeasureHook = this.CreateHook<PrepareMeasureDelegate>(prepareMeasure, this.PrepareMeasureDetour);
            else
                Plugin.Log.Warning("Text measuring setup not found: faces and a node's bold are measured as the game's");

            if (Plugin.SigScanner.TryScanText(TextWrapLayoutSignature, out var textWrapLayout))
                this._textWrapLayoutHook = this.CreateHook<TextWrapLayoutDelegate>(textWrapLayout, this.TextWrapLayoutDetour);
            else
                Plugin.Log.Warning("Text wrapper's layout not found: text is wrapped with the glyphs of its unscaled size");

            if (Plugin.SigScanner.TryScanText(PrepareNodeSignature, out var prepareNode))
                this._prepareNodeHook = this.CreateHook<PrepareNodeDelegate>(prepareNode, this.PrepareNodeDetour);
            else
                Plugin.Log.Warning("Text drawing setup not found: chat tabs are drawn in their family's fonts");

            this._lookup = new(copies, sized, layout, factory, placer, store, clock, flags, this._getGlyphHook.Original);
            this._getGlyphHook.Enable();
            this._pickFontHook.Enable();
            this._buildFontCacheHook.Enable();
            this._layOutCharacterHook.Enable();
            this._prepareMeasureHook?.Enable();
            this._textWrapLayoutHook?.Enable();
            this._prepareNodeHook?.Enable();
        }
        catch
        {
            this._finalizer.Dispose();
            throw;
        }
    }

    private delegate AtkFontManager.Font* PickFontDelegate(AtkFontManager.FontSet* set, byte useCache);

    private delegate void FontCacheDelegate(AtkTextNode* node);

    private delegate byte* LayOutCharacterDelegate(AtkFontAnalyzerBase* analyzer, byte** text, AtkFontAnalyzerState* state);

    private delegate AtkFontManager.FontSet* PrepareMeasureDelegate(AtkTextNode* node, byte considerScale);

    private delegate int TextWrapLayoutDelegate(nint wrapper, byte* text);

    private delegate void PrepareNodeDelegate(nint renderer, nint outArea, AtkFontManager.FontSet* set, AtkTextNode* node);

    public void Dispose() => this._finalizer.Dispose();

    /// <summary>
    /// A font cache keeps the glyph pointers laid out at the node's unscaled size, which fit only that size's copy and must not
    /// outlive the plugin, so while replacing, nodes get no cache and are laid out at each draw.
    /// </summary>
    public void UpdateFontCaches()
    {
        var stage = AtkStage.Instance();
        var manager = stage is null ? null : stage->RaptureAtkUnitManager;
        if (manager is null)
            return;
        ref var loaded = ref manager->AtkUnitManager.AllLoadedUnitsList;
        for (var i = 0; i < loaded.Count; i++)
        {
            var unit = loaded.Entries[i].Value;
            if (unit is not null)
                this.UpdateFontCaches(&unit->UldManager, 0);
        }
    }

    private Hook<T> CreateHook<T>(nint address, T detour)
        where T : Delegate =>
        this._finalizer.Add(Plugin.GameInterop.HookFromAddress(address, detour));

    private void UpdateFontCaches(AtkUldManager* uld, int depth)
    {
        if (depth > 16 || uld->NodeList is null)
            return;
        for (var i = 0; i < uld->NodeListCount; i++)
        {
            var node = uld->NodeList[i];
            if (node is null)
                continue;
            var type = (ushort)node->Type;
            if (type == (ushort)NodeType.Text)
            {
                var text = (AtkTextNode*)node;
                if (this._enabled.IsOn)
                {
                    if (text->FontCacheHandle != 0)
                        text->FreeFontCache();
                }
                else if ((text->TextFlags & TextFlags.FontCache) != 0)
                {
                    this._buildFontCacheHook.Original(text);
                }
            }
            else if (type >= FirstComponentNodeType)
            {
                var component = ((AtkComponentNode*)node)->Component;
                if (component is not null)
                    this.UpdateFontCaches(&component->UldManager, depth + 1);
            }
        }
    }

    private void BuildFontCacheDetour(AtkTextNode* node)
    {
        if (this._enabled.IsOn)
            node->FreeFontCache();
        else
            this._buildFontCacheHook.Original(node);
    }

    private void PrepareNodeDetour(nint renderer, nint outArea, AtkFontManager.FontSet* set, AtkTextNode* node)
    {
        this._prepareNodeHook!.Original(renderer, outArea, set, node);
        if (set is not null && node is not null)
            this._flags.ApplyNodeFace(set, node, this._flags.GetNodeFaces(node));
    }

    private AtkFontManager.FontSet* PrepareMeasureDetour(AtkTextNode* node, byte considerScale)
    {
        var set = this._prepareMeasureHook!.Original(node, considerScale);
        if (set is null || node is null)
            return set;

        var faces = this._flags.GetNodeFaces(node);
        this._flags.ApplyNodeFace(set, node, faces);

        // The game measures a node's bold upright, as it draws it.
        set->DrawFlags = (node->TextFlags & TextFlags.Bold) != 0 ? set->DrawFlags | FontSetFlags.BoldFlag : set->DrawFlags & ~FontSetFlags.BoldFlag;

        // Hinting rounds glyphs of different sizes differently, so text measured in the node's units (a speech balloon's) is laid
        // out with the glyphs it is drawn with, of its size on screen. Nameplate bakes are left alone, being drawn at a scale of
        // their own.
        if (this._enabled.IsOn && considerScale == 0 && FontSetFlags.PickScaleOf(set) <= 0 && !TextFaceNames.IsNamePlates(faces.Face))
        {
            ref readonly var m = ref node->Transform;
            var scale = MathF.Sqrt((m.M21 * m.M21) + (m.M22 * m.M22));
            if (float.IsFinite(scale) && scale > 0 && MathF.Abs(scale - 1) > 0.001f)
                FontSetFlags.PickScaleOf(set) = scale;
        }

        return set;
    }

    /// <summary>
    /// The text may come from a script or an addon, so there is no node to tell the scale it is drawn at. As glyphs of different
    /// sizes don't scale evenly, it is wrapped to fit at every UI scale.
    /// </summary>
    private int TextWrapLayoutDetour(nint wrapper, byte* text)
    {
        var outer = LayoutCall.Wrapping;
        LayoutCall.Wrapping = this._enabled.IsOn;
        try
        {
            return this._textWrapLayoutHook!.Original(wrapper, text);
        }
        finally
        {
            LayoutCall.Wrapping = outer;
        }
    }

    private AtkFontManager.Font* PickFontDetour(AtkFontManager.FontSet* set, byte useCache)
    {
        var font = this._pickFontHook.Original(set, useCache);
        if (font is null)
            return font;

        // The picker's cache is keyed by its own size, not the one copies are chosen by, so a copy picked before is decided again.
        if (this._copies.TryGet(font, out var picked))
            font = picked.Original;

        // A font not built yet (its glyph map may be gone) is left alone.
        if (!this._enabled.IsOn || !font->Flags.HasFlag(AtkFontManager.FontFlags.Ready))
            return font;

        try
        {
            // Picked for the on-screen size, even where the game picked for another (a node's fixed font resolution, or a pick
            // scale of its own), so that the renderer doesn't scale the glyphs. Text laid out unscaled takes its pick scale's.
            var size = set->ScaledSizeY;
            var pickScale = FontSetFlags.PickScaleOf(set);
            if (set->NodeScaleX == 1 && set->NodeScaleY == 1)
            {
                if (pickScale > 0 && float.IsFinite(pickScale))
                    size = FontSetFlags.UnscaledSizeYOf(set) * pickScale;
                else if (LayoutCall.Wrapping)
                    size *= GlyphLookup.WrapScales[^1];
            }

            var halfPx = GlyphLookup.HalfPxOf(size);
            var face = this._sized.Fonts.ResolveFace(this._flags.FaceOf(set));
            var drawn = size;
            if (this._sized.Get(font, halfPx, face, false).LineHeight is var lineHeight && lineHeight > MaxDrawnLineHeight)
            {
                // Line metrics are rounded, so the size whose lines fit is checked.
                halfPx = Math.Max(GlyphLookup.MinHalfPx, halfPx * MaxDrawnLineHeight / lineHeight);
                while (halfPx > GlyphLookup.MinHalfPx && this._sized.Get(font, halfPx, face, false).LineHeight > MaxDrawnLineHeight)
                    halfPx--;
                drawn = halfPx / 2f;
            }

            var copy = this._copies.GetOrCreate(font, halfPx, face);

            // Any renderer scale but exactly 1 resamples bilinearly (soft text), while a quarter pixel of size error doesn't show.
            if (halfPx < GlyphLookup.MaxHalfPx && halfPx > GlyphLookup.MinHalfPx)
                copy->Size = drawn;
            set->CurrentFont = copy;
            return copy;
        }
        catch (Exception ex)
        {
            this._enabled.Fail(ex, "Replacing a font failed");
            return font;
        }
    }

    private byte* LayOutCharacterDetour(AtkFontAnalyzerBase* analyzer, byte** text, AtkFontAnalyzerState* state)
    {
        var outer = LayoutCall.Current;
        var vtbl = *(nint*)analyzer;
        LayoutCall.Current = new()
        {
            Character = *text,
            MeasuringOnly = vtbl != this._rendererVtbl && vtbl != this._renderCountVtbl,
            State = state,
        };
        try
        {
            return this._layOutCharacterHook.Original(analyzer, text, state);
        }
        finally
        {
            // The italic macro reads the bit when italics end (the glyph after moves by the font's italic correction).
            if (LayoutCall.Current.ItalicCleared)
                state->Flags |= AtkFontManager.DrawFlags.Italic;
            LayoutCall.Current = outer;
        }
    }

    private AtkFontManager.Glyph* GetGlyphDetour(AtkFontManager.FontSet* set, uint utf8Value, AtkFontManager.Font* font)
    {
        if (!this._copies.TryGet(font, out var info))
            return this._getGlyphHook.Original(set, utf8Value, font);

        var glyph = this._lookup.Get(set, utf8Value, info);
        this._store.Hand(glyph, this._clock.Frame);
        return glyph;
    }
}
