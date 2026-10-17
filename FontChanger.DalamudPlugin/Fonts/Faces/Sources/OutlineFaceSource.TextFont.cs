using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using FontChanger.DalamudPlugin.Fonts.Faces.Sources.Nominal;
using FontChanger.DalamudPlugin.Fonts.Records;
using FontChanger.DalamudPlugin.Utilities;
using FontChanger.Presets.Utilities;

using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;

namespace FontChanger.DalamudPlugin.Fonts.Faces.Sources;

internal sealed unsafe partial class OutlineFaceSource
{
    /// <summary><paramref name="transform"/> takes the element's transformation's place; synthesis's still applies.</summary>
    public MergingFaceSource.IMergeTextFont CreateMergeTextFont(float size, Matrix2x2 transform) => new TextFont(this, size, transform * this.Font.Transform);

    private sealed class TextFont(OutlineFaceSource font, float size, Matrix2x2 transform) : MergingFaceSource.IMergeTextFont
    {
        private readonly List<TextRun> _runs = [];
        private readonly List<ShapedCluster> _clusters = [];

        public int Ascent { get; } = font.GetLineMetrics(size, transform).Ascent;

        public bool Has(Rune rune) => font.GetGlyphId(rune) != 0;

        public MergingFaceSource.MergeTextLine LayOut(string text, int letterSpacing)
        {
            var spacing = transform.M11 != 0 ? letterSpacing / transform.M11 : 0;
            var (glyphs, advances, offsets) = this.Shape(text, spacing);
            var width = glyphs.Length != 0 ? advances.Sum() - spacing : 0;
            var advance = (int)Misc.StdRound(width * transform.M11);

            float inkX1 = float.MaxValue, inkX2 = float.MinValue;
            int inkY1 = int.MaxValue, inkY2 = int.MinValue;
            var along = 0f;
            for (var i = 0; i < glyphs.Length; i++)
            {
                var offset = offsets?[i] ?? default;
                var x = (transform.M11 * (along + offset.advanceOffset)) - (transform.M12 * offset.ascenderOffset);
                if (font.MeasureGlyph(size, glyphs[i], offset with { advanceOffset = 0 }, transform) is { } ink)
                {
                    var (left, right) = font.GetInkExtent(size, glyphs[i], transform);
                    (inkX1, inkX2) = (Math.Min(inkX1, x + left), Math.Max(inkX2, x + right));
                    (inkY1, inkY2) = (Math.Min(inkY1, ink.Top), Math.Max(inkY2, ink.Bottom));
                }

                along += advances[i];
            }

            if (inkX1 > inkX2 || inkY1 > inkY2)
                (inkX1, inkX2, inkY1, inkY2) = (0, 0, 0, 0);
            else
                (inkY1, inkY2) = (inkY1 + this.Ascent, inkY2 + this.Ascent);

            return new(advance, true, inkX1, inkX2, inkY1, inkY2, x => {
                if (glyphs.Length == 0)
                    return new RasterGlyph(advance, 0, 0, 0, 0, []);
                fixed (ushort* g = glyphs)
                fixed (float* a = advances)
                fixed (DWRITE_GLYPH_OFFSET* o = offsets)
                    return font.RasterizeRun(size, g, a, o, (uint)glyphs.Length, x, advance, transform);
            });
        }

        private (ushort[] Glyphs, float[] Advances, DWRITE_GLYPH_OFFSET[]? Offsets) Shape(string text, float spacing)
        {
            var glyphs = new List<ushort>();
            var advances = new List<float>();
            var offsets = new List<DWRITE_GLYPH_OFFSET>();
            var extra = font.GetExtraAdvance(size);
            font.Services.TextAnalyzer.Itemize(text, this._runs);
            foreach (var run in this._runs)
            {
                for (var start = run.Start; start < run.End;)
                {
                    this._clusters.Clear();
                    start += font.Shape(size, text.AsSpan(start, run.End - start), run.Script, this._clusters);
                    foreach (var c in this._clusters)
                    {
                        for (var i = 0; i < c.GlyphIds.Length; i++)
                        {
                            glyphs.Add((ushort)c.GlyphIds[i]);
                            advances.Add(c.Advances[i] + extra);
                            var o = c.Offsets?[i] ?? default;
                            offsets.Add(new() { advanceOffset = o.Advance, ascenderOffset = o.Ascender });
                        }

                        if (advances.Count != 0)
                            advances[^1] += spacing;
                    }

                    // Shaping stops at a glyph the font lacks.
                    if (start < run.End)
                        start += char.IsSurrogatePair(text, start) ? 2 : 1;
                }
            }

            var hasOffsets = offsets.Exists(o => o.advanceOffset != 0 || o.ascenderOffset != 0);
            return (glyphs.ToArray(), advances.ToArray(), hasOffsets ? offsets.ToArray() : null);
        }
    }

    private (int Top, int Bottom)? MeasureGlyph(float size, ushort glyph, DWRITE_GLYPH_OFFSET offset, Matrix2x2 transform)
    {
        var advance = 0f;
        RECT bounds;
        if (this._freeTypeFace is not null)
        {
            var g = this.DrawRun(size, &glyph, &advance, &offset, 1, 0, 0, transform);
            bounds = new() { left = g.Left, top = g.Top, right = g.Left + g.Width, bottom = g.Top + g.Height };
        }
        else
        {
            bounds = this.Services.DirectWrite.GetRunBounds(this.Font.GetFace(size), size, &glyph, &advance, &offset, 1, this.DirectWriteParams, transform);
        }

        return bounds.right <= bounds.left || bounds.bottom <= bounds.top ? null : (bounds.top, bounds.bottom);
    }

    // Side bearings give the ink's extent in fractional pixels, though antialiased pixels may reach past them.
    private (float Left, float Right) GetInkExtent(float size, ushort glyph, Matrix2x2 transform)
    {
        DWRITE_GLYPH_METRICS gm;
        if (this.Font.GetFace(size)->GetDesignGlyphMetrics(&glyph, 1, &gm, BOOL.FALSE).FAILED)
            return (0, 0);
        var scale = size / this.Font.Metrics.designUnitsPerEm * transform.M11;
        var (x1, x2) = (gm.leftSideBearing * scale, ((int)gm.advanceWidth - gm.rightSideBearing) * scale);
        return x1 <= x2 ? (x1, x2) : (x2, x1);
    }
}
