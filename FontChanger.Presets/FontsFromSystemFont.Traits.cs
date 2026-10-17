using System.Collections.Generic;
using System.Text.Unicode;

using FontChanger.Presets.Enums;
using FontChanger.Presets.Records;

namespace FontChanger.Presets;

public static partial class FontsFromSystemFont
{
    /// <summary>TopPerCapHeight and BottomPerCapHeight are the samples' ink in the game's AXIS over its cap height, averaged
    /// from AXIS_18 and AXIS_36 (KrnAXIS for Hangul) where row coverage crosses one half.</summary>
    private sealed record CjkGroup(FontScripts Scripts, IReadOnlyList<RuneRange> Codepoints, string Samples, float TopPerCapHeight, float BottomPerCapHeight)
    {
        // Han takes what is drawn on the ideographic em box with it, such as radicals, CJK punctuation, bopomofo, and
        // enclosed, compatibility and fullwidth forms.
        public static readonly CjkGroup Han = new(
            FontScripts.Han,
            [
                .. RuneRange.From(
                    UnicodeRanges.CjkRadicalsSupplement,
                    UnicodeRanges.KangxiRadicals,
                    UnicodeRanges.IdeographicDescriptionCharacters,
                    UnicodeRanges.CjkSymbolsandPunctuation,
                    UnicodeRanges.Bopomofo,
                    UnicodeRanges.Kanbun,
                    UnicodeRanges.BopomofoExtended,
                    UnicodeRanges.CjkStrokes,
                    UnicodeRanges.EnclosedCjkLettersandMonths,
                    UnicodeRanges.CjkCompatibility,
                    UnicodeRanges.CjkUnifiedIdeographsExtensionA,
                    UnicodeRanges.CjkUnifiedIdeographs,
                    UnicodeRanges.CjkCompatibilityIdeographs,
                    UnicodeRanges.CjkCompatibilityForms),

                // Halfwidth and Fullwidth Forms: fullwidth ASCII, and the white parentheses.
                .. RuneRange.From(0xFF01, 0xFF60),

                // Halfwidth and Fullwidth Forms: fullwidth signs (cent, pound, not, macron, broken bar, yen, won).
                .. RuneRange.From(0xFFE0, 0xFFE6),

                // The Supplementary and Tertiary Ideographic Planes.
                .. RuneRange.From(0x20000, 0x3FFFF),
            ],
            "口日田中国東",
            1.036f,
            -0.075f);

        public static readonly CjkGroup Kana = new(
            FontScripts.Kana,
            [
                .. RuneRange.From(UnicodeRanges.Hiragana, UnicodeRanges.Katakana, UnicodeRanges.KatakanaPhoneticExtensions),

                // Halfwidth and Fullwidth Forms: halfwidth katakana, with the middle dot and the prolonged sound mark.
                .. RuneRange.From(0xFF65, 0xFF9F),

                // Kana Extended-B, Kana Supplement, Kana Extended-A, and Small Kana Extension.
                .. RuneRange.From(0x1AFF0, 0x1B16F),
            ],
            "あアのカ",
            1.012f,
            -0.049f);

        public static readonly CjkGroup Hangul = new(
            FontScripts.Hangul,
            [
                .. RuneRange.From(
                    UnicodeRanges.HangulJamo,
                    UnicodeRanges.HangulCompatibilityJamo,
                    UnicodeRanges.HangulJamoExtendedA,
                    UnicodeRanges.HangulSyllables,
                    UnicodeRanges.HangulJamoExtendedB),

                // Halfwidth and Fullwidth Forms: halfwidth Hangul jamo.
                .. RuneRange.From(0xFFA0, 0xFFDC),
            ],
            "가한글국를",
            1.070f,
            -0.109f);

        public static readonly CjkGroup[] All = [Han, Kana, Hangul];
    }

    private sealed record ExclamationMark(
        float SizePerDigitSize,
        float OffsetPerSize,
        float VerticalOffsetPerSize,
        float GapPerSize,
        float PitchPerInk);

    // ReferenceCharacters is 8 for the digit-only families, as JupiterN's old style 8 is as tall as a lining one;
    // ReferenceHeight is over the size; XHeightPerCapHeight is null where the lowercase letters are (small) capitals.
    private sealed record Traits(
        string ReferenceCharacters,
        float ReferenceHeight,
        float? XHeightPerCapHeight,
        bool HasLetters,
        bool IsAxis,
        string[] Features,
        ExclamationMark? Mark = null)
    {
        private const string Capitals = "HEFILTZ";

        public static Traits Of(string family) => family.ToUpperInvariant() switch
        {
            "JUPITERN" => new(
                "8",
                0.76f,
                null,
                false,
                false,
                ["onum"],
                new(146f / 96, -8f / 96, 15f / 96, -0.256f, 0.424f)),
            "JUPITER" => new(Capitals, 0.773f, null, true, false, ["onum", "smcp"]),
            "MEIDINGER" => new("8", 0.71f, null, false, false, []),
            "MIEDINGERMID" => new(Capitals, 0.705f, null, true, false, []),
            "TRUMPGOTHIC" => new(Capitals, 0.761f, 0.74f, true, false, []),
            _ => new(Capitals, 0.768f, 0.75f, true, true, []),
        };
    }
}
