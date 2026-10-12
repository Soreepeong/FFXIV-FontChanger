// What the plugin needs of AddonNamePlate that FFXIVClientStructs doesn't have, written as it would be there, to be
// contributed (FFXIV/Client/UI/AddonNamePlate.cs).

namespace CustomFonts;

/// <summary>AddonNamePlate members FFXIVClientStructs lacks.</summary>
internal static class AddonNamePlateMembers
{
    /// <summary>The number of objects of <c>NamePlateObjectArray</c>.</summary>
    public const int NamePlateObjectCount = 50;
}

/// <summary>AddonNamePlate.BakePlateRenderer members FFXIVClientStructs lacks.</summary>
internal static class BakePlateRendererMembers
{
    /// <summary>
    /// <c>[MemberFunction]</c> of <c>bool AllocateBake(NamePlateObject* obj)</c>: frees the object's region and takes a
    /// new one of ((TextW + 8) * 2) x ((TextH + 4) * 2) in the render texture; false when it doesn't fit.
    /// </summary>
    public const string AllocateBakeSignature = "48 89 5C 24 18 48 89 6C 24 20 57 41 54 41 55 41 56 41 57 48 83 EC 50 4C 8B 81 F8 01 00 00";

    /// <summary>
    /// <c>[MemberFunction]</c> of <c>void DrawBaked(AtkResNode* node, BakeData* bake)</c>: draws a baked region, (Width -
    /// 4) x (Height - 4) node units at the node's transform, centered on the node.
    /// </summary>
    public const string DrawBakedSignature = "48 8B C4 48 89 58 08 48 89 68 10 48 89 70 18 48 89 78 20 41 54 41 56 41 57 48 83 EC 50 F3 0F 10 42 64";

    /// <summary>
    /// <c>[MemberFunction]</c> of <c>void Draw(AtkResNode* node)</c> (vf2): draws a plate's text, baked or live.
    /// </summary>
    public const string DrawSignature =
        "48 89 5C 24 10 57 48 83 EC 20 48 8B F9 48 8B DA 48 8B 89 F8 01 00 00 4C 8B C1 48 8B 41 08 80 78 19 00 75 18 48 39 58 20 " +
        "73 06 48 8B 40 10 EB 06 4C 8B C0 48 8B 00 80 78 19 00 74 E8 41 80 78 19 00 75 06 49 3B 58 20 73 03 4C 8B C1 80 BF 30 02 " +
        "00 00 00 75 7E 4C 3B C1 74 79 49 8B 40 28 80 78 71 00";

    /// <summary>
    /// <c>[MemberFunction]</c> of <c>void PrepareNode(float* rect, GameFontSet* set, AtkResNode* node)</c> (vf3): the text
    /// node renderer's per-node setup, then for a bake (<c>CurrentBakeData</c> set) the bake rect and node scale 1.
    /// </summary>
    public const string PrepareNodeSignature =
        "48 89 5C 24 08 48 89 74 24 10 57 48 83 EC 20 49 8B F0 48 8B DA 48 8B F9 E8 ?? ?? ?? ?? 48 83 BF 28 02 00 00 00 74 ?? " +
        "48 8B CE E8 ?? ?? ?? ?? 81 60 38 FF FF F7 FF C7 40 10 00 00 80 3F";
}
