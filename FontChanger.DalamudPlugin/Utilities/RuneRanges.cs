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

        // The Hebrew part of Alphabetic Presentation Forms.
        .. RuneRange.From(0xFB1D, 0xFB4F),

        // The right-to-left SMP blocks, from Cypriot Syllabary to Elymaic.
        .. RuneRange.From(0x10800, 0x10FFF),

        // Mende Kikakui to Arabic Mathematical Alphabetic Symbols, all right-to-left.
        .. RuneRange.From(0x1E800, 0x1EFFF),
    ]).ToArray();
}
