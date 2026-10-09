using System;
using System.Runtime.InteropServices;

using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;

namespace CustomFonts;

/// <summary>Receives the glyph runs of a text layout (<see cref="GlyphRunCollector"/>).</summary>
internal unsafe interface IGlyphRunSink
{
    void OnGlyphRun(float baselineX, DWRITE_GLYPH_RUN* run, DWRITE_GLYPH_RUN_DESCRIPTION* description);
}

/// <summary>
/// An <c>IDWriteTextRenderer</c> that draws nothing: <c>IDWriteTextLayout.Draw</c> hands it each shaped glyph run (one
/// font face and script each, fallback applied), which it passes to the <see cref="IGlyphRunSink"/> given as the
/// drawing context. A native object with a static vtable; not reference counted (it lives as long as this object).
/// </summary>
internal sealed unsafe class GlyphRunCollector : IDisposable
{
    private static readonly void** Vtbl = CreateVtbl();

    private Native* native;

    public GlyphRunCollector()
    {
        this.native = (Native*)NativeMemory.AllocZeroed((nuint)sizeof(Native));
        this.native->Vtbl = Vtbl;
    }

    /// <summary>Draws a layout into a sink.</summary>
    public void Collect(IDWriteTextLayout* layout, IGlyphRunSink sink)
    {
        var handle = GCHandle.Alloc(sink);
        try
        {
            layout->Draw((void*)GCHandle.ToIntPtr(handle), (IDWriteTextRenderer*)this.native, 0, 0).ThrowOnError();
        }
        finally
        {
            handle.Free();
        }
    }

    public void Dispose()
    {
        if (this.native is not null)
        {
            NativeMemory.Free(this.native);
            this.native = null;
        }
    }

    private static void** CreateVtbl()
    {
        var vtbl = (void**)NativeMemory.Alloc(10, (nuint)sizeof(void*));
        vtbl[0] = (delegate* unmanaged<Native*, Guid*, void**, int>)&QueryInterface;
        vtbl[1] = (delegate* unmanaged<Native*, uint>)&AddRef;
        vtbl[2] = (delegate* unmanaged<Native*, uint>)&Release;
        vtbl[3] = (delegate* unmanaged<Native*, void*, BOOL*, int>)&IsPixelSnappingDisabled;
        vtbl[4] = (delegate* unmanaged<Native*, void*, DWRITE_MATRIX*, int>)&GetCurrentTransform;
        vtbl[5] = (delegate* unmanaged<Native*, void*, float*, int>)&GetPixelsPerDip;
        vtbl[6] = (delegate* unmanaged<Native*, void*, float, float, DWRITE_MEASURING_MODE, DWRITE_GLYPH_RUN*, DWRITE_GLYPH_RUN_DESCRIPTION*, IUnknown*, int>)&DrawGlyphRun;
        vtbl[7] = (delegate* unmanaged<Native*, void*, float, float, DWRITE_UNDERLINE*, IUnknown*, int>)&DrawUnderline;
        vtbl[8] = (delegate* unmanaged<Native*, void*, float, float, DWRITE_STRIKETHROUGH*, IUnknown*, int>)&DrawStrikethrough;
        vtbl[9] = (delegate* unmanaged<Native*, void*, float, float, IDWriteInlineObject*, BOOL, BOOL, IUnknown*, int>)&DrawInlineObject;
        return vtbl;
    }

    [UnmanagedCallersOnly]
    private static int QueryInterface(Native* self, Guid* riid, void** ppv)
    {
        if (*riid == IID.IID_IUnknown || *riid == IID.IID_IDWritePixelSnapping || *riid == IID.IID_IDWriteTextRenderer)
        {
            *ppv = self;
            return S.S_OK;
        }

        *ppv = null;
        return E.E_NOINTERFACE;
    }

    [UnmanagedCallersOnly]
    private static uint AddRef(Native* self) => 1;

    [UnmanagedCallersOnly]
    private static uint Release(Native* self) => 1;

    [UnmanagedCallersOnly]
    private static int IsPixelSnappingDisabled(Native* self, void* context, BOOL* isDisabled)
    {
        // Positions stay fractional; the cluster cells carry the fraction.
        *isDisabled = BOOL.TRUE;
        return S.S_OK;
    }

    [UnmanagedCallersOnly]
    private static int GetCurrentTransform(Native* self, void* context, DWRITE_MATRIX* transform)
    {
        *transform = new() { m11 = 1, m22 = 1 };
        return S.S_OK;
    }

    [UnmanagedCallersOnly]
    private static int GetPixelsPerDip(Native* self, void* context, float* pixelsPerDip)
    {
        *pixelsPerDip = 1;
        return S.S_OK;
    }

    [UnmanagedCallersOnly]
    private static int DrawGlyphRun(
        Native* self,
        void* context,
        float baselineX,
        float baselineY,
        DWRITE_MEASURING_MODE measuringMode,
        DWRITE_GLYPH_RUN* run,
        DWRITE_GLYPH_RUN_DESCRIPTION* description,
        IUnknown* effect)
    {
        try
        {
            ((IGlyphRunSink)GCHandle.FromIntPtr((nint)context).Target!).OnGlyphRun(baselineX, run, description);
            return S.S_OK;
        }
        catch (Exception ex)
        {
            Host.Log.Error(ex, "Collecting a glyph run failed");
            return E.E_FAIL;
        }
    }

    [UnmanagedCallersOnly]
    private static int DrawUnderline(Native* self, void* context, float x, float y, DWRITE_UNDERLINE* underline, IUnknown* effect) =>
        S.S_OK;

    [UnmanagedCallersOnly]
    private static int DrawStrikethrough(Native* self, void* context, float x, float y, DWRITE_STRIKETHROUGH* strikethrough, IUnknown* effect) =>
        S.S_OK;

    [UnmanagedCallersOnly]
    private static int DrawInlineObject(
        Native* self, void* context, float x, float y, IDWriteInlineObject* inlineObject, BOOL isSideways, BOOL isRightToLeft, IUnknown* effect) =>
        S.S_OK;

    [StructLayout(LayoutKind.Sequential)]
    private struct Native
    {
        public void** Vtbl;
    }
}
