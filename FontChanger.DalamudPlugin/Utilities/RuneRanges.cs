using System.Linq;
using System.Text;
using System.Text.Unicode;

using FontChanger.Presets.Records;

namespace FontChanger.DalamudPlugin.Utilities;

public static class RuneRanges
{
    public static readonly RuneRange[] RightToLeft = RuneRangeExtensions.Coalesce(
    [
        UnicodeRanges.Hebrew,
        UnicodeRanges.Arabic,
        UnicodeRanges.Syriac,
        UnicodeRanges.ArabicSupplement,
        UnicodeRanges.Thaana,
        UnicodeRanges.NKo,
        UnicodeRanges.Samaritan,
        UnicodeRanges.Mandaic,
        UnicodeRanges.SyriacSupplement,
        UnicodeRanges.ArabicExtendedB,
        UnicodeRanges.ArabicExtendedA,
        UnicodeRanges.ArabicPresentationFormsA,
        UnicodeRanges.ArabicPresentationFormsB,

        // Hebrew part of Alphabetic Presentation Forms
        .. RuneRange.From(0xFB1D, 0xFB4F),

        // Cypriot Syllabary, Imperial Aramaic, Palmyrene, Nabataean, Hatran, Phoenician, Lydian, Sidetic, Meroitic
        // Hieroglyphs, Meroitic Cursive, Kharoshthi, Old South Arabian, Old North Arabian, Manichaean, Avestan,
        // Inscriptional Parthian, Inscriptional Pahlavi, Psalter Pahlavi, Old Turkic, Old Hungarian, Hanifi Rohingya,
        // Garay, Rumi Numeral Symbols, Yezidi, Arabic Extended-C, Old Sogdian, Sogdian, Old Uyghur, Chorasmian, Elymaic
        .. RuneRange.From(0x10800, 0x10FFF),

        // Mende Kikakui, Adlam, Indic Siyaq Numbers, Ottoman Siyaq Numbers, Arabic Mathematical Alphabetic Symbols
        .. RuneRange.From(0x1E800, 0x1EFFF),
    ]).ToArray();

    /// <summary>Gets whether a rune is in any of the ranges.</summary>
    public static bool Contains(this RuneRange[] ranges, Rune rune)
    {
        foreach (var range in ranges)
        {
            if (rune >= range.First && rune <= range.Last)
                return true;
        }

        return false;
    }
}
