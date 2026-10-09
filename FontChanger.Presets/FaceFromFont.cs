using System;
using System.Collections.Generic;
using System.Linq;

namespace FontChanger.Presets;

/// <summary>
/// A glyph's box over the em: its left and right side bearings, its advance, and the top and bottom of its ink above the
/// baseline.
/// </summary>
public readonly record struct GlyphBox(float Left, float Right, float Advance, float Top, float Bottom);

/// <summary>What <see cref="FaceFromFont"/> needs to know of a font, from the API that draws it.</summary>
public interface IFontProbe
{
    /// <summary>Gets a glyph's box, or null if the font lacks the character.</summary>
    GlyphBox? GetGlyph(int codepoint);

    /// <summary>Gets whether the font has bitmaps of a size in pixels per em.</summary>
    bool HasBitmapOf(int ppem);

    /// <summary>Gets whether the font's OpenType layout (GSUB or GPOS) has a feature.</summary>
    bool HasFeature(string tag);
}

/// <summary>
/// Makes faces of the game's fonts drawn with a font of the system, as the font editor's Add from Font does (mirrored by its
/// FaceFromFont.cpp): sized so that
/// its capitals (or digits, for the families of only digits) are as tall as the game's, placed as the game places its
/// glyphs, and drawn with DirectWrite, in natural mode at sizes the font has bitmaps of, and in natural symmetric mode at
/// others. Faces of AXIS also get the game's symbols from the Lodestone font, and drawn in the font by glyph merging, as
/// are the IME indicators, with Windows' fonts for scripts it lacks.
/// </summary>
public static class FaceFromFont
{
    // The game's PUA glyphs, as the Lodestone web font (FFXIV_Lodestone_SSF) draws them.
    private const string LodestoneFontName = "XIV AXIS Std ATK";

    private const int RenderNatural = 4;
    private const int RenderNaturalSymmetric = 5;
    private const int MeasureGdiNatural = 2;
    private const int GridFitEnabled = 2;
    private const int StyleItalic = 2;

    private static readonly (int, int)[] AllCodepoints = [(0x20, 0x10FFFF)];
    private static readonly (int, int)[] LatinCodepoints = [(0x20, 0x2FFF)];
    private static readonly (int, int)[] CjkCodepoints = [(0x3000, 0x10FFFF)];
    private static readonly (int, int)[] HangulCodepoints = [(0x1100, 0x11FF), (0x3130, 0x318F), (0xAC00, 0xD7AF)];
    private static readonly (int, int)[] HanCodepoints = [(0x3400, 0x4DBF), (0x4E00, 0x9FFF)];
    private static readonly (int, int)[] PrivateUseCodepoints = [(0xE000, 0xF8FF)];

    // The fonts of every Windows installation that draw merged glyphs' texts in scripts a font lacks.
    private static readonly string[] MergedTextFallbackFonts = ["Yu Gothic UI", "Malgun Gothic"];

    /// <summary>
    /// Makes a face of a game font (<c>AXIS_12</c>) at its size with a font: the game's own glyphs, then the font's, which
    /// take the place of those the game's font has. Null if the font isn't installed.
    /// </summary>
    /// <param name="faceName">The face's name, as the game's font is named.</param>
    /// <param name="gameSize">The size the game's font is drawn at in the face.</param>
    /// <param name="font">The font: its name, weight, stretch and style.</param>
    /// <param name="openFont">Gets measurements of a font (this one, its italic, or a fallback), or null if it isn't installed.</param>
    /// <param name="monospacedDigits">Whether the digits are made monospaced, as the game's are (see <see cref="MakeElements"/>).</param>
    public static FaceDef? MakeFace(string faceName, float gameSize, LookupDef font, Func<LookupDef, IFontProbe?> openFont, bool monospacedDigits = true)
    {
        if (MakeElements(Preset.FamilyOf(faceName), gameSize, font, openFont, monospacedDigits) is not { } elements)
            return null;
        return new() { Name = faceName, Elements = [new() { Size = gameSize, Renderer = ElementRenderer.Game, Ranges = [(0, 0x10FFFF)] }, .. elements] };
    }

