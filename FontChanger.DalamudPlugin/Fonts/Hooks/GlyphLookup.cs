using System;

using FFXIVClientStructs.FFXIV.Component.GUI;

using FontChanger.DalamudPlugin.Fonts.Enums;
using FontChanger.DalamudPlugin.Fonts.Glyphs;
using FontChanger.DalamudPlugin.Fonts.Text;
using FontChanger.DalamudPlugin.PluginInterface;
using FontChanger.DalamudPlugin.Utilities;

namespace FontChanger.DalamudPlugin.Fonts.Hooks;

/// <summary>Which glyph GetGlyph returns for a copy.</summary>
internal sealed unsafe class GlyphLookup(
    CopyFonts copies,
    SizedFontRegistry sized,
    GameTextLayout layout,
    GlyphFactory factory,
    CellPlacer placer,
    GlyphStore store,
    FrameState state,
    FontSetFlags flags,
    GlyphLookup.GameGlyphDelegate gameGlyph)
{
    // The UI scales (High Resolution UI) text wrapped unscaled may be drawn at, the largest last.
    public static readonly float[] WrapScales = [1f, 1.5f, 2f, 3f];

    // What LayOutCharacter lays out a character a font has no glyph of as, in order of preference, as packed UTF-8.
    private static readonly uint[] MissingGlyphSubstitutes = [0xE38093, 0x2D, 0xE39080];

    /// <summary>The game's own lookup in a font.</summary>
    public delegate AtkFontManager.Glyph* GameGlyphDelegate(AtkFontManager.FontSet* set, uint utf8Value, AtkFontManager.Font* font);

    public AtkFontManager.Glyph* Get(AtkFontManager.FontSet* set, uint utf8Value, CopyInfo info)
    {
        var drawn = this.GetDrawnSized(set, info);
        var wrapping = LayoutCall.Wrapping && set->NodeScaleX == 1 && set->NodeScaleY == 1 && FontSetFlags.PickScaleOf(set) <= 0;
        var glyph = wrapping ? this.GetWrapRunGlyph(set, utf8Value, info, drawn) : null;
        if (glyph is not null)
            return glyph;

        glyph = this.GetCopyGlyph(set, utf8Value, info, drawn, true);
        if (glyph is null)
            glyph = this.GetMissingGlyph(set, utf8Value, info, drawn);
        return wrapping ? this.GetWrapGlyph(set, utf8Value, info, drawn, glyph) : glyph;
    }

    // An italic macro sets the state's bit, which measuring sees too. A text node's italics are only in the font set's own flags,
    // which only drawing sees.
    private static ItalicMode GetItalicMode(AtkFontManager.FontSet* set)
    {
        var state = LayoutCall.Current.State;
        if (state is null || (state->Flags & AtkFontManager.DrawFlags.Italic) == 0)
            return ItalicMode.Upright;
        return set is not null && (set->DrawFlags & AtkFontManager.DrawFlags.Italic) != 0
                   ? ItalicMode.Images
                   : ItalicMode.Real;
    }

    private AtkFontManager.Glyph* WithPixels(AtkFontManager.Glyph* glyph) =>
        LayoutCall.Current.MeasuringOnly ? glyph : placer.WithPixels(glyph, state.Frame);

    /// <summary>The sized fonts of the UI scales for the text being wrapped, <paramref name="drawn"/> (the largest) last.</summary>
    /// <seealso cref="WrapScales"/>
    private SizedFont[] GetWrapSizes(AtkFontManager.FontSet* set, CopyInfo info, SizedFont drawn) =>
        info.GetWrapSizes(sized, WrapScales, set->ScaledSizeY, FontSetFlags.IsBold(LayoutCall.Current.State), drawn);

    /// <summary>
    /// While wrapping, the character LayOutCharacter is at gets its cell of its run laid out at every UI scale.
    /// </summary>
    /// <returns>Null if the character is looked up by itself.</returns>
    /// <seealso cref="GameTextLayout.TryGetWrapGlyph"/>
    private AtkFontManager.Glyph* GetWrapRunGlyph(AtkFontManager.FontSet* set, uint utf8Value, CopyInfo info, SizedFont drawn)
    {
        var p = LayoutCall.Current.Character;
        if (p is null || GameUtf8.Pack(p) != utf8Value)
            return null;
        try
        {
            return layout.TryGetWrapGlyph(this.GetWrapSizes(set, info, drawn), WrapScales, p, GetItalicMode(set)).Value;
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Laying out a run at the UI scales failed; it is measured a character at a time");
            return null;
        }
    }

    /// <summary>
    /// While wrapping, for a character looked up by itself, in the copy of the largest UI scale, a glyph of no pixels as wide as
    /// <paramref name="glyph"/> is at the widest of the UI scales.
    /// </summary>
    private AtkFontManager.Glyph* GetWrapGlyph(AtkFontManager.FontSet* set, uint utf8Value, CopyInfo info, SizedFont drawn, AtkFontManager.Glyph* glyph)
    {
        if (glyph is null || glyph == store.EmptyGlyph)
            return glyph;

        // In the copy's pixels, which are those of the largest scale. Width + OffsetX is how far the game advances.
        var largest = WrapScales[^1];
        var own = glyph->Width + glyph->OffsetX;
        var widest = own;
        try
        {
            var sizes = this.GetWrapSizes(set, info, drawn);
            for (var i = 0; i < sizes.Length - 1; i++)
            {
                var other = this.GetCopyGlyph(set, utf8Value, info, sizes[i], false);
                if (other is not null && other != store.EmptyGlyph)
                    widest = Math.Max(widest, (int)MathF.Ceiling((other->Width + other->OffsetX) * largest / WrapScales[i]));
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Measuring 0x{utf8:X} at the other UI scales failed", utf8Value);
            return glyph;
        }

        // A spacer advances at most sbyte.MaxValue.
        return widest == own || widest > sbyte.MaxValue ? glyph : layout.GetSpacer(drawn, widest).Value;
    }

    /// <summary>Null if neither the copy nor its game font has a glyph of the character.</summary>
    /// <param name="set">The font set.</param>
    /// <param name="utf8Value">The character.</param>
    /// <param name="info">The copy.</param>
    /// <param name="font">The sized font to look in.</param>
    /// <param name="shape">Whether the character LayOutCharacter is at gets its shaped cluster's glyph.</param>
    /// <returns>The glyph.</returns>
    private AtkFontManager.Glyph* GetCopyGlyph(AtkFontManager.FontSet* set, uint utf8Value, CopyInfo info, SizedFont font, bool shape)
    {
        // Not for a lookup of the game's fallback characters.
        var p = LayoutCall.Current.Character;
        if (shape && p is not null && GameUtf8.Pack(p) == utf8Value)
        {
            try
            {
                var italic = GetItalicMode(set);
                var shaped = layout.TryGetGlyph(font, p, italic).Value;
                if (shaped is not null)
                {
                    // The quad emitter runs after this lookup, so clearing the italic bit until LayOutCharacterDetour ends draws
                    // a real italic glyph unsheared.
                    if (italic != ItalicMode.Upright && GlyphStore.IsOwn(shaped) && store.CellOf(shaped).RealItalic)
                    {
                        LayoutCall.Current.State->Flags &= ~AtkFontManager.DrawFlags.Italic;
                        LayoutCall.Current.ItalicCleared = true;
                    }

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
    private AtkFontManager.Glyph* MakeGlyph(AtkFontManager.FontSet* set, uint utf8Value, CopyInfo info, SizedFont font)
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
    private SizedFont GetDrawnSized(AtkFontManager.FontSet* set, CopyInfo info)
    {
        try
        {
            if (flags.LineFaceOf(set, LayoutCall.Current.Character) is var face && face != TextFace.Default && face != info.Face &&
                sized.Fonts.HasFonts(face) &&
                copies.TryGet(copies.GetOrCreate(info.Original, info.HalfPx, face), out var other) &&
                other.Sized.Atlas == info.Sized.Atlas)
                info = other;
            return FontSetFlags.IsBold(LayoutCall.Current.State) ? info.GetBold(sized) : info.Sized;
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
    private AtkFontManager.Glyph* GetMissingGlyph(AtkFontManager.FontSet* set, uint utf8Value, CopyInfo info, SizedFont drawn)
    {
        foreach (var substitute in MissingGlyphSubstitutes)
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
