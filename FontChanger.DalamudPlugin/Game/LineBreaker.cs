using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

using Dalamud.Hooking;

using FFXIVClientStructs.FFXIV.Component.GUI;

using FontChanger.DalamudPlugin.PluginInterface;
using FontChanger.DalamudPlugin.Rendering;
using FontChanger.DalamudPlugin.Rendering.Text;
using FontChanger.DalamudPlugin.Utilities;

using Lumina.Text.Payloads;
using Lumina.Text.ReadOnly;

using TerraFX.Interop.DirectX;

namespace FontChanger.DalamudPlugin.Game;

/// <summary>
/// Splits words that don't fit on a line where DirectWrite allows a line break, never inside a cluster.
/// </summary>
/// <remarks>
/// <para>The game wraps text once, when it is set (the text wrapper: the chat log, word-wrapped text nodes,
/// HandleFormatText). It cuts the text into words at separator bytes and puts each on the current line if it fits; a word
/// that doesn't goes to SplitWord, which returns how many of its units (characters and macros) stay on the line. That
/// takes as many as fit (FitUnits), then backs up to a break: anywhere between characters from U+2100 on (with the
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
    // Characters the game's word gathering puts for the non-breaking space and hyphen macros.
    private const byte NonBreakingSpaceChar = 0x1D;
    private const byte HyphenChar = 0x1E;

    private readonly FontReplacer _replacer;
    private readonly Hook<SplitWordDelegate> _splitWordHook;

    private readonly StringBuilder _text = new();
    private readonly List<Unit> _units = [];
    private bool[] _clusterEnd = new bool[64];
    private bool[] _wrapAfter = new bool[64];

    // The decision for the word being split, made at its first split.
    private bool _splittingOwnWord;

    // The format line breaks are found with: Segoe UI with the system's fallback (breaks don't depend on the font).
    private MyComPtr<IDWriteTextFormat3> _breakFormat;

    public LineBreaker(FontReplacer replacer)
    {
        this._replacer = replacer;
        try
        {
            var services = replacer.Services;
            using (var systemFallback = default(MyComPtr<IDWriteFontFallback>))
            using (var fallback = default(MyComPtr<IDWriteFontFallback1>))
            {
                services.Factory->GetSystemFontFallback(systemFallback.GetAddressOf()).ThrowOnError();
                systemFallback.CopyTo(fallback.GetAddressOf()).ThrowOnError();
                this._breakFormat = services.CreateFormat(SystemFallbackFonts.DefaultBaseFamily, 400, 0, 5, 16, fallback.Value);
            }

            var splitWord = AtkFontCodeModule.Addresses.SplitWord.Value;
            this._splitWordHook = Plugin.GameInterop.HookFromAddress<SplitWordDelegate>(splitWord, this.SplitWordDetour);
            this._splitWordHook.Enable();
        }
        catch
        {
            this.Dispose();
            throw;
        }
    }

    private delegate uint SplitWordDelegate(AtkFontCodeModule* wrapper, int width, int unused, byte* word, byte* measuredWord, byte lineStart);

    public void Dispose()
    {
        ((IDisposable?)this._splitWordHook)?.Dispose();
        this._breakFormat.Dispose();
    }

    /// <summary>
    /// Gets whether a word needs clusters or dictionary breaks: it has combining marks, conjoining jamo, joiners, or
    /// characters of a script written without spaces between words.
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
            if (GameUtf8.TryDecode(p, n, out var rune))
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
        this.GetBreaks(chars, this._clusterEnd.AsSpan(0, chars.Length + 1), this._wrapAfter.AsSpan(0, chars.Length + 1));

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
    /// Gets where a text may be split: <paramref name="clusterEnd"/>[i] is set when a cluster ends after the first i
    /// code units, and <paramref name="wrapAfter"/>[i] when a line may also break there (UAX #14, dictionary breaks for
    /// Thai and the like). Both spans are one longer than <paramref name="text"/>.
    /// </summary>
    private void GetBreaks(ReadOnlySpan<char> text, Span<bool> clusterEnd, Span<bool> wrapAfter)
    {
        clusterEnd.Clear();
        wrapAfter.Clear();
        if (text.IsEmpty)
            return;

        using var layout = this._replacer.Services.CreateLayout(text, this._breakFormat.Value);
        uint count;
        layout.Value->GetClusterMetrics(null, 0, &count);
        var metrics = count <= 256 ? stackalloc DWRITE_CLUSTER_METRICS[(int)count] : new DWRITE_CLUSTER_METRICS[count];
        fixed (DWRITE_CLUSTER_METRICS* m = metrics)
            layout.Value->GetClusterMetrics(m, count, &count).ThrowOnError();

        var position = 0;
        foreach (ref readonly var cluster in metrics)
        {
            position += cluster.length;
            clusterEnd[position] = true;
            wrapAfter[position] = cluster.canWrapLineAfter != 0;
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
