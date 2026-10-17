using System.Collections.Generic;
using System.Linq;
using System.Text;

using FontChanger.DalamudPlugin.Rendering.Rasterization;
using FontChanger.Presets.Utilities;

namespace FontChanger.DalamudPlugin.Rendering.Faces.Sources.Nominal;

// Glyph merging's texts drawn with the glyph images: one glyph per codepoint at whole pixels, with the files' kerning.
internal sealed partial class ImageFaceSource
{
    /// <summary>Makes the font glyph merging's texts are drawn with at a size, transformed by <paramref name="transform"/>.</summary>
    public MergingFaceSource.IMergeTextFont CreateMergeTextFont(float size, Matrix2x2 transform) => new TextFont(this, size, transform);

    private sealed class TextFont(ImageFaceSource images, float size, Matrix2x2 transform) : MergingFaceSource.IMergeTextFont
    {
        public int Ascent { get; } = images.GetLineMetrics(size, transform).Ascent;

        public bool Has(Rune rune) => images.GetGlyphId(rune) != 0;

        public MergingFaceSource.MergeTextLine LayOut(string text, int letterSpacing)
        {
            var pieces = new List<RasterGlyph>();
            var codepoints = text.EnumerateRunes().ToArray();
            var pen = 0;
            for (var i = 0; i < codepoints.Length; i++)
            {
                if (images.Rasterize(codepoints[i], size, transform) is not { } g)
                    continue;
                pieces.Add(g with { Left = g.Left + pen });
                pen += i + 1 < codepoints.Length ? g.Advance + images.GetKerning(codepoints[i], codepoints[i + 1], size, transform) + letterSpacing : g.Advance;
            }

            var merged = RasterGlyph.Merge(pieces, pen);
            var (inkX1, inkX2, inkY1, inkY2) = merged.Width == 0
                                                   ? (0f, 0f, 0, 0)
                                                   : (merged.Left, merged.Left + merged.Width, this.Ascent + merged.Top,
                                                         this.Ascent + merged.Top + merged.Height);
            return new(pen, false, inkX1, inkX2, inkY1, inkY2, x => merged with { Left = merged.Left + (int)Rounding.Round(x) });
        }
    }
}
