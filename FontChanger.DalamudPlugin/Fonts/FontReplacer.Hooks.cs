using System;
using System.Collections.Generic;
using System.Linq;

using Dalamud.Hooking;

using FFXIVClientStructs.FFXIV.Component.GUI;
using FFXIVClientStructs.Interop;

using FontChanger.DalamudPlugin.Fonts.Enums;
using FontChanger.DalamudPlugin.PluginInterface;
using FontChanger.DalamudPlugin.Utilities;

using KernelTexture = FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Texture;

namespace FontChanger.DalamudPlugin.Fonts;

internal sealed unsafe partial class FontReplacer
{
    // Sizes in half pixels: below 4 px nothing is legible; a request over 255 px uses the 255 px copy, scaled up by the renderer.
    private const int MinHalfPx = 8;
    private const int MaxHalfPx = 2 * byte.MaxValue;

    // AtkResNode.Type of the first component node type; those after are components too.
    private const ushort FirstComponentNodeType = 1000;

    // Its callers lay the text out with the font set it returns, and restore the set afterwards.
    // Calls, which matched every version since 5.55, unlike PrepareNode's prologue (since 7.30).
    private const string PrepareMeasureSignature = "E8 ?? ?? ?? ?? 48 8B D8 F7 80 ?? ?? ?? ?? 00 00 00 10";

    // Set by TextFlags.Bold (when drawing) and the bold macro. The game reads it nowhere.
    internal const AtkFontManager.DrawFlags BoldFlag = (AtkFontManager.DrawFlags)(1u << 26);

    // The game neither sets nor reads it, and restores the set's flags after each layout, so it can't outlive the text it marks.
    private const AtkFontManager.DrawFlags FaceFlag = (AtkFontManager.DrawFlags)(1u << 25);

    // AtkFontManager's buffer that multiline text is drawn from, a line at a time.
    private const int LineBufferOffset = 0x48;
    private const int LineBufferSize = 0x2000;

    private readonly (NodeFaces Faces, nint Text, int Length)[] _setFaces = new (NodeFaces, nint, int)[AtkFontManager.FontSetCount];
    private readonly List<Func<Pointer<AtkTextNode>, NodeFaces?>> _nodeFaces = [];

    // What LayOutCharacter lays out a character a font has no glyph of as, in order of preference (packed UTF-8): U+3013, '-', U+3400.
    private static readonly uint[] MissingGlyphSubstitutes = [0xE38093, 0x2D, 0xE39080];

    [ThreadStatic]
    private static LayoutContext _current;

    private readonly Hook<PickFontDelegate> _pickFontHook;
    private readonly Hook<GetGlyphDelegate> _getGlyphHook;
    private readonly Hook<FontCacheDelegate> _buildFontCacheHook;
    private readonly Hook<LayOutCharacterDelegate> _layOutCharacterHook;
    private readonly Hook<BuildFontDelegate> _buildFontHook;
    private readonly Hook<UnloadFontsDelegate> _unloadFontsHook;
    private readonly Hook<PrepareMeasureDelegate>? _prepareMeasureHook;
    private readonly nint _rendererVtbl;
    private readonly nint _renderCountVtbl;

    private delegate AtkFontManager.Font* PickFontDelegate(AtkFontManager.FontSet* set, byte useCache);

    private delegate AtkFontManager.Glyph* GetGlyphDelegate(AtkFontManager.FontSet* set, uint utf8Value, AtkFontManager.Font* font);

    private delegate void FontCacheDelegate(AtkTextNode* node);

    private delegate byte* LayOutCharacterDelegate(AtkFontAnalyzerBase* analyzer, byte** text, AtkFontAnalyzerState* state);

    private delegate int BuildFontDelegate(AtkFontManager* manager, ushort index);

    private delegate int UnloadFontsDelegate(AtkFontManager* manager);

    private delegate AtkFontManager.FontSet* PrepareMeasureDelegate(AtkTextNode* node, byte considerScale);

    /// <summary>The first to give a node's faces wins.</summary>
    public void AddNodeFaces(Func<Pointer<AtkTextNode>, NodeFaces?> faces) => this._nodeFaces.Add(Misc.DebugAssertMainThread(faces));

    public void RemoveNodeFaces(Func<Pointer<AtkTextNode>, NodeFaces?> faces) => this._nodeFaces.Remove(Misc.DebugAssertMainThread(faces));

