using System;

using Dalamud.Hooking;
using Dalamud.Utility;

using FFXIVClientStructs.FFXIV.Component.GUI;

using FontChanger.DalamudPlugin.Fonts.Enums;
using FontChanger.DalamudPlugin.Fonts.Glyphs;
using FontChanger.DalamudPlugin.Fonts.Text;
using FontChanger.DalamudPlugin.Game;
using FontChanger.DalamudPlugin.PluginInterface;
using FontChanger.DalamudPlugin.Utilities;

namespace FontChanger.DalamudPlugin.Fonts.Hooks;

/// <summary>Has font sets pick copies, and answers glyph lookups in them. Anything that fails turns replacing off.</summary>
internal sealed unsafe class TextLayoutHooks : IDisposable
{
    private readonly ReplacementFontTable _copies;
    private readonly GlyphStore _store;
    private readonly FrameState _state;
    private readonly LayoutContext _context;
    private readonly ReplacementGlyphLookup _lookup;
    private readonly DisposeSafety.ScopedFinalizer _finalizer = new();

    private readonly Hook<PickFontDelegate> _pickFontHook;
    private readonly Hook<ReplacementGlyphLookup.GameGlyphDelegate> _getGlyphHook;
    private readonly Hook<FontCacheDelegate> _buildFontCacheHook;
    private readonly Hook<LayOutCharacterDelegate> _layOutCharacterHook;
    private readonly Hook<AtkTextLayoutAdditions.PrepareMeasureDelegate>? _prepareMeasureHook;
    private readonly Hook<AtkTextLayoutAdditions.TextWrapLayoutDelegate>? _textWrapLayoutHook;
    private readonly Hook<AtkTextLayoutAdditions.PrepareNodeDelegate>? _prepareNodeHook;
    private readonly nint _rendererVtbl;
    private readonly nint _renderCountVtbl;

