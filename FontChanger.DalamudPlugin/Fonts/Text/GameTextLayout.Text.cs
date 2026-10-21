using System;
using System.Buffers;
using System.Text;

using FFXIVClientStructs.FFXIV.Component.GUI;
using FFXIVClientStructs.Interop;

using FontChanger.DalamudPlugin.Fonts.Enums;
using FontChanger.DalamudPlugin.Utilities;

using Lumina.Text.Payloads;
using Lumina.Text.ReadOnly;

namespace FontChanger.DalamudPlugin.Fonts.Text;

internal sealed unsafe partial class GameTextLayout
{
    /// <summary>Returns the run's count of UTF-16 code units, and its length in bytes in <paramref name="length"/>.</summary>
    private int ReadRun(byte* p, ItalicMode italic, out int length)
    {
        var count = 0;
        var i = 0;
        var inItalic = italic != ItalicMode.Upright;
        while (i < MaxRunBytes)
        {
            var b = p[i];
            if (b is 0 or 0x0A or 0x0D)
                break;

            if (b == ReadOnlySeString.Stx)
            {
                // Bold is laid out with another sized font.
                var macro = (MacroCode)p[i + 1];
                if (macro is MacroCode.NewLine or MacroCode.Bold || (macro == MacroCode.Italic && italic == ItalicMode.Images))
                    break;
                var total = GameUtf8.MacroLength(p + i);
                if (total == 0 || i + total > MaxRunBytes)
                    break;

                // <italic>'s argument must be a plain integer (1 on, 0 off); anything else ends the run.
                if (macro == MacroCode.Italic)
                {
                    if (!GameUtf8.TryReadInteger(p + i + 2, out var payload, out var n)
                        || !GameUtf8.TryReadInteger(p + i + 2 + n, out var on, out var onLength) || onLength != payload)
                        break;
                    inItalic = on != 0;
                }

                i += total;
                continue;
            }

            this._byteItalic[i] = inItalic;

            // Stepped as the game does.
            var charLength = GameUtf8.CharacterLength(p + i);
            if (charLength == 0 || i + charLength > MaxRunBytes)
                break;

            // Control characters and broken sequences shape as a replacement character, which keeps them per character.
            var r = Rune.ReplacementChar;
            var valid = b >= 0x20 && GameUtf8.TryDecode(p + i, charLength, out r);
            if (!valid)
                r = Rune.ReplacementChar;
            this.EnsureCapacity(count + 2);
            this._textToByte[count] = valid ? i : -1;
            var units = r.EncodeToUtf16(this._text.AsSpan(count));
            if (units == 2)
                this._textToByte[count + 1] = -1;
            count += units;
            i += charLength;
        }

        length = i;
        return count;
    }

    private void Shape(SizedFont sized, byte* p, int count, Pointer<AtkFontManager.Glyph>[] glyphs, ItalicMode italic)
    {
        this._absorbed.Clear();
        this._shapingSized = sized;
        try
        {
            count = this.ComposeHangul(count);
            this.AssignSources(count);

            if (italic != ItalicMode.Images)
            {
                // A code unit with no byte of its own is in the italics of the one before it.
                for (var t = 0; t < count; t++)
                {
                    var b = this._textToByte[t];
                    this._textItalic[t] = b >= 0 ? this._byteItalic[b] : t > 0 ? this._textItalic[t - 1] : italic != ItalicMode.Upright;
                }

                this.Collect(count, false);
            }
            else if (!this.CollectSpreadItalics(count))
            {
                this._textItalic.AsSpan(0, count).Clear();
                this.Collect(count, true);
            }

            this.PlaceClusters(p, glyphs);
        }
        finally
        {
            this._shapingSized = null;
        }

        foreach (var (b, owner) in this._absorbed)
            glyphs[b] = glyphs[owner].IsNull ? default : this._emptyGlyph;
    }

    /// <summary>
    /// Composes conjoining jamo into precomposed syllables as NFC does, since DirectWrite has no Hangul shaping and would
    /// draw each jamo as a separate full-width letter; Old Hangul, having no precomposed syllables, is left as it is.
    /// </summary>
    private int ComposeHangul(int count)
    {
        const int SyllableBase = 0xAC00, LeadBase = 0x1100, VowelBase = 0x1161, TrailBase = 0x11A7;
        const int LeadCount = 19, VowelCount = 21, TrailCount = 28, SCount = LeadCount * VowelCount * TrailCount;

        var w = 0;
        for (var i = 0; i < count;)
        {
            int c = this._text[i];
            var n = 1;
            if (c - LeadBase is >= 0 and < LeadCount && i + 1 < count && this._text[i + 1] - VowelBase is >= 0 and < VowelCount)
            {
                c = SyllableBase + ((((c - LeadBase) * VowelCount) + (this._text[i + 1] - VowelBase)) * TrailCount);
                n = 2;
            }

            if ((n == 2 || (c - SyllableBase is >= 0 and < SCount && (c - SyllableBase) % TrailCount == 0)) &&
                i + n < count && this._text[i + n] - TrailBase is > 0 and < TrailCount)
            {
                c += this._text[i + n] - TrailBase;
                n++;
            }

            for (var j = 1; j < n; j++)
                this._absorbed.Add((this._textToByte[i + j], this._textToByte[i]));
            this._text[w] = (char)c;
            this._textToByte[w] = this._textToByte[i];
            w++;
            i += n;
        }

        return w;
    }

    private void AssignSources(int count)
    {
        Span<Rune> runes = stackalloc Rune[MaxGraphemeRunes];
        for (var t = 0; t < count;)
        {
            var length = this.NextGrapheme(t, count);
            var n = 0;
            for (var u = t; u < t + length && n < MaxGraphemeRunes;)
            {
                if (Rune.DecodeFromUtf16(this._text.AsSpan(u, t + length - u), out var rune, out var used) != OperationStatus.Done)
                    (rune, used) = (Rune.ReplacementChar, 1);
                runes[n++] = rune;
                u += used;
            }

            var source = this._shapingSized!.Pick(runes[..n]);

            // Codepoint replacements are written into the text in place, so only where the UTF-16 length stays the same.
            if (source.Element is { DrawsGame: false } element)
            {
                for (var u = t; u < t + length;)
                {
                    if (Rune.DecodeFromUtf16(this._text.AsSpan(u, t + length - u), out var rune, out var used) != OperationStatus.Done)
                    {
                        u++;
                        continue;
                    }

                    var drawn = element.GetDrawnCodepoint(rune);
                    if (drawn != rune && drawn.Utf16SequenceLength == used)
                        drawn.EncodeToUtf16(this._text.AsSpan(u, used));
                    u += used;
                }
            }

            this._textSource.AsSpan(t, length).Fill(source);
            t += length;
        }
    }
}
