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

    extension(ref AtkFontManager.FontSet set)
    {
        /// <summary>Gets the float at 0x15C. Where positive, the game picks its font for this times the unscaled size, and scales the advances back.</summary>
        public ref float PickScale
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => ref *(float*)((byte*)Unsafe.AsPointer(ref set) + 0x15C);
        }

        /// <summary>Gets the float at 0x124, the font size before the node's scale.</summary>
        public float UnscaledSizeY
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => *(float*)((byte*)Unsafe.AsPointer(ref set) + 0x124);
        }
    }
}

/// <summary>Members of AtkFontManager and AtkFontManager.Font.</summary>
internal static unsafe class AtkFontManagerAdditions
{
    /// <summary>The size of the line buffer.</summary>
    public const int LineBufferSize = 0x2000;

    private static readonly nint ModuleFontManagerOffset = typeof(AtkModule).FieldOffsetOf(nameof(AtkModule.AtkFontManager));

    extension(ref AtkFontManager manager)
    {
        /// <summary>Gets the pointer at 0x48, to the buffer multiline text is drawn from, a line at a time.</summary>
        public byte* LineBuffer
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => *(byte**)((byte*)Unsafe.AsPointer(ref manager) + 0x48);
        }

        /// <summary>Gets the module the font manager is embedded in, which AtkStage has no pointer to.</summary>
        public AtkModule* Module => (AtkModule*)((byte*)Unsafe.AsPointer(ref manager) - ModuleFontManagerOffset);
    }

    extension(ref AtkFontManager.Font font)
    {
        /// <summary>The font's glyph of a character, null if it has none or isn't ready. Its glyph map only holds the basic plane.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public AtkFontManager.Glyph* FindGlyph(Rune rune) =>
            !rune.IsBmp
            || font.GlyphMap is null
            || !font.Flags.HasFlag(AtkFontManager.FontFlags.Ready)
            || !font.GlyphMap->TryGetValue(GameUtf8.Pack(rune), out var found) || found.Value is null
                ? null
                : found.Value;
    }
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

/// <summary>Members of LogViewer, which AddonChatLogPanel embeds.</summary>
internal static unsafe class LogViewerAdditions
{
    extension(ref LogViewer viewer)
    {
        /// <summary>Gets the byte at 0x98. The viewer's OnRequestedUpdate lays its messages out again only if this is set or they changed.</summary>
        public ref byte LayoutPending => ref *((byte*)Unsafe.AsPointer(ref viewer) + 0x98);
    }
}

/// <summary>Values and layout of the nameplates' types.</summary>
internal static unsafe class NamePlateAdditions
{
    /// <summary>The object ID of a nameplate info entry without an object.</summary>
    public const ulong NoObjectId = 0xE0000000;

    /// <summary>The bit of NamePlateObjectIntArrayData.UpdateFlags that has the plate updated, as a changed text does.</summary>
    public const int UpdateFlagUpdate = 1;

    /// <summary>The bit of NamePlateObjectIntArrayData.DrawFlags that places the title before the name.</summary>
    public const int DrawFlagPrefixTitle = 1;

    /// <summary>The bit of NamePlateObjectIntArrayData.DrawFlags that hides the title.</summary>
    public const int DrawFlagHideTitle = 0x80;

    /// <summary>The offset of the titles in the nameplate string array.</summary>
    public const int TitleStringOffset = 50;

    extension(ref AddonNamePlate.BakeData bake)
    {
        /// <summary>Gets the object the bake's data is of, being its first member.</summary>
        public AddonNamePlate.NamePlateObject* Object => (AddonNamePlate.NamePlateObject*)Unsafe.AsPointer(ref bake);
    }
}
