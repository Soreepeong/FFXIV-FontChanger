using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text;
using System.Text.Unicode;

using FontChanger.Presets.Enums;
using FontChanger.Presets.Records;

namespace FontChanger.Presets;

/// <summary>Creates a best-effort guess for making a face given a system font.</summary>
public static class FaceFromFont
{
    private const string LodestoneFontName = "XIV AXIS Std ATK";

    private const float DefaultGamma = 1.4f;

    private const int RenderNatural = 4;
    private const int RenderNaturalSymmetric = 5;
    private const int MeasureGdiNatural = 2;
    private const int GridFitEnabled = 2;
    private const int StyleItalic = 2;

    private static readonly RuneRange[] AllCodepoints = RuneRange.From(0x20, 0x10FFFF);
    private static readonly RuneRange[] LatinCodepoints = RuneRange.From(0x20, 0x2FFF);
    private static readonly RuneRange[] CjkCodepoints = RuneRange.From(0x3000, 0x10FFFF);
    private static readonly RuneRange[] PrivateUseCodepoints = RuneRange.From(0xE000, 0xF8FF);
    private static readonly RuneRange[] HanCodepoints = RuneRange.From(UnicodeRanges.CjkUnifiedIdeographsExtensionA, UnicodeRanges.CjkUnifiedIdeographs);

    private static readonly RuneRange[] HangulCodepoints = RuneRange.From(
        UnicodeRanges.HangulJamo,
        UnicodeRanges.HangulCompatibilityJamo,
        UnicodeRanges.HangulSyllables);

    public static Face? MakeFace(
        string gameFontFaceName,
        float gameSize,
        LookupStruct font,
        TryOpenFontDelegate tryOpenFont,
        bool monospacedDigits = true) =>
        MakeElements(Preset.FamilyOf(gameFontFaceName), gameSize, font, tryOpenFont, monospacedDigits) is { } elements
            ? new()
            {
                Name = gameFontFaceName,
                Elements =
                [
                    new() { Size = gameSize, Renderer = Renderer.Game, Ranges = RuneRange.From(0, 0x10FFFF) },
                    .. elements
                ]
            }
            : null;

    public static IReadOnlyList<FaceElement>? MakeElements(
        string family,
        float size,
        LookupStruct chosen,
        TryOpenFontDelegate tryOpenFont,
        bool monospacedDigits = true)
    {
        if (!tryOpenFont(chosen, out var font))
            return null;

        var traits = Traits.Of(family);
        var features = new Dictionary<uint, uint>(chosen.Features);
        if (monospacedDigits)
            features[Preset.Tag("tnum"u8)] = 1;
        foreach (var feature in traits.Features)
            features[Preset.Tag(feature)] = 1;
        var lookup = chosen with { Features = features };

        var inkHeight = GetInkHeight(font, traits.ReferenceGlyph);
        if (inkHeight <= 0)
            inkHeight = traits.ReferenceHeight;
        GetSizeAndMode(font, (size * traits.ReferenceHeight) / inkHeight, out var fontSize, out var renderMode);

        var large = size >= 36;
        var latinOffset = traits.IsAxis ? large ? 3 : 1 : MathF.Max(1, StdRound(size / 12));
        var (cjkOffset, cjkShift) = traits.IsAxis && large ? (1f, -1f) : (0f, 0f);
        var (puaOffset, puaShift) = large ? (3f, -1f) : (1f, 0f);

        // CriticalDirectHit!!
        FaceElement? markElement = null;
        if (traits.Mark is { } mark)
        {
            var italic = chosen with { Style = StyleItalic };
            var italicFont = tryOpenFont(italic, out var openedItalic) ? openedItalic : font;
            var placed = PlaceExclamationMark(mark, font, italicFont, fontSize, size, latinOffset);
            latinOffset = placed.DigitOffset;
            markElement = Make(
                italic,
                placed.Size,
                placed.RenderMode,
                1.2f,
                RuneRange.From('!', '!'),
                MergeMode.Replace,
                placed.Offset,
                placed.BaselineShift,
                placed.Spacing);
        }

        var hasKana = HasText(font, "あア");
        var hasHangul = HasText(font, "가");
        var hasHan = HasText(font, "中");
        var hasHanForChinese = HasText(font, "们");
        var needFakeTabularDigits = monospacedDigits && !HasTabularDigits(font);
        var needPua = traits.IsAxis;

        var res = new List<FaceElement>();
        if (traits.HasLetters && hasHangul)
            res.Add(Make(lookup, fontSize, renderMode, 1.2f, HangulCodepoints, MergeMode.AddAll, cjkOffset, cjkShift));
        if (traits.HasLetters && hasHanForChinese)
            res.Add(Make(lookup, fontSize, renderMode, 1.2f, HanCodepoints, MergeMode.AddAll, cjkOffset, cjkShift));

        if (traits.IsAxis && (hasKana || hasHangul || hasHan))
        {
            res.Add(Make(lookup, fontSize, renderMode, 1.2f, LatinCodepoints, MergeMode.Replace, latinOffset));
            res.Add(Make(lookup, fontSize, renderMode, 1.2f, CjkCodepoints, MergeMode.Replace, cjkOffset, cjkShift));
        }
        else
        {
            res.Add(Make(lookup, fontSize, renderMode, 1.2f, AllCodepoints, MergeMode.Replace, latinOffset));
        }

        if (needFakeTabularDigits)
        {
            res.Add(
                Make(
                    lookup,
                    fontSize,
                    renderMode,
                    1.2f,
                    RuneRange.From('0', '9'),
                    MergeMode.Replace,
                    latinOffset,
                    monospacing: new(1, 1, MonospacingUnit.ReferenceGlyph, new('0'), MonospacingAlignment.CenterAdvance)));
        }

        if (markElement is not null)
            res.Add(markElement);

        if (needPua)
        {
            res.Add(
                Make(
                    LookupStruct.Of(LodestoneFontName),
                    size,
                    RenderNaturalSymmetric,
                    1.4f,
                    PrivateUseCodepoints,
                    MergeMode.Replace,
                    puaOffset,
                    puaShift));
            res.AddRange(MakeAxisElements(chosen, font, size));
        }

        return res;
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
        var glyph = italic.GetGlyphOrDefault(new('!'));

        var excess = (markOffset + (glyph.LeftSideBearing * markSize) + digitRight) - latinOffset - (mark.GapPerSize * size);
        var digitOffset = latinOffset + StdRound(Math.Clamp(excess, 0, size / 3));
        if (excess < 0)
            markOffset -= StdRound(excess);

        var inkWidth = (glyph.AdvanceWidth - glyph.LeftSideBearing - glyph.RightSideBearing) * markSize;
        var spacing = StdRound((mark.PitchPerInk * inkWidth) - (glyph.AdvanceWidth * markSize));
        return new(digitOffset, markSize, renderMode, markOffset, StdRound(mark.BaselineShiftPerSize * size), spacing);
    }

