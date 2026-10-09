// Font structures of the game's UI text renderer, as of 7.56h (image base 0x140000000). FFXIVClientStructs has
// AtkFontManager, but its Font is 0xF0 with TextureCount at +0xE8; in 7.56h the stride is 0xF8 and the count is at
// +0xF0 (AtkFontManager setup, FUN_14064ecf0, and the texture binding loop, FUN_140655820, both read +0xF0).
// RE write-up: private-scratch/ffxiv/ui/font_rendering_756h.md.

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
using FFXIVClientStructs.FFXIV.Client.System.Resource.Handle;
using FFXIVClientStructs.FFXIV.Component.GUI;
using FFXIVClientStructs.Interop;

namespace CustomFonts;

/// <summary>The font types a text node selects (<c>AtkTextNode.AlignmentFontType</c> high nibble), one font set each.</summary>
public enum GameFontType
{
    Axis = 0,
    MiedingerMid = 1,
    Miedinger = 2,
    TrumpGothic = 3,
    Jupiter = 4,
    JupiterLarge = 5,
}

/// <summary>What <see cref="AtkFontManager"/> lacks; use through a cast: <c>((AtkFontManagerExtras*)manager)</c>.</summary>
[StructLayout(LayoutKind.Explicit)]
public unsafe struct AtkFontManagerExtras
{
    /// <summary>Number of font sets behind <see cref="FontSets"/>.</summary>
    public const int FontSetCount = 6;

    /// <summary>
    /// The font sets, indexed by <see cref="GameFontType"/>: <c>AtkTextNodeRenderer.Draw</c> (0x1406C4690) takes
    /// <c>FontSets + fontType * 0x278</c>. Filled by FUN_14064C140 once the fonts have loaded.
    /// </summary>
    [FieldOffset(0x00)] public GameFontSet* FontSets;

    /// <summary>The fonts, <see cref="FontCount"/> (41) of them, 0xF8 apart.</summary>
    [FieldOffset(0x08)] public GameFont* Fonts;

    /// <summary>Number of <see cref="Fonts"/>.</summary>
    [FieldOffset(0x10)] public ushort FontCount;

    /// <summary>Gets the font manager of the UI, or null before it is set up.</summary>
    public static AtkFontManagerExtras* Instance()
    {
        var stage = AtkStage.Instance();
        return stage is null ? null : (AtkFontManagerExtras*)stage->AtkFontManager;
    }
}

/// <summary>One slot of a <see cref="GameFontSet"/>: a font and the size it stands for.</summary>
[StructLayout(LayoutKind.Explicit, Size = 0x10)]
public unsafe struct GameFontSetSlot
{
    [FieldOffset(0x00)] public GameFont* Font;

    /// <summary>The size the slot is chosen for: the largest one not above the requested size + 0.5 wins.</summary>
    [FieldOffset(0x08)] public float Size;

    [FieldOffset(0x0C)] public float Scale;
}

/// <summary>
/// The fonts of one <see cref="GameFontType"/>, and the per-draw state the text analyzers keep in it (0x278 bytes). The
/// font is picked per run by FUN_140651DC0 from <see cref="Slots"/> 0..7, or 8..15 when <see cref="Flags"/> bit 3 is set.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 0x278)]
public unsafe struct GameFontSet
{
    /// <summary>Number of <see cref="Slots"/>.</summary>
    public const int SlotCount = 16;

    [FieldOffset(0x000)] public GameFontSetSlotArray16 Slots;

    /// <summary>The font size requested for the current run, before and after the node's scale (+0x128/+0x12C).</summary>
    [FieldOffset(0x120)] public float RequestedSizeX;

    [FieldOffset(0x124)] public float RequestedSizeY;

    [FieldOffset(0x128)] public float ScaledSizeX;

    [FieldOffset(0x12C)] public float ScaledSizeY;

    /// <summary>The node's screen scale (lengths of its transform's rows; AtkTextNodeRenderer vf3).</summary>
    [FieldOffset(0x130)] public float NodeScaleX;

    [FieldOffset(0x134)] public float NodeScaleY;

    /// <summary>
    /// Bits 0..15: depth; bit 31: the node's fixed font resolution flag (AtkTextNode +0x171 bit 4), which makes the picker
    /// use <see cref="RequestedSizeY"/> instead of <see cref="ScaledSizeY"/>. Built by AtkTextNodeRenderer vf3.
    /// </summary>
    [FieldOffset(0x158)] public uint DrawFlags;

    /// <summary>The node's +0x16C: when positive (and bit 31 of <see cref="DrawFlags"/> is clear), the picker uses this times <see cref="RequestedSizeY"/>.</summary>
    [FieldOffset(0x15C)] public float PickScale;

    /// <summary>The font picked last, and the size it was picked for (FUN_140651DC0 reuses it while the size is unchanged).</summary>
    [FieldOffset(0x250)] public GameFont* CurrentFont;

    [FieldOffset(0x258)] public float CurrentFontSize;

    /// <summary>Bit 1: has fonts; bit 2: has alternate slots; bit 3: alternate slots in use.</summary>
    [FieldOffset(0x268)] public byte Flags;
}

