using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

using Dalamud.Hooking;

namespace CustomFonts;

/// <summary>
/// Splits words that don't fit on a line where DirectWrite allows a line break, never inside a cluster.
/// </summary>
/// <remarks>
/// <para>The game wraps text once, when it is set (FUN_1406581A0: the chat log, word-wrapped text nodes,
/// HandleFormatText). It cuts the text into words at separator bytes and puts each on the current line if it fits; a word
/// that doesn't goes to FUN_14065B090, which returns how many of its units (characters and macros) stay on the line. That
/// takes as many as fit (FUN_14065AE60), then backs up to a break: anywhere between characters from U+2100 on (with the
/// game's own rules for CJK punctuation), after listed punctuation, or else, at the start of a line only, right at the
/// fit. Such a forced break can fall inside a cluster (a letter and its accent, a Thai consonant and its vowel, an Indic
/// conjunct), and Thai, written without spaces, has no breaks at all.</para>
/// <para>For a word with marks or such a script, the split is chosen here instead: the last DirectWrite line break
/// opportunity (UAX #14, with dictionary breaks for Thai and the like) among the units that fit; at the start of a line,
/// failing that, the last whole cluster that fits, or one cluster. Other words keep the game's rules. The game builds a
/// per-word table on a word's first split and indexes it on the later ones, so a word is decided once, at its first
/// split, and keeps that decision.</para>
/// </remarks>
internal sealed unsafe class LineBreaker : IDisposable
{
    // Macros that the game counts as characters (icons).
    private const byte IconMacro = 0x12;
    private const byte Icon2Macro = 0x1E;

    // Characters the game's word gathering puts for the non-breaking space and hyphen macros.
    private const byte NonBreakingSpaceChar = 0x1D;
    private const byte HyphenChar = 0x1E;

    private readonly FontReplacer replacer;
    private readonly Hook<SplitWordDelegate> splitWordHook;
    private readonly delegate* unmanaged<nint, byte*, int, int, int*, int> fitUnits;

    // Wrapper: the units of the current word already put on earlier lines (0 on its first split), and the unit limit.
    private readonly int consumedUnitsOffset;
    private readonly int maxUnitsOffset;
    private readonly int usedUnitsOffset;

    private readonly StringBuilder text = new();
    private readonly List<Unit> units = [];
    private bool[] clusterEnd = new bool[64];
    private bool[] wrapAfter = new bool[64];

    // The decision for the word being split, made at its first split.
    private bool splittingOwnWord;

    public LineBreaker(FontReplacer replacer)
    {
        this.replacer = replacer;
        try
        {
            // uint SplitWord(Wrapper* this, int width, int unused, byte* word, byte* measuredWord, bool lineStart), with
            // its call to int FitUnits(Wrapper* this, byte* measuredWord, int width, int maxUnits, int* bytes).
            var m = CodeSignature.Find("SplitWord");
            this.fitUnits = (delegate* unmanaged<nint, byte*, int, int, int*, int>)m.Target("FitUnits");
            this.consumedUnitsOffset = m.Int("Wrapper.ConsumedUnits");
            this.maxUnitsOffset = m.Int("Wrapper.MaxUnits");
            this.usedUnitsOffset = m.Int("Wrapper.UsedUnits");
            this.splitWordHook = Plugin.GameInterop.HookFromAddress<SplitWordDelegate>(m.Address, this.SplitWordDetour);
            this.splitWordHook.Enable();
        }
        catch
        {
            this.Dispose();
            throw;
        }
    }

    private delegate uint SplitWordDelegate(nint wrapper, int width, int unused, byte* word, byte* measuredWord, byte lineStart);

    public void Dispose() => this.splitWordHook?.Dispose();

    /// <summary>
    /// Gets whether a word needs clusters or dictionary breaks: it has combining marks, conjoining jamo, joiners, or
    /// characters of a script written without spaces between words.
    /// </summary>
    private static bool NeedsClusters(byte* p)
    {
        while (*p != 0)
        {
            if (*p == 0x02)
            {
                var length = MacroLength(p);
                if (length == 0)
                    return false;
                p += length;
                continue;
            }

            var n = SequenceLength(p);
            if (n == 0)
                return false;
            if (Rune.DecodeFromUtf8(new ReadOnlySpan<byte>(p, n), out var rune, out _) == System.Buffers.OperationStatus.Done)
            {
                var c = rune.Value;
                if (Rune.GetUnicodeCategory(rune) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark
                        or UnicodeCategory.EnclosingMark ||
                    c is >= 0x0E00 and <= 0x0FFF // Thai, Lao, Tibetan
                        or >= 0x1000 and <= 0x109F // Myanmar
                        or >= 0x1100 and <= 0x11FF // Hangul jamo
                        or >= 0x1780 and <= 0x17FF // Khmer
                        or 0x200C or 0x200D // joiners
                        or >= 0xA960 and <= 0xA97F or >= 0xD7B0 and <= 0xD7FF // Hangul jamo extended
                        or >= 0x1F1E6 and <= 0x1F1FF) // regional indicators
                {
                    return true;
                }
            }

            p += n;
        }

        return false;
    }

    /// <summary>Gets the length of the character at <paramref name="p"/> as the game steps; 0 if it runs into the end of the text.</summary>
    private static int SequenceLength(byte* p)
    {
        var n = GameUtf8.SequenceLength(*p);
        for (var i = 1; i < n; i++)
        {
            if (p[i] == 0)
                return 0;
        }

        return n;
    }

