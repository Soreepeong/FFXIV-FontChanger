using System;
using System.IO;
using System.Runtime.InteropServices;

using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;

using static TerraFX.Interop.DirectX.DirectX;
using static TerraFX.Interop.Windows.Windows;

namespace CustomFonts;

/// <summary>
/// Draws SVG documents and bitmaps into coverage with Direct2D, on the CPU (a WIC bitmap target), and decodes PNG files
/// with WIC (xivres image_fixed_size_font). SVG documents need Windows 10 1703 or later.
/// </summary>
internal sealed unsafe class ImageRenderer : IDisposable
{
    // A large viewport and viewBox of the same size map an SVG document's user units to the viewport one to one, so the
    // transformation alone decides where the drawing goes.
    private const float SvgViewportHalfSize = 100000;

    private ID2D1Factory1* factory;
    private IWICImagingFactory* wic;

    public ImageRenderer()
    {
        try
        {
            ID2D1Factory1* f;
            var iid = IID.IID_ID2D1Factory1;
            var options = default(D2D1_FACTORY_OPTIONS);
            D2D1CreateFactory(D2D1_FACTORY_TYPE.D2D1_FACTORY_TYPE_SINGLE_THREADED, &iid, &options, (void**)&f).ThrowOnError();
            this.factory = f;

            // Without COM, which the game's thread may not have set up.
            IWICImagingFactory* w;
            WICCreateImagingFactory_Proxy(0x0237, &w).ThrowOnError();
            this.wic = w;
        }
        catch
        {
            this.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (this.wic is not null)
        {
            this.wic->Release();
            this.wic = null;
        }

        if (this.factory is not null)
        {
            this.factory->Release();
            this.factory = null;
        }
    }

    /// <summary>
    /// Draws an SVG document into coverage of <paramref name="width"/> by <paramref name="height"/> pixels, with user
    /// units mapped to pixels by x' = m0 x + m1 y + m2, y' = m3 x + m4 y + m5. Coverage is the opacity less the luminance,
    /// so white shapes over black ones cut holes as they look. A group with the id xivfont-reference isn't drawn.
    /// </summary>
    public byte[] DrawSvg(string svg, ReadOnlySpan<float> m, int width, int height)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(svg);
        var matrix = ToD2D(m);

        // The viewBox starts at -SvgViewportHalfSize.
        matrix._31 -= SvgViewportHalfSize * (matrix._11 + matrix._21);
        matrix._32 -= SvgViewportHalfSize * (matrix._12 + matrix._22);
        var pixels = this.Render(width, height, context =>
        {
            IStream* stream;
            fixed (byte* p = bytes)
                stream = SHCreateMemStream(p, (uint)bytes.Length);
            if (stream is null)
                throw new OutOfMemoryException();

            ID2D1SvgDocument* document = null;
            ID2D1SvgElement* root = null;
            try
            {
                context->CreateSvgDocument(stream, new D2D_SIZE_F(2 * SvgViewportHalfSize, 2 * SvgViewportHalfSize), &document).ThrowOnError();
                RemoveReferenceLayer(document);

                document->GetRoot(&root);
                if (root is null)
                    throw new InvalidDataException("The SVG document has no root.");
                var viewBox = new D2D1_SVG_VIEWBOX { x = -SvgViewportHalfSize, y = -SvgViewportHalfSize, width = 2 * SvgViewportHalfSize, height = 2 * SvgViewportHalfSize };
                var length = new D2D1_SVG_LENGTH { value = 2 * SvgViewportHalfSize, units = D2D1_SVG_LENGTH_UNITS.D2D1_SVG_LENGTH_UNITS_NUMBER };
                var aspect = new D2D1_SVG_PRESERVE_ASPECT_RATIO { defer = BOOL.FALSE, align = D2D1_SVG_ASPECT_ALIGN.D2D1_SVG_ASPECT_ALIGN_NONE, meetOrSlice = D2D1_SVG_ASPECT_SCALING.D2D1_SVG_ASPECT_SCALING_MEET };
                fixed (char* name = "viewBox")
                    root->SetAttributeValue(name, D2D1_SVG_ATTRIBUTE_POD_TYPE.D2D1_SVG_ATTRIBUTE_POD_TYPE_VIEWBOX, &viewBox, (uint)sizeof(D2D1_SVG_VIEWBOX)).ThrowOnError();
                fixed (char* name = "width")
                    root->SetAttributeValue(name, &length).ThrowOnError();
                fixed (char* name = "height")
                    root->SetAttributeValue(name, &length).ThrowOnError();
                fixed (char* name = "preserveAspectRatio")
                    root->SetAttributeValue(name, &aspect).ThrowOnError();
                fixed (char* name = "x")
                    root->RemoveAttribute(name);
                fixed (char* name = "y")
                    root->RemoveAttribute(name);

                var transform = matrix;
                context->SetTransform(&transform);
                context->DrawSvgDocument(document);
            }
            finally
            {
                if (root is not null)
                    root->Release();
                if (document is not null)
                    document->Release();
                stream->Release();
            }
        });

        var coverage = new byte[pixels.Length];
        for (var i = 0; i < pixels.Length; i++)
        {
            var p = pixels[i];
            int b = (byte)p, g = (byte)(p >> 8), r = (byte)(p >> 16), a = (byte)(p >> 24);
            coverage[i] = (byte)Math.Clamp(a - (((2126 * r) + (7152 * g) + (722 * b) + 5000) / 10000), 0, 255);
        }

        return coverage;
    }

