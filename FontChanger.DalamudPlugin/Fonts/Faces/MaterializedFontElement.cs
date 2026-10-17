using System;
using System.Collections.Generic;
using System.Text;

using FontChanger.DalamudPlugin.Fonts.Enums;
using FontChanger.DalamudPlugin.Fonts.Records;
using FontChanger.DalamudPlugin.Utilities;
using FontChanger.Presets.Enums;
using FontChanger.Presets.Records;

namespace FontChanger.DalamudPlugin.Fonts.Faces;

/// <summary>
/// At a font size <c>px</c> an element draws at <c>px * Size / reference</c> and scales its pixel values by
/// <c>px / reference</c>, the reference being the font's first element's size.
/// </summary>
internal sealed class MaterializedFontElement(FontElement def, IFaceSource? source, float referenceSize) : IDisposable
{
    private readonly Dictionary<float, float> _referenceAdvances = [];

    public FontElement Def { get; } = def;

    /// <summary>Null when the glyphs are copied from the replaced game font.</summary>
    public IFaceSource? Source { get; } = source;

    public bool DrawsGame => this.Source is null;

    public bool Has(Rune rune) =>
        this.Def.Contains(rune) && this.Source is { } s && s.GetGlyphId(this.GetDrawnCodepoint(rune)) != 0;

    public Rune GetDrawnCodepoint(Rune rune) => this.Def.Replacements.TryGetValue(rune, out var to) ? to : rune;

    public float GetPx(float fontPx) => fontPx * this.Def.Size / referenceSize;

    public int GetLetterSpacing(float fontPx) => this.ScalePixels(this.Def.LetterSpacing, fontPx);

    public (int Ascent, int LineHeight) GetLineMetrics(float fontPx) => this.Source?.GetLineMetrics(this.GetPx(fontPx)) ?? (0, 0);

    /// <summary>Measured down from the line's top.</summary>
    public float GetRomanBaselineY(float fontPx, int ascent) =>
        this.Source is { } s && s.TryGetBaseline(this.GetPx(fontPx), Baseline.Roman, out var y) ? ascent - y : ascent;

    /// <summary>Measured down from the line's top.</summary>
    public float GetIdeographicCenterY(float fontPx, int ascent, int lineHeight)
    {
        if (this.Source is { } s)
        {
            var px = this.GetPx(fontPx);
            if (s.TryGetBaseline(px, Baseline.IdeographicFaceBottom, out var bottom) && s.TryGetBaseline(px, Baseline.IdeographicFaceTop, out var top))
                return ascent - ((bottom + top) / 2);
            if (s.TryGetBaseline(px, Baseline.IdeographicBottom, out var emBottom) && s.TryGetBaseline(px, Baseline.IdeographicTop, out var emTop))
                return ascent - ((emBottom + emTop) / 2);
        }

        return lineHeight / 2f;
    }

    /// <summary>
    /// As xivres wrapping_fixed_size_font; <paramref name="glyph"/> is <paramref name="run"/> as <paramref name="source"/>
    /// (the element's, or its italic) drew it at <paramref name="originX"/>, so that monospacing can draw it again narrower.
    /// </summary>
    public RasterGlyph Wrap(RasterGlyph glyph, float fontPx, in FontAlignment font, IFaceSource source, in GlyphRun run, float originX = 0)
    {
        var g = this.Monospace(fontPx, glyph, source, run, originX);
        return g with
        {
            Advance = g.Advance + this.GetLetterSpacing(fontPx),
            Left = g.Left + this.ScalePixels(this.Def.HorizontalOffset, fontPx),
            Top = g.Top + this.GetVerticalShift(fontPx, font),
        };
    }

    public void Dispose() => this.Source?.Dispose();

    /// <summary><paramref name="value"/> is in pixels at the font's reference size.</summary>
    private int ScalePixels(float value, float fontPx) => (int)Misc.StdRound(value * fontPx / referenceSize);

