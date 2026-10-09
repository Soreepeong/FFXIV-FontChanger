using System;
using System.Collections.Generic;
using System.Linq;

namespace FontChanger.Presets;

/// <summary>What <see cref="FaceFromFont"/> needs to know of a font, from the API that draws it.</summary>
public interface IFontProbe
{
    /// <summary>Gets whether the font has the characters of a text, other than line feeds and the bar of IME indicators.</summary>
    bool Has(string text);

    /// <summary>Gets the height of a glyph's ink over the em, from the baseline (or its bottom, if above it); 0 if none.</summary>
    float GetInkHeight(int codepoint);

    /// <summary>Gets whether the font has bitmaps of a size in pixels per em.</summary>
    bool HasBitmapOf(int ppem);

    /// <summary>Gets whether the font has tabular figures (the tnum feature), or digits that are all as wide by default.</summary>
    bool HasTabularDigits();

    /// <summary>Gets a glyph's left and right side bearings and its advance over the em; zeros if the font lacks it.</summary>
    (float Left, float Right, float Advance) GetHorizontalMetrics(int codepoint);
}

/// <summary>
/// Makes faces of the game's fonts drawn with a font of the system, as the font editor's Add from Font does: sized so that
/// its capitals (or digits, for the families of only digits) are as tall as the game's, placed as the game places its
/// glyphs, and drawn with DirectWrite, in natural mode at sizes the font has bitmaps of, and in natural symmetric mode at
/// others. Faces of AXIS also get the game's symbols from the Lodestone font if installed, and drawn in the font by glyph
/// merging, as are the IME indicators, with Windows' fonts for scripts it lacks.
/// </summary>
public static class FaceFromFont
{
    // The game's PUA glyphs, as the Lodestone web font (FFXIV_Lodestone_SSF) draws them.
    private const string LodestoneFontName = "XIV AXIS Std ATK";

    private const int RenderNatural = 4;
    private const int RenderNaturalSymmetric = 5;
    private const int MeasureGdiNatural = 2;
    private const int GridFitEnabled = 2;

    // JupiterN's exclamation mark with Source Han Sans K, as tuned in the game: the gap between the digits' ink and its
    // own over the size, and the distance between the marks of "!!" over the width of their ink.
    private const float ExclamationGapPerSize = -0.256f;
    private const float ExclamationPitchPerInk = 0.424f;

    private static readonly (int, int)[] AllCodepoints = [(0x20, 0x10FFFF)];
    private static readonly (int, int)[] LatinCodepoints = [(0x20, 0x2FFF)];
    private static readonly (int, int)[] CjkCodepoints = [(0x3000, 0x10FFFF)];
    private static readonly (int, int)[] HangulCodepoints = [(0x1100, 0x11FF), (0x3130, 0x318F), (0xAC00, 0xD7AF)];
    private static readonly (int, int)[] HanCodepoints = [(0x3400, 0x4DBF), (0x4E00, 0x9FFF)];
    private static readonly (int, int)[] PrivateUseCodepoints = [(0xE000, 0xF8FF)];

    /// <summary>Gets the fonts of every Windows installation that draw merged glyphs' texts in scripts a font lacks.</summary>
    public static IReadOnlyList<string> MergedTextFallbackFonts { get; } = ["Yu Gothic UI", "Malgun Gothic"];

    /// <summary>
    /// Makes a face of a game font (<c>AXIS_12</c>) at its size with a font: the game's own glyphs, then the font's, which
    /// take the place of those the game's font has.
    /// </summary>
    /// <param name="faceName">The face's name, as the game's font is named.</param>
    /// <param name="gameSize">The size the game's font is drawn at in the face.</param>
    /// <param name="font">The font: its name, weight, stretch and style.</param>
    /// <param name="probe">Measurements of the font.</param>
    /// <param name="openFont">Gets measurements of another font (its italic, or one of <see cref="MergedTextFallbackFonts"/>), or null if it isn't installed.</param>
    /// <param name="monospacedDigits">Whether the digits are made monospaced, as the game's are (see <see cref="MakeElements"/>).</param>
    public static FaceDef MakeFace(
        string faceName, float gameSize, LookupDef font, IFontProbe probe, Func<LookupDef, IFontProbe?> openFont, bool monospacedDigits = true)
    {
        var elements = new List<ElementDef> { new() { Size = gameSize, Renderer = ElementRenderer.Game, Ranges = [(0, 0x10FFFF)] } };
        elements.AddRange(MakeElements(Preset.FamilyOf(faceName), gameSize, font, probe, openFont, monospacedDigits));
        return new() { Name = faceName, Elements = elements };
    }

