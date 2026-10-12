// Font structures of the game's UI text renderer that FFXIVClientStructs lacks or has stale (AtkFontManager.Font), written
// as they would be there, to be contributed.
// RE write-up: private-scratch/ffxiv/ui/font_rendering_756h.md.

using System;
using System.Runtime.InteropServices;
using System.Text;

using FFXIVClientStructs.FFXIV.Component.GUI;

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

/// <summary>
/// The UI's font manager (FFXIVClientStructs' AtkFontManager, embedded in AtkModule; AtkStage points to it): the fonts
/// and the font sets made of them.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 0x90)]
public unsafe struct GameFontManager
{
    /// <summary>The number of font sets: one per <see cref="GameFontType"/>.</summary>
    public const int FontSetCount = 6;

    /// <summary>
    /// <c>[MemberFunction]</c> of <c>int BuildFont(ushort index)</c>: builds a font from its FDT (again, in place, on a
    /// reload or a switch between the lobby's fonts and the game's).
    /// </summary>
    public const string BuildFontSignature = "40 56 48 83 EC 60 66 3B 51 10 0F 83 ?? ?? ?? ?? 0F B7 C2 48 69 F0 F8 00 00 00";

    /// <summary>The font sets, indexed by <see cref="GameFontType"/>.</summary>
    [FieldOffset(0x00)] public GameFontSet* FontSets;

    /// <summary>The fonts, <see cref="FontCount"/> of them.</summary>
    [FieldOffset(0x08)] public GameFont* Fonts;

    [FieldOffset(0x10)] public ushort FontCount;

    /// <summary>Gets the font manager of the UI, or null before it is set up.</summary>
    public static GameFontManager* Instance()
    {
        var stage = AtkStage.Instance();
        return stage is null ? null : (GameFontManager*)stage->AtkFontManager;
    }

    /// <summary>Gets a font set; null if there are none yet.</summary>
    public GameFontSet* FontSet(int index) => this.FontSets is null ? null : this.FontSets + index;

    /// <summary>Gets a font of <see cref="Fonts"/>.</summary>
    public GameFont* Font(int index) => this.Fonts + index;

    /// <summary>Gets the index of a font in <see cref="Fonts"/>; -1 if it isn't one of them.</summary>
    public int IndexOf(GameFont* font)
    {
        var offset = (byte*)font - (byte*)this.Fonts;
        return this.Fonts is null || offset < 0 || offset % sizeof(GameFont) != 0 || offset / sizeof(GameFont) >= this.FontCount
                   ? -1
                   : (int)(offset / sizeof(GameFont));
    }
}

/// <summary>An entry of the fixed tables the game loads its fonts from (<see cref="GameFontTables"/>).</summary>
[StructLayout(LayoutKind.Explicit, Size = 0x18)]
public unsafe struct GameFontTableEntry
{
    [FieldOffset(0x00)] public ulong TextureCount;

    /// <summary>The textures' file name, with <c>%d</c> for the texture number.</summary>
    [FieldOffset(0x08)] public byte* TextureNameFormat;

    [FieldOffset(0x10)] public byte* FdtName;
}

/// <summary>The fixed tables the game loads its fonts from: the game's, and the lobby's two.</summary>
internal static unsafe class GameFontTables
{
    /// <summary>The entries of each table, one per font of the font manager.</summary>
    public const int EntryCount = 41;

    /// <summary>
    /// <c>[StaticAddress]</c> of the tables, where the fonts are loaded: the lobby's at <see cref="LobbyTableAOffset"/>
    /// and <see cref="LobbyTableBOffset"/>, the game's at <see cref="GameTableOffset"/>.
    /// </summary>
    public const string TablesSignature = "48 8D 1D ?? ?? ?? ?? 48 8D 05 ?? ?? ?? ?? 48 0F 45 D8 EB 07 48 8D 1D ?? ?? ?? ??";

    public const int LobbyTableAOffset = 3;
    public const int LobbyTableBOffset = 10;
    public const int GameTableOffset = 23;

    // The most textures a font of the global client's tables has.
    private const int GlobalMaxTextureCount = 7;

    public static GameFontTableEntry* GameTable() => (GameFontTableEntry*)Host.Current.GetStaticAddress(TablesSignature, GameTableOffset);

