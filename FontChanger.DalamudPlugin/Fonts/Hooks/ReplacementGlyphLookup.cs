using System;

using FFXIVClientStructs.FFXIV.Component.GUI;

using FontChanger.DalamudPlugin.Fonts.Enums;
using FontChanger.DalamudPlugin.Fonts.Glyphs;
using FontChanger.DalamudPlugin.Fonts.Text;
using FontChanger.DalamudPlugin.PluginInterface;
using FontChanger.DalamudPlugin.Utilities;

namespace FontChanger.DalamudPlugin.Fonts.Hooks;

/// <summary>Which glyph GetGlyph returns for a copy.</summary>
internal sealed unsafe class ReplacementGlyphLookup(
    ReplacementFontTable copies,
    SizedFontRegistry sized,
    ShapedRunCache layout,
    GlyphFactory factory,
    GlyphAtlasPlacer placer,
    GlyphStore store,
    FrameState state,
    LayoutContext context,
    ReplacementGlyphLookup.GameGlyphDelegate gameGlyph)
{
    private readonly UiScaleWrapMeasurer _wrap = new(sized, layout, store, context);

    /// <summary>The game's own lookup in a font.</summary>
    public delegate AtkFontManager.Glyph* GameGlyphDelegate(AtkFontManager.FontSet* set, uint utf8Value, AtkFontManager.Font* font);

    public AtkFontManager.Glyph* Get(AtkFontManager.FontSet* set, uint utf8Value, ReplacementFont info)
    {
        var drawn = this.GetDrawnSized(set, info);
        var wrapping = context.IsWrapMeasure(set);
        var glyph = wrapping ? this._wrap.GetRunGlyph(set, utf8Value, info, drawn) : null;
        if (glyph is not null)
            return glyph;

        glyph = this.GetCopyGlyph(set, utf8Value, info, drawn, true);
        if (glyph is null)
            glyph = this.GetMissingGlyph(set, utf8Value, info, drawn);
        return wrapping ? this._wrap.Widen(this, set, utf8Value, info, drawn, glyph) : glyph;
    }

    private AtkFontManager.Glyph* WithPixels(AtkFontManager.Glyph* glyph) =>
        context.MeasuringOnly ? glyph : placer.WithPixels(glyph, state.Frame);

    /// <summary>Null if neither the copy nor its game font has a glyph of the character.</summary>
    /// <param name="set">The font set.</param>
    /// <param name="utf8Value">The character.</param>
    /// <param name="info">The copy.</param>
    /// <param name="font">The sized font to look in.</param>
    /// <param name="shape">Whether the character LayOutCharacter is at gets its shaped cluster's glyph.</param>
    /// <returns>The glyph.</returns>
    public AtkFontManager.Glyph* GetCopyGlyph(AtkFontManager.FontSet* set, uint utf8Value, ReplacementFont info, SizedFont font, bool shape)
    {
        // Not for a lookup of the game's fallback characters.
        var p = context.Character;
        if (shape && p is not null && GameUtf8.Pack(p) == utf8Value)
        {
            try
            {
                var italic = context.ItalicModeOf(set);
                var shaped = layout.TryGetGlyph(font, p, italic).Value;
                if (shaped is not null)
                {
                    // The quad emitter runs after this lookup, so clearing the italic bit until LayOutCharacterDetour ends draws
                    // a real italic glyph unsheared.
                    if (italic != ItalicMode.Upright && GlyphStore.IsOwn(shaped) && store.CellOf(shaped).RealItalic)
                        context.ClearItalic();

                    return this.WithPixels(shaped);
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.Error(ex, "Shaping failed; the character is laid out by itself");
            }
        }

        if (!font.Glyphs.TryGetValue(utf8Value, out var glyph))
            glyph = font.Glyphs.Add(utf8Value, this.MakeGlyph(set, utf8Value, info, font));
        return glyph.IsNull ? null : this.WithPixels(glyph);
    }

    /// <summary>The sized font's own glyph, else a copy of its game fonts' or of the game's lookup's.</summary>
    /// <returns>Held once by the caller, or null if there is none.</returns>
    private AtkFontManager.Glyph* MakeGlyph(AtkFontManager.FontSet* set, uint utf8Value, ReplacementFont info, SizedFont font)
    {
        try
        {
            var own = factory.FromCodepoint(font, utf8Value);
            if (own is not null)
                return own;
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Rasterizing 0x{value:X} at {px} px failed", utf8Value, font.Px);
        }

        try
        {
            var game = factory.GameGlyphOf(font, utf8Value);
            if (game is not null)
                return game;
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Copying the game's glyph 0x{value:X} failed", utf8Value);
        }

        // The game font's own glyph has a texture index of the game font's textures, so it is copied into the atlas too.
        var original = (AtkFontManager.Font*)info.Original;
        var found = gameGlyph(set, utf8Value, original);
        if (found is null)
            return null;
        try
        {
            return factory.Convert(font, original, found, utf8Value);
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Copying the game's glyph 0x{value:X} failed", utf8Value);
            return store.EmptyGlyph;
        }
    }

    // A glyph takes its textures from the font the game picked, so a line's face is used only where it shares that font's atlas.
    private SizedFont GetDrawnSized(AtkFontManager.FontSet* set, ReplacementFont info)
    {
        try
        {
            if (context.LineFaceOf(set) is var face && face != TextFace.Default && face != info.Face &&
                sized.Fonts.HasFonts(face) &&
                copies.TryGet(copies.GetOrCreate(info.Original, info.HalfPx, face), out var other) &&
                other.Sized.Atlas == info.Sized.Atlas)
                info = other;
            return context.IsBold ? info.GetBold(sized) : info.Sized;
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Setting up the fonts of a line's face or of bold failed");
            info.BoldFailed();
            return info.Sized;
        }
    }

    // LayOutCharacter would take its substitutes from the font's glyph map directly, getting game glyphs of texture indices the
    // copy has no textures of, so the copy's own glyphs of them (else one of no width) are given instead.
    private AtkFontManager.Glyph* GetMissingGlyph(AtkFontManager.FontSet* set, uint utf8Value, ReplacementFont info, SizedFont drawn)
    {
        foreach (var substitute in GameUtf8.MissingGlyphSubstitutes)
        {
            if (utf8Value == substitute)
                continue;
            var glyph = this.GetCopyGlyph(set, substitute, info, drawn, true);
            if (glyph is not null)
                return glyph;
        }

        return store.EmptyGlyph;
    }
}
