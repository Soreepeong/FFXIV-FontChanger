using System;

using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;

using static TerraFX.Interop.DirectX.DirectX;

namespace CustomFonts;

/// <summary>
/// A rasterized glyph: 8-bit coverage, placed relative to the pen on the baseline. With <see cref="Source"/>, the coverage
/// comes from a game texture when the atlas uploads (<see cref="Alpha"/> is blank until then).
/// </summary>
internal readonly record struct RasterGlyph(int Advance, int Left, int Top, int Width, int Height, byte[] Alpha)
{
    public GameTextureSource? Source { get; init; }

    /// <summary>Gets whether the element's gamma was applied already (to the parts of a merged glyph it applies to).</summary>
    public bool GammaApplied { get; init; }

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

                alpha[(y * w) + x] = (byte)MathF.Round(sum / (Samples * Samples));
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

    /// <summary>Gets the glyph with its <see cref="Source"/>'s pixels (read as 8-bit coverage) scaled into its box.</summary>
    public RasterGlyph WithSourcePixels(byte[] sourceAlpha)
    {
        var s = this.Source!;
        var scaled = new RasterGlyph(0, s.Left, s.Top, s.Width, s.Height, sourceAlpha);
        if (s.Scale != 1)
            scaled = scaled.Scaled(s.Scale, 0, 0);
        var alpha = new byte[this.Width * this.Height];
        var dx = scaled.Left - this.Left;
        var dy = scaled.Top - this.Top;
        for (var y = Math.Max(0, -dy); y < scaled.Height && y + dy < this.Height; y++)
        {
            for (var x = Math.Max(0, -dx); x < scaled.Width && x + dx < this.Width; x++)
                alpha[((y + dy) * this.Width) + x + dx] = scaled.Alpha[(y * scaled.Width) + x];
        }

        return this with { Alpha = alpha, Source = null };
    }

    /// <summary>Makes a table mapping coverage to coverage to the power of <paramref name="exponent"/>.</summary>
    public static byte[] CoverageTable(float exponent)
    {
        var table = new byte[256];
        for (var i = 0; i < 256; i++)
            table[i] = (byte)MathF.Round(255 * MathF.Pow(i / 255f, exponent));
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
/// A game glyph's pixels: a rectangle of a plane of a game font texture (a Kernel::Texture), whose top left is at
/// (<see cref="Left"/>, <see cref="Top"/>) from the pen in the game font's pixels, scaled by <see cref="Scale"/>.
/// </summary>
internal sealed record GameTextureSource(nint Texture, int X, int Y, int Width, int Height, int Plane, int Left, int Top, float Scale);

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
                Plugin.Log.Warning(ex, "Direct2D can't be used; glyph images and SVG shapes aren't drawn");
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
                break;
            if (attempt != 0)
                hr.ThrowOnError();

            // Parameters DirectWrite refuses are tried again as the defaults.
            (renderingMode, measuringMode, gridFitMode) = (
                DWRITE_RENDERING_MODE.DWRITE_RENDERING_MODE_NATURAL_SYMMETRIC,
                DWRITE_MEASURING_MODE.DWRITE_MEASURING_MODE_NATURAL,
                DWRITE_GRID_FIT_MODE.DWRITE_GRID_FIT_MODE_DEFAULT);
        }

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
