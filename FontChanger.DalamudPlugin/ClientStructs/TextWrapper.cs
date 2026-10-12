// The game's text wrapper, which FFXIVClientStructs doesn't have, written as it would be there, to be contributed.

using System.Runtime.InteropServices;

namespace CustomFonts;

/// <summary>
/// The text wrapper: cuts text into words and puts them on lines (the chat log, word-wrapped text nodes,
/// HandleFormatText); only what is known of it.
/// </summary>
[StructLayout(LayoutKind.Explicit)]
internal struct TextWrapper
{
    /// <summary>
    /// <c>[MemberFunction]</c> of <c>uint SplitWord(int width, int unused, byte* word, byte* measuredWord, bool lineStart)</c>:
    /// gets how many units (characters and macros) of a word that doesn't fit stay on the line.
    /// </summary>
    public const string SplitWordSignature = "48 89 5C 24 20 56 57 41 57 48 83 EC 30 49 8B F1 44 8B C2 44 8B 89 50 01 00 00";

    /// <summary>
    /// <c>[MemberFunction]</c> of <c>int FitUnits(byte* measuredWord, int width, int maxUnits, int* bytes)</c>: gets how
    /// many units of a word fit in a width.
    /// </summary>
    public const string FitUnitsSignature = "48 8B C4 53 41 56 48 83 EC 68 48 89 68 08 33 ED 48 89 70 10";

    /// <summary>The unit limit of a line.</summary>
    [FieldOffset(0x150)] public int MaxUnits;

    [FieldOffset(0x154)] public int UsedUnits;

    /// <summary>The units of the current word already put on earlier lines (0 on its first split).</summary>
    [FieldOffset(0x1578)] public int ConsumedUnits;
}
