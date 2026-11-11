using System;

using FFXIVClientStructs.FFXIV.Component.GUI;

using FontChanger.DalamudPlugin.Fonts.Glyphs;
using FontChanger.DalamudPlugin.Fonts.Text;
using FontChanger.DalamudPlugin.PluginInterface;
using FontChanger.DalamudPlugin.Utilities;

namespace FontChanger.DalamudPlugin.Fonts.Hooks;

/// <summary>
/// Text the wrapper lays out has no scale to be drawn at, and glyphs of different sizes don't scale evenly, so each character
/// is measured at every UI scale and advances as far as at its widest.
/// </summary>
/// <seealso cref="LayoutContext.IsWrapMeasure"/>
internal sealed unsafe class UiScaleWrapMeasurer(SizedFontRegistry sized, ShapedRunCache layout, GlyphStore store, LayoutContext context)
{
    // The UI scales (High Resolution UI) text wrapped unscaled may be drawn at, the largest last.
    private static readonly float[] Scales = [1f, 1.5f, 2f, 3f];

    /// <summary>Gets the scale copies are picked for while wrapping, in whose pixels the text is measured.</summary>
    public static float LargestScale => Scales[^1];

    /// <summary>
    /// While wrapping, the character LayOutCharacter is at gets its cell of its run laid out at every UI scale.
    /// </summary>
    /// <returns>Null if the character is looked up by itself.</returns>
    /// <seealso cref="ShapedRunCache.TryGetWrapGlyph"/>
    public AtkFontManager.Glyph* GetRunGlyph(AtkFontManager.FontSet* set, uint utf8Value, ReplacementFont info, SizedFont drawn)
    {
        var p = context.Character;
        if (p is null || GameUtf8.Pack(p) != utf8Value)
            return null;
        try
        {
            return layout.TryGetWrapGlyph(this.GetSizes(set, info, drawn), Scales, p, context.ItalicModeOf(set)).Value;
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
    /// <param name="lookup">Looks the character up at the other scales.</param>
    /// <param name="set">The font set.</param>
    /// <param name="utf8Value">The character.</param>
    /// <param name="info">The copy.</param>
    /// <param name="drawn">The sized font of the largest scale.</param>
    /// <param name="glyph">The character's glyph at the largest scale.</param>
    /// <returns>The glyph to measure with.</returns>
    public AtkFontManager.Glyph* Widen(
        ReplacementGlyphLookup lookup, AtkFontManager.FontSet* set, uint utf8Value, ReplacementFont info, SizedFont drawn, AtkFontManager.Glyph* glyph)
    {
        if (glyph is null || glyph == store.EmptyGlyph)
            return glyph;

        // In the copy's pixels, which are those of the largest scale. Width + OffsetX is how far the game advances.
        var largest = Scales[^1];
        var own = glyph->Width + glyph->OffsetX;
        var widest = own;
        try
        {
            var sizes = this.GetSizes(set, info, drawn);
            for (var i = 0; i < sizes.Length - 1; i++)
            {
                var other = lookup.GetCopyGlyph(set, utf8Value, info, sizes[i], false);
                if (other is not null && other != store.EmptyGlyph)
                    widest = Math.Max(widest, (int)MathF.Ceiling((other->Width + other->OffsetX) * largest / Scales[i]));
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

    /// <summary>The sized fonts of the UI scales for the text being wrapped, <paramref name="drawn"/> (the largest) last.</summary>
    private SizedFont[] GetSizes(AtkFontManager.FontSet* set, ReplacementFont info, SizedFont drawn) =>
        info.GetWrapSizes(sized, Scales, set->ScaledSizeY, context.IsBold, drawn);
}