    /// <summary>
    /// Makes the elements that draw a face of a game font family at a size with a font, to follow the game's own element.
    /// With <paramref name="monospacedDigits"/>, the digits are the font's tabular figures (tnum), or, if it has none, put
    /// each in a cell as wide as its 0.
    /// </summary>
    public static IReadOnlyList<ElementDef> MakeElements(
        string family, float size, LookupDef chosen, IFontProbe probe, Func<LookupDef, IFontProbe?> openFont, bool monospacedDigits = true)
    {
        var traits = Traits.Of(family, size);
        var features = new Dictionary<uint, uint>(chosen.Features);
        if (monospacedDigits)
            features[Tag("tnum")] = 1;
        foreach (var feature in traits.Features)
            features[Tag(feature)] = 1;
        var lookup = chosen with { Features = features, AllowSynthesis = true };

        var inkHeight = probe.GetInkHeight(traits.ReferenceGlyph);
        if (inkHeight <= 0)
            inkHeight = traits.ReferenceHeight;
        var (fontSize, renderMode) = GetSizeAndMode(probe, size * traits.ReferenceHeight / inkHeight);
        var (cjkOffset, cjkShift) = traits.IsAxis && IsSize(size, 36) ? (1f, -1f) : (0f, 0f);
        var latinOffset = GetLatinOffset(traits, size);

        // JupiterN's digits are moved right, toward the exclamation mark after them.
        var mark = traits.IsJupiterN ? PlaceExclamationMark(probe, openFont(chosen with { Style = 2, AllowSynthesis = true }), fontSize, size, latinOffset) : default;
        if (traits.IsJupiterN)
            latinOffset = mark.DigitOffset;

        var hasKana = probe.Has("あア");
        var hasHangul = probe.Has("가");
        var hasHan = probe.Has("中");

        // Fonts made for Chinese, which have simplified characters, add the Han characters that the game's fonts lack.
        var isChinese = probe.Has("们");

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
        if (monospacedDigits && !probe.HasTabularDigits())
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

        // JupiterN's exclamation mark of critical hits: italic, half again as large as the digits, down past the baseline,
        // leaning over the digit before it, and the digit after it close.
        if (traits.IsJupiterN)
        {
            res.Add(Make(
                chosen with { Style = 2, AllowSynthesis = true },
                mark.Size,
                mark.RenderMode,
                1.2f,
                [('!', '!')],
                MergeMode.Replace,
                mark.Offset,
                MathF.Round(15f / 96 * size),
                mark.Spacing));
        }

        if (traits.IsAxis)
        {
            // The game's PUA glyphs as vectors, so that they scale alike at sizes that the game has no font of; after the
            // font's elements, which decide how the face is measured.
            res.Add(Make(LookupDef.Of(LodestoneFontName) with { AllowSynthesis = true }, size, RenderNaturalSymmetric, 1.4f, PrivateUseCodepoints, MergeMode.Replace, IsSize(size, 36) ? 3 : 1, cjkShift));
            res.AddRange(MakeMergedGlyphElements(chosen, probe, size, openFont));
        }

        return res;
    }