    public TextLayoutHooks(
        ReplacementFontTable copies,
        SizedFontRegistry sized,
        ShapedRunCache layout,
        GlyphFactory factory,
        GlyphAtlasPlacer placer,
        GlyphStore store,
        FrameState state,
        LayoutContext context)
    {
        this._copies = copies;
        this._store = store;
        this._state = state;
        this._context = context;
        try
        {
            this._rendererVtbl = AtkFontAnalyzerRenderer.Addresses.StaticVirtualTable.Value;
            this._renderCountVtbl = AtkFontAnalyzerRenderCount.Addresses.StaticVirtualTable.Value;
            this._getGlyphHook = this.CreateHook<ReplacementGlyphLookup.GameGlyphDelegate>(AtkFontManager.FontSet.Addresses.GetGlyph.Value, this.GetGlyphDetour);
            this._pickFontHook = this.CreateHook<PickFontDelegate>(AtkFontManager.FontSet.Addresses.PickFont.Value, this.PickFontDetour);
            this._buildFontCacheHook = this.CreateHook<FontCacheDelegate>(AtkTextNode.Addresses.BuildFontCache.Value, this.BuildFontCacheDetour);
            this._layOutCharacterHook = this.CreateHook<LayOutCharacterDelegate>(AtkFontAnalyzerBase.Addresses.LayOutCharacter.Value, this.LayOutCharacterDetour);
            if (Plugin.SigScanner.TryScanText(AtkTextLayoutAdditions.PrepareMeasureSignature, out var prepareMeasure))
                this._prepareMeasureHook = this.CreateHook<AtkTextLayoutAdditions.PrepareMeasureDelegate>(prepareMeasure, this.PrepareMeasureDetour);
            else
                Plugin.Log.Warning("Text measuring setup not found: faces and a node's bold are measured as the game's");

            if (Plugin.SigScanner.TryScanText(AtkTextLayoutAdditions.TextWrapLayoutSignature, out var textWrapLayout))
                this._textWrapLayoutHook = this.CreateHook<AtkTextLayoutAdditions.TextWrapLayoutDelegate>(textWrapLayout, this.TextWrapLayoutDetour);
            else
                Plugin.Log.Warning("Text wrapper's layout not found: text is wrapped with the glyphs of its unscaled size");

            if (Plugin.SigScanner.TryScanText(AtkTextLayoutAdditions.PrepareNodeSignature, out var prepareNode))
                this._prepareNodeHook = this.CreateHook<AtkTextLayoutAdditions.PrepareNodeDelegate>(prepareNode, this.PrepareNodeDetour);
            else
                Plugin.Log.Warning("Text drawing setup not found: chat tabs are drawn in their family's fonts");

            this._lookup = new(copies, sized, layout, factory, placer, store, state, context, this._getGlyphHook.Original);
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
                if (this._state.IsOn)
                {
                    if (text->FontCacheHandle != 0)
                        text->FreeFontCache();
                }
                else if ((text->TextFlags & TextFlags.FontCache) != 0)
                {
                    this._buildFontCacheHook.Original(text);
                }
            }
            else if (type >= AtkResNodeAdditions.FirstComponentNodeType)
            {
                var component = ((AtkComponentNode*)node)->Component;
                if (component is not null)
                    this.UpdateFontCaches(&component->UldManager, depth + 1);
            }
        }
    }

    private void BuildFontCacheDetour(AtkTextNode* node)
    {
        if (this._state.IsOn)
            node->FreeFontCache();
        else
            this._buildFontCacheHook.Original(node);
    }

    private void PrepareNodeDetour(nint renderer, nint outArea, AtkFontManager.FontSet* set, AtkTextNode* node)
    {
        this._prepareNodeHook!.Original(renderer, outArea, set, node);
        if (set is not null && node is not null)
            this._context.ApplyNodeFace(set, node, this._context.GetNodeFaces(node));
    }

    private AtkFontManager.FontSet* PrepareMeasureDetour(AtkTextNode* node, byte considerScale)
    {
        var set = this._prepareMeasureHook!.Original(node, considerScale);
        if (set is null || node is null)
            return set;

        var faces = this._context.GetMeasuredNodeFaces(node);
        this._context.ApplyNodeFace(set, node, faces);

        // The game measures a node's bold upright, as it draws it.
        set->DrawFlags = (node->TextFlags & TextFlags.Bold) != 0 ? set->DrawFlags | LayoutContext.BoldFlag : set->DrawFlags & ~LayoutContext.BoldFlag;

        // Hinting rounds glyphs of different sizes differently, so text measured in the node's units (a speech balloon's) is laid
        // out with the glyphs it is drawn with, of its size on screen. Nameplate bakes are left alone, being drawn at a scale of
        // their own.
        if (this._state.IsOn && considerScale == 0 && !LayoutContext.HasPickScale(set) && !TextFaceNames.IsNamePlates(faces.Face))
        {
            ref readonly var m = ref node->Transform;
            var scale = MathF.Sqrt((m.M21 * m.M21) + (m.M22 * m.M22));
            if (float.IsFinite(scale) && scale > 0 && MathF.Abs(scale - 1) > 0.001f)
                set->PickScale = scale;
        }

        return set;
    }

    /// <summary>
    /// The text may come from a script or an addon, so there is no node to tell the scale it is drawn at. As glyphs of different
    /// sizes don't scale evenly, it is wrapped to fit at every UI scale.
    /// </summary>
    private int TextWrapLayoutDetour(nint wrapper, byte* text)
    {
        Misc.DebugAssertMainThread();
        var outer = this._context.EnterWrap(this._state.IsOn);
        try
        {
            return this._textWrapLayoutHook!.Original(wrapper, text);
        }
        finally
        {
            this._context.LeaveWrap(outer);
        }
    }

    private AtkFontManager.Font* PickFontDetour(AtkFontManager.FontSet* set, byte useCache)
    {
        var font = this._pickFontHook.Original(set, useCache);
        if (font is null)
            return font;

        // The picker's cache is keyed by its own size, not the one copies are chosen by, so a copy picked before is decided again.
        font = this._copies.OriginalOf(font);

        // A font not built yet (its glyph map may be gone) is left alone.
        if (!this._state.IsOn || !font->Flags.HasFlag(AtkFontManager.FontFlags.Ready))
            return font;

        try
        {
            var size = this._context.PickSizeOf(set);

            // Text with faces of its own (nameplates, chat tabs) and wrapped text keep what they are laid out with.
            var face = this._context.FaceOf(set);
            var copy = this._copies.Pick(font, size, face, face == TextFace.Default && !this._context.Wrapping, out var halfPx, out var clamped);

            // Any renderer scale but exactly 1 resamples bilinearly (soft text), while a quarter pixel of size error doesn't show.
            if (halfPx is < ReplacementFontTable.MaxHalfPx and > ReplacementFontTable.MinHalfPx)
                copy->Size = clamped ? halfPx / 2f : size;
            set->CurrentFont = copy;
            return copy;
        }
        catch (Exception ex)
        {
            // Failing forgets every set's pick, this one's too, which the game is about to lay out with.
            var pickedSize = set->CurrentFontSize;
            this._state.Fail(ex, "Replacing a font failed");
            set->CurrentFont = font;
            set->CurrentFontSize = pickedSize;
            return font;
        }
    }

    private byte* LayOutCharacterDetour(AtkFontAnalyzerBase* analyzer, byte** text, AtkFontAnalyzerState* state)
    {
        // An invalid lead byte bypasses GetGlyph and a truncated sequence steps past the terminator, so U+3013 is laid out instead.
        Misc.DebugAssertMainThread();
        var p = *text;
        var invalid = this._state.IsOn && *p >= 0x80 && (*p < 0xC0 || *p >= 0xF8 || GameUtf8.CharacterLength(p) == 0);
        var vtbl = *(nint*)analyzer;
        var outer = this._context.EnterCharacter(invalid ? null : p, p, vtbl != this._rendererVtbl && vtbl != this._renderCountVtbl, state);
        try
        {
            if (!invalid)
                return this._layOutCharacterHook.Original(analyzer, text, state);

            fixed (byte* geta = "〓\0"u8)
            {
                *text = geta;
                var res = this._layOutCharacterHook.Original(analyzer, text, state);
                var next = p + 1;
                if (*p is >= 0xC0 and < 0xF8)
                {
                    while (*next != 0)
                        next++;
                }

                *text = next;
                return res;
            }
        }
        finally
        {
            this._context.LeaveCharacter(outer);
        }
    }

    private AtkFontManager.Glyph* GetGlyphDetour(AtkFontManager.FontSet* set, uint utf8Value, AtkFontManager.Font* font)
    {
        if (!this._copies.TryGet(font, out var info))
            return this._getGlyphHook.Original(set, utf8Value, font);

        var glyph = this._lookup.Get(set, utf8Value, info);
        this._store.Hand(glyph, this._state.Frame);
        return glyph;
    }
}
