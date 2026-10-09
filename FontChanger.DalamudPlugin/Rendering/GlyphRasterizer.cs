using System;
using System.Collections.Generic;

using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;

using static TerraFX.Interop.DirectX.DirectX;

namespace CustomFonts;

/// <summary>Rounding as the C++ code (xivres, FontChanger) does it: halves away from zero, as std::round and std::lround.</summary>
internal static class Rounding
{
    public static float Round(float value) => MathF.Round(value, MidpointRounding.AwayFromZero);
}

/// <summary>A rasterized glyph: 8-bit coverage, placed relative to the pen on the baseline.</summary>
internal readonly record struct RasterGlyph(int Advance, int Left, int Top, int Width, int Height, byte[] Alpha)
{
    /// <summary>
    /// Gets the box a glyph's coverage takes when scaled by <paramref name="scale"/> about the pen, moved right and down by
    /// a fraction of a pixel.
    /// </summary>
    public static (int Left, int Top, int Width, int Height) ScaledBounds(int left, int top, int width, int height, float scale, float shiftX, float shiftY)
    {
        var x0 = (int)MathF.Floor((left * scale) + shiftX);
        var y0 = (int)MathF.Floor((top * scale) + shiftY);
        return (x0, y0,
            (int)MathF.Ceiling((left + width) * scale + shiftX) - x0,
            (int)MathF.Ceiling((top + height) * scale + shiftY) - y0);
    }

    /// <summary>
    /// Gets the glyph scaled by <paramref name="scale"/> about the pen, moved right and down by a fraction of a pixel: each
    /// output pixel averages 4 x 4 bilinear samples, so it both enlarges and shrinks.
    /// </summary>
    public RasterGlyph Scaled(float scale, float shiftX, float shiftY)
    {
        var (x0, y0, w, h) = ScaledBounds(this.Left, this.Top, this.Width, this.Height, scale, shiftX, shiftY);
        var left = (this.Left * scale) + shiftX;
        var top = (this.Top * scale) + shiftY;
        var alpha = new byte[w * h];
        const int Samples = 4;
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var sum = 0f;
                for (var sy = 0; sy < Samples; sy++)
                {
                    for (var sx = 0; sx < Samples; sx++)
                    {
                        // The sample's position in source pixels, from their centers.
                        var u = ((x0 + x + ((sx + 0.5f) / Samples) - left) / scale) - 0.5f;
                        var v = ((y0 + y + ((sy + 0.5f) / Samples) - top) / scale) - 0.5f;
                        sum += this.Bilinear(u, v);
                    }
                }

                alpha[(y * w) + x] = (byte)Rounding.Round(sum / (Samples * Samples));
            }
        }

        return this with { Left = x0, Top = y0, Width = w, Height = h, Alpha = alpha };
    }

    private float Bilinear(float u, float v)
    {
        var x = (int)MathF.Floor(u);
        var y = (int)MathF.Floor(v);
        float fx = u - x, fy = v - y;
        return Lerp(Lerp(this.At(x, y), this.At(x + 1, y), fx), Lerp(this.At(x, y + 1), this.At(x + 1, y + 1), fx), fy);

        static float Lerp(float a, float b, float t) => a + ((b - a) * t);
    }

    private float At(int x, int y) => x < 0 || y < 0 || x >= this.Width || y >= this.Height ? 0 : this.Alpha[(y * this.Width) + x];

    /// <summary>
    /// Gets the glyph scaled horizontally by <paramref name="scale"/> about the pen, each pixel averaging the source
    /// columns it covers.
    /// </summary>
    public RasterGlyph SqueezedX(float scale)
    {
        var x0 = (int)MathF.Floor(this.Left * scale);
        var w = Math.Max(1, (int)MathF.Ceiling((this.Left + this.Width) * scale) - x0);
        var alpha = new byte[w * this.Height];
        for (var y = 0; y < this.Height; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var u0 = ((x0 + x) / scale) - this.Left;
                var u1 = ((x0 + x + 1) / scale) - this.Left;
                var sum = 0f;
                for (var u = (int)MathF.Floor(u0); u < MathF.Ceiling(u1); u++)
                {
                    if (u < 0 || u >= this.Width)
                        continue;
                    var cover = Math.Min(u + 1, u1) - Math.Max(u, u0);
                    sum += this.Alpha[(y * this.Width) + u] * cover;
                }

                alpha[(y * w) + x] = (byte)Math.Clamp(Rounding.Round(sum * scale), 0, 255);
            }
        }

        return this with { Left = x0, Width = w, Alpha = alpha };
    }

    /// <summary>Gets the glyph with its empty rows and columns cut off.</summary>
    public RasterGlyph Trimmed()
    {
        int x1 = this.Width, y1 = this.Height, x2 = 0, y2 = 0;
        for (var y = 0; y < this.Height; y++)
        {
            for (var x = 0; x < this.Width; x++)
            {
                if (this.Alpha[(y * this.Width) + x] != 0)
                    (x1, y1, x2, y2) = (Math.Min(x1, x), Math.Min(y1, y), Math.Max(x2, x + 1), Math.Max(y2, y + 1));
            }
        }

        if (x1 >= x2 || y1 >= y2)
            return this with { Left = 0, Top = 0, Width = 0, Height = 0, Alpha = [] };
        var w = x2 - x1;
        var alpha = new byte[w * (y2 - y1)];
        for (var y = y1; y < y2; y++)
            this.Alpha.AsSpan((y * this.Width) + x1, w).CopyTo(alpha.AsSpan((y - y1) * w));
        return this with { Left = this.Left + x1, Top = this.Top + y1, Width = w, Height = y2 - y1, Alpha = alpha };
    }

    /// <summary>
    /// Merges glyphs placed relative to one pen into one box with an advance, keeping the larger coverage where they
    /// overlap.
    /// </summary>
    public static RasterGlyph Merge(List<RasterGlyph> pieces, int advance)
    {
        pieces.RemoveAll(p => p.Width == 0);
        if (pieces.Count == 0)
            return new(advance, 0, 0, 0, 0, []);
        if (pieces.Count == 1)
            return pieces[0] with { Advance = advance };

        int left = int.MaxValue, top = int.MaxValue, right = int.MinValue, bottom = int.MinValue;
        foreach (var p in pieces)
            (left, top, right, bottom) = (Math.Min(left, p.Left), Math.Min(top, p.Top), Math.Max(right, p.Left + p.Width), Math.Max(bottom, p.Top + p.Height));
        var w = right - left;
        var h = bottom - top;
        var alpha = new byte[w * h];
        foreach (var p in pieces)
            BlitMax(alpha, w, h, p, p.Left - left, p.Top - top);
        return new(advance, left, top, w, h, alpha);
    }

    /// <summary>Draws a glyph's coverage into a buffer at a position, keeping the larger value where it overlaps.</summary>
    public static void BlitMax(byte[] buffer, int width, int height, RasterGlyph g, int x, int y)
    {
        for (var row = Math.Max(0, -y); row < g.Height && row + y < height; row++)
        {
            for (var col = Math.Max(0, -x); col < g.Width && col + x < width; col++)
            {
                ref var d = ref buffer[((row + y) * width) + col + x];
                d = Math.Max(d, g.Alpha[(row * g.Width) + col]);
            }
        }
    }

    /// <summary>Makes a table mapping coverage to coverage to the power of <paramref name="exponent"/>.</summary>
    public static byte[] CoverageTable(float exponent)
    {
        var table = new byte[256];
        for (var i = 0; i < 256; i++)
            table[i] = (byte)Rounding.Round(255 * MathF.Pow(i / 255f, exponent));
        return table;
    }

    /// <summary>Gets the glyph with its coverage mapped through a table, in a new array (coverage may be shared).</summary>
    public RasterGlyph WithCoverage(byte[] table)
    {
        var alpha = new byte[this.Alpha.Length];
        for (var i = 0; i < alpha.Length; i++)
            alpha[i] = table[this.Alpha[i]];
        return this with { Alpha = alpha };
    }
}