    public static GameFontTableEntry* LobbyTableA() => (GameFontTableEntry*)Host.Current.GetStaticAddress(TablesSignature, LobbyTableAOffset);

    public static GameFontTableEntry* LobbyTableB() => (GameFontTableEntry*)Host.Current.GetStaticAddress(TablesSignature, LobbyTableBOffset);

    /// <summary>
    /// Gets the most textures a font of the tables has (the indices from there on are free in every font); the global
    /// client's if the tables aren't found.
    /// </summary>
    public static int GetMaxTextureCount()
    {
        GameFontTableEntry*[] tables;
        try
        {
            tables = [GameTable(), LobbyTableA(), LobbyTableB()];
        }
        catch (Exception ex)
        {
            Host.Log.Warning(ex, "The font tables weren't found; taking the global client's texture count");
            return GlobalMaxTextureCount;
        }

        var max = 0;
        foreach (var table in tables)
        {
            for (var i = 0; i < EntryCount; i++)
                max = Math.Max(max, (int)table[i].TextureCount);
        }

        return max;
    }
}

/// <summary>
/// The fonts of one <see cref="GameFontType"/>, and the per-draw state the text analyzers keep in it. The font is picked
/// per run by PickFont from its slots (at its start; the alternate ones when its flags' bit 3 is set).
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 0x278)]
public unsafe struct GameFontSet
{
    /// <summary>The italic bit of <see cref="DrawFlags"/>.</summary>
    public const uint DrawFlagsItalic = 0x08000000;

    /// <summary>
    /// <c>[MemberFunction]</c> of <c>GameFont* PickFont(bool useCache)</c>: picks the font of the set for the requested
    /// size.
    /// </summary>
    public const string PickFontSignature = "83 B9 58 01 00 00 00 4C 8B C1 F3 0F 10 91 2C 01 00 00";

    /// <summary>
    /// <c>[MemberFunction]</c> of <c>GameGlyph* GetGlyph(uint utf8Value, GameFont* font)</c>: looks a glyph up in a font
    /// of the set.
    /// </summary>
    public const string GetGlyphSignature = "89 54 24 10 48 83 EC 38 41 F6 80 F2 00 00 00 04";

    /// <summary>The font size requested for the current run after the node's scale.</summary>
    [FieldOffset(0x12C)] public float ScaledSizeY;

    /// <summary>The node's screen scale (lengths of its transform's rows; AtkTextNodeRenderer vf3).</summary>
    [FieldOffset(0x130)] public float NodeScaleX;

    [FieldOffset(0x134)] public float NodeScaleY;

    /// <summary>Of the text being drawn; the text node's italics are in it.</summary>
    [FieldOffset(0x158)] public uint DrawFlags;

    /// <summary>The font picked last, and the size it was picked for (PickFont reuses it while the size is unchanged).</summary>
    [FieldOffset(0x250)] public GameFont* CurrentFont;

    [FieldOffset(0x258)] public float CurrentFontSize;
}

/// <summary>
/// A font: an FDT's metrics, its glyphs converted to <see cref="GameGlyph"/>, and its atlas textures. Built from the FDT
/// by BuildFont; the FDT resource handle is released afterwards.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 0xF8)]
public unsafe struct GameFont
{
    /// <summary>The most textures a font can have: the text renderer keeps vertex counts and buffers for that many.</summary>
    public const int MaxTextures = 10;

    /// <summary>The bit of <see cref="Flags"/> that BuildFont sets once the font is built.</summary>
    public const byte FlagsReady = 2;

    /// <summary>TextureResourceHandle*, of texture 0 first.</summary>
    [FieldOffset(0x00)] private fixed ulong textureResourceHandles[MaxTextures];

    /// <summary>Kernel::Texture*, the atlases the renderer binds; glyphs refer to them by index.</summary>
    [FieldOffset(0x58)] private fixed ulong textures[MaxTextures];

    [FieldOffset(0xAC)] public uint KerningCount;

    /// <summary>Also written to every vertex: FontEdgeVS and FontGlareVS take 1 / this as the texel step on both axes.</summary>
    [FieldOffset(0xB4)] public ushort TextureWidth;

    [FieldOffset(0xB6)] public ushort TextureHeight;

    /// <summary>The size the glyphs are drawn at; the renderer scales them by the requested size / this.</summary>
    [FieldOffset(0xB8)] public float Size;

