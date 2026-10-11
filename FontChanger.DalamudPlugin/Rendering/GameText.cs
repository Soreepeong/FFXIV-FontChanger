namespace CustomFonts;

/// <summary>
/// Steps through the game's text (SeString) as it does: characters by the first byte of their UTF-8 sequence, and macros
/// (0x02, type, length, payload, 0x03) whole.
/// </summary>
internal static unsafe class GameText
{
    public const byte MacroStart = 0x02;
    public const byte MacroEnd = 0x03;

    /// <summary>
    /// Decodes a SeString integer; returns the bytes its marker declares (not the shortest encoding's), 0 if malformed or
    /// cut off by the text's end. Values of 2^31 and above come out negative.
    /// </summary>
    public static int ReadInteger(byte* p, out int value)
    {
        value = 0;
        var marker = p[0];
        if (marker == 0 || marker > 0xFE)
            return 0;

        if (marker < 0xF0)
        {
            value = marker - 1;
            return 1;
        }

        // The marker's low bits say which bytes of the value (highest first) follow; the others are zero. A zero byte
        // among them is where the text ends, so it is not read past.
        var flags = marker + 1;
        var n = 1;
        var result = 0u;
        for (var bit = 3; bit >= 0; bit--)
        {
            if ((flags & (1 << bit)) == 0)
                continue;
            var b = p[n];
            if (b == 0)
                return 0;
            result |= (uint)b << (8 * bit);
            n++;
        }

        value = unchecked((int)result);
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

        return p[total - 1] == MacroEnd ? (int)total : 0;
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
