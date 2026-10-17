using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

using FontChanger.DalamudPlugin.PluginInterface;
using FontChanger.DalamudPlugin.Rendering.Rasterization;
using FontChanger.Presets.Enums;
using FontChanger.Presets.Utilities;

namespace FontChanger.DalamudPlugin.Rendering.Faces.Sources.Nominal;

/// <summary>
/// Glyph images (FontChanger GlyphFiles, xivres image_fixed_size_font): SVG documents and bitmaps by codepoint, one glyph
/// per codepoint, with font.json's units, line metrics and pair kerning. Glyph IDs are codepoints.
/// </summary>
/// <remarks>
/// SVG documents are in units of <c>UnitsPerEm</c> per em, with y growing downwards and the baseline at y =
/// <c>BaselineY</c>, unless their root's data-xivfont-units-per-em and data-xivfont-baseline say otherwise (and the
/// element doesn't set them). The origin is at x = 0, and the advance is data-xivfont-advance, the viewBox's width, or
/// the width. Bitmaps are in pixels at the size they were exported at, with the baseline at its ascent.
/// </remarks>
internal sealed partial class ImageFaceSource : NominalFaceSource
{
    private const float DefaultUnitsPerEm = 1000;
    private const float DefaultBaselineY = 880;

    private readonly Dictionary<Rune, Source> _glyphs;
    private readonly ImageRenderer? _renderer;
    private readonly Matrix2x2 _transform;
    private readonly byte[]? _gamma;
    private readonly float _unitsPerEm;
    private readonly float _baselineY;
    private readonly bool _unitsPerEmOverrides;
    private readonly bool _baselineYOverrides;
    private readonly float? _bitmapUnitsPerEm;
    private readonly float? _bitmapBaselineY;
    private readonly float _ascent;
    private readonly float _lineHeight;
    private readonly ImageCoverageMode _coverageMode;
    private readonly Dictionary<(Rune, Rune), float> _kerning = [];

    // Advances are those of the drawn glyphs, which drawing finds: kept by size and codepoint.
    private readonly Dictionary<(float Px, uint Id), float> _advances = [];

    /// <summary>Gets whether it draws anything: Direct2D is there.</summary>
    private bool CanDraw => this._renderer is not null;

    public override IEnumerable<Rune> Codepoints => this._glyphs.Keys;

    public override uint GetGlyphId(Rune rune) => this.CanDraw && this._glyphs.ContainsKey(rune) ? (uint)rune.Value : 0;

    public override float GetAdvance(float px, uint glyphId)
    {
        if (!this._advances.TryGetValue((px, glyphId), out var advance))
        {
            advance = this.RasterizeGlyph(px, glyphId)?.Advance ?? 0;
            this._advances.Add((px, glyphId), advance);
        }

        return advance;
    }

    public override (int Ascent, int LineHeight) GetLineMetrics(float px) => this.GetLineMetrics(px, this._transform);

    /// <summary>Gets the line metrics at a size, scaled vertically as glyphs transformed by <paramref name="transform"/> are.</summary>
    private (int Ascent, int LineHeight) GetLineMetrics(float px, Matrix2x2 transform)
    {
        var scale = px / this._unitsPerEm * MathF.Abs(transform.M22);
        return ((int)Rounding.Round(this._ascent * scale), (int)Rounding.Round(this._lineHeight * scale));
    }

    public override RasterGlyph? RasterizeGlyph(float px, uint glyphId) =>
        Rune.IsValid(glyphId) ? this.Rasterize(new((int)glyphId), px, this._transform) : null;

    public override float GetKerning(float px, uint leftGlyphId, uint rightGlyphId) =>
        Rune.IsValid(leftGlyphId) && Rune.IsValid(rightGlyphId) ? this.GetKerning(new((int)leftGlyphId), new((int)rightGlyphId), px, this._transform) : 0;

    /// <summary>Gets the kerning between two codepoints' glyphs at a size, transformed by <paramref name="transform"/>.</summary>
    private int GetKerning(Rune left, Rune right, float px, Matrix2x2 transform) =>
        this._kerning.TryGetValue((left, right), out var k) ? (int)Rounding.Round(k * px * transform.M11 / this._unitsPerEm) : 0;