[InlineArray(GameFontSet.SlotCount)]
public struct GameFontSetSlotArray16
{
    private GameFontSetSlot element;
}

/// <summary>
/// A font: an FDT's metrics, its glyphs converted to <see cref="GameGlyph"/>, and its atlas textures (0xF8 bytes).
/// Built from the FDT by FUN_14064ECF0; the FDT resource handle is released afterwards.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 0xF8)]
public unsafe struct GameFont
{
    /// <summary>Most textures a font can have: the text renderer keeps vertex counts and buffers for 10 per font.</summary>
    public const int MaxTextures = 10;

    [FieldOffset(0x00)] public TextureResourceHandlePointerArray10 TextureResourceHandles;

    [FieldOffset(0x50)] public ResourceHandle* FontdataResourceHandle;

    /// <summary>The atlas textures the renderer binds; glyphs refer to them by index.</summary>
    [FieldOffset(0x58)] public TexturePointerArray10 Textures;

    [FieldOffset(0xA8)] public uint GlyphCount;

    [FieldOffset(0xAC)] public uint KerningCount;

    /// <summary>Also written to every vertex (+0x16): FontEdgeVS and FontGlareVS take 1 / this as the texel step on both axes.</summary>
    [FieldOffset(0xB4)] public ushort TextureWidth;

    [FieldOffset(0xB6)] public ushort TextureHeight;

    /// <summary>The size the glyphs are drawn at; the renderer scales them by the requested size / this.</summary>
    [FieldOffset(0xB8)] public float Size;

    [FieldOffset(0xBC)] public int LineHeight;

    [FieldOffset(0xC0)] public int Ascent;

    [FieldOffset(0xC8)] public GameGlyph* Glyphs;

    /// <summary>
    /// <c>std::unordered_map&lt;uint, GameGlyph*&gt;*</c> keyed by <see cref="GameGlyph.Utf8Value"/>; see
    /// <see cref="GameGlyphMap"/>. The game looks up with <c>operator[]</c>, which inserts a null entry on a miss.
    /// </summary>
    [FieldOffset(0xD0)] public GameGlyphMap* GlyphMap;

    [FieldOffset(0xD8)] public GameKerningEntry* Kerning;

    /// <summary>A font whose glyph is used instead when it is narrower (times <see cref="SecondaryRatio"/>).</summary>
    [FieldOffset(0xE0)] public GameFont* Secondary;

    [FieldOffset(0xE8)] public float SecondaryRatio;

    /// <summary>1 loading, 2 loaded, 3 failed.</summary>
    [FieldOffset(0xEC)] public int LoadState;

    [FieldOffset(0xF0)] public ushort TextureCount;

    /// <summary>Bit 0: built; bit 1: ready; bit 2: <see cref="DirectIndexFirst"/> and up index <see cref="Glyphs"/> directly.</summary>
    [FieldOffset(0xF2)] public byte Flags;

    /// <summary>The first codepoint of the run of glyphs below U+0100 that the fast path indexes directly.</summary>
    [FieldOffset(0xF3)] public byte DirectIndexFirst;

    [FieldOffset(0xF4)] public sbyte XShift;

    /// <summary>Extra spacing between consecutive Hangul or CJK glyphs.</summary>
    [FieldOffset(0xF5)] public sbyte CjkSpacing;

    /// <summary>Gets the glyph of a codepoint, without adding a map entry as the game's lookup does; null if none.</summary>
    public readonly GameGlyph* FindGlyph(int codepoint)
    {
        if (this.GlyphMap is null)
            return null;
        var node = this.GlyphMap->Find(GameUtf8.Pack(codepoint));
        return node is null ? null : node->Value;
    }

    public readonly bool HasGlyph(int codepoint) => this.FindGlyph(codepoint) is not null;
}

[InlineArray(GameFont.MaxTextures)]
public struct TextureResourceHandlePointerArray10
{
    private Pointer<TextureResourceHandle> element;
}

[InlineArray(GameFont.MaxTextures)]
public struct TexturePointerArray10
{
    private Pointer<Texture> element;
}

/// <summary>
/// A glyph as the renderer reads it (12 bytes), converted from the FDT's 16-byte entry. There is no left bearing: the
/// quad starts at the pen, and the pen advances by <see cref="Width"/> + <see cref="OffsetX"/> (+ kerning).
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 0x0C)]
public struct GameGlyph
{
    /// <summary>The codepoint's UTF-8 bytes, big-endian packed (U+3013 is 0xE38093).</summary>
    [FieldOffset(0x00)] public uint Utf8Value;

    /// <summary>X (12 bits), Y (12 bits), channel (2 bits: R, G, B, A of a B8G8R8A8 texel) and texture index (6 bits).</summary>
    [FieldOffset(0x04)] public uint Packed;

    [FieldOffset(0x08)] public byte Width;

    [FieldOffset(0x09)] public byte Height;

    [FieldOffset(0x0A)] public sbyte OffsetX;