    /// <summary>
    /// Makes the elements that draw a face of a game font family at a size with a font, to follow the game's own element;
    /// null if the font isn't installed. With <paramref name="monospacedDigits"/>, the digits are the font's tabular figures
    /// (tnum), or, if it has none, put each in a cell as wide as its 0.
    /// </summary>
    public static IReadOnlyList<ElementDef>? MakeElements(
        string family, float size, LookupDef chosen, Func<LookupDef, IFontProbe?> openFont, bool monospacedDigits = true)
    {
        if (openFont(chosen) is not { } font)
            return null;

        var traits = Traits.Of(family);
        var features = new Dictionary<uint, uint>(chosen.Features);
        if (monospacedDigits)
            features[Preset.Tag("tnum")] = 1;
        foreach (var feature in traits.Features)
            features[Preset.Tag(feature)] = 1;
        var lookup = chosen with { Features = features };

        var inkHeight = GetInkHeight(font, traits.ReferenceGlyph);
        if (inkHeight <= 0)
            inkHeight = traits.ReferenceHeight;
        var (fontSize, renderMode) = GetSizeAndMode(font, size * traits.ReferenceHeight / inkHeight);

        // Pixels that the game's AXIS draws glyphs to the right of (and CJK ones down from) where AXIS Basic ProN would be,
        // assumed to grow alike with the size for the other families; and the Lodestone font's PUA glyphs.
        var large = size >= 36;
        var latinOffset = traits.IsAxis ? large ? 3 : 1 : MathF.Max(1, Round(size / 12));
        var (cjkOffset, cjkShift) = traits.IsAxis && large ? (1f, -1f) : (0f, 0f);
        var (puaOffset, puaShift) = large ? (3f, -1f) : (1f, 0f);

        // JupiterN's exclamation mark of critical hits: italic, larger than the digits, down past the baseline, leaning
        // over the digit before it, and the digit after it close. The digits move right toward it.
        ElementDef? markElement = null;
        if (traits.Mark is { } mark)
        {
            var italic = chosen with { Style = StyleItalic };
            var placed = PlaceExclamationMark(mark, font, openFont(italic) ?? font, fontSize, size, latinOffset);
            latinOffset = placed.DigitOffset;
            markElement = Make(italic, placed.Size, placed.RenderMode, 1.2f, [('!', '!')], MergeMode.Replace, placed.Offset, placed.BaselineShift, placed.Spacing);
        }

        var hasKana = HasText(font, "あア");
        var hasHangul = HasText(font, "가");
        var hasHan = HasText(font, "中");

        // Fonts made for Chinese, which have simplified characters, add the Han characters that the game's fonts lack.
        var isChinese = HasText(font, "们");

        var res = new List<ElementDef>();
        if (traits.HasText && hasHangul)
            res.Add(Make(lookup, fontSize, renderMode, 1.2f, HangulCodepoints, MergeMode.AddAll, cjkOffset, cjkShift));
        if (traits.HasText && isChinese)
            res.Add(Make(lookup, fontSize, renderMode, 1.2f, HanCodepoints, MergeMode.AddAll, cjkOffset, cjkShift));

        // The glyphs that the game's font has; in AXIS, CJK characters are placed apart from Latin ones, as the game does.
        if (traits.IsAxis && (hasKana || hasHangul || hasHan))
        {
            res.Add(Make(lookup, fontSize, renderMode, 1.2f, LatinCodepoints, MergeMode.Replace, latinOffset));
            res.Add(Make(lookup, fontSize, renderMode, 1.2f, CjkCodepoints, MergeMode.Replace, cjkOffset, cjkShift));
        }
        else
        {
            res.Add(Make(lookup, fontSize, renderMode, 1.2f, AllCodepoints, MergeMode.Replace, latinOffset));
        }

        // Digits of a font without tabular figures: each in a cell as wide as its 0, centered.
        if (monospacedDigits && !HasTabularDigits(font))
        {
            res.Add(Make(
                lookup,
                fontSize,
                renderMode,
                1.2f,
                [('0', '9')],
                MergeMode.Replace,
                latinOffset,
                monospacing: new(1, 1, MonospacingUnit.ReferenceGlyph, '0', MonospacingAlignment.CenterAdvance)));
        }

        if (markElement is not null)
            res.Add(markElement);

        if (traits.IsAxis)
        {
            // The game's PUA glyphs as vectors, so that they scale alike at sizes that the game has no font of; after the
            // font's elements, which decide how the face is measured.
            res.Add(Make(LookupDef.Of(LodestoneFontName), size, RenderNaturalSymmetric, 1.4f, PrivateUseCodepoints, MergeMode.Replace, puaOffset, puaShift));
            res.AddRange(MakeMergedGlyphElements(chosen, font, size, openFont));
        }

        return res;
    }

