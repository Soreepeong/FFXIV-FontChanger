using System;

using Lumina.Text;
using Lumina.Text.ReadOnly;

namespace CustomFonts;

/// <summary>
/// Steps through the game's text (SeString) as it does: characters by the first byte of their UTF-8 sequence, and macros
/// (0x02, type, length, payload, 0x03) whole.
/// </summary>
internal static unsafe class GameText
{
    /// <summary>
    /// Decodes a SeString integer; <paramref name="length"/> is the bytes its marker declares. Fails on an expression
    /// marker (0xD0 to 0xEF) or the end of the text inside it. Values of 2^31 and above come out negative.
    /// </summary>
    public static bool TryReadInteger(byte* p, out int value, out int length) =>
        // An integer takes at most 5 bytes, and Lumina stops at a zero byte, so the text's end isn't read past.
        SeExpressionUtilities.TryDecodeInt(new ReadOnlySpan<byte>(p, 5), out value, out length);

    /// <summary>
    /// Gets the length of the macro at <paramref name="p"/> (which starts with <see cref="ReadOnlySeString.Stx"/>); 0 if it
    /// is malformed: a bad length, the end of the text inside it, or no <see cref="ReadOnlySeString.Etx"/> after its payload.
    /// </summary>
    public static int MacroLength(byte* p)
    {
        if (p[1] == 0)
            return 0;
        if (!TryReadInteger(p + 2, out var payload, out var n) || payload < 0)
            return 0;

        // Counted in 64 bits so a huge declared length can't wrap; the payload is walked up to the end of the text, so
        // a length claiming more bytes than are left stops at the terminator instead of reading past it.
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

    /// <summary>
    /// Gets the length of the character at <paramref name="p"/> as the game steps (by its first byte alone, valid or
    /// not); 0 if it runs into the end of the text.
    /// </summary>
    public static int CharacterLength(byte* p)
    {
        var n = GameUtf8.SequenceLength(*p);
        for (var i = 1; i < n; i++)
        {
            if (p[i] == 0)
                return 0;
        }

        return n;
    }
}
