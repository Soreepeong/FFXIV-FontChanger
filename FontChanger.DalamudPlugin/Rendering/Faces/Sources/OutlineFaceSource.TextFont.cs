using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using FontChanger.DalamudPlugin.Rendering.Faces.Sources.Nominal;
using FontChanger.DalamudPlugin.Rendering.Rasterization;
using FontChanger.Presets.Utilities;

using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;

namespace FontChanger.DalamudPlugin.Rendering.Faces.Sources;

// Glyph merging's texts drawn with the font: laid out with DirectWrite (letter spacing and all), measured by the glyphs'
// outlines, and drawn as the source draws.
internal abstract unsafe partial class OutlineFaceSource
{
    /// <summary>
    /// Makes the font glyph merging's texts are drawn with at a size, transformed by <paramref name="transform"/> on top
    /// of synthesis's.
    /// </summary>
    public MergingFaceSource.IMergeTextFont CreateMergeTextFont(float size, Matrix2x2 transform) => new TextFont(this, size, transform * this.Font.Transform);

    private sealed class TextFont(OutlineFaceSource font, float size, Matrix2x2 transform) : MergingFaceSource.IMergeTextFont, IGlyphRunSink
    {
        private readonly List<Run> _runs = [];

        public int Ascent { get; } = font.GetLineMetrics(size, transform).Ascent;

        public bool Has(Rune rune) => font.GetGlyphId(rune) != 0;

        public MergingFaceSource.MergeTextLine LayOut(string text, int letterSpacing)
        {
            this._runs.Clear();
            var spacing = text.Length != 0 && transform.M11 != 0 ? letterSpacing / transform.M11 : 0;
            if (text.Length != 0)
                this.Shape(text, spacing);

            var runs = this._runs.ToArray();
            var extra = font.GetExtraAdvance(size);
            var width = runs.Sum(r => r.Advances.Sum() + (extra * r.Glyphs.Length)) - spacing;
            var advance = (int)Rounding.Round(width * transform.M11);

            float inkX1 = float.MaxValue, inkX2 = float.MinValue;
            int inkY1 = int.MaxValue, inkY2 = int.MinValue;
            var pen = 0f;
            foreach (var r in runs)
            {
                var runFace = font.GetRunFace(r.Face, r.EmSize);
                var along = 0f;
                for (var i = 0; i < r.Glyphs.Length; i++)
                {
                    var offset = r.Offsets?[i] ?? default;
                    var x = (pen + (transform.M11 * (along + offset.advanceOffset))) - (transform.M12 * offset.ascenderOffset);
                    if (font.MeasureGlyph(runFace, r.EmSize, r.Glyphs[i], offset with { advanceOffset = 0 }, transform) is { } ink)
                    {
                        var (left, right) = font.GetInkExtent(runFace, r.EmSize, r.Glyphs[i], transform);
                        (inkX1, inkX2) = (Math.Min(inkX1, x + left), Math.Max(inkX2, x + right));
                        (inkY1, inkY2) = (Math.Min(inkY1, ink.Top), Math.Max(inkY2, ink.Bottom));
                    }

                    along += r.Advances[i] + extra;
                }

                pen += along * transform.M11;
            }

            if (inkX1 > inkX2 || inkY1 > inkY2)
                (inkX1, inkX2, inkY1, inkY2) = (0, 0, 0, 0);
            else
                (inkY1, inkY2) = (inkY1 + this.Ascent, inkY2 + this.Ascent);

            return new(advance, true, inkX1, inkX2, inkY1, inkY2, x => {
                var pieces = new List<RasterGlyph>();
                foreach (var r in runs)
                {
                    var advances = r.Advances.Select(a => a + extra).ToArray();
                    fixed (ushort* g = r.Glyphs)
                    fixed (float* a = advances)
                    fixed (DWRITE_GLYPH_OFFSET* o = r.Offsets)
                    {
                        var runFace = font.GetRunFace(r.Face, r.EmSize);
                        pieces.Add(font.RasterizeRun(runFace, r.EmSize, g, a, o, (uint)r.Glyphs.Length, x, 0, 1, transform));
                    }

                    x += advances.Sum() * transform.M11;
                }

                return RasterGlyph.Merge(pieces, advance);
            });
        }

        void IGlyphRunSink.OnGlyphRun(IDWriteFontFace5* face, in DWRITE_GLYPH_RUN run, in DWRITE_GLYPH_RUN_DESCRIPTION description)
        {
            // Glyphs the font lacks are left out.
            var glyphs = new List<ushort>();
            var advances = new List<float>();
            var offsets = run.glyphOffsets is null ? null : new List<DWRITE_GLYPH_OFFSET>();
            for (var i = 0; i < run.glyphCount; i++)
            {
                if (run.glyphIndices[i] == 0)
                {
                    if (advances.Count != 0)
                        advances[^1] += run.glyphAdvances[i];
                    continue;
                }

                glyphs.Add(run.glyphIndices[i]);
                advances.Add(run.glyphAdvances[i]);
                offsets?.Add(run.glyphOffsets[i]);
            }

            if (glyphs.Count != 0)
                this._runs.Add(new(face, run.fontEmSize, glyphs.ToArray(), advances.ToArray(), offsets?.ToArray()));
        }

        private void Shape(string text, float letterSpacing)
        {
            var rasterizer = font._rasterizer;
            using var format = rasterizer.CreateFormat(
                font._def.Lookup.Name,
                font.Font.LayoutWeight,
                font.Font.LayoutStyle,
                font.Font.LayoutStretch,
                size,
                rasterizer.NoFallback);
            using var layout = rasterizer.CreateLayout(text, format.Get());

            var range = new DWRITE_TEXT_RANGE { startPosition = 0, length = (uint)text.Length };
            font.ApplyTo(layout.Get(), range, size);
            if (letterSpacing != 0)
                layout.Get()->SetCharacterSpacing(0, letterSpacing, 0, range);

            layout.Get()->CollectTo(this);
        }

        private sealed record Run(Pointer<IDWriteFontFace5> Face, float EmSize, ushort[] Glyphs, float[] Advances, DWRITE_GLYPH_OFFSET[]? Offsets);
    }
}