    /// <summary>Gets the length of the macro at <paramref name="p"/>; 0 if it is malformed.</summary>
    private static int MacroLength(byte* p)
    {
        if (p[1] == 0)
            return 0;
        var n = TextShaper.ReadInteger(p + 2, out var payload);
        if (n == 0 || payload < 0)
            return 0;
        var total = 2 + n + payload + 1;
        for (var i = 2; i < total - 1; i++)
        {
            if (p[i] == 0)
                return 0;
        }

        return p[total - 1] == 0x03 ? total : 0;
    }

    private uint SplitWordDetour(nint wrapper, int width, int unused, byte* word, byte* measuredWord, byte lineStart)
    {
        var first = *(int*)(wrapper + this.consumedUnitsOffset) == 0;
        try
        {
            if (first)
                this.splittingOwnWord = this.replacer.Enabled && NeedsClusters(word);
            if (this.splittingOwnWord)
                return this.Split(wrapper, width, word, measuredWord, lineStart != 0);
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Splitting a word failed");

            // The game's own split can take over a word only at its first split; later ones index the table the game
            // built at the first. Past that, a forced split at the fit keeps the line filled.
            if (!first)
            {
                var fit = this.fitUnits(wrapper, measuredWord, width, *(int*)(wrapper + this.maxUnitsOffset) - *(int*)(wrapper + this.usedUnitsOffset), null);
                return fit <= 0 ? lineStart != 0 ? 1u : 0u : (uint)fit;
            }

            this.splittingOwnWord = false;
        }

        return this.splitWordHook.Original(wrapper, width, unused, word, measuredWord, lineStart);
    }

    private uint Split(nint wrapper, int width, byte* word, byte* measuredWord, bool lineStart)
    {
        var fit = this.fitUnits(wrapper, measuredWord, width, *(int*)(wrapper + this.maxUnitsOffset) - *(int*)(wrapper + this.usedUnitsOffset), null);
        if (fit < 0)
            return 0;

        this.Decode(word);
        var count = this.units.Count;
        if (fit >= count)
            return (uint)count;

        if (this.clusterEnd.Length <= this.text.Length)
        {
            this.clusterEnd = new bool[this.text.Length * 2];
            this.wrapAfter = new bool[this.text.Length * 2];
        }

        var chars = this.text.Length <= 512 ? stackalloc char[this.text.Length] : new char[this.text.Length];
        this.text.CopyTo(0, chars, chars.Length);
        this.replacer.Shaper.GetBreaks(
            chars,
            this.clusterEnd.AsSpan(0, chars.Length + 1),
            this.wrapAfter.AsSpan(0, chars.Length + 1));

        // The last break opportunity among the units that fit. A break right after a unit is at the end of its text; a
        // macro has none of its own.
        var best = 0;
        for (var k = 1; k <= fit; k++)
        {
            var end = this.units[k - 1].TextEnd;
            if (end > 0 && this.wrapAfter[end])
                best = k;
        }

        // At the start of a line something has to go on it: the whole clusters that fit, or the first cluster.
        if (best == 0 && lineStart)
        {
            for (var k = 1; k <= fit; k++)
            {
                var end = this.units[k - 1].TextEnd;
                if (end > 0 && this.clusterEnd[end])
                    best = k;
            }

            for (var k = fit + 1; best == 0 && k <= count; k++)
            {
                var end = this.units[k - 1].TextEnd;
                if (end > 0 && this.clusterEnd[end])
                    best = k;
            }

            if (best == 0)
                best = count;
        }

        // Macros (colours and the like) right after the break stay with the line they follow, as in the game's split.
        while (best > 0 && best < count && this.units[best].IsMacro)
            best++;

        return (uint)best;
    }

    /// <summary>
    /// Reads a word into units as the game counts them: a character, or a macro. Characters go to the UTF-16 text, icon
    /// macros as an object replacement character (they take room like one), other macros as nothing.
    /// </summary>
    private void Decode(byte* p)
    {
        this.units.Clear();
        this.text.Clear();
        Span<char> utf16 = stackalloc char[2];
        while (*p != 0)
        {
            if (*p == 0x02)
            {
                var length = MacroLength(p);
                if (length == 0)
                    break;
                var icon = p[1] is IconMacro or Icon2Macro;
                if (icon)
                    this.text.Append('￼');
                this.units.Add(new(!icon, this.text.Length));
                p += length;
                continue;
            }

            var n = SequenceLength(p);
            if (n == 0)
                break;
            var rune = *p switch
            {
                NonBreakingSpaceChar => new Rune(0xA0),
                HyphenChar => new Rune('-'),
                _ => Rune.DecodeFromUtf8(new ReadOnlySpan<byte>(p, n), out var r, out var consumed) ==
                     System.Buffers.OperationStatus.Done && consumed == n
                    ? r
                    : Rune.ReplacementChar,
            };

            this.text.Append(utf16[..rune.EncodeToUtf16(utf16)]);
            this.units.Add(new(false, this.text.Length));
            p += n;
        }
    }

    /// <summary>A unit of a word: whether it is a macro taking no room, and where its text ends in the UTF-16 text.</summary>
    private readonly record struct Unit(bool IsMacro, int TextEnd);
}
