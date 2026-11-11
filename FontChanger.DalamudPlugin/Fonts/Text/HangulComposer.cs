using System.Collections.Generic;

namespace FontChanger.DalamudPlugin.Fonts.Text;

/// <summary>
/// Composes conjoining jamo into precomposed syllables as NFC does, since DirectWrite has no Hangul shaping and would
/// draw each jamo as a separate full-width letter. Old Hangul, having no precomposed syllables, is left as it is.
/// </summary>
internal static class HangulComposer
{
    private const int SyllableBase = 0xAC00, LeadBase = 0x1100, VowelBase = 0x1161, TrailBase = 0x11A7;
    private const int LeadCount = 19, VowelCount = 21, TrailCount = 28, SCount = LeadCount * VowelCount * TrailCount;

    /// <summary>Composes the first <paramref name="count"/> code units of <paramref name="text"/> in place.</summary>
    /// <param name="text">The UTF-16 text.</param>
    /// <param name="textToByte">Per code unit, its byte, which moves with it.</param>
    /// <param name="count">The code units to compose.</param>
    /// <param name="absorbed">Gets the bytes of the jamo composed into a syllable, with that syllable's byte.</param>
    /// <returns>The count of code units left.</returns>
    public static int Compose(char[] text, int[] textToByte, int count, List<(int Byte, int Owner)> absorbed)
    {
        var w = 0;
        for (var i = 0; i < count;)
        {
            int c = text[i];
            var n = 1;
            if (c - LeadBase is >= 0 and < LeadCount && i + 1 < count && text[i + 1] - VowelBase is >= 0 and < VowelCount)
            {
                c = SyllableBase + ((((c - LeadBase) * VowelCount) + (text[i + 1] - VowelBase)) * TrailCount);
                n = 2;
            }

            if ((n == 2 || (c - SyllableBase is >= 0 and < SCount && (c - SyllableBase) % TrailCount == 0)) &&
                i + n < count && text[i + n] - TrailBase is > 0 and < TrailCount)
            {
                c += text[i + n] - TrailBase;
                n++;
            }

            for (var j = 1; j < n; j++)
                absorbed.Add((textToByte[i + j], textToByte[i]));
            text[w] = (char)c;
            textToByte[w] = textToByte[i];
            w++;
            i += n;
        }

        return w;
    }
}
