namespace CustomFonts;

/// <summary>
/// Steps through the game's text (SeString) as it does: characters by the first byte of their UTF-8 sequence, and macros
/// (0x02, type, length, payload, 0x03) whole.
/// </summary>
internal static unsafe class GameText
{
    public const byte MacroStart = 0x02;
    public const byte MacroEnd = 0x03;

    /// <summary>Decodes a SeString integer (a macro's payload length); returns the bytes it takes, 0 if malformed.</summary>
    public static int ReadInteger(byte* p, out int value)
    {
        var marker = p[0];
        if (marker == 0 || marker > 0xFE)
        {
            value = 0;
            return 0;
        }

        if (marker < 0xF0)
        {
            value = marker - 1;
            return 1;
        }

        // The marker's low bits say which bytes of the value (highest first) follow; the others are zero.
        var flags = marker + 1;
        var n = 1;
        value = 0;
        for (var bit = 3; bit >= 0; bit--)
        {
            if ((flags & (1 << bit)) != 0)
                value |= p[n++] << (8 * bit);
        }

        return n;
    }

    /// <summary>
    /// Gets the length of the macro at <paramref name="p"/> (which starts with <see cref="MacroStart"/>); 0 if it is
    /// malformed: a bad length, the end of the text inside it, or no <see cref="MacroEnd"/> after its payload.
    /// </summary>
    public static int MacroLength(byte* p)
    {
        if (p[1] == 0)
            return 0;
        var n = ReadInteger(p + 2, out var payload);
        if (n == 0 || payload < 0)
            return 0;
        var total = 2 + n + payload + 1;
        for (var i = 2; i < total - 1; i++)
        {
            if (p[i] == 0)
                return 0;
        }

        return p[total - 1] == MacroEnd ? total : 0;
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
