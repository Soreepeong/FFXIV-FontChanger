using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Unicode;

using FontChanger.DalamudPlugin.Rendering.Faces;
using FontChanger.DalamudPlugin.Utilities;
using FontChanger.Presets.Records;

using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;

namespace FontChanger.DalamudPlugin.Rendering.Text;

/// <summary>
/// Cuts a line into runs of one script and direction: the scripts found with DirectWrite (AnalyzeScript), for the
/// shaping that pairs with it, and right to left by the character's block, as the game draws only left to right and
/// such runs are left to it.
/// </summary>
internal sealed unsafe class DirectWriteTextAnalyzer(FontServices services)
{
    private DWRITE_SCRIPT_ANALYSIS[] _scripts = new DWRITE_SCRIPT_ANALYSIS[256];

    /// <summary>Gets the runs of a text, in order, covering all of it.</summary>
    public void Itemize(ReadOnlySpan<char> text, List<TextRun> runs)
    {
        runs.Clear();
        var length = text.Length;
        if (length == 0)
            return;
        if (this._scripts.Length < length)
            this._scripts = new DWRITE_SCRIPT_ANALYSIS[Math.Max(length, this._scripts.Length * 2)];

        fixed (char* t = text)
        fixed (DWRITE_SCRIPT_ANALYSIS* scripts = this._scripts)
        {
            var source = new TextAnalysisSource(t, (uint)length);
            var sink = new ScriptSink(scripts, (uint)length);
            services.Analyzer->AnalyzeScript(source.Get(), 0, (uint)length, sink.Get()).ThrowOnError();
        }

        var start = 0;
        var rtl = false;
        for (var i = 0; i < length; i++)
        {
            // A low surrogate goes with its high one.
            var unitRtl = char.IsLowSurrogate(text[i]) && i > 0 ? rtl : IsRightToLeft(text, i);
            if (i != 0 && (unitRtl != rtl || ToToken(this._scripts[i]) != ToToken(this._scripts[start])))
            {
                runs.Add(new(start, i, ToToken(this._scripts[start]), rtl));
                start = i;
            }

            rtl = unitRtl;
        }

        runs.Add(new(start, length, ToToken(this._scripts[start]), rtl));
    }

    private static ScriptToken ToToken(DWRITE_SCRIPT_ANALYSIS a) => new(a.script, (int)a.shapes);

    /// <summary>
    /// The blocks of scripts written right to left. UnicodeRanges has the BMP's; Alphabetic Presentation Forms only from
    /// its Hebrew ones (before them are Latin and Armenian ligatures), and those of the supplementary planes (Phoenician,
    /// Kharoshthi, Adlam, ...) by their areas.
    /// </summary>
    private static readonly RuneRange[] RightToLeftRanges = RuneRangeExtensions.Coalesce(
    [
        UnicodeRanges.Hebrew,
        UnicodeRanges.Arabic,
        UnicodeRanges.Syriac,
        UnicodeRanges.ArabicSupplement,
        UnicodeRanges.Thaana,
        UnicodeRanges.NKo,
        UnicodeRanges.Samaritan,
        UnicodeRanges.Mandaic,
        UnicodeRanges.SyriacSupplement,
        UnicodeRanges.ArabicExtendedB,
        UnicodeRanges.ArabicExtendedA,
        UnicodeRanges.ArabicPresentationFormsA,
        UnicodeRanges.ArabicPresentationFormsB,

        // Hebrew part of Alphabetic Presentation Forms
        .. RuneRange.From(0xFB1D, 0xFB4F),

        // Cypriot Syllabary, Imperial Aramaic, Palmyrene, Nabataean, Hatran, Phoenician, Lydian, Sidetic, Meroitic
        // Hieroglyphs, Meroitic Cursive, Kharoshthi, Old South Arabian, Old North Arabian, Manichaean, Avestan,
        // Inscriptional Parthian, Inscriptional Pahlavi, Psalter Pahlavi, Old Turkic, Old Hungarian, Hanifi Rohingya,
        // Garay, Rumi Numeral Symbols, Yezidi, Arabic Extended-C, Old Sogdian, Sogdian, Old Uyghur, Chorasmian, Elymaic
        .. RuneRange.From(0x10800, 0x10FFF),

        // Mende Kikakui, Adlam, Indic Siyaq Numbers, Ottoman Siyaq Numbers, Arabic Mathematical Alphabetic Symbols
        .. RuneRange.From(0x1E800, 0x1EFFF),
    ]).ToArray();

