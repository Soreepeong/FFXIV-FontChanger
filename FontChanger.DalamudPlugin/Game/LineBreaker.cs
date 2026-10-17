using System;
using System.Collections.Generic;
using System.Text;

using Dalamud.Hooking;

using FFXIVClientStructs.FFXIV.Component.GUI;

using FontChanger.DalamudPlugin.Fonts;
using FontChanger.DalamudPlugin.PluginInterface;
using FontChanger.DalamudPlugin.Utilities;
using FontChanger.DalamudPlugin.Utilities.Unicode;

using Lumina.Text;
using Lumina.Text.Payloads;
using Lumina.Text.ReadOnly;

namespace FontChanger.DalamudPlugin.Game;

internal sealed unsafe class LineBreaker : IDisposable
{
    // The game turns the NonBreakingSpace and Hyphen macros into these bytes in the words it splits.
    private const byte NonBreakingSpaceChar = 0x1D;
    private const byte HyphenChar = 0x1E;

    private readonly FontReplacer _replacer;
    private readonly Hook<SplitWordDelegate> _splitWordHook;

    private readonly StringBuilder _text = new();
    private readonly List<Unit> _units = [];
    private bool[] _clusterEnd = new bool[64];
    private bool[] _wrapAfter = new bool[64];

    // Decided at a word's first split, as its later splits can't switch to the game's.
    private bool _splittingOwnWord;

    public LineBreaker(FontReplacer replacer)
    {
        this._replacer = replacer;
        this._splitWordHook = Plugin.GameInterop.HookFromAddress<SplitWordDelegate>(AtkFontCodeModule.Addresses.SplitWord.Value, this.SplitWordDetour);
        this._splitWordHook.Enable();
    }

    private delegate uint SplitWordDelegate(AtkFontCodeModule* wrapper, int width, int unused, byte* word, byte* measuredWord, byte lineStart);

    public void Dispose() => this._splitWordHook.Dispose();

    // Only words with characters that join clusters or need dictionary breaks (class SA) are split here; the game splits the rest.
    private static bool NeedsClusters(byte* p)
    {
        while (*p != 0)
        {
            if (*p == ReadOnlySeString.Stx)
            {
                var length = GameUtf8.MacroLength(p);
                if (length == 0)
                    return false;
                p += length;
                continue;
            }

            var n = GameUtf8.CharacterLength(p);
            if (n == 0)
                return false;
            if (GameUtf8.TryDecode(p, n, out var rune) &&
                (UnicodeData.GraphemeClusterBreak[rune.Value] is not (
                     UnicodeGraphemeClusterBreak.Other
                     or UnicodeGraphemeClusterBreak.CR
                     or UnicodeGraphemeClusterBreak.LF
                     or UnicodeGraphemeClusterBreak.Control)
                 || UnicodeData.LineBreak[rune.Value] == UnicodeLineBreakClass.SA))
                return true;

            p += n;
        }

        return false;
    }

    private uint SplitWordDetour(AtkFontCodeModule* wrapper, int width, int unused, byte* word, byte* measuredWord, byte lineStart)
    {
        var first = wrapper->ConsumedUnits == 0;
        try
        {
            if (first)
                this._splittingOwnWord = this._replacer.Enabled && NeedsClusters(word);
            if (this._splittingOwnWord)
                return this.Split(wrapper, width, word, measuredWord, lineStart != 0);
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Splitting a word failed");

            // The game's split can take over only at a word's first split, as later ones index the table it built then.
            if (!first)
            {
                var fit = wrapper->FitUnits(measuredWord, width, wrapper->MaxUnits - wrapper->UsedUnits, null);
                return fit <= 0 ? lineStart != 0 ? 1u : 0u : (uint)fit;
            }

            this._splittingOwnWord = false;
        }

        return this._splitWordHook.Original(wrapper, width, unused, word, measuredWord, lineStart);
    }

