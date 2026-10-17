using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;

using FontChanger.Presets.Records;

namespace FontChanger.Presets;

public static partial class FontsFromSystemFont
{
    private static bool IsLeftToGame(IFontProbe font, IFontProbe? baseFont, IFontProbe? gameFont, CjkGroup group)
    {
        if (gameFont is null)
            return false;

        int total = 0, left = 0;
        foreach (var rune in gameFont.Codepoints)
        {
            if (!group.Codepoints.Contains(rune))
                continue;
            total++;
            if (!font.TryGetGlyph(rune, out _) && baseFont?.TryGetGlyph(rune, out _) != true)
                left++;
        }

        return left * 20 > total;
    }

    // Below the CJK blocks, the game draws some symbols an em wide as Japanese fonts do, such as arrows and circled digits.
    private static List<RuneRange> GetEmWideSymbols(IFontProbe font, IFontProbe? gameFont)
    {
        if (gameFont is null)
            return [];

        var res = new List<RuneRange>();
        foreach (var rune in gameFont.Codepoints)
        {
            var c = rune.Value;
            var isSymbol = c is (>= 0x2000 and < 0x2E80) or (>= 0xA0 and <= 0xBF) or 0xD7 or 0xF7;
            if (!isSymbol || !gameFont.TryGetGlyph(rune, out var gameGlyph) || gameGlyph.AdvanceWidth < 0.9f)
                continue;
            if (font.TryGetGlyph(rune, out var glyph) && glyph.AdvanceWidth >= 0.9f)
                res.Add(new(rune, rune));
        }

        return res.Coalesce();
    }

    private static InkRange? MeasureInk(IFontProbe font, string samples)
    {
        var tops = new List<float>();
        var bottoms = new List<float>();
        var count = 0;
        foreach (var rune in samples.EnumerateRunes())
        {
            count++;
            if (font.TryGetGlyph(rune, out var g) && g.BoundingBoxTop > g.BoundingBoxBottom)
            {
                tops.Add(g.BoundingBoxTop);
                bottoms.Add(g.BoundingBoxBottom);
            }
        }

        if (tops.Count == 0 || tops.Count * 2 < count)
            return null;

        return new(Median(tops), Median(bottoms));

        static float Median(List<float> v)
        {
            v.Sort();
            return (v[v.Count / 2] + v[(v.Count - 1) / 2]) / 2;
        }
    }

    /// <summary>Targets are in pixels above the baseline.</summary>
    private static GroupPlacement FitInk(IFontProbe font, InkRange ink, float targetTop, float targetBottom, float latinSize, float horizontalOffset)
    {
        // The clamp keeps a script whose samples are drawn unusually near the Latin letters' size.
        var exactSize = Math.Clamp((targetTop - targetBottom) / (ink.Top - ink.Bottom), latinSize * 0.8f, latinSize * 1.25f);
        GetSizeAndMode(font, exactSize, out var size, out var renderMode);
        var up = ((targetTop + targetBottom) / 2) - ((size * (ink.Top + ink.Bottom)) / 2);
        return new(size, renderMode, horizontalOffset, StdRound(-up));
    }

    private static GroupPlacement KeepWithinLine(IFontProbe font, InkRange ink, GroupPlacement placement, LineMetrics? line, float latinSize)
    {
        if (line is not { Descent: > 0 } l)
            return placement;

        var top = (ink.Top * placement.Size) - placement.VerticalOffset;
        var bottom = (ink.Bottom * placement.Size) - placement.VerticalOffset;
        if (top <= l.Ascent && bottom >= -l.Descent)
            return placement;
        return FitInk(font, ink, MathF.Min(top, l.Ascent), MathF.Max(bottom, -l.Descent), latinSize, placement.HorizontalOffset);
    }

    /// <summary>Least-squares fits the cap and x-heights to the game's, measured from the capitals' bottom or the baseline if
    /// they reach below it, so that letters drawn above the baseline move onto it.</summary>
    private static LatinPlacement FitLatin(IFontProbe font, Traits traits, float size)
    {
        var targetCapHeight = size * traits.ReferenceHeight;
        var exactSize = size;
        var latinBottom = 0f;
        if (MeasureInk(font, traits.ReferenceCharacters) is { } caps)
        {
            latinBottom = MathF.Max(0, caps.Bottom);
            var capHeight = caps.Top - latinBottom;
            var numerator = capHeight * targetCapHeight;
            var denominator = capHeight * capHeight;

            // Lowercase letters as tall as the capitals are capitals or small capitals, which the game's are not.
            if (traits.XHeightPerCapHeight is { } xHeightPerCapHeight && MeasureInk(font, "xzvw") is { } x)
            {
                var xHeight = x.Top - latinBottom;
                if (xHeight > 0 && xHeight < capHeight * 0.9f)
                {
                    numerator += xHeight * targetCapHeight * xHeightPerCapHeight;
                    denominator += xHeight * xHeight;
                }
            }

            if (capHeight > 0)
                exactSize = numerator / denominator;
        }

        GetSizeAndMode(font, exactSize, out var fontSize, out var renderMode);
        return new(fontSize, renderMode, StdRound(latinBottom * fontSize));
    }

    private static LatinPlacement ScaleLatin(IFontProbe font, LatinPlacement latin, float by)
    {
        if (by == 0 || latin.Size <= 0)
            return latin;
        GetSizeAndMode(font, latin.Size * (1 + by), out var size, out var renderMode);
        return new(size, renderMode, StdRound((latin.VerticalOffset * size) / latin.Size));
    }

