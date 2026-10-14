// Conversions between codepoints and the packed UTF-8 values the game keys its glyphs by, which FFXIVClientStructs'
// AtkFontManager.Glyph.Utf8Value holds.

using System;
using System.Buffers;
using System.Text;

namespace CustomFonts.Utilities;

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
        return Rune.DecodeFromUtf8(buf[..n], out var rune, out var consumed) == OperationStatus.Done && consumed == n ? rune.Value : -1;
    }
}