    private uint Split(AtkFontCodeModule* wrapper, int width, byte* word, byte* measuredWord, bool lineStart)
    {
        var fit = wrapper->FitUnits(measuredWord, width, wrapper->MaxUnits - wrapper->UsedUnits, null);
        if (fit < 0)
            return 0;

        this.Decode(word);
        var count = this._units.Count;
        if (fit >= count)
            return (uint)count;

        if (this._clusterEnd.Length <= this._text.Length)
        {
            this._clusterEnd = new bool[this._text.Length * 2];
            this._wrapAfter = new bool[this._text.Length * 2];
        }

        var chars = this._text.Length <= 512 ? stackalloc char[this._text.Length] : new char[this._text.Length];
        this._text.CopyTo(0, chars, chars.Length);
        GetBreaks(chars, this._clusterEnd.AsSpan(0, chars.Length + 1), this._wrapAfter.AsSpan(0, chars.Length + 1));

        var best = LastEndAtOrBefore(this._wrapAfter, fit);

        // At the start of a line something has to go on it: the whole clusters that fit, or the first cluster.
        if (best == 0 && lineStart)
        {
            best = LastEndAtOrBefore(this._clusterEnd, fit);
            for (var k = fit + 1; best == 0 && k <= count; k++)
            {
                if (EndsAt(this._clusterEnd, k))
                    best = k;
            }

            if (best == 0)
                best = count;
        }

        // Macros (colors and the like) right after the break stay with the line they follow, as in the game's split.
        while (best > 0 && best < count && this._units[best].IsMacro)
            best++;

        return (uint)best;

        int LastEndAtOrBefore(bool[] breaks, int last)
        {
            for (var k = last; k > 0; k--)
            {
                if (EndsAt(breaks, k))
                    return k;
            }

            return 0;
        }

        // A macro has no text of its own, so a break after it is at the end of the text before it.
        bool EndsAt(bool[] breaks, int k) => this._units[k - 1].TextEnd is var end and > 0 && breaks[end];
    }

    // Index i of either span is the boundary after the first i UTF-16 code units, so both are one longer than the text.
    private static void GetBreaks(ReadOnlySpan<char> text, Span<bool> clusterEnd, Span<bool> wrapAfter)
    {
        clusterEnd.Clear();
        wrapAfter.Clear();
        if (text.IsEmpty)
            return;

        // The segmenters read UTF-8, so each byte is mapped back to the UTF-16 code unit it belongs to.
        var byteCount = Encoding.UTF8.GetByteCount(text);
        var bytes = byteCount <= 1024 ? stackalloc byte[byteCount] : new byte[byteCount];
        Encoding.UTF8.GetBytes(text, bytes);
        var unitAt = byteCount < 1024 ? stackalloc int[byteCount + 1] : new int[byteCount + 1];
        for (int b = 0, u = 0; u < text.Length;)
        {
            Rune.DecodeFromUtf16(text[u..], out var rune, out var units);
            unitAt.Slice(b, rune.Utf8SequenceLength).Fill(u);
            b += rune.Utf8SequenceLength;
            u += units;
        }

        unitAt[byteCount] = text.Length;

        // A boundary comes before the byte it is at.
        foreach (var offset in new UnicodeGraphemeClusterEnumerator(bytes))
            clusterEnd[unitAt[offset]] = true;

        foreach (var bp in new UnicodeLineBreakEnumerator(bytes))
        {
            var at = unitAt[bp.ByteOffset];
            if (clusterEnd[at])
                wrapAfter[at] = true;
        }
    }

    // Units are counted as the game does (a character or a macro); icon macros take room, so they become U+FFFC in the text.
    private void Decode(byte* p)
    {
        this._units.Clear();
        this._text.Clear();
        Span<char> utf16 = stackalloc char[2];
        while (*p != 0)
        {
            if (*p == ReadOnlySeString.Stx)
            {
                var length = GameUtf8.MacroLength(p);
                if (length == 0)
                    break;
                var icon = (MacroCode)p[1] is MacroCode.Icon or MacroCode.Icon2;
                if (icon)
                    this._text.Append('￼');
                this._units.Add(new(!icon, this._text.Length));
                p += length;
                continue;
            }

            var n = GameUtf8.CharacterLength(p);
            if (n == 0)
                break;
            var rune = *p switch
            {
                NonBreakingSpaceChar => new(0xA0),
                HyphenChar => new('-'),
                _ => GameUtf8.TryDecode(p, n, out var r) ? r : Rune.ReplacementChar,
            };

            this._text.Append(utf16[..rune.EncodeToUtf16(utf16)]);
            this._units.Add(new(false, this._text.Length));
            p += n;
        }
    }

    // IsMacro is set only for macros that take no room; TextEnd indexes the UTF-16 text.
    private readonly record struct Unit(bool IsMacro, int TextEnd);
}