    public void ApplyNodeFace(AtkFontManager.FontSet* set, AtkTextNode* node, NodeFaces faces)
    {
        var index = IndexOf(set);
        if (faces.Face == TextFace.Default || index < 0)
        {
            set->DrawFlags &= ~FaceFlag;
            return;
        }

        // A name the game shows shortened is in no line, so no line is the title's.
        var text = node is null ? null : (byte*)node->NodeText.StringPtr;
        var length = text is null ? 0 : (int)node->NodeText.Length;
        if (faces.MainLine is not { } main || new ReadOnlySpan<byte>(text, length).IndexOf(main) < 0)
            faces = faces with { MainLine = null };
        this._setFaces[index] = (faces, (nint)text, length);
        set->DrawFlags |= FaceFlag;
    }

    private static int IndexOf(AtkFontManager.FontSet* set)
    {
        var manager = AtkStage.Instance()->AtkFontManager;
        var index = manager is null || manager->FontSets is null ? -1 : set - manager->FontSets;
        return index >= 0 && index < AtkFontManager.FontSetCount ? (int)index : -1;
    }

    private TextFace FaceOf(AtkFontManager.FontSet* set) =>
        (set->DrawFlags & FaceFlag) != 0 && IndexOf(set) is var index and >= 0 ? this._setFaces[index].Faces.Face : TextFace.Default;

    private NodeFaces GetNodeFaces(AtkTextNode* node)
    {
        foreach (var resolve in this._nodeFaces)
        {
            if (resolve(node) is { } faces)
                return faces;
        }

        return default;
    }

    // Measuring lays a node's text out where it is. Drawing multiline text lays it out a line at a time, copied to the line buffer.
    private TextFace LineFaceOf(AtkFontManager.FontSet* set)
    {
        var p = _current.Character;
        if (p is null || (set->DrawFlags & FaceFlag) == 0 || IndexOf(set) is not (var index and >= 0))
            return TextFace.Default;
        var (faces, text, length) = this._setFaces[index];
        if (faces.MainLine is not { } main)
            return TextFace.Default;

        var buffer = *(byte**)((byte*)AtkStage.Instance()->AtkFontManager + LineBufferOffset);
        ReadOnlySpan<byte> line;
        if ((nint)p >= text && (nint)p < text + length)
            line = LineAround(new((byte*)text, length), (int)((nint)p - text));
        else if (buffer is not null && p >= buffer && p < buffer + LineBufferSize)
            line = LineAround(new(buffer, LineBufferSize), (int)(p - buffer));
        else
            return TextFace.Default;
        return line.IndexOf(main) < 0 ? faces.OtherLines : TextFace.Default;

        static ReadOnlySpan<byte> LineAround(ReadOnlySpan<byte> text, int at)
        {
            var start = text[..at].LastIndexOfAny((byte)'\n', (byte)'\r') + 1;
            var end = text[at..].IndexOfAny((byte)'\n', (byte)'\r', (byte)0);
            return text[start..(end < 0 ? text.Length : at + end)];
        }
    }

    private AtkFontManager.FontSet* PrepareMeasureDetour(AtkTextNode* node, byte considerScale)
    {
        var set = this._prepareMeasureHook!.Original(node, considerScale);
        if (set is null || node is null)
            return set;

        this.ApplyNodeFace(set, node, this.GetNodeFaces(node));

        // The game measures a node's bold upright, as it draws it.
        set->DrawFlags = (node->TextFlags & TextFlags.Bold) != 0 ? set->DrawFlags | BoldFlag : set->DrawFlags & ~BoldFlag;
        return set;
    }

    private Hook<T> CreateHook<T>(nint address, T detour)
        where T : Delegate =>
        this._finalizer.Add(Plugin.GameInterop.HookFromAddress(address, detour));

    /// <summary>Not through <see cref="Enabled"/>, which rebuilds the font caches.</summary>
    private void DisableAfterError()
    {
        this._enabled = false;
        ForgetPickedFonts();
    }

    private static void ForgetPickedFonts()
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

    // A font cache keeps the glyph pointers laid out at the node's unscaled size, which fit only that size's copy and must not
    // outlive the plugin, so while replacing, nodes get no cache and are laid out at each draw.
    private void BuildFontCacheDetour(AtkTextNode* node)
    {
        if (this._enabled)
            node->FreeFontCache();
        else
            this._buildFontCacheHook.Original(node);
    }

    private void UpdateFontCaches()
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
                if (this._enabled)
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

    // The game rebuilds a font in place (a reload, a switch between the lobby's fonts and the game's), freeing the glyph map and
    // glyphs its copies borrow.
    private int BuildFontDetour(AtkFontManager* manager, ushort index)
    {
        var result = this._buildFontHook.Original(manager, index);
        try
        {
            if (index < manager->FontCount)
                this.OnFontBuilt(manager->Fonts + index);
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Refreshing the copies of a rebuilt font failed; disabling");
            this.DisableAfterError();
        }

        return result;
    }