    /// <summary>From the top of the line.</summary>
    [FieldOffset(0x0B)] public sbyte OffsetY;

    public readonly int X => (int)(this.Packed & 0xFFF);

    public readonly int Y => (int)((this.Packed >> 12) & 0xFFF);

    public readonly int Channel => (int)((this.Packed >> 24) & 3);

    public readonly int TextureIndex => (int)(this.Packed >> 26);

    public static uint Pack(int x, int y, int channel, int textureIndex) =>
        (uint)(x & 0xFFF) | ((uint)(y & 0xFFF) << 12) | ((uint)(channel & 3) << 24) | ((uint)textureIndex << 26);
}

/// <summary>A kerning pair (12 bytes), sorted by (<see cref="Left"/>, <see cref="Right"/>); binary searched by FUN_14064FD10.</summary>
[StructLayout(LayoutKind.Explicit, Size = 0x0C)]
public struct GameKerningEntry
{
    [FieldOffset(0x00)] public uint Left;

    [FieldOffset(0x04)] public uint Right;

    [FieldOffset(0x08)] public int Adjustment;
}

/// <summary>
/// MSVC <c>std::unordered_map&lt;uint, GameGlyph*&gt;</c> (0x40 bytes): a doubly linked list of all nodes, and per bucket
/// the first and last node of its run. FNV-1a over the key's 4 bytes (FUN_1406E97C0).
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 0x40)]
public unsafe struct GameGlyphMap
{
    [FieldOffset(0x00)] public float MaxLoadFactor;

    /// <summary>The list's sentinel node.</summary>
    [FieldOffset(0x08)] public Node* Head;

    [FieldOffset(0x10)] public ulong Count;

    /// <summary>Pairs of (first, last) node per bucket; both are <see cref="Head"/> for an empty bucket.</summary>
    [FieldOffset(0x18)] public Node** Buckets;

    [FieldOffset(0x30)] public ulong Mask;

    [FieldOffset(0x38)] public ulong MaxIndex;

    [StructLayout(LayoutKind.Explicit, Size = 0x20)]
    public struct Node
    {
        [FieldOffset(0x00)] public Node* Next;

        [FieldOffset(0x08)] public Node* Prev;

        [FieldOffset(0x10)] public uint Key;

        [FieldOffset(0x18)] public GameGlyph* Value;
    }

    /// <summary>Finds a key's node without inserting (unlike the game's lookup), or returns null.</summary>
    public readonly Node* Find(uint key)
    {
        var hash = 0xCBF29CE484222325ul;
        for (var i = 0; i < 4; i++)
            hash = (hash ^ ((key >> (8 * i)) & 0xFF)) * 0x100000001B3ul;

        var bucket = (hash & this.Mask) * 2;
        var first = this.Buckets[bucket];
        var node = this.Buckets[bucket + 1];
        if (node == this.Head)
            return null;
        while (true)
        {
            if (node->Key == key)
                return node;
            if (node == first)
                return null;
            node = node->Prev;
        }
    }

    /// <summary>Enumerates every node, in list order.</summary>
    public readonly IEnumerable<nint> EnumerateNodes()
    {
        var list = new List<nint>((int)this.Count);
        for (var node = this.Head->Next; node != this.Head; node = node->Next)
            list.Add((nint)node);
        return list;
    }
}

/// <summary>Conversions between codepoints and the game's packed UTF-8 values.</summary>
public static class GameUtf8
{
    /// <summary>Packs a codepoint's UTF-8 bytes big-endian, the way the game keys glyphs.</summary>
    public static uint Pack(int codepoint)
    {
        Span<byte> buf = stackalloc byte[4];
        var n = new Rune(codepoint).EncodeToUtf8(buf);
        var v = 0u;
        for (var i = 0; i < n; i++)
            v = (v << 8) | buf[i];
        return v;
    }

    /// <summary>The length of a UTF-8 sequence by its first byte, as the game steps through text (FUN_1406EEC70).</summary>
    public static int SequenceLength(byte b) => b < 0x80 ? 1 : b < 0xC0 ? 1 : b < 0xE0 ? 2 : b < 0xF0 ? 3 : b < 0xF8 ? 4 : 1;

    /// <summary>Packs a UTF-8 sequence's bytes big-endian, the way the game keys glyphs.</summary>
    public static unsafe uint PackSequence(byte* p, int length)
    {
        var v = 0u;
        for (var i = 0; i < length; i++)
            v = (v << 8) | p[i];
        return v;
    }

    /// <summary>Gets the codepoint of a packed UTF-8 value, or -1 if it isn't valid UTF-8.</summary>
    public static int Unpack(uint value)
    {
        Span<byte> buf = stackalloc byte[4];
        var n = value > 0xFFFFFF ? 4 : value > 0xFFFF ? 3 : value > 0xFF ? 2 : 1;
        for (var i = 0; i < n; i++)
            buf[i] = (byte)(value >> (8 * (n - 1 - i)));
        return Rune.DecodeFromUtf8(buf[..n], out var rune, out _) == System.Buffers.OperationStatus.Done ? rune.Value : -1;
    }
}