    /// <summary>As xivres merged_fixed_size_font get_vertical_adjustment, plus the vertical offset.</summary>
    private int GetVerticalShift(float fontPx, in FontAlignment font)
    {
        var (ascent, lineHeight) = this.GetLineMetrics(fontPx);
        var adjustment = font.Mode switch
        {
            VerticalAlignment.Top => 0,
            VerticalAlignment.Middle => (font.LineHeight - lineHeight) / 2,
            VerticalAlignment.Bottom => font.LineHeight - lineHeight,
            VerticalAlignment.RomanBaseline => (int)Misc.StdRound(font.RomanBaselineY - this.GetRomanBaselineY(fontPx, ascent)),
            VerticalAlignment.IdeographicCenter => (int)Misc.StdRound(font.IdeographicCenterY - this.GetIdeographicCenterY(fontPx, ascent, lineHeight)),
            _ => font.Ascent - ascent,
        };
        return ascent + adjustment - font.Ascent + this.ScalePixels(this.Def.VerticalOffset, fontPx);
    }

    private RasterGlyph Monospace(float fontPx, RasterGlyph glyph, IFaceSource source, in GlyphRun run, float originX)
    {
        if (this.Def.Monospacing is not { } mono || glyph.Advance <= 0)
            return glyph;

        var unit = mono.Unit switch
        {
            MonospacingUnit.Pixels => fontPx / referenceSize,
            MonospacingUnit.Em => this.GetPx(fontPx),
            _ => this.GetReferenceAdvance(fontPx, mono.ReferenceCharacter),
        };
        if (unit is not > 0)
            return glyph;

        var min = mono.Min is { } a ? Math.Max(0, (int)Misc.StdRound(a * unit)) : 0;
        var max = mono.Max is { } b ? Math.Max(0, (int)Misc.StdRound(b * unit)) : int.MaxValue;
        min = Math.Min(min, max);
        var cell = Math.Clamp(glyph.Advance, min, max);
        if (glyph.Width == 0)
            return glyph with { Advance = cell };

        int left;
        if (mono.Max is not null && cell > 0 && glyph.Width > cell)
        {
            // Hinting and rounding make the ink only roughly as narrow as the scale says, so narrower scales are tried.
            var scale = (float)cell / glyph.Width;
            var px = this.GetPx(fontPx);
            var fitted = glyph;
            for (var attempt = 0; attempt < 8; attempt++)
            {
                fitted = source.Rasterize(px, run, new(originX, null, scale)) ?? glyph;
                if (fitted.Width <= cell)
                    break;
                scale *= (float)cell / fitted.Width * 0.98f;
            }

            left = (cell - fitted.Width) >> 1;
            glyph = fitted;
        }
        else
        {
            left = mono.Alignment switch
            {
                MonospacingAlignment.Left => glyph.Left,
                MonospacingAlignment.CenterInk => (cell - glyph.Width) >> 1,
                MonospacingAlignment.Right => glyph.Left + cell - glyph.Advance,
                _ => glyph.Left + ((cell - glyph.Advance) >> 1),
            };
        }

        // Wrap adds the horizontal offset afterwards; it may nudge the ink in its cell, but not left of the pen.
        var offset = this.ScalePixels(this.Def.HorizontalOffset, fontPx);
        return glyph with { Advance = cell, Left = Math.Max(0, left + offset) - offset };
    }

    private float GetReferenceAdvance(float fontPx, Rune rune)
    {
        if (this._referenceAdvances.TryGetValue(fontPx, out var advance))
            return advance;

        advance = 0;
        if (this.Source is { } s && s.GetGlyphId(rune) is var id and not 0)
        {
            var px = this.GetPx(fontPx);
            ReadOnlySpan<uint> ids = [id];
            ReadOnlySpan<float> advances = [s.GetAdvance(px, id)];
            advance = s.GetScreenAdvance(px, new(ids, advances));
        }

        this._referenceAdvances.Add(fontPx, advance);
        return advance;
    }
}
