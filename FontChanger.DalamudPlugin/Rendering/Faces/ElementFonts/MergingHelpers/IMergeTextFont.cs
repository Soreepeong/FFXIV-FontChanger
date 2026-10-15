using System.Text;

namespace FontChanger.DalamudPlugin.Rendering.Faces.ElementFonts.MergingHelpers;

/// <summary>
/// A font the texts of glyph merging are drawn with (xivres glyph_merging's text font): the element's, at a size and
/// transformation, its gamma applied. Its glyphs are placed relative to the pen on the baseline.
/// </summary>
internal interface IMergeTextFont
{
    /// <summary>Gets the ascent in pixels.</summary>
    int Ascent { get; }

    bool Has(Rune rune);

    /// <summary>
    /// Lays out a line, its glyphs <paramref name="letterSpacing"/> pixels further apart. A shaped line is drawn at any
    /// fraction of a pixel; others are drawn at whole pixels.
    /// </summary>
    MergeTextLine LayOut(string text, int letterSpacing);
}