    /// <summary>
    /// Places JupiterN's exclamation mark of critical hits by the font's glyphs: the gap between the ink of the digit before
    /// it (the one reaching furthest right) and its own, and the distance between the marks of "!!" over the ink's width, as
    /// in proportion they are with Source Han Sans K, whose placement was tuned in the game. A gap too wide moves the digits
    /// right (within a third of an em) rather than the mark left, as glyphs may not start left of their pens.
    /// </summary>
    private static ExclamationMarkPlacement PlaceExclamationMark(
        ExclamationMark mark, IFontProbe digits, IFontProbe italic, float fontSize, float size, float latinOffset)
    {
        var (markSize, renderMode) = GetSizeAndMode(italic, fontSize * mark.SizePerDigitSize);
        var markOffset = Round(mark.OffsetPerSize * size);

        var digitRight = Enumerable.Range('0', 10)
                                   .Select(c => digits.GetGlyph(c))
                                   .OfType<GlyphBox>()
                                   .Select(g => g.Right)
                                   .DefaultIfEmpty(0)
                                   .Min() * fontSize;
        var glyph = italic.GetGlyph('!') ?? default;

        // Of the gap with the digits where Latin glyphs are, too much moves the digits right, and too little the mark.
        var excess = markOffset + (glyph.Left * markSize) + digitRight - latinOffset - (mark.GapPerSize * size);
        var digitOffset = latinOffset + Round(Math.Clamp(excess, 0, size / 3));
        if (excess < 0)
            markOffset -= Round(excess);

        // The advance between marks.
        var inkWidth = (glyph.Advance - glyph.Left - glyph.Right) * markSize;
        var spacing = Round((mark.PitchPerInk * inkWidth) - (glyph.Advance * markSize));
        return new(digitOffset, markSize, renderMode, markOffset, Round(mark.BaselineShiftPerSize * size), spacing);
    }

    // Rounds half away from zero, as C++ does.
    private static float Round(float value) => MathF.Round(value, MidpointRounding.AwayFromZero);

    // Gets whether a font has the characters of a text, other than line feeds and the bar of IME indicators ("_").
    private static bool HasText(IFontProbe font, string text)
    {
        if (text.StartsWith('_'))
            text = text[1..];
        return text.EnumerateRunes().All(r => r.Value == '\n' || font.GetGlyph(r.Value) is not null);
    }

    // The height of a glyph's ink over the em, from the baseline (or its bottom, if above it); 0 if the font lacks it.
    private static float GetInkHeight(IFontProbe font, int codepoint) =>
        font.GetGlyph(codepoint) is { } g ? g.Top - MathF.Max(0, g.Bottom) : 0;

    // Whether a font has tabular figures (tnum), or digits that are all as wide by default (as most fonts' are).
    private static bool HasTabularDigits(IFontProbe font)
    {
        if (font.HasFeature("tnum"))
            return true;
        var advances = Enumerable.Range('0', 10).Select(c => font.GetGlyph(c)?.Advance).ToList();
        return advances.All(a => a is not null && a == advances[0]);
    }

    // The size to draw a font at for its glyph to be as tall as wanted, and how to draw it there: natural at sizes of the
    // font's bitmaps (rounded to them), natural symmetric otherwise.
    private static (float Size, int RenderMode) GetSizeAndMode(IFontProbe font, float exactSize)
    {
        var rounded = Round(exactSize);
        return rounded > 0 && font.HasBitmapOf((int)rounded)
                   ? (rounded, RenderNatural)
                   : (Round(exactSize * 10) / 10, RenderNaturalSymmetric);
    }

