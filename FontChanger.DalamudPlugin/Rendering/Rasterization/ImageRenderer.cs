using System;
using System.IO;
using System.Runtime.InteropServices;

using FontChanger.DalamudPlugin.Utilities;

using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;

using static TerraFX.Interop.DirectX.DirectX;
using static TerraFX.Interop.Windows.Windows;

namespace FontChanger.DalamudPlugin.Rendering.Rasterization;

/// <summary>
/// Draws SVG documents and bitmaps into coverage with Direct2D, on the CPU (a WIC bitmap target), and decodes PNG files
/// with WIC (xivres image_fixed_size_font). SVG documents need Windows 10 1703 or later.
/// </summary>
internal sealed unsafe class ImageRenderer : IDisposable
{
    // A large viewport and viewBox of the same size map an SVG document's user units to the viewport one to one, so the
    // transformation alone decides where the drawing goes.
    private const float SvgViewportHalfSize = 100000;

    private ComPtr<ID2D1Factory1> _factory;
    private ComPtr<IWICImagingFactory> _wic;

    public ImageRenderer()
    {
        try
        {
            var iid = IID.IID_ID2D1Factory1;
            var options = default(D2D1_FACTORY_OPTIONS);
            D2D1CreateFactory(D2D1_FACTORY_TYPE.D2D1_FACTORY_TYPE_SINGLE_THREADED, &iid, &options, (void**)this._factory.GetAddressOf()).ThrowOnError();

            // Without COM, which the game's thread may not have set up.
            WICCreateImagingFactory_Proxy(0x0237, this._wic.GetAddressOf()).ThrowOnError();
        }
        catch
        {
            this.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        this._wic.Dispose();
        this._factory.Dispose();
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
        var pixels = this.Render(
            width,
            height,
            context =>
            {
                var created = default(ComPtr<IStream>);
                fixed (byte* p = bytes)
                    created.Attach(SHCreateMemStream(p, (uint)bytes.Length));
                using var stream = created;
                if (stream.IsNull)
                    throw new OutOfMemoryException();

                using var document = default(ComPtr<ID2D1SvgDocument>);
                context->CreateSvgDocument(stream.Get(), new(2 * SvgViewportHalfSize, 2 * SvgViewportHalfSize), document.GetAddressOf()).ThrowOnError();
                RemoveReferenceLayer(document.Get());

                using var rootPtr = default(ComPtr<ID2D1SvgElement>);
                document.Get()->GetRoot(rootPtr.GetAddressOf());
                var root = rootPtr.Get();
                if (root is null)
                    throw new InvalidDataException("The SVG document has no root.");
                var viewBox = new D2D1_SVG_VIEWBOX
                    { x = -SvgViewportHalfSize, y = -SvgViewportHalfSize, width = 2 * SvgViewportHalfSize, height = 2 * SvgViewportHalfSize };
                var length = new D2D1_SVG_LENGTH { value = 2 * SvgViewportHalfSize, units = D2D1_SVG_LENGTH_UNITS.D2D1_SVG_LENGTH_UNITS_NUMBER };
                var aspect = new D2D1_SVG_PRESERVE_ASPECT_RATIO
                {
                    defer = BOOL.FALSE, align = D2D1_SVG_ASPECT_ALIGN.D2D1_SVG_ASPECT_ALIGN_NONE,
                    meetOrSlice = D2D1_SVG_ASPECT_SCALING.D2D1_SVG_ASPECT_SCALING_MEET
                };
                fixed (char* name = "viewBox")
                    root->SetAttributeValue(name, D2D1_SVG_ATTRIBUTE_POD_TYPE.D2D1_SVG_ATTRIBUTE_POD_TYPE_VIEWBOX, &viewBox, (uint)sizeof(D2D1_SVG_VIEWBOX))
                        .ThrowOnError();
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
                context->DrawSvgDocument(document.Get());
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
        byte[] source,
        int sourceWidth,
        int sourceHeight,
        (float X1, float Y1, float X2, float Y2) rect,
        ReadOnlySpan<float> m,
        int width,
        int height,
        bool nearest)
    {
        // Black, with the coverage as its opacity (premultiplied).
        var premultiplied = new uint[source.Length];
        for (var i = 0; i < source.Length; i++)
            premultiplied[i] = (uint)source[i] << 24;

        var matrix = ToD2D(m);
        var pixels = this.Render(
            width,
            height,
            context =>
            {
                using var bitmap = default(ComPtr<ID2D1Bitmap1>);
                var props = new D2D1_BITMAP_PROPERTIES1
                {
                    pixelFormat = new() { format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM, alphaMode = D2D1_ALPHA_MODE.D2D1_ALPHA_MODE_PREMULTIPLIED },
                    dpiX = 96,
                    dpiY = 96,
                };
                fixed (uint* p = premultiplied)
                    context->CreateBitmap(new((uint)sourceWidth, (uint)sourceHeight), p, (uint)sourceWidth * 4, &props, bitmap.GetAddressOf()).ThrowOnError();
                var transform = matrix;
                context->SetTransform(&transform);
                var dest = new D2D_RECT_F(rect.X1, rect.Y1, rect.X2, rect.Y2);
                context->DrawBitmap(
                    (ID2D1Bitmap*)bitmap.Get(),
                    &dest,
                    1,
                    nearest
                        ? D2D1_INTERPOLATION_MODE.D2D1_INTERPOLATION_MODE_NEAREST_NEIGHBOR
                        : D2D1_INTERPOLATION_MODE.D2D1_INTERPOLATION_MODE_HIGH_QUALITY_CUBIC,
                    null,
                    null);
            });

        var coverage = new byte[pixels.Length];
        for (var i = 0; i < pixels.Length; i++)
            coverage[i] = (byte)(pixels[i] >> 24);
        return coverage;
    }

    /// <summary>Decodes an image file (PNG and others WIC reads) into straight-alpha BGRA pixels.</summary>
    public (uint[] Pixels, int Width, int Height) Decode(byte[] data)
    {
        using var stream = default(ComPtr<IWICStream>);
        using var decoder = default(ComPtr<IWICBitmapDecoder>);
        using var frame = default(ComPtr<IWICBitmapFrameDecode>);
        using var converted = default(ComPtr<IWICBitmapSource>);
        this._wic.Get()->CreateStream(stream.GetAddressOf()).ThrowOnError();
        fixed (byte* p = data)
        {
            stream.Get()->InitializeFromMemory(p, (uint)data.Length).ThrowOnError();
            this._wic.Get()->CreateDecoderFromStream((IStream*)stream.Get(), null, WICDecodeOptions.WICDecodeMetadataCacheOnDemand, decoder.GetAddressOf())
                .ThrowOnError();
            decoder.Get()->GetFrame(0, frame.GetAddressOf()).ThrowOnError();
            var format = GUID.GUID_WICPixelFormat32bppBGRA;
            WICConvertBitmapSource(&format, (IWICBitmapSource*)frame.Get(), converted.GetAddressOf()).ThrowOnError();
            uint w, h;
            converted.Get()->GetSize(&w, &h).ThrowOnError();
            var pixels = new uint[w * h];
            fixed (uint* d = pixels)
                converted.Get()->CopyPixels(null, w * 4, (uint)pixels.Length * 4, (byte*)d).ThrowOnError();
            return (pixels, (int)w, (int)h);
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
        using var reference = default(ComPtr<ID2D1SvgElement>);
        fixed (char* id = "xivfont-reference")
        {
            if (document->FindElementById(id, reference.GetAddressOf()).FAILED || reference.IsNull)
                return;
        }

        using var parent = default(ComPtr<ID2D1SvgElement>);
        reference.Get()->GetParent(parent.GetAddressOf());
        if (!parent.IsNull)
            parent.Get()->RemoveChild(reference.Get());
    }

    /// <summary>Draws into a transparent target of a size, and returns its premultiplied BGRA pixels.</summary>
    private uint[] Render(int width, int height, DrawCallback draw)
    {
        using var bitmap = default(ComPtr<IWICBitmap>);
        using var target = default(ComPtr<ID2D1RenderTarget>);
        using var context = default(ComPtr<ID2D1DeviceContext5>);
        var format = GUID.GUID_WICPixelFormat32bppPBGRA;
        this._wic.Get()->CreateBitmap((uint)width, (uint)height, &format, WICBitmapCreateCacheOption.WICBitmapCacheOnLoad, bitmap.GetAddressOf()).ThrowOnError();
        var props = new D2D1_RENDER_TARGET_PROPERTIES
        {
            type = D2D1_RENDER_TARGET_TYPE.D2D1_RENDER_TARGET_TYPE_SOFTWARE,
            pixelFormat = new() { format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM, alphaMode = D2D1_ALPHA_MODE.D2D1_ALPHA_MODE_PREMULTIPLIED },
            dpiX = 96,
            dpiY = 96,
        };
        ((ID2D1Factory*)this._factory.Get())->CreateWicBitmapRenderTarget(bitmap.Get(), &props, target.GetAddressOf()).ThrowOnError();
        target.CopyTo(context.GetAddressOf()).ThrowOnError();

        context.Get()->BeginDraw();
        context.Get()->Clear(null);
        draw(context.Get());
        context.Get()->EndDraw(null, null).ThrowOnError();

        var pixels = new uint[width * height];
        var rect = new WICRect { X = 0, Y = 0, Width = width, Height = height };
        fixed (uint* p = pixels)
            bitmap.Get()->CopyPixels(&rect, (uint)width * 4, (uint)pixels.Length * 4, (byte*)p).ThrowOnError();
        return pixels;
    }
}