/// <summary>
/// The (first) file of a font face: its loader and reference key, which tell its file apart from others'. Valid until
/// disposed; <see cref="Loader"/> is null if the face has no file.
/// </summary>
internal readonly unsafe ref struct FontFileKey
{
    private readonly IDWriteFontFile* file;

    private FontFileKey(IDWriteFontFile* file, IDWriteFontFileLoader* loader, ReadOnlySpan<byte> key)
    {
        this.file = file;
        this.Loader = loader;
        this.Key = key;
    }

    public IDWriteFontFileLoader* Loader { get; }

    public ReadOnlySpan<byte> Key { get; }

    public static FontFileKey Of(IDWriteFontFace* face)
    {
        uint count = 1;
        IDWriteFontFile* file = null;
        if (face->GetFiles(&count, &file).FAILED || file is null)
            return default;

        IDWriteFontFileLoader* loader;
        void* key;
        uint size;
        if (file->GetReferenceKey(&key, &size).FAILED || file->GetLoader(&loader).FAILED)
        {
            file->Release();
            return default;
        }

        return new(file, loader, new(key, (int)size));
    }

    public void Dispose()
    {
        if (this.Loader is not null)
            this.Loader->Release();
        if (this.file is not null)
            this.file->Release();
    }
}

/// <summary>Finds system fonts and rasterizes glyph runs with DirectWrite, grayscale antialiased.</summary>
internal sealed unsafe class GlyphRasterizer : IDisposable
{
    private IDWriteFactory2* factory;
    private IDWriteFontCollection* systemFonts;

