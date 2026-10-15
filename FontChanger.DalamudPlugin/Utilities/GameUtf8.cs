// Conversions between codepoints and the packed UTF-8 values the game keys its glyphs by, which FFXIVClientStructs'
// AtkFontManager.Glyph.Utf8Value holds.

using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;

using Lumina.Text;
using Lumina.Text.ReadOnly;

namespace FontChanger.DalamudPlugin.Utilities;

public static unsafe class GameUtf8
{
    public static uint Pack(Rune c) => Pack((uint)c.Value);

    public static uint Pack(uint c) => c switch
    {
        < 0x80 => c,
        < 0x800 => 0xC080u | ((c << 2) & 0x1F00) | (c & 0x3F),
        < 0x10000 => 0xE08080u | ((c << 4) & 0x0F0000) | ((c << 2) & 0x3F00) | (c & 0x3F),
        _ => 0xF0808080u | ((c << 6) & 0x07000000) | ((c << 4) & 0x3F0000) | ((c << 2) & 0x3F00) | (c & 0x3F),
    };

    public static uint Pack(byte* p) => p[0] switch
    {
        0 => 0,
        < 0x80 => p[0],
        < 0xC0 => (uint)Rune.ReplacementChar.Value,
        < 0xE0 => BinaryPrimitives.ReadUInt16BigEndian(new(p, 2)),
        < 0xF0 => ((uint)p[0] << 16) | ((uint)p[1] << 8) | p[2],
        < 0xF8 => BinaryPrimitives.ReadUInt32BigEndian(new(p, 4)),
        _ => (uint)Rune.ReplacementChar.Value,
    };

    public static bool TryUnpack(uint value, out Rune rune)
    {
        var valueReversed = BinaryPrimitives.ReverseEndianness(value);
        var bytes = MemoryMarshal.Cast<uint, byte>(new Span<uint>(ref valueReversed));
        bytes = value switch
        {
            > 0xFFFFFF => bytes,
            > 0xFFFF => bytes[1..],
            > 0xFF => bytes[2..],
            _ => bytes[3..],
        };
        return Rune.DecodeFromUtf8(bytes, out rune, out var consumed) == OperationStatus.Done && consumed == bytes.Length;
    }

    public static int CharacterLength(byte* p) => *p switch
    {
        < 0xC0 => 1,
        < 0xE0 => p[1] != 0 ? 2 : 0,
        < 0xF0 => p[1] != 0 && p[2] != 0 ? 3 : 0,
        < 0xF8 => p[1] != 0 && p[2] != 0 && p[3] != 0 ? 4 : 0,
        _ => 1,
    };

    public static bool TryReadInteger(byte* p, out int value, out int length) =>
        SeExpressionUtilities.TryDecodeInt(new ReadOnlySpan<byte>(p, 5), out value, out length);

    public static int MacroLength(byte* p)
    {
        if (p[1] == 0)
            return 0;
        if (!TryReadInteger(p + 2, out var payload, out var n) || payload < 0)
            return 0;

        var total = 2L + n + payload + 1;
        if (total > int.MaxValue)
            return 0;
        for (var i = 2L + n; i < total - 1; i++)
        {
            if (p[i] == 0)
                return 0;
        }

        return p[total - 1] == ReadOnlySeString.Etx ? (int)total : 0;
    }
}
