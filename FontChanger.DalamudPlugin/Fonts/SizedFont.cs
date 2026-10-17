using System;
using System.Collections.Generic;
using System.Text;

using FFXIVClientStructs.FFXIV.Component.GUI;
using FFXIVClientStructs.Interop;

using FontChanger.DalamudPlugin.Fonts.Faces;

namespace FontChanger.DalamudPlugin.Fonts;

/// <summary>Shared by the copies of a family's fonts of one size, such as a font and its lobby version.</summary>
internal sealed unsafe class SizedFont(
    MaterializedFont font,
    float px,
    int ascent,
    int lineHeight,
    Pointer<AtkFontManager.Font> gameFont,
    GlyphAtlas atlas,
    IReadOnlyList<(MaterializedFont Font, Pointer<AtkFontManager.Font> Game)> fonts)
{
    public MaterializedFont Font { get; } = font;

    public GlyphAtlas Atlas { get; } = atlas;

    public Pointer<AtkFontManager.Font> GameFont { get; } = gameFont;

    public float Px { get; } = px;

    public int Ascent { get; } = ascent;

    public int LineHeight { get; } = lineHeight;

    /// <summary>Largest first; only <see cref="Font"/> at the size of one of the family's fonts.</summary>
    public IReadOnlyList<(MaterializedFont Font, Pointer<AtkFontManager.Font> Game)> Fonts { get; } = fonts;

    /// <summary>By packed UTF-8 value; 0 for a glyph left to the game.</summary>
    public Dictionary<uint, Pointer<AtkFontManager.Glyph>> Glyphs { get; } = [];

    /// <summary>Copied from the game fonts of <see cref="Fonts"/>, by packed UTF-8 value; 0 if none could be.</summary>
    public Dictionary<uint, Pointer<AtkFontManager.Glyph>> GameGlyphs { get; } = [];

    public Dictionary<(Pointer<AtkFontManager.Font> Font, Pointer<AtkFontManager.Glyph> Glyph), Pointer<AtkFontManager.Glyph>> ConvertedGlyphs { get; } =
        [];

    public Dictionary<Rune, GlyphSource> Picks { get; } = [];

    /// <summary>
    /// Of several fonts, the largest whose element other than the game's has the grapheme, else the game's, else one without
    /// any for it.
    /// </summary>
    public GlyphSource Pick(ReadOnlySpan<Rune> grapheme)
    {
        if (grapheme.Length == 1 && this.Picks.TryGetValue(grapheme[0], out var known))
            return known;
        this.Font.TryGetElement(grapheme, this.GameFont, out var gameElement);
        var pick = new GlyphSource(this.Font, this.GameFont, gameElement);
        if (this.Fonts.Count > 1)
        {
            GlyphSource? none = null, game = null, own = null;
            foreach (var (font, original) in this.Fonts)
            {
                font.TryGetElement(grapheme, original, out var element);
                if (element is { DrawsGame: false })
                {
                    own = new(font, original, element);
                    break;
                }

                if (element is null)
                    none ??= new(font, original, null);
                else
                    game ??= new(font, original, element);
            }

            pick = own ?? game ?? none ?? pick;
        }

        if (grapheme.Length == 1)
            this.Picks.Add(grapheme[0], pick);
        return pick;
    }
}