    /// <summary>
    /// Draws coverage of <paramref name="sourceWidth"/> by <paramref name="sourceHeight"/> pixels, as the rectangle
    /// (x1, y1)-(x2, y2) mapped by <paramref name="m"/> (as in <see cref="DrawSvg"/>), into coverage of
    /// <paramref name="width"/> by <paramref name="height"/>; with nearest-neighbour sampling, else high quality cubic.
    /// </summary>
    public byte[] DrawCoverage(
        byte[] source, int sourceWidth, int sourceHeight, (float X1, float Y1, float X2, float Y2) rect, ReadOnlySpan<float> m, int width, int height, bool nearest)
    {
        // Black, with the coverage as its opacity (premultiplied).
        var premultiplied = new uint[source.Length];
        for (var i = 0; i < source.Length; i++)
            premultiplied[i] = (uint)source[i] << 24;

        var matrix = ToD2D(m);
        var pixels = this.Render(width, height, context =>
        {
            ID2D1Bitmap1* bitmap;
            var props = new D2D1_BITMAP_PROPERTIES1
            {
                pixelFormat = new() { format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM, alphaMode = D2D1_ALPHA_MODE.D2D1_ALPHA_MODE_PREMULTIPLIED },
                dpiX = 96,
                dpiY = 96,
            };
            fixed (uint* p = premultiplied)
                context->CreateBitmap(new D2D_SIZE_U((uint)sourceWidth, (uint)sourceHeight), p, (uint)sourceWidth * 4, &props, &bitmap).ThrowOnError();
            try
            {
                var transform = matrix;
                context->SetTransform(&transform);
                var dest = new D2D_RECT_F(rect.X1, rect.Y1, rect.X2, rect.Y2);
                context->DrawBitmap(
                    (ID2D1Bitmap*)bitmap,
                    &dest,
                    1,
                    nearest ? D2D1_INTERPOLATION_MODE.D2D1_INTERPOLATION_MODE_NEAREST_NEIGHBOR : D2D1_INTERPOLATION_MODE.D2D1_INTERPOLATION_MODE_HIGH_QUALITY_CUBIC,
                    null,
                    null);
            }
            finally
            {
                bitmap->Release();
            }
        });

        var coverage = new byte[pixels.Length];
        for (var i = 0; i < pixels.Length; i++)
            coverage[i] = (byte)(pixels[i] >> 24);
        return coverage;
    }