    /// <summary>
    /// Places JupiterN's exclamation mark of critical hits by the font's glyphs: the gap between the ink of the digit before
    /// it (the one reaching furthest right) and its own, and the distance between the marks of "!!" over the ink's width, as
    /// in proportion they are with Source Han Sans K, whose placement was tuned in the game. A gap too wide moves the digits
    /// right (within a third of an em) rather than the mark left, as glyphs may not start left of their pens.
    /// </summary>
    private static (float DigitOffset, float Size, int RenderMode, float Offset, float Spacing) PlaceExclamationMark(
        IFontProbe digits, IFontProbe? italic, float fontSize, float size, float latinOffset)
    {
        var mark = italic ?? digits;
        var (markSize, renderMode) = GetSizeAndMode(mark, fontSize * 146 / 96);
        var markOffset = MathF.Round(-8f / 96 * size);

        var digitRight = Enumerable.Range('0', 10)
                                   .Select(digits.GetHorizontalMetrics)
                                   .Where(m => m.Advance > 0)
                                   .Select(m => m.Right)
                                   .DefaultIfEmpty(0)
                                   .Min() * fontSize;
        var (markLeft, markRight, markAdvance) = mark.GetHorizontalMetrics('!');

        // Of the gap with the digits where Latin glyphs are, too much moves the digits right, and too little the mark.
        var excess = markOffset + (markLeft * markSize) + digitRight - latinOffset - (ExclamationGapPerSize * size);
        var digitOffset = latinOffset + MathF.Round(Math.Clamp(excess, 0, size / 3));
        if (excess < 0)
            markOffset -= MathF.Round(excess);

        // The advance between marks.
        var inkWidth = (markAdvance - markLeft - markRight) * markSize;
        var spacing = MathF.Round((ExclamationPitchPerInk * inkWidth) - (markAdvance * markSize));
        return (digitOffset, markSize, renderMode, markOffset, spacing);
    }

    /// <summary>Gets an OpenType tag as DirectWrite has it (DWRITE_MAKE_OPENTYPE_TAG): its first character in the lowest byte.</summary>
    public static uint Tag(string tag) => (uint)(tag[0] | (tag[1] << 8) | (tag[2] << 16) | (tag[3] << 24));

    private static bool IsSize(float size, float other) => MathF.Abs(size - other) < 0.05f;

    // Pixels that the game's AXIS draws Latin glyphs to the right of where AXIS Basic ProN would be at its sizes, assumed
    // to grow alike with the size for the others.
    private static float GetLatinOffset(Traits traits, float size)
    {
        if (traits.IsAxis)
        {
            foreach (var (axisSize, offset) in new[] { (9.6f, 1f), (12f, 1f), (14f, 1f), (18f, 1f), (36f, 3f) })
            {
                if (IsSize(size, axisSize))
                    return offset;
            }
        }

        return MathF.Max(1, MathF.Round(size / 12));
    }