    private static ElementDef Make(
        LookupDef lookup,
        float size,
        int renderMode,
        float gamma,
        IReadOnlyList<(int, int)> ranges,
        MergeMode mergeMode,
        float horizontalOffset = 0,
        float baselineShift = 0,
        float letterSpacing = 0,
        GlyphMergingDef? glyphMerging = null,
        MonospacingDef? monospacing = null) => new()
    {
        Monospacing = monospacing,
        Size = size,
        Gamma = gamma,
        MergeMode = mergeMode,
        Renderer = ElementRenderer.DirectWrite,
        Lookup = lookup with { AllowSynthesis = true },
        Ranges = ranges,
        HorizontalOffset = horizontalOffset,
        BaselineShift = baselineShift,
        LetterSpacing = letterSpacing,
        DirectWrite = new(renderMode, MeasureGdiNatural, GridFitEnabled),
        GlyphMerging = glyphMerging,
    };

    // The glyph merging elements of a face of AXIS: the game's symbols and the IME indicators drawn in the font, and those
    // whose texts it lacks in the first of Windows' fonts that has them. Those whose texts no font has are left out, for
    // the glyphs of the game or of the Lodestone font to stay.
    private static IEnumerable<ElementDef> MakeMergedGlyphElements(LookupDef lookup, IFontProbe font, float size, Func<LookupDef, IFontProbe?> openFont)
    {
        var fonts = new List<(LookupDef Lookup, IFontProbe Font)> { (lookup with { Features = new Dictionary<uint, uint>() }, font) };
        foreach (var name in MergedTextFallbackFonts)
        {
            if (openFont(LookupDef.Of(name)) is { } fallback)
                fonts.Add((LookupDef.Of(name), fallback));
        }

        var glyphsByFont = fonts.Select(_ => new Dictionary<int, (MergeMapping, string)>()).ToList();
        foreach (var (codepoint, mapping, text) in GlyphMergeShapes.Presets.SelectMany(p => p))
        {
            var index = fonts.FindIndex(f => HasText(f.Font, text));
            if (index >= 0)
                glyphsByFont[index][codepoint] = (mapping, text);
        }

        for (var i = 0; i < fonts.Count; i++)
        {
            var glyphs = glyphsByFont[i];
            if (glyphs.Count == 0)
                continue;

            // Replacing the glyphs that the game's font has, at the size of the game's own, as the shapes are made for.
            yield return Make(fonts[i].Lookup, size, RenderNaturalSymmetric, 1.4f, ToRanges(glyphs.Keys), MergeMode.Replace, glyphMerging: new() { Glyphs = glyphs });
        }
    }

    private static List<(int, int)> ToRanges(IEnumerable<int> codepoints)
    {
        var res = new List<(int First, int Last)>();
        foreach (var c in codepoints.Order())
        {
            if (res.Count != 0 && res[^1].Last + 1 == c)
                res[^1] = (res[^1].First, c);
            else
                res.Add((c, c));
        }

        return res;
    }

    /// <summary>
    /// JupiterN's exclamation mark, over the size: its size over the digits', its offset and baseline shift, and, as tuned
    /// in the game with Source Han Sans K, the gap between the digits' ink and its own, and the distance between the marks
    /// of "!!" over the width of their ink.
    /// </summary>
    private sealed record ExclamationMark(float SizePerDigitSize, float OffsetPerSize, float BaselineShiftPerSize, float GapPerSize, float PitchPerInk);

    private sealed record ExclamationMarkPlacement(float DigitOffset, float Size, int RenderMode, float Offset, float BaselineShift, float Spacing);

    private sealed record Traits(int ReferenceGlyph, float ReferenceHeight, bool HasText, bool IsAxis, string[] Features, ExclamationMark? Mark = null)
    {
        // The glyph whose height is matched: the capital H, or 8 for the families of only digits (JupiterN's are old style,
        // where 8 is as tall as in lining figures). Its height over the size of the game's font is measured from the game's
        // glyphs; that of AXIS is of AXIS Basic ProN, which draws AXIS as the game does.
        public static Traits Of(string family) => family.ToUpperInvariant() switch
        {
            "JUPITERN" => new('8', 0.76f, false, false, ["onum"], new(146f / 96, -8f / 96, 15f / 96, -0.256f, 0.424f)),
            "JUPITER" => new('H', 0.773f, true, false, ["onum", "smcp"]),
            "MEIDINGER" => new('8', 0.71f, false, false, []),
            "MIEDINGERMID" => new('H', 0.705f, true, false, []),
            "TRUMPGOTHIC" => new('H', 0.761f, true, false, []),
            _ => new('H', 0.768f, true, true, []),
        };
    }
}
