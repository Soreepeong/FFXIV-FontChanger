// Font structures of the game's UI text renderer. Those whose fields moved over the patches (the font manager, font sets
// and fonts) are read at the offsets the game's code says (GameLayout, from game_font_signatures.json); those that kept
// their layout since 2020 are C# structures, checked against the code. Addresses in comments are of 7.56h (image base
// 0x140000000). RE write-up: private-scratch/ffxiv/ui/font_rendering_756h.md.

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

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
/// The UI's font manager (AtkFontManager, embedded in AtkModule; AtkStage points to it): the fonts and the font sets
/// made of them. Opaque: only used through pointers.
/// </summary>
[StructLayout(LayoutKind.Sequential, Size = 1)]
public unsafe struct GameFontManager
{
    private static nint stageInstance;
    private static int stageFontManager;
    private static int fontSets;
    private static int fonts;
    private static int fontCount;

    /// <summary>Gets the number of font sets: one per <see cref="GameFontType"/>.</summary>
    public static int FontSetCount { get; private set; }

    /// <summary>The font sets, indexed by <see cref="GameFontType"/>.</summary>
    public GameFontSet* FontSets => *(GameFontSet**)this.At(fontSets);

    /// <summary>The fonts, <see cref="FontCount"/> of them, <see cref="GameFont.StructSize"/> apart.</summary>
    public GameFont* Fonts => *(GameFont**)this.At(fonts);

    public ushort FontCount => *(ushort*)this.At(fontCount);

    /// <summary>Gets the font manager of the UI, or null before it is set up.</summary>
    public static GameFontManager* Instance()
    {
        var stage = *(nint*)stageInstance;
        return stage == 0 ? null : *(GameFontManager**)(stage + stageFontManager);
    }

    /// <summary>Gets a font set; null if there are none yet.</summary>
    public GameFontSet* FontSet(int index) =>
        this.FontSets is null ? null : (GameFontSet*)((byte*)this.FontSets + (index * GameFontSet.StructSize));

    /// <summary>Gets a font of <see cref="Fonts"/>.</summary>
    public GameFont* Font(int index) => (GameFont*)((byte*)this.Fonts + (index * GameFont.StructSize));

    /// <summary>Gets the index of a font in <see cref="Fonts"/>; -1 if it isn't one of them.</summary>
    public int IndexOf(GameFont* font)
    {
        var offset = (byte*)font - (byte*)this.Fonts;
        return this.Fonts is null || offset < 0 || offset % GameFont.StructSize != 0 || offset / GameFont.StructSize >= this.FontCount
                   ? -1
                   : (int)(offset / GameFont.StructSize);
    }

    internal static void Resolve()
    {
        stageInstance = GameLayout.Target("AtkStage.Instance");
        stageFontManager = GameLayout.Get("AtkStage.AtkFontManager");
        fontSets = GameLayout.Get("AtkFontManager.FontSets", 0);
        fonts = GameLayout.Get("AtkFontManager.Fonts");
        fontCount = GameLayout.Get("AtkFontManager.FontCount");
        FontSetCount = GameLayout.Get("AtkFontManager.FontSetCount");
    }

    private byte* At(int offset) => (byte*)Unsafe.AsPointer(ref this) + offset;
}

/// <summary>Resolves the font structures' layouts, within <see cref="GameLayout.Resolve"/>.</summary>
internal static class GameFontStructs
{
    public static void Resolve()
    {
        GameFontManager.Resolve();
        GameFontSet.Resolve();
        GameFont.Resolve();
        GameLayout.CheckFixed("GameGlyph", typeof(GameGlyph));
        GameLayout.CheckFixed("GameKerningEntry", typeof(GameKerningEntry));
        GameLayout.CheckFixed("GlyphMap", typeof(GameGlyphMap));
        GameLayout.CheckFixed("GlyphMap.Node", typeof(GameGlyphMap.Node));
    }
}

/// <summary>
/// The fonts of one <see cref="GameFontType"/>, and the per-draw state the text analyzers keep in it. The font is picked
/// per run by PickFont from its <see cref="GameFontSetSlot"/>s (at its start; the alternate ones when its flags' bit 3 is
/// set). Opaque: only used through pointers.
/// </summary>
[StructLayout(LayoutKind.Sequential, Size = 1)]
public unsafe struct GameFontSet
{
    private static int scaledSizeY;
    private static int nodeScaleX;
    private static int nodeScaleY;
    private static int currentFont;
    private static int currentFontSize;