    public GlyphRasterizer()
    {
        try
        {
            IDWriteFactory2* f;
            var iid = IID.IID_IDWriteFactory2;
            DWriteCreateFactory(DWRITE_FACTORY_TYPE.DWRITE_FACTORY_TYPE_SHARED, &iid, (IUnknown**)&f).ThrowOnError();
            this.factory = f;

            IDWriteFontCollection* collection;
            this.factory->GetSystemFontCollection(&collection, BOOL.FALSE).ThrowOnError();
            this.systemFonts = collection;

            IDWriteFontFallbackBuilder* builder;
            this.factory->CreateFontFallbackBuilder(&builder).ThrowOnError();
            IDWriteFontFallback* none;
            var hr = builder->CreateFontFallback(&none);
            builder->Release();
            hr.ThrowOnError();
            this.NoFallback = none;

            this.FreeType = FreeTypeFonts.Create();
            try
            {
                this.Images = new();
            }
            catch (Exception ex)
            {
                Host.Log.Warning(ex, "Direct2D can't be used; glyph images and SVG shapes aren't drawn");
            }
        }
        catch
        {
            this.Dispose();
            throw;
        }
    }

    /// <summary>Gets the DirectWrite factory (shared; owned by this object).</summary>
    public IDWriteFactory2* Factory => this.factory;

    /// <summary>Gets the system's font collection (owned by this object).</summary>
    public IDWriteFontCollection* SystemFonts => this.systemFonts;

    /// <summary>Gets FreeType, or null if its native library can't be loaded.</summary>
    public FreeTypeFonts? FreeType { get; }

    /// <summary>Gets Direct2D's drawing of SVG documents and bitmaps, or null if it can't be used.</summary>
    public ImageRenderer? Images { get; }

    /// <summary>Gets a font fallback that falls back to nothing: text is laid out in its own font only.</summary>
    public IDWriteFontFallback* NoFallback { get; private set; }

    /// <summary>Gets a collector of text layouts' glyph runs.</summary>
    public GlyphRunCollector RunCollector { get; } = new();

