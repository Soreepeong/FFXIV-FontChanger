// What the plugin needs of the font analyzers that FFXIVClientStructs doesn't have, written as it would be there, to be
// contributed (FFXIV/Component/GUI/AtkFontAnalyzer*.cs).

using System.Runtime.InteropServices;

namespace CustomFonts;

/// <summary>AtkFontAnalyzerBase, AtkFontAnalyzerRenderer and AtkFontAnalyzerRenderCount members FFXIVClientStructs lacks.</summary>
internal static class AtkFontAnalyzerMembers
{
    /// <summary>
    /// <c>[MemberFunction]</c> of AtkFontAnalyzerBase's <c>byte* LayOutCharacter(byte** text, AtkFontAnalyzerState* state)</c>:
    /// lays out the character (or macro) at <c>*text</c> and steps past it.
    /// </summary>
    public const string LayOutCharacterSignature = "40 53 55 56 57 41 54 41 55 41 56 48 83 EC 70 4C 8B 0A 49 8B F8 48 8B 69 40";

    /// <summary>
    /// <c>[VirtualTable]</c> of AtkFontAnalyzerRenderer (at <see cref="RendererVirtualTableOffset"/>) and of
    /// AtkFontAnalyzerRenderCount (at <see cref="RenderCountVirtualTableOffset"/>), set in AtkFontAnalyzer's constructor.
    /// </summary>
    public const string VirtualTablesSignature = "48 8D 05 ?? ?? ?? ?? 48 89 83 18 01 00 00 48 8D 05 ?? ?? ?? ?? 48 89 BB 80 02 00 00";

    public const int RendererVirtualTableOffset = 3;
    public const int RenderCountVirtualTableOffset = 17;
}

/// <summary>The layout state the font analyzers pass to LayOutCharacter; only what is known of it.</summary>
[StructLayout(LayoutKind.Explicit)]
internal struct AtkFontAnalyzerState
{
    /// <summary>The italic bit of <see cref="Flags"/>, a copy of <see cref="GameFontSet.DrawFlags"/>'.</summary>
    public const uint FlagsItalic = 0x08000000;

    [FieldOffset(0x38)] public uint Flags;
}