    /// <summary>Gets the size of a font set, the stride of <see cref="GameFontManager.FontSets"/>.</summary>
    public static int StructSize { get; private set; }

    /// <summary>The font size requested for the current run after the node's scale.</summary>
    public float ScaledSizeY => *(float*)this.At(scaledSizeY);

    /// <summary>The node's screen scale (lengths of its transform's rows; AtkTextNodeRenderer vf3).</summary>
    public float NodeScaleX
    {
        get => *(float*)this.At(nodeScaleX);
        set => *(float*)this.At(nodeScaleX) = value;
    }

    public float NodeScaleY
    {
        get => *(float*)this.At(nodeScaleY);
        set => *(float*)this.At(nodeScaleY) = value;
    }

    /// <summary>The font picked last, and the size it was picked for (PickFont reuses it while the size is unchanged).</summary>
    public GameFont* CurrentFont
    {
        get => *(GameFont**)this.At(currentFont);
        set => *(GameFont**)this.At(currentFont) = value;
    }

    public float CurrentFontSize
    {
        get => *(float*)this.At(currentFontSize);
        set => *(float*)this.At(currentFontSize) = value;
    }

    internal static void Resolve()
    {
        StructSize = GameLayout.Get("GameFontSet");
        scaledSizeY = GameLayout.Get("GameFontSet.ScaledSizeY");
        nodeScaleX = GameLayout.Get("GameFontSet.NodeScaleX");
        nodeScaleY = GameLayout.Get("GameFontSet.NodeScaleY");
        currentFont = GameLayout.Get("GameFontSet.CurrentFont");
        currentFontSize = GameLayout.Get("GameFontSet.CurrentFontSize");
    }

    private byte* At(int offset) => (byte*)Unsafe.AsPointer(ref this) + offset;
}

/// <summary>
/// A font: an FDT's metrics, its glyphs converted to <see cref="GameGlyph"/>, and its atlas textures. Built from the FDT
/// by BuildFont; the FDT resource handle is released afterwards. Opaque: only used through pointers; copied with
/// <see cref="CopyFrom"/>.
/// </summary>
[StructLayout(LayoutKind.Sequential, Size = 1)]
public unsafe struct GameFont
{
    private static int textureResourceHandles;
    private static int textures;
    private static int kerningCount;
    private static int textureWidth;
    private static int size;
    private static int ascent;
    private static int glyphMap;
    private static int secondary;
    private static int secondaryRatio;
    private static int textureCount;
    private static int italicCorrection = -1;

    /// <summary>Gets the size of a font, the stride of <see cref="GameFontManager.Fonts"/>.</summary>
    public static int StructSize { get; private set; }

    /// <summary>Gets the most textures a font can have: the text renderer keeps vertex counts and buffers for that many.</summary>
    public static int MaxTextures { get; private set; }

    public uint KerningCount
    {
        get => *(uint*)this.At(kerningCount);
        set => *(uint*)this.At(kerningCount) = value;
    }

    /// <summary>Also written to every vertex: FontEdgeVS and FontGlareVS take 1 / this as the texel step on both axes.</summary>
    public ushort TextureWidth
    {
        get => *(ushort*)this.At(textureWidth);
        set => *(ushort*)this.At(textureWidth) = value;
    }

    /// <summary>Follows <see cref="TextureWidth"/> (the FDT header's order).</summary>
    public ushort TextureHeight
    {
        get => *(ushort*)this.At(textureWidth + 2);
        set => *(ushort*)this.At(textureWidth + 2) = value;
    }

    /// <summary>The size the glyphs are drawn at; the renderer scales them by the requested size / this.</summary>
    public float Size
    {
        get => *(float*)this.At(size);
        set => *(float*)this.At(size) = value;
    }

    /// <summary>Follows <see cref="Size"/> (BuildFont copies both from the FDT header at once).</summary>
    public int LineHeight
    {
        get => *(int*)this.At(size + 4);
        set => *(int*)this.At(size + 4) = value;
    }