    /// <summary>
    /// Finds the font of a family closest to a weight, stretch and style (simulated if the family lacks it); null if the
    /// family isn't installed. The caller releases it.
    /// </summary>
    public IDWriteFont* FindFont(LookupDef lookup)
    {
        uint index;
        BOOL exists;
        fixed (char* name = lookup.Name)
            this.systemFonts->FindFamilyName(name, &index, &exists).ThrowOnError();
        if (!exists)
            return null;

        IDWriteFontFamily* fontFamily;
        this.systemFonts->GetFontFamily(index, &fontFamily).ThrowOnError();
        try
        {
            IDWriteFont* font;
            fontFamily->GetFirstMatchingFont(
                (DWRITE_FONT_WEIGHT)lookup.Weight,
                (DWRITE_FONT_STRETCH)lookup.Stretch,
                (DWRITE_FONT_STYLE)lookup.Style,
                &font).ThrowOnError();
            return font;
        }
        finally
        {
            fontFamily->Release();
        }
    }

    /// <summary>
    /// Makes a text format that doesn't wrap, of a family at a weight, style and stretch (DWRITE_* values) and a size,
    /// with a font fallback. The caller releases it.
    /// </summary>
    public IDWriteTextFormat1* CreateFormat(string family, int weight, int style, int stretch, float size, IDWriteFontFallback* fallback)
    {
        IDWriteTextFormat* format;
        fixed (char* name = family)
        fixed (char* locale = "en-us")
        {
            this.factory->CreateTextFormat(
                name, null, (DWRITE_FONT_WEIGHT)weight, (DWRITE_FONT_STYLE)style, (DWRITE_FONT_STRETCH)stretch, size, locale, &format).ThrowOnError();
        }

        try
        {
            format->SetWordWrapping(DWRITE_WORD_WRAPPING.DWRITE_WORD_WRAPPING_NO_WRAP).ThrowOnError();
            IDWriteTextFormat1* format1;
            var iid = IID.IID_IDWriteTextFormat1;
            ((IUnknown*)format)->QueryInterface(&iid, (void**)&format1).ThrowOnError();
            var hr = format1->SetFontFallback(fallback);
            if (hr.FAILED)
                format1->Release();
            hr.ThrowOnError();
            return format1;
        }
        finally
        {
            format->Release();
        }
    }

    /// <summary>
    /// Rasterizes a run of glyphs of one face (a glyph, or a shaped cluster: a ligature, a base with its marks) with the
    /// pen at <paramref name="originX"/>, a fraction of a pixel, so the cluster lands where shaping put it.
    /// </summary>
    public RasterGlyph RasterizeRun(
        IDWriteFontFace* face,
        float px,
        ushort* glyphs,
        float* advances,
        DWRITE_GLYPH_OFFSET* offsets,
        uint count,
        float originX,
        int advance,
        DirectWriteParams parameters,
        GlyphTransform transform)
    {
        var analysis = this.CreateAnalysis(face, px, glyphs, advances, offsets, count, originX, parameters, transform);
        try
        {
            // With grayscale antialiasing, the 1x1 texture type gives 8-bit coverage.
            RECT bounds;
            analysis->GetAlphaTextureBounds(DWRITE_TEXTURE_TYPE.DWRITE_TEXTURE_ALIASED_1x1, &bounds).ThrowOnError();
            var w = bounds.right - bounds.left;
            var h = bounds.bottom - bounds.top;
            var alpha = w > 0 && h > 0 ? new byte[w * h] : [];
            if (alpha.Length != 0)
            {
                fixed (byte* p = alpha)
                {
                    analysis->CreateAlphaTexture(DWRITE_TEXTURE_TYPE.DWRITE_TEXTURE_ALIASED_1x1, &bounds, p, (uint)alpha.Length)
                        .ThrowOnError();
                }
            }
            else
            {
                w = h = 0;
            }

            return new(advance, bounds.left, bounds.top, w, h, alpha);
        }
        finally
        {
            analysis->Release();
        }
    }

