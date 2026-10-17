using System;
using System.Text;

using FontChanger.DalamudPlugin.Rendering.Rasterization;

namespace FontChanger.DalamudPlugin.Rendering.Faces.Sources.Nominal;

// The fonts merged glyphs' texts are drawn with, which the element's outline or image source makes.
internal sealed partial class MergingFaceSource
{
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

    /// <summary>
    /// A laid out line: its advance; its ink, measured by its glyphs' metrics (horizontally from the outlines of shaped
    /// glyphs, which partly covered pixels may go past; vertically in pixels from the line's top; all 0 if it has none);
    /// and how to draw it with the pen at an x on the baseline.
    /// </summary>
    internal sealed record MergeTextLine(int Width, bool Shaped, float InkX1, float InkX2, int InkY1, int InkY2, Func<float, RasterGlyph> Draw);
}