    /// <summary>Gets whether the character at a code unit is of a right-to-left block (Hebrew, Arabic, Syriac, Thaana, ...).</summary>
    private static bool IsRightToLeft(ReadOnlySpan<char> text, int index)
    {
        if (Rune.DecodeFromUtf16(text[index..], out var rune, out _) != OperationStatus.Done)
            return false;
        foreach (var range in RightToLeftRanges)
        {
            if (rune >= range.First && rune <= range.Last)
                return true;
        }

        return false;
    }

    /// <summary>An IDWriteTextAnalysisSink that keeps the script of each code unit. It lives where it is declared.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct ScriptSink
    {
        private void* _vtable;
        private delegate* unmanaged<void*, Guid*, void**, int> _queryInterface;
        private delegate* unmanaged<void*, uint> _addRef;
        private delegate* unmanaged<void*, uint> _release;
        private delegate* unmanaged<void*, uint, uint, DWRITE_SCRIPT_ANALYSIS*, int> _setScriptAnalysis;
        private delegate* unmanaged<void*, uint, uint, DWRITE_LINE_BREAKPOINT*, int> _setLineBreakpoints;
        private delegate* unmanaged<void*, uint, uint, byte, byte, int> _setBidiLevel;
        private delegate* unmanaged<void*, uint, uint, IDWriteNumberSubstitution*, int> _setNumberSubstitution;
        private DWRITE_SCRIPT_ANALYSIS* _scripts;
        private uint _length;

        public ScriptSink(DWRITE_SCRIPT_ANALYSIS* scripts, uint length)
        {
            this._queryInterface = &QueryInterfaceImpl;
            this._addRef = &TextAnalysisSource.AddRefImpl;
            this._release = &TextAnalysisSource.ReleaseImpl;
            this._setScriptAnalysis = &SetScriptAnalysisImpl;
            this._setLineBreakpoints = &SetLineBreakpointsImpl;
            this._setBidiLevel = &SetBidiLevelImpl;
            this._setNumberSubstitution = &SetNumberSubstitutionImpl;
            this._scripts = scripts;
            this._length = length;
        }

        public IDWriteTextAnalysisSink* Get()
        {
            // The vtable is the function pointers right after it.
            var self = (void**)Unsafe.AsPointer(ref this);
            *self = self + 1;
            return (IDWriteTextAnalysisSink*)self;
        }

        [UnmanagedCallersOnly]
        private static int QueryInterfaceImpl(void* self, Guid* riid, void** ppv)
        {
            if (*riid == IID.IID_IUnknown || *riid == IID.IID_IDWriteTextAnalysisSink)
            {
                *ppv = self;
                return S.S_OK;
            }

            *ppv = null;
            return E.E_NOINTERFACE;
        }

        [UnmanagedCallersOnly]
        private static int SetScriptAnalysisImpl(void* self, uint position, uint length, DWRITE_SCRIPT_ANALYSIS* analysis)
        {
            var s = (ScriptSink*)self;
            for (var i = position; i < position + length && i < s->_length; i++)
                s->_scripts[i] = *analysis;
            return S.S_OK;
        }

        [UnmanagedCallersOnly]
        private static int SetLineBreakpointsImpl(void* self, uint position, uint length, DWRITE_LINE_BREAKPOINT* breakpoints) => S.S_OK;

        [UnmanagedCallersOnly]
        private static int SetBidiLevelImpl(void* self, uint position, uint length, byte explicitLevel, byte resolvedLevel) => S.S_OK;

        [UnmanagedCallersOnly]
        private static int SetNumberSubstitutionImpl(void* self, uint position, uint length, IDWriteNumberSubstitution* substitution) => S.S_OK;
    }
}