    // The size to draw a font at for its glyph to be as tall as wanted, and how to draw it there: natural at sizes of the
    // font's bitmaps (rounded to them), natural symmetric otherwise.
    private static (float Size, int RenderMode) GetSizeAndMode(IFontProbe probe, float exactSize)
    {
        var rounded = MathF.Round(exactSize);
        return rounded > 0 && probe.HasBitmapOf((int)rounded)
                   ? (rounded, RenderNatural)
                   : (MathF.Round(exactSize * 10) / 10, RenderNaturalSymmetric);
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
        Lookup = lookup,
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
    private static IEnumerable<ElementDef> MakeMergedGlyphElements(LookupDef lookup, IFontProbe probe, float size, Func<LookupDef, IFontProbe?> openFont)
    {
        var fonts = new List<(LookupDef Lookup, IFontProbe Probe)> { (lookup with { Features = new Dictionary<uint, uint>(), AllowSynthesis = true }, probe) };
        foreach (var name in MergedTextFallbackFonts)
        {
            if (openFont(LookupDef.Of(name)) is { } fallback)
                fonts.Add((LookupDef.Of(name) with { AllowSynthesis = true }, fallback));
        }

        var glyphsByFont = fonts.Select(_ => new Dictionary<int, (MergeMapping, string)>()).ToList();
        foreach (var (codepoint, mapping, text) in MergedGlyphs.All)
        {
            var index = fonts.FindIndex(f => f.Probe.Has(text));
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

        return res.Select(r => (r.First, r.Last)).ToList();
    }

    private sealed record Traits(int ReferenceGlyph, float ReferenceHeight, bool HasText, bool IsAxis, bool IsJupiterN, string[] Features)
    {
        // The glyph whose height is matched: the capital H, or 8 for the families of only digits (JupiterN's are old style,
        // where 8 is as tall as in lining figures). Its height over the size of the game's font is measured from the game's
        // glyphs; that of AXIS is of AXIS Basic ProN, which draws AXIS as the game does. Jupiter_45 and Jupiter_90 are of
        // JupiterN, which has only digits.
        public static Traits Of(string family, float size)
        {
            if (family.Equals("JupiterN", StringComparison.OrdinalIgnoreCase) ||
                (family.Equals("Jupiter", StringComparison.OrdinalIgnoreCase) && (IsSize(size, 45) || IsSize(size, 90))))
            {
                return new('8', 0.76f, false, false, true, ["onum"]);
            }

            return family.ToUpperInvariant() switch
            {
                "JUPITER" => new('H', 0.773f, true, false, false, ["onum", "smcp"]),
                "MEIDINGER" => new('8', 0.71f, false, false, false, []),
                "MIEDINGERMID" => new('H', 0.705f, true, false, false, []),
                "TRUMPGOTHIC" => new('H', 0.761f, true, false, false, []),
                _ => new('H', 0.768f, true, true, false, []),
            };
        }
    }

    /// <summary>The game's PUA glyphs drawn by glyph merging, as the font editor's presets of it (and the IME indicators as the game's).</summary>
    private static class MergedGlyphs
    {
        public static readonly IReadOnlyList<(int Codepoint, MergeMapping Mapping, string Text)> All = Make();

        private static List<(int, MergeMapping, string)> Make()
        {
            var res = new List<(int, MergeMapping, string)>();
            void Add(MergeShape shape, int first, IEnumerable<string> texts)
            {
                var mapping = new MergeMapping(shape, MergeTextMode.Subtract, string.Empty, string.Empty, 1000, null);
                foreach (var text in texts)
                    res.Add((first++, mapping, text));
            }

            static IEnumerable<string> Numbers(int from, int to) => Enumerable.Range(from, to - from + 1).Select(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture));

            Add(MergeShape.AmPm, 0xE06D, ["A\nM", "P\nM"]);
            Add(MergeShape.None, 0xE060, [.. Numbers(0, 9), "Lv", "ST", "Nv"]);
            Add(MergeShape.None, 0xE028, ["m", "分"]);
            Add(MergeShape.Box, 0xE070, ["?", .. Enumerable.Range('A', 26).Select(c => ((char)c).ToString())]);
            Add(MergeShape.Box, 0xE0AF, ["+", "E"]);
            Add(MergeShape.NumberBox, 0xE08F, Numbers(0, 31));
            Add(MergeShape.HollowBox, 0xE0E0, Numbers(0, 9));
            Add(MergeShape.Hexagon, 0xE0B1, Numbers(1, 9));
            Add(MergeShape.Rhombus, 0xE0BE, ["↓", "×"]);
            Add(MergeShape.Bozja, 0xE0C1, ["I", "II", "III", "IV", "V", "VI"]);
            Add(MergeShape.Time, 0xE0D0, ["LT", "ST", "ET", "OZ", "SZ", "EZ", "HL", "HS", "HE"]);
            Add(MergeShape.Time, 0xE0D9, ["本", "服", "艾"]);

            // The full-width IME indicators in a box, the half-width ones with a bar cut out at the lower left ("_").
            Add(MergeShape.Ime, 0xE020, ["あ", "ア", "A", "_ｱ", "_A", "가", "中", "英"]);

            // The star is not a text in a box; it splits the box of the Bozja glyphs in two, with its points through the edges.
            res.Add((0xE0C0, new MergeMapping(
                MergeShape.Custom,
                MergeTextMode.Subtract,
                "M335,50 Q240,52 176,116 Q112,180 110,275 L110,725 Q112,820 176,883 Q240,946 335,948 " +
                "L689,948 L647,665 L313,500 L647,335 L689,50 Z " +
                "M784,50 L962,233 L1330,169 L1156,500 L1330,831 L962,767 L784,948 L1377,948 " +
                "Q1472,946 1536,883 Q1600,820 1602,725 L1602,275 Q1600,180 1536,116 Q1472,52 1377,50 Z",
                string.Empty,
                1750,
                null), " "));
            return res;
        }
    }
}
