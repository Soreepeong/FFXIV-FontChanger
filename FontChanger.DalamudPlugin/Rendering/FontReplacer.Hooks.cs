using System;
using System.Collections.Generic;
using System.Linq;

using FFXIVClientStructs.FFXIV.Component.GUI;

using FontChanger.DalamudPlugin.PluginInterface;
using FontChanger.DalamudPlugin.Rendering.Text;
using FontChanger.DalamudPlugin.Utilities;

using KernelTexture = FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Texture;

namespace FontChanger.DalamudPlugin.Rendering;

// Inside the namespace, so that Pointer<T> is XIVCS's and FontNames is ours, over TerraFX's.
using FFXIVClientStructs.Interop;

// The game's functions hooked: font picking, glyph lookups, the layout of characters, and the fonts' builds and unloads.
internal sealed unsafe partial class FontReplacer
{
    /// <summary>Stops replacing after an error in a hook (not through <see cref="Enabled"/>, which rebuilds the font caches).</summary>
    private void DisableAfterError()
    {
        this._enabled = false;
        ForgetPickedFonts();
    }

    /// <summary>Makes every font set pick its font again on its next use.</summary>
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

    /// <summary>
    /// A font cache keeps the glyph pointers of the text as laid out when it was built, at the node's unscaled size
    /// (the cache build), and draws use them while picking the font for the on-screen size. Glyphs of a copy
    /// only fit the copy of their size, and a copy's glyphs must not outlive the plugin, so while the replacement is on
    /// nodes get no cache: their text is laid out at each draw, as for nodes that never asked for one.
    /// </summary>
    private void BuildFontCacheDetour(AtkTextNode* node)
    {
        if (this._enabled)
            node->FreeFontCache();
        else
            this._buildFontCacheHook.Original(node);
    }

    /// <summary>
    /// Brings every loaded text node's font cache in line with <see cref="Enabled"/>: freed while on, rebuilt (for the
    /// nodes that ask for one) while off.
    /// </summary>
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

    /// <summary>
    /// The game built a font again in place (a reload, a switch between the lobby's fonts and the game's), freeing the
    /// glyph map and glyphs its copies borrow: they take them again, with the face of the font's name now.
    /// </summary>
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

    /// <summary>
    /// Forgets the glyphs made from a game font's glyphs, whose pixels came from its old textures (and the converted ones
    /// are keyed by its old glyph pointers). True if any sized font had some.
    /// </summary>
    private bool ForgetGameGlyphsOf(Pointer<AtkFontManager.Font> font)
    {
        var any = false;
        foreach (var sized in this._sizedFonts.Values)
        {
            if (!sized.Sources.Contains(font) && sized.ConvertedGlyphs.Keys.All(k => k.Font != font))
                continue;
            sized.GameGlyphs.Clear();
            sized.ConvertedGlyphs.Clear();
            sized.Picks.Clear();
            any = true;
        }

        return any;
    }

    /// <summary>
    /// The game unloads every font (a switch between the lobby's fonts and the game's, a UI shader reload, shutdown),
    /// releasing their textures' resource handles: the game glyph pixels still to be read from those textures are dropped,
    /// and no font set may keep a copy picked until its font is built again.
    /// </summary>
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

    /// <summary>Drops the queued reads of game glyph pixels from any of some textures; the glyphs stay blank.</summary>
    private void DropGameReads(HashSet<Pointer<KernelTexture>> textures)
    {
        this._gameReads.RemoveAll(g => {
            ref var cell = ref this.CellOf(g);
            if (cell.Source is not { } s || !textures.Contains(s.Texture))
                return false;
            ReleasePixels(ref cell);
            return true;
        });
    }

