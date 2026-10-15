using System;
using System.Collections.Generic;
using System.Text;

using FFXIVClientStructs.FFXIV.Component.GUI;

using FontChanger.DalamudPlugin.Rendering.Rasterization;

namespace FontChanger.DalamudPlugin.Rendering.Faces.ElementFonts;

/// <summary>
/// The glyphs a face element draws (xivres fixed_size_font), at any size: what it has, its line metrics and baselines,
/// and its glyphs, rasterized as the element draws them (its gamma applied) but not yet adjusted by the face
/// (<see cref="ReplacementFace.Wrap"/>). Made by <see cref="ReplacementFace"/> from an element's renderer: the game's
/// glyphs, none, a font (DirectWrite or FreeType), glyph images, or glyph merging around one of those.
/// </summary>
/// <remarks>Sizes are the element's (the face's drawn size scaled by the element's size); glyphs are placed relative to
/// the pen on the baseline.</remarks>
internal unsafe interface IElementFont : IDisposable
{
    /// <summary>Gets whether a codepoint has a glyph; the game's glyphs are those of <paramref name="game"/>.</summary>
    bool Has(Rune rune, AtkFontManager.Font* game);

    /// <summary>Gets the line metrics at a size, in whole pixels.</summary>
    (int Ascent, int LineHeight) GetLineMetrics(float px, AtkFontManager.Font* game);

    /// <summary>Gets a baseline of the BASE table (by tag, as DWRITE_FONT_FEATURE_TAG) in pixels above the glyphs' origin; false if none.</summary>
    bool TryGetBaseline(uint tag, float px, out float baseline);

    /// <summary>Rasterizes a codepoint's glyph at a size; null if there is none, or the game draws it.</summary>
    RasterGlyph? Rasterize(Rune rune, float px);

    /// <summary>
    /// Gets a glyph (<paramref name="glyph"/>, rasterized by <see cref="Rasterize"/>) drawn narrower by a horizontal scale,
    /// for monospacing: resampled, unless the font can draw it so.
    /// </summary>
    RasterGlyph Squeeze(Rune rune, float px, RasterGlyph glyph, float scale) => glyph.SqueezedX(scale);

    /// <summary>Gets a codepoint's advance on screen at a size, unrounded (monospacing's reference glyph); 0 if none.</summary>
    float GetAdvance(Rune rune, float px);

    /// <summary>Gets the codepoints it has glyphs of; null if they can't be listed (the game's, which depend on the font).</summary>
    IEnumerable<Rune>? Codepoints => null;
}