    [FieldOffset(0xBC)] public int LineHeight;

    [FieldOffset(0xC0)] public int Ascent;

    /// <summary>
    /// <c>std::unordered_map&lt;uint, GameGlyph*&gt;*</c> keyed by <see cref="GameGlyph.Utf8Value"/>; see
    /// <see cref="GameGlyphMap"/>. The game looks up with <c>operator[]</c>, which inserts a null entry on a miss.
    /// </summary>
    [FieldOffset(0xD0)] public GameGlyphMap* GlyphMap;

    /// <summary>A font whose glyph is used instead when it is narrower (times <see cref="SecondaryRatio"/>).</summary>
    [FieldOffset(0xE0)] public GameFont* Secondary;

    [FieldOffset(0xE8)] public float SecondaryRatio;

    [FieldOffset(0xF0)] public ushort TextureCount;

    [FieldOffset(0xF2)] public byte Flags;

    /// <summary>
    /// How far the glyph after italics moves right (2 to 6 per font, set with the font sets), for the overhang of the
    /// sheared glyphs before it.
    /// </summary>
    [FieldOffset(0xF4)] public sbyte ItalicCorrection;

    /// <summary>Gets whether the font is built (BuildFont); a font that isn't may have no glyph map, or a freed one.</summary>
    public bool IsReady => (this.Flags & FlagsReady) != 0;

    /// <summary>Gets a texture resource handle (<see cref="MaxTextures"/> of them; the first is of texture 0).</summary>
    public nint GetTextureResourceHandle(int index) => (nint)this.textureResourceHandles[index];

    /// <summary>Gets an atlas texture the renderer binds (a Kernel::Texture*); glyphs refer to them by index.</summary>
    public nint GetTexture(int index) => (nint)this.textures[index];

    public void SetTexture(int index, nint texture) => this.textures[index] = (ulong)texture;

    /// <summary>Copies a whole font.</summary>
    public void CopyFrom(GameFont* source) => this = *source;

    /// <summary>Gets the glyph of a codepoint, without adding a map entry as the game's lookup does; null if none.</summary>
    public GameGlyph* FindGlyph(int codepoint)
    {
        if (this.GlyphMap is null)
            return null;
        var node = this.GlyphMap->Find(GameUtf8.Pack(codepoint));
        return node is null ? null : node->Value;
    }

    public bool HasGlyph(int codepoint) => this.FindGlyph(codepoint) is not null;
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

/// <summary>A kerning pair (12 bytes), sorted by (<see cref="Left"/>, <see cref="Right"/>); binary searched by the game.</summary>
[StructLayout(LayoutKind.Explicit, Size = 0x0C)]
public struct GameKerningEntry
{
    [FieldOffset(0x00)] public uint Left;

    [FieldOffset(0x04)] public uint Right;

    [FieldOffset(0x08)] public int Adjustment;
}

/// <summary>
/// MSVC <c>std::unordered_map&lt;uint, GameGlyph*&gt;</c> (0x40 bytes): a doubly linked list of all nodes, and per bucket
/// the first and last node of its run. FNV-1a over the key's 4 bytes (GlyphMapFind).
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
}

/// <summary>Conversions between codepoints and the game's packed UTF-8 values.</summary>
public static class GameUtf8
{
    /// <summary>Packs a codepoint's UTF-8 bytes big-endian, the way the game keys glyphs.</summary>
    public static uint Pack(int codepoint)
    {
        var c = (uint)codepoint;
        return c switch
        {
            < 0x80 => c,
            < 0x800 => 0xC080u | ((c << 2) & 0x1F00) | (c & 0x3F),
            < 0x10000 => 0xE08080u | ((c << 4) & 0x0F0000) | ((c << 2) & 0x3F00) | (c & 0x3F),
            _ => 0xF0808080u | ((c << 6) & 0x07000000) | ((c << 4) & 0x3F0000) | ((c << 2) & 0x3F00) | (c & 0x3F),
        };
    }

    /// <summary>The length of a UTF-8 sequence by its first byte, as the game steps through text (LayOutCharacter).</summary>
    public static int SequenceLength(byte b) => b < 0xC0 ? 1 : b < 0xE0 ? 2 : b < 0xF0 ? 3 : b < 0xF8 ? 4 : 1;

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
