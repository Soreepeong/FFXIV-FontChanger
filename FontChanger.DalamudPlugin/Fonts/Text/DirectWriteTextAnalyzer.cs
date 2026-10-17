using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

using FontChanger.DalamudPlugin.Fonts.Records;
using FontChanger.DalamudPlugin.Utilities;
using FontChanger.Presets.Records;

using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;

namespace FontChanger.DalamudPlugin.Fonts.Text;

internal sealed unsafe class DirectWriteTextAnalyzer(FontServices services)
{
    private DWRITE_SCRIPT_ANALYSIS[] _scripts = new DWRITE_SCRIPT_ANALYSIS[256];

    /// <summary>The runs cover all of the text, in order.</summary>
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
            services.Analyzer.AnalyzeScript(source.Get(), 0, (uint)length, sink.Get()).ThrowOnError();
        }

        var start = 0;
        var rtl = false;
        for (var i = 0; i < length; i++)
        {
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

    private static bool IsRightToLeft(ReadOnlySpan<char> text, int index) =>
        Rune.DecodeFromUtf16(text[index..], out var rune, out _) == OperationStatus.Done && RuneRanges.RightToLeft.Contains(rune);

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
            var self = (ScriptSink*)Unsafe.AsPointer(ref this);
            this._vtable = &self->_queryInterface;
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
