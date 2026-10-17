using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using FontChanger.DalamudPlugin.Rendering.Faces;
using FontChanger.DalamudPlugin.Rendering.Faces.Sources;
using FontChanger.DalamudPlugin.Rendering.Rasterization;
using FontChanger.DalamudPlugin.Utilities;

using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;

namespace FontChanger.DalamudPlugin.Rendering.Text;

/// <summary>A run of a line: its code units, its script, and whether it is written right to left.</summary>
internal readonly record struct TextRun(int Start, int End, ScriptToken Script, bool RightToLeft);

/// <summary>
/// Cuts a line into runs of one script and direction: the scripts found with DirectWrite (AnalyzeScript), for the
/// shaping that pairs with it, and right to left by the character's block, as the game draws only left to right and
/// such runs are left to it.
/// </summary>
internal sealed unsafe class DirectWriteTextAnalyzer(GlyphRasterizer rasterizer)
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
        fixed (char* locale = "en-us")
        fixed (DWRITE_SCRIPT_ANALYSIS* scripts = this._scripts)
        {
            var source = new TextAnalysisSource(t, (uint)length, locale);
            var sink = new ScriptSink(scripts, (uint)length);
            rasterizer.Analyzer->AnalyzeScript(source.Get(), 0, (uint)length, sink.Get()).ThrowOnError();
        }

        var start = 0;
        var rtl = false;
        for (var i = 0; i < length; i++)
        {
            // A low surrogate goes with its high one.
            var unitRtl = char.IsLowSurrogate(text[i]) && i > 0 ? rtl : IsRightToLeft(text, i);
            if (i != 0 && (unitRtl != rtl || !SameScript(this._scripts[i], this._scripts[start])))
            {
                runs.Add(new(start, i, ToToken(this._scripts[start]), rtl));
                start = i;
            }

            rtl = unitRtl;
        }

        runs.Add(new(start, length, ToToken(this._scripts[start]), rtl));
    }

    private static ScriptToken ToToken(DWRITE_SCRIPT_ANALYSIS a) => new(a.script, (int)a.shapes);

    private static bool SameScript(DWRITE_SCRIPT_ANALYSIS a, DWRITE_SCRIPT_ANALYSIS b) => a.script == b.script && a.shapes == b.shapes;

    /// <summary>Gets whether the character at a code unit is of a right-to-left block (Hebrew, Arabic, Syriac, Thaana, ...).</summary>
    private static bool IsRightToLeft(ReadOnlySpan<char> text, int index)
    {
        int c = text[index];
        if (char.IsHighSurrogate(text[index]) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]))
            c = char.ConvertToUtf32(text[index], text[index + 1]);
        return c is (>= 0x0590 and <= 0x08FF) or (>= 0xFB1D and <= 0xFDFF) or (>= 0xFE70 and <= 0xFEFF)
                   or (>= 0x10800 and <= 0x10FFF) or (>= 0x1E800 and <= 0x1EFFF);
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
            this._addRef = &AddRefImpl;
            this._release = &ReleaseImpl;
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
        private static uint AddRefImpl(void* self) => 1;

        [UnmanagedCallersOnly]
        private static uint ReleaseImpl(void* self) => 1;

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
