using System;
using System.IO;
using System.Runtime.InteropServices;

using FontChanger.DalamudPlugin.Utilities;

using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;

using static TerraFX.Interop.DirectX.DirectX;
using static TerraFX.Interop.Windows.Windows;

namespace FontChanger.DalamudPlugin.Fonts.Rasterization;

/// <summary>SVG documents need Windows 10 1703 or later.</summary>
internal sealed unsafe class ImageRenderer : IDisposable
{
    // A large viewport and viewBox of the same size map user units one to one, so the transformation alone places the drawing.
    private const float SvgViewportHalfSize = 100000;

    private MyComPtr<ID2D1Factory1> _factory;
    private MyComPtr<IWICImagingFactory> _wic;

    public ImageRenderer()
    {
        try
        {
            var iid = IID.IID_ID2D1Factory1;
            var options = default(D2D1_FACTORY_OPTIONS);
            fixed (ID2D1Factory1** factory = this._factory)
                D2D1CreateFactory(D2D1_FACTORY_TYPE.D2D1_FACTORY_TYPE_SINGLE_THREADED, &iid, &options, (void**)factory).ThrowOnError();

            // Without COM, which the game's thread may not have set up.
            fixed (IWICImagingFactory** wic = this._wic)
                WICCreateImagingFactory_Proxy(0x0237, wic).ThrowOnError();
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
    /// Maps user units to pixels by x' = m0 x + m1 y + m2, y' = m3 x + m4 y + m5; coverage is the opacity less the
    /// luminance, so that white shapes over black ones cut holes as they look.
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
            context => {
                using var stream = default(MyComPtr<IStream>);
                fixed (byte* p = bytes)
                    stream.Attach(SHCreateMemStream(p, (uint)bytes.Length));
                if (!stream)
                    throw new OutOfMemoryException();

                using var document = default(MyComPtr<ID2D1SvgDocument>);
                context->CreateSvgDocument(stream.Value, new(2 * SvgViewportHalfSize, 2 * SvgViewportHalfSize), document.GetAddressOf()).ThrowOnError();
                RemoveReferenceLayer(document.Value);

                using var rootPtr = default(MyComPtr<ID2D1SvgElement>);
                document.Value->GetRoot(rootPtr.GetAddressOf());
                var root = rootPtr.Value;
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
                context->DrawSvgDocument(document.Value);
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

    /// <summary><paramref name="m"/> maps <paramref name="rect"/> to pixels as in <see cref="DrawSvg"/>.</summary>
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
        var premultiplied = new uint[source.Length];
        for (var i = 0; i < source.Length; i++)
            premultiplied[i] = (uint)source[i] << 24;

        var matrix = ToD2D(m);
        var pixels = this.Render(
            width,
            height,
            context => {
                using var bitmap = default(MyComPtr<ID2D1Bitmap1>);
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
                    (ID2D1Bitmap*)bitmap.Value,
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

    /// <summary>Returns straight-alpha BGRA pixels.</summary>
    public (uint[] Pixels, int Width, int Height) Decode(byte[] data)
    {
        using var stream = default(MyComPtr<IWICStream>);
        using var decoder = default(MyComPtr<IWICBitmapDecoder>);
        using var frame = default(MyComPtr<IWICBitmapFrameDecode>);
        using var converted = default(MyComPtr<IWICBitmapSource>);
        this._wic.Value->CreateStream(stream.GetAddressOf()).ThrowOnError();
        fixed (byte* p = data)
        {
            stream.Value->InitializeFromMemory(p, (uint)data.Length).ThrowOnError();
            this._wic.Value->CreateDecoderFromStream((IStream*)stream.Value, null, WICDecodeOptions.WICDecodeMetadataCacheOnDemand, decoder.GetAddressOf())
                .ThrowOnError();
            decoder.Value->GetFrame(0, frame.GetAddressOf()).ThrowOnError();
            var format = GUID.GUID_WICPixelFormat32bppBGRA;
            WICConvertBitmapSource(&format, (IWICBitmapSource*)frame.Value, converted.GetAddressOf()).ThrowOnError();
            uint w, h;
            converted.Value->GetSize(&w, &h).ThrowOnError();
            var pixels = new uint[w * h];
            fixed (uint* d = pixels)
                converted.Value->CopyPixels(null, w * 4, (uint)pixels.Length * 4, (byte*)d).ThrowOnError();
            return (pixels, (int)w, (int)h);
        }
    }

    private delegate void DrawCallback(ID2D1DeviceContext5* context);

    [DllImport("windowscodecs.dll")]
    private static extern HRESULT WICCreateImagingFactory_Proxy(uint sdkVersion, IWICImagingFactory** factory);

    // Direct2D transforms row vectors.
    private static D2D_MATRIX_3X2_F ToD2D(ReadOnlySpan<float> m) => new()
    {
        _11 = m[0],
        _12 = m[3],
        _21 = m[1],
        _22 = m[4],
        _31 = m[2],
        _32 = m[5],
    };

    /// <summary>Exported glyphs' xivfont-reference layer is there to be traced over, not drawn.</summary>
    private static void RemoveReferenceLayer(ID2D1SvgDocument* document)
    {
        using var reference = default(MyComPtr<ID2D1SvgElement>);
        fixed (char* id = "xivfont-reference")
        {
            if (document->FindElementById(id, reference.GetAddressOf()).FAILED || !reference)
                return;
        }

        using var parent = default(MyComPtr<ID2D1SvgElement>);
        reference.Value->GetParent(parent.GetAddressOf());
        if (parent)
            parent.Value->RemoveChild(reference.Value);
    }

    /// <summary>Returns premultiplied BGRA pixels.</summary>
    private uint[] Render(int width, int height, DrawCallback draw)
    {
        using var bitmap = default(MyComPtr<IWICBitmap>);
        using var target = default(MyComPtr<ID2D1RenderTarget>);
        using var context = default(MyComPtr<ID2D1DeviceContext5>);
        var format = GUID.GUID_WICPixelFormat32bppPBGRA;
        this._wic.Value->CreateBitmap((uint)width, (uint)height, &format, WICBitmapCreateCacheOption.WICBitmapCacheOnLoad, bitmap.GetAddressOf())
            .ThrowOnError();
        var props = new D2D1_RENDER_TARGET_PROPERTIES
        {
            type = D2D1_RENDER_TARGET_TYPE.D2D1_RENDER_TARGET_TYPE_SOFTWARE,
            pixelFormat = new() { format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM, alphaMode = D2D1_ALPHA_MODE.D2D1_ALPHA_MODE_PREMULTIPLIED },
            dpiX = 96,
            dpiY = 96,
        };
        ((ID2D1Factory*)this._factory.Value)->CreateWicBitmapRenderTarget(bitmap.Value, &props, target.GetAddressOf()).ThrowOnError();
        target.CopyTo(context.GetAddressOf()).ThrowOnError();

        context.Value->BeginDraw();
        context.Value->Clear(null);
        draw(context.Value);
        context.Value->EndDraw(null, null).ThrowOnError();

        var pixels = new uint[width * height];
        var rect = new WICRect { X = 0, Y = 0, Width = width, Height = height };
        fixed (uint* p = pixels)
            bitmap.Value->CopyPixels(&rect, (uint)width * 4, (uint)pixels.Length * 4, (byte*)p).ThrowOnError();
        return pixels;
    }
}
