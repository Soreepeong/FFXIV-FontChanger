using System;
using System.Collections.Generic;
using System.Text;

using Dalamud.Hooking;

using FFXIVClientStructs.FFXIV.Component.GUI;

using FontChanger.DalamudPlugin.PluginInterface;
using FontChanger.DalamudPlugin.Rendering;
using FontChanger.DalamudPlugin.Utilities;
using FontChanger.DalamudPlugin.Utilities.Unicode;

using Lumina.Text;
using Lumina.Text.Payloads;
using Lumina.Text.ReadOnly;

namespace FontChanger.DalamudPlugin.Game;

/// <summary>Splits words that don't fit on a line where UAX #14 allows a line break, but never inside a grapheme cluster.</summary>
internal sealed unsafe class LineBreaker : IDisposable
{
    /// <summary>What <see cref="MacroCode.NonBreakingSpace"/> gets transformed to.</summary>
    private const byte NonBreakingSpaceChar = 0x1D;

    /// <summary>What <see cref="MacroCode.Hyphen"/> gets transformed to.</summary>
    private const byte HyphenChar = 0x1E;

    private readonly FontReplacer _replacer;
    private readonly Hook<SplitWordDelegate> _splitWordHook;

    private readonly StringBuilder _text = new();
    private readonly List<Unit> _units = [];
    private bool[] _clusterEnd = new bool[64];
    private bool[] _wrapAfter = new bool[64];

    // The decision for the word being split, made at its first split.
    private bool _splittingOwnWord;

    public LineBreaker(FontReplacer replacer)
    {
        this._replacer = replacer;
        this._splitWordHook = Plugin.GameInterop.HookFromAddress<SplitWordDelegate>(AtkFontCodeModule.Addresses.SplitWord.Value, this.SplitWordDetour);
        this._splitWordHook.Enable();
    }

    private delegate uint SplitWordDelegate(AtkFontCodeModule* wrapper, int width, int unused, byte* word, byte* measuredWord, byte lineStart);

    public void Dispose() => this._splitWordHook.Dispose();

    /// <summary>
    /// Gets whether a word needs clusters or dictionary breaks: whether it has a character that can join a cluster with
    /// one next to it (marks, joiners, jamo, regional indicators, and the like), or one of a script written without spaces
    /// between words (line break class SA).
    /// </summary>
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

            // The game's own split can take over a word only at its first split; later ones index the table the game
            // built at the first. Past that, a forced split at the fit keeps the line filled.
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

        // The last break opportunity among the units that fit. A break right after a unit is at the end of its text; a
        // macro has none of its own.
        var best = 0;
        for (var k = 1; k <= fit; k++)
        {
            if (EndsAt(this._wrapAfter, k))
                best = k;
        }

        // At the start of a line something has to go on it: the whole clusters that fit, or the first cluster.
        if (best == 0 && lineStart)
        {
            for (var k = 1; k <= fit; k++)
            {
                if (EndsAt(this._clusterEnd, k))
                    best = k;
            }

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

        bool EndsAt(bool[] breaks, int k) => this._units[k - 1].TextEnd is var end and > 0 && breaks[end];
    }

    /// <summary>
    /// Gets where a text may be split: <paramref name="clusterEnd"/>[i] is set when a cluster (UAX #29) ends after the
    /// first i code units, and <paramref name="wrapAfter"/>[i] when a line may also break there (UAX #14, dictionary
    /// breaks for Thai and the like). Both spans are one longer than <paramref name="text"/>.
    /// </summary>
    private static void GetBreaks(ReadOnlySpan<char> text, Span<bool> clusterEnd, Span<bool> wrapAfter)
    {
        clusterEnd.Clear();
        wrapAfter.Clear();
        if (text.IsEmpty)
            return;

        // The text as UTF-8, which the segmenters read, and the code unit each of its bytes starts.
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
        foreach (var offset in new UnicodeGraphemeClusterEnumerator(bytes, UtfEnumeratorFlags.Utf8))
            clusterEnd[unitAt[offset]] = true;

        // One inside a cluster isn't taken.
        foreach (var item in new UnicodeLineBreakEnumerator(bytes, UtfEnumeratorFlags.Utf8))
        {
            var at = unitAt[item.ByteOffset];
            if (clusterEnd[at])
                wrapAfter[at] = true;
        }
    }

    /// <summary>
    /// Reads a word into units as the game counts them: a character, or a macro. Characters go to the UTF-16 text, icon
    /// macros as an object replacement character (they take room like one), other macros as nothing.
    /// </summary>
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

    /// <summary>A unit of a word: whether it is a macro taking no room, and where its text ends in the UTF-16 text.</summary>
    private readonly record struct Unit(bool IsMacro, int TextEnd);
}
