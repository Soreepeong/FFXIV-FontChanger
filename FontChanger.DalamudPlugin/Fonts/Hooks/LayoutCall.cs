using FFXIVClientStructs.FFXIV.Component.GUI;

namespace FontChanger.DalamudPlugin.Fonts.Hooks;

/// <summary>The character LayOutCharacter is laying out, for GetGlyph, which isn't told which character of the text it is.</summary>
/// <remarks>Static rather than per thread, as the game lays text out and draws only on the main thread.</remarks>
internal unsafe struct LayoutCall
{
    public static LayoutCall Current;

    /// <summary>While the text wrapper lays text out.</summary>
    public static bool Wrapping;

    public byte* Character;

    // Laid out by an analyzer that only measures, so its glyph needs no pixels yet.
    public bool MeasuringOnly;

    public AtkFontAnalyzerState* State;

    // The italic bit was cleared for a real italic glyph, to be set again once the character is laid out.
    public bool ItalicCleared;
}