    private AtkFontManager.Font* PickFontDetour(AtkFontManager.FontSet* set, byte useCache)
    {
        var font = this._pickFontHook.Original(set, useCache);
        if (font is null)
            return font;

        // The picker's cached choice may be a copy picked before: decide again from its game font, since the picker's
        // cache is keyed by its own size, which isn't the one copies are chosen by.
        if (this._copies.TryGetValue(font, out var picked))
            font = picked.Original;

        if (!this._enabled)
            return font;

        // A font not built yet (its glyph map may be gone) is left alone.
        if (!font->Flags.HasFlag(AtkFontManager.FontFlags.Ready))
            return font;

        try
        {
            this.ApplyPendingPages();

            // Picked for the on-screen size, even where the game picked for another (the unscaled size of a node with a
            // fixed font resolution, or a pick scale of its own), so the renderer doesn't scale the glyphs.
            var size = set->ScaledSizeY;
            var halfPx = Math.Clamp((int)Misc.StdRound(size * 2), MinHalfPx, MaxHalfPx);
            var copy = this.GetOrCreateCopy(font, halfPx);

            // Rasterized at half-pixel sizes, but the copy claims the exact size: any renderer scale but exactly 1 resamples
            // bilinearly (soft text), while a quarter pixel of size error doesn't show.
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

    /// <summary>
    /// Gets how the character being laid out is in italics: by an italic macro (which measuring sees too), or by its text
    /// node (which only drawing sees: the bit is in the font set's own flags, before any macro changed the copy).
    /// </summary>
    private GameTextLayout.ItalicMode GetItalicMode(AtkFontManager.FontSet* set)
    {
        if (_current.State is null || (_current.State->Flags & AtkFontManager.DrawFlags.Italic) == 0)
            return GameTextLayout.ItalicMode.Upright;
        return set is not null && (set->DrawFlags & AtkFontManager.DrawFlags.Italic) != 0
                   ? GameTextLayout.ItalicMode.Images
                   : GameTextLayout.ItalicMode.Real;
    }

    private AtkFontManager.Glyph* GetGlyphDetour(AtkFontManager.FontSet* set, uint utf8Value, AtkFontManager.Font* font)
    {
        if (!this._copies.TryGetValue(font, out var info))
            return this._getGlyphHook.Original(set, utf8Value, font);
        var glyph = this.GetCopyGlyph(set, utf8Value, info);
        return glyph is not null ? glyph : this.GetMissingGlyph(set, utf8Value, info);
    }

    /// <summary>Gets a copy's glyph of a character, all of whose pixels are in its atlas; null if neither it nor its game font has one.</summary>
    private AtkFontManager.Glyph* GetCopyGlyph(AtkFontManager.FontSet* set, uint utf8Value, CopyInfo info)
    {
        var sized = info.Sized;

        // Laid out by LayOutCharacter at a known place in the text: the glyph of its shaped cluster. The character there must
        // be the one asked for (the lookups of the game's fallback characters, if any, are not).
        var p = _current.Character;
        if (p is not null && GameUtf8.Pack(p) == utf8Value)
        {
            try
            {
                var italic = this.GetItalicMode(set);
                var shaped = this._layout.TryGetGlyph(sized, p, italic).Value;
                if (shaped is not null)
                {
                    // A real italic glyph is drawn as it is: the italic bit is cleared while the character is laid out
                    // (the quad emitter runs after this lookup), and set again after (LayOutCharacterDetour).
                    if (italic != GameTextLayout.ItalicMode.Upright && ((GlyphSlot*)shaped)->Id >= 0 && this.CellOf(shaped).RealItalic)
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

        // The game's glyph, looked up in the game font, whose glyph map the copy borrows: its texture index is of the game
        // font's textures, so it is copied into the atlas too.
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

    /// <summary>
    /// Gets what a character a copy has no glyph for is laid out as, never null. LayOutCharacter would take the geta mark
    /// (U+3013), else '-', else U+3400, from the font's glyph map itself rather than through GetGlyph, which would give the
    /// game font's glyph, of a texture index the copy has no texture of. The copy's own glyphs of them are given instead,
    /// else a glyph of no width.
    /// </summary>
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

    /// <summary>
    /// The character the game is laying out on a thread, for GetGlyph to look up in its shaped run: whether it is laid out
    /// by an analyzer that only measures (its glyph needs no pixels yet), its layout state (whose flags have italics), and
    /// whether its italic bit was cleared for a real italic glyph, to be set again once the character is laid out.
    /// </summary>
    private struct LayoutContext
    {
        public byte* Character;
        public bool MeasuringOnly;
        public AtkFontAnalyzerState* State;
        public bool ItalicCleared;
    }
}