    private static float StdRound(float value) => MathF.Round(value, MidpointRounding.AwayFromZero);

    private static bool HasText(IFontProbe font, string text) => text.EnumerateRunes().All(r => r.Value == '\n' || font.TryGetGlyph(new(r.Value), out _));

    private static float GetInkHeight(IFontProbe font, Rune rune) =>
        font.TryGetGlyph(rune, out var g) ? g.BoundingBoxTop - MathF.Max(0, g.BoundingBoxBottom) : 0;

    [SuppressMessage("ReSharper", "CompareOfFloatsByEqualityOperator")]
    private static bool HasTabularDigits(IFontProbe font)
    {
        if (font.HasGposFeature("tnum") || font.HasGsubFeature("tnum"))
            return true;
        if (!font.TryGetGlyph(new('0'), out var b0))
            return false;
        for (var i = '1'; i <= '9'; ++i)
        {
            if (!font.TryGetGlyph(new(i), out var b) || b.AdvanceWidth != b0.AdvanceWidth)
                return false;
        }

        return true;
    }

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
        renderMode = RenderNaturalSymmetric;
    }

    private static FaceElement Make(
        LookupStruct lookup,
        float size,
        int renderMode,
        float gamma,
        IReadOnlyList<RuneRange> ranges,
        MergeMode mergeMode,
        float horizontalOffset = 0,
        float baselineShift = 0,
        float letterSpacing = 0,
        GlyphMergingStruct? glyphMerging = null,
        Monospacing? monospacing = null) => new()
    {
        Monospacing = monospacing,
        Size = size,
        Gamma = gamma,
        MergeMode = mergeMode,
        Renderer = Renderer.DirectWrite,
        Lookup = lookup with { AllowSynthesis = true },
        Ranges = ranges,
        HorizontalOffset = horizontalOffset,
        BaselineShift = baselineShift,
        LetterSpacing = letterSpacing,
        DirectWrite = new(renderMode, MeasureGdiNatural, GridFitEnabled),
        GlyphMerging = glyphMerging,
    };

    private static IEnumerable<FaceElement> MakeAxisElements(LookupStruct lookup, IFontProbe font, float size)
    {
        lookup = lookup with { Features = new Dictionary<uint, uint>() };

        var glyphs = GlyphMergeShapes.Presets.SelectMany(p => p)
                                     .Where(e => HasText(font, e.Text.TrimStart('_')))
                                     .ToDictionary(e => e.Codepoint, e => (e.Mapping, e.Text));
        if (glyphs.Count == 0)
            yield break;

        yield return Make(
            lookup,
            size,
            RenderNaturalSymmetric,
            DefaultGamma,
            ToRanges(glyphs.Keys),
            MergeMode.Replace,
            glyphMerging: new() { Glyphs = glyphs });
    }

    private static List<RuneRange> ToRanges(IEnumerable<Rune> codepoints)
    {
        var res = new List<RuneRange>();
        foreach (var c in codepoints.Order())
        {
            if (res.Count != 0 && res[^1].Last.Value + 1 == c.Value)
                res[^1] = res[^1] with { Last = c };
            else
                res.Add(new(c, c));
        }

        return res;
    }

    private sealed record ExclamationMark(
        float SizePerDigitSize,
        float OffsetPerSize,
        float BaselineShiftPerSize,
        float GapPerSize,
        float PitchPerInk);

    private sealed record ExclamationMarkPlacement(
        float DigitOffset,
        float Size,
        int RenderMode,
        float Offset,
        float BaselineShift,
        float Spacing);

    private sealed record Traits(
        Rune ReferenceGlyph,
        float ReferenceHeight,
        bool HasLetters,
        bool IsAxis,
        string[] Features,
        ExclamationMark? Mark = null)
    {
        public static Traits Of(string family) => family.ToUpperInvariant() switch
        {
            "JUPITERN" => new(
                new('8'),
                0.76f,
                false,
                false,
                ["onum"],
                new(146f / 96, -8f / 96, 15f / 96, -0.256f, 0.424f)),
            "JUPITER" => new(new('H'), 0.773f, true, false, ["onum", "smcp"]),
            "MEIDINGER" => new(new('8'), 0.71f, false, false, []),
            "MIEDINGERMID" => new(new('H'), 0.705f, true, false, []),
            "TRUMPGOTHIC" => new(new('H'), 0.761f, true, false, []),
            _ => new(new('H'), 0.768f, true, true, []),
        };
    }
}
