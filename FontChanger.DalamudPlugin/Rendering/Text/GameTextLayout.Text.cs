using System;
using System.Text;

using FFXIVClientStructs.FFXIV.Component.GUI;

namespace FontChanger.DalamudPlugin.Rendering.Text;

// Inside the namespace, so that Pointer<T> is XIVCS's over TerraFX's.
using FFXIVClientStructs.Interop;

// The text of a run made ready to shape: Hangul composed, and each grapheme given its glyph source.
internal sealed unsafe partial class GameTextLayout
{
    private void Shape(FontReplacer.SizedFont sized, byte* p, int count, Pointer<AtkFontManager.Glyph>[] glyphs, ItalicMode italic)
    {
        this._absorbed.Clear();
        this._shapingBytes = p;
        this._shapingGlyphs = glyphs;
        this._shapingSized = sized;
        try
        {
            count = this.ComposeHangul(count);
            this.AssignSources(count);

            // Each code unit is in italics as its character's byte is (a low surrogate or control character as the one before).
            for (var t = 0; t < count; t++)
            {
                var b = this._textToByte[t];
                this._textItalic[t] = b >= 0 ? this._byteItalic[b] : t > 0 ? this._textItalic[t - 1] : italic != ItalicMode.Upright;
            }

            // A run in a node's italics is spaced to its upright width (or drawn per character at its upright places);
            // the others are shaped whole, in italics where their italic macros say.
            if (italic != ItalicMode.Images)
            {
                this.Collect(count, false);
            }
            else if (!this.CollectSpreadItalics(count))
            {
                this._textItalic.AsSpan(0, count).Clear();
                this.Collect(count, true);
            }

            this.PlaceClusters();
        }
        finally
        {
            this._shapingBytes = null;
            this._shapingGlyphs = null;
            this._shapingSized = null;
        }

        // A jamo composed into its syllable is part of the syllable's cluster.
        foreach (var (b, owner) in this._absorbed)
            glyphs[b] = glyphs[owner].IsNull ? default : this._emptyGlyph;
    }

    /// <summary>
    /// Composes conjoining jamo (L V, L V T, and a precomposed LV syllable followed by T) into precomposed syllables, as
    /// NFC does: DirectWrite has no Hangul shaping (it applies neither composition nor the fonts' ljmo/vjmo/tjmo), so it
    /// would draw each jamo as a separate full-width letter. The absorbed jamo's bytes are recorded with their syllable's.
    /// Old Hangul sequences, which have no precomposed syllable, are left as they are.
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

    /// <summary>
    /// Finds the glyph source of each grapheme of the text (all its code units get it), and puts in the characters an
    /// element draws with another's glyph (its codepoint replacements), where the UTF-16 length allows.
    /// </summary>
    private void AssignSources(int count)
    {
        Span<Rune> runes = stackalloc Rune[MaxGraphemeRunes];
        for (var t = 0; t < count;)
        {
            var length = this.NextGrapheme(t, count);
            var n = 0;
            for (var u = t; u < t + length && n < MaxGraphemeRunes;)
            {
                if (Rune.DecodeFromUtf16(this._text.AsSpan(u, t + length - u), out var rune, out var used) != System.Buffers.OperationStatus.Done)
                    (rune, used) = (Rune.ReplacementChar, 1);
                runes[n++] = rune;
                u += used;
            }

            var source = this._shapingSized!.Pick(runes[..n]);
            if (source.Element is { DrawsGame: false } element)
            {
                for (var u = t; u < t + length;)
                {
                    if (Rune.DecodeFromUtf16(this._text.AsSpan(u, t + length - u), out var rune, out var used) != System.Buffers.OperationStatus.Done)
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