    /// <summary>Decodes an image file (PNG and others WIC reads) into straight-alpha BGRA pixels.</summary>
    public (uint[] Pixels, int Width, int Height) Decode(byte[] data)
    {
        IWICStream* stream = null;
        IWICBitmapDecoder* decoder = null;
        IWICBitmapFrameDecode* frame = null;
        IWICBitmapSource* converted = null;
        try
        {
            this.wic->CreateStream(&stream).ThrowOnError();
            fixed (byte* p = data)
            {
                stream->InitializeFromMemory(p, (uint)data.Length).ThrowOnError();
                this.wic->CreateDecoderFromStream((IStream*)stream, null, WICDecodeOptions.WICDecodeMetadataCacheOnDemand, &decoder).ThrowOnError();
                decoder->GetFrame(0, &frame).ThrowOnError();
                var format = GUID.GUID_WICPixelFormat32bppBGRA;
                WICConvertBitmapSource(&format, (IWICBitmapSource*)frame, &converted).ThrowOnError();
                uint w, h;
                converted->GetSize(&w, &h).ThrowOnError();
                var pixels = new uint[w * h];
                fixed (uint* d = pixels)
                    converted->CopyPixels(null, w * 4, (uint)pixels.Length * 4, (byte*)d).ThrowOnError();
                return (pixels, (int)w, (int)h);
            }
        }
        finally
        {
            if (converted is not null)
                converted->Release();
            if (frame is not null)
                frame->Release();
            if (decoder is not null)
                decoder->Release();
            if (stream is not null)
                stream->Release();
        }
    }

    private delegate void DrawCallback(ID2D1DeviceContext5* context);

    [DllImport("windowscodecs.dll")]
    private static extern HRESULT WICCreateImagingFactory_Proxy(uint sdkVersion, IWICImagingFactory** factory);

    /// <summary>Gets x' = m0 x + m1 y + m2, y' = m3 x + m4 y + m5 as Direct2D's matrix, which transforms row vectors.</summary>
    private static D2D_MATRIX_3X2_F ToD2D(ReadOnlySpan<float> m) => new()
    {
        _11 = m[0],
        _12 = m[3],
        _21 = m[1],
        _22 = m[4],
        _31 = m[2],
        _32 = m[5],
    };

    /// <summary>Removes the reference layer of exported glyphs, which is there to be traced over, not drawn.</summary>
    private static void RemoveReferenceLayer(ID2D1SvgDocument* document)
    {
        ID2D1SvgElement* reference = null;
        fixed (char* id = "xivfont-reference")
        {
            if (document->FindElementById(id, &reference).FAILED || reference is null)
                return;
        }

        ID2D1SvgElement* parent = null;
        reference->GetParent(&parent);
        if (parent is not null)
        {
            parent->RemoveChild(reference);
            parent->Release();
        }

        reference->Release();
    }

    /// <summary>Draws into a transparent target of a size, and returns its premultiplied BGRA pixels.</summary>
    private uint[] Render(int width, int height, DrawCallback draw)
    {
        IWICBitmap* bitmap = null;
        ID2D1RenderTarget* target = null;
        ID2D1DeviceContext5* context = null;
        try
        {
            var format = GUID.GUID_WICPixelFormat32bppPBGRA;
            this.wic->CreateBitmap((uint)width, (uint)height, &format, WICBitmapCreateCacheOption.WICBitmapCacheOnLoad, &bitmap).ThrowOnError();
            var props = new D2D1_RENDER_TARGET_PROPERTIES
            {
                type = D2D1_RENDER_TARGET_TYPE.D2D1_RENDER_TARGET_TYPE_SOFTWARE,
                pixelFormat = new() { format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM, alphaMode = D2D1_ALPHA_MODE.D2D1_ALPHA_MODE_PREMULTIPLIED },
                dpiX = 96,
                dpiY = 96,
            };
            ((ID2D1Factory*)this.factory)->CreateWicBitmapRenderTarget(bitmap, &props, &target).ThrowOnError();
            var iid = IID.IID_ID2D1DeviceContext5;
            ((IUnknown*)target)->QueryInterface(&iid, (void**)&context).ThrowOnError();

            context->BeginDraw();
            context->Clear(null);
            draw(context);
            context->EndDraw(null, null).ThrowOnError();

            var pixels = new uint[width * height];
            var rect = new WICRect { X = 0, Y = 0, Width = width, Height = height };
            fixed (uint* p = pixels)
                bitmap->CopyPixels(&rect, (uint)width * 4, (uint)pixels.Length * 4, (byte*)p).ThrowOnError();
            return pixels;
        }
        finally
        {
            if (context is not null)
                context->Release();
            if (target is not null)
                target->Release();
            if (bitmap is not null)
                bitmap->Release();
        }
    }

}
