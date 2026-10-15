using System.Collections.Generic;
using System.Linq;
using System.Text;

using FontChanger.DalamudPlugin.Rendering.Rasterization;
using FontChanger.Presets.Utilities;

namespace FontChanger.DalamudPlugin.Rendering.Faces.ElementFonts.MergingHelpers;

internal sealed unsafe class ImageTextFont(ImageElementFont? images, float size, Matrix2x2 transform) : IMergeTextFont
{
    public int Ascent { get; } = images?.GetLineMetrics(size, transform).Ascent ?? 0;

    public bool Has(Rune rune) => images is { CanDraw: true } && images.Has(rune, null);

    public MergeTextLine LayOut(string text, int letterSpacing)
    {
        var pieces = new List<RasterGlyph>();
        var codepoints = text.EnumerateRunes().ToArray();
        var pen = 0;
        for (var i = 0; i < codepoints.Length; i++)
        {
            if (images!.Rasterize(codepoints[i], size, transform) is not { } g)
                continue;
            pieces.Add(g with { Left = g.Left + pen });
            pen += i + 1 < codepoints.Length ? g.Advance + images.GetKerning(codepoints[i], codepoints[i + 1], size, transform) + letterSpacing : g.Advance;
        }

        var merged = RasterGlyph.Merge(pieces, pen);
        var (inkX1, inkX2, inkY1, inkY2) = merged.Width == 0
                                               ? (0f, 0f, 0, 0)
                                               : (merged.Left, merged.Left + merged.Width, this.Ascent + merged.Top, this.Ascent + merged.Top + merged.Height);
        return new(pen, false, inkX1, inkX2, inkY1, inkY2, x => merged with { Left = merged.Left + (int)Rounding.Round(x) });
    }
}