    /// <summary>Draws a codepoint's glyph at a size, transformed by <paramref name="transform"/>, with the gamma; null if there is none.</summary>
    private RasterGlyph? Rasterize(Rune rune, float px, Matrix2x2 transform)
    {
        if (this._renderer is not { } renderer || !this._glyphs.TryGetValue(rune, out var source))
            return null;

        try
        {
            float unitsPerEm, baselineY, advance, x1, y1, x2, y2;
            if (source.Svg is not null)
            {
                var metrics = source.SvgMetrics ?? throw new InvalidDataException("Not an SVG document.");
                unitsPerEm = this._unitsPerEmOverrides ? this._unitsPerEm : metrics.UnitsPerEm ?? this._unitsPerEm;
                baselineY = this._baselineYOverrides ? this._baselineY : metrics.BaselineY ?? this._baselineY;
                advance = metrics.Advance ?? unitsPerEm;

                // Drawings may go past the line box: this much room is left around it, and the ink is trimmed afterwards.
                (x1, y1, x2, y2) = (-unitsPerEm, baselineY - (2 * unitsPerEm), Math.Max(advance, 0) + unitsPerEm, baselineY + unitsPerEm);
            }
            else
            {
                unitsPerEm = this._bitmapUnitsPerEm ?? px;
                baselineY = this._bitmapBaselineY ?? Rounding.Round(unitsPerEm * DefaultBaselineY / DefaultUnitsPerEm);
                advance = source.Advance ?? source.Width;
                (x1, y1) = (-source.OriginX, -source.OriginY);
                (x2, y2) = (x1 + source.Width, y1 + source.Height);
            }

            if (!(unitsPerEm > 0))
                return null;

            // Units to pixels relative to the pen on the baseline: scaled, the baseline moved to the origin, transformed.
            var s = px / unitsPerEm;
            Span<float> m =
            [
                transform.M11 * s, transform.M12 * s, -transform.M12 * s * baselineY,
                transform.M21 * s, transform.M22 * s, -transform.M22 * s * baselineY,
            ];
            var advancePx = (int)Rounding.Round(advance * s * transform.M11);

            // The pixels the rectangle covers, with one to spare for antialiasing.
            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            foreach (var (x, y) in (ReadOnlySpan<(float, float)>)[(x1, y1), (x2, y1), (x1, y2), (x2, y2)])
            {
                var px1 = (m[0] * x) + (m[1] * y) + m[2];
                var py1 = (m[3] * x) + (m[4] * y) + m[5];
                (minX, maxX, minY, maxY) = (Math.Min(minX, px1), Math.Max(maxX, px1), Math.Min(minY, py1), Math.Max(maxY, py1));
            }

            var left = (int)MathF.Floor(minX) - 1;
            var top = (int)MathF.Floor(minY) - 1;
            var width = (int)MathF.Ceiling(maxX) + 1 - left;
            var height = (int)MathF.Ceiling(maxY) + 1 - top;
            if (width <= 0 || height <= 0 || width > 8192 || height > 8192)
                return new RasterGlyph(advancePx, 0, 0, 0, 0, []);
            m[2] -= left;
            m[5] -= top;

            byte[] coverage;
            if (source.Svg is not null)
            {
                coverage = renderer.DrawSvg(source.Svg, m, width, height);
            }
            else
            {
                // Pixel art keeps its pixels when it is only moved, or scaled by whole numbers.
                var whole = transform is { M12: 0, M21: 0 }
                            && MathF.Abs(m[0] - Rounding.Round(m[0])) < 1e-4f
                            && MathF.Abs(m[4] - Rounding.Round(m[4])) < 1e-4f;
                coverage = renderer.DrawCoverage(this.GetBitmapCoverage(source), source.Width, source.Height, (x1, y1, x2, y2), m, width, height, whole);
            }

            var glyph = new RasterGlyph(advancePx, left, top, width, height, coverage).Trimmed();
            return this._gamma is null ? glyph : glyph.WithCoverage(this._gamma);
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Glyph image U+{rune:X4} can't be drawn", rune);
            return new RasterGlyph(0, 0, 0, 0, 0, []);
        }
    }

    /// <summary>Gets a bitmap's coverage, by its alpha, darkness or brightness.</summary>
    private byte[] GetBitmapCoverage(Source source)
    {
        var pixels = source.Pixels!;
        var mode = this._coverageMode;
        if (mode == ImageCoverageMode.Auto)
            mode = pixels.Any(p => p >> 24 != 0xFF) ? ImageCoverageMode.Alpha : ImageCoverageMode.Darkness;

        var coverage = new byte[pixels.Length];
        for (var i = 0; i < pixels.Length; i++)
        {
            var p = pixels[i];
            int b = (byte)p, g = (byte)(p >> 8), r = (byte)(p >> 16), a = (byte)(p >> 24);
            var luminance = ((2126 * r) + (7152 * g) + (722 * b) + 5000) / 10000;
            coverage[i] = mode switch
            {
                ImageCoverageMode.Darkness => (byte)((255 - luminance) * a / 255),
                ImageCoverageMode.Brightness => (byte)(luminance * a / 255),
                _ => (byte)a,
            };
        }

        return coverage;
    }
}
