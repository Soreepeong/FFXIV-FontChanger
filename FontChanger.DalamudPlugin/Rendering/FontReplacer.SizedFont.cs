using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using FFXIVClientStructs.FFXIV.Component.GUI;

using FontChanger.DalamudPlugin.Rendering.Faces;

namespace FontChanger.DalamudPlugin.Rendering;

// Inside the namespace, so that Pointer<T> is XIVCS's and FontNames is ours, over TerraFX's.
using FFXIVClientStructs.Interop;

// A family's glyphs at a size, and where each grapheme's come from.
internal sealed unsafe partial class FontReplacer
{
    /// <summary>
    /// Where a sized font's glyph of a codepoint comes from: <see cref="Face"/>'s <see cref="Element"/> (null for the
    /// system's fonts), with <see cref="Game"/> for the face's game glyphs.
    /// </summary>
    internal sealed record GlyphSource(MaterializedFont Face, Pointer<AtkFontManager.Font> Game, MaterializedFontElement? Element);

    /// <summary>
    /// A family's glyphs at one pixel size, shared by the copies of its fonts of that size (a font and its lobby version).
    /// <see cref="Face"/>, with <see cref="GameFont"/> (its game font), gives the line metrics and how text is laid out.
    /// </summary>
    internal sealed class SizedFont(
        MaterializedFont face,
        float px,
        int ascent,
        int lineHeight,
        Pointer<AtkFontManager.Font> gameFont,
        GlyphAtlas atlas,
        IReadOnlyList<(MaterializedFont Face, Pointer<AtkFontManager.Font> Game)> faces)
    {
        public MaterializedFont Face { get; } = face;

        public GlyphAtlas Atlas { get; } = atlas;

        public Pointer<AtkFontManager.Font> GameFont { get; } = gameFont;

        public float Px { get; } = px;

        public int Ascent { get; } = ascent;

        public int LineHeight { get; } = lineHeight;

        /// <summary>
        /// Gets the faces glyphs may come from with their game fonts, largest first: only <see cref="Face"/> at the size of
        /// one of the family's faces.
        /// </summary>
        public IReadOnlyList<(MaterializedFont Face, Pointer<AtkFontManager.Font> Game)> Faces { get; } = faces;

        /// <summary>Gets the game fonts the game's glyphs are copied from, largest first.</summary>
        public IReadOnlyList<Pointer<AtkFontManager.Font>> Sources { get; } = faces.Select(f => f.Game).ToList();

        /// <summary>By packed UTF-8 value; 0 for a glyph left to the game.</summary>
        public Dictionary<uint, Pointer<AtkFontManager.Glyph>> Glyphs { get; } = [];

        /// <summary>Copied from <see cref="Sources"/>, by packed UTF-8 value; 0 if none could be.</summary>
        public Dictionary<uint, Pointer<AtkFontManager.Glyph>> GameGlyphs { get; } = [];

        /// <summary>Copied from the glyphs the game fonts' own lookups gave copies, by game font and game glyph.</summary>
        public Dictionary<(Pointer<AtkFontManager.Font> Font, Pointer<AtkFontManager.Glyph> Glyph), Pointer<AtkFontManager.Glyph>> ConvertedGlyphs { get; } =
            [];

        /// <summary><see cref="Pick"/>'s of single codepoints.</summary>
        public Dictionary<Rune, GlyphSource> Picks { get; } = [];

        /// <summary>
        /// Gets where a grapheme's glyphs come from (one codepoint for the per-character path): of several faces, the
        /// largest whose element other than the game's has it, else the game's, else one that has none for it.
        /// </summary>
        public GlyphSource Pick(ReadOnlySpan<Rune> grapheme)
        {
            if (grapheme.Length == 1 && this.Picks.TryGetValue(grapheme[0], out var known))
                return known;
            this.Face.TryGetElement(grapheme, this.GameFont, out var gameElement);
            var pick = new GlyphSource(this.Face, this.GameFont, gameElement);
            if (this.Faces.Count > 1)
            {
                GlyphSource? none = null, game = null, own = null;
                foreach (var (face, font) in this.Faces)
                {
                    face.TryGetElement(grapheme, font, out var element);
                    if (element is { DrawsGame: false })
                    {
                        own = new(face, font, element);
                        break;
                    }

                    if (element is null)
                        none ??= new(face, font, null);
                    else
                        game ??= new(face, font, element);
                }

                pick = own ?? game ?? none ?? pick;
            }

            if (grapheme.Length == 1)
                this.Picks.Add(grapheme[0], pick);
            return pick;
        }
    }
}
