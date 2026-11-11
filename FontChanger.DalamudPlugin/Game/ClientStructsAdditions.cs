using System.Runtime.CompilerServices;
using System.Text;

using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

using FontChanger.DalamudPlugin.Utilities;

namespace FontChanger.DalamudPlugin.Game;

// What the plugin knows of the game's layout that FFXIVClientStructs doesn't have yet, a class per game type. Offsets and
// signatures are as of 7.56h unless a member says otherwise.

/// <summary>Members of AtkFontManager.FontSet.</summary>
internal static unsafe class AtkFontSetAdditions
{
    /// <summary>Bit 25 of the draw flags, which the game neither sets nor reads.</summary>
    public const AtkFontManager.DrawFlags UnusedDrawFlag25 = (AtkFontManager.DrawFlags)(1u << 25);

    /// <summary>Bit 26 of the draw flags, which the game reads nowhere.</summary>
    public const AtkFontManager.DrawFlags UnusedDrawFlag26 = (AtkFontManager.DrawFlags)(1u << 26);

    /// <summary>The float at 0x15C. Where positive, the game picks its font for this times the unscaled size, and scales the advances back.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ref float PickScale(AtkFontManager.FontSet* set) => ref *(float*)((byte*)set + 0x15C);

    /// <summary>The float at 0x124, the font size before the node's scale.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float UnscaledSizeY(AtkFontManager.FontSet* set) => *(float*)((byte*)set + 0x124);
}

/// <summary>Members of AtkFontManager and AtkFontManager.Font.</summary>
internal static unsafe class AtkFontManagerAdditions
{
    /// <summary>The size of <see cref="LineBuffer"/>.</summary>
    public const int LineBufferSize = 0x2000;

    /// <summary>The pointer at 0x48, to the buffer multiline text is drawn from, a line at a time.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte* LineBuffer(AtkFontManager* manager) => *(byte**)((byte*)manager + 0x48);

    /// <summary>The font's glyph of a character, null if it has none or isn't ready. Its glyph map only holds the basic plane.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static AtkFontManager.Glyph* FindGlyph(this ref AtkFontManager.Font font, Rune rune) =>
        !rune.IsBmp
        || font.GlyphMap is null
        || !font.Flags.HasFlag(AtkFontManager.FontFlags.Ready)
        || !font.GlyphMap->TryGetValue(GameUtf8.Pack(rune), out var found) || found.Value is null
            ? null
            : found.Value;
}

/// <summary>Members of AtkModule.</summary>
internal static unsafe class AtkModuleAdditions
{
    private static readonly nint FontManagerOffset = typeof(AtkModule).FieldOffsetOf(nameof(AtkModule.AtkFontManager));

    /// <summary>The module a font manager is embedded in, which AtkStage has no pointer to.</summary>
    public static AtkModule* FromFontManager(AtkFontManager* fontManager) => (AtkModule*)((byte*)fontManager - FontManagerOffset);
}

/// <summary>Values of AtkResNode.</summary>
internal static class AtkResNodeAdditions
{
    /// <summary>The first node type of a component node. Those after are components too.</summary>
    public const ushort FirstComponentNodeType = 1000;
}

/// <summary>Functions of text nodes and their layout, which have no addresses in FFXIVClientStructs.</summary>
internal static unsafe class AtkTextLayoutAdditions
{
    /// <summary>
    /// A call of the function that sets a font set up to measure a text node. Its callers lay the text out with the set it
    /// returns, and restore the set afterwards.
    /// </summary>
    /// <remarks>Calls, which matched every version since 5.55.</remarks>
    public const string PrepareMeasureSignature = "E8 ?? ?? ?? ?? 48 8B D8 F7 80 ?? ?? ?? ?? 00 00 00 10";

    /// <summary>The only call of the text wrapper's layout.</summary>
    public const string TextWrapLayoutSignature = "E8 ?? ?? ?? ?? 48 8B 4B 70 8B F8";

    /// <summary>A call of the text node renderer's function that sets a font set up to draw a node.</summary>
    /// <remarks>Its prologue only matches since 7.30.</remarks>
    public const string PrepareNodeSignature = "E8 ?? ?? ?? ?? 48 83 BF ?? ?? ?? ?? 00 74 ?? 48 8B CE E8";

    public delegate AtkFontManager.FontSet* PrepareMeasureDelegate(AtkTextNode* node, byte considerScale);

    public delegate int TextWrapLayoutDelegate(nint wrapper, byte* text);

    public delegate void PrepareNodeDelegate(nint renderer, nint outArea, AtkFontManager.FontSet* set, AtkTextNode* node);
}

/// <summary>Members of the log viewer embedded in AddonChatLogPanel.</summary>
internal static unsafe class LogViewerAdditions
{
    /// <summary>The byte at 0x98. The viewer's OnRequestedUpdate lays its messages out again only if this is set or they changed.</summary>
    public static ref byte LayoutPending(void* logViewer) => ref *((byte*)logViewer + 0x98);
}

/// <summary>Values and layout of the nameplates' types.</summary>
internal static unsafe class NamePlateAdditions
{
    /// <summary>The object ID of a nameplate info entry without an object, as an NPC's.</summary>
    public const uint NoObjectId = 0xE0000000;

    /// <summary>The bit of NamePlateObjectIntArrayData.UpdateFlags that has the plate updated, as a changed text does.</summary>
    public const int UpdateFlagUpdate = 1;

    /// <summary>The object a bake's data is of, being its first member.</summary>
    public static AddonNamePlate.NamePlateObject* ObjectOf(AddonNamePlate.BakeData* bake) => (AddonNamePlate.NamePlateObject*)bake;
}
