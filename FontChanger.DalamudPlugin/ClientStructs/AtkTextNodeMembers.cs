// What the plugin needs of AtkTextNode that FFXIVClientStructs doesn't have, written as it would be there, to be
// contributed (FFXIV/Component/GUI/AtkTextNode.cs).

namespace CustomFonts;

/// <summary>AtkTextNode members FFXIVClientStructs lacks.</summary>
internal static class AtkTextNodeMembers
{
    /// <summary>
    /// <c>[MemberFunction]</c> of <c>void BuildFontCache()</c>: lays the node's text out into an entry of the font
    /// manager's caches (<c>FontCacheHandle</c>), which its draws use instead of laying it out again.
    /// </summary>
    public const string BuildFontCacheSignature = "48 85 C9 0F 84 ?? ?? ?? ?? 56 48 83 EC 20 48 89 5C 24 30 48 8B F1 0F B7 89 68 01 00 00";

    /// <summary><c>[MemberFunction]</c> of <c>void FreeFontCache()</c>: frees the node's font cache entry, if any.</summary>
    public const string FreeFontCacheSignature = "48 85 C9 74 ?? 57 48 83 EC 20 48 8B F9 0F B7 89 68 01 00 00";
}