    public int Ascent
    {
        get => *(int*)this.At(ascent);
        set => *(int*)this.At(ascent) = value;
    }

    /// <summary>
    /// <c>std::unordered_map&lt;uint, GameGlyph*&gt;*</c> keyed by <see cref="GameGlyph.Utf8Value"/>; see
    /// <see cref="GameGlyphMap"/>. The game looks up with <c>operator[]</c>, which inserts a null entry on a miss.
    /// </summary>
    public GameGlyphMap* GlyphMap => *(GameGlyphMap**)this.At(glyphMap);

    /// <summary>A font whose glyph is used instead when it is narrower (times <see cref="SecondaryRatio"/>).</summary>
    public GameFont* Secondary
    {
        get => *(GameFont**)this.At(secondary);
        set => *(GameFont**)this.At(secondary) = value;
    }

    public float SecondaryRatio
    {
        get => *(float*)this.At(secondaryRatio);
        set => *(float*)this.At(secondaryRatio) = value;
    }

    public ushort TextureCount
    {
        get => *(ushort*)this.At(textureCount);
        set => *(ushort*)this.At(textureCount) = value;
    }

    /// <summary>
    /// Gets or sets how far the glyph after italics moves right (2 to 6 per font, set with the font sets), for the overhang
    /// of the sheared glyphs before it; does nothing if the game's code doesn't say where it is (XShift).
    /// </summary>
    public sbyte ItalicCorrection
    {
        get => italicCorrection < 0 ? (sbyte)0 : *(sbyte*)this.At(italicCorrection);
        set
        {
            if (italicCorrection >= 0)
                *(sbyte*)this.At(italicCorrection) = value;
        }
    }

    /// <summary>Gets a texture resource handle (<see cref="MaxTextures"/> of them; the first is of texture 0).</summary>
    public nint GetTextureResourceHandle(int index) => ((nint*)this.At(textureResourceHandles))[index];

    /// <summary>Gets an atlas texture the renderer binds (a Kernel::Texture*); glyphs refer to them by index.</summary>
    public nint GetTexture(int index) => ((nint*)this.At(textures))[index];

    public void SetTexture(int index, nint texture) => ((nint*)this.At(textures))[index] = texture;

    /// <summary>Copies a whole font.</summary>
    public void CopyFrom(GameFont* source) =>
        Buffer.MemoryCopy(source, Unsafe.AsPointer(ref this), StructSize, StructSize);

    /// <summary>Gets the glyph of a codepoint, without adding a map entry as the game's lookup does; null if none.</summary>
    public GameGlyph* FindGlyph(int codepoint)
    {
        if (this.GlyphMap is null)
            return null;
        var node = this.GlyphMap->Find(GameUtf8.Pack(codepoint));
        return node is null ? null : node->Value;
    }

    public bool HasGlyph(int codepoint) => this.FindGlyph(codepoint) is not null;

    internal static void Resolve()
    {
        StructSize = GameLayout.Get("GameFont");
        MaxTextures = GameLayout.Get("GameFont.MaxTextures");
        textureResourceHandles = GameLayout.Get("GameFont.TextureResourceHandles", 0);
        textures = GameLayout.Get("GameFont.Textures");
        kerningCount = GameLayout.Get("GameFont.KerningCount");
        textureWidth = GameLayout.Get("GameFont.TextureWidth");
        size = GameLayout.Get("GameFont.Size");
        ascent = GameLayout.Get("GameFont.Ascent");

        // The glyph map replaced a sorted glyph array in 7.40; the plugin only looks glyphs up in the map.
        glyphMap = GameLayout.Get("GameFont.GlyphMap");
        secondary = GameLayout.Get("GameFont.Secondary");
        secondaryRatio = GameLayout.Get("GameFont.SecondaryRatio");
        textureCount = GameLayout.Get("GameFont.TextureCount");

        // Optional: only changed in copies.
        italicCorrection = GameLayout.TryGet("GameFont.XShift") ?? -1;
    }

    private byte* At(int offset) => (byte*)Unsafe.AsPointer(ref this) + offset;
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

    /// <summary>The length of a UTF-8 sequence by its first byte, as the game steps through text (LayOutCharacter).</summary>
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