    private static GroupPlacement ScaleAroundInk(IFontProbe font, InkRange ink, GroupPlacement placement, float by)
    {
        if (by == 0)
            return placement;
        GetSizeAndMode(font, placement.Size * (1 + by), out var size, out var renderMode);
        var center = (ink.Top + ink.Bottom) / 2;
        return placement with { Size = size, RenderMode = renderMode, VerticalOffset = StdRound(placement.VerticalOffset + (center * (size - placement.Size))) };
    }

    private static ExclamationMarkPlacement PlaceExclamationMark(
        ExclamationMark mark,
        IFontProbe digits,
        IFontProbe italic,
        float fontSize,
        float size,
        float latinOffset)
    {
        GetSizeAndMode(italic, fontSize * mark.SizePerDigitSize, out var markSize, out var renderMode);
        var markOffset = StdRound(mark.OffsetPerSize * size);

        var digitRight = Enumerable.Range('0', 10)
                                   .Select(c => digits.TryGetGlyph(new(c), out var g) ? g.RightSideBearing : (float?)null)
                                   .OfType<float>()
                                   .DefaultIfEmpty(0)
                                   .Min() * fontSize;
        var glyph = italic.TryGetGlyph(new('!'), out var bang) ? bang : default;

        var excess = (markOffset + (glyph.LeftSideBearing * markSize) + digitRight) - latinOffset - (mark.GapPerSize * size);
        var digitOffset = latinOffset + StdRound(Math.Clamp(excess, 0, size / 3));
        if (excess < 0)
            markOffset -= StdRound(excess);

        var inkWidth = (glyph.AdvanceWidth - glyph.LeftSideBearing - glyph.RightSideBearing) * markSize;
        var spacing = StdRound((mark.PitchPerInk * inkWidth) - (glyph.AdvanceWidth * markSize));
        return new(digitOffset, markSize, renderMode, markOffset, StdRound(mark.VerticalOffsetPerSize * size), spacing);
    }

    private static float StdRound(float value) => MathF.Round(value, MidpointRounding.AwayFromZero);

    private static bool HasText(IFontProbe font, string text) => text.EnumerateRunes().All(r => r.Value == '\n' || font.TryGetGlyph(r, out _));

    // Tabular figures (tnum) don't count, as features only apply to shaped text, which glyphs drawn one by one aren't.
    [SuppressMessage("ReSharper", "CompareOfFloatsByEqualityOperator")]
    private static bool HasTabularDigits(IFontProbe font)
    {
        if (!font.TryGetGlyph(new('0'), out var b0))
            return false;
        for (var i = '1'; i <= '9'; ++i)
        {
            if (!font.TryGetGlyph(new(i), out var b) || b.AdvanceWidth != b0.AdvanceWidth)
                return false;
        }

        return true;
    }

    /// <summary>The DWRITE_RENDERING_MODE1 that fonts made here use at a size, for fonts drawn at sizes not fitted here, such
    /// as the system's fallback fonts.</summary>
    public static int GetRenderMode(IFontProbe font, float size)
    {
        GetSizeAndMode(font, size, out _, out var renderMode);
        return renderMode;
    }

    // Natural symmetric downsampled trades quality for speed, which glyphs rendered once don't need, and outline is drawn as
    // natural symmetric, so neither is used.
    private static void GetSizeAndMode(IFontProbe font, float exactSize, out float size, out int renderMode)
    {
        var rounded = StdRound(exactSize);
        if (rounded > 0 && font.HasBitmapOf((int)rounded))
        {
            size = rounded;
            renderMode = RenderNatural;
            return;
        }

        size = StdRound(exactSize * 10) / 10;
        renderMode = font.PrefersNatural(size) ? RenderNatural : RenderNaturalSymmetric;
    }

    private sealed record ExclamationMarkPlacement(
        float DigitOffset,
        float Size,
        int RenderMode,
        float Offset,
        float VerticalOffset,
        float Spacing);

    /// <summary>In ems above the baseline.</summary>
    private readonly record struct InkRange(float Top, float Bottom);

    /// <summary>In pixels, the descent below the baseline.</summary>
    private readonly record struct LineMetrics(float Ascent, float Descent);

    private readonly record struct LatinPlacement(float Size, int RenderMode, float VerticalOffset);

    /// <summary>Offsets are in pixels, VerticalOffset downwards.</summary>
    private readonly record struct GroupPlacement(float Size, int RenderMode, float HorizontalOffset, float VerticalOffset);

    /// <summary>Pixels by which the game's AXIS draws glyphs right of (Latin, PUA) and below (CJK, PUA) where AXIS Basic ProN
    /// would, assumed to scale with the size for other families.</summary>
    private readonly record struct Placement(float LatinOffset, float PuaOffset, float CjkShift)
    {
        // nominalSize is that of the game's font by its name, which larger fonts are scaled from.
        public static Placement Of(Traits traits, float size, float nominalSize)
        {
            var res = !traits.IsAxis ? new Placement(MathF.Max(1, StdRound(nominalSize / 12)), 0, 0)
                      : nominalSize >= 36 ? new Placement(3, 3, -1)
                      : new Placement(1, 1, 0);
            var scale = nominalSize > 0 ? size / nominalSize : 1;
            return new(StdRound(res.LatinOffset * scale), StdRound(res.PuaOffset * scale), StdRound(res.CjkShift * scale));
        }
    }
}