    private void OnFontBuilt(AtkFontManager.Font* font)
    {
        var any = this.ForgetGameGlyphsOf(font);
        any |= this.ResyncCopies(font);
        if (any)
            this.TextInvalidated?.Invoke();
    }

    /// <summary>Their pixels came from the font's old textures, and converted ones are keyed by its old glyph pointers.</summary>
    private bool ForgetGameGlyphsOf(Pointer<AtkFontManager.Font> font)
    {
        var any = false;
        foreach (var sized in this._sizedFonts.Values)
        {
            if (!sized.Fonts.Any(f => f.Game == font) && sized.ConvertedGlyphs.Keys.All(k => k.Font != font))
                continue;
            sized.GameGlyphs.Clear();
            sized.ConvertedGlyphs.Clear();
            sized.Picks.Clear();
            any = true;
        }

        return any;
    }

    // The game unloads every font (a switch between the lobby's fonts and the game's, a UI shader reload, shutdown), releasing
    // their textures' resource handles.
    private int UnloadFontsDetour(AtkFontManager* manager)
    {
        try
        {
            var textures = new HashSet<Pointer<KernelTexture>>();
            for (var i = 0; manager->Fonts is not null && i < manager->FontCount; i++)
            {
                var font = manager->Fonts + i;
                if (!font->Flags.HasFlag(AtkFontManager.FontFlags.Built))
                    continue;
                for (var t = 0; t < font->TextureCount && t < AtkFontManager.Font.MaxTextures; t++)
                {
                    if (font->Textures[t].Value is not null)
                        textures.Add(font->Textures[t]);
                }

                this.ForgetGameGlyphsOf(font);
            }

            this.DropGameReads(textures);
            ForgetPickedFonts();
            this.TextInvalidated?.Invoke();
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Letting go of the fonts being unloaded failed; disabling");
            this._gameReads.Clear();
            this.DisableAfterError();
        }

        return this._unloadFontsHook.Original(manager);
    }

    private AtkFontManager.Font* PickFontDetour(AtkFontManager.FontSet* set, byte useCache)
    {
        var font = this._pickFontHook.Original(set, useCache);
        if (font is null)
            return font;

        // The picker's cache is keyed by its own size, not the one copies are chosen by, so a copy picked before is decided again.
        if (this._copies.TryGetValue(font, out var picked))
            font = picked.Original;

        // A font not built yet (its glyph map may be gone) is left alone.
        if (!this._enabled || !font->Flags.HasFlag(AtkFontManager.FontFlags.Ready))
            return font;

        try
        {
            this.ApplyPendingPages();

            // Picked for the on-screen size, even where the game picked for another (a node's fixed font resolution, or a pick
            // scale of its own), so that the renderer doesn't scale the glyphs.
            var size = set->ScaledSizeY;
            var halfPx = Math.Clamp((int)Misc.StdRound(size * 2), MinHalfPx, MaxHalfPx);
            var copy = this.GetOrCreateCopy(font, halfPx, this.ResolveFace(this.FaceOf(set)));

            // Any renderer scale but exactly 1 resamples bilinearly (soft text), while a quarter pixel of size error doesn't show.
            if (halfPx < MaxHalfPx && halfPx > MinHalfPx)
                copy->Size = size;
            set->CurrentFont = copy;
            return copy;
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Replacing a font failed; disabling");
            this.DisableAfterError();
            return font;
        }
    }

