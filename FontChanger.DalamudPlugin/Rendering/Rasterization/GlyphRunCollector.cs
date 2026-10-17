using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using FontChanger.DalamudPlugin.PluginInterface;
using FontChanger.DalamudPlugin.Utilities;

using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;

namespace FontChanger.DalamudPlugin.Rendering.Rasterization;

internal static unsafe class GlyphRunCollector
{
    public static void CollectTo(this ref IDWriteTextLayout4 layout, IGlyphRunSink sink)
    {
        var obj = new Native
        {
            QueryInterface = &QueryInterfaceImpl,
            AddRef = &AddRefImpl,
            Release = &ReleaseImpl,
            IsPixelSnappingDisabled = &IsPixelSnappingDisabledImpl,
            GetCurrentTransform = &GetCurrentTransformImpl,
            GetPixelsPerDip = &GetPixelsPerDipImpl,
            DrawGlyphRun = &DrawGlyphRunImpl,
            DrawUnderline = &DrawUnderlineImpl,
            DrawStrikethrough = &DrawStrikethroughImpl,
            DrawInlineObject = &DrawInlineObjectImpl,
        };
        obj.VTable = &obj.QueryInterface;
        layout.Draw(Unsafe.AsPointer(ref sink), (IDWriteTextRenderer*)&obj, 0, 0).ThrowOnError();
    }

    [UnmanagedCallersOnly]
    private static int QueryInterfaceImpl(void* self, Guid* riid, void** ppv)
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
    private static uint AddRefImpl(void* self) => 1;

    [UnmanagedCallersOnly]
    private static uint ReleaseImpl(void* self) => 1;

    [UnmanagedCallersOnly]
    private static int IsPixelSnappingDisabledImpl(void* self, void* context, BOOL* isDisabled)
    {
        // Positions stay fractional; the cluster cells carry the fraction.
        *isDisabled = BOOL.TRUE;
        return S.S_OK;
    }

    [UnmanagedCallersOnly]
    private static int GetCurrentTransformImpl(void* self, void* context, DWRITE_MATRIX* transform)
    {
        *transform = new() { m11 = 1, m22 = 1 };
        return S.S_OK;
    }

    [UnmanagedCallersOnly]
    private static int GetPixelsPerDipImpl(void* self, void* context, float* pixelsPerDip)
    {
        *pixelsPerDip = 1;
        return S.S_OK;
    }

    [UnmanagedCallersOnly]
    private static int DrawGlyphRunImpl(
        void* self,
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
            using var face = default(ComPtr<IDWriteFontFace5>);
            var iid = IID.IID_IDWriteFontFace5;
            run->fontFace->QueryInterface(&iid, (void**)face.GetAddressOf()).ThrowOnError();
            Unsafe.AsRef<IGlyphRunSink>(context).OnGlyphRun(baselineX, face.Get(), in *run, in *description);
            return S.S_OK;
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Collecting a glyph run failed");
            return E.E_FAIL;
        }
    }

    [UnmanagedCallersOnly]
    private static int DrawUnderlineImpl(void* self, void* context, float x, float y, DWRITE_UNDERLINE* underline, IUnknown* effect) =>
        S.S_OK;

    [UnmanagedCallersOnly]
    private static int DrawStrikethroughImpl(void* self, void* context, float x, float y, DWRITE_STRIKETHROUGH* strikethrough, IUnknown* effect) =>
        S.S_OK;

    [UnmanagedCallersOnly]
    private static int DrawInlineObjectImpl(
        void* self,
        void* context,
        float x,
        float y,
        IDWriteInlineObject* inlineObject,
        BOOL isSideways,
        BOOL isRightToLeft,
        IUnknown* effect) =>
        S.S_OK;

    [StructLayout(LayoutKind.Sequential)]
    private struct Native
    {
        public void* VTable;
        public delegate* unmanaged<void*, Guid*, void**, int> QueryInterface;
        public delegate* unmanaged<void*, uint> AddRef;
        public delegate* unmanaged<void*, uint> Release;
        public delegate* unmanaged<void*, void*, BOOL*, int> IsPixelSnappingDisabled;
        public delegate* unmanaged<void*, void*, DWRITE_MATRIX*, int> GetCurrentTransform;
        public delegate* unmanaged<void*, void*, float*, int> GetPixelsPerDip;
        public delegate* unmanaged<void*, void*, float, float, DWRITE_MEASURING_MODE, DWRITE_GLYPH_RUN*, DWRITE_GLYPH_RUN_DESCRIPTION*, IUnknown*, int> DrawGlyphRun;
        public delegate* unmanaged<void*, void*, float, float, DWRITE_UNDERLINE*, IUnknown*, int> DrawUnderline;
        public delegate* unmanaged<void*, void*, float, float, DWRITE_STRIKETHROUGH*, IUnknown*, int> DrawStrikethrough;
        public delegate* unmanaged<void*, void*, float, float, IDWriteInlineObject*, BOOL, BOOL, IUnknown*, int> DrawInlineObject;
    }
}