    /// <summary>Gets the pixels a run of glyphs covers with the pen at the origin, as <see cref="RasterizeRun"/> draws it.</summary>
    public RECT GetRunBounds(
        IDWriteFontFace* face,
        float px,
        ushort* glyphs,
        float* advances,
        DWRITE_GLYPH_OFFSET* offsets,
        uint count,
        DirectWriteParams parameters,
        GlyphTransform transform)
    {
        var analysis = this.CreateAnalysis(face, px, glyphs, advances, offsets, count, 0, parameters, transform);
        try
        {
            RECT bounds;
            analysis->GetAlphaTextureBounds(DWRITE_TEXTURE_TYPE.DWRITE_TEXTURE_ALIASED_1x1, &bounds).ThrowOnError();
            return bounds;
        }
        finally
        {
            analysis->Release();
        }
    }

    /// <summary>Makes DirectWrite's analysis of a glyph run, which the caller releases.</summary>
    private IDWriteGlyphRunAnalysis* CreateAnalysis(
        IDWriteFontFace* face,
        float px,
        ushort* glyphs,
        float* advances,
        DWRITE_GLYPH_OFFSET* offsets,
        uint count,
        float originX,
        DirectWriteParams parameters,
        GlyphTransform transform)
    {
        // DirectWrite transforms row vectors; the origin is on screen, after the transformation.
        var matrix = new DWRITE_MATRIX
        {
            m11 = transform.M11,
            m12 = transform.M21,
            m21 = transform.M12,
            m22 = transform.M22,
            dx = originX,
            dy = 0,
        };

        var run = new DWRITE_GLYPH_RUN
        {
            fontFace = face,
            fontEmSize = px,
            glyphCount = count,
            glyphIndices = glyphs,
            glyphAdvances = advances,
            glyphOffsets = offsets,
            isSideways = BOOL.FALSE,
            bidiLevel = 0,
        };

        var measuringMode = (DWRITE_MEASURING_MODE)parameters.MeasureMode;
        var renderingMode = (DWRITE_RENDERING_MODE)parameters.RenderMode;
        if (renderingMode == DWRITE_RENDERING_MODE.DWRITE_RENDERING_MODE_DEFAULT)
            face->GetRecommendedRenderingMode(px, 1, measuringMode, null, &renderingMode);

        // Outlines can't be rasterized.
        if (renderingMode is DWRITE_RENDERING_MODE.DWRITE_RENDERING_MODE_OUTLINE or DWRITE_RENDERING_MODE.DWRITE_RENDERING_MODE_DEFAULT)
            renderingMode = DWRITE_RENDERING_MODE.DWRITE_RENDERING_MODE_NATURAL_SYMMETRIC;

        IDWriteGlyphRunAnalysis* analysis;
        var gridFitMode = (DWRITE_GRID_FIT_MODE)parameters.GridFitMode;
        for (var attempt = 0; ; attempt++)
        {
            var hr = this.factory->CreateGlyphRunAnalysis(
                &run,
                &matrix,
                renderingMode,
                measuringMode,
                gridFitMode,
                DWRITE_TEXT_ANTIALIAS_MODE.DWRITE_TEXT_ANTIALIAS_MODE_GRAYSCALE,
                0,
                0,
                &analysis);
            if (hr.SUCCEEDED)
                return analysis;
            if (attempt != 0)
                hr.ThrowOnError();

            // Parameters DirectWrite refuses are tried again as the defaults.
            (renderingMode, measuringMode, gridFitMode) = (
                DWRITE_RENDERING_MODE.DWRITE_RENDERING_MODE_NATURAL_SYMMETRIC,
                DWRITE_MEASURING_MODE.DWRITE_MEASURING_MODE_NATURAL,
                DWRITE_GRID_FIT_MODE.DWRITE_GRID_FIT_MODE_DEFAULT);
        }
    }

    public void Dispose()
    {
        this.RunCollector.Dispose();
        this.Images?.Dispose();
        this.FreeType?.Dispose();
        if (this.NoFallback is not null)
        {
            this.NoFallback->Release();
            this.NoFallback = null;
        }

        if (this.systemFonts is not null)
        {
            this.systemFonts->Release();
            this.systemFonts = null;
        }

        if (this.factory is not null)
        {
            this.factory->Release();
            this.factory = null;
        }
    }
}