    private byte* LayOutCharacterDetour(AtkFontAnalyzerBase* analyzer, byte** text, AtkFontAnalyzerState* state)
    {
        var outer = _current;
        var vtbl = *(nint*)analyzer;
        _current = new()
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
            if (_current.ItalicCleared)
                state->Flags |= AtkFontManager.DrawFlags.Italic;
            _current = outer;
        }
    }

    // An italic macro sets the state's bit, which measuring sees too; a text node's italics are only in the font set's own flags,
    // which only drawing sees.
    private ItalicMode GetItalicMode(AtkFontManager.FontSet* set)
    {
        if (_current.State is null || (_current.State->Flags & AtkFontManager.DrawFlags.Italic) == 0)
            return ItalicMode.Upright;
        return set is not null && (set->DrawFlags & AtkFontManager.DrawFlags.Italic) != 0
                   ? ItalicMode.Images
                   : ItalicMode.Real;
    }

    private AtkFontManager.Glyph* GetGlyphDetour(AtkFontManager.FontSet* set, uint utf8Value, AtkFontManager.Font* font)
    {
        if (!this._copies.TryGetValue(font, out var info))
            return this._getGlyphHook.Original(set, utf8Value, font);
        var glyph = this.GetCopyGlyph(set, utf8Value, info);
        return glyph is not null ? glyph : this.GetMissingGlyph(set, utf8Value, info);
    }

    /// <summary>Null if neither the copy nor its game font has a glyph of the character.</summary>
    private AtkFontManager.Glyph* GetCopyGlyph(AtkFontManager.FontSet* set, uint utf8Value, CopyInfo info)
    {
        var sized = this.GetDrawnSized(set, info);

        // The character LayOutCharacter is at gets its shaped cluster's glyph, unless the lookup is of the game's fallback characters.
        var p = _current.Character;
        if (p is not null && GameUtf8.Pack(p) == utf8Value)
        {
            try
            {
                var italic = this.GetItalicMode(set);
                var shaped = this._layout.TryGetGlyph(sized, p, italic).Value;
                if (shaped is not null)
                {
                    // The quad emitter runs after this lookup, so clearing the italic bit until LayOutCharacterDetour ends draws
                    // a real italic glyph unsheared.
                    if (italic != ItalicMode.Upright && ((GlyphSlot*)shaped)->Id >= 0 && this.CellOf(shaped).RealItalic)
                    {
                        _current.State->Flags &= ~AtkFontManager.DrawFlags.Italic;
                        _current.ItalicCleared = true;
                    }

                    return this.WithPixels(shaped);
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.Error(ex, "Shaping failed; the character is laid out by itself");
            }
        }

        if (!sized.Glyphs.TryGetValue(utf8Value, out var glyph))
        {
            try
            {
                glyph = this.CreateGlyph(sized, utf8Value);
            }
            catch (Exception ex)
            {
                Plugin.Log.Error(ex, "Rasterizing 0x{value:X} at {px} px failed", utf8Value, sized.Px);
                glyph = default;
            }

            sized.Glyphs.Add(utf8Value, glyph);
        }

        if (glyph != default)
            return this.WithPixels(glyph);

        try
        {
            var own = this.GetGameGlyph(sized, utf8Value);
            if (own is not null)
                return this.WithPixels(own);
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Copying the game's glyph 0x{value:X} failed", utf8Value);
        }

        // The game font's own glyph has a texture index of the game font's textures, so it is copied into the atlas too.
        var original = (AtkFontManager.Font*)info.Original;
        var game = this._getGlyphHook.Original(set, utf8Value, original);
        if (game is null)
            return null;
        try
        {
            return this.WithPixels(this.ConvertGameGlyph(sized, original, game, utf8Value));
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Copying the game's glyph 0x{value:X} failed", utf8Value);
            return &this._emptyGlyph->Glyph;
        }
    }

    // A glyph takes its textures from the font the game picked, so a line's face is used only where it shares that font's atlas.
    private SizedFont GetDrawnSized(AtkFontManager.FontSet* set, CopyInfo info)
    {
        try
        {
            if (this.LineFaceOf(set) is var face && face != TextFace.Default && face != info.Face && this.HasFonts(face) &&
                this._copies.TryGetValue(this.GetOrCreateCopy(info.Original, info.HalfPx, face), out var other) &&
                other.Sized.Atlas == info.Sized.Atlas)
                info = other;
            return _current.State is not null && (_current.State->Flags & BoldFlag) != 0 ? this.GetBoldSized(info) : info.Sized;
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Setting up the fonts of a line's face or of bold failed");
            return info.BoldSized = info.Sized;
        }
    }

    // LayOutCharacter would take its substitutes from the font's glyph map directly, getting game glyphs of texture indices the
    // copy has no textures of, so the copy's own glyphs of them (else one of no width) are given instead.
    private AtkFontManager.Glyph* GetMissingGlyph(AtkFontManager.FontSet* set, uint utf8Value, CopyInfo info)
    {
        foreach (var substitute in MissingGlyphSubstitutes)
        {
            if (utf8Value == substitute)
                continue;
            var glyph = this.GetCopyGlyph(set, substitute, info);
            if (glyph is not null)
                return glyph;
        }

        return &this._emptyGlyph->Glyph;
    }

    // For GetGlyph, which isn't told which character of the text LayOutCharacter is laying out.
    private struct LayoutContext
    {
        public byte* Character;

        // Laid out by an analyzer that only measures, so its glyph needs no pixels yet.
        public bool MeasuringOnly;

        public AtkFontAnalyzerState* State;

        // The italic bit was cleared for a real italic glyph, to be set again once the character is laid out.
        public bool ItalicCleared;
    }
}
